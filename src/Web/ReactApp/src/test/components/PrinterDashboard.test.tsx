import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { BrowserRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
// @ts-expect-error: alias module resolution handled by Vite test environment
import { PrinterDashboard } from '@/components/PrinterDashboard';
// @ts-expect-error: alias module resolution handled by Vite test environment
import type { Printer } from '@/types/api';

// Mock the API hooks
vi.mock('@/hooks/useApi', async () => ({
  usePrinters: vi.fn(),
  useDeletePrinter: () => ({ mutateAsync: vi.fn() }),
  useStartDiscoveryStream: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useCreatePrinter: () => ({ mutateAsync: vi.fn() }),
  useBasicHealth: () => ({ data: { status: 'ok' }, isLoading: false, error: null }),
  usePrinterDetails: vi.fn(() => ({ data: undefined })),
  useManufacturers: vi.fn(() => ({ data: [] })),
  useModels: vi.fn(() => ({ data: [] })),
  useUpdatePrinter: () => ({ mutateAsync: vi.fn() }),
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
// @ts-expect-error: dynamic alias import resolved by Vite
const { usePrinters } = await import('@/hooks/useApi');

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

    expect(screen.getByText('Test Printer 1')).toBeTruthy();
    expect(screen.getByText('Test Printer 2')).toBeTruthy();
    expect(screen.getByText('Prusa MK3S+')).toBeTruthy();
    expect(screen.getByText('Creality Ender 3')).toBeTruthy();
  });
});