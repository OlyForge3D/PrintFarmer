import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { EditPrinterModal } from '../EditPrinterModal';
import { PrinterBackend } from '@/types/api';

const mockUsePrinterDetails = vi.fn();
const mockUseUpdatePrinter = vi.fn();
const mockUsePrinterCameras = vi.fn();
const testConnection = vi.fn();

const { mockToast } = vi.hoisted(() => ({
  mockToast: { success: vi.fn(), error: vi.fn(), dismiss: vi.fn() },
}));

vi.mock('sonner', () => ({ toast: mockToast }));

vi.mock('@/services/api', () => ({
  apiClient: {
    testConnection: (...args: unknown[]) => testConnection(...args),
  },
}));

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
        rowVersion: 'printer-v1',
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

  it('displays the configured server URL', async () => {
    render(
      <EditPrinterModal
        printerId="printer-1"
        isOpen
        onClose={vi.fn()}
        onSuccess={vi.fn()}
      />
    );

    expect(await screen.findByDisplayValue('http://qp4-1.local')).toBeInTheDocument();
  });

  it('exposes a unique id for the printer-name input when a toolhead shares the "Name" label', async () => {
    mockUsePrinterDetails.mockReturnValue({
      data: {
        rowVersion: 'printer-v1',
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
        capabilities: {},
        backendPort: 7125,
        frontendPort: 80,
        obicoEnabled: false,
        toolheads: [
          { id: 'th-0', index: 0, name: 'Toolhead 1', isPrimary: true },
        ],
      },
    });

    const { container } = render(
      <EditPrinterModal
        printerId="printer-1"
        isOpen
        onClose={vi.fn()}
        onSuccess={vi.fn()}
      />
    );

    // The primary toolhead auto-expands, so its own "Name" field (id
    // `toolhead-name-0`) renders alongside the printer's "Name" field —
    // both labels start with "Name", so a substring lookup like Playwright's
    // default `getByLabel('Name')` is ambiguous. The printer-name input must
    // remain uniquely addressable by its own id regardless of how many
    // toolhead "Name" fields exist.
    await screen.findByDisplayValue('http://qp4-1.local');
    expect(screen.getAllByLabelText('Name', { exact: false })).toHaveLength(2);
    expect(container.querySelector<HTMLInputElement>('#edit-printer-name')?.value).toBe('qp4-1');
  });

  it('reveals the stored PrusaLink password when the eye button is activated', async () => {
    const user = userEvent.setup();
    mockUsePrinterDetails.mockReturnValue({
      data: {
        rowVersion: 'printer-v1',
        name: 'prusalink-1',
        serverUrl: 'http://prusalink-1.local',
        originalServerUrl: 'http://prusalink-1.local',
        notes: '',
        manufacturerId: 'manufacturer-1',
        modelId: 'model-1',
        backend: 'PrusaLink',
        apiKey: '',
        username: 'maker',
        password: 'stored-printer-password',
        capabilities: {},
        backendPort: 80,
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

    const passwordInput = await screen.findByTitle('PrusaLink password from printer settings');
    expect(passwordInput).toHaveAttribute('type', 'password');
    expect(passwordInput).toHaveValue('stored-printer-password');

    await user.click(screen.getByRole('button', { name: 'Show password' }));

    expect(passwordInput).toHaveAttribute('type', 'text');
    expect(passwordInput).toHaveValue('stored-printer-password');
  });

  it('rejects a non-HTTP server URL', async () => {
    const user = userEvent.setup();

    render(
      <EditPrinterModal
        printerId="printer-1"
        isOpen
        onClose={vi.fn()}
        onSuccess={vi.fn()}
      />
    );

    const serverUrl = await screen.findByDisplayValue('http://qp4-1.local');
    await user.clear(serverUrl);
    await user.type(serverUrl, 'javascript:alert(1)');
    await user.click(screen.getByRole('button', { name: /save changes/i }));

    expect(screen.getByText('Please enter a valid HTTP/HTTPS URL')).toBeInTheDocument();
    expect(mutateAsync).not.toHaveBeenCalled();
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
        reviewedRowVersion: 'printer-v1',
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

  // #1865: a failed connection test (e.g. an unreachable/rejected Moonraker URL) must
  // surface the backend's actual rejection reason to the user, and must keep doing so
  // on every retry rather than silently no-op'ing or collapsing to a generic message.
  describe('Test connection feedback (#1865)', () => {
    it('surfaces the backend rejection message via an error toast on test failure, repeatedly', async () => {
      const user = userEvent.setup();
      // apiClient rejects with the shared ApiError shape built by the Axios response
      // interceptor (see src/services/api.ts) — not a raw Error instance.
      testConnection.mockRejectedValue({
        message: 'The requested server address is not allowed.',
        statusCode: 400,
        data: { success: false, message: 'The requested server address is not allowed.' },
        isAxiosError: true,
      });

      render(
        <EditPrinterModal
          printerId="printer-1"
          isOpen
          onClose={vi.fn()}
          onSuccess={vi.fn()}
        />
      );

      const testButton = await screen.findByRole('button', { name: /test/i });

      await user.click(testButton);
      await waitFor(() => {
        expect(mockToast.error).toHaveBeenCalledWith(
          'The requested server address is not allowed.',
          expect.objectContaining({ duration: 8000 })
        );
      });

      // Retrying the test must keep surfacing feedback, not silently no-op.
      await user.click(testButton);
      await waitFor(() => expect(mockToast.error).toHaveBeenCalledTimes(2));
      expect(mockToast.error).toHaveBeenNthCalledWith(
        2,
        'The requested server address is not allowed.',
        expect.objectContaining({ duration: 8000 })
      );
    });
  });
});
