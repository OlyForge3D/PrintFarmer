import { act, fireEvent, render, renderHook, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useState, type PropsWithChildren } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PrinterBackend, type CommandResult, type Printer, type PrinterBackendCapabilitiesDto } from '@/types/api';
import { DetailedPrinterCard } from '@/features/printers/components/DetailedPrinterCard';
import { PrinterDetailsSidebar } from '@/features/printers/components/PrinterDetailsSidebar';
import { usePrinterControls } from '@/features/printers/hooks/use-printer-controls';

const commands = vi.hoisted(() => ({
  cancelPrint: vi.fn(), pausePrint: vi.fn(), resumePrint: vi.fn(),
  emergencyStop: vi.fn(), firmwareRestart: vi.fn(), disableMotors: vi.fn(),
  homePrinter: vi.fn(), homeXY: vi.fn(), homeZ: vi.fn(),
  movePrinter: vi.fn(), movePrinterTo: vi.fn(), extrudeFilament: vi.fn(),
  setTemperatures: vi.fn(), turnOffHeaters: vi.fn(),
  loadFilament: vi.fn(), unloadFilament: vi.fn(),
  getPrinterVersionInfo: vi.fn(),
}));
const errorToast = vi.hoisted(() => vi.fn());
vi.mock('@/services/api', () => ({ apiClient: commands }));
vi.mock('sonner', () => ({ toast: { error: errorToast, success: vi.fn(), info: vi.fn() } }));
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: () => ({ hasPermission: () => true }) }));
vi.mock('@/features/printers/hooks/use-printer-controls-mode', () => ({
  usePrinterControlsMode: () => ({ mode: 'guided' }),
}));
vi.mock('@/common/hooks/usePrinterDisplay', () => ({ usePrinterDisplay: (printer: Printer) => printer }));
vi.mock('@/common/hooks/useSpoolmanConfigured', () => ({ useSpoolmanConfigured: () => ({ ready: false }) }));
vi.mock('@/common/hooks/useApi', () => ({
  queryKeys: { printers: ['printers'], printJobObjects: (id: string) => ['printers', id, 'objects'] },
  usePrinter: () => ({ data: undefined, isLoading: false, refetch: vi.fn() }),
  usePrinterDetails: () => ({ data: undefined }),
  usePrintJobObjects: () => ({ data: undefined }),
}));
vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAutoDispatchStatus: () => ({ data: undefined }),
  useSetAutoDispatchEnabled: () => ({ mutateAsync: vi.fn() }),
}));
vi.mock('@/features/printers/hooks/useFailureDetectionAlert', () => ({
  useFailureDetectionAlert: () => ({ event: undefined, recentEvents: [] }),
}));
vi.mock('@/features/printers/hooks/useFailureDetectionPolling', () => ({
  useFailureDetectionPollingEnabled: () => false,
}));
vi.mock('@/features/printers/hooks/usePrinterFailureDetectionStatus', () => ({
  usePrinterFailureDetectionStatus: () => ({ printerStatus: undefined }),
}));
vi.mock('@/features/printers/components/PrinterHistoryModal', () => ({ PrinterHistoryModal: () => null }));
vi.mock('@/features/printers/components/PrinterFilesModal', () => ({ PrinterFilesModal: () => null }));
vi.mock('@/features/printers/components/SpoolPickerModal', () => ({ SpoolPickerModal: () => null }));
vi.mock('@/features/printers/components/PrinterServiceStatus', () => ({ PrinterServiceStatus: () => null }));
vi.mock('@/features/printers/components/PrinterTroubleshootingButton', () => ({ PrinterTroubleshootingButton: () => null }));
vi.mock('@/features/printers/components/BedClearEvidencePanel', () => ({ BedClearEvidencePanel: () => null }));
vi.mock('@/features/printers/components/RuntimeFactorControls', () => ({ RuntimeFactorControls: () => null }));

const printer: Printer = {
  id: 'printer-1', name: 'Test printer', backend: PrinterBackend.PrusaLink,
  backendUrl: 'http://printer.local', isOnline: true, isEnabled: true,
  state: 'Printing', jobName: 'folder/part.gcode', fileName: 'part.gcode', homedAxes: 'xyz',
};
const capabilities: PrinterBackendCapabilitiesDto = {
  printerId: printer.id, printerName: printer.name, backend: printer.backend,
  supportsCamera: false, supportsFileDownload: false, supportsFileList: false,
  supportsFileUpload: false, supportsStartPrint: false, supportsControlOperations: true,
  supportsFileMetadata: false, supportsMovement: true, supportsRelativeMovement: true,
  supportsAbsoluteMovement: true, supportsHoming: true, supportsHomingXY: true, supportsHomingZ: true,
  supportsTemperatureControl: true, supportsPrinterInformation: false,
  supportsHistory: false, supportsFilamentControl: true, supportsExtrusion: true,
  supportsObjectExclusion: false,
};

function Wrapper({ children }: PropsWithChildren) {
  const [client] = useState(() => new QueryClient({ defaultOptions: { queries: { retry: false } } }));
  return <QueryClientProvider client={client}>
    {children}
  </QueryClientProvider>;
}

beforeEach(() => {
  for (const command of Object.values(commands)) command.mockReset().mockResolvedValue({ success: true });
  errorToast.mockReset();
  localStorage.clear();
});

describe.each(['card', 'sidebar'] as const)('shared printer controls on %s', surface => {
  function content(value: Printer, caps = capabilities, selectedId = value.id) {
    return surface === 'card'
      ? <DetailedPrinterCard printer={value} backendCapabilities={caps} />
      : <PrinterDetailsSidebar printerId={selectedId} printer={value} backendCapabilities={caps} onClose={vi.fn()} />;
  }
  const cancelButton = () => screen.getByRole('button', { name: surface === 'card' ? 'Cancel' : 'Cancel print' });
  const dialog = () => screen.getByRole('dialog', { name: 'Cancel print?' });

  it('dismisses without a command and confirms exactly once, including a same-tick duplicate', async () => {
    let finish!: (result: CommandResult) => void;
    commands.cancelPrint.mockImplementation(() => new Promise<CommandResult>(resolve => { finish = resolve; }));
    render(content(printer), { wrapper: Wrapper });
    fireEvent.click(cancelButton());
    expect(dialog()).toHaveTextContent('part.gcode');
    expect(dialog()).toHaveTextContent(printer.name);
    expect(commands.cancelPrint).not.toHaveBeenCalled();
    fireEvent.click(within(dialog()).getByRole('button', { name: 'Keep printing' }));
    expect(commands.cancelPrint).not.toHaveBeenCalled();
    fireEvent.click(cancelButton());
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('dialog', { name: 'Cancel print?' })).not.toBeInTheDocument();
    expect(commands.cancelPrint).not.toHaveBeenCalled();
    fireEvent.click(cancelButton());
    const confirm = within(dialog()).getByRole('button', { name: 'Cancel print' });
    fireEvent.click(confirm);
    fireEvent.click(confirm);
    expect(commands.cancelPrint).toHaveBeenCalledExactlyOnceWith(printer.id);
    expect(cancelButton()).toBeDisabled();
    await act(async () => finish({ success: true }));
  });

  it.each([
    ['printer', { ...printer, id: 'printer-2' }],
    ['filename', { ...printer, fileName: 'other.gcode' }],
    ['job path with unchanged filename', { ...printer, jobName: 'different/part.gcode' }],
    ['idle', { ...printer, state: 'Idle' }],
    ['offline', { ...printer, isOnline: false }],
    ['disabled', { ...printer, isEnabled: false }],
  ])('invalidates consent on %s changes and does not resurrect it', (_label, changed) => {
    const { rerender } = render(content(printer), { wrapper: Wrapper });
    fireEvent.click(cancelButton());
    rerender(content(changed));
    expect(screen.queryByRole('dialog', { name: 'Cancel print?' })).not.toBeInTheDocument();
    rerender(content(printer));
    expect(screen.queryByRole('dialog', { name: 'Cancel print?' })).not.toBeInTheDocument();
    expect(commands.cancelPrint).not.toHaveBeenCalled();
  });

  it('invalidates consent when control capability is withdrawn', () => {
    const { rerender } = render(content(printer), { wrapper: Wrapper });
    fireEvent.click(cancelButton());
    rerender(content(printer, { ...capabilities, supportsControlOperations: false }));
    expect(screen.queryByRole('dialog', { name: 'Cancel print?' })).not.toBeInTheDocument();
    rerender(content(printer));
    expect(screen.queryByRole('dialog', { name: 'Cancel print?' })).not.toBeInTheDocument();
    expect(commands.cancelPrint).not.toHaveBeenCalled();
  });

  it.each(['response', 'rejection'])('shows %s cancellation errors', async failure => {
    if (failure === 'response') commands.cancelPrint.mockResolvedValue({ success: false, error: 'Printer refused cancellation' });
    else commands.cancelPrint.mockRejectedValue(new Error('Printer refused cancellation'));
    render(content(printer), { wrapper: Wrapper });
    fireEvent.click(cancelButton());
    fireEvent.click(within(dialog()).getByRole('button', { name: 'Cancel print' }));
    await waitFor(() => expect(errorToast).toHaveBeenCalledWith(expect.stringContaining('Printer refused cancellation')));
    expect(commands.cancelPrint).toHaveBeenCalledExactlyOnceWith(printer.id);
  });

  it('keeps pause, resume, emergency stop and firmware restart immediate', async () => {
    const { rerender } = render(content(printer), { wrapper: Wrapper });
    fireEvent.click(screen.getByRole('button', { name: surface === 'card' ? 'Pause' : 'Pause print' }));
    await waitFor(() => expect(commands.pausePrint).toHaveBeenCalledExactlyOnceWith(printer.id));
    rerender(content({ ...printer, state: 'Paused' }));
    const resume = screen.getByRole('button', { name: surface === 'card' ? 'Resume' : 'Resume print' });
    await waitFor(() => expect(resume).toBeEnabled());
    fireEvent.click(resume);
    await waitFor(() => expect(commands.resumePrint).toHaveBeenCalledExactlyOnceWith(printer.id));
    const stop = screen.getByRole('button', { name: 'Emergency Stop' });
    await waitFor(() => expect(stop).toBeEnabled());
    fireEvent.click(stop);
    await waitFor(() => expect(commands.emergencyStop).toHaveBeenCalledExactlyOnceWith(printer.id));
    rerender(content({ ...printer, state: 'Shutdown' }));
    const restart = screen.getByRole('button', { name: 'Firmware Restart' });
    await waitFor(() => expect(restart).toBeEnabled());
    fireEvent.click(restart);
    await waitFor(() => expect(commands.firmwareRestart).toHaveBeenCalledExactlyOnceWith(printer.id));
    expect(commands.cancelPrint).not.toHaveBeenCalled();
  });

  it('retains per-axis affirmative homing after a full snapshot removes homing', async () => {
    const idle = { ...printer, state: 'Idle', homedAxes: 'x' };
    const { rerender } = render(content(idle), { wrapper: Wrapper });
    expect(screen.getByRole('button', { name: 'Jog X positive' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Jog Y positive' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Jog Z positive' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Jog X positive' }));
    await waitFor(() => expect(commands.movePrinter).toHaveBeenCalledExactlyOnceWith(printer.id, { x: surface === 'card' ? 1 : 10 }));
    rerender(content({ ...idle, homedAxes: undefined }));
    for (const axis of ['X', 'Y', 'Z']) {
      expect(screen.getByRole('button', { name: `Jog ${axis} positive` })).toBeDisabled();
    }
    expect(commands.movePrinter).toHaveBeenCalledTimes(1);
  });

  it('keeps homing and absolute controls fail-closed without explicit capabilities', () => {
    render(content({ ...printer, state: 'Idle' }, {
      ...capabilities, supportsHoming: undefined, supportsHomingXY: undefined, supportsHomingZ: undefined,
      supportsAbsoluteMovement: undefined, supportsRelativeMovement: false,
    }), { wrapper: Wrapper });
    for (const title of ['Home all axes', 'Home X/Y', 'Home Z']) expect(screen.getByTitle(title)).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Absolute' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Jog X positive' })).toBeDisabled();
    expect(commands.homePrinter).not.toHaveBeenCalled();
  });
});

describe('shared command handlers', () => {
  it('guards duplicate confirmation callbacks before React publishes pending state', async () => {
    let finish!: (value: CommandResult) => void;
    commands.cancelPrint.mockImplementation(() => new Promise<CommandResult>(resolve => { finish = resolve; }));
    const { result } = renderHook(() => usePrinterControls(printer, capabilities), { wrapper: Wrapper });
    await act(async () => { await result.current.actionBarProps.onControlAction('cancel'); });
    const confirm = result.current.cancelConfirmation.onConfirm;
    act(() => { confirm(); confirm(); });
    expect(commands.cancelPrint).toHaveBeenCalledExactlyOnceWith(printer.id);
    await act(async () => finish({ success: true }));
  });

  it('invalidates sidebar consent before a newly selected printer finishes loading', () => {
    const { rerender } = render(<PrinterDetailsSidebar printerId={printer.id} printer={printer}
      backendCapabilities={capabilities} onClose={vi.fn()} />, { wrapper: Wrapper });
    fireEvent.click(screen.getByRole('button', { name: 'Cancel print' }));
    rerender(<PrinterDetailsSidebar printerId="loading-printer" printer={printer}
      backendCapabilities={capabilities} onClose={vi.fn()} />);
    expect(screen.queryByRole('dialog', { name: 'Cancel print?' })).not.toBeInTheDocument();
    expect(commands.cancelPrint).not.toHaveBeenCalled();
  });

  it('guards motion handlers as well as buttons and retains axis-specific homing APIs', async () => {
    const { result } = renderHook(() => usePrinterControls({ ...printer, state: 'Idle', homedAxes: 'x' }, capabilities), { wrapper: Wrapper });
    await act(async () => {
      await result.current.movementProps.onMove('Y', 1);
      await result.current.movementProps.onMoveTo?.({ y: 5 });
      await result.current.movementProps.onMoveTo?.({ x: Number.NaN });
    });
    expect(commands.movePrinter).not.toHaveBeenCalled();
    expect(commands.movePrinterTo).not.toHaveBeenCalled();
    await act(async () => { await result.current.movementProps.onHome('xy'); });
    expect(commands.homeXY).toHaveBeenCalledExactlyOnceWith(printer.id);
    await act(async () => { await result.current.movementProps.onHome('z'); });
    expect(commands.homeZ).toHaveBeenCalledExactlyOnceWith(printer.id);
    expect(commands.homePrinter).not.toHaveBeenCalled();
  });

  it('keeps explicit heater shutdown while printing, blocks heat presets, and enforces heater limits', async () => {
    const { result, rerender } = renderHook(({ value }) => usePrinterControls(value,
      { ...capabilities, supportsHeaterShutdown: true }, { capabilities: {
        id: 'limits-1', printerId: printer.id, printerName: printer.name,
        hasHeatedBed: true, hasEnclosure: false, multiMaterial: false, isAvailable: true,
        supportsAutoLeveling: false, lastUpdated: new Date('2026-11-15T00:00:00Z'),
        maxHotendTemp: 180, maxBedTemp: 120,
      } }),
    { initialProps: { value: printer }, wrapper: Wrapper });
    await act(async () => {
      await result.current.temperatureProps.onApplyPreset('PLA');
      await result.current.temperatureProps.onApplyPreset('cooldown');
    });
    expect(commands.turnOffHeaters).toHaveBeenCalledExactlyOnceWith(printer.id);
    expect(commands.setTemperatures).not.toHaveBeenCalled();
    rerender({ value: { ...printer, state: 'Idle' } });
    await act(async () => { await result.current.temperatureProps.onApplyPreset('PLA'); });
    expect(errorToast).toHaveBeenCalledWith(expect.stringContaining('180'));
    expect(commands.setTemperatures).not.toHaveBeenCalled();
    await act(async () => { await result.current.temperatureProps.onApplySingleHeaterPreset('bed', 'PLA'); });
    expect(commands.setTemperatures).toHaveBeenCalledExactlyOnceWith(printer.id, { bed: 60 });
  });

  it('retains filament load/unload, extrusion direction and speed conversion', async () => {
    const { result, rerender } = renderHook(({ value }) => usePrinterControls(value, capabilities),
      { initialProps: { value: { ...printer, state: 'Idle' } }, wrapper: Wrapper });
    await act(async () => { result.current.filamentControl?.onAction('load'); });
    await waitFor(() => expect(commands.loadFilament).toHaveBeenCalledExactlyOnceWith(printer.id));
    await act(async () => { result.current.filamentControl?.onAction('unload'); });
    await waitFor(() => expect(commands.unloadFilament).toHaveBeenCalledExactlyOnceWith(printer.id));
    await act(async () => {
      result.current.movementProps.onExtrudeStepChange(10);
      result.current.movementProps.onExtrudeSpeedChange(5);
    });
    await act(async () => { await result.current.movementProps.onExtrude('retract'); });
    expect(commands.extrudeFilament).toHaveBeenCalledExactlyOnceWith(printer.id, -10, 300);
    rerender({ value: { ...printer, state: 'Printing' } });
    await act(async () => { result.current.filamentControl?.onAction('load'); });
    expect(commands.loadFilament).toHaveBeenCalledTimes(1);
    expect(result.current.filamentControl?.blockedReason).toBe('Not available while printing');
  });
});
