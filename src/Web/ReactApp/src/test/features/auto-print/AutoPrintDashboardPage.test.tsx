import '@testing-library/jest-dom';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AutoPrintDashboardPage } from '@/features/auto-print/pages/AutoPrintDashboardPage';

// Mock the API hooks
vi.mock('@/common/hooks/useApi', () => ({
  useAutoPrintStatus: vi.fn(),
  useMarkPrinterReady: vi.fn(),
  useSkipAutoPrintJob: vi.fn(),
  useCancelAutoPrint: vi.fn(),
  useSetAutoPrintEnabled: vi.fn(),
  useSetAutoPrintGlobalEnabled: vi.fn(),
}));

// Dynamic import after mocks
const {
  useAutoPrintStatus,
  useMarkPrinterReady,
  useSkipAutoPrintJob,
  useCancelAutoPrint,
  useSetAutoPrintEnabled,
  useSetAutoPrintGlobalEnabled,
} = await import('@/common/hooks/useApi');

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

describe('AutoPrintDashboardPage', () => {
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
    currentJobName: 'test-print.gcode',
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

  beforeEach(() => {
    vi.clearAllMocks();
    
    vi.mocked(useMarkPrinterReady).mockReturnValue(mockMarkReadyMutation as ReturnType<typeof useMarkPrinterReady>);
    vi.mocked(useSkipAutoPrintJob).mockReturnValue(mockSkipMutation as ReturnType<typeof useSkipAutoPrintJob>);
    vi.mocked(useCancelAutoPrint).mockReturnValue(mockCancelMutation as ReturnType<typeof useCancelAutoPrint>);
    vi.mocked(useSetAutoPrintEnabled).mockReturnValue(mockSetEnabledMutation as ReturnType<typeof useSetAutoPrintEnabled>);
    vi.mocked(useSetAutoPrintGlobalEnabled).mockReturnValue(mockSetGlobalEnabledMutation as ReturnType<typeof useSetAutoPrintGlobalEnabled>);
  });

  it('renders dashboard with global toggle and printer cards', () => {
    vi.mocked(useAutoPrintStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoPrintStatus>);

    render(
      <TestWrapper>
        <AutoPrintDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText('Auto-Print Dashboard')).toBeInTheDocument();
    expect(screen.getByLabelText('Global auto-print toggle')).toBeInTheDocument();
    expect(screen.getByText('Printer 1')).toBeInTheDocument();
  });

  it('shows loading spinner while data is fetching', () => {
    vi.mocked(useAutoPrintStatus).mockReturnValue({
      data: undefined,
      isLoading: true,
      error: null,
    } as ReturnType<typeof useAutoPrintStatus>);

    render(
      <TestWrapper>
        <AutoPrintDashboardPage />
      </TestWrapper>
    );

    // Check for spinner by its SVG structure (has circle and path elements for loading animation)
    const spinners = document.querySelectorAll('svg.animate-spin');
    expect(spinners.length).toBeGreaterThan(0);
    expect(screen.queryByText('Printer 1')).not.toBeInTheDocument();
  });

  it('shows empty state when no printers configured', () => {
    vi.mocked(useAutoPrintStatus).mockReturnValue({
      data: { globalEnabled: true, printers: [] },
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoPrintStatus>);

    render(
      <TestWrapper>
        <AutoPrintDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText('No Printers Configured')).toBeInTheDocument();
    expect(screen.getByText('Configure printers to enable auto-print queue management.')).toBeInTheDocument();
  });

  it('displays ready-gate checks with pass/fail indicators', () => {
    vi.mocked(useAutoPrintStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoPrintStatus>);

    render(
      <TestWrapper>
        <AutoPrintDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText('Printer Online')).toBeInTheDocument();
    expect(screen.getByText('Not Printing')).toBeInTheDocument();
    expect(screen.getByText('Bed Clear')).toBeInTheDocument();
    expect(screen.getByText('Temperature OK')).toBeInTheDocument();
  });

  it('global enable/disable toggle calls correct mutation', async () => {
    const user = userEvent.setup();
    vi.mocked(useAutoPrintStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoPrintStatus>);

    render(
      <TestWrapper>
        <AutoPrintDashboardPage />
      </TestWrapper>
    );

    const globalToggle = screen.getByLabelText('Global auto-print toggle');
    await user.click(globalToggle);

    await waitFor(() => {
      expect(mockSetGlobalEnabledMutation.mutate).toHaveBeenCalled();
    });
  });

  it('per-printer enable/disable toggle works', async () => {
    const user = userEvent.setup();
    vi.mocked(useAutoPrintStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoPrintStatus>);

    render(
      <TestWrapper>
        <AutoPrintDashboardPage />
      </TestWrapper>
    );

    const printerToggle = screen.getByLabelText(/Toggle auto-print for Printer 1/);
    await user.click(printerToggle);

    await waitFor(() => {
      expect(mockSetEnabledMutation.mutate).toHaveBeenCalled();
    });
  });

  it('mark ready button calls mutation with correct printerId', async () => {
    const user = userEvent.setup();
    vi.mocked(useAutoPrintStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoPrintStatus>);

    render(
      <TestWrapper>
        <AutoPrintDashboardPage />
      </TestWrapper>
    );

    const markReadyButton = screen.getByText('Mark Ready');
    await user.click(markReadyButton);

    expect(mockMarkReadyMutation.mutate).toHaveBeenCalledWith('printer-1');
  });

  it('skip button calls skip mutation', async () => {
    const user = userEvent.setup();
    vi.mocked(useAutoPrintStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoPrintStatus>);

    render(
      <TestWrapper>
        <AutoPrintDashboardPage />
      </TestWrapper>
    );

    const skipButton = screen.getByText('Skip');
    await user.click(skipButton);

    expect(mockSkipMutation.mutate).toHaveBeenCalledWith('printer-1');
  });

  it('cancel button calls cancel mutation', async () => {
    const user = userEvent.setup();
    vi.mocked(useAutoPrintStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoPrintStatus>);

    render(
      <TestWrapper>
        <AutoPrintDashboardPage />
      </TestWrapper>
    );

    const cancelButton = screen.getByText('Cancel');
    await user.click(cancelButton);

    expect(mockCancelMutation.mutate).toHaveBeenCalledWith('printer-1');
  });

  it('ready-gate check items show pass indicator for passed checks', () => {
    vi.mocked(useAutoPrintStatus).mockReturnValue({
      data: mockGlobalStatus,
      isLoading: false,
      error: null,
    } as ReturnType<typeof useAutoPrintStatus>);

    render(
      <TestWrapper>
        <AutoPrintDashboardPage />
      </TestWrapper>
    );

    // Verify passed checks are displayed
    expect(screen.getByText('Printer Online')).toBeInTheDocument();
    expect(screen.getByText('Not Printing')).toBeInTheDocument();
    expect(screen.getByText('Temperature OK')).toBeInTheDocument();
    expect(screen.getByText('Bed Clear')).toBeInTheDocument();
  });

  it('shows error message when data fails to load', () => {
    vi.mocked(useAutoPrintStatus).mockReturnValue({
      data: undefined,
      isLoading: false,
      error: new Error('Failed to fetch auto-print status'),
    } as ReturnType<typeof useAutoPrintStatus>);

    render(
      <TestWrapper>
        <AutoPrintDashboardPage />
      </TestWrapper>
    );

    expect(screen.getByText(/Failed to load auto-print status/)).toBeInTheDocument();
  });
});
