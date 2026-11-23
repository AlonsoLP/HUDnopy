// HUDnopy PWA Service Worker — caches static shell assets while always bypassing live /ws and /api/* traffic.
const CACHE_NAME = 'hudnopy-shell-v0.5.0';
const SHELL_ASSETS = ['/', '/styles.css', '/app.js', '/manifest.json', '/icon.svg'];

self.addEventListener('install', event => {
  event.waitUntil(
    caches.open(CACHE_NAME).then(cache => cache.addAll(SHELL_ASSETS)).catch(() => {})
  );
  self.skipWaiting();
});

self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys().then(keys => Promise.all(
      keys.filter(k => k !== CACHE_NAME).map(k => caches.delete(k))
    ))
  );
  self.clients.claim();
});

self.addEventListener('fetch', event => {
  const req = event.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/ws') || url.pathname === '/login') {
    return;
  }
  event.respondWith(
    fetch(req)
      .then(resp => {
        if (resp && resp.ok && url.origin === self.location.origin) {
          const clone = resp.clone();
          caches.open(CACHE_NAME).then(cache => cache.put(req, clone)).catch(() => {});
        }
        return resp;
      })
      .catch(() => caches.match(req))
  );
});
