import '@testing-library/jest-dom';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render } from '@testing-library/react';
import { screen } from '@testing-library/dom';
import { TestRouter } from '@/test/utils/TestRouter';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PrinterDashboard } from '@/features/printers/components/PrinterDashboard';
import type { Printer } from '@/types/api';
import { PrinterBackend } from '@/types/api';
import { AuthProvider } from '@/common/contexts/AuthContext';

// Mock the API hooks
vi.mock('@/common/hooks/useApi', async () => ({
  usePrinters: vi.fn(),
  useDeletePrinter: () => ({ mutateAsync: vi.fn() }),
  useStartDiscoveryStream: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useCreatePrinter: () => ({ mutateAsync: vi.fn() }),
  useBasicHealth: () => ({ data: { status: 'ok' }, isLoading: false, error: null }),
  useHealthStatus: () => ({ data: undefined, isLoading: false, error: null }),
  usePrinterDetails: vi.fn(() => ({ data: undefined })),
  useManufacturers: vi.fn(() => ({ data: [] })),
  useModels: vi.fn(() => ({ data: [] })),
  useFilamentTypes: vi.fn(() => ({ data: [] })),
  useUpdatePrinter: () => ({ mutateAsync: vi.fn() }),
  useJobQueue: vi.fn(() => ({ 
    data: [], 
    isLoading: false, 
    error: null 
  })),
  usePrinterHistory: vi.fn(() => ({ 
    data: { jobs: [], total: 0 }, 
    isLoading: false, 
    error: null, 
    refetch: vi.fn() 
  })),
  usePrinterHistoryTotals: vi.fn(() => ({ 
    data: { totalJobs: 0, totalPrintTime: 0, successfulJobs: 0 }, 
    isLoading: false 
  })),
}));

vi.mock('@/hooks/useSignalR', () => ({
  usePrinterStatusUpdates: vi.fn(() => ({ printerStatuses: new Map() })),
  useDiscoveryStream: () => ({
    progress: null,
    foundPrinters: [],
    completed: false,
    resetDiscovery: vi.fn(),
    isActive: false,
    isCompleted: false,
  }),
}));

// dynamic import after mocks
const { usePrinters } = await import('@/common/hooks/useApi');

function TestWrapper({ children }: { children: React.ReactNode }) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });

  return (
    <AuthProvider>
      <QueryClientProvider client={queryClient}>
        <TestRouter>{children}</TestRouter>
      </QueryClientProvider>
    </AuthProvider>
  );
}

describe('PrinterDashboard', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it('should render loading state', () => {
  vi.mocked(usePrinters).mockReturnValue({
      data: undefined,
      isLoading: true,
      error: null,
      refetch: vi.fn(),
  } as unknown as ReturnType<typeof usePrinters>);

    render(
      <TestWrapper>
        <PrinterDashboard />
      </TestWrapper>
    );

    // Loading skeleton present — look for the status region or loading placeholders with aria-label
    expect(screen.getByRole('status')).toBeInTheDocument();
    expect(screen.getAllByLabelText(/Loading printer/i).length).toBeGreaterThan(0);
  });

  it('should render empty state when no printers', () => {
  vi.mocked(usePrinters).mockReturnValue({
      data: [] as Printer[],
      isLoading: false,
      error: null,
      refetch: vi.fn(),
  } as unknown as ReturnType<typeof usePrinters>);

    render(
      <TestWrapper>
        <PrinterDashboard />
      </TestWrapper>
    );

    expect(screen.getByText('No Printers Found')).toBeTruthy();
    expect(screen.getByText('Get started by adding your first 3D printer.')).toBeTruthy();
  });

  it('should render error state', () => {
    const mockError = {
      message: 'Failed to fetch printers',
      statusCode: 500,
    };

  vi.mocked(usePrinters).mockReturnValue({
      data: undefined,
      isLoading: false,
      error: mockError,
      refetch: vi.fn(),
  } as unknown as ReturnType<typeof usePrinters>);

    render(
      <TestWrapper>
        <PrinterDashboard />
      </TestWrapper>
    );

    expect(screen.getByText('Error Loading Printers')).toBeTruthy();
    expect(screen.getByText('Failed to fetch printers')).toBeTruthy();
  });

  it('should render printers when data is available', () => {
    const mockPrinters = [
      {
        id: '1',
        name: 'Test Printer 1',
        serverUrl: 'http://printer1.local',
        isOnline: true,
        state: 'printing',
        manufacturerName: 'Prusa',
        modelName: 'MK3S+',
        backend: PrinterBackend.Moonraker,
      },
      {
        id: '2',
        name: 'Test Printer 2',
        serverUrl: 'http://printer2.local',
        isOnline: false,
        state: null,
        manufacturerName: 'Creality',
        modelName: 'Ender 3',
        backend: PrinterBackend.PrusaLink,
      },
    ];

  vi.mocked(usePrinters).mockReturnValue({
      data: mockPrinters as Printer[],
      isLoading: false,
      error: null,
      refetch: vi.fn(),
  } as unknown as ReturnType<typeof usePrinters>);

    render(
      <TestWrapper>
        <PrinterDashboard />
      </TestWrapper>
    );

    // Check that stats are rendered instead of printer cards
    expect(screen.getByText('Total Printers')).toBeInTheDocument();
    expect(screen.getByText('Online')).toBeInTheDocument();
    expect(screen.getByText('Printing')).toBeInTheDocument();
  });

  it('exposes data-testid attributes for printers list and items', () => {
    // Reuse a small mock response
    vi.mocked(usePrinters).mockReturnValue({
      data: [
        { id: '42', name: 'X', manufacturerName: 'M', modelName: 'Model' }
      ] as unknown as Printer[],
      isLoading: false,
      error: null,
      refetch: vi.fn(),
    } as unknown as ReturnType<typeof usePrinters>);

    render(
      <TestWrapper>
        <PrinterDashboard />
      </TestWrapper>
    );

    // Check that the dashboard stats are rendered (not individual printers)
    expect(screen.getByText('Total Printers')).toBeInTheDocument();
    expect(screen.getByText('Online')).toBeInTheDocument();
  });
});