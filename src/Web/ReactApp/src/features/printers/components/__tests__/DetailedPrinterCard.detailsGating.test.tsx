import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { PrinterBackend, type MmuStatus, type Printer } from '@/types/api';

const usePrinterDetailsMock = vi.hoisted(() =>
  vi.fn(() => ({ data: undefined, isLoading: false }))
);
const useSpoolmanConfiguredMock = vi.hoisted(() => vi.fn(() => ({ ready: true })));

vi.mock('@/common/hooks/useApi', () => ({
  usePrinterDetails: usePrinterDetailsMock,
}));

vi.mock('@/common/hooks/useSpoolmanConfigured', () => ({
  useSpoolmanConfigured: useSpoolmanConfiguredMock,
}));

vi.mock('@tanstack/react-query', () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn(), setQueryData: vi.fn() }),
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
vi.mock('@/features/printers/components/ToolheadSpoolPicker', () => ({
  ToolheadSpoolPicker: () => <div data-testid="toolhead-spool-picker" />,
}));
vi.mock('@/features/printers/components/AmsSlotVisualization', () => ({
  AmsSlotVisualization: () => <div data-testid="ams-slot-visualization" />,
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
  });

  it('does not eagerly request printer details when the printer already reports MMU/AMS gate data', () => {
    const printer = makePrinter({
      mmuStatus: { gates: [{ index: 0, status: 1, color: '#fff', material: 'PLA' }] } as unknown as MmuStatus,
    } as Partial<Printer>);

    render(<DetailedPrinterCard printer={printer} />);

    expect(usePrinterDetailsMock).toHaveBeenCalledWith(
      'printer-1',
      expect.objectContaining({ enabled: false }),
    );
  });

  it('preserves the collapsed Material Slots badge for MMU printers using only mmuStatus (no fetch needed)', () => {
    const printer = makePrinter({
      mmuStatus: { gates: [{ index: 0, status: 1, color: '#fff', material: 'PLA' }] } as unknown as MmuStatus,
    } as Partial<Printer>);

    render(<DetailedPrinterCard printer={printer} />);

    expect(screen.getByText('Material Slots')).toBeInTheDocument();
  });

  it('eagerly requests printer details collapsed when there is no MMU/AMS signal at all (narrowest safe gate)', () => {
    const printer = makePrinter();

    render(<DetailedPrinterCard printer={printer} />);

    expect(usePrinterDetailsMock).toHaveBeenCalledWith(
      'printer-1',
      expect.objectContaining({ enabled: true }),
    );
  });

  it('requests printer details once the Z-Offset wizard is opened, even for an MMU printer that was otherwise deferred', () => {
    const printer = makePrinter({
      mmuStatus: { gates: [{ index: 0, status: 1, color: '#fff', material: 'PLA' }] } as unknown as MmuStatus,
    } as Partial<Printer>);

    render(<DetailedPrinterCard printer={printer} />);
    expect(usePrinterDetailsMock).toHaveBeenLastCalledWith(
      'printer-1',
      expect.objectContaining({ enabled: false }),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Calibrate Z-Offset' }));

    expect(usePrinterDetailsMock).toHaveBeenLastCalledWith(
      'printer-1',
      expect.objectContaining({ enabled: true }),
    );
  });

  it('never requests printer details when Spoolman is not configured, regardless of the collapsed gate', () => {
    useSpoolmanConfiguredMock.mockReturnValue({ ready: false });
    const printer = makePrinter(); // no mmuStatus — would otherwise be the eager/collapsed-probe case

    render(<DetailedPrinterCard printer={printer} />);

    expect(usePrinterDetailsMock).toHaveBeenCalledWith(
      'printer-1',
      expect.objectContaining({ enabled: false }),
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