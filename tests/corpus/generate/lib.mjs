// Shared helpers for the fixture generator. Everything here is deterministic apart from timestamps
// that the producing libraries embed; expected results never depend on those.
import fs from "node:fs";
import path from "node:path";
import zlib from "node:zlib";
import { fileURLToPath } from "node:url";

export const CORPUS = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
export const GENERATED = path.join(CORPUS, "generated");      // large or bulky files; ignored by git
export const LISTENER = "http://127.0.0.1:47831";             // tests/harness/request-listener.mjs
export const WEBDAV_UNC = "\\\\127.0.0.1@47831\\share";       // UNC path Windows would try over WebDAV on the same port

// Some packages do not export package.json, so read it from disk.
export function packageVersion(name) {
  const file = path.join(path.dirname(fileURLToPath(import.meta.url)), "node_modules", name, "package.json");
  return JSON.parse(fs.readFileSync(file, "utf8")).version;
}

const entries = [];
export function record(entry) { entries.push(entry); }
export function manifestEntries() { return entries; }

export function write(relative, data) {
  const target = path.join(CORPUS, relative);
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, data);
  return target;
}

export function streamToBuffer(doc) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    doc.on("data", c => chunks.push(c));
    doc.on("end", () => resolve(Buffer.concat(chunks)));
    doc.on("error", reject);
    doc.end();
  });
}

// Minimal RGB PNG encoder, so image fixtures need no third-party image files.
export function png(width, height, pixel) {
  const raw = Buffer.alloc((width * 3 + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (width * 3 + 1)] = 0;
    for (let x = 0; x < width; x++) {
      const [r, g, b] = pixel(x, y);
      const o = y * (width * 3 + 1) + 1 + x * 3;
      raw[o] = r; raw[o + 1] = g; raw[o + 2] = b;
    }
  }
  const chunk = (type, data) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
    const crc = Buffer.alloc(4); crc.writeUInt32BE(zlib.crc32(body) >>> 0);
    return Buffer.concat([len, body, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0); ihdr.writeUInt32BE(height, 4); ihdr[8] = 8; ihdr[9] = 2;
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr), chunk("IDAT", zlib.deflateSync(raw)), chunk("IEND", Buffer.alloc(0))
  ]);
}

export const chartPng = () => png(240, 120, (x, y) => {
  const bar = Math.floor(x / 60), heights = [60, 95, 40, 110];
  return y > 120 - heights[bar] && x % 60 > 8 ? [[0x26, 0x6d, 0xb3], [0x2e, 0x9e, 0x6b], [0xd9, 0x8c, 0x1f], [0xb3, 0x3b, 0x3b]][bar] : [255, 255, 255];
});

// Hand-written PDF for cases a PDF library will not produce (JavaScript, Launch actions, UNC URIs).
// Objects are strings; the writer computes the cross-reference table.
export function rawPdf(objects) {
  let out = "%PDF-1.7\n%\xE2\xE3\xCF\xD3\n";
  const offsets = [];
  objects.forEach((body, i) => { offsets.push(Buffer.byteLength(out, "latin1")); out += `${i + 1} 0 obj\n${body}\nendobj\n`; });
  const xref = Buffer.byteLength(out, "latin1");
  out += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  for (const o of offsets) out += `${String(o).padStart(10, "0")} 00000 n \n`;
  out += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(out, "latin1");
}

export function textStream(lines) {
  const body = "BT /F1 14 Tf 72 740 Td 18 TL\n" + lines.map(l => `(${l.replace(/[()\\]/g, m => "\\" + m)}) Tj T*`).join("\n") + "\nET";
  return `<< /Length ${Buffer.byteLength(body, "latin1")} >>\nstream\n${body}\nendstream`;
}

export const SAFE_RULES = { network: "none", sourceUnchanged: true, filesBesideSource: "none" };

export const FIXED_DATE = new Date(Date.UTC(2026, 8, 27));

// Office files embed the current time (ZIP entry dates, docProps/core.xml). Rewrite both with a fixed date so
// regenerated fixtures are identical.
export async function stableZip(buffer, edit) {
  const { default: JSZip } = await import("jszip");
  const zip = await JSZip.loadAsync(buffer);
  const core = zip.file("docProps/core.xml");
  if (core) {
    const xml = (await core.async("string")).replace(/(<dcterms:(?:created|modified)[^>]*>)[^<]*(<)/g, `$1${FIXED_DATE.toISOString().replace(/\.\d+Z$/, "Z")}$2`);
    zip.file("docProps/core.xml", xml);
  }
  // Some libraries give relationships random IDs (for example docx hyperlinks); number them instead.
  const ids = new Map();
  const names = Object.keys(zip.files).filter(n => !zip.files[n].dir && /\.(xml|rels)$/.test(n));
  for (const name of names) for (const id of (await zip.file(name).async("string")).match(/rId[a-z0-9_-]{12,}/g) ?? []) if (!ids.has(id)) ids.set(id, `rIdStable${ids.size + 1}`);
  if (ids.size) for (const name of names) {
    const text = await zip.file(name).async("string");
    const fixed = text.replace(/rId[a-z0-9_-]{12,}/g, id => ids.get(id) ?? id);
    if (fixed !== text) zip.file(name, fixed);
  }
  // Embedded Office files (such as a chart's workbook) carry their own timestamps.
  for (const name of Object.keys(zip.files).filter(n => /\.(xlsx|docx|pptx)$/.test(n) && !zip.files[n].dir))
    zip.file(name, await stableZip(await zip.file(name).async("nodebuffer")));
  if (edit) await edit(zip);
  zip.forEach((_, file) => { file.date = FIXED_DATE; });
  return zip.generateAsync({ type: "nodebuffer", compression: "DEFLATE", compressionOptions: { level: 6 } });
}

// Shared ways to break an Office file, used by every Office format so each gets the same coverage.
export async function officeVariants({ format, folder, simple, complex, mainPart, textPart = mainPart, macroType, macroExtension, variants = [], producer, licence }) {
  const { default: JSZip } = await import("jszip");
  const { default: officeCrypto } = await import("officecrypto-tool");
  const { default: CFB } = await import("cfb");
  const rec = (name, category, expect, extra = {}, extension = name === "macro" ? macroExtension : format) => record({ id: `${format}-${name}`, file: `${folder}/${name}.${extension}`, format: extension, category, producer, licence, expect, rules: SAFE_RULES, ...extra });

  write(`${folder}/password.${format}`, officeCrypto.encrypt(simple, { password: "viewer-test" }));
  rec("password", "password", { result: "error", error: "password" }, { password: "viewer-test", producer: `${producer}, encrypted with officecrypto-tool ${packageVersion("officecrypto-tool")}`,
    notes: "Without a password: asks for it. Opens with the test password viewer-test (core and smoke tests); a wrong one is refused." });

  const container = CFB.utils.cfb_new();
  CFB.utils.cfb_add(container, format === "docx" ? "WordDocument" : "PowerPoint Document", Buffer.from("Container only; not a real binary document."));
  write(`${folder}/old-format-renamed.${format}`, Buffer.from(CFB.write(container, { type: "buffer" })));
  rec("old-format-renamed", "wrong-extension", { result: "error", error: "mismatch" }, { producer: `cfb ${packageVersion("cfb")} (Node)` });

  write(`${folder}/damaged-truncated.${format}`, complex.subarray(0, Math.floor(complex.length * 0.5)));
  rec("damaged-truncated", "damaged", { result: "error", error: "damaged" }, { producer: `${producer}, then truncated` });
  write(`${folder}/zero-byte.${format}`, Buffer.alloc(0));
  rec("zero-byte", "empty", { result: "error", error: "empty" }, { producer: "generator (empty file)" });
  write(`${folder}/not-a-document.${format}`, `Plain text saved with a .${format} name.\r\n`);
  rec("not-a-document", "wrong-extension", { result: "error", error: "mismatch" }, { producer: "generator (text)" });

  const macro = await JSZip.loadAsync(simple);
  macro.file(mainPart.replace(/[^/]+$/, "vbaProject.bin"), Buffer.from("Placeholder, not real VBA."), { date: FIXED_DATE });
  const types = await macro.file("[Content_Types].xml").async("string");
  macro.file("[Content_Types].xml", types.replace(/(PartName="\/[^"]+" ContentType=")[^"]+main\+xml"/, `$1${macroType}"`)
    .replace("</Types>", '<Default Extension="bin" ContentType="application/vnd.ms-office.vbaProject"/></Types>'), { date: FIXED_DATE });
  write(`${folder}/macro.${macroExtension}`, await macro.generateAsync({ type: "nodebuffer", compression: "DEFLATE" }));
  rec("macro", "macro", { result: "open", macrosRemoved: true }, { producer: `${producer}, repackaged with JSZip`, notes: "Opens with the macro project removed (never run) and a notice saying so." });

  // Templates and shows: the same content with the variant main-part type; each opens like the ordinary document.
  for (const { extension, type } of variants) {
    const variant = await JSZip.loadAsync(simple);
    const variantTypes = await variant.file("[Content_Types].xml").async("string");
    variant.file("[Content_Types].xml", variantTypes.replace(/(PartName="\/[^"]+" ContentType=")[^"]+main\+xml"/, `$1${type}"`), { date: FIXED_DATE });
    variant.forEach((_, f) => { f.date = FIXED_DATE; });
    write(`${folder}/variant.${extension}`, await variant.generateAsync({ type: "nodebuffer", compression: "DEFLATE" }));
    rec("variant", "variant", { result: "open" }, { id: `${format}-variant-${extension}`, producer: `${producer}, repackaged with JSZip`, notes: `${extension} (${type})` }, extension);
  }

  const bomb = await JSZip.loadAsync(simple);
  bomb.file(`${mainPart.split("/")[0]}/media/bomb.bin`, Buffer.alloc(300 * 1024 * 1024), { date: FIXED_DATE, compression: "DEFLATE", compressionOptions: { level: 9 } });
  bomb.forEach((_, f) => { f.date = FIXED_DATE; });
  write(`${folder}/attack-zip-bomb.${format}`, await bomb.generateAsync({ type: "nodebuffer", compression: "DEFLATE" }));
  rec("attack-zip-bomb", "attack", { result: "error", error: "damaged" }, { producer: `${producer}, repackaged with JSZip`, notes: "Must be refused by archive limits without decompressing the 300 MB entry." });

  const xxe = await JSZip.loadAsync(simple);
  const main = await xxe.file(textPart).async("string");
  const root = main.match(/<([a-z]+:[A-Za-z]+)[\s>]/)[1];
  const doctype = `<!DOCTYPE ${root} [<!ENTITY remote SYSTEM "${LISTENER}/${format}-xxe/entity"><!ENTITY local SYSTEM "file:///C:/Windows/win.ini">]>`;
  xxe.file(textPart, main.replace(/^(<\?xml[^>]*\?>)/, `$1${doctype}`).replace(/Hello/, "&remote;&local;Hello"), { date: FIXED_DATE });
  write(`${folder}/attack-xxe.${format}`, await xxe.generateAsync({ type: "nodebuffer", compression: "DEFLATE" }));
  rec("attack-xxe", "attack", { result: "error", error: "damaged" }, { producer: `${producer}, edited with JSZip`,
    notes: "Either refuse the file or show it without resolving either entity. Any request under /" + format + "-xxe/ is a failure." });
}
