import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi } from 'vitest';
import { PrinterBackend, type Printer } from '@/types/api';

vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: () => ({ hasPermission: () => true }) }));

// Keep the troubleshooting control, sidebar, and guide unmocked to exercise
// the complete help path from both card layouts.
vi.mock('@/common/hooks/useApi', () => ({
  usePrinterDetails: () => ({ data: undefined, isLoading: false }),
  usePrintJobObjects: () => ({ data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() }),
  queryKeys: { printJobObjects: (printerId: string) => ['printJobObjects', printerId] },
}));

vi.mock('@/common/hooks/useSpoolmanConfigured', () => ({
  useSpoolmanConfigured: () => ({ ready: true }),
}));

vi.mock('@/features/printers/hooks/usePrinterTagsFleet', () => ({
  usePrinterTagsFromFleet: () => ({ data: [], isPending: false }),
}));

vi.mock('@/features/printers/hooks/useQueueSummariesFleet', () => ({
  useQueueSummaryFromFleet: () => ({ data: undefined }),
}));

vi.mock('@/components/TaggingModal', () => ({ TaggingModal: () => null }));

vi.mock('@/services/maintenanceService', () => ({
  maintenanceService: { getPrinterStatistics: vi.fn() },
}));

vi.mock('@tanstack/react-query', () => ({
  useIsMutating: () => 0,
  useQueryClient: () => ({ invalidateQueries: vi.fn(), setQueryData: vi.fn() }),
  useQuery: () => ({ data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() }),
  useMutation: () => ({ mutate: vi.fn(), mutateAsync: vi.fn(), isPending: false }),
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAutoDispatchStatus: () => ({ data: null, isLoading: false }),
  useSetAutoDispatchEnabled: () => ({ mutateAsync: vi.fn() }),
}));

vi.mock('@/features/printers/hooks/useFailureDetectionAlert', () => ({
  useFailureDetectionAlert: () => ({ event: undefined, recentEvents: [] }),
}));

vi.mock('@/features/printers/hooks/usePrinterFailureDetectionStatus', () => ({
  usePrinterFailureDetectionStatus: () => ({ printerStatus: undefined, data: undefined, isLoading: false }),
}));

vi.mock('@/features/printers/hooks/useFailureDetectionPolling', () => ({
  useFailureDetectionPollingEnabled: () => false,
}));

vi.mock('@/features/filament-coverage/components/FilamentCoverageBreakdown', () => ({
  FilamentCoverageBreakdown: () => null,
}));

vi.mock('@/features/printers/components/PrinterHistoryModal', () => ({ PrinterHistoryModal: () => null }));
vi.mock('@/features/printers/components/PrinterFilesModal', () => ({ PrinterFilesModal: () => null }));
vi.mock('@/features/printers/components/SpoolPickerModal', () => ({ SpoolPickerModal: () => null }));
vi.mock('@/features/printers/components/MaterialLoadout', () => ({
  MaterialLoadout: () => <div data-testid="material-loadout" />,
}));
vi.mock('@/features/printers/components/TemperatureControlSection', () => ({
  TemperatureControlSection: () => <div data-testid="temp-section" />,
}));
vi.mock('@/features/printers/components/MovementControlSection', () => ({
  MovementControlSection: ({ rightContent }: { rightContent?: React.ReactNode }) => (
    <div data-testid="movement-section">{rightContent}</div>
  ),
}));
vi.mock('@/features/printers/components/PrinterActionBar', () => ({ PrinterActionBar: () => <div data-testid="action-bar" /> }));
vi.mock('@/features/printers/components/BedClearBanner', () => ({ BedClearBanner: () => null }));
vi.mock('@/features/printers/components/PrintProgressBar', () => ({ PrintProgressBar: () => <div data-testid="print-progress" /> }));
vi.mock('@/features/printers/components/EstimatedCompletionBadge', () => ({ EstimatedCompletionBadge: () => null }));
vi.mock('@/features/printers/components/FailureDetectionBadge', () => ({ FailureDetectionBadge: () => null }));
vi.mock('@/features/printers/components/FailureDetectionMonitoringBadge', () => ({ FailureDetectionMonitoringBadge: () => null }));
vi.mock('@/features/printers/components/FailureDetectionMonitoringSummary', () => ({ FailureDetectionMonitoringSummary: () => null }));
vi.mock('@/features/printers/components/PrinterCameraPreview', () => ({ PrinterCameraPreview: () => <div data-testid="camera-preview" /> }));
vi.mock('@/services/api', () => ({ apiClient: {} }));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() } }));

import { DetailedPrinterCard } from '../DetailedPrinterCard';
import { CompactPrinterCard } from '../CompactPrinterCard';

function makeOfflinePrinter(overrides: Partial<Printer> = {}): Printer {
  return {
    id: 'printer-1',
    name: 'Printer 1',
    backend: PrinterBackend.Moonraker,
    backendUrl: 'http://printer-1.local',
    frontendUrl: 'http://printer-1.local',
    isOnline: false,
    isEnabled: true,
    isReachable: false,
    state: 'Offline',
    progress: 0,
    ...overrides,
  } as Printer;
}

describe.each([
  ['detailed', (printer: Printer) => <DetailedPrinterCard printer={printer} />],
  ['compact', (printer: Printer) => <CompactPrinterCard printer={printer} onExpand={vi.fn()} />],
] as const)('%s card offline troubleshooting', (_layout, renderCard) => {
  it('shows one icon-only header control without an inline troubleshooting block', () => {
    render(renderCard(makeOfflinePrinter()));

    const button = screen.getByRole('button', { name: 'Open troubleshooting for Printer 1' });
    expect(button).toHaveAttribute('aria-haspopup', 'dialog');
    expect(button).toHaveAttribute('aria-expanded', 'false');
    expect(button.textContent).toBe('');
    expect(button.parentElement).toContainElement(screen.getByText('Printer 1'));
    expect(screen.queryByText('Troubleshoot')).not.toBeInTheDocument();
    expect(screen.queryByText('Printer Offline — Troubleshooting Steps')).not.toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.queryByText('Common Checks')).not.toBeInTheDocument();
  });

  it('opens a body-portaled sidebar with the printer-specific guide and restores focus on Escape', async () => {
    const user = userEvent.setup();
    const { container } = render(renderCard(makeOfflinePrinter({ ipAddress: '192.168.1.100' })));
    const button = screen.getByRole('button', { name: 'Open troubleshooting for Printer 1' });
    button.focus();
    await user.keyboard('{Enter}');

    const dialog = screen.getByRole('dialog', { name: 'Troubleshooting' });
    expect(container).not.toContainElement(dialog);
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(within(dialog).getByText('Printer 1')).toBeInTheDocument();
    expect(within(dialog).getByRole('list', { name: 'Common troubleshooting steps' })).toBeInTheDocument();
    expect(within(dialog).getByText('ping 192.168.1.100')).toBeInTheDocument();
    expect(within(dialog).getByText('systemctl status moonraker')).toBeInTheDocument();
    expect(within(dialog).getByRole('link', { name: /^http:\/\/printer-1\.local/ }))
      .toHaveAttribute('href', 'http://printer-1.local');
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Close troubleshooting' })).toHaveFocus());
    await user.tab({ shift: true });
    expect(within(dialog).getByRole('link', { name: /^Moonraker Docs/ })).toHaveFocus();
    await user.tab();
    expect(within(dialog).getByRole('button', { name: 'Close troubleshooting' })).toHaveFocus();

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await waitFor(() => expect(button).toHaveFocus());
  });

  it('closes from the close button and backdrop, but not from clicks on guide content', async () => {
    const user = userEvent.setup();
    render(renderCard(makeOfflinePrinter()));
    const button = screen.getByRole('button', { name: 'Open troubleshooting for Printer 1' });
    await user.click(button);
    await user.click(screen.getByText('Common Checks'));
    expect(screen.getByRole('dialog', { name: 'Troubleshooting' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Close troubleshooting' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    await user.click(button);
    await user.click(screen.getByRole('dialog', { name: 'Troubleshooting' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('removes the control and any open sidebar when the printer reconnects', async () => {
    const user = userEvent.setup();
    const { rerender } = render(renderCard(makeOfflinePrinter()));
    await user.click(screen.getByRole('button', { name: 'Open troubleshooting for Printer 1' }));
    expect(screen.getByRole('dialog', { name: 'Troubleshooting' })).toBeInTheDocument();

    rerender(renderCard(makeOfflinePrinter({ isOnline: true, state: 'Idle', isReachable: true })));
    expect(screen.queryByRole('button', { name: 'Open troubleshooting for Printer 1' })).not.toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    rerender(renderCard(makeOfflinePrinter()));
    expect(screen.getByRole('button', { name: 'Open troubleshooting for Printer 1' })).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});
