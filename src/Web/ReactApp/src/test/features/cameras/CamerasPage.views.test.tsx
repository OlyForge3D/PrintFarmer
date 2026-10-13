import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CamerasPage } from '@/features/cameras/pages/CamerasPage';
import { CameraHealthStatus, CameraSource, CameraType } from '@/types/api';
import type { DisplayCameraDto } from '@/types/api';

const { cameraServiceMock, streamMock } = vi.hoisted(() => ({
  cameraServiceMock: { getDisplayCameras: vi.fn() },
  streamMock: { useAuthenticatedMjpegStream: vi.fn() },
}));
vi.mock('@/services/cameraService', () => ({ cameraService: cameraServiceMock }));
vi.mock('@/services/api', () => ({ apiClient: { getSnapshotPreview: vi.fn() } }));
vi.mock('@/features/cameras/hooks/useAuthenticatedMjpegStream', () => ({
  useAuthenticatedMjpegStream: streamMock.useAuthenticatedMjpegStream,
}));
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => false }),
}));
vi.mock('@/features/cameras/components/EditCameraModal', () => ({ EditCameraModal: () => null }));
vi.mock('@/features/cameras/components/CameraManagementPanel', () => ({ CameraManagementPanel: () => null }));
vi.mock('@/common/components/modals/ConfirmationModal', () => ({ ConfirmationModal: () => null }));

function camera(overrides: Partial<DisplayCameraDto> & Pick<DisplayCameraDto, 'id' | 'name'>): DisplayCameraDto {
  return {
    isEnabled: true,
    sortOrder: 0,
    isStandalone: true,
    printerIds: [],
    printers: [],
    isLocationDerived: false,
    source: CameraSource.Standalone,
    cameraType: CameraType.General,
    healthStatus: CameraHealthStatus.Healthy,
    ...overrides,
  };
}

const cameras: DisplayCameraDto[] = [
  camera({
    id: 'c1',
    name: 'Voron Nozzle',
    sortOrder: 1,
    printerIds: ['p1'],
    printers: [{ id: 'p1', name: 'Voron', isPrimary: true }],
    effectiveLocationId: 'loc-w',
    effectiveLocationName: 'Workshop',
    isLocationDerived: true,
  }),
  camera({
    id: 'c2',
    name: 'Room Overview',
    sortOrder: 2,
    printerIds: ['p1', 'p2'],
    printers: [
      { id: 'p1', name: 'Voron', isPrimary: false },
      { id: 'p2', name: 'Bambu', isPrimary: true },
    ],
    locationId: 'loc-w',
    effectiveLocationId: 'loc-w',
    effectiveLocationName: 'Workshop',
  }),
  camera({ id: 'c3', name: 'Door Webcam', sortOrder: 3 }),
];

function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location-search">{location.search}</div>;
}

function renderPage(initialEntry = '/cameras') {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <CamerasPage />
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

const cameraNamesIn = (element: HTMLElement) =>
  within(element).queryAllByRole('article').map((article) => article.getAttribute('aria-label'));

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal('IntersectionObserver', undefined);
  cameraServiceMock.getDisplayCameras.mockReset().mockResolvedValue(cameras);
  streamMock.useAuthenticatedMjpegStream.mockReset().mockReturnValue({
    streamSrc: null,
    streamUnsupported: false,
    streamFailed: false,
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('CamerasPage views', () => {
  it('defaults to the flat All view with an accessible view switcher', async () => {
    renderPage();

    expect(await screen.findByRole('article', { name: 'Voron Nozzle camera' })).toBeInTheDocument();
    const switcher = screen.getByRole('group', { name: 'Camera view' });
    expect(within(switcher).getByRole('button', { name: 'All' })).toHaveAttribute('aria-pressed', 'true');
    expect(within(switcher).getByRole('button', { name: 'By location' })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getAllByRole('article')).toHaveLength(3);
    expect(screen.queryByRole('heading', { level: 2 })).not.toBeInTheDocument();
  });

  it('groups by location with an Unassigned group when ?view=location', async () => {
    renderPage('/cameras?view=location');

    const workshop = await screen.findByRole('region', { name: /Workshop/ });
    expect(cameraNamesIn(workshop)).toEqual(['Voron Nozzle camera', 'Room Overview camera']);
    const unassigned = screen.getByRole('region', { name: /Unassigned/ });
    expect(cameraNamesIn(unassigned)).toEqual(['Door Webcam camera']);
    expect(screen.getByRole('heading', { level: 2, name: /Workshop/ })).toBeInTheDocument();
  });

  it('lists a multi-printer camera under every linked printer when ?view=printer', async () => {
    renderPage('/cameras?view=printer');

    const bambu = await screen.findByRole('region', { name: /Bambu/ });
    expect(cameraNamesIn(bambu)).toEqual(['Room Overview camera']);
    expect(cameraNamesIn(screen.getByRole('region', { name: /Voron/ }))).toEqual([
      'Voron Nozzle camera',
      'Room Overview camera',
    ]);
    expect(cameraNamesIn(screen.getByRole('region', { name: /Not linked to a printer/ }))).toEqual([
      'Door Webcam camera',
    ]);
  });

  it('switches views through the URL while preserving other params', async () => {
    renderPage('/cameras?foo=bar');
    await screen.findByRole('article', { name: 'Door Webcam camera' });

    fireEvent.click(screen.getByRole('button', { name: 'By printer' }));
    expect(screen.getByTestId('location-search')).toHaveTextContent('?foo=bar&view=printer');
    expect(screen.getByRole('button', { name: 'By printer' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('region', { name: /Not linked to a printer/ })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'All' }));
    expect(screen.getByTestId('location-search')).toHaveTextContent(/^\?foo=bar$/);
    expect(screen.queryByRole('region', { name: /Not linked to a printer/ })).not.toBeInTheDocument();
  });

  it('falls back to the All view for an unknown ?view value', async () => {
    renderPage('/cameras?view=bogus');
    await screen.findByRole('article', { name: 'Door Webcam camera' });
    expect(screen.getByRole('button', { name: 'All' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.queryByRole('region')).not.toBeInTheDocument();
  });
});
