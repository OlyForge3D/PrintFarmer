import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Printer } from '@/types/api';
import { PrintersPage } from '../PrintersPage';

const mockRefetchPrinters = vi.fn();
const mockUsePrinters = vi.fn();
const mockQueryClient = {
  invalidateQueries: vi.fn(),
  refetchQueries: vi.fn(),
};

const mockPrinters: Printer[] = [
  {
    id: 'printer-1',
    name: 'Printer Alpha',
    manufacturerName: 'Prusa',
    modelName: 'MK4',
    backend: 'PrusaLink',
    isOnline: true,
    isEnabled: true,
    state: 'Idle',
  },
  {
    id: 'printer-2',
    name: 'Printer Beta',
    manufacturerName: 'Bambu Lab',
    modelName: 'X1C',
    backend: 'Moonraker',
    isOnline: true,
    isEnabled: true,
    state: 'Printing',
  },
] as Printer[];

vi.mock('@tanstack/react-query', () => ({
  useQueryClient: () => mockQueryClient,
}));

vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => mockUsePrinters(),
  useDeletePrinter: () => ({ mutateAsync: vi.fn() }),
  usePrinterBackendCapabilities: () => ({ data: [] }),
  usePrinterCameraUrls: () => ({ data: [] }),
  useBedTypes: () => ({ data: [] }),
}));

vi.mock('@/common/hooks/usePrinterDisplay', () => ({
  usePrinterDisplays: (printers: Printer[]) => printers,
}));

vi.mock('@/common/hooks/useKeyboardShortcuts', () => ({
  useKeyboardShortcuts: vi.fn(),
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => false }),
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAllAutoDispatchStatuses: () => ({ data: [] }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getSettings: vi.fn().mockResolvedValue({ enableDiscovery: false, lastHeartbeat: null }),
    setPrinterMaintenance: vi.fn().mockResolvedValue(undefined),
    updatePrinter: vi.fn().mockResolvedValue(undefined),
  },
}));

vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));

vi.mock('@/common/components/ui/Button', () => ({
  Button: ({ children, ...props }: React.ButtonHTMLAttributes<HTMLButtonElement>) => <button {...props}>{children}</button>,
}));

vi.mock('@/common/components/ui/Select', () => ({
  Select: ({ children, ...props }: React.SelectHTMLAttributes<HTMLSelectElement>) => <select {...props}>{children}</select>,
}));

vi.mock('@/common/components/ViewModeToggle', () => ({
  ViewModeToggle: () => <div data-testid="view-mode-toggle" />,
}));

vi.mock('@/features/printers/components/CompactPrinterCard', () => ({
  CompactPrinterCard: ({ printer, onExpand }: { printer: Printer; onExpand: (printerId: string) => void }) => (
    <button type="button" onClick={() => onExpand(printer.id)}>
      Open {printer.name}
    </button>
  ),
}));

vi.mock('@/features/printers/components/DetailedPrinterCard', () => ({
  DetailedPrinterCard: ({ printer }: { printer: Printer }) => <div>{printer.name}</div>,
}));

vi.mock('@/features/printers/components/PrinterTableView', () => ({
  PrinterTableView: () => <div>PrinterTableViewMock</div>,
}));

vi.mock('@/features/printers/components/PrinterDetailsSidebar', () => ({
  PrinterDetailsSidebar: ({ printerId, onClose }: { printerId: string; onClose: () => void }) => (
    <div data-testid="printer-details-sidebar">
      <span>{printerId}</span>
      <button type="button" onClick={onClose}>Close sidebar</button>
    </div>
  ),
}));

vi.mock('@/features/printers/components/EditPrinterModal', () => ({
  EditPrinterModal: () => null,
}));

vi.mock('@/features/printers/components/AddPrinterButton', () => ({
  AddPrinterButton: () => null,
}));

vi.mock('@/features/printers/components/PrinterDiscoveryModal', () => ({
  PrinterDiscoveryModal: () => null,
}));

vi.mock('@/common/components/modals/DeleteConfirmationModal', () => ({
  DeleteConfirmationModal: () => null,
}));

vi.mock('@/common/components/skeletons/PrinterCardSkeleton', () => ({
  PrinterCardSkeleton: () => null,
}));

vi.mock('@/features/printers/components/admin/PrinterImportExportControls', () => ({
  default: () => null,
}));

vi.mock('@/features/printers/components/admin/PrinterBulkControls', () => ({
  default: () => null,
}));

vi.mock('@/common/components/HelpButton', () => ({
  HelpButton: () => null,
}));

vi.mock('@/common/components/icons/MdiIcons', () => ({
  PrinterIcon: () => <span>PrinterIcon</span>,
  PrinterSearchIcon: () => <span>PrinterSearchIcon</span>,
}));

vi.mock('@/common/utils/printerStateDisplay', () => ({
  requiresBedClearConfirmation: () => false,
}));

vi.mock('@/features/filament-coverage/hooks', () => ({
  useFleetFilamentCoverage: vi.fn(() => ({
    data: null,
    isLoading: false,
    isError: false,
  })),
  usePrinterFilamentCoverage: vi.fn(() => ({
    data: null,
    isLoading: false,
    isError: false,
  })),
  __resetFilamentCoverageSubscriptionForTests: vi.fn(),
}));

vi.mock('@/features/printers/hooks/usePrinterTagsFleet', () => ({
  useFleetPrinterTags: vi.fn(() => ({ data: [], isLoading: false, isError: false })),
  usePrinterTagsFromFleet: vi.fn(() => ({ data: [], isPending: false, isError: false, error: null })),
}));

vi.mock('@/features/printers/hooks/useQueueSummariesFleet', () => ({
  useFleetQueueSummaries: vi.fn(() => ({ data: [], isLoading: false, isError: false })),
  useQueueSummaryFromFleet: vi.fn(() => ({ data: undefined, isPending: false, isError: false, error: null })),
}));

vi.mock('@/features/printers/hooks/useDiscoveryAvailability', () => ({
  useDiscoveryAvailable: vi.fn(() => false),
  useNetworkDiscoverySettings: vi.fn(() => ({ data: undefined, isLoading: false, isError: false })),
}));

vi.mock('@/common/components/ui', () => ({
  Button: ({ children, ...props }: React.ButtonHTMLAttributes<HTMLButtonElement>) => <button {...props}>{children}</button>,
}));

vi.mock('@/common/hooks/usePageTour', () => ({
  usePageTour: () => ({ startTour: vi.fn() }),
}));

vi.mock('@/features/printers/tours/printers.tour', () => ({
  printersTour: [],
}));

function LocationDisplay() {
  const location = useLocation();
  return <div data-testid="location-display">{location.pathname}</div>;
}

function renderPage(initialEntry: string) {
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <Routes>
        <Route path="/printers" element={<PrintersPage />} />
        <Route path="/printers/:printerId" element={<PrintersPage />} />
      </Routes>
      <LocationDisplay />
    </MemoryRouter>
  );
}

describe('PrintersPage', () => {
  beforeEach(() => {
    localStorage.clear();
    mockRefetchPrinters.mockClear();
    mockUsePrinters.mockReturnValue({
      data: mockPrinters,
      isLoading: false,
      isError: false,
      error: null,
      refetch: mockRefetchPrinters,
    });
    mockQueryClient.invalidateQueries.mockClear();
    mockQueryClient.refetchQueries.mockClear();
  });

  it('opens the details sidebar when loaded from /printers/:printerId', () => {
    renderPage('/printers/printer-1');

    const sidebars = screen.getAllByTestId('printer-details-sidebar');
    expect(sidebars).toHaveLength(2);
    sidebars.forEach((sidebar) => {
      expect(sidebar).toHaveTextContent('printer-1');
    });
    expect(screen.getByTestId('location-display')).toHaveTextContent('/printers/printer-1');
  });

  it('falls back to /printers when the route printer id does not exist', async () => {
    renderPage('/printers/missing-printer');

    await waitFor(() => {
      expect(screen.getByTestId('location-display')).toHaveTextContent('/printers');
    });
    expect(screen.queryByTestId('printer-details-sidebar')).not.toBeInTheDocument();
  });

  it('updates the URL when opening printer details from the list', async () => {
    const user = userEvent.setup();
    renderPage('/printers');

    await user.click(screen.getByRole('button', { name: 'Open Printer Alpha' }));

    await waitFor(() => {
      expect(screen.getByTestId('location-display')).toHaveTextContent('/printers/printer-1');
    });

    const sidebars = screen.getAllByTestId('printer-details-sidebar');
    expect(sidebars).toHaveLength(2);
    sidebars.forEach((sidebar) => {
      expect(sidebar).toHaveTextContent('printer-1');
    });
  });

  it('navigates back to /printers when the sidebar closes', async () => {
    const user = userEvent.setup();
    renderPage('/printers/printer-1');

    await user.click(screen.getAllByRole('button', { name: 'Close sidebar' })[0]);

    await waitFor(() => {
      expect(screen.getByTestId('location-display')).toHaveTextContent('/printers');
    });
    expect(screen.queryByTestId('printer-details-sidebar')).not.toBeInTheDocument();
  });

  it('renders a retryable error instead of the empty state when loading printers fails', async () => {
    const user = userEvent.setup();
    mockUsePrinters.mockReturnValue({
      data: undefined,
      isLoading: false,
      isError: true,
      error: new Error('Printer service unavailable'),
      refetch: mockRefetchPrinters,
    });

    renderPage('/printers');

    expect(screen.getByRole('alert')).toHaveTextContent('Unable to Load Printers');
    expect(screen.getByRole('alert')).toHaveTextContent('Printer service unavailable');
    expect(screen.queryByText('No Printers Found')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Retry' }));
    expect(mockRefetchPrinters).toHaveBeenCalledTimes(1);
  });

  it('keeps the error alert and Retry button visible through a transient refetch after a sustained failure (#1581)', () => {
    // React Query only reaches `status: 'error'` after retries are exhausted,
    // and the printers query never had a successful fetch here (every attempt
    // 503s). Any subsequent refetch of that still-empty query — a manual
    // Retry click, or QueueRealtimeBridge's `invalidateQueries(['printers'])`
    // on SignalR reconnect/queue events — resets `status` back to `pending`,
    // which is exactly what this mock sequence simulates: error -> transient
    // pending/fetching with no data yet -> (still failing) error again. The
    // page must not fall back to the loading skeleton in that middle state.
    mockUsePrinters.mockReturnValue({
      data: undefined,
      isLoading: false,
      isError: true,
      error: new Error('Printer service unavailable'),
      refetch: mockRefetchPrinters,
    });
    const { rerender } = renderPage('/printers');

    expect(screen.getByRole('alert')).toHaveTextContent('Unable to Load Printers');

    // Simulate the invalidateQueries-triggered refetch: React Query resets
    // isError/error back to their pending defaults even though no printers
    // data has ever arrived.
    mockUsePrinters.mockReturnValue({
      data: undefined,
      isLoading: true,
      isError: false,
      error: null,
      refetch: mockRefetchPrinters,
    });
    rerender(
      <MemoryRouter initialEntries={['/printers']}>
        <Routes>
          <Route path="/printers" element={<PrintersPage />} />
        </Routes>
      </MemoryRouter>
    );

    expect(screen.getByRole('alert')).toHaveTextContent('Unable to Load Printers');
    expect(screen.getByRole('button', { name: 'Retry' })).toBeVisible();
    expect(screen.queryByRole('status')).not.toBeInTheDocument();

    // Once the fleet actually loads, the error state must clear.
    mockUsePrinters.mockReturnValue({
      data: mockPrinters,
      isLoading: false,
      isError: false,
      error: null,
      refetch: mockRefetchPrinters,
    });
    rerender(
      <MemoryRouter initialEntries={['/printers']}>
        <Routes>
          <Route path="/printers" element={<PrintersPage />} />
        </Routes>
      </MemoryRouter>
    );

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
