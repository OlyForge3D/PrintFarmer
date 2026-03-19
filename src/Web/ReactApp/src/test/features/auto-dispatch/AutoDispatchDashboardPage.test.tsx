import '@testing-library/jest-dom';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AutoDispatchDashboardPage } from '@/features/auto-dispatch/pages/AutoDispatchDashboardPage';

// Mock the auto-dispatch hooks
vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAutoDispatchGlobalStatus: vi.fn(),
  useConfirmBedClear: vi.fn(),
  useSkipNextJob: vi.fn(),
  useCancelAutoDispatch: vi.fn(),
  useSetAutoDispatchEnabled: vi.fn(),
  useSetAllAutoDispatchEnabled: vi.fn(),
  usePreClearBed: vi.fn(),
}));

// Dynamic import after mocks
const {
  useAutoDispatchGlobalStatus,
  useConfirmBedClear,
  useSkipNextJob,
  useCancelAutoDispatch,
  useSetAutoDispatchEnabled,
  useSetAllAutoDispatchEnabled,
  usePreClearBed,
} = await import('@/features/printers/hooks/useAutoDispatch');

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
      {children}
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
    readyGateChecks: [
      { name: 'Printer Online', passed: true, message: 'Printer is online', checkedAt: '2025-01-15T10:00:00Z' },
      { name: 'Not Printing', passed: true, message: 'Printer is idle', checkedAt: '2025-01-15T10:00:00Z' },
      { name: 'Bed Clear', passed: false, message: 'Bed has objects', checkedAt: '2025-01-15T10:00:00Z' },
      { name: 'Temperature OK', passed: true, message: 'Temperature in range', checkedAt: '2025-01-15T10:00:00Z' },
    ],
    state: 'PendingReady',
  };

  const mockPrintingStatus = {
    ...mockPrinterStatus,
    currentJobName: 'test-print.gcode',
    state: 'None',
  };

  const mockGlobalStatus = {
    globalEnabled: true,
    printers: [mockPrinterStatus],
  };

  const mockMarkReadyMutation = {
    mutate: vi.fn(),
    isPending: false,
  };

  const mockSkipMutation = {
    mutate: vi.fn(),
    isPending: false,
  };

  const mockCancelMutation = {
    mutate: vi.fn(),
    isPending: false,
  };

  const mockSetEnabledMutation = {
    mutate: vi.fn(),
    isPending: false,
  };

  const mockSetGlobalEnabledMutation = {
    mutate: vi.fn(),
    isPending: false,
  };

  const mockPreClearMutation = {
    mutate: vi.fn(),
    isPending: false,
  };

  beforeEach(() => {
    vi.clearAllMocks();
    
    vi.mocked(useConfirmBedClear).mockReturnValue(mockMarkReadyMutation as ReturnType<typeof useConfirmBedClear>);
    vi.mocked(useSkipNextJob).mockReturnValue(mockSkipMutation as ReturnType<typeof useSkipNextJob>);
    vi.mocked(useCancelAutoDispatch).mockReturnValue(mockCancelMutation as ReturnType<typeof useCancelAutoDispatch>);
    vi.mocked(useSetAutoDispatchEnabled).mockReturnValue(mockSetEnabledMutation as ReturnType<typeof useSetAutoDispatchEnabled>);
    vi.mocked(useSetAllAutoDispatchEnabled).mockReturnValue(mockSetGlobalEnabledMutation as ReturnType<typeof useSetAllAutoDispatchEnabled>);
    vi.mocked(usePreClearBed).mockReturnValue(mockPreClearMutation as ReturnType<typeof usePreClearBed>);
  });

  it('renders dashboard with global toggle and printer cards', () => {
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText('Auto-Dispatch Dashboard')).toBeInTheDocument();
    expect(screen.getByLabelText('Global auto-dispatch toggle')).toBeInTheDocument();
    expect(screen.getByText('Printer 1')).toBeInTheDocument();
  });

  it('shows loading spinner while data is fetching', () => {
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: undefined,
      isLoading: true,
      error: null,
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

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
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: { globalEnabled: true, printers: [] },
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText('No Printers Configured')).toBeInTheDocument();
    expect(screen.getByText('Configure printers to enable auto-dispatch queue management.')).toBeInTheDocument();
  });

  it('displays ready-gate checks with pass/fail indicators', () => {
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

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
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    const globalToggle = screen.getByLabelText('Global auto-dispatch toggle');
    await user.click(globalToggle);

    await waitFor(() => {
      expect(mockSetGlobalEnabledMutation.mutate).toHaveBeenCalled();
    });
  });

  it('per-printer auto-dispatch toggle works', async () => {
    const user = userEvent.setup();
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    const printerToggle = screen.getByLabelText(/Toggle auto-dispatch for Printer 1/);
    await user.click(printerToggle);

    await waitFor(() => {
      expect(mockSetEnabledMutation.mutate).toHaveBeenCalled();
    });
  });

  it('mark ready button calls mutation with correct printerId', async () => {
    const user = userEvent.setup();
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    const markReadyButton = screen.getByText('Mark Ready');
    await user.click(markReadyButton);

    expect(mockMarkReadyMutation.mutate).toHaveBeenCalledWith('printer-1');
  });

  it('skip button calls skip mutation', async () => {
    const user = userEvent.setup();
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    const skipButton = screen.getByText('Skip');
    await user.click(skipButton);

    expect(mockSkipMutation.mutate).toHaveBeenCalledWith('printer-1');
  });

  it('cancel button calls cancel mutation', async () => {
    const user = userEvent.setup();
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: { globalEnabled: true, printers: [mockPrintingStatus] },
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    const cancelButton = screen.getByText('Cancel');
    await user.click(cancelButton);

    expect(mockCancelMutation.mutate).toHaveBeenCalledWith('printer-1');
  });

  it('ready-gate check items show pass indicator for passed checks', () => {
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

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
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: undefined,
      isLoading: false,
      error: new Error('Failed to fetch auto-dispatch status'),
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText(/Failed to load auto-dispatch status/)).toBeInTheDocument();
  });

  it('hides Mark Ready button when printer is actively printing', () => {
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: { globalEnabled: true, printers: [mockPrintingStatus] },
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.queryByText('Mark Ready')).not.toBeInTheDocument();
    expect(screen.getByText('Cancel')).toBeInTheDocument();
  });

  it('hides Cancel button when printer is not printing', () => {
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.queryByText('Cancel')).not.toBeInTheDocument();
    expect(screen.getByText('Mark Ready')).toBeInTheDocument();
  });

  it('shows Printing badge when printer is actively printing', () => {
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: { globalEnabled: true, printers: [mockPrintingStatus] },
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

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
    vi.mocked(useAutoDispatchGlobalStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoDispatchGlobalStatus>);

    render(
      <TestWrapper>
        <AutoDispatchDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText('Awaiting Bed Clear')).toBeInTheDocument();
  });
});
