// Plain Viewer web page view: web pages, saved web archives and EPUB books (0.8.0). Runs in the same locked-down
// WebView2 as the PDF view. The worker has read the file and collected the parts, style sheets and pictures stored in
// it (web.json, pictures served by the app). This page:
// 1. parses each part with DOMParser, which runs nothing and loads nothing;
// 2. removes scripts, event handlers, frames, objects, media, forms, <base>, <meta> and every reference to something
//    outside the file (pictures, style sheets, imports and url() in styles), counting what it removed;
// 3. moves the cleaned nodes into a frame sandboxed without scripts, which also inherits this page's content policy.
// WebView2's request filter blocks anything that still tried to load, and the app counts such attempts.
const DOC = "https://doc.plainviewer.invalid";
const host = window.chrome?.webview;
const post = message => host?.postMessage(message);
const frame = document.getElementById("frame");
document.documentElement.dataset.theme = new URLSearchParams(location.search).get("theme") === "dark" ? "dark" : "light";

const removed = { active: 0, embedded: 0, pictures: 0 };
let content = null, sections = [], scale = 1, frameDoc = null, frameWin = null;

// ---- Cleaning ----

const SCRIPT_LIKE = "script";
const EMBEDDED = "iframe,frame,frameset,object,embed,applet,portal,audio,video";
const DROPPED = "base,meta,template,source,track,param,link,noembed";
const DROPPED_ATTRIBUTES = new Set(["srcset", "action", "formaction", "ping", "background", "dynsrc", "lowsrc", "poster", "longdesc",
  "manifest", "codebase", "archive", "data", "profile", "usemap", "target", "download", "integrity", "nonce"]);

// A reference inside the file: an address as saved (web archives), or a path relative to the part (books).
function resolve(base, reference) {
  if (/^cid:/i.test(reference)) return reference;
  if (/^[a-z][a-z0-9+.-]*:/i.test(reference)) return reference.split("#")[0];
  if (/^[a-z][a-z0-9+.-]*:/i.test(base)) { try { return new URL(reference, base).href.split("#")[0]; } catch { return ""; } }
  const folder = base.includes("/") ? base.slice(0, base.lastIndexOf("/") + 1) : "";
  const parts = [];
  let path = reference.split("#")[0];
  try { path = decodeURIComponent(path); } catch { }
  for (const piece of (folder + path).split("/")) {
    if (!piece || piece === ".") continue;
    if (piece === "..") parts.pop(); else parts.push(piece);
  }
  return parts.join("/");
}
const lookup = (map, base, reference) => map[reference] ?? map[resolve(base, reference)];

// Style sheets keep their rules; imports and url() references go, except pictures written into the sheet itself and a
// book's own fonts (served by the app under names the worker gave them). base: where the sheet is in the book.
function cleanCss(css, base = "") {
  return String(css)
    .replace(/@import[^;]*;?/gi, "")
    .replace(/url\(\s*(['"]?)([^)]*?)\1\s*\)/gi, (match, quote, address) => {
      if (/^data:image\/(png|jpeg|gif|webp|avif|bmp)[;,]/i.test(address)) return match;
      const font = lookup(content.fonts ?? {}, base, address);
      return font ? `url("${mediaUrl(font)}")` : "none";
    })
    .replace(/expression\s*\(|-moz-binding|behavior\s*:/gi, "invalid(");
}

function placeholder(doc, text) {
  const span = doc.createElement("span");
  span.className = "pv-placeholder";
  span.textContent = text;
  return span;
}

const mediaUrl = name => `${DOC}/media?name=${encodeURIComponent(name)}`;
const safeDataImage = address => /^data:image\/(png|jpeg|gif|webp|avif|bmp|svg\+xml)[;,]/i.test(address);

function cleanPart(part, index, styles) {
  const doc = new DOMParser().parseFromString(part.html, "text/html");
  const base = part.name;
  for (const element of doc.querySelectorAll(SCRIPT_LIKE)) { removed.active++; element.remove(); }
  for (const link of doc.querySelectorAll("link")) {
    const rel = (link.getAttribute("rel") || "").toLowerCase(), href = link.getAttribute("href") || "";
    if (rel.split(/\s+/).includes("stylesheet") && href) { const css = lookup(content.styles, base, href); if (css != null) styles.add(cleanCss(css, content.styles[href] != null ? href : resolve(base, href))); }
  }
  for (const style of doc.querySelectorAll("style")) { styles.add(cleanCss(style.textContent, base)); style.remove(); }
  for (const element of doc.querySelectorAll(EMBEDDED)) {
    removed.embedded++;
    const name = element.localName;
    element.replaceWith(placeholder(doc, name === "video" ? "[Video not shown]" : name === "audio" ? "[Sound not shown]" : "[Embedded content not shown]"));
  }
  for (const element of doc.querySelectorAll(DROPPED)) element.remove();
  for (const form of doc.querySelectorAll("form")) form.replaceWith(...form.childNodes);
  for (const element of [doc.body, ...doc.body.querySelectorAll("*")]) {
    for (const attribute of [...element.attributes]) {
      const name = attribute.name.toLowerCase();
      if (name.startsWith("on")) { removed.active++; element.removeAttribute(attribute.name); }
      else if (DROPPED_ATTRIBUTES.has(name)) element.removeAttribute(attribute.name);
      else if (name === "style") element.setAttribute("style", cleanCss(attribute.value));
    }
    const tag = element.localName;
    if (tag === "img" || (tag === "input" && element.getAttribute("type")?.toLowerCase() === "image")) {
      const src = (element.getAttribute("src") || "").trim();
      const picture = src ? lookup(content.pictures, base, src) : null;
      if (picture) element.setAttribute("src", mediaUrl(picture));
      else if (safeDataImage(src)) { }
      else {
        if (src) removed.pictures++;
        element.replaceWith(placeholder(doc, element.getAttribute("alt") ? `[Image: ${element.getAttribute("alt")}]` : "[Image]"));
        continue;
      }
      if (tag === "input") element.setAttribute("type", "button");
    }
    else if (element.hasAttribute("src")) element.removeAttribute("src");
    // SVG: pictures and references by address (image, use, feImage, a).
    for (const name of ["href", "xlink:href"]) {
      if (!element.hasAttribute(name) || tag === "a" || tag === "area") continue;
      const href = (element.getAttribute(name) || "").trim();
      const picture = href && !href.startsWith("#") ? lookup(content.pictures, base, href) : null;
      if (href.startsWith("#")) continue;
      if (picture && tag === "image") element.setAttribute(name, mediaUrl(picture));
      else { if (tag === "image" && href) removed.pictures++; element.removeAttribute(name); }
    }
    if (tag === "a" || tag === "area") {
      const href = (element.getAttribute("href") || "").trim();
      if (!href) continue;
      if (href.startsWith("#")) element.dataset.pvTarget = `${index}#${href.slice(1)}`;
      else if (/^(https?:|mailto:)/i.test(href)) element.dataset.pvLink = href;
      else if (!/^[a-z][a-z0-9+.-]*:/i.test(href) && content.kind === "book") element.dataset.pvTarget = `${resolve(base, href)}#${href.split("#")[1] ?? ""}`;
      else if (/^javascript:/i.test(href)) removed.active++;
      element.removeAttribute("href");
      element.setAttribute("tabindex", "0");
      element.setAttribute("role", "link");
      element.classList.add("pv-link");
    }
  }
  const section = doc.createElement("section");
  section.className = "pv-part";
  section.dataset.part = String(index);
  section.dataset.name = part.name;
  for (const name of ["lang", "dir"]) {
    const value = doc.body.getAttribute(name) || doc.documentElement.getAttribute(name);
    if (value) section.setAttribute(name, value);
  }
  section.append(...doc.body.childNodes);
  return { section, title: doc.title };
}

// ---- Showing ----

const BASE_STYLE = `
.pv-placeholder { color: #595959; font-style: italic; }
.pv-link { color: #0b57d0; text-decoration: underline; cursor: pointer; }
::highlight(pv-match) { background-color: #ffe066; color: #000000; }
::highlight(pv-current) { background-color: #f29100; color: #000000; }
@media (forced-colors: active) { ::highlight(pv-match) { background-color: Mark; color: MarkText; } ::highlight(pv-current) { background-color: Highlight; color: HighlightText; } }`;
const BOOK_STYLE = `
body { margin: 0 auto; max-width: 46em; padding: 24px 32px 64px; font-family: "Segoe UI", sans-serif; line-height: 1.55; color: #1b1b1b; background: #ffffff; overflow-wrap: break-word; }
img, svg { max-width: 100%; height: auto; }
.pv-part + .pv-part { border-top: 1px solid #c8c8c8; margin-top: 2.5em; padding-top: 2em; }`;

function addStyle(text) {
  const style = frameDoc.createElement("style");
  style.textContent = text;
  frameDoc.head.append(style);
}

async function show() {
  const response = await fetch(`${DOC}/web.json`, { cache: "no-store" });
  if (!response.ok) throw new Error("unavailable");
  content = await response.json();
  content.pictures ??= {}; content.styles ??= {};
  await new Promise(resolve => { frame.addEventListener("load", resolve, { once: true }); frame.srcdoc = '<!DOCTYPE html><html><head><meta charset="utf-8"></head><body></body></html>'; });
  frameDoc = frame.contentDocument; frameWin = frame.contentWindow;
  const styles = new Set();
  const cleaned = content.parts.map((part, index) => cleanPart(part, index, styles));
  addStyle(content.kind === "book" ? BASE_STYLE + BOOK_STYLE : BASE_STYLE);
  for (const css of styles) addStyle(css);
  for (const { section } of cleaned) frameDoc.body.append(frameDoc.adoptNode(section));
  sections = [...frameDoc.querySelectorAll("section.pv-part")];
  frameDoc.title = content.title || cleaned[0]?.title || "";
  frameDoc.addEventListener("click", onClick, true);
  frameDoc.addEventListener("auxclick", event => event.preventDefault(), true);
  frameDoc.addEventListener("keydown", event => { if (event.key === "Enter" && event.target.closest?.(".pv-link")) onClick(event); }, true);
  frameDoc.addEventListener("dragstart", event => event.preventDefault(), true);
  frameWin.addEventListener("scroll", report, { passive: true });
  post({ type: "loaded", pages: sections.length, notice: notice(), title: frameDoc.title });
  report();
  requestAnimationFrame(() => requestAnimationFrame(() => post({ type: "rendered" })));
}

function notice() {
  const notes = [];
  if (removed.active > 0) notes.push("Scripts and other active content in this file were removed and never ran.");
  if (removed.embedded > 0) notes.push(`${removed.embedded} embedded frame${removed.embedded === 1 ? ", object or media item is" : "s, objects or media items are"} not shown.`);
  if (removed.pictures > 0) notes.push(`${removed.pictures} picture${removed.pictures === 1 ? " is" : "s are"} stored outside this file and ${removed.pictures === 1 ? "is" : "are"} not loaded.`);
  return notes.join(" ");
}

function current() {
  let index = 0;
  for (let i = 0; i < sections.length; i++) if (sections[i].getBoundingClientRect().top <= 8) index = i;
  return index;
}
function report() { post({ type: "state", page: sections.length ? current() + 1 : 1, pages: Math.max(1, sections.length), scale }); }

function goTo(index, id) {
  const section = sections[Math.max(0, Math.min(sections.length - 1, index))];
  if (!section) return;
  const target = id ? [...section.querySelectorAll("[id], a[name]")].find(element => element.id === id || element.getAttribute("name") === id) : null;
  (target ?? section).scrollIntoView({ block: "start" });
  report();
}

function onClick(event) {
  const link = event.target.closest?.(".pv-link");
  if (!link) return;
  event.preventDefault(); event.stopPropagation();
  if (link.dataset.pvLink) { post({ type: "link", href: link.dataset.pvLink }); return; }
  const target = link.dataset.pvTarget;
  if (!target) return;
  const hash = target.indexOf("#"), where = target.slice(0, hash), id = target.slice(hash + 1);
  const index = /^\d+$/.test(where) ? Number(where) : sections.findIndex(section => section.dataset.name === where);
  if (index >= 0) goTo(index, id);
}

function zoom(value) {
  if (value === "in" || value === "out") scale *= value === "in" ? 1.1 : 1 / 1.1;
  else if (typeof value === "number") scale = value;
  else scale = 1;
  scale = Math.min(3, Math.max(0.5, Math.round(scale * 100) / 100));
  frameDoc.documentElement.style.zoom = String(scale);
  report();
}

// ---- Search (text within one text node; matches are highlighted without changing the document) ----

let lastQuery = "", matches = [], position = -1;
function find(query, previous) {
  if (!frameDoc) return;
  if (query !== lastQuery) {
    lastQuery = query; matches = []; position = -1;
    if (query) {
      const needle = query.toLocaleLowerCase();
      const walker = frameDoc.createTreeWalker(frameDoc.body, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node && matches.length < 10000; node = walker.nextNode()) {
        const text = node.data.toLocaleLowerCase();
        for (let at = text.indexOf(needle); at >= 0 && matches.length < 10000; at = text.indexOf(needle, at + needle.length)) {
          const range = frameDoc.createRange(); range.setStart(node, at); range.setEnd(node, at + query.length); matches.push(range);
        }
      }
    }
  }
  if (matches.length) position = position < 0 ? (previous ? matches.length - 1 : 0) : (position + (previous ? -1 : 1) + matches.length) % matches.length;
  const highlights = frameWin.CSS.highlights;
  highlights.clear();
  if (matches.length) {
    highlights.set("pv-match", new frameWin.Highlight(...matches));
    highlights.set("pv-current", new frameWin.Highlight(matches[position]));
    const rect = matches[position].getBoundingClientRect();
    frameWin.scrollBy({ top: rect.top - frameWin.innerHeight / 3 });
    const selection = frameWin.getSelection(); selection.removeAllRanges(); selection.addRange(matches[position].cloneRange());
  }
  post({ type: "find", current: matches.length ? position + 1 : 0, total: matches.length, done: true });
  report();
}

host?.addEventListener("message", event => {
  const m = event.data ?? {};
  if (!frameDoc) return;
  switch (m.type) {
    case "find": find(String(m.query ?? ""), !!m.previous); break;
    case "zoom": zoom(m.value); break;
    case "page": goTo(Number(m.number) - 1); break;
    case "step": goTo(current() + (m.delta < 0 ? -1 : 1)); break;
    case "theme": document.documentElement.dataset.theme = m.dark ? "dark" : "light"; break;
    case "focus": frameWin.focus(); break;
  }
});

show().catch(() => post({ type: "error", kind: "web" }));
