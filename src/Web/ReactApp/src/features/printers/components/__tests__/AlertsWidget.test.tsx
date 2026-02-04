import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AlertsWidget } from '../AlertsWidget';

// Mock the hooks and services
vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: vi.fn(),
}));

vi.mock('@/common/hooks/usePrinterDisplay', () => ({
  usePrinterDisplays: vi.fn((printers) => printers),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getSettings: vi.fn(),
  },
}));

import { usePrinters } from '@/common/hooks/useApi';
import { apiClient } from '@/services/api';

describe('AlertsWidget', () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
      },
    });
    vi.clearAllMocks();
  });

  const renderWidget = (props = {}) => {
    return render(
      <QueryClientProvider client={queryClient}>
        <AlertsWidget {...props} />
      </QueryClientProvider>
    );
  };

  it('should render without crashing', () => {
    vi.mocked(usePrinters).mockReturnValue({ data: [], isLoading: false } as any);
    vi.mocked(apiClient.getSettings).mockResolvedValue({
      enabled: true,
      showOfflinePrinterAlerts: true,
    });

    renderWidget();
    expect(screen.getByRole('heading', { name: /Alerts/i })).toBeInTheDocument();
  });

  it('should show "No Active Alerts" when all printers online', () => {
    vi.mocked(usePrinters).mockReturnValue({
      data: [
        { id: '1', name: 'Printer 1', isOnline: true, inMaintenance: false },
        { id: '2', name: 'Printer 2', isOnline: true, inMaintenance: false },
      ],
      isLoading: false,
    } as any);
    vi.mocked(apiClient.getSettings).mockResolvedValue({
      enabled: true,
      showOfflinePrinterAlerts: true,
    });

    renderWidget();
    expect(screen.getByText(/All systems healthy/i)).toBeInTheDocument();
  });

  it('should show offline printer alert', async () => {
    vi.mocked(usePrinters).mockReturnValue({
      data: [
        { id: '1', name: 'Printer 1', isOnline: false, inMaintenance: false },
        { id: '2', name: 'Printer 2', isOnline: true, inMaintenance: false },
      ],
      isLoading: false,
    } as any);
    vi.mocked(apiClient.getSettings).mockResolvedValue({
      enabled: true,
      showOfflinePrinterAlerts: true,
    });

    renderWidget();
    
    // Wait for async updates
    await vi.waitFor(() => {
      expect(screen.getByText(/1 Printer Offline/i)).toBeInTheDocument();
    });
  });

  it('should show maintenance alert', async () => {
    vi.mocked(usePrinters).mockReturnValue({
      data: [
        { id: '1', name: 'Printer 1', isOnline: true, inMaintenance: true },
        { id: '2', name: 'Printer 2', isOnline: true, inMaintenance: false },
      ],
      isLoading: false,
    } as any);
    vi.mocked(apiClient.getSettings).mockResolvedValue({
      enabled: true,
      showOfflinePrinterAlerts: true,
    });

    renderWidget();
    
    await vi.waitFor(() => {
      expect(screen.getByText(/1 Printer in Maintenance/i)).toBeInTheDocument();
    });
  });

  it('should show multiple alerts', async () => {
    vi.mocked(usePrinters).mockReturnValue({
      data: [
        { id: '1', name: 'Printer 1', isOnline: false, inMaintenance: false },
        { id: '2', name: 'Printer 2', isOnline: true, inMaintenance: true },
        { id: '3', name: 'Printer 3', isOnline: false, inMaintenance: false },
      ],
      isLoading: false,
    } as any);
    vi.mocked(apiClient.getSettings).mockResolvedValue({
      enabled: true,
      showOfflinePrinterAlerts: true,
    });

    renderWidget();
    
    await vi.waitFor(() => {
      expect(screen.getByText(/3 active alerts/i)).toBeInTheDocument();
    });
  });

  it('should respect showOfflinePrinterAlerts setting', async () => {
    vi.mocked(usePrinters).mockReturnValue({
      data: [
        { id: '1', name: 'Printer 1', isOnline: false, inMaintenance: false },
      ],
      isLoading: false,
    } as any);
    vi.mocked(apiClient.getSettings).mockResolvedValue({
      enabled: true,
      showOfflinePrinterAlerts: false,
    });

    renderWidget();
    
    await vi.waitFor(() => {
      expect(screen.queryByText(/Offline/i)).not.toBeInTheDocument();
    });
  });

  it('should pluralize alerts correctly', async () => {
    vi.mocked(usePrinters).mockReturnValue({
      data: [
        { id: '1', name: 'Printer 1', isOnline: false, inMaintenance: false },
        { id: '2', name: 'Printer 2', isOnline: false, inMaintenance: false },
      ],
      isLoading: false,
    } as any);
    vi.mocked(apiClient.getSettings).mockResolvedValue({
      enabled: true,
      showOfflinePrinterAlerts: true,
    });

    renderWidget();
    
    await vi.waitFor(() => {
      expect(screen.getByText(/2 Printers Offline/i)).toBeInTheDocument();
    });
  });
});
