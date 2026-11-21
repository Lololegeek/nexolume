// Exercises the real Rust engine compiled to WebAssembly (office-wasm), outside any browser.
// Run: node --test tests/Web/wasm-engine.test.mjs  (after scripts/build-web.ps1 or build-wasm step)
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const dir = process.env.NEXOLUME_WASM_DIR ?? path.join(root, 'src/rust/target/web-bindgen');
if (!existsSync(path.join(dir, 'office_wasm.js'))) throw new Error(`office-wasm bindings not found in ${dir}; build them first.`);
const engine = await import(pathToFileURL(path.join(dir, 'office_wasm.js')).href);
engine.initSync({ module: readFileSync(path.join(dir, 'office_wasm_bg.wasm')) });

function send(handle, request) {
  const reply = JSON.parse(engine.execute(handle, JSON.stringify({ protocol: 1, params: {}, ...request })));
  return reply;
}
function ok(handle, request) {
  const reply = send(handle, request);
  assert.equal(reply.ok, true, JSON.stringify(reply.error));
  return reply;
}

test('protocol version and engine lifecycle', () => {
  assert.equal(engine.protocolVersion(), 1);
  const before = engine.openEngines();
  const h = engine.create();
  assert.equal(engine.openEngines(), before + 1);
  assert.equal(engine.destroy(h), true);
  assert.equal(engine.destroy(h), false);
  assert.throws(() => engine.execute(h, JSON.stringify({ protocol: 1, command: 'Snapshot', params: {} })), /UnknownEngine/);
});

test('Writer: UTF-8 text, undo/redo, revisions and deltas', () => {
  const h = engine.create();
  const created = ok(h, { command: 'CreateDocument', params: { kind: 'writer', title: 'Notes été 🦀' } });
  const doc = created.snapshot.id;
  assert.equal(created.snapshot.title, 'Notes été 🦀');
  const paragraph = created.snapshot.content.paragraphs[0].id;
  const edited = ok(h, { command: 'SetParagraph', document: doc, expected_revision: created.revision, params: { id: paragraph, text: 'Bonjour 👋 ça va' } });
  assert.ok(edited.revision > created.revision);
  assert.ok(Array.isArray(edited.changes) && edited.changes.length > 0, 'delta expected');
  assert.equal(edited.canUndo, true);
  const stale = send(h, { command: 'SetParagraph', document: doc, expected_revision: created.revision, params: { id: paragraph, text: 'x' } });
  assert.equal(stale.ok, false);
  assert.equal(stale.error.code, 'RevisionMismatch');
  const undone = ok(h, { command: 'Undo', document: doc, expected_revision: edited.revision });
  assert.equal(undone.canRedo, true);
  const redone = ok(h, { command: 'Redo', document: doc, expected_revision: undone.revision });
  const snap = ok(h, { command: 'Snapshot', document: doc, expected_revision: redone.revision });
  assert.equal(snap.snapshot.content.paragraphs[0].text, 'Bonjour 👋 ça va');
  ok(h, { command: 'CloseDocument', document: doc });
  engine.destroy(h);
});

test('Writer: DOCX and ODT export/import roundtrip through WASM', () => {
  const h = engine.create();
  const created = ok(h, { command: 'CreateDocument', params: { kind: 'writer', title: 'Rapport' } });
  const doc = created.snapshot.id;
  const paragraph = created.snapshot.content.paragraphs[0].id;
  const edited = ok(h, { command: 'SetParagraph', document: doc, expected_revision: created.revision, params: { id: paragraph, text: 'Texte exporté — Ünïcödé' } });
  for (const format of ['docx', 'odt']) {
    const exported = ok(h, { command: 'ExportOfficeDocument', document: doc, expected_revision: edited.revision, params: { format } });
    const bytes = Buffer.from(exported.data, 'base64');
    assert.equal(bytes.subarray(0, 2).toString('latin1'), 'PK', `${format} must be a zip container`);
    const imported = ok(h, { command: 'ImportOfficeDocument', params: { format, title: 'Réimport', data: exported.data } });
    assert.equal(imported.snapshot.content.paragraphs[0].text, 'Texte exporté — Ünïcödé');
  }
  engine.destroy(h);
});

test('Sheets: formulas, dependency recalculation, NOW/RAND without SystemTime', () => {
  const h = engine.create();
  const created = ok(h, { command: 'CreateDocument', params: { kind: 'sheets', title: 'Budget' } });
  const doc = created.snapshot.id;
  let revision = created.revision;
  const set = (cell, input) => { const r = ok(h, { command: 'SetCell', document: doc, expected_revision: revision, params: { address: cell, input } }); revision = r.revision; return r; };
  set('A1', '10'); set('A2', '20'); let r = set('A3', '=A1+A2');
  r = ok(h, { command: 'Snapshot', document: doc, expected_revision: revision });
  assert.equal(r.snapshot.content.cells.A3.display, '30');
  set('A1', '15');
  r = ok(h, { command: 'Snapshot', document: doc, expected_revision: revision });
  assert.equal(r.snapshot.content.cells.A3.display, '35');
  set('B1', '=NOW()'); set('B2', '=RAND()'); set('B3', '=RAND()');
  r = ok(h, { command: 'Snapshot', document: doc, expected_revision: revision });
  const now = Number(r.snapshot.content.cells.B1.value ?? r.snapshot.content.cells.B1.display.replace(',', '.'));
  assert.ok(now > 45000, `NOW() should use the browser clock, got ${JSON.stringify(r.snapshot.content.cells.B1)}`);
  assert.notEqual(r.snapshot.content.cells.B2.display, r.snapshot.content.cells.B3.display, 'RAND() values must differ');
  engine.destroy(h);
});

test('Slides: add slide, undo', () => {
  const h = engine.create();
  const created = ok(h, { command: 'CreateDocument', params: { kind: 'slides', title: 'Présentation' } });
  const doc = created.snapshot.id;
  const count = created.snapshot.content.slides.length;
  const added = ok(h, { command: 'AddSlide', document: doc, expected_revision: created.revision, params: { id: 'slide-wasm-1' } });
  const snap = ok(h, { command: 'Snapshot', document: doc, expected_revision: added.revision });
  assert.equal(snap.snapshot.content.slides.length, count + 1);
  const undone = ok(h, { command: 'Undo', document: doc, expected_revision: added.revision });
  const after = ok(h, { command: 'Snapshot', document: doc, expected_revision: undone.revision });
  assert.equal(after.snapshot.content.slides.length, count);
  engine.destroy(h);
});

test('errors are reported, never faked', () => {
  const h = engine.create();
  assert.throws(() => engine.execute(h, 'not json'), /InvalidRequest/);
  const reply = send(h, { command: 'Snapshot', document: 'missing' });
  assert.equal(reply.ok, false);
  assert.ok(reply.error.code.length > 0);
  const bad = send(h, { command: 'ImportOfficeDocument', params: { format: 'docx', title: 'x', data: Buffer.from('not a zip').toString('base64') } });
  assert.equal(bad.ok, false);
  assert.equal(engine.lastPanic(), undefined);
  engine.destroy(h);
});
