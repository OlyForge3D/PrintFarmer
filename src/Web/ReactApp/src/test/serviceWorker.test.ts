import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import vm from 'node:vm';
import { describe, expect, it, vi } from 'vitest';

// Regression coverage for #3368: the service worker must keep the cached `/`
// entry pointed at the SPA shell no matter which documents the user opens.

const ORIGIN = 'https://farm.example';
const SW_SOURCE = readFileSync(resolve(__dirname, '../../public/sw.js'), 'utf8');
const SHELL_HTML = '<!doctype html><div id="root"></div>';

type Listener = (event: unknown) => void;
type FetchImpl = (req: { url: string }) => Promise<Response>;

interface ResponseOptions {
  status?: number;
  contentType?: string;
  url?: string;
  redirected?: boolean;
  type?: ResponseType;
}

function makeResponse(body: string, opts: ResponseOptions = {}): Response {
  const status = opts.status ?? 200;
  // Opaque responses report status 0, which the Response constructor rejects.
  const res = new Response(body, {
    status: status === 0 ? 200 : status,
    headers: opts.contentType === undefined ? {} : { 'content-type': opts.contentType },
  });
  if (status === 0) Object.defineProperty(res, 'status', { value: 0 });
  Object.defineProperty(res, 'url', { value: opts.url ?? '' });
  Object.defineProperty(res, 'redirected', { value: opts.redirected ?? false });
  if (opts.type) Object.defineProperty(res, 'type', { value: opts.type });
  return res;
}

const html = (body = SHELL_HTML, opts: ResponseOptions = {}) =>
  makeResponse(body, { contentType: 'text/html; charset=utf-8', ...opts });

function loadServiceWorker() {
  const listeners = new Map<string, Listener>();
  const stores = new Map<string, Map<string, Response>>();
  const keyOf = (key: string | { url: string }) =>
    new URL(typeof key === 'string' ? key : key.url, ORIGIN).href;

  const caches = {
    open: async (name: string) => {
      if (!stores.has(name)) stores.set(name, new Map());
      const store = stores.get(name)!;
      return {
        put: async (key: string | { url: string }, res: Response) => {
          store.set(keyOf(key), res);
        },
        addAll: async (keys: string[]) => {
          for (const k of keys) store.set(keyOf(k), html());
        },
      };
    },
    match: async (key: string | { url: string }) => {
      for (const store of stores.values()) {
        const hit = store.get(keyOf(key));
        if (hit) return hit.clone();
      }
      return undefined;
    },
    keys: async () => [...stores.keys()],
    delete: async (name: string) => stores.delete(name),
  };

  let fetchImpl: FetchImpl = async () => html();
  const fetchSpy = vi.fn((req: { url: string }) => fetchImpl(req));

  const self = {
    location: { origin: ORIGIN },
    addEventListener: (type: string, fn: Listener) => listeners.set(type, fn),
    skipWaiting: async () => undefined,
    clients: { claim: async () => undefined },
  };

  vm.runInNewContext(SW_SOURCE, { self, caches, fetch: fetchSpy, URL, Response });

  const shellCacheName = () => [...stores.keys()].find((k) => k.startsWith('pf-shell-'));

  return {
    stores,
    fetchSpy,
    setFetch(impl: FetchImpl) {
      fetchImpl = impl;
    },
    async install() {
      let done: Promise<unknown> = Promise.resolve();
      listeners.get('install')!({ waitUntil: (p: Promise<unknown>) => { done = p; } });
      await done;
    },
    async activate() {
      let done: Promise<unknown> = Promise.resolve();
      listeners.get('activate')!({ waitUntil: (p: Promise<unknown>) => { done = p; } });
      await done;
    },
    /** Returns the SW's response, or `undefined` when the SW let the browser handle it. */
    async navigate(path: string, mode: RequestMode = 'navigate', destination = 'document') {
      let responded: Promise<Response> | undefined;
      listeners.get('fetch')!({
        request: { method: 'GET', url: new URL(path, ORIGIN).href, mode, destination },
        respondWith: (p: Promise<Response>) => { responded = p; },
      });
      return responded ? await responded : undefined;
    },
    async cachedShell() {
      const res = stores.get(shellCacheName()!)?.get(`${ORIGIN}/`);
      return res ? await res.clone().text() : undefined;
    },
  };
}

async function installedWorker() {
  const sw = loadServiceWorker();
  await sw.install();
  expect(await sw.cachedShell()).toBe(SHELL_HTML);
  return sw;
}

describe('service worker offline shell cache (#3368)', () => {
  it('updates the shell from a successful same-origin SPA navigation', async () => {
    const sw = await installedWorker();
    const updated = '<!doctype html><div id="root" data-build="next"></div>';
    sw.setFetch(async (req) => html(updated, { url: req.url }));

    const res = await sw.navigate('/printers/42');

    expect(await res!.text()).toBe(updated);
    expect(await sw.cachedShell()).toBe(updated);
  });

  it('serves the cached shell for SPA routes while offline', async () => {
    const sw = await installedWorker();
    sw.setFetch(async () => { throw new TypeError('Failed to fetch'); });

    const res = await sw.navigate('/jobs');

    expect(await res!.text()).toBe(SHELL_HTML);
  });

  it.each([
    '/licenses/droid-fonts/README.txt',
    '/licenses/droid-fonts/NOTICE.txt',
    '/install-ca',
    '/ca.cer',
    '/health',
    '/healthz',
    '/slicer/healthz',
    '/grafana/d/abc',
    '/jaeger/search',
    '/api/artifacts/123/download',
    '/api',
    '/hubs/printers',
    '/hubs',
    '/exports/report.gcode',
  ])('does not intercept or cache the non-SPA navigation %s', async (path) => {
    const sw = await installedWorker();
    sw.setFetch(async (req) => makeResponse('not the shell', { contentType: 'text/plain', url: req.url }));

    const res = await sw.navigate(path);

    expect(res).toBeUndefined();
    expect(sw.fetchSpy).not.toHaveBeenCalled();
    expect(await sw.cachedShell()).toBe(SHELL_HTML);
  });

  it('still treats SPA routes that share a reserved prefix as SPA navigations', async () => {
    const sw = await installedWorker();
    const updated = '<!doctype html><div id="root" data-route="slicer"></div>';
    sw.setFetch(async (req) => html(updated, { url: req.url }));

    expect(await (await sw.navigate('/slicer'))!.text()).toBe(updated);
    expect(await sw.cachedShell()).toBe(updated);
  });

  it.each<[string, () => Response]>([
    ['a 404 HTML page', () => html('<h1>404</h1>', { status: 404 })],
    ['a 500 HTML page', () => html('<h1>oops</h1>', { status: 500 })],
    ['a redirected document', () => html('<h1>login</h1>', { redirected: true, url: `${ORIGIN}/login` })],
    ['a cross-origin document', () => html('<h1>sso</h1>', { url: 'https://idp.example/login' })],
    ['an opaque response', () => html('', { status: 0, type: 'opaque' })],
    ['a text/plain document', () => makeResponse('plain', { contentType: 'text/plain' })],
    ['a binary download', () => makeResponse('bin', { contentType: 'application/octet-stream' })],
    ['a response without content-type', () => makeResponse('?')],
  ])('does not replace the shell with %s on an SPA route', async (_label, build) => {
    const sw = await installedWorker();
    sw.setFetch(async () => build());

    await sw.navigate('/printers');

    expect(await sw.cachedShell()).toBe(SHELL_HTML);
  });

  it('keeps bypassing /api and /hubs for non-navigation requests', async () => {
    const sw = await installedWorker();

    expect(await sw.navigate('/api/printers', 'cors', '')).toBeUndefined();
    expect(await sw.navigate('/hubs/printers/negotiate', 'cors', '')).toBeUndefined();
    expect(sw.fetchSpy).not.toHaveBeenCalled();
  });

  it('discards a polluted shell cache from an older worker on activation', async () => {
    const sw = loadServiceWorker();
    const legacy = 'pf-shell-oldhash-0';
    sw.stores.set(legacy, new Map([[`${ORIGIN}/`, makeResponse('polluted', { contentType: 'text/plain' })]]));

    await sw.install();
    await sw.activate();

    expect(sw.stores.has(legacy)).toBe(false);
    expect(await sw.cachedShell()).toBe(SHELL_HTML);
  });
});
