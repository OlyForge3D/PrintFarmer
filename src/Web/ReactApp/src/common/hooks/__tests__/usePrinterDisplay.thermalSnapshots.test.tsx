import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react';
import { usePrinterDisplay, usePrinterDisplays } from '@/common/hooks/usePrinterDisplay';
import { TemperatureControlSection } from '@/features/printers/components/TemperatureControlSection';
import { loadWireContractFixture } from '@/test/wireContracts';
import { PrinterBackend, type Printer, type PrinterStatusUpdate } from '@/types/api';

const state = vi.hoisted(() => ({
  statuses: new Map<string, PrinterStatusUpdate>(),
  setPrinterSensor: vi.fn(),
  setTemperatures: vi.fn(),
}));
vi.mock('@/common/hooks/useSignalR', () => ({
  usePrinterStatusUpdates: () => ({ printerStatuses: state.statuses }),
}));
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: () => ({ hasPermission: () => true }) }));
vi.mock('@/services/api', () => ({
  apiClient: { setPrinterSensor: state.setPrinterSensor, setTemperatures: state.setTemperatures },
}));

describe('Moonraker authoritative heater snapshots', () => {
  beforeEach(() => {
    state.statuses = new Map();
    vi.clearAllMocks();
    state.setTemperatures.mockResolvedValue({ success: true });
  });

  it('clears stale API identity and readings on both display hooks without blocking native heater commands', async () => {
    const known = loadWireContractFixture<PrinterStatusUpdate>('api/printer-status/moonraker-primary.known.json');
    const invalid = loadWireContractFixture<PrinterStatusUpdate>('api/printer-status/moonraker-primary.invalid.json');
    const unknown = loadWireContractFixture<PrinterStatusUpdate>('api/printer-status/moonraker-primary.unknown.json');
    const printer: Printer = { id: known.id, name: 'Four tool', backend: PrinterBackend.Moonraker,
      isOnline: true, activeExtruderId: 'extruder3', hotendTemp: 213, hotendTarget: 220, bedTemp: 60, bedTarget: 65 };
    state.statuses.set(printer.id, known);
    const { result, rerender } = renderHook(() => ({
      single: usePrinterDisplay(printer), bulk: usePrinterDisplays([printer])[0],
    }));
    for (const display of Object.values(result.current)) {
      expect(display.activeExtruderId).toBe('extruder3');
      expect(display.hotendTemp).toBe(213);
    }

    expect(invalid).not.toHaveProperty('hotendTemp');
    expect(invalid).not.toHaveProperty('hotendTarget');
    state.statuses.set(printer.id, invalid);
    rerender();
    for (const display of Object.values(result.current)) {
      expect(display.activeExtruderId).toBe('extruder3');
      expect(display.hotendTemp).toBeUndefined();
      expect(display.hotendTarget).toBeUndefined();
      expect(display.bedTemp).toBe(60);
    }

    expect(unknown).not.toHaveProperty('activeExtruderId');
    state.statuses.set(printer.id, unknown);
    rerender();
    for (const display of Object.values(result.current)) {
      expect(display.activeExtruderId).toBeNull();
      expect(display.hotendTemp).toBeUndefined();
      expect(display.hotendTarget).toBeUndefined();
      expect(display.bedTemp).toBeUndefined();
      expect(display.bedTarget).toBeUndefined();
    }
    const display = result.current.single;
    render(<TemperatureControlSection printerId={display.id} activeExtruderId={display.activeExtruderId}
      hotendCurrent={display.hotendTemp} hotendTarget={display.hotendTarget} sensors={display.sensors}
      temperatureActionPending={false} canSetTemperatures canCooldown
      onApplyPreset={vi.fn()} onApplySingleHeaterPreset={vi.fn()} />);
    const hotendInput = screen.getByRole('spinbutton', { name: 'Extruder target temperature' });
    const bedInput = screen.getByRole('spinbutton', { name: 'Heater Bed target temperature' });
    expect(hotendInput).toBeEnabled();
    expect(hotendInput).toHaveValue(null);
    expect(bedInput).toBeEnabled();
    expect(bedInput).toHaveValue(null);
    expect(state.setPrinterSensor).not.toHaveBeenCalled();
    expect(state.setTemperatures).not.toHaveBeenCalled();

    fireEvent.change(hotendInput, { target: { value: '220' } });
    fireEvent.keyDown(hotendInput, { key: 'Enter' });
    await waitFor(() => expect(state.setTemperatures).toHaveBeenCalledExactlyOnceWith(printer.id, { hotend: 220 }));
    expect(await screen.findByText('Accepted 220°C · awaiting telemetry')).toBeVisible();
    expect(state.setPrinterSensor).not.toHaveBeenCalled();
  });

  it.each([PrinterBackend.Moonraker, PrinterBackend.PrusaLink])('preserves omitted delta fields without a complete %s sensor snapshot', backend => {
    const printer: Printer = { id: 'p1', name: 'Printer', backend, isOnline: true,
      activeExtruderId: 'extruder3', hotendTemp: 213, hotendTarget: 220, bedTemp: 60, bedTarget: 65 };
    state.statuses.set(printer.id, { id: printer.id, isOnline: true, progress: 20 });
    const { result } = renderHook(() => usePrinterDisplay(printer));
    expect(result.current.hotendTemp).toBe(213);
    expect(result.current.hotendTarget).toBe(220);
    expect(result.current.bedTemp).toBe(60);
    expect(result.current.bedTarget).toBe(65);
    expect(result.current.activeExtruderId).toBe('extruder3');
  });

  it('does not change another backend delta when it includes an empty sensor list', () => {
    const printer: Printer = { id: 'p1', name: 'Printer', backend: PrinterBackend.PrusaLink,
      isOnline: true, hotendTemp: 213, hotendTarget: 220, bedTemp: 60, bedTarget: 65 };
    state.statuses.set(printer.id, { id: printer.id, isOnline: true, sensors: [] });
    const { result } = renderHook(() => usePrinterDisplay(printer));
    expect(result.current.hotendTemp).toBe(213);
    expect(result.current.hotendTarget).toBe(220);
    expect(result.current.bedTemp).toBe(60);
    expect(result.current.bedTarget).toBe(65);
  });
});
