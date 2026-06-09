import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Printer } from '@/types/api';
import { PrintersPage } from '../PrintersPage';

const mockRefetchPrinters = vi.fn();
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
  usePrinters: () => ({
    data: mockPrinters,
    isLoading: false,
    refetch: mockRefetchPrinters,
  }),
  useDeletePrinter: () => ({ mutateAsync: vi.fn() }),
  usePrinterBackendCapabilities: () => ({ data: [] }),
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
  CompactPrinterCard: ({ printer, onExpand }: { printer: Printer; onExpand: () => void }) => (
    <button type="button" onClick={onExpand}>
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
});

