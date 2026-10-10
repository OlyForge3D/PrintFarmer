import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { EditCameraModal } from '@/features/cameras/components/EditCameraModal';
import { CameraHealthStatus, CameraSource, CameraType } from '@/types/api';
import type { CameraConfigDto, DisplayCameraDto } from '@/types/api';
import type { ReactNode } from 'react';

const { service } = vi.hoisted(() => ({
  service: { getCameraConfig: vi.fn(), updateCamera: vi.fn(), detectCameraEndpoints: vi.fn() },
}));
vi.mock('@/services/cameraService', () => ({ cameraService: service }));
vi.mock('@/common/hooks/useApi', () => ({ usePrinters: () => ({ data: [] }) }));
vi.mock('@/common/components/modals/Modal', () => ({
  Modal: ({ isOpen, children, footer }: { isOpen: boolean; children: ReactNode; footer: ReactNode }) =>
    isOpen ? <div>{children}{footer}</div> : null,
}));

const camera: DisplayCameraDto = {
  id: 'camera-1',
  name: 'Workshop',
  streamUrl: '/api/cameras/camera-1/stream',
  snapshotUrl: '/api/cameras/camera-1/snapshot',
  isEnabled: true,
  sortOrder: 0,
  isStandalone: true,
  source: CameraSource.Standalone,
  cameraType: CameraType.General,
  healthStatus: CameraHealthStatus.Healthy,
};
const config: CameraConfigDto = {
  id: camera.id,
  streamUrl: 'http://camera.local/stream',
  snapshotUrl: 'http://camera.local/snapshot',
  streamUrlHasCredentials: false,
  snapshotUrlHasCredentials: false,
};

beforeEach(() => {
  vi.clearAllMocks();
  service.getCameraConfig.mockResolvedValue(config);
  service.updateCamera.mockResolvedValue({});
});
afterEach(cleanup);

function renderModal() {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <EditCameraModal camera={camera} isOpen onClose={vi.fn()} onSuccess={vi.fn()} />
    </QueryClientProvider>
  );
}

async function loadModal() {
  renderModal();
  await waitFor(() => expect(screen.getByLabelText('Stream URL')).toBeEnabled());
}

describe('EditCameraModal configuration', () => {
  it('blocks editing and saving while configuration loads, never using display proxy URLs', () => {
    service.getCameraConfig.mockReturnValue(new Promise(() => {}));
    renderModal();
    expect(screen.getByText('Loading camera configuration…')).toBeInTheDocument();
    expect(screen.getByLabelText('Stream URL')).toHaveValue('');
    expect(screen.getByLabelText('Snapshot URL')).toHaveValue('');
    expect(screen.getByLabelText('Stream URL')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Save Camera' })).toBeDisabled();
  });

  it('loads actual redacted configuration rather than display proxy URLs', async () => {
    await loadModal();
    expect(service.getCameraConfig).toHaveBeenCalledWith(camera.id);
    expect(screen.getByLabelText('Stream URL')).toHaveValue(config.streamUrl);
    expect(screen.getByLabelText('Snapshot URL')).toHaveValue(config.snapshotUrl);
    expect(screen.getByRole('button', { name: 'Save Camera' })).toBeDisabled();
  });

  it('visibly reports a forbidden configuration request and prevents saving without a fallback', async () => {
    service.getCameraConfig.mockRejectedValue(new Error('HTTP 403'));
    renderModal();
    expect(await screen.findByText('Could not load camera configuration')).toBeInTheDocument();
    expect(screen.getByLabelText('Stream URL')).toHaveValue('');
    expect(screen.getByLabelText('Stream URL')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Save Camera' })).toBeDisabled();
    expect(service.updateCamera).not.toHaveBeenCalled();
  });

  it('omits unchanged URL fields when saving metadata to preserve hidden credentials', async () => {
    service.getCameraConfig.mockResolvedValue({
      ...config, streamUrlHasCredentials: true, snapshotUrlHasCredentials: true,
    });
    await loadModal();
    expect(screen.getAllByText(/Stored credentials are hidden/)).toHaveLength(2);
    fireEvent.change(screen.getByLabelText(/Camera Name/), { target: { value: 'Renamed' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Camera' }));
    await waitFor(() => expect(service.updateCamera).toHaveBeenCalled());
    const request = service.updateCamera.mock.calls[0][1];
    expect(request.name).toBe('Renamed');
    expect(request).not.toHaveProperty('streamUrl');
    expect(request).not.toHaveProperty('snapshotUrl');
  });

  it('sends only deliberately replaced URLs', async () => {
    service.getCameraConfig.mockResolvedValue({ ...config, streamUrlHasCredentials: true });
    await loadModal();
    fireEvent.change(screen.getByLabelText('Stream URL'), { target: { value: 'http://new-camera.local/stream' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Camera' }));
    await waitFor(() => expect(service.updateCamera).toHaveBeenCalled());
    expect(service.updateCamera.mock.calls[0][1]).toMatchObject({ streamUrl: 'http://new-camera.local/stream' });
    expect(service.updateCamera.mock.calls[0][1]).not.toHaveProperty('snapshotUrl');
  });

  it('sends an empty string for a deliberately cleared URL while retaining the other URL', async () => {
    await loadModal();
    fireEvent.change(screen.getByLabelText('Stream URL'), { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Camera' }));
    await waitFor(() => expect(service.updateCamera).toHaveBeenCalled());
    expect(service.updateCamera.mock.calls[0][1]).toMatchObject({ streamUrl: '' });
    expect(service.updateCamera.mock.calls[0][1]).not.toHaveProperty('snapshotUrl');
  });

  it('preserves an unrenderable credentialed URL when editing metadata', async () => {
    service.getCameraConfig.mockResolvedValue({
      ...config, streamUrl: null, snapshotUrl: null, streamUrlHasCredentials: true,
    });
    await loadModal();
    expect(screen.getByLabelText('Stream URL')).toHaveValue('');
    fireEvent.change(screen.getByLabelText(/Camera Name/), { target: { value: 'Renamed' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Camera' }));
    await waitFor(() => expect(service.updateCamera).toHaveBeenCalled());
    expect(service.updateCamera.mock.calls[0][1]).not.toHaveProperty('streamUrl');
    expect(service.updateCamera.mock.calls[0][1]).not.toHaveProperty('snapshotUrl');
  });
});
