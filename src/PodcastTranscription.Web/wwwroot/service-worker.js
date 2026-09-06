const CACHE_PREFIX = 'podcast-transcription-shell-';
const CACHE_NAME = `${CACHE_PREFIX}v1`;
const OFFLINE_URL = new URL('offline.html', self.registration.scope).href;
const SHELL_ASSETS = [
    OFFLINE_URL,
    new URL('favicon.png', self.registration.scope).href,
    new URL('icons/icon-192.png', self.registration.scope).href,
    new URL('icons/icon-512.png', self.registration.scope).href
];

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then((cache) => cache.addAll(SHELL_ASSETS))
            .then(() => self.skipWaiting())
    );
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys()
            .then((names) => Promise.all(
                names
                    .filter((name) => name.startsWith(CACHE_PREFIX) && name !== CACHE_NAME)
                    .map((name) => caches.delete(name))
            ))
            .then(() => self.clients.claim())
    );
});

self.addEventListener('fetch', (event) => {
    const request = event.request;

    if (request.method !== 'GET' || new URL(request.url).origin !== self.location.origin) {
        return;
    }

    if (request.mode === 'navigate') {
        event.respondWith(
            fetch(request).catch(() => caches.match(OFFLINE_URL))
        );
        return;
    }

    if (SHELL_ASSETS.includes(request.url)) {
        event.respondWith(
            caches.match(request).then((cached) => cached ?? fetch(request))
        );
    }
});
