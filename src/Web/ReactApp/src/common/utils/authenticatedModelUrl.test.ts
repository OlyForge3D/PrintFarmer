import { describe, it, expect, vi, beforeEach } from 'vitest';

const { apiClientGetMock } = vi.hoisted(() => ({
  apiClientGetMock: vi.fn(),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    get: apiClientGetMock,
  },
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: vi.fn(() => '/api'),
}));

import { isAuthenticatedModelUrl, loadModelArrayBuffer } from './authenticatedModelUrl';

/**
 * Regression coverage for #1711: after uploading a model and selecting it
 * from the slicer library, the model viewer's `GET /api/3d-models/file/{id}`
 * request returned 401. Root cause: three.js loaders (STLLoader, PLYLoader,
 * etc.) fetch their `url` with a bare request that never carries the app's
 * bearer token. `isAuthenticatedModelUrl`/`loadModelArrayBuffer` detect the
 * authenticated endpoints and route them through `apiClient` (which attaches
 * the Authorization header) instead of an unauthenticated `fetch`.
 */
describe('authenticatedModelUrl', () => {
  beforeEach(() => {
    apiClientGetMock.mockReset();
  });

  describe('isAuthenticatedModelUrl', () => {
    it('recognizes the 3d-models file endpoint', () => {
      expect(isAuthenticatedModelUrl('/api/3d-models/file/model-123')).toBe(true);
    });

    it('recognizes the 3d-models file endpoint with a query string appended (3MF STL fallback)', () => {
      expect(isAuthenticatedModelUrl('/api/3d-models/file/model-123?forceStl=true')).toBe(true);
    });

    it('recognizes the download-for-viewer endpoint', () => {
      expect(isAuthenticatedModelUrl('/api/3d-models/download-for-viewer')).toBe(true);
    });

    it('recognizes an absolute URL for the file endpoint', () => {
      expect(isAuthenticatedModelUrl('http://localhost:3000/api/3d-models/file/model-123')).toBe(true);
    });

    it('does not flag unrelated URLs as authenticated', () => {
      expect(isAuthenticatedModelUrl('/textures/bed.png')).toBe(false);
      expect(isAuthenticatedModelUrl('/api/printers/1')).toBe(false);
    });

    it('returns false for an unparsable URL instead of throwing', () => {
      expect(isAuthenticatedModelUrl('::not-a-url::')).toBe(false);
    });
  });

  describe('loadModelArrayBuffer', () => {
    it('fetches authenticated model URLs through apiClient so the bearer token is attached', async () => {
      const data = new ArrayBuffer(4);
      apiClientGetMock.mockResolvedValue({ data });

      const result = await loadModelArrayBuffer('/api/3d-models/file/model-123');

      expect(apiClientGetMock).toHaveBeenCalledWith('/api/3d-models/file/model-123', expect.objectContaining({
        responseType: 'arraybuffer',
        baseURL: '',
      }));
      expect(result).toBe(data);
    });

    it('does not attempt an unauthenticated fetch for authenticated model URLs', async () => {
      const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response());
      apiClientGetMock.mockResolvedValue({ data: new ArrayBuffer(0) });

      await loadModelArrayBuffer('/api/3d-models/file/model-123');

      expect(fetchSpy).not.toHaveBeenCalled();
      fetchSpy.mockRestore();
    });

    it('falls back to a plain fetch for non-authenticated URLs', async () => {
      const data = new ArrayBuffer(8);
      const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
        new Response(data, { status: 200 }),
      );

      const result = await loadModelArrayBuffer('/textures/bed.png');

      expect(fetchSpy).toHaveBeenCalledWith('/textures/bed.png', expect.objectContaining({}));
      expect(apiClientGetMock).not.toHaveBeenCalled();
      expect(result.byteLength).toBe(data.byteLength);
      fetchSpy.mockRestore();
    });

    it('throws when a plain fetch for a non-authenticated URL fails', async () => {
      const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
        new Response(null, { status: 404 }),
      );

      await expect(loadModelArrayBuffer('/textures/missing.png')).rejects.toThrow('404');
      fetchSpy.mockRestore();
    });
  });
});
