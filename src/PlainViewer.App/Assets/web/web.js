// Plain Viewer web page view: web pages, saved web archives and EPUB books (0.8.0). Runs in the same locked-down
// WebView2 as the PDF view. The worker has read the file and collected the parts, style sheets and pictures stored in
// it (web.json, pictures served by the app). This page:
// 1. parses each part with DOMParser, which runs nothing and loads nothing;
// 2. removes scripts, event handlers, frames, objects, media, forms, <base>, <meta> and every reference to something
//    outside the file (pictures, style sheets, imports and url() in styles), counting what it removed;
// 3. moves the cleaned nodes into a frame sandboxed without scripts, which also inherits this page's content policy.
// WebView2's request filter blocks anything that still tried to load, and the app counts such attempts.
// EPUB books (after 0.9.0) are laid out as pages the size of the view, in one or two columns, turned with the keys,
// the wheel or the app's page controls; web pages and archives scroll.
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
    .replace(/(?:-moz-binding|behavior)\s*:[^;}]*/gi, "")
    .replace(/expression\s*\(/gi, "invalid(");
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
      const href = (element.getAttribute("href") || element.getAttribute("xlink:href") || "").trim();
      element.removeAttribute("xlink:href");
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
// Books: the body's size, padding and columns are set by layout() as inline !important values, so a book's own style
// sheet cannot move the pages; pictures fit inside one page.
const BOOK_STYLE = `
body { font-family: "Segoe UI", sans-serif; line-height: 1.55; color: #1b1b1b; background: #ffffff; overflow-wrap: break-word; }
img { max-width: 100%; max-height: var(--pv-page-height, 90vh); width: auto; height: auto; object-fit: contain; break-inside: avoid; }
svg { max-width: 100%; max-height: var(--pv-page-height, 90vh); break-inside: avoid; }
pre { white-space: pre-wrap; }
table { max-width: 100%; }
.pv-part + .pv-part { break-before: column; }
.pv-end { height: 0; margin: 0; padding: 0; }
.pv-extent { position: absolute; top: 0; left: 0; width: 1px; height: 1px; pointer-events: none; }`;

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
  if (paged()) {
    end = frameDoc.createElement("div"); end.className = "pv-end"; frameDoc.body.append(end);
    // Pages always run left to right (layout() sets the page's own direction); a book whose style sheet makes the
    // whole book right-to-left keeps that direction in each chapter instead.
    if (frameWin.getComputedStyle(frameDoc.body).direction === "rtl")
      for (const section of sections) if (!section.hasAttribute("dir")) section.setAttribute("dir", "rtl");
    // Marks the end of the last page, so it can be scrolled to even when its last column is empty.
    extent = frameDoc.createElement("div"); extent.className = "pv-extent"; frameDoc.body.append(extent);
    baseFont = parseFloat(frameWin.getComputedStyle(frameDoc.body).fontSize) || 16;
    for (const image of frameDoc.images) if (!image.complete) image.addEventListener("load", queueLayout, { once: true });
    frameDoc.fonts?.ready.then(queueLayout);
    window.addEventListener("resize", queueLayout);
    frameDoc.addEventListener("keydown", onPageKey, true);
    frameDoc.addEventListener("wheel", onWheel, { passive: false, capture: true });
    layout();
  }
  frameDoc.addEventListener("click", onClick, true);
  frameDoc.addEventListener("auxclick", event => event.preventDefault(), true);
  frameDoc.addEventListener("keydown", event => { if (event.key === "Enter" && event.target.closest?.(".pv-link")) onClick(event); }, true);
  frameDoc.addEventListener("dragstart", event => event.preventDefault(), true);
  frameWin.addEventListener("scroll", onScroll, { passive: true });
  post({ type: "loaded", pages: paged() ? pageCount : sections.length, notice: notice(), title: frameDoc.title });
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

// ---- Book pages ----

const paged = () => content?.kind === "book";
let pageIndex = 0, pageCount = 1, pageWidth = 1, columnsPerPage = 1, end = null, extent = null, baseFont = 16, layoutQueued = false, wheel = 0, wheelAt = 0;

function setImportant(element, styles) { for (const [name, value] of Object.entries(styles)) element.style.setProperty(name, value, "important"); }
const clampPage = index => Math.max(0, Math.min(pageCount - 1, index));
// The page a box starts on (columns of the page sit side by side; each page is one view wide).
const pageAt = rect => Math.floor((rect.left + frameWin.scrollX + 1) / pageWidth);
const firstRect = node => (node.nodeType === 1 ? node : node.parentElement)?.getClientRects()[0];

function queueLayout() { if (!layoutQueued) { layoutQueued = true; requestAnimationFrame(layout); } }

// Lays the book out for the view's current size and zoom, keeping the text that was at the top of the page in view.
function layout() {
  layoutQueued = false;
  if (!frameDoc || !end) return;
  let anchor = null;
  if (pageIndex > 0) {
    const pad = parseFloat(frameDoc.body.style.paddingLeft) || 0, top = parseFloat(frameDoc.body.style.paddingTop) || 0;
    const range = frameDoc.caretRangeFromPoint?.(pad + 2, top + 2);
    if (range && frameDoc.body.contains(range.startContainer)) anchor = range;
  }
  const fraction = pageIndex / Math.max(1, pageCount);
  const width = frameWin.innerWidth, height = frameWin.innerHeight;
  const columns = width >= 1000 ? 2 : 1;
  // Columns that run right to left (a right-to-left or vertical book) would give negative page positions.
  const flow = { direction: "ltr", "writing-mode": "horizontal-tb", position: "static" };
  setImportant(frameDoc.documentElement, { overflow: "hidden", height: "100%", margin: "0", padding: "0", ...flow });
  setImportant(frameDoc.body, flow);
  setImportant(frameDoc.body, { "font-size": `${baseFont * scale}px` });
  const em = parseFloat(frameWin.getComputedStyle(frameDoc.body).fontSize) || 16;
  // Lines of about 40 em at most; the space either side of a column is half the gap between columns, so every column
  // (and every page) starts a fixed distance after the last.
  const padX = Math.max(28, Math.floor((width / columns - 40 * em) / 2)), padY = 28;
  setImportant(frameDoc.body, { margin: "0", width: `${width}px`, "max-width": "none", "min-width": "0", height: `${height}px`, "box-sizing": "border-box",
    padding: `${padY}px ${padX}px`, "column-count": String(columns), "column-gap": `${2 * padX}px`, "column-fill": "auto", overflow: "visible" });
  frameDoc.documentElement.style.setProperty("--pv-page-height", `${height - 2 * padY}px`);
  pageWidth = Math.max(1, width); columnsPerPage = columns;
  frameWin.scrollTo(0, 0);
  extent.style.left = "0px";
  pageCount = Math.max(1, pageAt(end.getBoundingClientRect()) + 1);
  extent.style.left = `${pageCount * pageWidth - 1}px`;
  const rect = anchor ? anchor.getClientRects()[0] ?? firstRect(anchor.startContainer) : null;
  showPage(rect ? pageAt(rect) : Math.round(fraction * pageCount));
}

function showPage(index) {
  pageIndex = clampPage(index);
  frameWin.scrollTo(pageIndex * pageWidth, 0);
  report();
}

// Selecting text by dragging can scroll the page sideways; it always comes back to a whole page.
function onScroll() {
  if (!paged()) { report(); return; }
  if (frameWin.scrollY !== 0 || Math.abs(frameWin.scrollX - pageIndex * pageWidth) > 1) showPage(Math.round(frameWin.scrollX / pageWidth));
}

function onPageKey(event) {
  if (event.ctrlKey || event.altKey || event.metaKey) return;
  const key = event.key;
  if (["ArrowRight", "ArrowDown", "PageDown"].includes(key) || (key === " " && !event.shiftKey)) showPage(pageIndex + 1);
  else if (["ArrowLeft", "ArrowUp", "PageUp"].includes(key) || (key === " " && event.shiftKey)) showPage(pageIndex - 1);
  else if (key === "Home") showPage(0);
  else if (key === "End") showPage(pageCount - 1);
  else return;
  event.preventDefault();
}

function onWheel(event) {
  if (event.ctrlKey) return;
  event.preventDefault();
  const now = performance.now();
  if (now - wheelAt < 250) return;
  wheel += event.deltaY || event.deltaX;
  if (Math.abs(wheel) >= 40) { showPage(pageIndex + Math.sign(wheel)); wheel = 0; wheelAt = now; }
}

// The chapter at the top of a page's first column: the last one starting in or before that column.
function chapterAt(page) {
  let index = 0;
  const column = rect => Math.floor((rect.left + frameWin.scrollX + 1) / (pageWidth / columnsPerPage));
  sections.forEach((section, i) => { const rect = section.getClientRects()[0]; if (rect && column(rect) <= page * columnsPerPage) index = i; });
  return index;
}

// Next chapter: the first one starting on a later page; previous: the start of this chapter, or of the one before it,
// on an earlier page (two short chapters can share a page).
function chapterStep(delta) {
  const starts = sections.map(section => section.getClientRects()[0]).filter(Boolean).map(pageAt);
  if (delta > 0) { const next = starts.find(page => page > pageIndex); if (next !== undefined) showPage(next); }
  else showPage(starts.filter(page => page < pageIndex).pop() ?? 0);
}

function current() {
  if (paged()) return chapterAt(pageIndex);
  let index = 0;
  for (let i = 0; i < sections.length; i++) if (sections[i].getBoundingClientRect().top <= 8) index = i;
  return index;
}
function report() {
  if (paged()) post({ type: "state", page: pageIndex + 1, pages: pageCount, chapter: current() + 1, chapters: Math.max(1, sections.length), scale });
  else post({ type: "state", page: sections.length ? current() + 1 : 1, pages: Math.max(1, sections.length), scale });
}

function goTo(index, id) {
  const section = sections[Math.max(0, Math.min(sections.length - 1, index))];
  if (!section) return;
  const target = id ? [...section.querySelectorAll("[id], a[name]")].find(element => element.id === id || element.getAttribute("name") === id) : null;
  if (paged()) { const rect = firstRect(target ?? section); if (rect) showPage(pageAt(rect)); return; }
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
  // Books reflow: the text grows and the pages are laid out again (pictures keep fitting a page).
  if (paged()) { layout(); return; }
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
      // Case-insensitive matching on the text itself, so match positions are positions in the text.
      const pattern = new RegExp(query.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "giu");
      const walker = frameDoc.createTreeWalker(frameDoc.body, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node && matches.length < 10000; node = walker.nextNode()) {
        pattern.lastIndex = 0;
        for (let found = pattern.exec(node.data); found && matches.length < 10000; found = pattern.exec(node.data)) {
          const range = frameDoc.createRange(); range.setStart(node, found.index); range.setEnd(node, found.index + found[0].length); matches.push(range);
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
    if (paged()) showPage(pageAt(rect));
    else frameWin.scrollBy({ top: rect.top - frameWin.innerHeight / 3 });
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
    case "page": if (paged()) showPage(Number(m.number) - 1); else goTo(Number(m.number) - 1); break;
    case "step": if (paged()) showPage(pageIndex + (m.delta < 0 ? -1 : 1)); else goTo(current() + (m.delta < 0 ? -1 : 1)); break;
    case "chapter": if (paged()) chapterStep(m.delta < 0 ? -1 : 1); else goTo(current() + (m.delta < 0 ? -1 : 1)); break;
    case "theme": document.documentElement.dataset.theme = m.dark ? "dark" : "light"; break;
    case "focus": frameWin.focus(); break;
  }
});

show().catch(() => post({ type: "error", kind: "web" }));
