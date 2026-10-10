import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router';
import { CamerasPage } from '@/features/cameras/pages/CamerasPage';
import { CameraAccessMode, CameraHealthStatus, CameraSnapshotStrategy, CameraSource, CameraStreamFormat, CameraType } from '@/types/api';

const { cameraServiceMock, apiMock, streamMock } = vi.hoisted(() => ({
  cameraServiceMock: { getDisplayCameras: vi.fn() },
  apiMock: { getSnapshotPreview: vi.fn() },
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

const cameraId = '00000000-0000-4000-8000-000000000001';
const snapshotUrl = `/api/cameras/${cameraId}/snapshot`;
const NativeURL = URL;

function createCamera(overrides: Record<string, unknown> = {}) {
  return {
    id: cameraId,
    name: 'x400 Camera',
    isEnabled: true,
    sortOrder: 0,
    isStandalone: true,
    source: CameraSource.Standalone,
    cameraType: CameraType.General,
    healthStatus: CameraHealthStatus.Healthy,
    snapshotUrl,
    accessMode: CameraAccessMode.SnapshotOnly,
    streamFormat: CameraStreamFormat.Unknown,
    snapshotStrategy: CameraSnapshotStrategy.DirectUrl,
    ...overrides,
  };
}

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal('IntersectionObserver', undefined);
  vi.stubGlobal('URL', Object.assign(class TestURL extends NativeURL {}, {
    createObjectURL: vi.fn(() => 'blob:camera-preview'),
    revokeObjectURL: vi.fn(),
  }));
  cameraServiceMock.getDisplayCameras.mockReset().mockResolvedValue([createCamera()]);
  apiMock.getSnapshotPreview.mockReset().mockResolvedValue(new Blob(['jpeg'], { type: 'image/jpeg' }));
  streamMock.useAuthenticatedMjpegStream.mockReset().mockImplementation((_url, enabled) => ({
    streamSrc: null,
    streamUnsupported: enabled,
    streamFailed: false,
  }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function renderPage() {
  return render(
    <MemoryRouter>
      <CamerasPage />
    </MemoryRouter>
  );
}

describe('CamerasPage camera previews', () => {
  it.each(['unsupported', 'failed'] as const)('does not invent a snapshot for a stream-only camera when its stream is %s', async (issue) => {
    cameraServiceMock.getDisplayCameras.mockResolvedValue([
      createCamera({
        streamUrl: `/api/cameras/${cameraId}/stream`,
        snapshotUrl: undefined,
        accessMode: undefined,
        streamFormat: undefined,
        snapshotStrategy: undefined,
      }),
    ]);
    localStorage.setItem(`printfarmer-camera-mode:camera:${cameraId}`, 'snapshot');
    streamMock.useAuthenticatedMjpegStream.mockImplementation((_url, enabled) => ({
      streamSrc: null,
      streamUnsupported: enabled && issue === 'unsupported',
      streamFailed: enabled && issue === 'failed',
    }));

    renderPage();

    const label = issue === 'unsupported' ? 'Live stream unsupported' : 'Live stream unavailable';
    expect(await screen.findByRole('status', { name: `x400 Camera: ${label}` })).toBeInTheDocument();
    expect(streamMock.useAuthenticatedMjpegStream).toHaveBeenCalledWith(
      `/api/cameras/${cameraId}/stream`, true
    );
    expect(apiMock.getSnapshotPreview).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Snapshot mode' })).not.toBeInTheDocument();
    expect(screen.queryByRole('img', { name: 'x400 Camera camera preview' })).not.toBeInTheDocument();
    expect(screen.queryByText(/showing snapshot/)).not.toBeInTheDocument();
    expect(screen.getByText('Preview failed · probe healthy')).toBeInTheDocument();
  });

  it('loads a standalone protected snapshot as an authenticated blob and shows single-mode status', async () => {
    renderPage();

    expect(await screen.findByRole('img', { name: 'x400 Camera camera preview' })).toHaveAttribute('src', 'blob:camera-preview');
    expect(apiMock.getSnapshotPreview).toHaveBeenCalledWith(snapshotUrl, expect.any(AbortSignal));
    expect(screen.getByRole('status', { name: 'x400 Camera: Snapshot only' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Stream mode' })).not.toBeInTheDocument();
  });

  it('offers both mode buttons when both endpoints are configured', async () => {
    cameraServiceMock.getDisplayCameras.mockResolvedValue([
      createCamera({
        streamUrl: `/api/cameras/${cameraId}/stream`,
        accessMode: CameraAccessMode.StreamAndSnapshot,
        streamFormat: CameraStreamFormat.Mjpeg,
      }),
    ]);
    localStorage.setItem(`printfarmer-camera-mode:camera:${cameraId}`, 'snapshot');

    renderPage();

    expect(await screen.findByRole('group', { name: 'x400 Camera preview mode' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Snapshot mode' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Stream mode' })).toBeInTheDocument();
  });

  it('falls back to the snapshot when a selected stream is unsupported and reports the failed preview', async () => {
    cameraServiceMock.getDisplayCameras.mockResolvedValue([
      createCamera({
        streamUrl: `/api/cameras/${cameraId}/stream`,
        accessMode: CameraAccessMode.StreamAndSnapshot,
        streamFormat: CameraStreamFormat.Mjpeg,
      }),
    ]);
    localStorage.setItem(`printfarmer-camera-mode:camera:${cameraId}`, 'snapshot');

    renderPage();
    const streamButton = await screen.findByRole('button', { name: 'Stream mode' });
    expect(streamButton).toHaveAttribute('aria-pressed', 'false');
    fireEvent.click(streamButton);

    expect(await screen.findByRole('img', { name: 'x400 Camera camera preview' })).toHaveAttribute('src', 'blob:camera-preview');
    expect(screen.getByRole('button', { name: 'Snapshot mode' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Stream mode' })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getByText('Live stream unsupported · showing snapshot')).toBeInTheDocument();
    expect(screen.getByText('Preview failed · probe healthy')).toBeInTheDocument();
  });

  it('does not fall back to an unauthenticated image request when proxy loading fails', async () => {
    apiMock.getSnapshotPreview.mockRejectedValue(new Error('HTTP 502'));

    renderPage();

    expect(await screen.findByText('Snapshot preview failed')).toBeInTheDocument();
    expect(apiMock.getSnapshotPreview).toHaveBeenCalledWith(snapshotUrl, expect.any(AbortSignal));
    expect(screen.queryByRole('img', { name: 'x400 Camera camera preview' })).not.toBeInTheDocument();
    expect(screen.getByText('Preview failed · probe healthy')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText('This card could not load the snapshot preview.')).toBeInTheDocument());
  });
});
