import '@testing-library/jest-dom';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { AutoDispatchDashboardPage } from '@/features/auto-dispatch/pages/AutoDispatchDashboardPage';
import type {
  AutoDispatchGlobalStatus,
  AutoDispatchStatus,
} from '@/types/api';

type AutoDispatchModule = typeof import('@/features/printers/hooks/useAutoDispatch');
type MutationHookName =
  | 'useSkipNextJob'
  | 'useCancelAutoDispatch'
  | 'useSetAutoDispatchEnabled'
  | 'useSetAllAutoDispatchEnabled'
  | 'usePreClearBed';
type ReadyFlow = ReturnType<AutoDispatchModule['useAutoDispatchReadyFlow']>;
type ReadyFlowResult = Pick<
  ReadyFlow,
  'challenge' | 'confirmReady' | 'confirmFilamentOverride' | 'cancelFilamentOverride'
> & {
  confirmation: Pick<ReadyFlow['confirmation'], 'isPending'>;
};
type DashboardHookResults = {
  useAutoDispatchGlobalStatus: Pick<
    ReturnType<AutoDispatchModule['useAutoDispatchGlobalStatus']>,
    'data' | 'isLoading' | 'error'
  >;
  useAutoDispatchReadyFlow: ReadyFlowResult;
} & {
  [Name in MutationHookName]: Pick<ReturnType<AutoDispatchModule[Name]>, 'mutate' | 'isPending'>;
};
type DashboardHooks = {
  [Name in keyof DashboardHookResults]:
    (...args: Parameters<AutoDispatchModule[Name]>) => DashboardHookResults[Name];
};

// Project the real module onto the fields consumed by the dashboard, without
// asserting incomplete doubles as full TanStack Query results.
const autoDispatchHooks = vi.hoisted(() => ({
  useAutoDispatchGlobalStatus: vi.fn<DashboardHooks['useAutoDispatchGlobalStatus']>(),
  useAutoDispatchReadyFlow: vi.fn<DashboardHooks['useAutoDispatchReadyFlow']>(),
  useSkipNextJob: vi.fn<DashboardHooks['useSkipNextJob']>(),
  useCancelAutoDispatch: vi.fn<DashboardHooks['useCancelAutoDispatch']>(),
  useSetAutoDispatchEnabled: vi.fn<DashboardHooks['useSetAutoDispatchEnabled']>(),
  useSetAllAutoDispatchEnabled: vi.fn<DashboardHooks['useSetAllAutoDispatchEnabled']>(),
  usePreClearBed: vi.fn<DashboardHooks['usePreClearBed']>(),
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', (): DashboardHooks => autoDispatchHooks);

function TestWrapper({ children }: { children: React.ReactNode }) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });

  return (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        {children}
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('AutoDispatchDashboardPage', () => {
  const mockPrinterStatus = {
    printerId: 'printer-1',
    printerName: 'Printer 1',
    enabled: true,
    isReady: false,
    queueDepth: 3,
    dispatchStateETag: 'dispatch-v1',
    printerETag: 'printer-v1',
    nextJobETag: 'job-v1',
    readyGateChecks: [
      { name: 'Printer Online', passed: true, message: 'Printer is online', checkedAt: '2025-01-15T10:00:00Z' },
      { name: 'Not Printing', passed: true, message: 'Printer is idle', checkedAt: '2025-01-15T10:00:00Z' },
      { name: 'Bed Clear', passed: false, message: 'Bed has objects', checkedAt: '2025-01-15T10:00:00Z' },
      { name: 'Temperature OK', passed: true, message: 'Temperature in range', checkedAt: '2025-01-15T10:00:00Z' },
    ],
    state: 'PendingReady',
  } satisfies AutoDispatchStatus;

  const mockPrintingStatus = {
    ...mockPrinterStatus,
    currentJobName: 'test-print.gcode',
    state: 'None',
  } satisfies AutoDispatchStatus;

  const mockPreClearedStatus = {
    ...mockPrinterStatus,
    state: 'None',
    bedPreConfirmed: true,
    readyGateChecks: [
      { name: 'Printer Online', passed: true, message: 'Printer is online', checkedAt: '2025-01-15T10:00:00Z' },
      { name: 'Not Printing', passed: true, message: 'Printer is idle', checkedAt: '2025-01-15T10:00:00Z' },
      { name: 'Bed Clear Confirmed', passed: true, message: 'Bed pre-cleared for immediate dispatch', checkedAt: '2025-01-15T10:00:00Z' },
      { name: 'Temperature OK', passed: true, message: 'Temperature in range', checkedAt: '2025-01-15T10:00:00Z' },
    ],
  } satisfies AutoDispatchStatus;

  const mockGlobalStatus = {
    globalEnabled: true,
    printers: [mockPrinterStatus],
  } satisfies AutoDispatchGlobalStatus;

  const mockConfirmReady = vi.fn<ReadyFlowResult['confirmReady']>().mockResolvedValue(undefined);
  const mockConfirmFilamentOverride = vi.fn<ReadyFlowResult['confirmFilamentOverride']>().mockResolvedValue(undefined);
  const mockCancelFilamentOverride = vi.fn<ReadyFlowResult['cancelFilamentOverride']>();
  const mockReadyFlow: ReadyFlowResult = {
    challenge: null,
    confirmation: { isPending: false },
    confirmReady: mockConfirmReady,
    confirmFilamentOverride: mockConfirmFilamentOverride,
    cancelFilamentOverride: mockCancelFilamentOverride,
  };

  const mockSkipMutation = {
    mutate: vi.fn<DashboardHookResults['useSkipNextJob']['mutate']>(),
    isPending: false,
  };

  const mockCancelMutation = {
    mutate: vi.fn<DashboardHookResults['useCancelAutoDispatch']['mutate']>(),
    isPending: false,
  };

  const mockSetEnabledMutation = {
    mutate: vi.fn<DashboardHookResults['useSetAutoDispatchEnabled']['mutate']>(),
    isPending: false,
  };

  const mockSetGlobalEnabledMutation = {
    mutate: vi.fn<DashboardHookResults['useSetAllAutoDispatchEnabled']['mutate']>(),
    isPending: false,
  };

  const mockPreClearMutation = {
    mutate: vi.fn<DashboardHookResults['usePreClearBed']['mutate']>(),
    isPending: false,
  };

  beforeEach(() => {
    vi.clearAllMocks();
    
    autoDispatchHooks.useAutoDispatchReadyFlow.mockReturnValue(mockReadyFlow);
    autoDispatchHooks.useSkipNextJob.mockReturnValue(mockSkipMutation);
    autoDispatchHooks.useCancelAutoDispatch.mockReturnValue(mockCancelMutation);
    autoDispatchHooks.useSetAutoDispatchEnabled.mockReturnValue(mockSetEnabledMutation);
    autoDispatchHooks.useSetAllAutoDispatchEnabled.mockReturnValue(mockSetGlobalEnabledMutation);
    autoDispatchHooks.usePreClearBed.mockReturnValue(mockPreClearMutation);
  });

  it('renders dashboard with global toggle and printer cards', () => {
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText('Auto-Dispatch')).toBeInTheDocument();
    expect(screen.getByLabelText('Global auto-dispatch toggle')).toBeInTheDocument();
    expect(screen.getByText('Printer 1')).toBeInTheDocument();
  });

  it('shows loading spinner while data is fetching', () => {
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: undefined,
      isLoading: true,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    // Check for spinner by its SVG structure (has circle and path elements for loading animation)
    const spinners = document.querySelectorAll('svg.animate-spin');
    expect(spinners.length).toBeGreaterThan(0);
    expect(screen.queryByText('Printer 1')).not.toBeInTheDocument();
  });

  it('shows empty state when no printers configured', () => {
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: { globalEnabled: true, printers: [] },
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText('No Printers Configured')).toBeInTheDocument();
    expect(screen.getByText('Configure printers to enable auto-dispatch queue management.')).toBeInTheDocument();
  });

  it('displays ready-gate checks with pass/fail indicators', () => {
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText('Printer Online')).toBeInTheDocument();
    expect(screen.getByText('Not Printing')).toBeInTheDocument();
    expect(screen.getByText('Bed Clear')).toBeInTheDocument();
    expect(screen.getByText('Temperature OK')).toBeInTheDocument();
  });

  it('global enable/disable toggle calls correct mutation', async () => {
    const user = userEvent.setup();
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    const globalToggle = screen.getByLabelText('Global auto-dispatch toggle');
    await user.click(globalToggle);

    await waitFor(() => {
      expect(mockSetGlobalEnabledMutation.mutate).toHaveBeenCalledWith({
        enabled: false,
        statuses: mockGlobalStatus.printers,
      } satisfies Parameters<DashboardHookResults['useSetAllAutoDispatchEnabled']['mutate']>[0]);
    });
  });

  it('per-printer auto-dispatch toggle works', async () => {
    const user = userEvent.setup();
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    const printerToggle = screen.getByLabelText(/Toggle auto-dispatch for Printer 1/);
    await user.click(printerToggle);

    await waitFor(() => {
      expect(mockSetEnabledMutation.mutate).toHaveBeenCalledWith({
        printerId: 'printer-1',
        enabled: false,
        dispatchStateETag: 'dispatch-v1',
        printerETag: 'printer-v1',
      } satisfies Parameters<DashboardHookResults['useSetAutoDispatchEnabled']['mutate']>[0]);
    });
  });

  it('mark ready button calls mutation with correct printerId', async () => {
    const user = userEvent.setup();
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    const markReadyButton = screen.getByText('Mark Ready');
    await user.click(markReadyButton);

    expect(mockConfirmReady).toHaveBeenCalledWith(mockPrinterStatus, 'Printer 1');
  });

  it('shows and confirms a filament challenge on the dashboard', async () => {
    const user = userEvent.setup();
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    });
    autoDispatchHooks.useAutoDispatchReadyFlow.mockReturnValue({
      ...mockReadyFlow,
      challenge: {
        status: mockPrinterStatus,
        printerName: 'Printer 1',
        result: {
          status: mockPrinterStatus,
          nextJob: {
            id: 'job-1',
            name: 'PETG part',
            jobKind: 'Standard',
            jobETag: 'job-v1',
          },
          dispatchInitiated: false,
          requiresFilamentOverride: true,
          filamentOverrideApplied: false,
          filamentCheck: {
            outcome: 'Incompatible',
            sufficient: false,
            materialMismatch: true,
            loadedMaterial: 'PLA',
            requiredMaterial: 'PETG',
            message: 'Material mismatch: loaded PLA, job requires PETG',
          },
        },
      },
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText('Material mismatch: loaded PLA, job requires PETG')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Confirm and Dispatch Anyway' }));
    expect(mockConfirmFilamentOverride).toHaveBeenCalledOnce();
  });

  it('skip button calls skip mutation', async () => {
    const user = userEvent.setup();
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    const skipButton = screen.getByText('Skip');
    await user.click(skipButton);

    expect(mockSkipMutation.mutate).toHaveBeenCalledWith(mockPrinterStatus);
  });

  it('cancel button calls cancel mutation', async () => {
    const user = userEvent.setup();
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: { globalEnabled: true, printers: [mockPrintingStatus] },
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    const cancelButton = screen.getByText('Cancel');
    await user.click(cancelButton);

    expect(mockCancelMutation.mutate).toHaveBeenCalledWith(mockPrintingStatus);
  });

  it('ready-gate check items show pass indicator for passed checks', () => {
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    // Verify passed checks are displayed
    expect(screen.getByText('Printer Online')).toBeInTheDocument();
    expect(screen.getByText('Not Printing')).toBeInTheDocument();
    expect(screen.getByText('Temperature OK')).toBeInTheDocument();
    expect(screen.getByText('Bed Clear')).toBeInTheDocument();
  });

  it('shows error message when data fails to load', () => {
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: undefined,
      isLoading: false,
      error: new Error('Failed to fetch auto-dispatch status'),
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText(/Failed to load auto-dispatch status/)).toBeInTheDocument();
  });

  it('hides Mark Ready button when printer is actively printing', () => {
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: { globalEnabled: true, printers: [mockPrintingStatus] },
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.queryByText('Mark Ready')).not.toBeInTheDocument();
    expect(screen.getByText('Cancel')).toBeInTheDocument();
  });

  it('hides Cancel button when printer is not printing', () => {
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.queryByText('Cancel')).not.toBeInTheDocument();
    expect(screen.getByText('Mark Ready')).toBeInTheDocument();
  });

  it('shows Printing badge when printer is actively printing', () => {
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: { globalEnabled: true, printers: [mockPrintingStatus] },
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.getAllByText('Printing').length).toBeGreaterThanOrEqual(1);
    // Verify the badge specifically exists
    const badges = screen.getAllByText('Printing');
    expect(badges.some(el => el.closest('[class*="badge"]') || el.tagName === 'SPAN')).toBe(true);
  });

  it('shows Awaiting Bed Clear badge when in PendingReady state', () => {
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText('Awaiting Bed Clear')).toBeInTheDocument();
  });

  it('shows pre-cleared readiness state without a failed bed-clear diagnostic', () => {
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: { globalEnabled: true, printers: [mockPreClearedStatus] },
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText('Bed pre-cleared — ready for dispatch')).toBeInTheDocument();
    expect(screen.getByText('Bed Clear Confirmed')).toBeInTheDocument();
    expect(screen.getAllByText('Ready').length).toBeGreaterThanOrEqual(1);
    expect(screen.queryByText('Pre-Clear Bed')).not.toBeInTheDocument();
  });

  it('sorts printers with the same state by queue depth before name', () => {
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: {
        globalEnabled: true,
        printers: [
          {
            ...mockPrinterStatus,
            printerId: 'printer-alpha',
            printerName: 'Alpha Printer',
            queueDepth: 1,
          },
          {
            ...mockPrinterStatus,
            printerId: 'printer-zulu',
            printerName: 'Zulu Printer',
            queueDepth: 5,
          },
        ],
      },
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    const printerHeadings = screen.getAllByRole('heading', { level: 3 }).map((heading) => heading.textContent);
    expect(printerHeadings.slice(0, 2)).toEqual(['Zulu Printer', 'Alpha Printer']);
  });

  it('filters to printers that have queued jobs regardless of current state', async () => {
    const user = userEvent.setup();
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: {
        globalEnabled: true,
        printers: [
          {
            ...mockPrinterStatus,
            printerId: 'printer-printing',
            printerName: 'Printing Queue',
            currentJobName: 'active-job.gcode',
            state: 'None',
            queueDepth: 2,
          },
          {
            ...mockPrinterStatus,
            printerId: 'printer-ready',
            printerName: 'Ready Queue',
            isReady: true,
            state: 'Ready',
            queueDepth: 1,
          },
          {
            ...mockPrinterStatus,
            printerId: 'printer-idle',
            printerName: 'Idle Queue',
            state: 'None',
            queueDepth: 3,
          },
          {
            ...mockPrinterStatus,
            printerId: 'printer-empty',
            printerName: 'No Queue',
            state: 'None',
            queueDepth: 0,
          },
        ],
      },
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    await user.click(screen.getByRole('radio', { name: /queued jobs/i }));

    expect(screen.getByText('Printing Queue')).toBeInTheDocument();
    expect(screen.getByText('Ready Queue')).toBeInTheDocument();
    expect(screen.getByText('Idle Queue')).toBeInTheDocument();
    expect(screen.queryByText('No Queue')).not.toBeInTheDocument();
  });

  it('includes pre-cleared printers in the Ready filter instead of Idle', async () => {
    const user = userEvent.setup();
    autoDispatchHooks.useAutoDispatchGlobalStatus.mockReturnValue({
      data: {
        globalEnabled: true,
        printers: [
          {
            ...mockPreClearedStatus,
            printerId: 'printer-precleared',
            printerName: 'Pre-Cleared Ready',
          },
          {
            ...mockPrinterStatus,
            printerId: 'printer-idle',
            printerName: 'Actually Idle',
            state: 'None',
            queueDepth: 0,
          },
        ],
      },
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    await user.click(screen.getByRole('radio', { name: /ready/i }));
    expect(screen.getByText('Pre-Cleared Ready')).toBeInTheDocument();
    expect(screen.queryByText('Actually Idle')).not.toBeInTheDocument();

    await user.click(screen.getByRole('radio', { name: /idle/i }));
    expect(screen.queryByText('Pre-Cleared Ready')).not.toBeInTheDocument();
    expect(screen.getByText('Actually Idle')).toBeInTheDocument();
  });
});
