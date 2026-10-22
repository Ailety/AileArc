import { Cmd, utf8Bytes, type EnvMsg } from "@native-sdk/core";
import { applyTextInputEvent, containsIgnoreCase, type TextEditState, type TextInputEvent } from "@native-sdk/core/text";
import type { KeyEvent, ThemeState, VirtualListRange } from "@native-sdk/core/events";
import type { ThemeColor, ThemeDesignTokenOverrides } from "@native-sdk/core/theme";

export interface Entry { readonly id: number; readonly identity: Uint8Array; readonly name: Uint8Array; readonly path: Uint8Array; readonly size: Uint8Array; readonly folder: boolean; }
export interface Row { readonly id: number; readonly identity: Uint8Array; readonly name: Uint8Array; readonly path: Uint8Array; readonly size: Uint8Array; readonly selected: boolean; readonly focused: boolean; readonly folder: boolean; }
export interface Labels {
  readonly title: Uint8Array; readonly subtitle: Uint8Array; readonly root: Uint8Array; readonly search: Uint8Array;
  readonly find: Uint8Array; readonly name: Uint8Array; readonly size: Uint8Array; readonly details: Uint8Array;
  readonly select: Uint8Array; readonly clear: Uint8Array; readonly previous: Uint8Array; readonly next: Uint8Array;
  readonly enter: Uint8Array; readonly up: Uint8Array; readonly sample: Uint8Array; readonly real: Uint8Array;
  readonly readonly: Uint8Array; readonly empty: Uint8Array;
}
export interface Model {
  readonly labels: Labels; readonly draft: TextEditState; readonly count: number; readonly matches: readonly number[];
  readonly entries: readonly Entry[]; readonly selected: readonly number[]; readonly allSelected: boolean;
  readonly focused: number; readonly endpoint: Uint8Array; readonly token: Uint8Array; readonly directory: Uint8Array;
  readonly archiveName: Uint8Array; readonly offset: number; readonly total: number; readonly archiveCount: number;
  readonly busy: boolean; readonly real: boolean; readonly status: Uint8Array; readonly needQuery: boolean; readonly selectionEpoch: boolean;
}
export type Msg =
  | { readonly kind: "edit"; readonly edit: TextInputEvent }
  | { readonly kind: "search" } | { readonly kind: "sample"; readonly count: number }
  | { readonly kind: "real" } | { readonly kind: "focus"; readonly id: number }
  | { readonly kind: "toggle"; readonly id: number } | { readonly kind: "select_all" }
  | { readonly kind: "clear" } | { readonly kind: "enter" } | { readonly kind: "up" }
  | { readonly kind: "root" } | { readonly kind: "next" } | { readonly kind: "previous" }
  | { readonly kind: "move_down" } | { readonly kind: "move_up" }
  | { readonly kind: "extend_down" } | { readonly kind: "extend_up" }
  | { readonly kind: "endpoint"; readonly value: Uint8Array } | { readonly kind: "token"; readonly value: Uint8Array }
  | { readonly kind: "page"; readonly code: number; readonly body: Uint8Array }
  | { readonly kind: "failed"; readonly reason: Uint8Array };
export const envMsgs = [{ env: "AILEARC_ENDPOINT", msg: "endpoint" }, { env: "AILEARC_TOKEN", msg: "token" }] as const satisfies readonly EnvMsg<Msg>[];
export const viewUnbound = ["endpoint", "token", "page", "failed", "move_down", "move_up", "extend_down", "extend_up", "matches", "entries", "selected", "focused", "count", "archiveCount", "needQuery"] as const;
function editor(): TextEditState { return { text: utf8Bytes(""), selection: { anchor: 0, focus: 0 }, composition: null }; }
function indices(count: number): readonly number[] { const result: number[] = []; for (let i = 0; i < count && i < 100000; i += 1) result.push(i); return result; }
export function initialModel(): Model {
  return {
    labels: { title: utf8Bytes("AileArc"), subtitle: utf8Bytes("暖石墨 · Native SDK 验证"), root: utf8Bytes("归档根目录"),
      search: utf8Bytes("搜索文件名，支持中文输入"), find: utf8Bytes("搜索"), name: utf8Bytes("文件名称"), size: utf8Bytes("大小 / KiB"),
      details: utf8Bytes("所选项目"), select: utf8Bytes("全选当前结果"), clear: utf8Bytes("清除选择"), previous: utf8Bytes("上一页"),
      next: utf8Bytes("下一页"), enter: utf8Bytes("进入目录"), up: utf8Bytes("返回上级"), sample: utf8Bytes("界面压力样本"),
      real: utf8Bytes("真实归档"), readonly: utf8Bytes("只读验证 · 文件保持不变"), empty: utf8Bytes("没有匹配的文件") },
    draft: editor(), count: 1000, matches: indices(1000), entries: [], selected: [], allSelected: false, focused: -1,
    endpoint: utf8Bytes(""), token: utf8Bytes(""), directory: utf8Bytes(""), archiveName: utf8Bytes("项目资料.zip"),
    offset: 0, total: 1000, archiveCount: 1000, busy: false, real: false, needQuery: false, selectionEpoch: false,
    status: utf8Bytes("生成样本 · 仅构建可见行；真实归档需通过启动脚本加载") };
}
function sampleName(id: number): Uint8Array { return utf8Bytes(`项目说明_${id}.md`); }
export function smallCount(model: Model): number { return 1000; }
export function largeCount(model: Model): number { return 100000; }
// The 0.11.0 fixed-extent adapter emits unused Zig locals without an extent
// helper. Use the supported estimator path with the same uniform row height.
export function rowExtent(model: Model, index: number): number { return 40; }
function isSelected(model: Model, id: number): boolean { return model.allSelected !== model.selected.includes(id); }
function nextSelectionEpoch(model: Model): boolean { return !model.selectionEpoch; }
export function selectionKey(model: Model): Uint8Array { return model.selectionEpoch ? utf8Bytes("selection-a") : utf8Bytes("selection-b"); }
export function rowCount(model: Model): number { return model.real ? model.entries.length : model.matches.length; }
export function selectedCount(model: Model): number { return model.allSelected ? rowCount(model) - model.selected.length : model.selected.length; }
export function visibleRows(model: Model, range: VirtualListRange): readonly Row[] {
  const first = range.start_index >= 0 && range.start_index <= 100000 ? Math.trunc(range.start_index) : 0;
  const last = range.end_index >= first && range.end_index <= 100000 ? Math.trunc(range.end_index) : first;
  const rows: Row[] = [];
  for (let i = first; i < last && i < rowCount(model); i += 1) {
    if (model.real) {
      const entry = model.entries[i];
      const id = entry.id >= -500000 && entry.id <= 500000 ? Math.trunc(entry.id) : 0;
      rows.push({ id, identity: entry.identity, name: entry.name, path: entry.path, size: entry.size, selected: isSelected(model, id),
        focused: id === model.focused, folder: entry.folder });
    } else {
      const value = model.matches[i];
      const id = value >= 0 && value < 100000 ? Math.trunc(value) : 0;
      rows.push({ id, identity: utf8Bytes(`sample_${id}`), name: sampleName(id), path: utf8Bytes(`文档/项目说明_${id}.md`), size: utf8Bytes("24"),
        selected: isSelected(model, id), focused: id === model.focused, folder: false });
    }
  }
  return rows;
}
export function focusedName(model: Model): Uint8Array {
  if (model.real) { const row = model.entries.find(entry => entry.id === model.focused); return row === undefined ? utf8Bytes("请选择一个项目") : row.name; }
  return model.focused < 0 ? utf8Bytes("请选择一个项目") : sampleName(model.focused);
}
export function focusedPath(model: Model): Uint8Array {
  if (model.real) { const row = model.entries.find(entry => entry.id === model.focused); return row === undefined ? utf8Bytes("复选框可独立切换选择") : row.path; }
  return utf8Bytes("生成的界面压力数据，不是归档读取性能");
}
export function location(model: Model): Uint8Array { return model.directory.length === 0 ? utf8Bytes("根目录") : model.directory; }
export function counter(model: Model): Uint8Array { return utf8Bytes(`当前 ${rowCount(model)} 项 · 已选 ${selectedCount(model)} 项`); }
export function summary(model: Model): Uint8Array { return utf8Bytes(`索引 ${model.archiveCount} 项 · 结果 ${model.total} 项 · 起点 ${model.offset}`); }
export function hasNext(model: Model): boolean { return model.real && model.offset + model.entries.length < model.total && !model.busy; }
export function hasPrevious(model: Model): boolean { return model.real && model.offset > 0 && !model.busy; }
export function canEnter(model: Model): boolean { return model.real && model.entries.some(entry => entry.id === model.focused && entry.folder) && !model.busy; }
function hex(text: Uint8Array): Uint8Array {
  const digits = utf8Bytes("0123456789ABCDEF"), result: number[] = [];
  for (let i = 0; i < text.length; i += 1) { result.push(digits[text[i] >> 4]); result.push(digits[text[i] & 15]); }
  return new Uint8Array(result);
}
function concatBytes(parts: readonly Uint8Array[]): Uint8Array {
  let length = 0;
  for (const part of parts) length += part.length;
  const result = new Uint8Array(length);
  let offset = 0;
  for (const part of parts) { result.set(part, offset); offset += part.length; }
  return result;
}
function digit(value: number): number { return value >= 48 && value <= 57 ? value - 48 : value >= 65 && value <= 70 ? value - 55 : -1; }
function unhex(text: Uint8Array): Uint8Array {
  if (text.length % 2 !== 0) return utf8Bytes(""); const result: number[] = [];
  for (let i = 0; i < text.length; i += 2) { const a = digit(text[i]), b = digit(text[i + 1]); if (a < 0 || b < 0) return utf8Bytes(""); result.push(a * 16 + b); }
  return new Uint8Array(result);
}
function decimal(text: Uint8Array): number {
  let value = 0, start = 0, sign = 1; if (text.length > 0 && text[0] === 45) { start = 1; sign = -1; }
  for (let i = start; i < text.length; i += 1) { if (text[i] < 48 || text[i] > 57 || value > 100000000) return 0; value = value * 10 + text[i] - 48; }
  return sign * value;
}
function request(model: Model): Model {
  if (model.endpoint.length === 0 || model.token.length === 0) return { ...model, status: utf8Bytes("请通过 Start-NativeValidation.ps1 指定压缩包") };
  return { ...model, busy: true, real: true, needQuery: true, status: utf8Bytes("正在查询现有 Core 索引…") };
}
interface Field { readonly bytes: Uint8Array; }
function splitBytes(text: Uint8Array, separator: number): readonly Field[] {
  const result: Field[] = []; let start = 0;
  for (let i = 0; i <= text.length; i += 1) {
    if (i === text.length || text[i] === separator) { result.push({ bytes: text.slice(start, i) }); start = i + 1; }
  }
  return result;
}
function decodePage(model: Model, body: Uint8Array): Model {
  const lines = splitBytes(body, 10), header = splitBytes(lines[0].bytes, 124);
  if (header.length !== 5 || header[0].bytes.length !== 8 || !containsIgnoreCase(header[0].bytes, utf8Bytes("AILEARC1"))) return { ...model, busy: false, status: utf8Bytes("归档响应格式无效") };
  const entries: Entry[] = [];
  for (let i = 1; i < lines.length && entries.length < 200; i += 1) {
    if (lines[i].bytes.length === 0) continue; const cells = splitBytes(lines[i].bytes, 124);
    if (cells.length !== 5) return { ...model, busy: false, status: utf8Bytes("归档响应被截断") };
    entries.push({ id: decimal(cells[0].bytes), identity: cells[0].bytes, folder: decimal(cells[1].bytes) === 1, name: unhex(cells[2].bytes), path: unhex(cells[3].bytes), size: unhex(cells[4].bytes) });
  }
  return { ...model, entries, total: decimal(header[1].bytes), offset: decimal(header[2].bytes), archiveCount: decimal(header[3].bytes), archiveName: unhex(header[4].bytes),
    selected: [], allSelected: false, focused: -1, busy: false, selectionEpoch: nextSelectionEpoch(model), status: utf8Bytes("现有 Core / Worker · 每页最多 200 项 · 页面切换清除选择") };
}
function transition(model: Model, msg: Msg): Model {
  switch (msg.kind) {
    case "endpoint": return { ...model, endpoint: msg.value };
    case "token": return request({ ...model, token: msg.value, offset: 0 });
    case "edit": { const draft = applyTextInputEvent(model.draft, msg.edit, 512); return draft === null ? model : { ...model, draft }; }
    case "search": {
      if (model.busy || model.draft.composition !== null) return model;
      if (model.real) return request({ ...model, offset: 0 });
      const matches = indices(model.count).filter(id => containsIgnoreCase(sampleName(id), model.draft.text));
      return { ...model, matches, total: matches.length, selected: [], allSelected: false, focused: -1, selectionEpoch: nextSelectionEpoch(model) };
    }
    case "sample": {
      if (model.busy) return model; const count = msg.count === 100000 ? 100000 : 1000;
      return { ...model, real: false, count, total: count, archiveCount: count, matches: indices(count), selected: [], allSelected: false,
        focused: -1, offset: 0, draft: editor(), directory: utf8Bytes(""), archiveName: utf8Bytes("项目资料.zip"), selectionEpoch: nextSelectionEpoch(model),
        status: utf8Bytes("生成样本 · 大列表只创建可见行，不代表真实归档扫描速度") };
    }
    case "real": return model.busy ? model : request({ ...model, offset: 0, draft: editor(), directory: utf8Bytes("") });
    case "focus": return { ...model, focused: msg.id };
    case "toggle": return { ...model, focused: msg.id, selected: model.selected.includes(msg.id) ? model.selected.filter(id => id !== msg.id) : [...model.selected, msg.id] };
    case "select_all": return { ...model, allSelected: !model.allSelected, selected: [], selectionEpoch: nextSelectionEpoch(model) };
    case "clear": return { ...model, allSelected: false, selected: [], selectionEpoch: nextSelectionEpoch(model) };
    case "enter": {
      if (model.busy) return model; const entry = model.entries.find(row => row.id === model.focused);
      return entry === undefined || !entry.folder ? model : request({ ...model, directory: entry.path, offset: 0, draft: editor() });
    }
    case "up": {
      if (model.busy || !model.real) return model;
      let slash = -1;
      for (let i = 0; i < model.directory.length; i += 1) if (model.directory[i] === 47) slash = i;
      return request({ ...model, directory: slash < 0 ? utf8Bytes("") : model.directory.slice(0, slash), offset: 0, draft: editor() });
    }
    case "root": return model.busy || !model.real ? model : request({ ...model, directory: utf8Bytes(""), offset: 0, draft: editor() });
    case "next": return hasNext(model) ? request({ ...model, offset: model.offset + model.entries.length }) : model;
    case "previous": return hasPrevious(model) ? request({ ...model, offset: Math.max(0, model.offset - 200) }) : model;
    case "page": return msg.code === 200 ? decodePage(model, msg.body) : { ...model, busy: false, status: utf8Bytes("后端拒绝查询或请求超出验证边界") };
    case "failed": return { ...model, busy: false, status: concatBytes([utf8Bytes("只读后端连接失败："), msg.reason]) };
    case "move_down": case "move_up": case "extend_down": case "extend_up": {
      const ids = model.real ? model.entries.map(row => row.id) : model.matches;
      const delta = msg.kind === "move_down" || msg.kind === "extend_down" ? 1 : -1;
      const index = Math.max(0, Math.min(ids.length - 1, ids.indexOf(model.focused) + delta));
      if (ids.length === 0) return model; const id = ids[index];
      if (msg.kind === "move_down" || msg.kind === "move_up") return { ...model, focused: id };
      return { ...model, focused: id, selectionEpoch: nextSelectionEpoch(model), selected: isSelected(model, id) ? model.selected : model.allSelected ? model.selected.filter(value => value !== id) : [...model.selected, id] };
    }
  }
}
export function update(model: Model, msg: Msg): Model | [Model, Cmd<Msg>] {
  const next = transition(model, msg);
  if (!next.needQuery) return next;
  const url = concatBytes([next.endpoint, utf8Bytes(`page?offset=${next.offset}&directory=`), hex(next.directory), utf8Bytes("&search="), hex(next.draft.text)]);
  return [{ ...next, needQuery: false }, Cmd.fetch({ url, method: "GET", headers: { "X-AileArc-Token": next.token }, timeoutMs: 30000 },
    { key: "page", ok: "page", err: "failed" })];
}
export function keyMsg(key: KeyEvent): Msg | null {
  if (key.control && key.key === "a") return { kind: "select_all" };
  if (key.key === "escape") return { kind: "clear" };
  if (key.key === "arrowdown") return key.shift ? { kind: "extend_down" } : { kind: "move_down" };
  if (key.key === "arrowup") return key.shift ? { kind: "extend_up" } : { kind: "move_up" };
  return null;
}
export function themeState(model: Model): ThemeState { return { pack: "geist", colorScheme: "dark", accent: "#c6ac86" }; }
function rgb(r: number, g: number, b: number): ThemeColor { return { r: r / 255, g: g / 255, b: b / 255, a: 1 }; }
export function tokenOverrides(model: Model): ThemeDesignTokenOverrides {
  return { colors: { background: rgb(30,29,28), surface: rgb(37,35,33), surface_subtle: rgb(44,41,38), text: rgb(232,227,220),
    text_muted: rgb(163,155,145), border: rgb(64,59,53), accent: rgb(198,172,134), accent_text: rgb(30,29,28), focus_ring: rgb(198,172,134) },
    typography: { font_id: 64, button_font_id: 64, body_size: 14, label_size: 12, button_size: 13 },
    radius: { sm: 4, md: 5, lg: 7 }, metrics: { row_extent: 38 }, density: "regular" };
}
