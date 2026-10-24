// Intensive Listening Web Player - Service Worker
// Version: 1.0.0 (Offline PWA)

const CACHE_NAME = 'il-pwa-1.5.0+19';

const PRECACHE_RESOURCES = [
  './',
  './index.html',
  './manifest.json',
  './favicon.ico',
  './css/fluent.css',
  './fonts/HarmonyOSSansSC-Regular.woff2',
  './fonts/HarmonyOSSansSC-Bold.woff2',
  './js/app.js',
  './js/ilp-parser.js',
  './js/srt-parser.js',
  './js/storage.js',
  './lib/jszip.min.js',
  './sample.ilp',
  './icons/icon-192.png',
  './icons/icon-512.png',
  './icons/icon-maskable.png',
  './icons/icon.svg',
  './icons/favicon-32x32.png',
  './icons/favicon-16x16.png'
];

// Install: Cache all core assets immediately
self.addEventListener('install', (event) => {
  event.waitUntil(
    caches.open(CACHE_NAME)
      .then((cache) => {
        return cache.addAll(PRECACHE_RESOURCES);
      })
      .then(() => self.skipWaiting())
      .catch((err) => {
        console.warn('[PWA SW] Pre-cache failed:', err);
        throw err;
      })
  );
});

// Activate: Clean up old caches and take control
self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys().then((cacheNames) => {
      return Promise.all(
        cacheNames
          .filter((name) => name.startsWith('il-pwa-') && name !== CACHE_NAME)
          .map((name) => caches.delete(name))
      );
    }).then(() => self.clients.claim())
  );
});

function matchCached(request) {
  return caches.open(CACHE_NAME).then(cache => cache.match(request)).catch(() => undefined);
}

// Fetch: Cache-first for app shell, offline fallback for navigation
self.addEventListener('fetch', (event) => {
  const request = event.request;
  const url = new URL(request.url);

  if (url.origin !== self.location.origin) return;

  // Non-GET requests (e.g. telemetry POST/beacon) pass through or fail quietly if offline
  if (request.method !== 'GET') {
    return;
  }

  // 1. External telemetry / RUM / Analytics requests: Bypass Service Worker so ad-blocker detection works accurately
  if (url.origin.includes('aliyuncs.com') || url.origin.includes('51.la')) {
    return;
  }

  // 2. Navigation requests (HTML documents)
  if (request.mode === 'navigate') {
    event.respondWith(
      matchCached(request)
        .then((cachedResponse) => {
          if (cachedResponse) {
            // Fetch in background to update cache (stale-while-revalidate for HTML)
            event.waitUntil(fetch(request).then((networkResponse) => {
              if (networkResponse && networkResponse.status === 200) {
                return caches.open(CACHE_NAME).then((cache) => cache.put(request, networkResponse));
              }
            }).catch(() => {/* offline, keep cached */}));
            return cachedResponse;
          }
          // Fallback to cached index.html or root
          return matchCached('./index.html').then((indexCached) => {
            if (indexCached) return indexCached;
            return matchCached('./').then((rootCached) => {
              if (rootCached) return rootCached;
              return fetch(request);
            });
          });
        })
        .catch(async () => (await matchCached('./index.html')) || new Response('Offline resource unavailable', { status: 503 }))
    );
    return;
  }

  // 3. Local static assets: Cache-first, fallback to network
  event.respondWith(
    matchCached(request).then((cachedResponse) => {
      if (cachedResponse) {
        return cachedResponse;
      }

      return fetch(request)
        .then((networkResponse) => {
          if (networkResponse && networkResponse.status === 200) {
            const responseClone = networkResponse.clone();
            event.waitUntil(caches.open(CACHE_NAME).then((cache) => cache.put(request, responseClone))
              .catch(error => console.warn('[PWA SW] Resource could not be cached:', error)));
          }
          return networkResponse;
        })
        .catch(() => {
          // If offline and request is an image or icon
          if (request.destination === 'image') {
            return matchCached('./icons/icon.svg').then(icon => icon || new Response('Offline resource unavailable', { status: 503 }));
          }
          return new Response('Offline resource unavailable', {
            status: 503,
            statusText: 'Service Unavailable'
          });
        });
    })
  );
});
