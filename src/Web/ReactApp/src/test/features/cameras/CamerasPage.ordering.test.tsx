import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CamerasPage } from '@/features/cameras/pages/CamerasPage';
import { CameraHealthStatus, CameraSource, CameraType } from '@/types/api';
import type { DisplayCameraDto } from '@/types/api';

const { cameraServiceMock, apiMock, streamMock } = vi.hoisted(() => ({
  cameraServiceMock: { getDisplayCameras: vi.fn() },
  apiMock: { getSnapshotPreview: vi.fn(), getPrinterSummary: vi.fn() },
  streamMock: { useAuthenticatedMjpegStream: vi.fn() },
}));
vi.mock('@/services/cameraService', () => ({ cameraService: cameraServiceMock }));
vi.mock('@/services/api', () => ({ apiClient: apiMock }));
vi.mock('@/features/cameras/hooks/useAuthenticatedMjpegStream', () => ({
  useAuthenticatedMjpegStream: streamMock.useAuthenticatedMjpegStream,
}));
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => false }),
}));
vi.mock('@/features/cameras/components/EditCameraModal', () => ({ EditCameraModal: () => null }));
vi.mock('@/features/cameras/components/CameraManagementPanel', () => ({ CameraManagementPanel: () => null }));
vi.mock('@/common/components/modals/ConfirmationModal', () => ({ ConfirmationModal: () => null }));

const id = (n: number) => `00000000-0000-4000-8000-00000000000${n}`;
const streamUrl = (n: number) => `/api/cameras/${id(n)}/stream`;

function streamCamera(n: number, name: string, printerId?: string): DisplayCameraDto {
  return {
    id: id(n),
    name,
    streamUrl: streamUrl(n),
    isEnabled: true,
    sortOrder: n,
    isStandalone: !printerId,
    printerIds: printerId ? [printerId] : [],
    printers: printerId ? [{ id: printerId, name: printerId, isPrimary: true }] : [],
    effectiveLocationId: 'loc',
    effectiveLocationName: 'Workshop',
    isLocationDerived: false,
    source: CameraSource.Standalone,
    cameraType: CameraType.General,
    healthStatus: CameraHealthStatus.Healthy,
  };
}

// Sort order deliberately interleaves working, failed and offline cameras, like the reported page.
const cameras = [
  streamCamera(1, 'Failing', 'p-on'),
  streamCamera(2, 'Working', 'p-on'),
  streamCamera(3, 'Offline', 'p-off'),
  streamCamera(4, 'Unloaded', 'p-on'),
  streamCamera(5, 'Standalone'),
];

type StreamState = { streamSrc: string | null; streamUnsupported: boolean; streamFailed: boolean };
let streams: Record<string, StreamState>;
let documentVisibility: DocumentVisibilityState;

function renderPage(entry = '/cameras') {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter initialEntries={[entry]}>
        <CamerasPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

const order = (container: HTMLElement = document.body) =>
  within(container).getAllByRole('article').map((article) => article.getAttribute('aria-label')?.replace(' camera', ''));

function deliverFrame(name: string) {
  fireEvent.load(screen.getByRole('img', { name: `${name} live camera feed` }));
}

async function renderSettledPage(entry?: string) {
  renderPage(entry);
  await screen.findAllByRole('article');
  await act(async () => { await vi.advanceTimersByTimeAsync(0); });
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  localStorage.clear();
  vi.stubGlobal('IntersectionObserver', undefined);
  documentVisibility = 'visible';
  vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => documentVisibility);
  streams = {
    [streamUrl(1)]: { streamSrc: null, streamUnsupported: false, streamFailed: true },
    [streamUrl(2)]: { streamSrc: 'blob:working', streamUnsupported: false, streamFailed: false },
    [streamUrl(3)]: { streamSrc: 'blob:offline', streamUnsupported: false, streamFailed: false },
    [streamUrl(4)]: { streamSrc: null, streamUnsupported: false, streamFailed: false },
    [streamUrl(5)]: { streamSrc: 'blob:standalone', streamUnsupported: false, streamFailed: false },
  };
  cameraServiceMock.getDisplayCameras.mockReset().mockResolvedValue(cameras);
  apiMock.getSnapshotPreview.mockReset();
  apiMock.getPrinterSummary.mockReset().mockResolvedValue([
    { id: 'p-on', name: 'p-on', isOnline: true },
    { id: 'p-off', name: 'p-off', isOnline: false },
  ]);
  streamMock.useAuthenticatedMjpegStream.mockReset().mockImplementation((url: string, enabled: boolean) =>
    enabled ? streams[url] : { streamSrc: null, streamUnsupported: false, streamFailed: false });
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

async function refresh() {
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: /refresh/i })); });
  await act(async () => { await vi.advanceTimersByTimeAsync(0); });
}

async function settleInitialRanking() {
  await act(async () => { await vi.advanceTimersByTimeAsync(4_000); });
}

describe('CamerasPage camera ordering', () => {
  it('ranks delivering online cameras first, then online cameras without an image, then offline cameras', async () => {
    await renderSettledPage();
    expect(order()).toEqual(['Failing', 'Working', 'Unloaded', 'Standalone', 'Offline']);

    deliverFrame('Working');
    deliverFrame('Offline');
    deliverFrame('Standalone');
    await settleInitialRanking();

    expect(order()).toEqual(['Working', 'Standalone', 'Failing', 'Unloaded', 'Offline']);
    expect(apiMock.getSnapshotPreview).not.toHaveBeenCalled();
  });

  it('does not move cards as images load after the initial ranking, and keeps focus in place', async () => {
    await renderSettledPage();
    await settleInitialRanking();
    expect(order()).toEqual(['Failing', 'Working', 'Unloaded', 'Standalone', 'Offline']);

    const rotate = within(screen.getByRole('article', { name: 'Standalone camera' })).getAllByRole('button')[0];
    rotate.focus();
    deliverFrame('Standalone');
    deliverFrame('Working');
    await act(async () => { await vi.advanceTimersByTimeAsync(30_000); });

    expect(order()).toEqual(['Failing', 'Working', 'Unloaded', 'Standalone', 'Offline']);
    expect(document.activeElement).toBe(rotate);

    await refresh();
    expect(order()).toEqual(['Working', 'Standalone', 'Failing', 'Unloaded', 'Offline']);
  });

  it('demotes a delivering camera on refresh only after a visible failure persists', async () => {
    await renderSettledPage();
    deliverFrame('Working');
    deliverFrame('Standalone');
    await settleInitialRanking();
    expect(order()).toEqual(['Working', 'Standalone', 'Failing', 'Unloaded', 'Offline']);

    streams[streamUrl(2)] = { streamSrc: null, streamUnsupported: false, streamFailed: true };
    await refresh();
    await act(async () => { await vi.advanceTimersByTimeAsync(4_000); });
    await refresh();
    expect(order()[0]).toBe('Working');

    await act(async () => { await vi.advanceTimersByTimeAsync(1_000); });
    expect(order()[0]).toBe('Working');
    await refresh();
    expect(order()).toEqual(['Standalone', 'Failing', 'Working', 'Unloaded', 'Offline']);
  });

  it('keeps a delivering camera first while its preview is paused because the tab is hidden', async () => {
    await renderSettledPage();
    deliverFrame('Standalone');
    await settleInitialRanking();
    expect(order()[0]).toBe('Standalone');

    // Even if a paused card still reports a stale failure, it must not be demoted while hidden.
    streamMock.useAuthenticatedMjpegStream.mockImplementation((url: string) => streams[url]);
    streams[streamUrl(5)] = { streamSrc: null, streamUnsupported: false, streamFailed: true };
    documentVisibility = 'hidden';
    act(() => { document.dispatchEvent(new Event('visibilitychange')); });
    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });
    await refresh();

    expect(order()[0]).toBe('Standalone');
  });

  it('applies the same ordering within grouped views', async () => {
    await renderSettledPage('/cameras?view=location');
    const workshop = screen.getByRole('region', { name: /Workshop/ });
    deliverFrame('Standalone');
    await settleInitialRanking();

    expect(order(workshop)).toEqual(['Standalone', 'Failing', 'Working', 'Unloaded', 'Offline']);
  });

  it('does not demote cameras as offline when printer status is unavailable', async () => {
    apiMock.getPrinterSummary.mockRejectedValue(new Error('forbidden'));
    await renderSettledPage();
    deliverFrame('Offline');
    await settleInitialRanking();

    expect(order()).toEqual(['Offline', 'Failing', 'Working', 'Unloaded', 'Standalone']);
  });
});