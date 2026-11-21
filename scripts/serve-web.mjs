// Minimal static server for the built site (development and Playwright tests only).
// It serves files with the headers documented in deploy/ (wasm MIME type, no-cache for the entry
// points) so local runs behave like Vercel, GitHub Pages or nginx. Usage: node scripts/serve-web.mjs <dir> [port]
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, join, normalize, resolve, sep } from 'node:path';

const root = resolve(process.argv[2] ?? 'artifacts/web');
const port = Number(process.argv[3] ?? 8080);
const types = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.json': 'application/json', '.wasm': 'application/wasm', '.dll': 'application/octet-stream',
  '.dat': 'application/octet-stream', '.pdb': 'application/octet-stream', '.ico': 'image/x-icon', '.png': 'image/png',
  '.svg': 'image/svg+xml', '.webmanifest': 'application/manifest+json', '.txt': 'text/plain; charset=utf-8', '.ttf': 'font/ttf',
};

const server = createServer(async (request, response) => {
  try {
    const path = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    let file = normalize(join(root, path));
    if (file !== root && !file.startsWith(root + sep)) { response.writeHead(403).end(); return; }
    let info = await stat(file).catch(() => null);
    if (info?.isDirectory()) { file = join(file, 'index.html'); info = await stat(file).catch(() => null); }
    if (!info) {
      // Missing assets are real 404s; only extension-less navigations fall back to the app page.
      if (extname(file)) { response.writeHead(404).end(); return; }
      file = join(root, 'index.html');
    }
    const body = await readFile(file);
    const name = file.slice(root.length).replaceAll('\\', '/');
    const entry = /\/(index\.html|sw\.js|precache\.json|manifest\.webmanifest)$/.test(name) || name === '/404.html';
    response.writeHead(200, {
      'content-type': types[extname(file).toLowerCase()] ?? 'application/octet-stream',
      'cache-control': entry ? 'no-cache' : 'public, max-age=3600',
      'x-content-type-options': 'nosniff',
    });
    response.end(body);
  } catch (error) {
    response.writeHead(500).end(String(error));
  }
});
server.listen(port, '127.0.0.1', () => console.log(`Nexolume web: http://localhost:${port}/ (${root})`));
