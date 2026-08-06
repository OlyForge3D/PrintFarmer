import React from 'react';
import { render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PrintersPage } from '../PrintersPage';

// Focused a11y regression test for the "Discover Printers" toolbar button
// (WCAG 2.5.3 Label in Name): unlike ../__tests__/PrintersPage.test.tsx,
// this file renders the *real* Button and MdiIcons components (not stubs),
// because the property under test — the computed accessible name, and the
// icon's decorative treatment — lives in that real markup, not in a mock.

const mockQueryClient = {
  invalidateQueries: vi.fn(),
  refetchQueries: vi.fn(),
};

vi.mock('@tanstack/react-query', () => ({
  useQueryClient: () => mockQueryClient,
}));

vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({ data: [], isLoading: false, refetch: vi.fn() }),
  useDeletePrinter: () => ({ mutateAsync: vi.fn() }),
  usePrinterBackendCapabilities: () => ({ data: [] }),
  useBedTypes: () => ({ data: [] }),
}));

vi.mock('@/common/hooks/usePrinterDisplay', () => ({
  usePrinterDisplays: () => [],
}));

vi.mock('@/common/hooks/useKeyboardShortcuts', () => ({
  useKeyboardShortcuts: vi.fn(),
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  // Admin permission (and network-discovery availability, mocked below) are
  // exactly what gates this button into existence.
  useAuth: () => ({ hasPermission: () => true }),
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

vi.mock('@/common/components/ui/Select', () => ({
  Select: ({ children, ...props }: React.SelectHTMLAttributes<HTMLSelectElement>) => <select {...props}>{children}</select>,
}));

vi.mock('@/common/components/ViewModeToggle', () => ({
  ViewModeToggle: () => <div data-testid="view-mode-toggle" />,
}));

vi.mock('@/features/printers/components/CompactPrinterCard', () => ({
  CompactPrinterCard: () => null,
}));

vi.mock('@/features/printers/components/DetailedPrinterCard', () => ({
  DetailedPrinterCard: () => null,
}));

vi.mock('@/features/printers/components/PrinterTableView', () => ({
  PrinterTableView: () => null,
}));

vi.mock('@/features/printers/components/PrinterDetailsSidebar', () => ({
  PrinterDetailsSidebar: () => null,
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
  useFleetFilamentCoverage: vi.fn(() => ({ data: null, isLoading: false, isError: false })),
  usePrinterFilamentCoverage: vi.fn(() => ({ data: null, isLoading: false, isError: false })),
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
  // True — the button under test is gated on `hasPermission(...) && discoveryAvailable`.
  useDiscoveryAvailable: vi.fn(() => true),
  useNetworkDiscoverySettings: vi.fn(() => ({ data: undefined, isLoading: false, isError: false })),
}));

vi.mock('@/common/hooks/usePageTour', () => ({
  usePageTour: () => ({ startTour: vi.fn() }),
}));

vi.mock('@/features/printers/tours/printers.tour', () => ({
  printersTour: [],
}));

// Intentionally NOT mocked: '@/common/components/ui/Button' and
// '@/common/components/icons/MdiIcons' — the real `Button` (which wraps
// `iconLeft` in an `aria-hidden` span) and the real `PrinterSearchIcon` (a
// plain `<svg role="img">`) are exactly what this test verifies.

function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/printers']}>
      <Routes>
        <Route path="/printers" element={<PrintersPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('PrintersPage — Discover Printers button accessible name (WCAG 2.5.3 Label in Name)', () => {
  beforeEach(() => {
    mockQueryClient.invalidateQueries.mockClear();
    mockQueryClient.refetchQueries.mockClear();
  });

  it("exposes an accessible name that contains the exact visible label, 'Discover Printers'", () => {
    renderPage();

    // getByRole with an exact `name` match proves the *computed* accessible
    // name is 'Discover Printers on the local network' — which contains the
    // full visible text "Discover Printers" verbatim, satisfying Label in
    // Name for voice-access users issuing "Click Discover Printers".
    const button = screen.getByRole('button', { name: 'Discover Printers on the local network' });
    expect(button).toBeInTheDocument();
    expect(button).toHaveTextContent('Discover Printers');
  });

  it('keeps the leading icon decorative (hidden from the accessibility tree)', () => {
    renderPage();

    const button = screen.getByRole('button', { name: 'Discover Printers on the local network' });
    // The real PrinterSearchIcon renders `role="img"`, but Button wraps
    // `iconLeft` in an `aria-hidden` span, so it must not surface as its own
    // accessible "img" — only the button's own name should be exposed.
    expect(within(button).queryByRole('img')).not.toBeInTheDocument();
  });
});