import '@testing-library/jest-dom';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, within } from '@testing-library/react';
import { screen } from '@testing-library/dom';
import { BrowserRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PrinterDashboard } from '@/components/PrinterDashboard';
import type { Printer } from '@/types/api';

// Mock the API hooks
vi.mock('@/hooks/useApi', async () => ({
  usePrintersWithCameraUrls: vi.fn(),
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
  usePrinterStatusUpdates: vi.fn(() => ({ getPrinterStatus: () => undefined })),
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
const { usePrintersWithCameraUrls } = await import('@/hooks/useApi');

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
      <BrowserRouter>
        {children}
      </BrowserRouter>
    </QueryClientProvider>
  );
}

describe('PrinterDashboard', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it('should render loading state', () => {
  vi.mocked(usePrintersWithCameraUrls).mockReturnValue({
      data: undefined,
      isLoading: true,
      error: null,
      refetch: vi.fn(),
  } as unknown as ReturnType<typeof usePrintersWithCameraUrls>);

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
  vi.mocked(usePrintersWithCameraUrls).mockReturnValue({
      data: [] as Printer[],
      isLoading: false,
      error: null,
      refetch: vi.fn(),
  } as unknown as ReturnType<typeof usePrintersWithCameraUrls>);

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

  vi.mocked(usePrintersWithCameraUrls).mockReturnValue({
      data: undefined,
      isLoading: false,
      error: mockError,
      refetch: vi.fn(),
  } as unknown as ReturnType<typeof usePrintersWithCameraUrls>);

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
        backend: 0, // Moonraker
      },
      {
        id: '2',
        name: 'Test Printer 2',
        serverUrl: 'http://printer2.local',
        isOnline: false,
        state: null,
        manufacturerName: 'Creality',
        modelName: 'Ender 3',
        backend: 1, // PrusaLink
      },
    ];

  vi.mocked(usePrintersWithCameraUrls).mockReturnValue({
      data: mockPrinters as Printer[],
      isLoading: false,
      error: null,
      refetch: vi.fn(),
  } as unknown as ReturnType<typeof usePrintersWithCameraUrls>);

    render(
      <TestWrapper>
        <PrinterDashboard />
      </TestWrapper>
    );

    // The printers list should be present and contain two listitems
    const list = screen.getByRole('list', { name: /printers list/i });
    expect(list).toBeInTheDocument();
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(2);

    // Prefer accessible queries: check text inside each listitem
    expect(within(items[0]).getByText('Test Printer 1')).toBeInTheDocument();
    expect(within(items[1]).getByText('Test Printer 2')).toBeInTheDocument();
    expect(within(items[0]).getByText('Prusa MK3S+')).toBeInTheDocument();
    expect(within(items[1]).getByText('Creality Ender 3')).toBeInTheDocument();
  });

  it('exposes data-testid attributes for printers list and items', () => {
    // Reuse a small mock response
    vi.mocked(usePrintersWithCameraUrls).mockReturnValue({
      data: [
        { id: '42', name: 'X', manufacturerName: 'M', modelName: 'Model' }
      ] as unknown as Printer[],
      isLoading: false,
      error: null,
      refetch: vi.fn(),
    } as unknown as ReturnType<typeof usePrintersWithCameraUrls>);

    render(
      <TestWrapper>
        <PrinterDashboard />
      </TestWrapper>
    );

    // Locate the list via accessible role and assert the elements expose the test ids
    const list = screen.getByRole('list', { name: /printers list/i });
    expect(list).toBeInTheDocument();
    const item = within(list).getByRole('listitem', { name: /Printer X/i });
    expect(item).toBeInTheDocument();
    // ensure data-testid attributes are present to prevent regressions
    expect(list).toHaveAttribute('data-testid', 'printers-list');
    expect(item).toHaveAttribute('data-testid', 'printer-item-42');
    // validate visible text using accessible queries
    expect(within(item).getByText('X')).toBeInTheDocument();
    expect(within(item).getByText('M Model')).toBeInTheDocument();
  });
});