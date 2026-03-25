import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { EditPrinterModal } from '../EditPrinterModal';
import { PrinterBackend } from '@/types/api';

const mockUsePrinterDetails = vi.fn();
const mockUseUpdatePrinter = vi.fn();

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

  it('disables Obico checkbox when no camera URLs are configured', async () => {
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
        cameraStreamUrl: '', // No camera URL
        cameraSnapshotUrl: '', // No camera URL
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
    expect(screen.getByText(/configure a camera url above to enable failure detection/i)).toBeInTheDocument();
  });

  it('enables Obico checkbox when at least one camera URL is configured', async () => {
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
        cameraStreamUrl: 'http://qp4-1.local/webcam/?action=stream', // Camera URL present
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

  it('enables Obico checkbox when snapshot URL is configured', async () => {
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
        cameraSnapshotUrl: 'http://qp4-1.local/webcam/?action=snapshot', // Snapshot URL present
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
});
