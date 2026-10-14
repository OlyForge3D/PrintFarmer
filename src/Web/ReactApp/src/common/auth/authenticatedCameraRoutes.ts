const CAMERA_PROXY_PATH = /^\/api\/cameras\/[a-zA-Z0-9-]+\/(?:stream|snapshot)$/;
const PRINTER_PROXY_PATH = /^\/api\/printers\/[a-zA-Z0-9-]+\/(?:snapshot|camera\/(?:stream|snapshot))$/;

/** Returns only same-origin app proxy routes that are safe to request with a bearer token. */
export function getAuthenticatedCameraProxyRoute(value?: string | null): string | null {
  if (!value || typeof window === 'undefined') return null;
  try {
    const url = new URL(value, window.location.origin);
    if (url.origin !== window.location.origin
      || url.searchParams.has('access_token')
      || (!CAMERA_PROXY_PATH.test(url.pathname) && !PRINTER_PROXY_PATH.test(url.pathname))) {
      return null;
    }
    return `${url.pathname}${url.search}`;
  } catch {
    return null;
  }
}

export function isAuthenticatedCameraProxyRoute(value?: string | null): boolean {
  return getAuthenticatedCameraProxyRoute(value) !== null;
}
