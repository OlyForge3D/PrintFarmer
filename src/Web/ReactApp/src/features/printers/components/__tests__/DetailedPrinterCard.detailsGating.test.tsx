import '@testing-library/jest-dom';
import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { PrinterBackend, type MmuStatus, type Printer, type PrinterDetails } from '@/types/api';

function chooseTemperaturePreset(value: string) {
  fireEvent.click(screen.getByRole('button', { name: 'Apply temperature preset' }));
  const label = value.includes(':') ? value.replace('hotend:', 'Extruder ').replace('bed:', 'Heater Bed ') : `Both heaters ${value}`;
  fireEvent.click(screen.getByRole('menuitem', { name: new RegExp(`^${label},`) }));
}

const sensorAuth = vi.hoisted(() => ({ allowed: true }));
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: () => ({ hasPermission: () => sensorAuth.allowed }) }));

const usePrinterDetailsMock = vi.hoisted(() =>
  vi.fn<() => { data: Partial<PrinterDetails> | undefined; isLoading: boolean }>(() => ({ data: undefined, isLoading: false }))
);
const setTemperaturesMock = vi.hoisted(() => vi.fn());
const turnOffHeatersMock = vi.hoisted(() => vi.fn());
const toastErrorMock = vi.hoisted(() => vi.fn());
const useSpoolmanConfiguredMock = vi.hoisted(() => vi.fn(() => ({ ready: true })));
const printerHistoryModalMock = vi.hoisted(() =>
  vi.fn<(props: { isOpen: boolean }) => null>(() => null)
);

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
vi.mock('@/features/printers/components/OfflineTroubleshootingGuide', () => ({ OfflineTroubleshootingGuide: () => null }));
vi.mock('@/features/printers/components/PrinterCameraPreview', () => ({ PrinterCameraPreview: () => <div data-testid="camera-preview" /> }));
vi.mock('@/services/api', () => ({ apiClient: { setTemperatures: setTemperaturesMock, turnOffHeaters: turnOffHeatersMock } }));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: toastErrorMock, info: vi.fn() } }));

import { DetailedPrinterCard } from '../DetailedPrinterCard';

function makePrinter(overrides: Partial<Printer> = {}): Printer {
  return {
    id: 'printer-1',
    name: 'Printer 1',
    backend: PrinterBackend.PrusaLink,
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
    sensorAuth.allowed = true;
    usePrinterDetailsMock.mockClear();
    usePrinterDetailsMock.mockReturnValue({ data: undefined, isLoading: false });
    useSpoolmanConfiguredMock.mockReturnValue({ ready: true });
    printerHistoryModalMock.mockClear();
    setTemperaturesMock.mockReset().mockResolvedValue({ success: true });
    turnOffHeatersMock.mockReset().mockResolvedValue({ success: true });
    toastErrorMock.mockClear();
  });

  it.each(['Printing', 'Paused'])('keeps manual edits but disables presets and uses explicit heater shutdown while %s', async state => {
    render(<DetailedPrinterCard printer={makePrinter({ state, backend: PrinterBackend.Moonraker, activeExtruderId: 'extruder',
      sensors: [{ id: 'extruder', displayName: 'Extruder', kind: 'Nozzle', available: true, readings: [],
        writable: [{ quantity: 'TargetTemperature', unit: 'Celsius', min: 0, max: 300, step: null, allowZero: true }] }],
    })} backendCapabilities={{
      printerId: 'printer-1', printerName: 'Printer 1', backend: PrinterBackend.Moonraker,
      supportsCamera: false, supportsFileDownload: false, supportsFileList: false, supportsFileUpload: false,
      supportsStartPrint: false, supportsControlOperations: false, supportsFileMetadata: false,
      supportsMovement: false, supportsTemperatureControl: true, supportsPrinterInformation: false,
      supportsHistory: false, supportsFilamentControl: false, supportsObjectExclusion: false,
      supportsHeaterShutdown: true,
    }} />);
    expect(screen.getByRole('spinbutton', { name: 'Extruder target temperature' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Apply temperature preset' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Cooldown' }));
    await waitFor(() => expect(turnOffHeatersMock).toHaveBeenCalledExactlyOnceWith('printer-1'));
    expect(setTemperaturesMock).not.toHaveBeenCalled();
  });

  it('allows the native Moonraker hotend command when REST omits its identity', () => {
    render(<DetailedPrinterCard printer={makePrinter({ backend: PrinterBackend.Moonraker })} />);
    expect(screen.getByRole('spinbutton', { name: 'Extruder target temperature' })).toBeEnabled();
  });

  it.each([
    ['permitted', true, true, true, true, true],
    ['no permission', false, true, true, true, true],
    ['disabled', true, false, true, true, true],
    ['offline', true, true, false, true, true],
    ['unavailable', true, true, true, false, true],
    ['no control capability', true, true, true, true, false],
  ])('renders shared sensors after Temps with %s gates', (_label, allowed, enabled, online, available, control) => {
    sensorAuth.allowed = allowed;
    const printer = makePrinter({
      isEnabled: enabled, isOnline: online, hotendTemp: 30, hotendTarget: 0,
      sensors: [
        { id: 'extruder', kind: 'Nozzle', displayName: 'Primary nozzle', available: true, readings: [{ quantity: 'Temperature', value: 30, unit: 'Celsius' }] },
        { id: 'extruder1', kind: 'Nozzle', displayName: 'Second nozzle', available: true, readings: [{ quantity: 'Temperature', value: 30, unit: 'Celsius' }] },
        { id: 'fan', kind: 'Fan', displayName: 'Part fan', available, readings: [{ quantity: 'FanSpeed', value: 40, unit: 'Percent' }],
          writable: [{ quantity: 'FanSpeed', min: 0, max: 100, step: 1, unit: 'Percent' }] },
        { id: 'heater_generic box1', kind: 'Chamber', displayName: 'Heater Box1', available,
          readings: [{ quantity: 'Temperature', value: 36, unit: 'Celsius' }, { quantity: 'TargetTemperature', value: 40, unit: 'Celsius' }],
          writable: [{ quantity: 'TargetTemperature', min: 0, max: 80, step: 1, unit: 'Celsius' }] },
      ],
    });
    render(<DetailedPrinterCard printer={printer} backendCapabilities={{
      printerId: printer.id, printerName: printer.name, backend: printer.backend,
      supportsCamera: false, supportsFileDownload: false, supportsFileList: false, supportsFileUpload: false,
      supportsStartPrint: false, supportsControlOperations: false, supportsFileMetadata: false,
      supportsMovement: false, supportsTemperatureControl: true, supportsPrinterInformation: false,
      supportsHistory: false, supportsFilamentControl: false, supportsObjectExclusion: false,
      supportsSensorControl: control,
    }} />);
    expect(screen.queryByText('Primary nozzle')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Second Nozzle Temperature: 30°C')).toBeInTheDocument();
    expect(screen.getAllByRole('spinbutton', { name: /^Set / })).toHaveLength(2);
    for (const input of screen.getAllByRole('spinbutton', { name: /^Set / })) {
      expect(input).toHaveProperty('disabled', !(allowed && enabled && online && control));
    }
    expect(screen.getByRole('spinbutton', { name: 'Set Heater Box1 Target' })).toBeInTheDocument();
    expect(screen.getAllByText('Temperatures')).toHaveLength(1);
    const primaryTarget = screen.getByRole('spinbutton', { name: 'Extruder target temperature' });
    expect(primaryTarget).toHaveProperty('disabled', !(allowed && enabled && online));
    expect(screen.getAllByRole('region', { name: 'Temperatures' })[0]).toContainElement(primaryTarget);
    expect(screen.getAllByRole('region', { name: 'Temperatures' })[0]).toContainElement(screen.getByTestId('sensor-extruder1'));
    expect(primaryTarget.compareDocumentPosition(screen.getByText('Miscellaneous')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it.each([
    ['Extruder', 250, 'hotend', '210.5', '220'],
    ['Heater Bed', 100, 'bed', '60.5', '65'],
  ] as const)('enforces persisted %s limits and integer precision without writable descriptors', async (name, max, heater, fractional, accepted) => {
    useSpoolmanConfiguredMock.mockReturnValue({ ready: false });
    usePrinterDetailsMock.mockReturnValue({ isLoading: false, data: { capabilities: {
      id: 'caps', printerId: 'printer-1', printerName: 'Printer 1', maxHotendTemp: 250, maxBedTemp: 100,
      hasHeatedBed: true, hasEnclosure: false, multiMaterial: false, isAvailable: true,
      supportsAutoLeveling: false, lastUpdated: new Date(),
    } } });
    render(<DetailedPrinterCard printer={makePrinter({ rowVersion: 'v1' })} />);
    const input = screen.getByRole('spinbutton', { name: `${name} target temperature` });
    expect(input).toHaveAttribute('max', String(max));
    expect(input).toHaveAttribute('step', '1');
    expect(input).toBeEnabled();
    expect(input).not.toHaveAccessibleDescription(/maximum temperature unknown/);
    for (const value of [String(max + 1), '2100', fractional]) {
      fireEvent.change(input, { target: { value } });
      fireEvent.keyDown(input, { key: 'Enter' });
      fireEvent.blur(input);
      expect(setTemperaturesMock).not.toHaveBeenCalled();
      expect(input).toHaveAttribute('aria-invalid', 'true');
    }
    fireEvent.change(input, { target: { value: accepted } });
    fireEvent.keyDown(input, { key: 'Enter' });
    await waitFor(() => expect(setTemperaturesMock).toHaveBeenCalledExactlyOnceWith('printer-1', { [heater]: Number(accepted) }));
  });

  it.each([['Extruder', 'hotend'], ['Heater Bed', 'bed']] as const)('allows integer %s entry with an associated firmware-limit warning when limits are unknown', async (name, heater) => {
    render(<DetailedPrinterCard printer={makePrinter()} />);
    const input = screen.getByRole('spinbutton', { name: `${name} target temperature` });
    expect(input).toBeEnabled();
    expect(input).not.toHaveAttribute('max');
    expect(input).toHaveAccessibleDescription(new RegExp(`${name} maximum temperature unknown.*firmware limits.*does not enforce an upper bound`));
    expect(screen.getByRole('button', { name: 'Cooldown' })).toBeEnabled();
    fireEvent.change(input, { target: { value: '60.5' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(setTemperaturesMock).not.toHaveBeenCalled();
    fireEvent.change(input, { target: { value: '65' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    await waitFor(() => expect(setTemperaturesMock).toHaveBeenCalledExactlyOnceWith('printer-1', { [heater]: 65 }));
    expect(input).toHaveAccessibleDescription(/maximum temperature unknown/);
  });

  it.each(['persisted', 'descriptor'])('rejects combined and per-heater PC presets above %s maximums without changing valid presets or cooldown', async source => {
    usePrinterDetailsMock.mockReturnValue({ isLoading: false, data: { capabilities: {
      id: 'caps', printerId: 'printer-1', printerName: 'Printer 1',
      maxHotendTemp: source === 'persisted' ? 250 : 300, maxBedTemp: source === 'persisted' ? 100 : 120,
      hasHeatedBed: true, hasEnclosure: false, multiMaterial: false, isAvailable: true,
      supportsAutoLeveling: false, lastUpdated: new Date(),
    } } });
    render(<DetailedPrinterCard printer={makePrinter({ sensors: source === 'descriptor' ? [
      { id: 'extruder', kind: 'Nozzle', displayName: 'Nozzle', available: true, readings: [],
        writable: [{ quantity: 'TargetTemperature', min: 0, max: 250, step: 1, unit: 'Celsius' }] },
      { id: 'heater_bed', kind: 'Bed', displayName: 'Bed', available: true, readings: [],
        writable: [{ quantity: 'TargetTemperature', min: 0, max: 100, step: 1, unit: 'Celsius' }] },
    ] : undefined })} />);
    const presets = screen.getByRole('button', { name: 'Apply temperature preset' });
    for (const preset of ['PC', 'hotend:PC', 'bed:PC']) {
      chooseTemperaturePreset(preset);
      expect(setTemperaturesMock).not.toHaveBeenCalled();
      expect(toastErrorMock).toHaveBeenLastCalledWith(expect.stringMatching(/exceeds maximum.*Nothing sent/));
    }
    expect(toastErrorMock).toHaveBeenCalledTimes(3);
    let count = 0;
    for (const [preset, targets] of [
      ['PLA', { hotend: 210, bed: 60 }], ['hotend:PLA', { hotend: 210 }], ['bed:PLA', { bed: 60 }],
    ] as const) {
      chooseTemperaturePreset(preset);
      await waitFor(() => expect(presets).toBeEnabled());
      expect(setTemperaturesMock).toHaveBeenCalledTimes(++count);
      expect(setTemperaturesMock).toHaveBeenLastCalledWith('printer-1', targets);
    }
    fireEvent.click(screen.getByRole('button', { name: 'Cooldown' }));
    await waitFor(() => expect(setTemperaturesMock).toHaveBeenCalledTimes(4));
    expect(setTemperaturesMock).toHaveBeenLastCalledWith('printer-1', { hotend: 0, bed: 0 });
  });

  it.each([[250, 120], [300, 100]])('does not partially apply a combined preset if only one heater exceeds %s/%s', (maxHotendTemp, maxBedTemp) => {
    usePrinterDetailsMock.mockReturnValue({ isLoading: false, data: { capabilities: {
      id: 'caps', printerId: 'printer-1', printerName: 'Printer 1', maxHotendTemp, maxBedTemp,
      hasHeatedBed: true, hasEnclosure: false, multiMaterial: false, isAvailable: true,
      supportsAutoLeveling: false, lastUpdated: new Date(),
    } } });
    render(<DetailedPrinterCard printer={makePrinter()} />);
    chooseTemperaturePreset('PC');
    expect(setTemperaturesMock).not.toHaveBeenCalled();
    expect(toastErrorMock).toHaveBeenCalledWith(expect.stringMatching(/exceeds maximum.*Nothing sent/));
  });

  it('retains all PC preset scopes and cooldown with the user-selected unknown-limit warning', async () => {
    render(<DetailedPrinterCard printer={makePrinter()} />);
    const presets = screen.getByRole('button', { name: 'Apply temperature preset' });
    let count = 0;
    for (const [preset, targets] of [
      ['PC', { hotend: 280, bed: 110 }], ['hotend:PC', { hotend: 280 }], ['bed:PC', { bed: 110 }],
    ] as const) {
      chooseTemperaturePreset(preset);
      await waitFor(() => expect(presets).toBeEnabled());
      expect(setTemperaturesMock).toHaveBeenCalledTimes(++count);
      expect(setTemperaturesMock).toHaveBeenLastCalledWith('printer-1', targets);
    }
    expect(screen.getByRole('spinbutton', { name: 'Extruder target temperature' })).toHaveAccessibleDescription(/maximum temperature unknown.*firmware limits/);
    fireEvent.click(screen.getByRole('button', { name: 'Cooldown' }));
    await waitFor(() => expect(setTemperaturesMock).toHaveBeenCalledTimes(4));
    expect(setTemperaturesMock).toHaveBeenLastCalledWith('printer-1', { hotend: 0, bed: 0 });
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

  it('represents MMU slots exactly once — via the AMS control box, not a second materials module', () => {
    const printer = makePrinter({
      mmuStatus: { gates: [{ index: 0, status: 1, color: '#fff', material: 'PLA' }] } as unknown as MmuStatus,
    } as Partial<Printer>);

    render(<DetailedPrinterCard printer={printer} />);

    expect(screen.getByTestId('ams-control-box')).toBeInTheDocument();
    expect(screen.queryByTestId('material-loadout')).not.toBeInTheDocument();
  });

  it('eagerly requests printer details collapsed when there is no MMU/AMS signal at all (narrowest safe gate)', () => {
    const printer = makePrinter();

    render(<DetailedPrinterCard printer={printer} />);

    expect(usePrinterDetailsMock).toHaveBeenCalledWith(
      'printer-1',
      expect.objectContaining({ enabled: true }),
    );
  });

  it('requests heater limits even when Spoolman is off and the card already carries a revision', () => {
    useSpoolmanConfiguredMock.mockReturnValue({ ready: false });
    // The revision alone does not supply the temperature safety limits.
    const printer = makePrinter({ rowVersion: 'printer-v1' }); // no mmuStatus

    render(<DetailedPrinterCard printer={printer} />);

    expect(usePrinterDetailsMock).toHaveBeenCalledWith(
      'printer-1',
      expect.objectContaining({ enabled: true }),
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
  it('renders without an Open details sidebar control or the sidebar-only Statistics/Version rows', () => {
    render(<DetailedPrinterCard printer={makePrinter()} />);

    expect(screen.queryByRole('button', { name: 'Open details sidebar' })).not.toBeInTheDocument();
    // Statistics and Version stay in PrinterDetailsSidebar only; on the card they
    // were always-collapsed rows that only added height.
    expect(screen.queryByText('Statistics')).not.toBeInTheDocument();
    expect(screen.queryByText('Version')).not.toBeInTheDocument();
  });
});