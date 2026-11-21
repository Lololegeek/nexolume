// Offline cache of the published app. scripts/build-web.ps1 writes precache.json (every published
// file with its SHA-256) at publish time; its hash names the cache, so a new release replaces the
// old files atomically. In development there is no precache.json and this worker only passes
// requests through. It never contacts any other origin.
const PREFIX = 'nexolume-';

async function manifest() {
  const response = await fetch('./precache.json', { cache: 'no-store' });
  if (!response.ok) return null;
  return response.json();
}

self.addEventListener('install', (event) => {
  event.waitUntil((async () => {
    const list = await manifest().catch(() => null);
    if (!list) return;
    const cache = await caches.open(PREFIX + list.version);
    // Hash-checked fetches: an interrupted or mixed deployment must not be cached as a release.
    await cache.addAll(list.files.map((file) => new Request(file.url, { cache: 'reload', integrity: file.integrity })));
    await cache.put('./precache.json', new Response(JSON.stringify(list), { headers: { 'content-type': 'application/json' } }));
    await self.skipWaiting();
  })());
});

self.addEventListener('activate', (event) => {
  event.waitUntil((async () => {
    const list = await manifest().catch(() => null);
    if (list) {
      for (const key of await caches.keys()) if (key.startsWith(PREFIX) && key !== PREFIX + list.version) await caches.delete(key);
    }
    await self.clients.claim();
  })());
});

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.method !== 'GET' || new URL(request.url).origin !== self.location.origin) return;
  event.respondWith((async () => {
    const cached = await caches.match(request, { ignoreSearch: true });
    if (cached) return cached;
    try {
      return await fetch(request);
    } catch (error) {
      // Offline navigation (or a file handler launch) falls back to the cached page.
      if (request.mode === 'navigate') {
        const page = await caches.match('./index.html') ?? await caches.match('./');
        if (page) return page;
      }
      throw error;
    }
  })());
});
