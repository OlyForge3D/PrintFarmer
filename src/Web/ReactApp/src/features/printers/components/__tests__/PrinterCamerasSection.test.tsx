import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { PrinterCamerasSection } from '../PrinterCamerasSection';
import { CameraHealthStatus, CameraSource, CameraType, type CameraDto } from '@/types/api';

const { mockToast, cameraServiceMock, printerServiceMock, hasPermission } = vi.hoisted(() => ({
  mockToast: { success: vi.fn(), error: vi.fn() },
  cameraServiceMock: {
    getAllCameras: vi.fn(),
    createCamera: vi.fn(),
    linkCameraToPrinter: vi.fn(),
    removeCameraFromPrinter: vi.fn(),
  },
  printerServiceMock: { discoverCameras: vi.fn() },
  hasPermission: vi.fn<(resource: string, action: string) => boolean>(() => true),
}));

vi.mock('sonner', () => ({ toast: mockToast }));
vi.mock('@/services/cameraService', () => ({ cameraService: cameraServiceMock }));
vi.mock('@/services/printerService', () => ({ printerService: printerServiceMock }));
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: () => ({ hasPermission }) }));
vi.mock('@/features/cameras/components/EditCameraModal', () => ({
  EditCameraModal: ({ camera }: { camera: CameraDto }) => <div role="dialog">Editing {camera.name}</div>,
}));
vi.mock('@/common/components/modals/ConfirmationModal', () => ({
  ConfirmationModal: ({ isOpen, message, onConfirm, onCancel }: {
    isOpen: boolean; message: string; onConfirm: () => void; onCancel: () => void;
  }) => (isOpen ? (
    <div role="alertdialog">
      <p>{message}</p>
      <button type="button" onClick={onConfirm}>Confirm remove</button>
      <button type="button" onClick={onCancel}>Cancel remove</button>
    </div>
  ) : null),
}));

const PRINTER_ID = 'printer-1';

const makeCamera = (overrides: Partial<CameraDto> = {}): CameraDto => ({
  id: 'cam-1',
  name: 'Bed cam',
  isEnabled: true,
  sortOrder: 0,
  printerIds: [PRINTER_ID],
  printers: [{ id: PRINTER_ID, name: 'Voron', isPrimary: true }],
  isLocationDerived: true,
  effectiveLocationName: 'Lab',
  source: CameraSource.Moonraker,
  cameraType: CameraType.Bed,
  healthStatus: CameraHealthStatus.Healthy,
  userModified: false,
  ...overrides,
} as CameraDto);

const renderSection = () => {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
  render(
    <QueryClientProvider client={queryClient}>
      <PrinterCamerasSection printerId={PRINTER_ID} />
    </QueryClientProvider>
  );
  return { invalidate };
};

describe('PrinterCamerasSection', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    hasPermission.mockReturnValue(true);
    cameraServiceMock.getAllCameras.mockResolvedValue([
      makeCamera(),
      makeCamera({
        id: 'cam-2',
        name: 'Shared cam',
        source: CameraSource.Standalone,
        printers: [
          { id: 'other', name: 'Prusa', isPrimary: true },
          { id: PRINTER_ID, name: 'Voron', isPrimary: false },
        ],
        isLocationDerived: false,
      }),
    ]);
  });

  it('lists cameras linked to the printer with a primary badge', async () => {
    renderSection();

    const list = await screen.findByRole('list', { name: 'Linked cameras' });
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(cameraServiceMock.getAllCameras).toHaveBeenCalledWith({ printerId: PRINTER_ID });
    expect(within(items[0]).getByText('Primary')).toBeInTheDocument();
    expect(within(items[0]).getByText(/Lab \(from printer\)/)).toBeInTheDocument();
    expect(within(items[1]).queryByText('Primary')).not.toBeInTheDocument();
    expect(within(items[1]).getByText(/Shared with 1 other printer/)).toBeInTheDocument();
  });

  it('removes a camera after confirmation and reports a deletion', async () => {
    const user = userEvent.setup();
    cameraServiceMock.removeCameraFromPrinter.mockResolvedValue({ result: 'Deleted' });
    const { invalidate } = renderSection();

    await user.click(await screen.findByRole('button', { name: 'Remove camera Bed cam' }));
    expect(cameraServiceMock.removeCameraFromPrinter).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Confirm remove' }));

    await waitFor(() => expect(cameraServiceMock.removeCameraFromPrinter).toHaveBeenCalledWith(PRINTER_ID, 'cam-1'));
    await waitFor(() => expect(mockToast.success).toHaveBeenCalledWith('Camera "Bed cam" was deleted.'));
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['cameras'] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['printers', 'camera-urls'] });
  });

  it('reports an unlink when the camera is kept', async () => {
    const user = userEvent.setup();
    cameraServiceMock.removeCameraFromPrinter.mockResolvedValue({ result: 'Unlinked' });
    renderSection();

    await user.click(await screen.findByRole('button', { name: 'Remove camera Shared cam' }));
    await user.click(screen.getByRole('button', { name: 'Confirm remove' }));

    await waitFor(() => expect(mockToast.success).toHaveBeenCalledWith('Camera "Shared cam" was unlinked from this printer.'));
  });

  it('discovers cameras from the printer backend', async () => {
    const user = userEvent.setup();
    printerServiceMock.discoverCameras.mockResolvedValue([makeCamera()]);
    renderSection();

    await user.click(await screen.findByRole('button', { name: 'Discover cameras' }));

    await waitFor(() => expect(printerServiceMock.discoverCameras).toHaveBeenCalledWith(PRINTER_ID));
    await waitFor(() => expect(mockToast.success).toHaveBeenCalledWith('Discovered 1 camera.'));
  });

  it('adds a manual camera linked only to this printer without a location field', async () => {
    const user = userEvent.setup();
    cameraServiceMock.createCamera.mockResolvedValue(makeCamera({ id: 'cam-3', name: 'Nozzle cam' }));
    renderSection();

    await user.click(await screen.findByRole('button', { name: 'Add camera' }));
    expect(screen.queryByLabelText(/location/i)).not.toBeInTheDocument();
    await user.type(screen.getByLabelText(/camera name/i), 'Nozzle cam');
    await user.type(screen.getByLabelText('Snapshot URL'), 'http://cam.local/snap.jpg');
    await user.click(screen.getByRole('button', { name: 'Save camera' }));

    await waitFor(() => expect(cameraServiceMock.createCamera).toHaveBeenCalledTimes(1));
    const request = cameraServiceMock.createCamera.mock.calls[0][0];
    expect(request).toEqual(expect.objectContaining({
      name: 'Nozzle cam',
      snapshotUrl: 'http://cam.local/snap.jpg',
      printerIds: [PRINTER_ID],
    }));
    expect(request).not.toHaveProperty('locationId');
  });

  it('links an existing camera that is not already linked', async () => {
    const user = userEvent.setup();
    cameraServiceMock.getAllCameras.mockImplementation((filter?: { printerId?: string }) =>
      Promise.resolve(filter?.printerId
        ? [makeCamera()]
        : [makeCamera(), makeCamera({ id: 'cam-9', name: 'Room cam', printers: [], printerIds: [] })])
    );
    cameraServiceMock.linkCameraToPrinter.mockResolvedValue(makeCamera({ id: 'cam-9', name: 'Room cam' }));
    renderSection();

    await user.click(await screen.findByRole('button', { name: 'Link existing camera' }));
    const select = await screen.findByLabelText('Camera');
    expect(within(select).queryByRole('option', { name: 'Bed cam' })).not.toBeInTheDocument();
    await user.selectOptions(select, 'cam-9');
    await user.click(screen.getByRole('button', { name: 'Link camera' }));

    await waitFor(() => expect(cameraServiceMock.linkCameraToPrinter).toHaveBeenCalledWith('cam-9', PRINTER_ID));
  });

  it('hides management actions without admin permissions', async () => {
    hasPermission.mockReturnValue(false);
    renderSection();

    await screen.findByRole('list', { name: 'Linked cameras' });
    expect(screen.queryByRole('button', { name: 'Discover cameras' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add camera' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Remove camera/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Edit camera/ })).not.toBeInTheDocument();
  });
});
