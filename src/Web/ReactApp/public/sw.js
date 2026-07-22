// Enhanced versioned service worker (manual) for PrintFarmer
// Provides build-versioned shell caching, runtime caching for assets & images, and navigation fallback.

// Build time constant injected via Vite define
// Build time injected constant (defined via Vite define); fallback to 'dev' in development
// @ts-ignore - injected define constants
const BUILD_TIME = self.__BUILD_TIME__ || 'dev';
// @ts-ignore
const GIT_HASH = self.__GIT_HASH__ || 'dev';
const SHELL_CACHE = `pf-shell-${GIT_HASH}-${BUILD_TIME}`;
const RUNTIME_CACHE = 'pf-runtime-v1';

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
    await Promise.all(keys.filter(k => k.startsWith('pf-shell-') && k !== SHELL_CACHE).map(k => caches.delete(k)));
    await self.clients.claim();
  })());
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return;

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
