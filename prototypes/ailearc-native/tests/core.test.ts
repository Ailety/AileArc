import test from 'node:test';
import assert from 'node:assert/strict';
import { initialModel, update, visibleRows, selectedCount, rowCount, type Model, type Msg } from '../src/core.ts';

const bytes = (text: string) => new TextEncoder().encode(text);
function dispatch(model: Model, msg: Msg): Model { const next = update(model, msg); return Array.isArray(next) ? next[0] : next; }
const range = { start_index: 99980, end_index: 99990, first_visible_index: 99980, last_visible_index: 99989,
  item_extent: 40, item_gap: 0, scroll_offset: 3999200, layout_offset: 0, content_extent: 4000000, before_extent: 3999200, after_extent: 400, anchor_extent: 40 };

test('100k logical rows produce only the requested visible range; selection survives range changes', () => {
  let model = dispatch(initialModel(), { kind: 'sample', count: 100000 });
  assert.equal(rowCount(model), 100000);
  assert.equal(visibleRows(model, range).length, 10);
  model = dispatch(model, { kind: 'toggle', id: 99985 });
  assert.equal(visibleRows(model, range)[5].selected, true);
  model = dispatch(model, { kind: 'select_all' });
  assert.equal(selectedCount(model), 100000);
  model = dispatch(model, { kind: 'toggle', id: 99985 });
  assert.equal(selectedCount(model), 99999);
  model = dispatch(model, { kind: 'clear' });
  assert.equal(selectedCount(model), 0);
});
test('Chinese composition can cancel/commit; search waits until composition ends', () => {
  let model = initialModel();
  model = dispatch(model, { kind: 'edit', edit: { kind: 'set_composition', text: bytes('项目'), cursor: null } });
  assert.notEqual(model.draft.composition, null);
  assert.equal(dispatch(model, { kind: 'search' }), model);
  model = dispatch(model, { kind: 'edit', edit: { kind: 'cancel_composition' } });
  assert.equal(model.draft.text.length, 0);
  model = dispatch(model, { kind: 'edit', edit: { kind: 'insert_text', text: bytes('说明_12') } });
  model = dispatch(model, { kind: 'search' });
  assert.equal(model.total, 11);
  model = dispatch(model, { kind: 'edit', edit: { kind: 'clear' } });
  model = dispatch(model, { kind: 'search' });
  assert.equal(model.total, 1000);
});
test('protocol preserves Unicode and distinct entry ids; page arrival clears old selection', () => {
  const hex = (text: string) => Buffer.from(text).toString('hex').toUpperCase();
  let model = dispatch(initialModel(), { kind: 'toggle', id: 7 });
  model = dispatch(model, { kind: 'page', code: 200, body: bytes(`AILEARC1|2|0|2|${hex('中文.zip')}\n7|0|${hex('配置.yml')}|${hex('配置.yml')}|3234\n8|0|${hex('配置.yml')}|${hex('配置.yml')}|3234\n`) });
  assert.equal(model.entries.length, 2);
  assert.equal(new TextDecoder().decode(model.entries[0].name), '配置.yml');
  assert.notEqual(model.entries[0].id, model.entries[1].id);
  assert.equal(selectedCount(model), 0);
});
test('backend request carries literal UTF-8 URL bytes and keeps token out of URL', () => {
  let model = dispatch(initialModel(), { kind: 'endpoint', value: bytes('http://127.0.0.1:12345/') });
  const result = update(model, { kind: 'token', value: bytes('public-test-token') });
  assert.ok(Array.isArray(result));
  assert.equal(result[1].op, 'fetch');
  if (result[1].op === 'fetch') {
    assert.equal(new TextDecoder().decode(result[1].url), 'http://127.0.0.1:12345/page?offset=0&directory=&search=');
  }
});
