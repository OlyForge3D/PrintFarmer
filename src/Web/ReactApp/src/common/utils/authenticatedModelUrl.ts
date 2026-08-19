/**
 * Shared helpers for loading 3D model files that live behind the API's
 * authenticated file endpoints (`/3d-models/file/{id}` and
 * `/3d-models/download-for-viewer`).
 *
 * three.js loaders (STLLoader, PLYLoader, etc.) fetch their `url` prop with a
 * bare `fetch`/`XMLHttpRequest` call that never carries the app's bearer
 * token, so pointing them directly at an authenticated API URL returns 401.
 * These helpers detect that case and pre-fetch the bytes through the shared
 * `apiClient` (which attaches the Authorization header), exposing the result
 * as a `Blob` object URL that any loader can consume unauthenticated.
 */
import { apiClient } from '@/services/api';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';

/**
 * Returns true when `url` points at one of the API's authenticated 3D model
 * file endpoints and therefore needs a bearer token to fetch successfully.
 */
export function isAuthenticatedModelUrl(url: string): boolean {
  try {
    const apiBase = new URL(getApiBaseUrl(), window.location.origin);
    const candidate = new URL(url, window.location.origin);
    const apiPath = apiBase.pathname.replace(/\/$/, '');

    return candidate.origin === apiBase.origin
      && (
        candidate.pathname.startsWith(`${apiPath}/3d-models/file/`)
        || candidate.pathname === `${apiPath}/3d-models/download-for-viewer`
      );
  } catch {
    return false;
  }
}

/**
 * Fetches the bytes for `url`, using the authenticated `apiClient` (bearer
 * token attached) when the URL targets a protected 3D model endpoint, or a
 * plain `fetch` otherwise (e.g. public bed textures/models).
 */
export async function loadModelArrayBuffer(url: string, signal?: AbortSignal): Promise<ArrayBuffer> {
  if (isAuthenticatedModelUrl(url)) {
    const response = await apiClient.get<ArrayBuffer>(url, {
      responseType: 'arraybuffer',
      // `url` is already fully-qualified (built via getApiBaseUrl()); override
      // baseURL to avoid Axios double-prefixing it with the instance's /api baseURL.
      baseURL: '',
      signal,
    });
    return response.data;
  }

  const response = await fetch(url, { signal });
  if (!response.ok) {
    throw new Error(`Failed to load model (${response.status})`);
  }

  return response.arrayBuffer();
}
