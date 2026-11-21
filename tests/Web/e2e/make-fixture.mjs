// Produces test files with the published WebAssembly engine, outside the page under test.
// Runs as a plain Node process (Playwright's test loader cannot import the wasm-bindgen ES module).
// Usage: node make-fixture.mjs <engineDir> <docx|ossheet> -> base64 on stdout.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const [engineDir, kind] = process.argv.slice(2);
const engine = await import(pathToFileURL(join(engineDir, 'office_wasm.js')).href);
engine.initSync({ module: readFileSync(join(engineDir, 'office_wasm_bg.wasm')) });
const h = engine.create();
const call = (request) => {
  const reply = JSON.parse(engine.execute(h, JSON.stringify({ protocol: 1, params: {}, ...request })));
  if (!reply.ok) throw new Error(JSON.stringify(reply.error));
  return reply;
};

let output;
if (kind === 'docx') {
  const created = call({ command: 'CreateDocument', params: { kind: 'writer', title: 'Rapport' } });
  const doc = created.snapshot.id;
  const edited = call({ command: 'SetParagraph', document: doc, expected_revision: created.revision, params: { id: created.snapshot.content.paragraphs[0].id, text: 'Rapport Word — Ünïcödé' } });
  output = Buffer.from(call({ command: 'ExportOfficeDocument', document: doc, expected_revision: edited.revision, params: { format: 'docx' } }).data, 'base64');
} else if (kind === 'ossheet') {
  let reply = call({ command: 'CreateDocument', params: { kind: 'sheets', title: 'Budget' } });
  const doc = reply.snapshot.id;
  for (const [address, input] of [['A1', '2'], ['A2', '3'], ['A3', '=SUM(A1:A2)']]) {
    reply = call({ command: 'SetCell', document: doc, expected_revision: reply.revision, params: { address, input } });
  }
  output = Buffer.from(call({ command: 'SaveDocument', document: doc, expected_revision: reply.revision }).json, 'utf8');
} else {
  throw new Error(`unknown fixture ${kind}`);
}
engine.destroy(h);
process.stdout.write(output.toString('base64'));
