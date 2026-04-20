// Simple service worker for caching static assets
const CACHE_NAME = "catalog-v1";
// Use the service worker's scope as the base path (set by registration or <base> tag)
const BASE = self.registration?.scope ? new URL(self.registration.scope).pathname : "/";
const STATIC_ASSETS = [
  BASE,
  BASE + "assets/uthman/uthmantn1_v10.woff2",
  BASE + "assets/uthman/uthmantn1b_v10.woff2",
  BASE + "vite.svg",
];

// Install event - cache critical assets
self.addEventListener("install", (event) => {
  event.waitUntil(
    caches.open(CACHE_NAME).then((cache) => {
      return cache.addAll(STATIC_ASSETS);
    })
  );
  self.skipWaiting();
});

// Activate event - clean old caches
self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys().then((cacheNames) => {
      return Promise.all(
        cacheNames.map((cacheName) => {
          if (cacheName !== CACHE_NAME) {
            return caches.delete(cacheName);
          }
        })
      );
    })
  );
  self.clients.claim();
});

// Fetch event - serve from cache first, then network
self.addEventListener("fetch", (event) => {
  // Only cache GET requests
  if (event.request.method !== "GET") return;

  // Skip cross-origin requests
  if (!event.request.url.startsWith(self.location.origin)) return;

  event.respondWith(
    caches.match(event.request).then((response) => {
      if (response) {
        return response;
      }

      return fetch(event.request).then((response) => {
        // Only cache successful responses
        if (!response || response.status !== 200 || response.type !== "basic") {
          return response;
        }

        // Cache fonts, CSS, and JS files
        if (event.request.url.match(/\.(woff2?|css|js)$/)) {
          const responseToCache = response.clone();
          caches.open(CACHE_NAME).then((cache) => {
            cache.put(event.request, responseToCache);
          });
        }

        return response;
      });
    })
  );
});
