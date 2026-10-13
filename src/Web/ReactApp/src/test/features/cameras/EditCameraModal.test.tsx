import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { EditCameraModal } from '@/features/cameras/components/EditCameraModal';
import { CameraHealthStatus, CameraSource, CameraType } from '@/types/api';
import type { CameraConfigDto, DisplayCameraDto } from '@/types/api';
import type { ReactNode } from 'react';

const { service, locations, printersState } = vi.hoisted(() => ({
  service: { getCameraConfig: vi.fn(), updateCamera: vi.fn(), detectCameraEndpoints: vi.fn() },
  locations: { getAllLocations: vi.fn() },
  printersState: { data: [] as Array<{ id: string; name: string; location?: { id: string; name: string } }> },
}));
vi.mock('@/services/cameraService', () => ({ cameraService: service }));
vi.mock('@/services/locationService', () => ({ locationService: locations }));
vi.mock('@/common/hooks/useApi', () => ({ usePrinters: () => ({ data: printersState.data }) }));
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
  printerIds: [],
  printers: [],
  isLocationDerived: false,
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
  printersState.data = [];
  locations.getAllLocations.mockResolvedValue([
    { id: 'loc-office', name: 'Office', depth: 0, sortOrder: 0, printerCount: 0, totalPrinterCount: 0, createdAt: '' },
    { id: 'loc-shop', name: 'Workshop', depth: 0, sortOrder: 1, printerCount: 0, totalPrinterCount: 0, createdAt: '' },
  ]);
});
afterEach(cleanup);

function renderModal(modalCamera: DisplayCameraDto = camera, lockPrinterId?: string) {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <EditCameraModal camera={modalCamera} isOpen onClose={vi.fn()} onSuccess={vi.fn()} lockPrinterId={lockPrinterId} />
    </QueryClientProvider>
  );
}

async function loadModal(modalCamera: DisplayCameraDto = camera, lockPrinterId?: string) {
  renderModal(modalCamera, lockPrinterId);
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

describe('EditCameraModal printer links and location', () => {
  const printers = [
    { id: 'p1', name: 'Voron', location: { id: 'loc-shop', name: 'Workshop' } },
    { id: 'p2', name: 'Bambu', location: { id: 'loc-office', name: 'Office' } },
  ];
  const singlePrinterCamera: DisplayCameraDto = {
    ...camera,
    isStandalone: false,
    printerId: 'p1',
    printerIds: ['p1'],
    printers: [{ id: 'p1', name: 'Voron', isPrimary: true }],
    effectiveLocationId: 'loc-shop',
    effectiveLocationName: 'Workshop',
    isLocationDerived: true,
  };

  beforeEach(() => {
    printersState.data = printers;
  });

  it('derives and disables the location when exactly one printer is linked', async () => {
    await loadModal(singlePrinterCamera);
    const location = screen.getByLabelText('Location');
    expect(location).toBeDisabled();
    expect(location).toHaveValue('Workshop');
    expect(screen.getByText('Location follows Voron')).toBeInTheDocument();
    expect(screen.getByLabelText('Voron')).toBeChecked();
    expect(screen.getByLabelText('Bambu')).not.toBeChecked();
  });

  it('shows the newly selected printer location when switching the single linked printer', async () => {
    await loadModal(singlePrinterCamera);
    fireEvent.click(screen.getByLabelText('Voron'));
    fireEvent.click(screen.getByLabelText('Bambu'));
    expect(screen.getByLabelText('Location')).toHaveValue('Office');
    expect(screen.getByText('Location follows Bambu')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Save Camera' }));
    await waitFor(() => expect(service.updateCamera).toHaveBeenCalled());
    const request = service.updateCamera.mock.calls[0][1];
    expect(request.printerIds).toEqual(['p2']);
    expect(request).not.toHaveProperty('locationId');
    expect(request).not.toHaveProperty('clearLocationId');
    expect(request).not.toHaveProperty('printerId');
    expect(request).not.toHaveProperty('location');
  });

  it('makes the location settable for an unlinked camera and sends the chosen locationId', async () => {
    await loadModal();
    const location = await screen.findByRole('combobox', { name: 'Location' });
    expect(location).toBeEnabled();
    await screen.findByRole('option', { name: 'Workshop' });
    fireEvent.change(location, { target: { value: 'loc-shop' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Camera' }));
    await waitFor(() => expect(service.updateCamera).toHaveBeenCalled());
    const request = service.updateCamera.mock.calls[0][1];
    expect(request.locationId).toBe('loc-shop');
    expect(request).not.toHaveProperty('printerIds');
  });

  it('sends clearLocationId when an explicit location is cleared', async () => {
    await loadModal({ ...camera, locationId: 'loc-office', effectiveLocationId: 'loc-office', effectiveLocationName: 'Office' });
    const location = screen.getByRole('combobox', { name: 'Location' });
    await screen.findByRole('option', { name: 'Workshop' });
    expect(location).toHaveValue('loc-office');
    fireEvent.change(location, { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Camera' }));
    await waitFor(() => expect(service.updateCamera).toHaveBeenCalled());
    const request = service.updateCamera.mock.calls[0][1];
    expect(request.clearLocationId).toBe(true);
    expect(request).not.toHaveProperty('locationId');
  });

  it('links several printers with the chosen primary first and a settable location', async () => {
    await loadModal(singlePrinterCamera);
    fireEvent.click(screen.getByLabelText('Bambu'));

    const location = screen.getByRole('combobox', { name: 'Location' });
    expect(location).toBeEnabled();
    expect(location).toHaveValue('loc-shop');
    fireEvent.change(screen.getByLabelText('Primary Printer'), { target: { value: 'p2' } });
    fireEvent.change(location, { target: { value: 'loc-office' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Camera' }));

    await waitFor(() => expect(service.updateCamera).toHaveBeenCalled());
    const request = service.updateCamera.mock.calls[0][1];
    expect(request.printerIds).toEqual(['p2', 'p1']);
    expect(request.locationId).toBe('loc-office');
  });

  const multiPrinterCamera: DisplayCameraDto = {
    ...singlePrinterCamera,
    printerIds: ['p2', 'p1'],
    printers: [
      { id: 'p2', name: 'Bambu', isPrimary: true },
      { id: 'p1', name: 'Voron', isPrimary: false },
    ],
    locationId: 'loc-office',
    effectiveLocationId: 'loc-office',
    effectiveLocationName: 'Office',
    isLocationDerived: false,
  };

  it('keeps every linked printer and the primary when saving unrelated edits', async () => {
    await loadModal(multiPrinterCamera);
    expect(screen.getByLabelText('Voron')).toBeChecked();
    expect(screen.getByLabelText('Bambu')).toBeChecked();
    expect(screen.getByLabelText('Primary Printer')).toHaveValue('p2');
    fireEvent.change(screen.getByLabelText(/Camera Name/), { target: { value: 'Renamed' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Camera' }));
    await waitFor(() => expect(service.updateCamera).toHaveBeenCalled());
    const request = service.updateCamera.mock.calls[0][1];
    expect(request).not.toHaveProperty('printerIds');
    expect(request).not.toHaveProperty('printerId');
    expect(request).not.toHaveProperty('clearLocationId');
  });

  it('sends the full printer set when another printer is linked', async () => {
    printersState.data = [...printers, { id: 'p3', name: 'Prusa' }];
    await loadModal(multiPrinterCamera);
    fireEvent.click(screen.getByLabelText('Prusa'));
    fireEvent.click(screen.getByRole('button', { name: 'Save Camera' }));
    await waitFor(() => expect(service.updateCamera).toHaveBeenCalled());
    expect(service.updateCamera.mock.calls[0][1].printerIds).toEqual(['p2', 'p1', 'p3']);
  });

  it('shows the derived location read-only whenever the server reports it as derived', async () => {
    await loadModal({ ...multiPrinterCamera, isLocationDerived: true, effectiveLocationName: 'Office' });
    expect(screen.getByLabelText('Location')).toBeDisabled();
    expect(screen.getByLabelText('Location')).toHaveValue('Office');
    fireEvent.change(screen.getByLabelText(/Camera Name/), { target: { value: 'Renamed' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Camera' }));
    await waitFor(() => expect(service.updateCamera).toHaveBeenCalled());
    const request = service.updateCamera.mock.calls[0][1];
    expect(request).not.toHaveProperty('locationId');
    expect(request).not.toHaveProperty('clearLocationId');
  });

  it('prevents unlinking the locked printer when opened from Edit Printer', async () => {
    await loadModal(multiPrinterCamera, 'p1');
    const locked = screen.getByLabelText('Voron');
    expect(locked).toBeChecked();
    expect(locked).toBeDisabled();
    expect(screen.getByText(/Voron stays linked while editing from that printer/)).toBeInTheDocument();
    expect(screen.getByLabelText('Bambu')).toBeEnabled();
    fireEvent.click(screen.getByLabelText('Bambu'));
    fireEvent.click(screen.getByRole('button', { name: 'Save Camera' }));
    await waitFor(() => expect(service.updateCamera).toHaveBeenCalled());
    expect(service.updateCamera.mock.calls[0][1].printerIds).toEqual(['p1']);
  });
});
