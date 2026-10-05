// Plain Viewer spreadsheet page. Runs inside the same locked-down WebView2 as the PDF view. The workbook JSON is
// display text prepared by the worker process; nothing here evaluates formulas or loads anything else.
// Large sheets (with a "store") keep only the rows near the viewport in the page and fetch others from the app.
const DOC = "https://doc.plainviewer.invalid";
const DATA_URL = `${DOC}/workbook.json`;
const CHUNK = 200;                                   // rows per <tbody>; off-screen chunks are skipped by the browser
const PAGE = 256;                                    // rows per request for large sheets
const MARGIN = 60;                                   // rows rendered above and below the viewport for large sheets
const host = window.chrome?.webview;
const post = message => host?.postMessage(message);
const scroller = document.getElementById("scroller");
const tabs = document.getElementById("tabs");
document.documentElement.dataset.theme = new URLSearchParams(location.search).get("theme") === "dark" ? "dark" : "light";

let workbook = null, active = 0, zoom = 1;
let hits = [], hitIndex = -1, lastQuery = "";
let big = null;                                      // state of the active large sheet
let bigHits = null;                                  // search results for the active large sheet

const columnName = index => { let name = ""; for (let v = index + 1; v > 0; v = Math.floor((v - 1) / 26)) name = String.fromCharCode(65 + (v - 1) % 26) + name; return name; };
const pixels = width => width <= 0 ? 0 : Math.round(width * 7 + 5);   // Excel character width to pixels (Calibri 11, 96 DPI)

// Row heights: an Excel row is its height in points × 4/3 pixels; this grid's default row is 22 pixels where Excel's is
// 20, so heights and offsets within rows are scaled by 22/20. Large sheets (paged from the app) keep uniform rows.
const SCALE = 22 / 20;
function rowPixels(sheet, r) {
  if (sheet.store) return 22;
  const points = sheet.rowHeights?.[r + 1] ?? sheet.defaultRowHeight ?? 15;
  return Math.max(0, Math.round(points * 4 / 3 * SCALE));
}
// The top of row r (zero-based) below the column letters, counting only rows that are shown.
function rowTop(sheet, r) {
  const hidden = sheet._hidden ??= new Set(sheet.hiddenRows);
  const tops = sheet._tops ??= [22];
  while (tops.length <= r) { const i = tops.length - 1; tops.push(tops[i] + (hidden.has(i + 1) ? 0 : rowPixels(sheet, i))); }
  return tops[r];
}

function state() {
  const sheet = workbook.sheets[active];
  post({ type: "state", sheet: active + 1, sheets: workbook.sheets.length, name: sheet.name, scale: zoom });
}

function renderTabs() {
  tabs.replaceChildren(...workbook.sheets.map((sheet, i) => {
    const button = document.createElement("button");
    button.type = "button";
    button.setAttribute("role", "tab");
    button.setAttribute("aria-selected", String(i === active));
    button.tabIndex = i === active ? 0 : -1;
    button.textContent = sheet.name;
    button.addEventListener("click", () => show(i));
    return button;
  }));
}

tabs.addEventListener("keydown", event => {
  if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
  const step = (event.key === "ArrowRight") !== (getComputedStyle(tabs).direction === "rtl") ? 1 : -1;
  show((active + step + workbook.sheets.length) % workbook.sheets.length);
  tabs.children[active]?.focus();
  event.preventDefault();
});

function show(index) {
  active = index;
  hits = []; hitIndex = -1; lastQuery = ""; big = null; bigHits = null;
  renderTabs();
  scroller.scrollTo(0, 0);
  render(workbook.sheets[index]);
  state();
}

// Values and alignment of row r, or null while a large sheet's row is still being fetched.
function cellsOf(sheet, r) {
  if (r < sheet.rows.length) return [sheet.rows[r], sheet.align[r] ?? ""];
  const fields = big?.cache.get(Math.floor(r / PAGE))?.[r % PAGE];
  return fields ? [fields.slice(1), fields[0] ?? ""] : null;
}

// The table with its column widths; returns the left offset of each column (for frozen columns).
function frame(sheet) {
  const columns = sheet.columnWidths.length;
  const table = document.createElement("table");
  table.className = "grid";
  table.setAttribute("aria-label", `Sheet ${sheet.name}`);
  if (sheet.rightToLeft) table.dir = "rtl";
  const group = document.createElement("colgroup");
  const rowHeaderColumn = document.createElement("col");
  rowHeaderColumn.style.width = "52px";
  group.append(rowHeaderColumn);
  const offsets = [];
  let x = 52;
  for (let c = 0; c < columns; c++) {
    const col = document.createElement("col");
    col.style.width = pixels(sheet.columnWidths[c]) + "px";
    group.append(col);
    offsets.push(x); x += pixels(sheet.columnWidths[c]);
  }
  // An exact width keeps the fixed layout: columns keep the workbook's widths instead of growing to fit their text.
  table.style.width = x + "px";
  table.append(group);
  return { table, offsets };
}

// Column letters plus frozen rows, in <thead> so they stay visible while scrolling.
function header(sheet, table, offsets, frozen, spans, covered, hiddenRows) {
  const head = document.createElement("thead");
  const letters = document.createElement("tr");
  letters.className = "letters";
  const corner = document.createElement("th");
  corner.className = "corner";
  corner.setAttribute("aria-label", "Row and column headers");
  letters.append(corner);
  for (let c = 0; c < sheet.columnWidths.length; c++) {
    const th = document.createElement("th");
    th.scope = "col";
    th.textContent = columnName(c);
    if (sheet.columnWidths[c] <= 0) th.className = "hidden-col";
    if (c < sheet.frozenColumns) { th.classList.add("frozen-col"); th.style.insetInlineStart = offsets[c] + "px"; }
    letters.append(th);
  }
  head.append(letters);
  for (let r = 0; r < frozen; r++) head.append(row(sheet, r, spans, covered, hiddenRows, offsets, true));
  table.append(head);
  return head;
}

// Merged cells within rows [first, last]; a merge that continues past `last` is cut there and the rest of it
// shows as empty cells. `position` maps a row to its place among rendered rows (hidden rows are not rendered).
function mergesWithin(sheet, first, last, position) {
  const spans = new Map(), covered = new Set();
  for (const [r1, c1, r2, c2] of sheet.merges) {
    if (r1 < first || r1 > last || c1 < 0 || r2 < r1 || c2 < c1 || c2 - c1 > 16384) continue;   // never trust a range blindly
    const lastRow = Math.min(r2, last);
    spans.set(`${r1},${c1}`, [position(lastRow) - position(r1) + 1, c2 - c1 + 1]);
    for (let r = r1; r <= lastRow; r++) for (let c = c1; c <= c2; c++) if (r !== r1 || c !== c1) covered.add(`${r},${c}`);
  }
  return { spans, covered };
}

function render(sheet) {
  if (sheet.chartSheet) { renderChartSheet(sheet); return; }
  if (!(sheet.rowCount ?? sheet.rows.length) && !sheet.pictures?.length) {
    const note = document.createElement("p");
    note.className = "empty";
    note.textContent = "This sheet is empty.";
    scroller.replaceChildren(note);
    return;
  }
  if (sheet.store) { renderLarge(sheet); return; }
  padForDrawings(sheet);
  const { table, offsets } = frame(sheet);
  const frozen = Math.min(sheet.frozenRows, sheet.rows.length);
  const sections = [[0, frozen]];
  for (let start = frozen; start < sheet.rows.length; start += CHUNK) sections.push([start, Math.min(start + CHUNK, sheet.rows.length)]);
  const hiddenRows = new Set(sheet.hiddenRows);
  // Merged cells span within their section.
  const spans = new Map(), covered = new Set();
  for (const [s, e] of sections) {
    const part = mergesWithin(sheet, s, e - 1, r => r);
    part.spans.forEach((v, k) => spans.set(k, v)); part.covered.forEach(k => covered.add(k));
  }
  header(sheet, table, offsets, frozen, spans, covered, hiddenRows);
  for (const [start, end] of sections.slice(1)) {
    const body = document.createElement("tbody");
    body.className = "chunk";
    body.style.containIntrinsicSize = `auto ${(end - start) * 22}px`;
    for (let r = start; r < end; r++) body.append(row(sheet, r, spans, covered, hiddenRows, offsets, false));
    table.append(body);
  }
  scroller.replaceChildren(withDrawings(sheet, table, offsets));
}

function row(sheet, r, spans, covered, hiddenRows, offsets, isFrozen) {
  const tr = document.createElement("tr");
  tr.dataset.r = r;
  if (hiddenRows.has(r + 1)) tr.hidden = true;
  const height = rowPixels(sheet, r);
  if (height === 0) tr.hidden = true; else if (height !== 22) tr.style.setProperty("--row-height", height + "px");
  const frozenTop = big ? `calc(var(--row) * ${r + 1})` : `${rowTop(sheet, r)}px`;
  if (isFrozen) { tr.className = "frozen"; tr.style.setProperty("--top", frozenTop); }
  const header = document.createElement("th");
  header.scope = "row";
  header.textContent = String(r + 1);
  if (isFrozen) header.style.top = frozenTop;
  tr.append(header);
  const data = cellsOf(sheet, r);
  if (!data) { tr.classList.add("loading"); tr.setAttribute("aria-busy", "true"); }
  const [values, layout] = data ?? [[], ""];
  const [align, styleText] = layout.split("|");
  const styleIds = styleText ? styleText.split(".").map(Number) : null;
  const styleOf = c => (styleIds && styleIds[c] > 0 && workbook.styles?.[styleIds[c]]) || null;
  const current = big && bigHits?.list[hitIndex] ? bigHits.list[hitIndex].join(",") : null;
  for (let c = 0; c < values.length; c++) {
    const key = `${r},${c}`;
    if (covered.has(key)) continue;
    const td = document.createElement("td");
    td.dataset.c = c;
    td.textContent = values[c];                                    // text only, never HTML
    if (align[c] === "r") td.className = "r"; else if (align[c] === "c") td.className = "c";
    if (values[c] === "Result unavailable") td.classList.add("unavailable");
    if (sheet.columnWidths[c] <= 0) td.classList.add("hidden-col");
    const style = styleOf(c);
    if (style) applyStyle(td, style);
    const span = spans.get(key);
    // Wrapped text shows as many lines as the row's height allows, as in Excel.
    if (style?.wrap && !big) {
      const box = document.createElement("div");
      box.className = "lines"; box.textContent = values[c];
      if (!span) box.style.maxHeight = Math.max(0, height - 2) + "px";
      td.classList.add("wrap"); td.replaceChildren(box);
    }
    if (style?.icon) { const mark = icon(style.icon); if (mark) td.prepend(mark); }
    if (span) { td.rowSpan = span[0]; td.colSpan = span[1]; td.classList.add("merged"); }
    else {
      // Like Excel, left-aligned text too long for its cell runs on over empty cells beside it.
      const extra = spill(sheet, r, c, values, align[c] ?? "l", style, spans, covered, styleOf);
      if (extra > 0) { td.colSpan = extra + 1; td.classList.add("spill"); c += extra; }
    }
    if (+td.dataset.c < sheet.frozenColumns) { td.classList.add("frozen-col"); td.style.insetInlineStart = offsets[td.dataset.c] + "px"; }
    if (isFrozen) td.style.top = frozenTop;
    if (big && bigHits?.set.has(key)) td.classList.add(key === current ? "current" : "hit");
    tr.append(td);
  }
  return tr;
}

// Cell styles from the workbook. Every value is checked here as well: only a known form reaches the page's CSS.
const COLOUR = /^#[0-9a-f]{6}$/;
const FONT_NAME = /^[\p{L}\p{N} \-]{1,64}$/u;
function applyStyle(td, style) {
  const s = td.style;
  if (style.bold) s.fontWeight = "700";
  if (style.italic) s.fontStyle = "italic";
  const lines = [style.underline && "underline", style.strike && "line-through"].filter(Boolean);
  if (lines.length) s.textDecorationLine = lines.join(" ");
  if (COLOUR.test(style.color ?? "")) s.color = style.color;
  if (COLOUR.test(style.fill ?? "")) {
    s.backgroundColor = style.fill;
    if (!style.color) s.color = "#000000";                        // automatic text on a fill is black, as in Excel
  }
  if (typeof style.size === "number" && style.size > 0) s.fontSize = `${Math.min(big ? 1.5 : 4, Math.max(0.6, style.size)) * 13}px`;
  if (FONT_NAME.test(style.font ?? "")) s.fontFamily = `"${style.font}", "Segoe UI", system-ui, sans-serif`;
  if (style.vAlign === "top" || style.vAlign === "middle") s.verticalAlign = style.vAlign;
  if (Number.isInteger(style.indent) && style.indent > 0) s.paddingInlineStart = `${4 + Math.min(15, style.indent) * 9}px`;
  for (const [side, value] of [["Top", style.top], ["Right", style.right], ["Bottom", style.bottom], ["Left", style.left]]) {
    const m = /^([123]) (solid|dashed|dotted|double) (#[0-9a-f]{6})$/.exec(value ?? "");
    if (m) s[`border${side}`] = `${m[1]}px ${m[2]} ${m[3]}`;
  }
  // Data bar (conditional formatting): a bar of the given share of the cell, fading like Excel's gradient bars.
  const bar = /^(\d{1,3}) (#[0-9a-f]{6})$/.exec(style.bar ?? "");
  if (bar) {
    const share = Math.min(100, Number(bar[1]));
    s.backgroundImage = `linear-gradient(to right, ${bar[2]} 0%, ${bar[2]}40 ${share}%, transparent ${share}%)`;
    s.backgroundSize = "100% 70%"; s.backgroundRepeat = "no-repeat"; s.backgroundPosition = "left center";
  }
  // Line pattern fills (Excel's stripes, grids and hatching) over the fill colour; a data bar takes their place.
  const pattern = PATTERN.exec(style.pattern ?? "");
  if (pattern && !bar) {
    const [, weight, kind, colour] = pattern, width = weight === "dark" ? 2 : 1;
    const lines = angle => `repeating-linear-gradient(${angle}deg, ${colour} 0 ${width}px, transparent ${width}px 4px)`;
    s.backgroundImage = { Horizontal: [0], Vertical: [90], Down: [45], Up: [-45], Grid: [0, 90], Trellis: [45, -45] }[kind].map(lines).join(", ");
  }
}
const PATTERN = /^(dark|light)(Horizontal|Vertical|Down|Up|Grid|Trellis) (#[0-9a-f]{6})$/;

// Icon (conditional formatting icon sets): a coloured symbol before the value. Only known shapes and colours are drawn.
const ICONS = {
  "arrow-up": "↑", "arrow-down": "↓", "arrow-right": "→", "arrow-up-right": "↗", "arrow-down-right": "↘", circle: "●", flag: "⚑",
  check: "✔", cross: "✖", exclamation: "!", "star-full": "★", "star-half": "★", "star-empty": "☆", "triangle-up": "▲", "triangle-down": "▼", dash: "▬",
  "bar-0": "▁", "bar-1": "▂", "bar-2": "▄", "bar-3": "▆", "bar-4": "█", "quarter-0": "○", "quarter-1": "◔", "quarter-2": "◑", "quarter-3": "◕", "quarter-4": "●"
};
const ICON_COLOURS = { green: "#1e9e4a", yellow: "#e6a700", red: "#d9302c", gray: "#7f7f7f", black: "#262626", pink: "#f28b8b", blue: "#3b73c4" };
function icon(value) {
  const [shape, colour] = value.split(" ");
  if (!(shape in ICONS) || !(colour in ICON_COLOURS)) return null;
  const mark = document.createElement("span");
  mark.className = "icon"; mark.textContent = ICONS[shape];
  mark.style.color = ICON_COLOURS[colour];
  if (shape === "star-half") mark.style.opacity = "0.55";
  mark.setAttribute("aria-hidden", "true");
  return mark;
}

const measure = document.createElement("canvas").getContext("2d");
function spill(sheet, r, c, values, align, style, spans, covered, styleOf) {
  const text = values[c];
  if (!text || align !== "l" || style?.wrap) return 0;
  measure.font = `${style?.bold ? "700 " : ""}${style?.italic || text === "Result unavailable" ? "italic " : ""}13px "Segoe UI"`;
  const needed = measure.measureText(text).width + 8;
  let room = pixels(sheet.columnWidths[c]), extra = 0;
  // Stays within the frozen columns, and stops at the first cell with content, a fill or a border, or a merge (other
  // formatting of an empty cell, such as bold, does not stop it, as in Excel).
  const last = c < sheet.frozenColumns ? sheet.frozenColumns - 1 : sheet.columnWidths.length - 1;
  const visible = s => s && (s.fill || s.top || s.right || s.bottom || s.left);
  while (room < needed && c + extra + 1 <= last) {
    const next = c + extra + 1, key = `${r},${next}`;
    if ((values[next] ?? "") !== "" || spans.has(key) || covered.has(key) || visible(styleOf(next))) break;
    room += pixels(sheet.columnWidths[next]); extra++;
  }
  return room >= needed || extra > 0 ? extra : 0;
}

// ---- Large sheets: only rows near the viewport are in the page; spacers stand in for the rest. ----

function spacer(columns) {
  const body = document.createElement("tbody");
  const tr = document.createElement("tr");
  tr.className = "spacer";
  tr.setAttribute("aria-hidden", "true");
  const td = document.createElement("td");
  td.colSpan = columns + 1;
  tr.append(td);
  body.append(tr);
  return body;
}

function renderLarge(sheet) {
  const hidden = new Set(sheet.hiddenRows);
  const frozen = Math.min(sheet.frozenRows, sheet.rowCount);
  const order = new Int32Array(sheet.rowCount - frozen);
  let count = 0;
  for (let r = frozen; r < sheet.rowCount; r++) if (!hidden.has(r + 1)) order[count++] = r;
  const { table, offsets } = frame(sheet);
  table.setAttribute("aria-rowcount", String(sheet.rowCount + 1));
  const top = spacer(sheet.columnWidths.length), body = document.createElement("tbody"), bottom = spacer(sheet.columnWidths.length);
  big = { sheet, rows: order.subarray(0, count), hidden, offsets, top, body, bottom, start: -1, end: -1, cache: new Map(), pending: new Map(), head: null, frozen };
  const heads = mergesWithin(sheet, 0, frozen - 1, r => r);
  big.head = header(sheet, table, offsets, frozen, heads.spans, heads.covered, hidden);
  table.append(top, body, bottom);
  scroller.replaceChildren(withDrawings(sheet, table, offsets));
  update(true, 0);
}

// ---- Pictures and charts, drawn over the cells where the workbook places them. ----

const MEDIA = /^media-\d+-\d+\.(png|jpeg|gif|bmp|webp|avif|ico)$/;

// The table inside a positioned wrapper with a layer of pictures and charts. Rows are all var(--row) high, so a
// cell's position follows from the column widths and the number of hidden rows above it.
function withDrawings(sheet, table, offsets) {
  if (!sheet.pictures?.length) return table;
  const wrap = document.createElement("div");
  wrap.className = "drawings";
  if (sheet.rightToLeft) wrap.dir = "rtl";
  const layer = document.createElement("div");
  layer.className = "layer";
  const hidden = new Set(sheet.hiddenRows);
  const columnCount = sheet.columnWidths.length;
  const x = (c, offset) => {
    let left = c < columnCount ? offsets[c] : (offsets[columnCount - 1] ?? 52) + pixels(sheet.columnWidths[columnCount - 1] ?? 8.43) + (c - columnCount) * pixels(8.43);
    return left + Math.min(offset, pixels(sheet.columnWidths[c] ?? 8.43));
  };
  const y = (r, offset) => rowTop(sheet, r) + (hidden.has(r + 1) ? 0 : Math.min(rowPixels(sheet, r), offset * SCALE));
  for (const p of sheet.pictures) {
    let left = x(p.column, p.columnOffset), top = y(p.row, p.rowOffset);
    let width = p.toColumn >= 0 ? x(p.toColumn, p.toColumnOffset) - left : p.width;
    let height = p.toRow >= 0 ? y(p.toRow, p.toRowOffset) - top : p.height * SCALE;
    // A shape or picture inside a group: its own box within the anchor's box.
    if (Array.isArray(p.part) && p.part.length === 4 && p.part.every(finite)) {
      left += p.part[0] * width; top += p.part[1] * height; width *= p.part[2]; height *= p.part[3];
    }
    // A line may be flat in one direction; everything else needs some width and height.
    if (p.shape?.geometry === "line" && (width > 1 || height > 1)) { width = Math.max(width, 2); height = Math.max(height, 2); }
    if (!(width > 1 && height > 1)) continue;
    let item;
    if (p.chart) item = chartElement(p.chart, width, height);
    else if (p.shape) item = shapeElement(p.shape, width, height, p.description);
    else if (MEDIA.test(p.media ?? "")) {
      item = document.createElement("img");
      item.src = `${DOC}/media?name=${encodeURIComponent(p.media)}`;
      item.alt = p.description || "Picture";
      item.draggable = false;
    } else continue;
    item.classList.add("drawing");
    Object.assign(item.style, { insetInlineStart: left + "px", top: top + "px", width: width + "px", height: height + "px" });
    layer.append(item);
  }
  wrap.append(table, layer);
  return wrap;
}

// Empty cells under pictures and charts that reach past the sheet's data, so they sit on the grid as in Excel.
function padForDrawings(sheet) {
  if (!sheet.pictures?.length || sheet.padded) return;
  sheet.padded = true;
  let rows = sheet.rows.length, columns = sheet.columnWidths.length;
  for (const p of sheet.pictures) {
    rows = Math.max(rows, (p.toRow >= 0 ? p.toRow : p.row + Math.ceil(p.height / 20)) + 2);
    columns = Math.max(columns, (p.toColumn >= 0 ? p.toColumn : p.column + Math.ceil(p.width / 64)) + 2);
  }
  rows = Math.min(rows, sheet.rows.length + 1000); columns = Math.min(columns, sheet.columnWidths.length + 100);
  while (sheet.columnWidths.length < columns) sheet.columnWidths.push(8.43);
  for (const values of sheet.rows) while (values.length < columns) values.push("");
  while (sheet.rows.length < rows) { sheet.rows.push(Array(columns).fill("")); sheet.align.push(""); }
}

function renderChartSheet(sheet) {
  const chart = sheet.pictures?.[0]?.chart;
  if (!chart) {
    const note = document.createElement("p");
    note.className = "empty";
    note.textContent = sheet.notice || "This chart sheet has no chart to show.";
    scroller.replaceChildren(note);
    return;
  }
  const width = Math.max(320, scroller.clientWidth - 32), height = Math.max(240, scroller.clientHeight - 32);
  const item = chartElement(chart, width, height);
  item.classList.add("chart-sheet");
  scroller.replaceChildren(item);
}

// A chart drawn from its saved values as SVG. Charts keep the workbook's own look: white, with Office's colours.
const SVG = "http://www.w3.org/2000/svg";
const PALETTE = ["#4472c4", "#ed7d31", "#a5a5a5", "#ffc000", "#5b9bd5", "#70ad47", "#264478", "#9e480e", "#636363", "#997300"];
const svg = (name, attributes = {}, text) => {
  const element = document.createElementNS(SVG, name);
  for (const [key, value] of Object.entries(attributes)) element.setAttribute(key, String(value));
  if (text !== undefined) element.textContent = text;                // text only, never markup
  return element;
};
const colourOf = (series, i) => COLOUR.test(series?.color ?? "") ? series.color : PALETTE[i % PALETTE.length];
const finite = v => typeof v === "number" && Number.isFinite(v);

// Lowest and highest of a long list (charts may hold 500,000 values, too many to spread into Math.min).
function lowest(list, start = Infinity) { let low = start; for (const v of list) if (v < low) low = v; return low; }
function highest(list, start = -Infinity) { let high = start; for (const v of list) if (v > high) high = v; return high; }

function niceTicks(min, max) {
  if (!Number.isFinite(min) || !Number.isFinite(max)) { min = 0; max = 1; }
  if (min === max) { min -= 1; max += 1; }
  // A range smaller than the numbers' own precision (1e16 and 1e16 + 2) is widened, so each step moves the value.
  const spread = Math.max(Math.abs(min), Math.abs(max)) * 1e-9;
  if (max - min < spread) { min -= spread; max += spread; }
  const raw = (max - min) / 5, power = 10 ** Math.floor(Math.log10(raw)), step = [1, 2, 2.5, 5, 10].map(m => m * power).find(s => s >= raw);
  const ticks = [];
  for (let v = Math.floor(min / step) * step; v <= max + step * 0.001 && ticks.length < 100; v += step) ticks.push(Math.round(v / step) * step);
  if (ticks.length < 2 || ticks[ticks.length - 1] < max) ticks.push(ticks[ticks.length - 1] + step);
  return ticks;
}
const tickLabel = (v, percent) => (percent ? `${Math.round(v)}%` : Math.abs(v) >= 1e6 ? `${+(v / 1e6).toFixed(2)}M` : Math.abs(v) >= 1e4 ? `${+(v / 1e3).toFixed(1)}k` : `${+v.toFixed(4)}`);

function chartElement(chart, width, height) {
  const root = svg("svg", { viewBox: `0 0 ${width} ${height}`, role: "img", class: "chart" });
  root.setAttribute("aria-label", chart.title ? `Chart: ${chart.title}` : "Chart");
  root.append(svg("rect", { x: 0.5, y: 0.5, width: width - 1, height: height - 1, fill: "#ffffff", stroke: "#d9d9d9" }));
  const series = (chart.series ?? []).filter(s => Array.isArray(s.values));
  let top = 10;
  if (chart.title) { root.append(svg("text", { x: width / 2, y: 26, "text-anchor": "middle", class: "chart-title" }, chart.title)); top = 40; }
  const round = chart.type === "pie" || chart.type === "doughnut";
  // A stock chart's series are parts of one mark (high, low, close), so it has no legend.
  const names = round ? chart.categories ?? [] : chart.type === "stock" ? [] : series.map(s => s.name);
  const legendHeight = names.length && height > 160 ? 24 : 0;
  if (legendHeight) {
    const legend = svg("g", { class: "chart-legend" });
    let lx = 0;
    names.slice(0, 20).forEach((name, i) => {
      const colour = round ? PALETTE[i % PALETTE.length] : colourOf(series[i], i);
      legend.append(svg("rect", { x: lx, y: -8, width: 9, height: 9, fill: colour }), svg("text", { x: lx + 13, y: 0 }, name));
      lx += 22 + Math.min(160, measureChart(name));
    });
    legend.setAttribute("transform", `translate(${Math.max(8, (width - lx) / 2)} ${height - 10})`);
    root.append(legend);
  }
  const area = { left: 8, top, right: width - 10, bottom: height - 10 - legendHeight };
  // Axis titles: the value axis along the left (the category axis for horizontal bars), the other along the bottom.
  if (!round && chart.type !== "radar" && series.length) {
    const horizontal = chart.type === "bar";
    const left = horizontal ? chart.categoryTitle : chart.valueTitle, bottom = horizontal ? chart.valueTitle : chart.categoryTitle;
    if (left) {
      root.append(svg("text", { transform: `translate(${area.left + 9} ${(area.top + area.bottom) / 2}) rotate(-90)`, "text-anchor": "middle", class: "chart-axis-title" }, left));
      area.left += 18;
    }
    if (bottom) {
      root.append(svg("text", { x: (area.left + area.right) / 2, y: area.bottom - 2, "text-anchor": "middle", class: "chart-axis-title" }, bottom));
      area.bottom -= 18;
    }
  }
  if (!series.length || area.right - area.left < 40 || area.bottom - area.top < 40) {
    root.append(svg("text", { x: width / 2, y: height / 2, "text-anchor": "middle", class: "chart-note" }, chart.notice || "This chart has no data to show."));
    return root;
  }
  if (round) drawRound(root, chart, series[0], area);
  else if (chart.type === "scatter" || chart.type === "bubble") drawScatter(root, series, area, chart.type === "bubble");
  else if (chart.type === "radar") drawRadar(root, chart, series, area);
  else if (chart.type === "stock") drawStock(root, chart, series, area);
  else drawAxes(root, chart, series, area);
  if (chart.notice) root.append(svg("text", { x: width - 8, y: height - 4 - legendHeight, "text-anchor": "end", class: "chart-note" }, chart.notice));
  return root;
}

// A shape or text box: its outline (flipped and rotated as saved), then its text laid out inside, as Excel does with
// its default insets. Only checked colours, known outlines and plain text reach the page.
let markerCount = 0;
function shapeElement(shape, width, height, description) {
  const root = svg("svg", { viewBox: `0 0 ${width} ${height}`, class: "shape", overflow: "visible" });
  root.setAttribute("role", "img");
  root.setAttribute("aria-label", description || "Shape");
  const colour = c => COLOUR.test(c ?? "") ? c : "none";
  const stroke = colour(shape.line), lineWidth = Math.min(20, Math.max(0.5, +shape.lineWidth || 1));
  const look = { fill: shape.geometry === "line" ? "none" : colour(shape.fill), stroke, "stroke-width": stroke === "none" ? 0 : lineWidth };
  if (shape.dash === "dash") look["stroke-dasharray"] = `${lineWidth * 4} ${lineWidth * 3}`;
  else if (shape.dash === "dot") look["stroke-dasharray"] = `${lineWidth} ${lineWidth * 2}`;
  const all = svg("g", {});
  const rotation = finite(shape.rotation) ? shape.rotation : 0;
  if (rotation) all.setAttribute("transform", `rotate(${rotation} ${width / 2} ${height / 2})`);
  const outline = svg("g", { transform: `translate(${shape.flipH ? width : 0} ${shape.flipV ? height : 0}) scale(${shape.flipH ? -1 : 1} ${shape.flipV ? -1 : 1})` });
  const w = width, h = height, m = Math.min(w, h), points = list => svg("polygon", { points: list.map(p => p.join(",")).join(" "), ...look });
  switch (shape.geometry) {
    case "line": {
      const line = svg("line", { x1: w <= 2 ? w / 2 : 0, y1: h <= 2 ? h / 2 : 0, x2: w <= 2 ? w / 2 : w, y2: h <= 2 ? h / 2 : h, ...look });
      // Arrow heads as markers in the line's colour.
      for (const [end, wanted] of [["marker-start", shape.startArrow], ["marker-end", shape.endArrow]]) {
        if (!wanted || stroke === "none") continue;
        const id = `arrow-${++markerCount}`;
        const marker = svg("marker", { id, viewBox: "0 0 10 10", refX: 9, refY: 5, markerWidth: 5, markerHeight: 5, orient: "auto-start-reverse" });
        marker.append(svg("path", { d: "M0,0 L10,5 L0,10 Z", fill: stroke }));
        root.append(marker);
        line.setAttribute(end, `url(#${id})`);
      }
      outline.append(line);
      break;
    }
    case "roundRect": outline.append(svg("rect", { x: 0, y: 0, width: w, height: h, rx: m / 6, ...look })); break;
    case "ellipse": outline.append(svg("ellipse", { cx: w / 2, cy: h / 2, rx: w / 2, ry: h / 2, ...look })); break;
    case "triangle": outline.append(points([[w / 2, 0], [w, h], [0, h]])); break;
    case "rtTriangle": outline.append(points([[0, 0], [0, h], [w, h]])); break;
    case "diamond": outline.append(points([[w / 2, 0], [w, h / 2], [w / 2, h], [0, h / 2]])); break;
    case "parallelogram": outline.append(points([[m / 4, 0], [w, 0], [w - m / 4, h], [0, h]])); break;
    case "hexagon": outline.append(points([[m / 4, 0], [w - m / 4, 0], [w, h / 2], [w - m / 4, h], [m / 4, h], [0, h / 2]])); break;
    case "rightArrow": case "leftArrow": {
      const head = Math.min(w, h / 2 * 1);
      const right = [[0, h / 4], [w - head, h / 4], [w - head, 0], [w, h / 2], [w - head, h], [w - head, h * 3 / 4], [0, h * 3 / 4]];
      outline.append(points(shape.geometry === "rightArrow" ? right : right.map(([px, py]) => [w - px, py])));
      break;
    }
    case "upArrow": case "downArrow": {
      const head = Math.min(h, w / 2);
      const down = [[w / 4, 0], [w / 4, h - head], [0, h - head], [w / 2, h], [w, h - head], [w * 3 / 4, h - head], [w * 3 / 4, 0]];
      outline.append(points(shape.geometry === "downArrow" ? down : down.map(([px, py]) => [px, h - py])));
      break;
    }
    default: outline.append(svg("rect", { x: 0, y: 0, width: w, height: h, ...look }));
  }
  all.append(outline);
  const paragraphs = (shape.paragraphs ?? []).filter(p => typeof p.text === "string");
  if (paragraphs.length && shape.geometry !== "line") {
    const box = svg("foreignObject", { x: 0, y: 0, width: w, height: h });
    const text = document.createElementNS("http://www.w3.org/1999/xhtml", "div");
    text.className = `shape-text ${{ ctr: "middle", b: "bottom" }[shape.vAlign] ?? "top"}`;
    for (const p of paragraphs) {
      const line = document.createElementNS("http://www.w3.org/1999/xhtml", "div");
      line.textContent = p.text || " ";
      Object.assign(line.style, {
        textAlign: { ctr: "center", r: "right" }[p.align] ?? "left",
        fontWeight: p.bold ? "700" : "400", fontStyle: p.italic ? "italic" : "normal",
        fontSize: `${Math.min(96, Math.max(4, +p.size || 11)) * 4 / 3}px`, color: COLOUR.test(p.color ?? "") ? p.color : "#000000"
      });
      text.append(line);
    }
    box.append(text);
    all.append(box);
  }
  root.append(all);
  return root;
}

const measureChart = text => { measure.font = '11px "Segoe UI"'; return measure.measureText(text).width; };

// Column, bar, line and area charts: categories along one axis, values along the other.
function drawAxes(root, chart, series, area) {
  const categories = chart.categories ?? [];
  const n = Math.max(categories.length, ...series.map(s => s.values.length));
  const horizontal = chart.type === "bar", stacked = chart.stacked, percent = chart.percent;
  const value = (s, i) => finite(series[s].values[i]) ? series[s].values[i] : 0;
  const totals = Array.from({ length: n }, (_, i) => series.reduce((sum, _s, s) => sum + Math.abs(value(s, i)), 0) || 1);
  const shown = (s, i) => percent ? value(s, i) / totals[i] * 100 : value(s, i);
  let min = 0, max = 0;
  for (let i = 0; i < n; i++) {
    if (stacked) {
      let up = 0, down = 0;
      series.forEach((_s, s) => { const v = shown(s, i); if (v >= 0) up += v; else down += v; });
      max = Math.max(max, up); min = Math.min(min, down);
    } else series.forEach((_s, s) => { if (finite(series[s].values[i])) { max = Math.max(max, shown(s, i)); min = Math.min(min, shown(s, i)); } });
  }
  if (percent) { max = Math.min(max, 100); min = Math.max(min, -100); }
  const ticks = niceTicks(min, max), low = ticks[0], high = ticks[ticks.length - 1];
  const labelWidth = Math.min(80, Math.max(...ticks.map(t => measureChart(tickLabel(t, percent))))) + 8;
  const categoryWidth = horizontal ? Math.min(140, Math.max(20, ...categories.slice(0, 200).map(measureChart))) + 8 : 0;
  const plot = horizontal
    ? { left: area.left + categoryWidth, top: area.top, right: area.right, bottom: area.bottom - 18 }
    : { left: area.left + labelWidth, top: area.top, right: area.right, bottom: area.bottom - 18 };
  const along = horizontal ? plot.bottom - plot.top : plot.right - plot.left;       // category axis length
  const across = horizontal ? plot.right - plot.left : plot.bottom - plot.top;
  const scale = v => (v - low) / (high - low) * across;
  const grid = svg("g", { class: "chart-grid" });
  for (const t of ticks) {
    const p = scale(t);
    if (horizontal) {
      grid.append(svg("line", { x1: plot.left + p, x2: plot.left + p, y1: plot.top, y2: plot.bottom }));
      grid.append(svg("text", { x: plot.left + p, y: plot.bottom + 14, "text-anchor": "middle" }, tickLabel(t, percent)));
    } else {
      grid.append(svg("line", { x1: plot.left, x2: plot.right, y1: plot.bottom - p, y2: plot.bottom - p }));
      grid.append(svg("text", { x: plot.left - 6, y: plot.bottom - p + 4, "text-anchor": "end" }, tickLabel(t, percent)));
    }
  }
  root.append(grid);
  const band = along / Math.max(1, n);
  const every = Math.max(1, Math.ceil(n / Math.max(1, Math.floor(along / (horizontal ? 16 : 60)))));
  const labels = svg("g", { class: "chart-grid" });
  for (let i = 0; i < n; i += every) {
    const text = categories[i] ?? "", mid = band * (i + 0.5);
    labels.append(horizontal
      ? svg("text", { x: plot.left - 6, y: plot.top + mid + 4, "text-anchor": "end" }, text.length > 24 ? text.slice(0, 23) + "…" : text)
      : svg("text", { x: plot.left + mid, y: plot.bottom + 14, "text-anchor": "middle" }, text.length > 16 ? text.slice(0, 15) + "…" : text));
  }
  root.append(labels);
  const zero = scale(Math.min(Math.max(0, low), high));
  const point = (i, v) => horizontal ? [plot.left + scale(v), plot.top + band * (i + 0.5)] : [plot.left + band * (i + 0.5), plot.bottom - scale(v)];
  // Data labels, drawn last so that they lie over the bars and lines.
  const dataLabels = svg("g", { class: "chart-label" });
  const label = (text, x, y, anchor = "middle") => { if (text) dataLabels.append(svg("text", { x, y, "text-anchor": anchor }, text)); };
  // Combined charts: each series may have its own kind (columns with lines over them).
  const kindOf = s => s.type ?? chart.type, barLike = s => kindOf(s) === "column" || kindOf(s) === "bar";
  const bars = series.map((s, k) => [s, k]).filter(([s]) => barLike(s));
  if (bars.length) {
    const gap = band * 0.25, inner = band - gap * 2, width = stacked ? inner : inner / bars.length;
    const base = Array(n).fill(0), baseDown = Array(n).fill(0);
    bars.forEach(([s, k], slot) => {
      const group = svg("g", { fill: colourOf(s, k) });
      for (let i = 0; i < n; i++) {
        if (!finite(s.values[i])) continue;
        const v = shown(k, i);
        let from = 0;
        if (stacked) { if (v >= 0) { from = base[i]; base[i] += v; } else { from = baseDown[i]; baseDown[i] += v; } }
        const a = scale(Math.min(Math.max(from, low), high)), b = scale(Math.min(Math.max(from + v, low), high));
        const offset = band * i + gap + (stacked ? 0 : width * slot);
        const [start, length] = [Math.min(a, b), Math.abs(b - a)];
        group.append(horizontal
          ? svg("rect", { x: plot.left + start, y: plot.top + offset, width: length, height: width })
          : svg("rect", { x: plot.left + offset, y: plot.bottom - start - length, width, height: length }));
        // Outside the end of a bar, or in the middle of a stacked segment.
        const text = s.pointLabels?.[i], outward = b >= a;
        if (horizontal) {
          const y = plot.top + offset + width / 2 + 4;
          if (stacked) label(text, plot.left + start + length / 2, y);
          else label(text, plot.left + (outward ? start + length + 4 : start - 4), y, outward ? "start" : "end");
        } else {
          const x = plot.left + offset + width / 2;
          if (stacked) label(text, x, plot.bottom - start - length / 2 + 4);
          else label(text, x, outward ? plot.bottom - start - length - 4 : plot.bottom - start + 12);
        }
      }
      root.append(group);
    });
  }
  if (bars.length < series.length) {
    const base = Array(n).fill(0);
    const drawn = series.map((s, k) => {
      if (barLike(s)) return [];
      const points = [];
      for (let i = 0; i < n; i++) {
        if (!finite(s.values[i]) && !stacked) { points.push(null); continue; }
        const from = base[i], v = stacked ? from + shown(k, i) : shown(k, i);
        if (stacked) base[i] = v;
        points.push({ i, from, v });
      }
      return points;
    });
    drawn.forEach((points, k) => {
      const colour = colourOf(series[k], k);
      const valid = points.filter(Boolean);
      if (!valid.length) return;
      if (kindOf(series[k]) === "area") {
        const upper = valid.map(p => point(p.i, p.v)), lower = valid.map(p => point(p.i, stacked ? p.from : Math.max(low, Math.min(0, high)))).reverse();
        root.append(svg("polygon", { points: [...upper, ...lower].map(p => p.join(",")).join(" "), fill: colour, "fill-opacity": 0.85 }));
      } else {
        let path = "", pen = false;
        for (const p of points) { if (!p) { pen = false; continue; } const [px, py] = point(p.i, p.v); path += `${pen ? "L" : "M"}${px},${py} `; pen = true; }
        root.append(svg("path", { d: path, fill: "none", stroke: colour, "stroke-width": 2.25, "stroke-linejoin": "round" }));
        if (n <= 60) for (const p of valid) { const [px, py] = point(p.i, p.v); root.append(svg("circle", { cx: px, cy: py, r: 3, fill: colour })); }
      }
      for (const p of valid) { const [px, py] = point(p.i, p.v); label(series[k].pointLabels?.[p.i], px, py - 8); }
    });
  }
  root.append(dataLabels);
  root.append(horizontal
    ? svg("line", { x1: plot.left + zero, x2: plot.left + zero, y1: plot.top, y2: plot.bottom, class: "chart-axis" })
    : svg("line", { x1: plot.left, x2: plot.right, y1: plot.bottom - zero, y2: plot.bottom - zero, class: "chart-axis" }));
}

// Radar charts: one spoke per category, values outwards from the centre, rings at the value ticks.
function drawRadar(root, chart, series, area) {
  const categories = chart.categories ?? [], n = Math.max(categories.length, ...series.map(s => s.values.length));
  if (n < 3) return;
  const values = series.flatMap(s => s.values.filter(finite));
  const ticks = niceTicks(lowest(values, 0), highest(values, 0)), low = ticks[0], high = ticks[ticks.length - 1];
  const cx = (area.left + area.right) / 2, cy = (area.top + area.bottom) / 2 + 6;
  const r = Math.max(10, Math.min(area.right - area.left, area.bottom - area.top) / 2 - 22);
  const at = (i, v) => { const a = -Math.PI / 2 + i / n * Math.PI * 2, d = (v - low) / (high - low) * r; return [cx + d * Math.cos(a), cy + d * Math.sin(a)]; };
  const grid = svg("g", { class: "chart-grid" });
  for (const t of ticks) {
    grid.append(svg("polygon", { points: Array.from({ length: n }, (_, i) => at(i, t).join(",")).join(" "), fill: "none" }));
    grid.append(svg("text", { x: cx + 3, y: at(0, t)[1] + 4 }, tickLabel(t)));
  }
  for (let i = 0; i < n; i++) {
    const [ex, ey] = at(i, high), [lx, ly] = at(i, high + (high - low) * 0.12);
    grid.append(svg("line", { x1: cx, y1: cy, x2: ex, y2: ey }));
    const text = categories[i] ?? "";
    grid.append(svg("text", { x: lx, y: ly + 4, "text-anchor": Math.abs(lx - cx) < 4 ? "middle" : lx > cx ? "start" : "end" }, text.length > 16 ? text.slice(0, 15) + "…" : text));
  }
  root.append(grid);
  series.forEach((s, k) => {
    const colour = colourOf(s, k), points = Array.from({ length: n }, (_, i) => at(i, finite(s.values[i]) ? s.values[i] : low));
    root.append(svg("polygon", { points: points.map(p => p.join(",")).join(" "), fill: chart.filled ? colour : "none", "fill-opacity": chart.filled ? 0.45 : 0, stroke: colour, "stroke-width": 2 }));
    points.forEach((p, i) => { const text = s.pointLabels?.[i]; if (text) root.append(svg("text", { x: p[0], y: p[1] - 6, "text-anchor": "middle", class: "chart-label" }, text)); });
  });
}

// Stock charts: per category a line from high to low, with the close marked to the right and (with four series)
// the open to the left. Series order as Excel saves it: (open,) high, low, close.
function drawStock(root, chart, series, area) {
  const categories = chart.categories ?? [], n = Math.max(categories.length, ...series.map(s => s.values.length));
  const four = series.length >= 4, [open, high, low, close] = four ? series.slice(0, 4) : [null, ...series.slice(0, 3)];
  if (!high || !low || !close) { drawAxes(root, { ...chart, type: "line" }, series, area); return; }
  const all = [open, high, low, close].filter(Boolean).flatMap(s => s.values.filter(finite));
  if (!all.length) return;
  const ticks = niceTicks(lowest(all), highest(all)), bottom = ticks[0], top = ticks[ticks.length - 1];
  const labelWidth = Math.min(80, Math.max(...ticks.map(t => measureChart(tickLabel(t))))) + 8;
  const plot = { left: area.left + labelWidth, top: area.top, right: area.right, bottom: area.bottom - 18 };
  const y = v => plot.bottom - (v - bottom) / (top - bottom) * (plot.bottom - plot.top), band = (plot.right - plot.left) / Math.max(1, n);
  const grid = svg("g", { class: "chart-grid" });
  for (const t of ticks) grid.append(svg("line", { x1: plot.left, x2: plot.right, y1: y(t), y2: y(t) }), svg("text", { x: plot.left - 6, y: y(t) + 4, "text-anchor": "end" }, tickLabel(t)));
  const every = Math.max(1, Math.ceil(n / Math.max(1, Math.floor((plot.right - plot.left) / 60))));
  for (let i = 0; i < n; i += every) grid.append(svg("text", { x: plot.left + band * (i + 0.5), y: plot.bottom + 14, "text-anchor": "middle" }, (categories[i] ?? "").slice(0, 16)));
  root.append(grid);
  const marks = svg("g", { stroke: colourOf(close, 0), "stroke-width": 2 });
  for (let i = 0; i < n; i++) {
    const h = high.values[i], l = low.values[i], c = close.values[i], x = plot.left + band * (i + 0.5), tick = Math.min(8, band / 3);
    if (finite(h) && finite(l)) marks.append(svg("line", { x1: x, x2: x, y1: y(h), y2: y(l) }));
    if (finite(c)) marks.append(svg("line", { x1: x, x2: x + tick, y1: y(c), y2: y(c) }));
    if (open && finite(open.values[i])) marks.append(svg("line", { x1: x - tick, x2: x, y1: y(open.values[i]), y2: y(open.values[i]) }));
  }
  root.append(marks, svg("line", { x1: plot.left, x2: plot.right, y1: plot.bottom, y2: plot.bottom, class: "chart-axis" }));
}

function drawScatter(root, series, area, bubbles) {
  const xs = series.flatMap(s => (s.x?.length ? s.x : s.values.map((_v, i) => i + 1)).filter(finite));
  const ys = series.flatMap(s => s.values.filter(finite));
  if (!xs.length || !ys.length) return;
  const xt = niceTicks(lowest(xs), highest(xs)), yt = niceTicks(lowest(ys, 0), highest(ys, 0));
  const labelWidth = Math.min(80, Math.max(...yt.map(t => measureChart(tickLabel(t))))) + 8;
  const plot = { left: area.left + labelWidth, top: area.top, right: area.right - 8, bottom: area.bottom - 18 };
  const sx = v => plot.left + (v - xt[0]) / (xt[xt.length - 1] - xt[0]) * (plot.right - plot.left);
  const sy = v => plot.bottom - (v - yt[0]) / (yt[yt.length - 1] - yt[0]) * (plot.bottom - plot.top);
  const grid = svg("g", { class: "chart-grid" });
  for (const t of yt) grid.append(svg("line", { x1: plot.left, x2: plot.right, y1: sy(t), y2: sy(t) }), svg("text", { x: plot.left - 6, y: sy(t) + 4, "text-anchor": "end" }, tickLabel(t)));
  for (const t of xt) grid.append(svg("text", { x: sx(t), y: plot.bottom + 14, "text-anchor": "middle" }, tickLabel(t)));
  root.append(grid, svg("line", { x1: plot.left, x2: plot.right, y1: plot.bottom, y2: plot.bottom, class: "chart-axis" }));
  // Bubbles: the area follows the size, the largest bubble an eighth of the plot across.
  const sizes = bubbles ? series.flatMap(s => (s.sizes ?? []).filter(v => finite(v) && v > 0)) : [];
  const biggest = highest(sizes, 1e-9), most = Math.min(plot.right - plot.left, plot.bottom - plot.top) / 8;
  series.forEach((s, k) => {
    const group = svg("g", { fill: colourOf(s, k), "fill-opacity": bubbles ? 0.75 : 1 });
    s.values.forEach((v, i) => {
      const xv = s.x?.length ? s.x[i] : i + 1;
      const size = bubbles ? s.sizes?.[i] : null;
      const radius = bubbles ? (finite(size) && size > 0 ? Math.max(2, Math.sqrt(size / biggest) * most) : 0) : 3.5;
      if (finite(v) && finite(xv) && radius > 0) group.append(svg("circle", { cx: sx(xv), cy: sy(v), r: radius }));
    });
    root.append(group);
  });
  const dataLabels = svg("g", { class: "chart-label" });
  series.forEach(s => s.values.forEach((v, i) => {
    const xv = s.x?.length ? s.x[i] : i + 1, text = s.pointLabels?.[i];
    if (text && finite(v) && finite(xv)) dataLabels.append(svg("text", { x: sx(xv) + 6, y: sy(v) - 6 }, text));
  }));
  root.append(dataLabels);
}

// Pie and doughnut charts show their first series, one slice per category.
function drawRound(root, chart, series, area) {
  const values = series.values.map(v => finite(v) && v > 0 ? v : 0), total = values.reduce((a, b) => a + b, 0);
  if (!total) return;
  const cx = (area.left + area.right) / 2, cy = (area.top + area.bottom) / 2, r = Math.max(10, Math.min(area.right - area.left, area.bottom - area.top) / 2 - 4);
  const hole = chart.type === "doughnut" ? r * 0.5 : 0;
  const labels = svg("g", { class: "chart-label" });
  let angle = -Math.PI / 2;
  values.forEach((v, i) => {
    if (!v) return;
    const sweep = v / total * Math.PI * 2, end = angle + sweep, colour = PALETTE[i % PALETTE.length];
    if (sweep >= Math.PI * 2 - 1e-6) {
      root.append(svg("circle", { cx, cy, r, fill: colour }));
      if (hole) root.append(svg("circle", { cx, cy, r: hole, fill: "#ffffff" }));
    } else {
      const large = sweep > Math.PI ? 1 : 0, at = (a, radius) => `${cx + radius * Math.cos(a)},${cy + radius * Math.sin(a)}`;
      const d = hole
        ? `M${at(angle, r)} A${r},${r} 0 ${large} 1 ${at(end, r)} L${at(end, hole)} A${hole},${hole} 0 ${large} 0 ${at(angle, hole)} Z`
        : `M${cx},${cy} L${at(angle, r)} A${r},${r} 0 ${large} 1 ${at(end, r)} Z`;
      root.append(svg("path", { d, fill: colour, stroke: "#ffffff", "stroke-width": 1 }));
    }
    // A slice's label in the middle of the slice (or of the ring).
    const text = series.pointLabels?.[i];
    if (text) {
      const middle = angle + sweep / 2, radius = hole ? (r + hole) / 2 : r * 0.62;
      labels.append(svg("text", { x: cx + radius * Math.cos(middle), y: cy + radius * Math.sin(middle) + 4, "text-anchor": "middle" }, text));
    }
    angle = end;
  });
  root.append(labels);
}

// First row index (into big.rows) under the sticky header, from the rendered geometry (works at any zoom).
function firstVisible() {
  const rowHeight = big.body.rows[0]?.getBoundingClientRect().height || 22 * zoom;
  const view = scroller.getBoundingClientRect();
  const underHeader = view.top + big.head.getBoundingClientRect().height;
  const origin = big.top.getBoundingClientRect().top;                 // where row index 0 would start
  return { first: Math.max(0, Math.floor((underHeader - origin) / rowHeight)), count: Math.ceil(view.height / rowHeight) + 1 };
}

function lowerBound(array, value) {
  let low = 0, high = array.length;
  while (low < high) { const middle = (low + high) >> 1; if (array[middle] < value) low = middle + 1; else high = middle; }
  return low;
}

// Renders the rows around the viewport (or around `center`, an index into big.rows) and fetches missing pages.
function update(force, center) {
  if (!big) return;
  const { rows } = big;
  let first, count;
  if (center === undefined) ({ first, count } = firstVisible());
  else { count = Math.ceil(scroller.clientHeight / 22) + 1; first = Math.max(0, center - (count >> 1)); }
  const covered = first >= big.start && first + count <= big.end
    && (big.start === 0 || first - big.start >= MARGIN / 3) && (big.end === rows.length || big.end - first - count >= MARGIN / 3);
  if (!force && covered) return;
  big.start = Math.max(0, first - MARGIN);
  big.end = Math.min(rows.length, first + count + MARGIN);
  big.top.rows[0].cells[0].style.height = `calc(var(--row) * ${big.start})`;
  big.bottom.rows[0].cells[0].style.height = `calc(var(--row) * ${rows.length - big.end})`;
  const rendered = [];
  if (big.end > big.start) {
    const firstRow = rows[big.start], lastRow = rows[big.end - 1];
    const { spans, covered: hiddenCells } = mergesWithin(big.sheet, firstRow, lastRow, r => lowerBound(rows, r));
    for (let i = big.start; i < big.end; i++) {
      const r = rows[i];
      const tr = row(big.sheet, r, spans, hiddenCells, big.hidden, big.offsets, false);
      tr.setAttribute("aria-rowindex", String(r + 2));
      rendered.push(tr);
      if (r >= big.sheet.rows.length) fetchPage(Math.floor(r / PAGE));
    }
  }
  big.body.replaceChildren(...rendered);
}

// Fetches one page of rows (once); resolves when it is in the cache or has failed.
function fetchPage(page) {
  const state = big;
  if (!state || state.cache.has(page)) return Promise.resolve();
  if (state.pending.has(page)) return state.pending.get(page);
  const start = page * PAGE, count = Math.min(PAGE, state.sheet.rowCount - start);
  const request = fetch(`${DOC}/rows?sheet=${active}&start=${start}&count=${count}`, { cache: "no-store" })
    .then(response => response.ok ? response.json() : Promise.reject(new Error("rows")))
    .then(data => {
      if (big !== state) return;
      state.cache.set(page, data.rows);
      if (state.cache.size > 400) state.cache.delete(state.cache.keys().next().value);
      refreshSoon();
    })
    .catch(() => { })
    .finally(() => state.pending.delete(page));
  state.pending.set(page, request);
  return request;
}

let refreshQueued = false;
function refreshSoon() {
  if (refreshQueued) return;
  refreshQueued = true;
  requestAnimationFrame(() => { refreshQueued = false; update(true); });
}

let scrollQueued = false;
scroller.addEventListener("scroll", () => {
  if (!big || scrollQueued) return;
  scrollQueued = true;
  requestAnimationFrame(() => { scrollQueued = false; update(false); });
});
window.addEventListener("resize", () => { if (big) update(true); else if (workbook?.sheets[active]?.chartSheet) render(workbook.sheets[active]); });

// The app searches large sheets (they are not all in the page) and returns up to 10,000 cells.
async function findLarge(query, previous) {
  if (!bigHits || bigHits.query !== query) {
    const state = big;
    let result = { total: 0, hits: [] };
    if (query) {
      try {
        const response = await fetch(`${DOC}/find?sheet=${active}&q=${encodeURIComponent(query)}`, { cache: "no-store" });
        if (response.ok) result = await response.json();
      } catch { }
    }
    if (big !== state) return;
    bigHits = { query, list: result.hits, total: result.total, set: new Set(result.hits.map(([r, c]) => `${r},${c}`)) };
    hitIndex = previous ? 0 : -1;
  }
  if (bigHits.list.length) {
    hitIndex = (hitIndex + (previous ? -1 : 1) + bigHits.list.length) % bigHits.list.length;
    const [r, c] = bigHits.list[hitIndex];
    const state = big;
    if (r >= big.sheet.rows.length) await fetchPage(Math.floor(r / PAGE));   // the cell must exist to scroll to it
    if (big !== state) return;
    const i = lowerBound(big.rows, r);
    if (big.rows[i] === r) update(true, i); else update(true);
    scroller.querySelector(`tr[data-r="${r}"]`)?.scrollIntoView({ block: "center" });
    cellElement(r, c)?.scrollIntoView({ block: "nearest", inline: "center" });
  } else update(true);
  post({ type: "find", current: bigHits.list.length ? hitIndex + 1 : 0, total: bigHits.total, done: true });
}

function cellElement(r, c) {
  const tr = scroller.querySelector(`tr[data-r="${r}"]`);
  return tr?.querySelector(`td[data-c="${c}"]`) ?? null;
}

function find(query, previous) {
  if (big) { findLarge(query, previous); return; }
  const sheet = workbook.sheets[active];
  if (query !== lastQuery) {
    for (const el of scroller.querySelectorAll("td.hit, td.current")) el.classList.remove("hit", "current");
    const needle = query.toLocaleLowerCase();
    hits = [];
    if (needle) sheet.rows.forEach((values, r) => values.forEach((text, c) => { if (text && text.toLocaleLowerCase().includes(needle)) hits.push([r, c]); }));
    hitIndex = previous ? 0 : -1;
    lastQuery = query;
    for (const [r, c] of hits) cellElement(r, c)?.classList.add("hit");
  }
  if (hits.length) {
    cellElement(...(hits[hitIndex] ?? [-1, -1]))?.classList.remove("current");
    hitIndex = (hitIndex + (previous ? -1 : 1) + hits.length) % hits.length;
    const target = cellElement(...hits[hitIndex]);
    target?.classList.add("current");
    target?.scrollIntoView({ block: "center", inline: "center" });
  }
  post({ type: "find", current: hits.length ? hitIndex + 1 : 0, total: hits.length, done: true });
}

host?.addEventListener("message", event => {
  const m = event.data ?? {};
  switch (m.type) {
    case "find": find(String(m.query ?? ""), !!m.previous); break;
    case "sheet": if (workbook) show((active + (m.delta > 0 ? 1 : -1) + workbook.sheets.length) % workbook.sheets.length); break;
    case "zoom":
      if (m.value === "in") zoom = Math.min(4, Math.round((zoom + 0.1) * 10) / 10);
      else if (m.value === "out") zoom = Math.max(0.5, Math.round((zoom - 0.1) * 10) / 10);
      else if (typeof m.value === "number") zoom = Math.min(4, Math.max(0.5, m.value));
      scroller.style.zoom = String(zoom);
      if (big) update(true);
      state();
      break;
    case "theme": document.documentElement.dataset.theme = m.dark ? "dark" : "light"; break;
    case "focus": scroller.focus(); break;
  }
});

try {
  const response = await fetch(DATA_URL, { cache: "no-store" });
  if (!response.ok) throw new Error("unavailable");
  workbook = await response.json();
  if (!workbook?.sheets?.length) throw new Error("empty");
  post({ type: "loaded", sheets: workbook.sheets.length });
  show(0);
  requestAnimationFrame(() => post({ type: "rendered" }));    // first sheet drawn (used for timing)
} catch {
  post({ type: "error", kind: "unavailable" });
}
