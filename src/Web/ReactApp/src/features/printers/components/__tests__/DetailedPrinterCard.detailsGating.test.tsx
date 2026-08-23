import '@testing-library/jest-dom';
import React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { PrinterBackend, type MmuStatus, type Printer } from '@/types/api';

const usePrinterDetailsMock = vi.hoisted(() =>
  vi.fn(() => ({ data: undefined, isLoading: false }))
);
const useSpoolmanConfiguredMock = vi.hoisted(() => vi.fn(() => ({ ready: true })));
const printerHistoryModalMock = vi.hoisted(() => vi.fn(() => null));
const calibrationSetupPromptRenderMock = vi.hoisted(() => vi.fn());

vi.mock('@/common/hooks/useApi', () => ({
  usePrinterDetails: usePrinterDetailsMock,
  usePrintJobObjects: () => ({ data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() }),
  queryKeys: { printJobObjects: (printerId: string) => ['printJobObjects', printerId] },
}));

vi.mock('@/common/hooks/useSpoolmanConfigured', () => ({
  useSpoolmanConfigured: useSpoolmanConfiguredMock,
}));

vi.mock('@/services/maintenanceService', () => ({
  maintenanceService: { getPrinterStatistics: vi.fn() },
}));

vi.mock('@tanstack/react-query', () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn(), setQueryData: vi.fn() }),
  useQuery: () => ({ data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() }),
  useMutation: () => ({ mutate: vi.fn(), isPending: false }),
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

vi.mock('@/features/printers/components/PrinterHistoryModal', () => ({
  PrinterHistoryModal: (props: { isOpen: boolean }) =>
    printerHistoryModalMock(props),
}));
vi.mock('@/features/printers/components/PrinterFilesModal', () => ({ PrinterFilesModal: () => null }));
vi.mock('@/features/printers/components/SpoolPickerModal', () => ({ SpoolPickerModal: () => null }));
vi.mock('@/features/printers/components/MaterialLoadout', () => ({
  MaterialLoadout: () => <div data-testid="material-loadout" />,
}));
vi.mock('@/features/printers/components/MmuControlBox', () => ({
  MmuControlBox: () => <div data-testid="ams-control-box" />,
}));
vi.mock('@/features/printers/components/TemperatureControlSection', () => ({
  TemperatureControlSection: () => <div data-testid="temp-section" />,
}));
vi.mock('@/features/printers/components/MovementControlSection', () => ({
  MovementControlSection: ({ rightContent }: { rightContent?: React.ReactNode }) => (
    <div data-testid="movement-section">{rightContent}</div>
  ),
}));
vi.mock('@/features/printers/components/FilamentControlSection', () => ({
  FilamentControlSection: () => <div data-testid="filament-section" />,
}));
vi.mock('@/features/printers/components/PrinterActionBar', () => ({ PrinterActionBar: () => <div data-testid="action-bar" /> }));
vi.mock('@/features/printers/components/BedClearBanner', () => ({ BedClearBanner: () => null }));
vi.mock('@/features/printers/components/PrintProgressBar', () => ({ PrintProgressBar: () => <div data-testid="print-progress" /> }));
vi.mock('@/features/printers/components/EstimatedCompletionBadge', () => ({ EstimatedCompletionBadge: () => null }));
vi.mock('@/features/printers/components/FailureDetectionBadge', () => ({ FailureDetectionBadge: () => null }));
vi.mock('@/features/printers/components/FailureDetectionMonitoringBadge', () => ({ FailureDetectionMonitoringBadge: () => null }));
vi.mock('@/features/printers/components/CalibrationSetupPrompt', () => ({
  CalibrationSetupPrompt: (props: Record<string, unknown>) => {
    calibrationSetupPromptRenderMock(props);
    return null;
  },
}));
vi.mock('@/features/printers/components/FailureDetectionMonitoringSummary', () => ({ FailureDetectionMonitoringSummary: () => null }));
vi.mock('@/features/printers/components/OfflineTroubleshootingGuide', () => ({ OfflineTroubleshootingGuide: () => null }));
vi.mock('@/features/printers/components/PrinterCameraPreview', () => ({ PrinterCameraPreview: () => <div data-testid="camera-preview" /> }));
vi.mock('@/features/printers/components/ZOffsetCalibrationWizard', () => ({
  ZOffsetCalibrationWizard: () => <div data-testid="zoffset-wizard" />,
}));
vi.mock('@/services/api', () => ({ apiClient: {} }));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() } }));

import { DetailedPrinterCard } from '../DetailedPrinterCard';

function makePrinter(overrides: Partial<Printer> = {}): Printer {
  return {
    id: 'printer-1',
    name: 'Printer 1',
    backend: PrinterBackend.Moonraker,
    backendUrl: 'http://printer-1.local',
    frontendUrl: 'http://printer-1.local',
    isOnline: true,
    isEnabled: true,
    isReachable: true,
    state: 'Idle',
    progress: 0,
    ...overrides,
  } as Printer;
}

describe('DetailedPrinterCard printerDetails gating (#1146 item 4)', () => {
  beforeEach(() => {
    usePrinterDetailsMock.mockClear();
    usePrinterDetailsMock.mockReturnValue({ data: undefined, isLoading: false });
    useSpoolmanConfiguredMock.mockReturnValue({ ready: true });
    printerHistoryModalMock.mockClear();
  });

  describe('DetailedPrinterCard history modal gating', () => {
    it('keeps the always-mounted modal closed until history is requested', () => {
      render(<DetailedPrinterCard printer={makePrinter()} />);

      expect(printerHistoryModalMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ isOpen: false }),
      );

      fireEvent.click(screen.getByRole('button', { name: 'View print history' }));

      expect(printerHistoryModalMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ isOpen: true }),
      );
    });
  });

  it('eagerly requests printer details when the printer reports live MMU/AMS gate data so topology arrives before assignment (#1585 blocker 2)', () => {
    // Previously the details fetch was deferred whenever `mmuStatus.gates`
    // was populated, on the theory that live MMU gates already told the card
    // enough to render the materials rail. But without persisted topology the
    // card cannot safely translate live-gate indices to backend API indices,
    // so `MaterialLoadout` locks assignment until topology loads. That's a
    // silent dead end unless topology is actually being fetched. Enable it.
    const printer = makePrinter({
      mmuStatus: { gates: [{ index: 0, status: 1, color: '#fff', material: 'PLA' }] } as unknown as MmuStatus,
    } as Partial<Printer>);

    render(<DetailedPrinterCard printer={printer} />);

    expect(usePrinterDetailsMock).toHaveBeenCalledWith(
      'printer-1',
      expect.objectContaining({ enabled: true }),
    );
  });

  it('preserves the collapsed materials module for MMU printers using only mmuStatus (no fetch needed)', () => {
    const printer = makePrinter({
      mmuStatus: { gates: [{ index: 0, status: 1, color: '#fff', material: 'PLA' }] } as unknown as MmuStatus,
    } as Partial<Printer>);

    render(<DetailedPrinterCard printer={printer} />);

    expect(screen.getByTestId('material-loadout')).toBeInTheDocument();
  });

  it('eagerly requests printer details collapsed when there is no MMU/AMS signal at all (narrowest safe gate)', () => {
    const printer = makePrinter();

    render(<DetailedPrinterCard printer={printer} />);

    expect(usePrinterDetailsMock).toHaveBeenCalledWith(
      'printer-1',
      expect.objectContaining({ enabled: true }),
    );
  });

  it('does not request printer details when Spoolman is off and the card already carries a revision', () => {
    useSpoolmanConfiguredMock.mockReturnValue({ ready: false });
    // The card already has its concurrency token, so there is nothing to
    // recover — the details fetch stays disabled to avoid an unnecessary request.
    const printer = makePrinter({ rowVersion: 'printer-v1' }); // no mmuStatus

    render(<DetailedPrinterCard printer={printer} />);

    expect(usePrinterDetailsMock).toHaveBeenCalledWith(
      'printer-1',
      expect.objectContaining({ enabled: false }),
    );
  });

  it('requests printer details when the card lacks a revision, even with Spoolman off, to recover the concurrency token (revision-guard fallback)', () => {
    useSpoolmanConfiguredMock.mockReturnValue({ ready: false });
    // The compact list DTO can omit `rowVersion`; the spool mutation guards need
    // an authoritative token, so the details fetch must run regardless of Spoolman.
    const printer = makePrinter(); // no rowVersion, no mmuStatus

    render(<DetailedPrinterCard printer={printer} />);

    expect(usePrinterDetailsMock).toHaveBeenCalledWith(
      'printer-1',
      expect.objectContaining({ enabled: true }),
    );
  });
});

describe('DetailedPrinterCard Open in Browser (#1546)', () => {
  beforeEach(() => {
    useSpoolmanConfiguredMock.mockReturnValue({ ready: true });
  });

  it('disables Open in Browser with an explanatory tooltip for a TestEmulator internal-only host', () => {
    const printer = makePrinter({
      frontendUrl: 'http://testemulator-11111111-1111-1111-1111-111111111111',
    });

    render(<DetailedPrinterCard printer={printer} />);

    expect(screen.queryByRole('link', { name: /open printer/i })).not.toBeInTheDocument();
    const disabledButton = screen.getByRole('button', {
      name: `Open in Browser unavailable for printer ${printer.name}: not available for simulated test printers`,
    });
    expect(disabledButton).not.toBeDisabled();
    expect(disabledButton).toHaveAttribute('aria-disabled', 'true');
    expect(disabledButton).toHaveAttribute('title', 'Not available for simulated test printers');
  });

  it('keeps a real printer browser URL as a working link', () => {
    const printer = makePrinter({ frontendUrl: 'http://printer-1.local' });

    render(<DetailedPrinterCard printer={printer} />);

    expect(screen.getByRole('link', { name: `Open printer ${printer.name} in new tab` }))
      .toHaveAttribute('href', 'http://printer-1.local');
  });
});

describe('DetailedPrinterCard inline details (#1584)', () => {
  it('renders the inline detail sections without an Open details sidebar control', () => {
    render(<DetailedPrinterCard printer={makePrinter()} />);

    expect(screen.queryByRole('button', { name: 'Open details sidebar' })).not.toBeInTheDocument();
    // The card folds the sidebar's informational sections in directly, so the
    // detail is reachable without opening a sidebar. Field-level coverage of
    // those sections lives in DetailedPrinterCard.inlineDetails.test.tsx.
    expect(screen.getByText('Statistics')).toBeInTheDocument();
    expect(screen.getByText('Version')).toBeInTheDocument();
  });
});

describe('DetailedPrinterCard calibration setup wiring (#1923)', () => {
  beforeEach(() => {
    calibrationSetupPromptRenderMock.mockClear();
  });

  it('wires CalibrationSetupPrompt with this printer\'s id, name, and rowVersion so the onboarding affordance opens setup for the correct printer', () => {
    const printer = makePrinter({ id: 'printer-99', name: 'Printer Ninety-Nine', rowVersion: 'rv-3' });

    render(<DetailedPrinterCard printer={printer} />);

    expect(calibrationSetupPromptRenderMock).toHaveBeenCalledWith(
      expect.objectContaining({
        printerId: 'printer-99',
        printerName: 'Printer Ninety-Nine',
        rowVersion: 'rv-3',
      }),
    );
  });
});