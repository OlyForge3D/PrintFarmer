import React from 'react';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const mockUseAdminHubParent = vi.fn();

vi.mock('@/features/admin/utils/adminHubParentState', () => ({
  ADMIN_HUB_ROUTE_STATE: { pageParent: 'admin-control-center' },
  useAdminHubParent: () => mockUseAdminHubParent(),
}));
import type { Printer } from '@/types/api';
import { PrintersPage } from '../PrintersPage';
import { LG_BREAKPOINT_QUERY } from '@/common/hooks/useMediaQuery';

const mockRefetchPrinters = vi.fn();
const mockUsePrinters = vi.fn();
const mockUsePrinterCameraUrls = vi.fn();
const mockUseBedTypes = vi.fn();
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
  usePrinterCameraUrls: () => mockUsePrinterCameraUrls(),
  useBedTypes: () => mockUseBedTypes(),
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
  PageTemplate: ({
    children,
    parent,
  }: {
    children: React.ReactNode;
    parent?: { label: string };
  }) => (
    <div>
      {parent && <div data-testid="page-parent">{parent.label}</div>}
      {children}
    </div>
  ),
}));

vi.mock('@/common/components/ViewModeToggle', () => ({
  ViewModeToggle: ({ onChange }: { viewMode: string; onChange: (mode: 'collapsed' | 'detailed' | 'table') => void }) => (
    <div data-testid="view-mode-toggle">
      <button type="button" onClick={() => onChange('detailed')}>Detailed view</button>
      <button type="button" onClick={() => onChange('collapsed')}>Collapsed view</button>
      <button type="button" onClick={() => onChange('table')}>Table view</button>
    </div>
  ),
}));

vi.mock('@/features/printers/components/CompactPrinterCard', () => ({
  CompactPrinterCard: ({ printer, onExpand }: { printer: Printer; onExpand: (printerId: string) => void }) => (
    <button
      type="button"
      onClick={() => onExpand(printer.id)}
      data-testid={`compact-card-${printer.id}`}
      data-camera-stream={printer.cameraStreamUrl ?? ''}
      data-camera-snapshot={printer.cameraSnapshotUrl ?? ''}
      data-camera-access-mode={printer.cameraAccessMode ?? ''}
    >
      Open {printer.name}
    </button>
  ),
}));

vi.mock('@/features/printers/components/DetailedPrinterCard', () => ({
  DetailedPrinterCard: ({ printer }: { printer: Printer }) => (
    <div
      data-testid={`detailed-card-${printer.id}`}
      data-camera-stream={printer.cameraStreamUrl ?? ''}
      data-camera-snapshot={printer.cameraSnapshotUrl ?? ''}
    >
      {printer.name}
    </div>
  ),
}));

vi.mock('@/features/printers/components/PrinterTableView', () => ({
  PrinterTableView: ({ printers }: { printers: Printer[] }) => (
    <div data-testid="printer-table">
      {printers.map(printer => <div key={printer.id}>{printer.name}</div>)}
    </div>
  ),
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

/**
 * The global matchMedia polyfill (src/test/setup.ts) always reports
 * `matches: false`, i.e. "not desktop". Override it for tests that need to
 * exercise the `isLgUp` (desktop/large-screen) branch of PrintersPage.
 */
function mockLgBreakpoint(matches: boolean) {
  const mql = {
    matches,
    media: LG_BREAKPOINT_QUERY,
    onchange: null,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
  };
  window.matchMedia = vi.fn().mockReturnValue(mql) as unknown as typeof window.matchMedia;
}

describe('PrintersPage', () => {
  beforeEach(() => {
    localStorage.clear();
    mockUseAdminHubParent.mockReset();
    mockUseAdminHubParent.mockReturnValue(undefined);
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
    mockUsePrinterCameraUrls.mockReturnValue({ data: [] });
    mockUseBedTypes.mockClear();
    mockUseBedTypes.mockReturnValue({ data: [{ id: 'textured', name: 'Textured' }] });
  });

  it('feeds each card the primary linked camera routes from /printers/camera-urls, keyed by printer id (#3331)', async () => {
    mockUsePrinterCameraUrls.mockReturnValue({
      data: [
        {
          id: 'printer-1',
          name: 'Printer Alpha',
          cameraStreamUrl: '/api/printers/printer-1/camera/stream',
          cameraSnapshotUrl: '/api/printers/printer-1/camera/snapshot',
          cameraAccessMode: 'Stream',
        },
        { id: 'printer-2', name: 'Printer Beta' },
      ],
    });

    const { unmount } = renderPage('/printers');

    const alpha = screen.getByTestId('compact-card-printer-1');
    expect(alpha).toHaveAttribute('data-camera-stream', '/api/printers/printer-1/camera/stream');
    expect(alpha).toHaveAttribute('data-camera-snapshot', '/api/printers/printer-1/camera/snapshot');
    expect(alpha).toHaveAttribute('data-camera-access-mode', 'Stream');
    const beta = screen.getByTestId('compact-card-printer-2');
    expect(beta).toHaveAttribute('data-camera-stream', '');
    expect(beta).toHaveAttribute('data-camera-snapshot', '');

    unmount();
    renderPage('/printers?view=detailed');

    await waitFor(() => {
      expect(screen.getByTestId('detailed-card-printer-1')).toHaveAttribute(
        'data-camera-stream',
        '/api/printers/printer-1/camera/stream'
      );
    });
    expect(screen.getByTestId('detailed-card-printer-2')).toHaveAttribute('data-camera-stream', '');
  });

  it('opens the details sidebar when loaded from /printers/:printerId', () => {
    renderPage('/printers/printer-1');

    // #1702 (follow-up): exactly one PrinterDetailsSidebar is mounted, never
    // both the mobile and desktop layout copies at once — see
    // "mounts exactly one PrinterDetailsSidebar..." below for the dedicated
    // regression test on this invariant.
    expect(screen.getByTestId('printer-details-sidebar')).toHaveTextContent('printer-1');
    expect(screen.getByTestId('location-display')).toHaveTextContent('/printers/printer-1');
  });

  it('mounts exactly one PrinterDetailsSidebar regardless of viewport width (#1702 follow-up)', () => {
    // PrinterDetailsSidebar renders MaterialLoadout/MmuControlBox, which fire
    // hardware mutations from onClick handlers with no concurrency guard.
    // Previously PrintersPage rendered PrinterDetailsSidebar twice
    // unconditionally (a `lg:hidden` mobile copy and a `hidden lg:block`
    // desktop copy) and relied on CSS alone to hide whichever one didn't
    // apply — both React trees were mounted regardless of viewport. That's
    // the same dual-mount hazard #1702 describes, just gated by viewport
    // width instead of route/view-mode. Assert only one mounts, on both
    // sides of the lg breakpoint.
    mockLgBreakpoint(false);
    const { unmount } = renderPage('/printers/printer-1');
    expect(screen.getAllByTestId('printer-details-sidebar')).toHaveLength(1);
    unmount();

    mockLgBreakpoint(true);
    renderPage('/printers/printer-1');
    expect(screen.getAllByTestId('printer-details-sidebar')).toHaveLength(1);

    mockLgBreakpoint(false);
  });

  it('shows the Admin Control Center parent when navigation state marks the page as admin-origin', () => {
    mockUseAdminHubParent.mockReturnValue({ label: 'Admin Control Center', to: '/admin' });
    renderPage('/printers');

    expect(screen.getByTestId('page-parent')).toHaveTextContent('Admin Control Center');
  });

  it('does not show the Admin Control Center parent during ordinary printers navigation', () => {
    const { unmount } = renderPage('/printers');

    expect(screen.queryByTestId('page-parent')).not.toBeInTheDocument();
    unmount();

    mockLgBreakpoint(true);
    renderPage('/printers/printer-1');
    expect(screen.getAllByTestId('printer-details-sidebar')).toHaveLength(1);

    // Restore the default (mobile) breakpoint so later tests in this file
    // aren't affected by this override.
    mockLgBreakpoint(false);
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

    const sidebar = screen.getByTestId('printer-details-sidebar');
    expect(sidebar).toHaveTextContent('printer-1');
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

  it('closes the details sidebar when landing in detailed view for the same printer (#1702 dual-mount race)', async () => {
    // In detailed view, DetailedPrinterCard already folds the sidebar's
    // MaterialLoadout/MmuControlBox content inline (#1584). If the sidebar
    // stayed open too, both would mount for the same printer at once, each
    // capable of firing its own AMS/MMU hardware mutation with no shared
    // lock. The route must be redirected back to /printers instead.
    renderPage('/printers/printer-1?view=detailed');

    await waitFor(() => {
      expect(screen.getByTestId('location-display')).toHaveTextContent('/printers');
    });
    expect(screen.getByTestId('location-display')).not.toHaveTextContent('/printers/printer-1');
    expect(screen.queryByTestId('printer-details-sidebar')).not.toBeInTheDocument();

    // The grid itself is untouched — the view mode (and its query param) is
    // preserved across the redirect, so the remounted page still resolves
    // viewMode from the URL and both printers still render via
    // DetailedPrinterCard instead of falling back to the collapsed view.
    await waitFor(() => {
      expect(screen.getByText('Printer Alpha')).toBeInTheDocument();
      expect(screen.getByText('Printer Beta')).toBeInTheDocument();
    });
  });

  it('never renders the sidebar alongside the detailed grid, even for the render that switches view mode (#1702)', async () => {
    // End-to-end confirmation that the settled state is correct once the
    // page finishes redirecting after a client-side view-mode switch. The
    // render-time guard itself is unit-tested directly in
    // printerSidebarVisibility.test.ts, since a DOM assertion taken after an
    // interaction can't observe whether the guard fired at render time or
    // was only eventually corrected by the redirect effect below.
    const user = userEvent.setup();
    renderPage('/printers/printer-1');

    expect(screen.getByTestId('printer-details-sidebar')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Detailed view' }));

    await waitFor(() => {
      expect(screen.queryByTestId('printer-details-sidebar')).not.toBeInTheDocument();
    });
  });

  it('starts collapsed with the shared Filters disclosure and keeps sort outside its panel', async () => {
    const user = userEvent.setup();
    renderPage('/printers');

    const toggle = screen.getByRole('button', { name: 'Filters' });
    expect(toggle).toHaveTextContent('Filters');
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    const stripId = toggle.getAttribute('aria-controls')!;
    expect(document.getElementById(stripId)).not.toBeVisible();
    expect(screen.getAllByRole('combobox')).toHaveLength(1);
    expect(screen.getByRole('combobox', { name: 'Sort by' })).toBeVisible();

    await user.tab();
    expect(toggle).toHaveFocus();
    await user.keyboard('{Enter}');

    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(document.getElementById(stripId)).toBeVisible();
    expect(screen.getByRole('region', { name: 'Filters' })).toBeVisible();
    const strip = screen.getByRole('group', { name: 'Printer filters' });
    expect(within(strip).getAllByRole('combobox')).toHaveLength(3);
    expect(within(strip).getByText('State', { selector: 'label' })).toHaveAttribute('for', 'state-filter');
    expect(within(strip).getByText('Backend', { selector: 'label' })).toHaveAttribute('for', 'backend-filter');
    expect(within(strip).getByText('Done in', { selector: 'label' })).toHaveAttribute('for', 'availability-filter');
    expect(screen.queryByRole('button', { name: /Apply/i })).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/bed type/i)).not.toBeInTheDocument();
    expect(mockUseBedTypes).not.toHaveBeenCalled();

    await user.keyboard(' ');
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(toggle).toHaveFocus();
    await user.tab();
    expect(screen.getByRole('combobox', { name: 'Sort by' })).toHaveFocus();
  });

  it.each([
    ['online', ['printer-1', 'printer-2', 'paused']],
    ['printing', ['printer-2']],
    ['paused', ['paused']],
    ['offline', ['offline']],
  ])('applies the %s state immediately without filtering by bed type', async (state, expectedIds) => {
    const user = userEvent.setup();
    mockUsePrinters.mockReturnValue({
      data: [
        { ...mockPrinters[0], bedTypeId: 'textured' },
        { ...mockPrinters[1], bedTypeId: 'smooth' },
        { ...mockPrinters[0], id: 'paused', name: 'Paused Printer', state: 'Paused', bedTypeId: null },
        { ...mockPrinters[0], id: 'offline', name: 'Offline Printer', isOnline: false, state: 'Offline' },
      ],
      isLoading: false,
      refetch: mockRefetchPrinters,
    });
    renderPage('/printers');
    await user.click(screen.getByRole('button', { name: 'Filters' }));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Filter by printer state' }), state);

    const cards = screen.getAllByTestId(/^compact-card-/);
    expect(cards.map(card => card.getAttribute('data-testid'))).toEqual(
      expect.arrayContaining(expectedIds.map(id => `compact-card-${id}`))
    );
    expect(cards).toHaveLength(expectedIds.length);
    const toggle = screen.getByRole('button', { name: 'Filters (1)' });
    expect(toggle).toHaveTextContent('Filters (1)');
  });

  it('keeps completion filtering inclusive and includes printers without completion times', async () => {
    const user = userEvent.setup();
    const now = Date.now();
    const clock = vi.spyOn(Date, 'now').mockReturnValue(now);
    try {
      mockUsePrinters.mockReturnValue({
        data: [
          mockPrinters[0],
          { ...mockPrinters[1], estimatedCompletionTimeUtc: new Date(now + 60 * 60 * 1000).toISOString() },
          { ...mockPrinters[1], id: 'later', name: 'Later Printer', estimatedCompletionTimeUtc: new Date(now + 3 * 60 * 60 * 1000).toISOString() },
        ],
        isLoading: false,
        refetch: mockRefetchPrinters,
      });
      renderPage('/printers');
      await user.click(screen.getByRole('button', { name: 'Filters' }));
      const completion = screen.getByRole('combobox', { name: 'Done in (estimated completion time)' });
      expect(within(completion).getAllByRole('option').map(option => option.getAttribute('value')))
        .toEqual(['all', '1', '2', '4', '8', '12', '24']);
      await user.selectOptions(completion, '1');
      expect(screen.getByText('Open Printer Alpha')).toBeVisible();
      expect(screen.getByText('Open Printer Beta')).toBeVisible();
      expect(screen.queryByText('Open Later Printer')).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Filters (1)' })).toBeVisible();
      await user.selectOptions(completion, '4');
      expect(screen.getByText('Open Later Printer')).toBeVisible();
      expect(screen.getByRole('button', { name: 'Filters (1)' })).toBeVisible();
      await user.selectOptions(completion, 'all');
      expect(screen.getByText('Open Later Printer')).toBeVisible();
      expect(screen.getByRole('button', { name: 'Filters' })).toBeVisible();
      expect(screen.getByRole('button', { name: 'Reset filters' })).toBeDisabled();
      await user.selectOptions(completion, '1');
      await user.click(screen.getByRole('button', { name: 'Reset filters' }));
      expect(screen.getByText('Open Later Printer')).toBeVisible();
      expect(screen.getByRole('button', { name: 'Filters' })).toHaveFocus();
    } finally {
      clock.mockRestore();
    }
  });

  it('keeps the tour spotlight around the toggle and the filters it describes', async () => {
    const user = userEvent.setup();
    const { container } = renderPage('/printers');
    const toggle = screen.getByRole('button', { name: 'Filters' });
    await user.click(toggle);

    expect(container.querySelectorAll('[data-tour="printers-filters"]')).toHaveLength(1);
    expect(toggle.closest('[data-tour="printers-filters"]'))
      .toContainElement(screen.getByRole('group', { name: 'Printer filters' }));
  });

  it('narrows the collapsed tour spotlight to the filter, sort and view controls', () => {
    const { container } = renderPage('/printers');
    const anchors = container.querySelectorAll('[data-tour="printers-filters"]');
    expect(anchors).toHaveLength(1);

    const anchor = anchors[0];
    expect(anchor).toContainElement(screen.getByRole('button', { name: 'Filters' }));
    expect(anchor).toContainElement(screen.getByLabelText('Sort by'));
    expect(anchor).not.toContainElement(container.querySelector('[data-tour="printers-actions"]') as HTMLElement);
  });

  it('retains combined filters when collapsed and resets them from the shared filter panel', async () => {
    const user = userEvent.setup();
    renderPage('/printers');
    await user.click(screen.getByRole('button', { name: 'Filters' }));
    await user.selectOptions(screen.getByLabelText('Filter by printer state'), 'printing');
    await user.selectOptions(screen.getByLabelText('Filter by backend'), 'Moonraker');
    await user.selectOptions(screen.getByLabelText('Done in (estimated completion time)'), '4');
    const toggle = screen.getByRole('button', { name: 'Filters (3)' });
    expect(screen.getByRole('group', { name: 'Printer filters' })).toBeVisible();
    await user.click(toggle);

    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(toggle).toHaveTextContent('Filters (3)');
    expect(screen.queryByRole('group', { name: 'Printer filters' })).not.toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: 'Filter by backend' })).not.toBeInTheDocument();
    expect(screen.queryByText('Open Printer Alpha')).not.toBeInTheDocument();
    expect(screen.getByText('Open Printer Beta')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Clear filters' })).not.toBeInTheDocument();
    await user.keyboard('{Enter}');
    expect(screen.getByLabelText('Filter by printer state')).toHaveValue('printing');
    expect(screen.getByLabelText('Filter by backend')).toHaveValue('Moonraker');
    expect(screen.getByLabelText('Done in (estimated completion time)')).toHaveValue('4');
    await user.click(screen.getByRole('button', { name: 'Reset filters' }));
    expect(screen.getByRole('button', { name: 'Filters' })).toHaveFocus();
    expect(screen.getByLabelText('Filter by printer state')).toHaveValue('all');
    expect(screen.getByLabelText('Filter by backend')).toHaveValue('all');
    expect(screen.getByLabelText('Done in (estimated completion time)')).toHaveValue('all');
  });

  it('resets filters from the shared panel without changing sort or persisted table view', async () => {
    const user = userEvent.setup();
    renderPage('/printers');
    await user.selectOptions(screen.getByRole('combobox', { name: 'Sort by' }), 'name');
    await user.click(screen.getByRole('button', { name: 'Table view' }));
    await user.click(screen.getByRole('button', { name: 'Filters' }));
    await user.selectOptions(screen.getByLabelText('Filter by printer state'), 'printing');
    await user.selectOptions(screen.getByLabelText('Filter by backend'), 'Moonraker');
    await user.selectOptions(screen.getByLabelText('Done in (estimated completion time)'), '2');
    expect(screen.getByRole('group', { name: 'Printer filters' })).toBeVisible();
    expect(within(screen.getByTestId('printer-table')).queryByText('Printer Alpha')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Filters (3)' }));
    expect(screen.queryByRole('button', { name: 'Reset filters' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Filters (3)' }));
    expect(screen.getByRole('button', { name: 'Reset filters' })).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Reset filters' }));

    const toggle = screen.getByRole('button', { name: 'Filters' });
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(toggle).toHaveFocus();
    expect(screen.getByRole('group', { name: 'Printer filters' })).toBeVisible();
    expect(screen.getByRole('combobox', { name: 'Sort by' })).toHaveValue('name');
    expect(localStorage.getItem('printerSortMode')).toBe('name');
    expect(localStorage.getItem('printerViewMode')).toBe('table');
    expect(within(screen.getByTestId('printer-table')).getAllByText(/Printer/).map(row => row.textContent))
      .toEqual(['Printer Alpha', 'Printer Beta']);
    expect(screen.getByLabelText('Filter by printer state')).toHaveValue('all');
    expect(screen.getByLabelText('Filter by backend')).toHaveValue('all');
    expect(screen.getByLabelText('Done in (estimated completion time)')).toHaveValue('all');
  });

  it('shows a distinct filtered-empty state (not the onboarding empty state) when a backend filter matches no printers (#1713)', async () => {
    const user = userEvent.setup();
    renderPage('/printers');

    // Sanity check: printers are seeded and neither empty state shows yet.
    expect(screen.queryByText('No Printers Found')).not.toBeInTheDocument();
    expect(screen.queryByText('No printers match these filters')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Filters' }));
    await user.selectOptions(screen.getByLabelText('Filter by backend'), 'PrusaLink');

    // Only Printer Alpha (PrusaLink) matches; Printer Beta (Moonraker) does not.
    expect(screen.queryByText('No printers match these filters')).not.toBeInTheDocument();
    expect(screen.getByText('Open Printer Alpha')).toBeInTheDocument();
    expect(screen.queryByText('Open Printer Beta')).not.toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText('Filter by backend'), 'SDCP');

    // No seeded printer uses SDCP: the filtered-empty state must show, not
    // the onboarding "No Printers Found" / "add your first printer" prompt.
    expect(screen.getByText('No printers match these filters')).toBeInTheDocument();
    expect(screen.queryByText('No Printers Found')).not.toBeInTheDocument();
    expect(screen.queryByText(/add your first 3D printer/i)).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Filters (1)' }));
    expect(screen.queryByRole('combobox', { name: 'Filter by backend' })).not.toBeInTheDocument();
    // Clearing filters restores the full list.
    const emptyState = screen.getByText('No printers match these filters').parentElement!;
    expect(within(emptyState).queryByRole('img')).not.toBeInTheDocument();
    await user.click(within(emptyState).getByRole('button', { name: 'Clear filters' }));
    expect(screen.queryByText('No printers match these filters')).not.toBeInTheDocument();
    expect(screen.getByText('Open Printer Alpha')).toBeInTheDocument();
    expect(screen.getByText('Open Printer Beta')).toBeInTheDocument();
  });

  it('still shows the onboarding empty state when the farm genuinely has no printers', () => {
    mockUsePrinters.mockReturnValue({
      data: [],
      isLoading: false,
      isError: false,
      error: null,
      refetch: mockRefetchPrinters,
    });

    renderPage('/printers');

    expect(screen.getByText('No Printers Found')).toBeInTheDocument();
    expect(screen.queryByText('No printers match these filters')).not.toBeInTheDocument();
  });

  it('offers a Shutdown option in the State filter and filters to Shutdown printers only (#2221)', async () => {
    const user = userEvent.setup();
    mockUsePrinters.mockReturnValue({
      data: [
        ...mockPrinters,
        {
          id: 'printer-3',
          name: 'Moonraker Shutdown',
          manufacturerName: 'Bambu Lab',
          modelName: 'X1C',
          backend: 'Moonraker',
          isOnline: true,
          isEnabled: true,
          state: 'Shutdown',
        } as Printer,
      ],
      isLoading: false,
      isError: false,
      error: null,
      refetch: mockRefetchPrinters,
    });

    renderPage('/printers');

    await user.click(screen.getByRole('button', { name: 'Filters' }));
    const stateFilterSelect = screen.getByLabelText('Filter by printer state');
    expect(within(stateFilterSelect).getByRole('option', { name: 'Shutdown' })).toBeInTheDocument();

    // All three printers show before the filter is applied.
    expect(screen.getByText('Open Printer Alpha')).toBeInTheDocument();
    expect(screen.getByText('Open Printer Beta')).toBeInTheDocument();
    expect(screen.getByText('Open Moonraker Shutdown')).toBeInTheDocument();

    await user.selectOptions(stateFilterSelect, 'shutdown');

    // Only the Shutdown printer remains once the filter is applied.
    expect(screen.queryByText('Open Printer Alpha')).not.toBeInTheDocument();
    expect(screen.queryByText('Open Printer Beta')).not.toBeInTheDocument();
    expect(screen.getByText('Open Moonraker Shutdown')).toBeInTheDocument();
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
