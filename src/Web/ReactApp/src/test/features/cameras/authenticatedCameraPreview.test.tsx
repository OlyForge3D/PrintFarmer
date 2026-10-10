import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { getAuthenticatedCameraProxyRoute } from '@/common/auth/authenticatedCameraRoutes';
import { useAuthenticatedMjpegStream } from '@/features/cameras/hooks/useAuthenticatedMjpegStream';
import { usePrinterSnapshotPreview } from '@/features/cameras/hooks/usePrinterSnapshotPreview';

const apiMock = vi.hoisted(() => ({ getSnapshotPreview: vi.fn() }));
const renewalMock = vi.hoisted(() => vi.fn().mockResolvedValue(null));
vi.mock('@/services/api', () => ({ apiClient: apiMock }));
vi.mock('@/common/auth/sessionTokens', () => ({ renewAccessToken: renewalMock }));

const multipartFrame = new Uint8Array([
  ...new TextEncoder().encode('--frame\r\nContent-Type: image/jpeg\r\nContent-Length: 4\r\n\r\n'),
  0xff, 0xd8, 0xff, 0xd9,
  ...new TextEncoder().encode('\r\n--frame\r\n'),
]);
const snapshotBlob = new Blob(['snapshot-image'], { type: 'image/jpeg' });
const NativeURL = URL;

beforeEach(() => {
  localStorage.clear();
  localStorage.setItem('auth-token', 'preview-token');
  vi.stubGlobal('IntersectionObserver', undefined);
  vi.stubGlobal('URL', Object.assign(class TestURL extends NativeURL {}, {
    createObjectURL: vi.fn(() => 'blob:camera-frame'),
    revokeObjectURL: vi.fn(),
  }));
  renewalMock.mockReset().mockResolvedValue(null);
  apiMock.getSnapshotPreview.mockReset().mockResolvedValue(snapshotBlob);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('authenticated camera previews', () => {
  it('allows bearer-authenticated requests only to same-origin camera proxy routes', () => {
    expect(getAuthenticatedCameraProxyRoute('/api/cameras/camera-1/snapshot')).toBe('/api/cameras/camera-1/snapshot');
    expect(getAuthenticatedCameraProxyRoute('/api/printers/printer-1/camera/stream')).toBe('/api/printers/printer-1/camera/stream');
    expect(getAuthenticatedCameraProxyRoute('https://external.example/api/cameras/camera-1/stream')).toBeNull();
    expect(getAuthenticatedCameraProxyRoute('/api/admin/users')).toBeNull();
  });

  it('loads a protected snapshot through the API client and aborts the request on unmount', async () => {
    const { result, unmount } = renderHook(() => usePrinterSnapshotPreview(
      undefined,
      false,
      10_000,
      '/api/cameras/camera-1/snapshot',
      true,
      '/api/cameras/camera-1/snapshot',
    ));

    await waitFor(() => expect(result.current.snapshotSrc).toBe('blob:camera-frame'));
    expect(apiMock.getSnapshotPreview).toHaveBeenCalledOnce();
    expect(apiMock.getSnapshotPreview.mock.calls[0][0]).toBe('/api/cameras/camera-1/snapshot');
    expect(URL.createObjectURL).toHaveBeenCalledWith(snapshotBlob);
    const signal = apiMock.getSnapshotPreview.mock.calls[0][1] as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:camera-frame');
  });

  it('sends the current bearer token to a validated stream proxy and aborts/revokes on unmount', async () => {
    let fetchSignal: AbortSignal | undefined;
    const body = new ReadableStream<Uint8Array>({
      start(controller) { controller.enqueue(multipartFrame); },
    });
    vi.stubGlobal('fetch', vi.fn(async (_url: string, options: RequestInit) => {
      fetchSignal = options.signal as AbortSignal;
      expect(new Headers(options.headers).get('Authorization')).toBe('Bearer preview-token');
      return new Response(body, { status: 200, headers: { 'Content-Type': 'multipart/x-mixed-replace; boundary=frame' } });
    }));

    const { result, unmount } = renderHook(() => useAuthenticatedMjpegStream('/api/cameras/camera-1/stream'));
    await waitFor(() => expect(result.current.streamSrc).toBe('blob:camera-frame'));
    unmount();
    expect(fetchSignal?.aborted).toBe(true);
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:camera-frame');
  });

  it('renews once after a stream 401 and retries with the rotated access token', async () => {
    renewalMock.mockResolvedValue('rotated-access-token');
    const body = new ReadableStream<Uint8Array>({
      start(controller) { controller.enqueue(multipartFrame); },
    });
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(null, { status: 401 }))
      .mockResolvedValueOnce(new Response(body, { status: 200, headers: { 'Content-Type': 'multipart/x-mixed-replace; boundary=frame' } }));
    vi.stubGlobal('fetch', fetchMock);

    const { result, unmount } = renderHook(() => useAuthenticatedMjpegStream('/api/cameras/camera-1/stream'));
    await waitFor(() => expect(result.current.streamSrc).toBe('blob:camera-frame'));
    expect(renewalMock).toHaveBeenCalledOnce();
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(new Headers(fetchMock.mock.calls[0][1].headers).get('Authorization')).toBe('Bearer preview-token');
    expect(new Headers(fetchMock.mock.calls[1][1].headers).get('Authorization')).toBe('Bearer rotated-access-token');
    unmount();
  });

  it('does not request arbitrary external stream URLs with application credentials', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const { result } = renderHook(() => useAuthenticatedMjpegStream('https://camera.example/stream'));
    await act(async () => {});
    expect(fetchMock).not.toHaveBeenCalled();
    expect(result.current.streamSrc).toBeNull();
  });
});
