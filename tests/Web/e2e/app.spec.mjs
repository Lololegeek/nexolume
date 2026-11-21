// End-to-end tests of the published browser build (artifacts/web): the real .NET WebAssembly
// UI drawn by Avalonia on a canvas, driving the real Rust engine compiled to WebAssembly.
// The canvas has no DOM, so tests act with the keyboard/mouse like a user and read the result
// through the read-only diagnostics hook (globalThis.nexolume.state()).
import { test, expect } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const site = process.env.NEXOLUME_WEB_DIR ?? join(repo, 'artifacts/web');
const engineDir = join(site, 'engine');

/** A real file produced by the same WebAssembly engine in a separate Node process (make-fixture.mjs). */
function fixture(kind) {
  const run = spawnSync(process.execPath, [join(dirname(fileURLToPath(import.meta.url)), 'make-fixture.mjs'), engineDir, kind], { encoding: 'utf8' });
  if (run.status !== 0) throw new Error(run.stderr);
  return Buffer.from(run.stdout, 'base64');
}

/** Page errors and console errors are collected so every test can assert none happened. */
function watch(page) {
  const problems = [];
  page.on('pageerror', (error) => problems.push(`pageerror: ${error.message}`));
  page.on('console', (message) => {
    if (message.type() !== 'error') return;
    const text = message.text();
    // The service worker may be refused by some test browsers (e.g. no SW in a private context).
    if (/ServiceWorker|sw\.js/.test(text)) return;
    problems.push(`console: ${text}`);
  });
  return problems;
}

async function boot(page, { forceDownload = true } = {}) {
  if (forceDownload) {
    // Exercise the universal fallback (download) instead of the native save picker, which a
    // headless browser cannot answer.
    await page.addInitScript(() => { delete window.showSaveFilePicker; delete window.showOpenFilePicker; });
  }
  await page.goto('./');
  await expect(page.locator('html')).toHaveAttribute('data-ready', 'true', { timeout: 90_000 });
  await expect(page.locator('#splash')).toHaveCount(0);
  await page.waitForFunction(() => globalThis.nexolume?.state().ready === true);
  // Keys reach Avalonia once its canvas container has the page focus (a click on the status bar).
  await page.mouse.click(700, 886);
}

const state = (page) => page.evaluate(() => globalThis.nexolume.state());
async function waitFor(page, predicate, description) {
  await expect.poll(async () => predicate(await state(page)), { message: description, timeout: 30_000 }).toBeTruthy();
  return state(page);
}

test('starts without errors and shows the home page', async ({ page }) => {
  const problems = watch(page);
  await boot(page);
  const s = await state(page);
  expect(s.documents).toEqual([]);
  expect(s.dialogOpen).toBe(false);
  await expect(page).toHaveTitle(/Nexolume/);
  expect(problems).toEqual([]);
});

test('Writer: create, type UTF-8 text, undo/redo, save (download) and reopen', async ({ page }) => {
  const problems = watch(page);
  await boot(page);
  await page.keyboard.press('Control+N');
  let s = await waitFor(page, (x) => x.documents.length === 1 && x.documents[0].kind === 'writer', 'writer document created');
  const created = s.documents[0].revision;

  await page.keyboard.type('Bonjour été 2026 — ça marche');
  s = await waitFor(page, (x) => JSON.stringify(x.documents[0].content).includes('Bonjour été 2026 — ça marche'), 'typed text reaches the engine');
  expect(s.documents[0].dirty).toBe(true);
  expect(s.documents[0].revision).toBeGreaterThan(created);
  // Unsaved work is announced in the tab title (beforeunload guard is armed with it).
  await expect(page).toHaveTitle(/^• /);

  await page.keyboard.press('Control+Z');
  s = await waitFor(page, (x) => !JSON.stringify(x.documents[0].content).includes('ça marche'), 'undo removes the text');
  await page.keyboard.press('Control+Y');
  s = await waitFor(page, (x) => JSON.stringify(x.documents[0].content).includes('ça marche'), 'redo restores the text');

  const download = page.waitForEvent('download');
  await page.keyboard.press('Control+S');
  const file = await download;
  expect(file.suggestedFilename()).toMatch(/\.oswriter$/);
  const saved = await file.path();
  const json = readFileSync(saved, 'utf8');
  expect(json).toContain('Bonjour été 2026 — ça marche');
  s = await waitFor(page, (x) => x.documents[0].dirty === false, 'document marked saved');
  await expect(page).toHaveTitle(/^Nexolume/);

  // Reopen the downloaded file through the browser file picker fallback.
  const chooser = page.waitForEvent('filechooser');
  await page.keyboard.press('Control+O');
  await (await chooser).setFiles({ name: 'reouvert.oswriter', mimeType: 'application/json', buffer: Buffer.from(json, 'utf8') });
  s = await waitFor(page, (x) => x.documents.length === 2, 'second document opened');
  expect(JSON.stringify(s.documents[1].content)).toContain('Bonjour été 2026 — ça marche');
  expect(s.documents[1].dirty).toBe(false);
  expect(problems).toEqual([]);
});

test('Writer: opens a .docx produced by the engine and keeps its text', async ({ page }) => {
  const problems = watch(page);
  test.skip(!existsSync(join(engineDir, 'office_wasm.js')), 'site not built');
  const docx = fixture('docx');

  await boot(page);
  const chooser = page.waitForEvent('filechooser');
  await page.keyboard.press('Control+O');
  await (await chooser).setFiles({ name: 'rapport.docx', mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', buffer: docx });
  const s = await waitFor(page, (x) => x.documents.length === 1, 'docx opened');
  expect(s.documents[0].kind).toBe('writer');
  expect(JSON.stringify(s.documents[0].content)).toContain('Rapport Word — Ünïcödé');

  // Saving a document opened from .docx downloads a .docx again (same format, no silent conversion).
  const download = page.waitForEvent('download');
  await page.keyboard.press('Control+S');
  const file = await download;
  expect(file.suggestedFilename()).toBe('rapport.docx');
  expect(readFileSync(await file.path()).subarray(0, 2).toString('latin1')).toBe('PK');
  expect(problems).toEqual([]);
});

test('Sheets: opens a workbook, edits a cell and recalculates the formula in the engine', async ({ page }) => {
  const problems = watch(page);
  test.skip(!existsSync(join(engineDir, 'office_wasm.js')), 'site not built');
  await boot(page);
  const chooser = page.waitForEvent('filechooser');
  await page.keyboard.press('Control+O');
  await (await chooser).setFiles({ name: 'budget.ossheet', mimeType: 'application/json', buffer: fixture('ossheet') });
  let s = await waitFor(page, (x) => x.documents.length === 1 && x.documents[0].kind === 'sheets', 'workbook opened');
  expect(s.documents[0].content.cells.A3.display).toBe('5');
  // A1 is selected when a workbook opens: typing starts an in-cell edit, Enter commits it.
  await page.keyboard.type('40');
  await page.keyboard.press('Enter');
  s = await waitFor(page, (x) => x.documents[0].content.cells.A3?.display === '43', 'dependent formula recalculated');
  expect(s.documents[0].content.cells.A1.input).toBe('40');
  await page.keyboard.press('Control+Z');
  await waitFor(page, (x) => x.documents[0].content.cells.A3?.display === '5', 'undo restores the result');
  expect(problems).toEqual([]);
});

test('warns before leaving with unsaved changes', async ({ page }) => {
  await boot(page);
  await page.keyboard.press('Control+N');
  await waitFor(page, (x) => x.documents.length === 1, 'document created');
  await page.keyboard.type('à garder');
  await waitFor(page, (x) => x.documents[0].dirty, 'dirty');
  let asked = false;
  page.on('dialog', async (dialog) => { asked = dialog.type() === 'beforeunload'; await dialog.dismiss(); });
  await page.close({ runBeforeUnload: true });
  await expect.poll(() => asked).toBe(true);
});

test('works offline after the first visit (service worker cache)', async ({ page, context, browserName }) => {
  test.skip(browserName !== 'chromium', 'offline cache checked on Chromium');
  await boot(page);
  await page.evaluate(async () => { await navigator.serviceWorker.ready; });
  await expect.poll(() => page.evaluate(async () => (await caches.keys()).some((k) => k.startsWith('nexolume-'))), { timeout: 60_000 }).toBe(true);
  // The worker must control the page before going offline.
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-ready', 'true', { timeout: 90_000 });
  await context.setOffline(true);
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-ready', 'true', { timeout: 90_000 });
  await page.mouse.click(700, 886);
  await page.keyboard.press('Control+N');
  await waitFor(page, (x) => x.documents.length === 1, 'editing works offline');
  await context.setOffline(false);
});

test('sends no request to another origin (no telemetry)', async ({ page }) => {
  const foreign = [];
  page.on('request', (request) => { if (!request.url().startsWith('http://localhost')) foreign.push(request.url()); });
  await boot(page);
  await page.keyboard.press('Control+N');
  await waitFor(page, (x) => x.documents.length === 1, 'document created');
  expect(foreign.filter((url) => !url.startsWith('data:') && !url.startsWith('blob:'))).toEqual([]);
});
