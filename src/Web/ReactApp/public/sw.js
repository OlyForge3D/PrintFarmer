// Enhanced versioned service worker (manual) for PrintFarmer
// Provides build-versioned shell caching, runtime caching for assets & images, and navigation fallback.

// These placeholders are replaced by the Vite build plugin. Public assets are
// copied verbatim, so Vite's `define` entries cannot inject these values.
const BUILD_TIME = '__PRINTFARMER_BUILD_TIME__';
const GIT_HASH = '__PRINTFARMER_GIT_HASH__';
const SHELL_CACHE = `pf-shell-${GIT_HASH}-${BUILD_TIME}`;
// Version-scoped like the shell cache. A fixed key (the old `pf-runtime-v1`)
// meant the cache-first script handler below kept serving a previous build's
// route chunk forever; that stale chunk then dynamically imported a hashed
// module which no longer exists on the server, producing "Failed to fetch
// dynamically imported module" until the user hard-reloaded past the SW.
const RUNTIME_CACHE = `pf-runtime-${GIT_HASH}-${BUILD_TIME}`;

const CORE = [
  '/',
  '/index.html',
  '/manifest.webmanifest',
  '/printfarmer-logo.svg'
];

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches.open(SHELL_CACHE).then(cache => cache.addAll(CORE)).then(() => self.skipWaiting())
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(
      keys
        .filter(k => (k.startsWith('pf-shell-') && k !== SHELL_CACHE) ||
                     (k.startsWith('pf-runtime-') && k !== RUNTIME_CACHE))
        .map(k => caches.delete(k))
    );
    await self.clients.claim();
  })());
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return;

  // Never cache authenticated or live application traffic. Cache keys do not
  // vary by Authorization, and serving an API response from a shared browser
  // cache can expose stale or another user's data.
  const requestPath = new URL(req.url).pathname;
  if (requestPath === '/api' || requestPath.startsWith('/api/') ||
      requestPath === '/hubs' || requestPath.startsWith('/hubs/')) {
    return;
  }

  if (req.mode === 'navigate') {
    event.respondWith((async () => {
      try {
        const fresh = await fetch(req);
        const cache = await caches.open(SHELL_CACHE);
        cache.put('/', fresh.clone());
        return fresh;
      } catch {
        return (await caches.match('/')) || (await caches.match('/index.html')) || Response.error();
      }
    })());
    return;
  }

  if (['style', 'script', 'font', 'image'].includes(req.destination)) {
    event.respondWith((async () => {
      const cached = await caches.match(req);
      if (cached) return cached;
      try {
        const res = await fetch(req);
        const cache = await caches.open(RUNTIME_CACHE);
        cache.put(req, res.clone());
        return res;
      } catch {
        return cached || Response.error();
      }
    })());
    return;
  }

  event.respondWith((async () => {
    try {
      const res = await fetch(req);
      const cache = await caches.open(RUNTIME_CACHE);
      cache.put(req, res.clone());
      return res;
    } catch {
      const cached = await caches.match(req);
      if (cached) return cached;
      throw new Error('Network error and no cache for ' + req.url);
    }
  })());
});

// Web Push notification handler
self.addEventListener('push', (event) => {
  const defaultPayload = { title: 'PrintFarmer', body: 'You have a new notification.' };
  let data = defaultPayload;
  try {
    if (event.data) {
      data = { ...defaultPayload, ...event.data.json() };
    }
  } catch {
    if (event.data) {
      data = { ...defaultPayload, body: event.data.text() };
    }
  }

  event.waitUntil(
    self.registration.showNotification(data.title, {
      body: data.body,
      icon: '/favicon.png',
      badge: '/favicon-16x16.png',
      data: data,
    })
  );
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(clients => {
      if (clients.length > 0) {
        return clients[0].focus();
      }
      return self.clients.openWindow('/');
    })
  );
});
