// Service worker: makes the site installable and shows an offline page when the server can't be reached.
// Signed-in pages are never cached (they hold private messages); only public static files are.
const CACHE = 'meshcore-static-v1';
const OFFLINE = '/offline.html';
const PRECACHE = [OFFLINE, '/lib/bootstrap/dist/css/bootstrap.min.css', '/lib/bootstrap-icons/font/bootstrap-icons.min.css', '/img/icon.svg'];

self.addEventListener('install', (event) => {
    event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(PRECACHE)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
            .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (event) => {
    const request = event.request;
    if (request.method !== 'GET') { return; }
    const url = new URL(request.url);
    if (url.origin !== self.location.origin || url.pathname.startsWith('/hubs/')) { return; }

    // Pages: always from the network; offline page if that fails.
    if (request.mode === 'navigate') {
        event.respondWith(fetch(request).catch(() => caches.match(OFFLINE)));
        return;
    }

    // Library files, icons and fonts: cache first (their URLs are versioned or immutable).
    if (url.pathname.startsWith('/lib/') || url.pathname.startsWith('/img/')) {
        event.respondWith(caches.match(request).then((hit) => hit || fetch(request).then((response) => {
            if (response.ok) {
                const copy = response.clone();
                caches.open(CACHE).then((cache) => cache.put(request, copy));
            }
            return response;
        })));
    }
});
