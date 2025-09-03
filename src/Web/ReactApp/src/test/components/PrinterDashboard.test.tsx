import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { BrowserRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PrinterDashboard } from '@/components/PrinterDashboard';

// Mock the API hooks
vi.mock('@/hooks/useApi', () => ({
  usePrinters: vi.fn(),
}));

vi.mock('@/hooks/useSignalR', () => ({
  usePrinterStatusUpdates: vi.fn(() => ({})),
}));

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
    } as any);

    render(
      <TestWrapper>
        <PrinterDashboard />
      </TestWrapper>
    );

    expect(screen.getByRole('status', { hidden: true })).toBeInTheDocument();
  });

  it('should render empty state when no printers', () => {
    vi.mocked(usePrinters).mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
      refetch: vi.fn(),
    } as any);

    render(
      <TestWrapper>
        <PrinterDashboard />
      </TestWrapper>
    );

    expect(screen.getByText('No printers')).toBeInTheDocument();
    expect(screen.getByText('Get started by adding your first 3D printer.')).toBeInTheDocument();
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
    } as any);

    render(
      <TestWrapper>
        <PrinterDashboard />
      </TestWrapper>
    );

    expect(screen.getByText('Error loading printers')).toBeInTheDocument();
    expect(screen.getByText('Failed to fetch printers')).toBeInTheDocument();
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
      data: mockPrinters,
      isLoading: false,
      error: null,
      refetch: vi.fn(),
    } as any);

    render(
      <TestWrapper>
        <PrinterDashboard />
      </TestWrapper>
    );

    expect(screen.getByText('Test Printer 1')).toBeInTheDocument();
    expect(screen.getByText('Test Printer 2')).toBeInTheDocument();
    expect(screen.getByText('Prusa MK3S+')).toBeInTheDocument();
    expect(screen.getByText('Creality Ender 3')).toBeInTheDocument();
  });
});