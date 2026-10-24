// Offline support. Online: always loads the newest files from GitHub. Offline: uses the saved copy.
// Raise VERSION (and APP_VERSION in index.html) after editing any file.
const VERSION = 'ceiling-light-1.4';
const FILES = ['./', 'index.html', 'protocol.js', 'manifest.webmanifest', 'icon-192.png', 'icon-512.png'];
self.addEventListener('install', (e) => { e.waitUntil(caches.open(VERSION).then((c) => c.addAll(FILES.map((f) => new Request(f, { cache: 'reload' }))))); self.skipWaiting(); });
self.addEventListener('activate', (e) => {
  e.waitUntil(caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== VERSION).map((k) => caches.delete(k)))).then(() => self.clients.claim()));
});
self.addEventListener('fetch', (e) => {
  if (e.request.method !== 'GET' || new URL(e.request.url).origin !== location.origin) return;
  // Network first (3 s limit), cached copy as fallback.
  e.respondWith((async () => {
    const cache = await caches.open(VERSION);
    try {
      const net = await Promise.race([fetch(e.request, { cache: 'no-cache' }), new Promise((_, rej) => setTimeout(() => rej(new Error('slow')), 3000))]);
      if (net.ok) cache.put(e.request, net.clone());
      return net;
    } catch {
      return (await cache.match(e.request, { ignoreSearch: true })) || fetch(e.request);
    }
  })());
});
