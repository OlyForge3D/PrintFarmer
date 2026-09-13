import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ZOffsetCalibrationWizard } from '../ZOffsetCalibrationWizard';
import type { Printer } from '@/types/api';
import type { PrinterControlIntent, PrinterControlOperation, PrinterStatus } from '@/types/api';
import { AuthContext } from '@/common/contexts/auth-context';
import type { AuthContextType } from '@/contexts/AuthContextValue';

const mockHomePrinter = vi.fn();
const mockMovePrinterTo = vi.fn();
const mockSaveZOffset = vi.fn();
const mockCreateOperation = vi.fn();
const mockGetPrinter = vi.fn();
const mockGetPrinters = vi.fn();
const mockGetPrinterStatus = vi.fn();
let completedOperation: PrinterControlOperation | null = null;
let completionState: 'Succeeded' | 'Running' | 'Recovered' = 'Succeeded';
const auth = {
  isAuthenticated: true, user: { id: 'calibration-user' },
  hasRole: () => false, hasPermission: () => false,
} as unknown as AuthContextType;

vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    isConnected: true, onControlOperationUpdated: () => vi.fn(), onConnectionStateChange: () => vi.fn(),
    subscribeToPrinter: vi.fn().mockResolvedValue(undefined),
  },
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    homePrinter: (...args: unknown[]) => mockHomePrinter(...args),
    movePrinterTo: (...args: unknown[]) => mockMovePrinterTo(...args),
    saveZOffset: (...args: unknown[]) => mockSaveZOffset(...args),
    getPrinter: (...args: unknown[]) => mockGetPrinter(...args),
    getPrinters: (...args: unknown[]) => mockGetPrinters(...args),
    getPrinterStatus: (...args: unknown[]) => mockGetPrinterStatus(...args),
    createPrinterControlOperation: (...args: unknown[]) => mockCreateOperation(...args),
    getPrinterControlOperation: async () => ({ operation: completedOperation, etag: '"v1"' }),
    getCurrentPrinterControlOperation: async () => ({
      physicalControl: {
        supportedOperations: ['HomeAll', 'HomeXY', 'HomeZ', 'Jog', 'MoveTo'],
        barrierHeld: completedOperation?.barrierHeld ?? false, requiresRecovery: false,
        operationId: completedOperation?.barrierHeld ? completedOperation.operationId : null,
        state: completedOperation?.barrierHeld ? completedOperation.state : null,
      }, operation: completedOperation?.barrierHeld ? completedOperation : null,
    }),
  },
}));

vi.mock('@/common/hooks/useApi', () => ({
  queryKeys: {
    printers: ['printers'],
    printerDetails: (id: string) => ['printers', id, 'details'],
  },
}));

vi.mock('@/common/components/modals/Modal', () => ({
  Modal: ({ isOpen, title, footer, children }: { isOpen: boolean; title: string; footer?: React.ReactNode; children: React.ReactNode }) => (
    isOpen ? (
      <div data-testid="modal">
        <h1>{title}</h1>
        {children}
        {footer}
      </div>
    ) : null
  ),
}));

function createTestPrinter(overrides: Partial<Printer> = {}): Printer {
  return {
    id: '11111111-1111-4111-8111-111111111111',
    name: 'Test Printer',
    backend: 'Moonraker' as unknown as Printer['backend'],
    isOnline: true,
    isEnabled: true,
    inMaintenance: false,
    state: 'Idle',
    serverUrl: 'http://test.local',
    rowVersion: 'printer-v1',
    ...overrides,
  } as Printer;
}

function freshStatus(): PrinterStatus {
  return {
    id: createTestPrinter().id, isOnline: true, state: 'Idle',
    safetyTelemetry: {
      homedAxes: { value: ['X', 'y', 'Z'], observedAtUtc: new Date().toISOString(), staleAfterSeconds: 15, source: 'backend.status.homedAxes' },
    },
  } as PrinterStatus;
}

function renderWizard(props: { isOpen?: boolean; printer?: Printer } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onClose = vi.fn();
  const printer = props.printer ?? createTestPrinter();

  return {
    onClose,
    ...render(
      <QueryClientProvider client={queryClient}>
        <AuthContext.Provider value={auth}>
        <ZOffsetCalibrationWizard
          isOpen={props.isOpen ?? true}
          onClose={onClose}
          printer={printer}
        />
        </AuthContext.Provider>
      </QueryClientProvider>,
    ),
  };
}

describe('ZOffsetCalibrationWizard', () => {
  afterEach(() => vi.useRealTimers());
  beforeEach(() => {
    vi.clearAllMocks();
    mockHomePrinter.mockResolvedValue({ success: true });
    mockMovePrinterTo.mockResolvedValue({ success: true });
    mockSaveZOffset.mockResolvedValue({ success: true });
    mockGetPrinters.mockImplementation(async () => [createTestPrinter()]);
    mockGetPrinterStatus.mockImplementation(async () => freshStatus());
    localStorage.clear();
    localStorage.setItem('auth-token', crypto.randomUUID());
    completedOperation = null;
    completionState = 'Succeeded';
    mockCreateOperation.mockImplementation(async (printerId: string, operationId: string, intent: PrinterControlIntent) => {
      completedOperation = {
        printerId, operationId, kind: intent.kind, x: intent.x ?? null, y: intent.y ?? null, z: intent.z ?? null, f: intent.f ?? null,
        state: completionState, rowVersion: 'v1', barrierHeld: completionState === 'Running', requiresRecovery: false,
        completionEvidence: completionState === 'Succeeded' ? 'MotionQueueDrained' : completionState === 'Recovered' ? 'OperatorVerifiedRecovery' : 'None',
        senderIsolation: 'NotRequested', failure: null, createdAtUtc: '2026-09-12T18:00:00Z', updatedAtUtc: '2026-09-12T18:00:00Z',
        startedAtUtc: null, completedAtUtc: completionState === 'Running' ? null : new Date().toISOString(),
      };
      return { operation: completedOperation, etag: '"v1"' };
    });
  });

  it('does not render when closed', async () => {
    renderWizard({ isOpen: false });
    await act(async () => {});
    expect(screen.queryByTestId('modal')).not.toBeInTheDocument();
  });

  it('renders the introduction step initially', async () => {
    renderWizard();
    await act(async () => {});
    expect(screen.getByText('Z-Offset Calibration')).toBeInTheDocument();
    expect(screen.getByText(/this wizard will guide you/i)).toBeInTheDocument();
  });

  it('shows step progress as a progress bar', async () => {
    renderWizard();
    await act(async () => {});
    expect(screen.getByRole('progressbar')).toBeInTheDocument();
    // Step label text is split across nodes: "Step 1 of 6: Introduction"
    expect(screen.getByText(/Introduction/)).toBeInTheDocument();
  });

  it('advances to Home Axes step when Next is clicked', async () => {
    const user = userEvent.setup();
    renderWizard();

    await user.click(screen.getByRole('button', { name: /next/i }));
    expect(screen.getByRole('button', { name: /home all axes/i })).toBeInTheDocument();
  });

  it('uses durable home admission and advances only after REST completion and fresh safety checks', async () => {
    const user = userEvent.setup();
    renderWizard();

    // Go to Home Axes step
    await user.click(screen.getByRole('button', { name: /next/i }));
    await user.click(screen.getByRole('button', { name: /home all axes/i }));

    expect(mockCreateOperation).toHaveBeenCalledWith(createTestPrinter().id, expect.any(String), { kind: 'HomeAll' });
    expect(mockHomePrinter).not.toHaveBeenCalled();
    // After success, auto-advances to Move to Center
    await waitFor(() => {
      expect(screen.getByText(/move the nozzle to the center/i)).toBeInTheDocument();
    });
    expect(mockGetPrinters).toHaveBeenCalledWith(true, true);
    expect(mockGetPrinterStatus).toHaveBeenCalledWith(createTestPrinter().id);
    expect(mockGetPrinter).not.toHaveBeenCalled();
  });

  it.each([
    'missing-fact', 'legacy-axes-only', 'stale', 'future', 'before-completion',
    'missing-timestamp', 'omitted-timestamp', 'omitted-axes', 'invalid-timestamp', 'zero-ttl', 'missing-axis', 'wrong-printer', 'offline', 'printing',
  ])('does not advance on unsafe authoritative status: %s', async invalid => {
    mockGetPrinterStatus.mockImplementation(async () => {
      const status = freshStatus();
      const homed = status.safetyTelemetry!.homedAxes!;
      if (invalid === 'missing-fact') status.safetyTelemetry = null;
      if (invalid === 'legacy-axes-only') return { ...status, safetyTelemetry: null, homedAxes: 'xyz' };
      if (invalid === 'stale') homed.observedAtUtc = new Date(Date.now() - 16_000).toISOString();
      if (invalid === 'future') homed.observedAtUtc = new Date(Date.now() + 10_000).toISOString();
      if (invalid === 'before-completion') homed.observedAtUtc = new Date(Date.parse(completedOperation!.completedAtUtc!) - 1).toISOString();
      if (invalid === 'missing-timestamp') homed.observedAtUtc = null;
      if (invalid === 'omitted-timestamp') delete homed.observedAtUtc;
      if (invalid === 'omitted-axes') delete homed.value;
      if (invalid === 'invalid-timestamp') homed.observedAtUtc = 'not-a-date';
      if (invalid === 'zero-ttl') homed.staleAfterSeconds = 0;
      if (invalid === 'missing-axis') homed.value = ['x', 'y'];
      if (invalid === 'wrong-printer') status.id = 'different-printer';
      if (invalid === 'offline') status.isOnline = false;
      if (invalid === 'printing') status.state = 'Printing';
      return status;
    });
    const user = userEvent.setup();
    renderWizard();
    await user.click(screen.getByRole('button', { name: /next/i }));
    await user.click(screen.getByRole('button', { name: /home all axes/i }));
    await waitFor(() => expect(mockGetPrinterStatus).toHaveBeenCalledOnce());
    await waitFor(() => expect(screen.getByRole('button', { name: /home all axes/i })).toBeEnabled());
    expect(screen.queryByText(/move the nozzle to the center/i)).not.toBeInTheDocument();
    expect(mockCreateOperation).toHaveBeenCalledTimes(1);
  });

  it.each(['absent', 'disabled', 'maintenance', 'unknown-enabled', 'unknown-maintenance'])('does not advance with %s list configuration', async invalid => {
    mockGetPrinters.mockResolvedValue(invalid === 'absent' ? [] : [createTestPrinter({
      isEnabled: invalid === 'unknown-enabled' ? undefined : invalid !== 'disabled',
      inMaintenance: invalid === 'unknown-maintenance' ? undefined : invalid === 'maintenance',
    })]);
    const user = userEvent.setup();
    renderWizard();
    await user.click(screen.getByRole('button', { name: /next/i }));
    await user.click(screen.getByRole('button', { name: /home all axes/i }));
    await waitFor(() => expect(mockGetPrinterStatus).toHaveBeenCalledOnce());
    await waitFor(() => expect(screen.getByRole('button', { name: /home all axes/i })).toBeEnabled());
    expect(screen.queryByText(/move the nozzle to the center/i)).not.toBeInTheDocument();
  });

  it('preserves non-Moonraker calibration without the Moonraker safety contract', async () => {
    const user = userEvent.setup();
    renderWizard({ printer: createTestPrinter({ backend: 'PrusaLink' as Printer['backend'] }) });
    await user.click(screen.getByRole('button', { name: /next/i }));
    await user.click(screen.getByRole('button', { name: /home all axes/i }));
    await waitFor(() => expect(screen.getByText(/move the nozzle to the center/i)).toBeInTheDocument());
    expect(mockHomePrinter).toHaveBeenCalledOnce();
    expect(mockGetPrinterStatus).not.toHaveBeenCalled();
    expect(mockGetPrinters).not.toHaveBeenCalled();
  });

  it('does not advance calibration on 202 or after 27 seconds, only after confirmed motion completion', async () => {
      vi.useFakeTimers();
      try {
        completionState = 'Running';
        renderWizard();
        await act(async () => { await vi.advanceTimersByTimeAsync(0); });
        fireEvent.click(screen.getByRole('button', { name: /next/i }));
        fireEvent.click(screen.getByRole('button', { name: /home all axes/i }));
        await act(async () => { await vi.advanceTimersByTimeAsync(27_110); });
        expect(screen.getByRole('button', { name: /Please wait/i })).toBeDisabled();
        expect(screen.getByRole('status')).toHaveTextContent('HomeAll: Running');
        expect(screen.queryByText(/move the nozzle to the center/i)).not.toBeInTheDocument();
        expect(mockCreateOperation).toHaveBeenCalledTimes(1);
        completedOperation = { ...completedOperation!, state: 'Succeeded', barrierHeld: false, completionEvidence: 'MotionQueueDrained', completedAtUtc: new Date().toISOString() };
        fireEvent.click(screen.getByRole('button', { name: /Recheck motion status/i }));
        await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });
        expect(screen.getByText(/move the nozzle to the center/i)).toBeInTheDocument();
      } finally {
        vi.useRealTimers();
      }
    });

  it('does not advance calibration on operator Recovered', async () => {
      completionState = 'Recovered';
      const user = userEvent.setup();
      renderWizard();
      await user.click(screen.getByRole('button', { name: /next/i }));
      await user.click(screen.getByRole('button', { name: /home all axes/i }));
      await waitFor(() => expect(mockCreateOperation).toHaveBeenCalledTimes(1));
      expect(screen.queryByText(/move the nozzle to the center/i)).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: /home all axes/i })).toBeInTheDocument();
  });

  it('navigates back with the Back button', async () => {
    const user = userEvent.setup();
    renderWizard();

    // Go to step 2
    await user.click(screen.getByRole('button', { name: /next/i }));
    expect(screen.getByRole('button', { name: /home all axes/i })).toBeInTheDocument();

    // Go back — intro text should reappear
    await user.click(screen.getByRole('button', { name: /back/i }));
    expect(screen.getByText(/this wizard will guide you/i)).toBeInTheDocument();
  });

  it('navigates to Adjust Z-Offset step via action buttons', async () => {
    const user = userEvent.setup();
    renderWizard();

    // Step 1 → 2 via Next
    await user.click(screen.getByRole('button', { name: /next/i }));
    // Step 2 → 3 via Home action (auto-advance on success)
    await user.click(screen.getByRole('button', { name: /home all axes/i }));
    await waitFor(() => {
      expect(screen.getByText(/move the nozzle to the center/i)).toBeInTheDocument();
    });
    // Step 3 → 4 via Move action (auto-advance on success)
    await user.click(screen.getByRole('button', { name: /move to center/i }));
    await waitFor(() => {
      expect(screen.getByText(/Z-Offset:.*0\.000 mm/)).toBeInTheDocument();
    });

    // Verify increment buttons
    expect(screen.getByText('0.01 mm')).toBeInTheDocument();
    expect(screen.getByText('0.05 mm')).toBeInTheDocument();
    expect(screen.getByText('0.1 mm')).toBeInTheDocument();
  });

  it('adjusts Z-offset down through the typed move endpoint', async () => {
    const user = userEvent.setup();
    renderWizard();

    // Navigate to Adjust step
    await user.click(screen.getByRole('button', { name: /next/i }));
    await user.click(screen.getByRole('button', { name: /home all axes/i }));
    await waitFor(() => expect(screen.getByText(/move the nozzle/i)).toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: /move to center/i }));
    await waitFor(() => expect(screen.getByText(/Z-Offset:/)).toBeInTheDocument());

    // Click Nozzle Down — sends an absolute typed move from Z=10 offset by -0.05
    await user.click(screen.getByRole('button', { name: /nozzle down/i }));
    expect(mockCreateOperation).toHaveBeenLastCalledWith(
      createTestPrinter().id, expect.any(String),
      { kind: 'MoveTo', x: 110, y: 110, z: 9.95, f: 300 },
    );
    expect(mockMovePrinterTo).not.toHaveBeenCalled();
  });

  it('shows the first layer visual guide on Adjust step', async () => {
    const user = userEvent.setup();
    renderWizard();

    // Navigate to Adjust step
    await user.click(screen.getByRole('button', { name: /next/i }));
    await user.click(screen.getByRole('button', { name: /home all axes/i }));
    await waitFor(() => expect(screen.getByText(/move the nozzle/i)).toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: /move to center/i }));
    await waitFor(() => expect(screen.getByText(/Z-Offset:/)).toBeInTheDocument());

    expect(screen.getByText('First Layer Visual Guide')).toBeInTheDocument();
    expect(screen.getByText('Too Far')).toBeInTheDocument();
    expect(screen.getByText('Just Right')).toBeInTheDocument();
    expect(screen.getByText('Too Close')).toBeInTheDocument();
  });

  it('"Looks Good — Continue" advances to Save step', async () => {
    const user = userEvent.setup();
    renderWizard();

    // Navigate to Adjust step
    await user.click(screen.getByRole('button', { name: /next/i }));
    await user.click(screen.getByRole('button', { name: /home all axes/i }));
    await waitFor(() => expect(screen.getByText(/move the nozzle/i)).toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: /move to center/i }));
    await waitFor(() => expect(screen.getByText(/Z-Offset:/)).toBeInTheDocument());

    // Click "Looks Good — Continue"
    await user.click(screen.getByRole('button', { name: /looks good/i }));
    expect(screen.getByText(/ready to save/i)).toBeInTheDocument();
  });

  it('shows Klipper commands for Moonraker backend on Save step', async () => {
    const user = userEvent.setup();
    renderWizard();

    // Navigate to Save step
    await user.click(screen.getByRole('button', { name: /next/i }));
    await user.click(screen.getByRole('button', { name: /home all axes/i }));
    await waitFor(() => expect(screen.getByText(/move the nozzle/i)).toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: /move to center/i }));
    await waitFor(() => expect(screen.getByText(/Z-Offset:/)).toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: /looks good/i }));

    expect(screen.getByText(/Klipper\/Moonraker/i)).toBeInTheDocument();
    expect(screen.getByText(/SET_GCODE_OFFSET/)).toBeInTheDocument();
  });
});
