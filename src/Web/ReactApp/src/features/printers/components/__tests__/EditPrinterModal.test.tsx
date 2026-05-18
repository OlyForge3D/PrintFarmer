import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { EditPrinterModal } from '../EditPrinterModal';
import { PrinterBackend } from '@/types/api';

const mockUsePrinterDetails = vi.fn();
const mockUseUpdatePrinter = vi.fn();
const mockUsePrinterCameras = vi.fn();

vi.mock('@/common/hooks/useApi', () => ({
  usePrinterDetails: (...args: unknown[]) => mockUsePrinterDetails(...args),
  useUpdatePrinter: (...args: unknown[]) => mockUseUpdatePrinter(...args),
  useManufacturers: () => ({ data: [] }),
  useModels: () => ({ data: [] }),
  useFilamentTypes: () => ({ data: [] }),
  useModelDefaultCapabilities: () => ({ data: undefined, isLoading: false }),
  useHotendModels: () => ({ data: [] }),
  useExtruderModels: () => ({ data: [] }),
  useToolheadModels: () => ({ data: [] }),
  useNozzleModels: () => ({ data: [] }),
}));

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({ isSlicerAvailable: false }),
}));

vi.mock('@/features/cameras/hooks/usePrinterCameras', () => ({
  usePrinterCameras: (...args: unknown[]) => mockUsePrinterCameras(...args),
}));

vi.mock('@/common/components/modals/Modal', () => ({
  Modal: ({ isOpen, title, footer, children }: { isOpen: boolean; title: string; footer?: React.ReactNode; children: React.ReactNode }) => (
    isOpen ? (
      <div>
        <h1>{title}</h1>
        {children}
        {footer}
      </div>
    ) : null
  ),
}));

vi.mock('@/features/slicer/components/CloneProfilesModal', () => ({
  CloneProfilesModal: () => null,
}));

describe('EditPrinterModal', () => {
  const mutateAsync = vi.fn().mockResolvedValue({ name: 'qp4-1' });

  beforeEach(() => {
    vi.clearAllMocks();

    mockUsePrinterCameras.mockReturnValue({
      data: [
        {
          id: 'camera-1',
          streamUrl: 'http://qp4-1.local/cam/stream',
          snapshotUrl: '',
          isEnabled: true,
        },
      ],
    });

    mockUsePrinterDetails.mockReturnValue({
      data: {
        name: 'qp4-1',
        serverUrl: 'http://qp4-1.local',
        originalServerUrl: 'http://qp4-1.local',
        notes: '',
        manufacturerId: 'manufacturer-1',
        modelId: 'model-1',
        backend: 'Moonraker',
        apiKey: '',
        username: '',
        password: '',
        cameraStreamUrl: 'http://qp4-1.local/webcam/?action=stream',
        cameraSnapshotUrl: 'http://qp4-1.local/webcam/?action=snapshot',
        capabilities: {},
        backendPort: 7125,
        frontendPort: 80,
        obicoEnabled: false,
        toolheads: [],
      },
    });

    mockUseUpdatePrinter.mockReturnValue({
      status: 'idle',
      mutateAsync,
    });
  });

  it('enables save when only Obico monitoring is toggled', async () => {
    const user = userEvent.setup();

    render(
      <EditPrinterModal
        printerId="printer-1"
        isOpen
        onClose={vi.fn()}
        onSuccess={vi.fn()}
      />
    );

    const saveButton = await screen.findByRole('button', { name: /save changes/i });
    expect(saveButton).toBeDisabled();

    await user.click(screen.getByLabelText(/enable obico monitoring for this printer/i));

    await waitFor(() => {
      expect(saveButton).toBeEnabled();
    });
  });

  it('submits obicoEnabled when saving after toggle', async () => {
    const user = userEvent.setup();

    render(
      <EditPrinterModal
        printerId="printer-1"
        isOpen
        onClose={vi.fn()}
        onSuccess={vi.fn()}
      />
    );

    await user.click(await screen.findByLabelText(/enable obico monitoring for this printer/i));
    await user.click(screen.getByRole('button', { name: /save changes/i }));

    await waitFor(() => {
      expect(mutateAsync).toHaveBeenCalledWith({
        id: 'printer-1',
        printer: expect.objectContaining({
          backend: PrinterBackend.Moonraker,
          obicoEnabled: true,
        }),
      });
    });
  });

  it('disables Obico checkbox when no linked cameras are configured', async () => {
    mockUsePrinterCameras.mockReturnValue({
      data: [],
    });

    mockUsePrinterDetails.mockReturnValue({
      data: {
        name: 'qp4-1',
        serverUrl: 'http://qp4-1.local',
        originalServerUrl: 'http://qp4-1.local',
        notes: '',
        manufacturerId: 'manufacturer-1',
        modelId: 'model-1',
        backend: 'Moonraker',
        apiKey: '',
        username: '',
        password: '',
        cameraStreamUrl: '',
        cameraSnapshotUrl: '',
        capabilities: {},
        backendPort: 7125,
        frontendPort: 80,
        obicoEnabled: false,
        toolheads: [],
      },
    });

    render(
      <EditPrinterModal
        printerId="printer-1"
        isOpen
        onClose={vi.fn()}
        onSuccess={vi.fn()}
      />
    );

    const checkbox = await screen.findByLabelText(/enable obico monitoring for this printer/i);
    expect(checkbox).toBeDisabled();
    expect(screen.getByText(/configure and enable a linked camera/i)).toBeInTheDocument();
  });

  it('enables Obico checkbox when at least one enabled linked camera is configured', async () => {
    mockUsePrinterDetails.mockReturnValue({
      data: {
        name: 'qp4-1',
        serverUrl: 'http://qp4-1.local',
        originalServerUrl: 'http://qp4-1.local',
        notes: '',
        manufacturerId: 'manufacturer-1',
        modelId: 'model-1',
        backend: 'Moonraker',
        apiKey: '',
        username: '',
        password: '',
        cameraStreamUrl: 'http://qp4-1.local/webcam/?action=stream',
        cameraSnapshotUrl: '',
        capabilities: {},
        backendPort: 7125,
        frontendPort: 80,
        obicoEnabled: false,
        toolheads: [],
      },
    });

    render(
      <EditPrinterModal
        printerId="printer-1"
        isOpen
        onClose={vi.fn()}
        onSuccess={vi.fn()}
      />
    );

    const checkbox = await screen.findByLabelText(/enable obico monitoring for this printer/i);
    expect(checkbox).not.toBeDisabled();
  });

  it('disables Obico checkbox when only disabled linked cameras exist', async () => {
    mockUsePrinterCameras.mockReturnValue({
      data: [
        {
          id: 'camera-1',
          streamUrl: 'http://qp4-1.local/cam/stream',
          snapshotUrl: '',
          isEnabled: false,
        },
      ],
    });

    render(
      <EditPrinterModal
        printerId="printer-1"
        isOpen
        onClose={vi.fn()}
        onSuccess={vi.fn()}
      />
    );

    const checkbox = await screen.findByLabelText(/enable obico monitoring for this printer/i);
    expect(checkbox).toBeDisabled();
  });

  it('enables Obico checkbox when linked camera exists even without populated legacy camera fields', async () => {
    mockUsePrinterDetails.mockReturnValue({
      data: {
        name: 'arco',
        serverUrl: 'http://arco.local',
        originalServerUrl: 'http://arco.local',
        notes: '',
        manufacturerId: 'manufacturer-1',
        modelId: 'model-1',
        backend: 'Moonraker',
        apiKey: '',
        username: '',
        password: '',
        cameraStreamUrl: '',
        cameraSnapshotUrl: '',
        capabilities: {},
        backendPort: 7125,
        frontendPort: 80,
        obicoEnabled: false,
        toolheads: [],
      },
    });

    mockUsePrinterCameras.mockReturnValue({
      data: [
        {
          id: 'camera-1',
          streamUrl: 'http://arco.local/cam/stream',
          snapshotUrl: '',
          isEnabled: true,
        },
      ],
    });

    render(
      <EditPrinterModal
        printerId="printer-1"
        isOpen
        onClose={vi.fn()}
        onSuccess={vi.fn()}
      />
    );

    const checkbox = await screen.findByLabelText(/enable obico monitoring for this printer/i);
    expect(checkbox).not.toBeDisabled();
  });

  it('keeps Obico checkbox interactive when already enabled without currently eligible linked cameras', async () => {
    mockUsePrinterDetails.mockReturnValue({
      data: {
        name: 'qp4-1',
        serverUrl: 'http://qp4-1.local',
        originalServerUrl: 'http://qp4-1.local',
        notes: '',
        manufacturerId: 'manufacturer-1',
        modelId: 'model-1',
        backend: 'Moonraker',
        apiKey: '',
        username: '',
        password: '',
        cameraStreamUrl: '',
        cameraSnapshotUrl: '',
        capabilities: {},
        backendPort: 7125,
        frontendPort: 80,
        obicoEnabled: true,
        toolheads: [],
      },
    });

    render(
      <EditPrinterModal
        printerId="printer-1"
        isOpen
        onClose={vi.fn()}
        onSuccess={vi.fn()}
      />
    );

    const checkbox = await screen.findByLabelText(/enable obico monitoring for this printer/i);
    expect(checkbox).not.toBeDisabled();
    expect(checkbox).toBeChecked();
  });
});
