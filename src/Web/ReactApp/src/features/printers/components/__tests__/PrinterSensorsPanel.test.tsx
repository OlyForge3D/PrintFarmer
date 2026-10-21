import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PrinterSensorsPanel, PrinterSensorsSection } from '@/features/printers/components/PrinterSensorsPanel';
import { additionalPrinterSensors, primaryHeaterPower } from '@/features/printers/utils/additionalPrinterSensors';
import type { PrinterSensor } from '@/types/api';

const mocks = vi.hoisted(() => ({
  setPrinterSensor: vi.fn(),
  toastError: vi.fn(),
}));

vi.mock('@/services/api', () => ({ apiClient: { setPrinterSensor: mocks.setPrinterSensor } }));
vi.mock('sonner', () => ({ toast: { error: mocks.toastError } }));
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: () => ({ hasPermission: () => true }) }));

const fan: PrinterSensor = {
  id: 'fan part',
  displayName: 'Part fan',
  kind: 'Fan',
  fanRole: 'PartCooling',
  readings: [
    { quantity: 'FanSpeed', value: 40, unit: 'Percent' },
    { quantity: 'FanRpm', value: 3200, unit: 'Rpm' },
  ],
  available: true,
  writable: [{ quantity: 'FanSpeed', min: 0, max: 100, step: 1, unit: 'Percent' }],
};
const hotendFan: PrinterSensor = {
  id: 'fan hotend',
  displayName: 'Hotend fan',
  kind: 'Fan',
  fanRole: 'Hotend',
  readings: [{ quantity: 'FanSpeed', value: 100, unit: 'Percent' }],
  available: true,
  writable: [],
};
const chamber: PrinterSensor = {
  id: 'chamber',
  displayName: 'Chamber',
  kind: 'Chamber',
  readings: [{ quantity: 'Temperature', value: 31.24, unit: 'Celsius' }],
  available: true,
  writable: [],
};
const bme: PrinterSensor = {
  id: 'bme',
  displayName: 'Enclosure',
  kind: 'Ambient',
  readings: [
    { quantity: 'Humidity', value: 45, unit: 'Percent' },
    { quantity: 'Pressure', value: 1013, unit: 'HectoPascal' },
  ],
  available: true,
  writable: [],
};
const runout: PrinterSensor = {
  id: 'runout',
  displayName: 'Runout',
  kind: 'FilamentRunout',
  readings: [{ quantity: 'FilamentDetected', value: false, unit: 'None' }],
  available: true,
  writable: [],
};

const renderPanel = (sensors: PrinterSensor[] | undefined, props: { canControl?: boolean; isOnline?: boolean } = {}) =>
  render(<PrinterSensorsPanel printerId="p1" sensors={sensors} {...props} />);

describe('PrinterSensorsPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.setPrinterSensor.mockResolvedValue({ success: true });
  });

  it('shows an empty state when there are no sensors', () => {
    renderPanel([]);
    expect(screen.getByText('No sensors reported.')).toBeInTheDocument();
  });

  it('renders read-only sensors in groups with formatted units and no inputs', () => {
    renderPanel([chamber, bme, hotendFan, runout], { canControl: true });
    for (const name of ['Temperatures', 'Fans & outputs', 'Filament']) {
      expect(screen.getByRole('region', { name })).toBeInTheDocument();
    }
    expect(screen.queryByRole('region', { name: 'Environment' })).not.toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Temperatures' })).toContainElement(screen.getByTestId('sensor-bme'));
    expect(screen.getByLabelText('Chamber Temperature: 31.2°C')).toBeInTheDocument();
    expect(screen.getByLabelText('Enclosure Humidity: 45%')).toBeInTheDocument();
    expect(screen.getByLabelText('Enclosure Pressure: 1013 hPa')).toBeInTheDocument();
    expect(screen.getByLabelText('Runout Filament: Empty')).toBeInTheDocument();
    expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
  });

  it('renders inputs only for writable readings in a mixed list', () => {
    renderPanel([fan, hotendFan], { canControl: true });
    expect(screen.getAllByRole('spinbutton')).toHaveLength(1);
    const input = screen.getByRole('spinbutton', { name: 'Set Part Fan Speed' });
    expect(input).toHaveAttribute('min', '0');
    expect(input).toHaveAttribute('max', '100');
    expect(input).toHaveAttribute('step', '1');
    expect(input.parentElement).toHaveClass('w-[4.5rem]');
    expect(screen.getByLabelText('Part Fan RPM: 3200 RPM')).toBeInTheDocument();
  });

  it('marks unavailable sensors and disables their control', () => {
    renderPanel([{ ...fan, available: false }], { canControl: true });
    expect(screen.getByText('Unavailable')).toBeInTheDocument();
    expect(screen.getByRole('spinbutton')).toBeDisabled();
  });

  it('hides redundant RPM quantity labels on both shared surfaces while retaining accessible context and other labels', () => {
    render(<>
      <PrinterSensorsSection printerId="card" sensors={[fan]} temperatures={{}} />
      <PrinterSensorsSection printerId="sidebar" sensors={[fan]} temperatures={{}} />
    </>);
    for (const label of screen.getAllByText('RPM', { exact: true })) expect(label).toHaveClass('sr-only');
    expect(screen.getAllByText('Part Fan', { exact: true })).toHaveLength(2);
    for (const label of screen.getAllByText('Speed', { exact: true })) expect(label).not.toHaveClass('sr-only');
    for (const reading of screen.getAllByLabelText('Part Fan RPM: 3200 RPM')) {
      expect(reading).toHaveTextContent('3200 RPM');
      expect(reading).toHaveClass('ml-auto');
    }
  });

  it('keeps RPM-only fan identity and the quantity label when no RPM value is reported', () => {
    renderPanel([{ ...fan, readings: [{ quantity: 'FanRpm', value: 3200, unit: 'Rpm' }], writable: [] },
      { ...fan, id: 'missing-rpm', displayName: 'Controller fan', readings: [
        { quantity: 'FanSpeed', value: 40, unit: 'Percent' },
        { quantity: 'FanRpm', value: Number.NaN, unit: 'Rpm' },
      ] }]);
    expect(screen.getByText('Part Fan', { exact: true })).toBeInTheDocument();
    expect(screen.getByText('RPM', { exact: true })).not.toHaveClass('sr-only');
    expect(screen.getByLabelText('Part Fan RPM: 3200 RPM')).toHaveTextContent('3200 RPM');
  });

  it('renders detected filament and tolerates null values', () => {
    renderPanel([
      { ...runout, readings: [{ quantity: 'FilamentDetected', value: true, unit: 'None' }] },
      { ...chamber, readings: [{ quantity: 'Temperature', value: null as unknown as number, unit: 'Celsius' }] },
    ]);
    expect(screen.getByLabelText('Runout Filament: Detected')).toBeInTheDocument();
    expect(screen.getByLabelText('Chamber Temperature: —')).toBeInTheDocument();
  });

  it('disables controls without control capability or when offline', () => {
    const { unmount } = renderPanel([fan], { canControl: false });
    expect(screen.getByRole('spinbutton')).toBeDisabled();
    unmount();
    renderPanel([fan], { canControl: true, isOnline: false });
    expect(screen.getByRole('spinbutton')).toBeDisabled();
  });

  it('submits the encoded sensor write and shows the optimistic value', async () => {
    const user = userEvent.setup();
    let resolve: (v: { success: boolean }) => void = () => {};
    mocks.setPrinterSensor.mockReturnValue(new Promise((r) => (resolve = r)));
    const { rerender } = renderPanel([fan], { canControl: true });
    const input = screen.getByRole('spinbutton', { name: 'Set Part Fan Speed' });
    await user.clear(input);
    await user.type(input, '65');
    await user.tab();
    expect(mocks.setPrinterSensor).toHaveBeenCalledWith('p1', 'fan part', { quantity: 'FanSpeed', value: 65 });
    expect(screen.getByLabelText('Part Fan Speed: 65%')).toBeInTheDocument();
    resolve({ success: true });
    await waitFor(() => expect(mocks.setPrinterSensor).toHaveBeenCalledTimes(1));
    // Confirmed value is retained until live data changes, then live data wins.
    expect(screen.getByLabelText('Part Fan Speed: 65%')).toBeInTheDocument();
    const live = { ...fan, readings: [{ quantity: 'FanSpeed' as const, value: 70, unit: 'Percent' as const }, fan.readings[1]] };
    rerender(<PrinterSensorsPanel printerId="p1" sensors={[live]} canControl />);
    expect(screen.getByLabelText('Part Fan Speed: 70%')).toBeInTheDocument();
    const back = { ...fan, readings: [{ quantity: 'FanSpeed' as const, value: 40, unit: 'Percent' as const }, fan.readings[1]] };
    rerender(<PrinterSensorsPanel printerId="p1" sensors={[back]} canControl />);
    expect(screen.getByLabelText('Part Fan Speed: 40%')).toBeInTheDocument();
  });

  it('rejects values not aligned to the writable step', async () => {
    const user = userEvent.setup();
    const stepped = { ...fan, writable: [{ quantity: 'FanSpeed' as const, min: 0, max: 100, step: 0.5, unit: 'Percent' as const }] };
    renderPanel([stepped], { canControl: true });
    const input = screen.getByRole('spinbutton', { name: 'Set Part Fan Speed' });
    await user.clear(input);
    await user.type(input, '10.3');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    await user.keyboard('{Enter}');
    await user.tab();
    expect(mocks.setPrinterSensor).not.toHaveBeenCalled();
    await user.click(input);
    await user.clear(input);
    await user.type(input, '10.5');
    await user.tab();
    expect(mocks.setPrinterSensor).toHaveBeenCalledWith('p1', fan.id, { quantity: 'FanSpeed', value: 10.5 });
  });

  it('saves on Tab then resumes live values when the authoritative value changes', async () => {
    const user = userEvent.setup();
    const { rerender } = renderPanel([fan], { canControl: true });
    const input = screen.getByRole('spinbutton', { name: 'Set Part Fan Speed' });
    await user.clear(input);
    await user.type(input, '12');
    expect(input).toHaveValue(12);
    await user.tab();
    expect(mocks.setPrinterSensor).toHaveBeenCalledTimes(1);
    const live = { ...fan, readings: [{ quantity: 'FanSpeed' as const, value: 55, unit: 'Percent' as const }] };
    rerender(<PrinterSensorsPanel printerId="p1" sensors={[live]} canControl />);
    expect(screen.getByRole('spinbutton', { name: 'Set Part Fan Speed' })).toHaveValue(55);
  });

  it('keeps control descriptions unique across cards and resets an invalid draft on Escape', async () => {
    const user = userEvent.setup();
    render(<><PrinterSensorsPanel printerId="p1" sensors={[fan]} canControl /><PrinterSensorsPanel printerId="p2" sensors={[fan]} canControl /></>);
    const inputs = screen.getAllByRole('spinbutton');
    expect(inputs[0].getAttribute('aria-describedby')).not.toBe(inputs[1].getAttribute('aria-describedby'));
    await user.clear(inputs[0]);
    await user.type(inputs[0], '150{Escape}');
    await user.tab();
    expect(inputs[0]).toHaveValue(40);
    expect(mocks.setPrinterSensor).not.toHaveBeenCalled();
  });

  it('rejects out-of-range values without calling the API', async () => {
    const user = userEvent.setup();
    renderPanel([fan], { canControl: true });
    const input = screen.getByRole('spinbutton', { name: 'Set Part Fan Speed' });
    await user.clear(input);
    await user.type(input, '150');
    await user.keyboard('{Enter}');
    await user.tab();
    expect(mocks.setPrinterSensor).not.toHaveBeenCalled();
  });

  it('submits Enter once even when immediately blurred and while its request is pending', async () => {
    const user = userEvent.setup();
    let resolve: (value: { success: boolean }) => void = () => {};
    mocks.setPrinterSensor.mockReturnValue(new Promise(r => { resolve = r; }));
    renderPanel([fan], { canControl: true });
    const input = screen.getByRole('spinbutton');
    await user.clear(input);
    await user.type(input, '65{Enter}{Enter}');
    await user.tab();
    expect(mocks.setPrinterSensor).toHaveBeenCalledTimes(1);
    expect(input).toBeDisabled();
    resolve({ success: true });
    await waitFor(() => expect(input).toBeEnabled());
    expect(mocks.setPrinterSensor).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: /Set/ })).not.toBeInTheDocument();
  });

  it.each(['{Tab}', '{Enter}{Tab}'])('does not write an unchanged value with %s', async (keys) => {
    const user = userEvent.setup();
    renderPanel([fan], { canControl: true });
    await user.click(screen.getByRole('spinbutton'));
    await user.keyboard(keys);
    expect(mocks.setPrinterSensor).not.toHaveBeenCalled();
  });

  it('accepts another deliberate edit after Enter without requiring a new focus event', async () => {
    renderPanel([fan], { canControl: true });
    const input = screen.getByRole('spinbutton');
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: '65' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    await waitFor(() => expect(input).toBeEnabled());
    fireEvent.change(input, { target: { value: '40' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    await waitFor(() => expect(mocks.setPrinterSensor).toHaveBeenCalledTimes(2));
    expect(mocks.setPrinterSensor).toHaveBeenLastCalledWith('p1', fan.id, { quantity: 'FanSpeed', value: 40 });
  });

  it('Escape cancels a valid change and subsequent blur does not submit it', async () => {
    const user = userEvent.setup();
    renderPanel([fan], { canControl: true });
    const input = screen.getByRole('spinbutton');
    await user.clear(input);
    await user.type(input, '65{Escape}');
    await user.tab();
    expect(mocks.setPrinterSensor).not.toHaveBeenCalled();
    expect(input).toHaveValue(40);
  });

  it('keeps all remaining heater readings inline with its target and temperature', async () => {
    const user = userEvent.setup();
    const heater: PrinterSensor = { ...chamber, displayName: 'Heater Box1',
      readings: [
        { quantity: 'Temperature', value: 36, unit: 'Celsius' },
        { quantity: 'TargetTemperature', value: 40, unit: 'Celsius' },
        { quantity: 'Power', value: 25, unit: 'Percent' },
        { quantity: 'Humidity', value: 42, unit: 'Percent' },
        { quantity: 'Pressure', value: 1013, unit: 'HectoPascal' },
        { quantity: 'Gas', value: 5, unit: 'None' },
        { quantity: 'Other', value: 7, unit: 'None' },
      ],
      writable: [
        { quantity: 'TargetTemperature', min: 0, max: 80, step: 1, unit: 'Celsius' },
        { quantity: 'Gas', min: 0, max: 100, step: 1, unit: 'None' },
      ],
    };
    render(<PrinterSensorsSection printerId="p1" sensors={[heater, fan, runout]} temperatures={{}} canControl>
      <div>Legacy temperature controls</div>
    </PrinterSensorsSection>);
    expect(screen.getAllByText('Temperatures')).toHaveLength(1);
    expect(screen.getAllByRole('region', { name: 'Fans & outputs' })[0]).toBeInTheDocument();
    expect(screen.getByLabelText('Heater Box1 Temperature: 36°C')).toBeInTheDocument();
    expect(screen.getAllByText('25%')).toHaveLength(1);
    expect(screen.getByLabelText('Heater Box1 Humidity: 42%')).toBeInTheDocument();
    expect(screen.getByLabelText('Heater Box1 Pressure: 1013 hPa')).toBeInTheDocument();
    expect(screen.getByLabelText('Heater Box1 Value: 7')).toBeInTheDocument();
    const gasInput = screen.getByRole('spinbutton', { name: 'Set Heater Box1 Gas' });
    expect(gasInput).toHaveValue(5);
    const input = screen.getByRole('spinbutton', { name: 'Set Heater Box1 Target' });
    await user.clear(input);
    await user.type(input, '50');
    await user.tab();
    expect(mocks.setPrinterSensor).toHaveBeenCalledWith('p1', 'chamber', { quantity: 'TargetTemperature', value: 50 });
  });

  it('formats names, hides underscore-prefixed objects, and preserves separate sensor IDs with the same short name', () => {
    const sensorTemperature: PrinterSensor = {
      id: 'temperature_sensor heater_box1',
      displayName: 'heater_box1',
      kind: 'Ambient',
      readings: [
        { quantity: 'Temperature', value: 37, unit: 'Celsius' },
        { quantity: 'Humidity', value: 42, unit: 'Percent' },
      ],
      available: true,
      writable: [],
    };
    const heater: PrinterSensor = {
      id: 'heater_generic heater_box1',
      displayName: 'heater_box1',
      kind: 'Other',
      readings: [{ quantity: 'TargetTemperature', value: 40, unit: 'Celsius' }],
      available: true,
      writable: [{ quantity: 'TargetTemperature', min: 0, max: 100, step: 1, unit: 'Celsius' }],
    };
    const hidden: PrinterSensor = {
      ...sensorTemperature,
      id: 'temperature_sensor _private',
      displayName: '_private',
    };
    renderPanel([sensorTemperature, heater, hidden], { canControl: true });

    expect(screen.getByTestId('sensor-temperature_sensor heater_box1')).toBeInTheDocument();
    expect(screen.getByTestId('sensor-heater_generic heater_box1')).toBeInTheDocument();
    expect(screen.getAllByText('Heater Box1', { exact: true })).toHaveLength(2);
    expect(screen.queryByTestId('sensor-temperature_sensor _private')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Heater Box1 Temperature: 37°C')).toBeInTheDocument();
    expect(screen.getByLabelText('Heater Box1 Humidity: 42%')).toBeInTheDocument();
    expect(screen.getByRole('spinbutton', { name: 'Set Heater Box1 Target' })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Environment' })).not.toBeInTheDocument();
  });

  describe('additionalPrinterSensors', () => {
    const heater = (id: string, kind: 'Nozzle' | 'Bed' = 'Nozzle'): PrinterSensor => ({
      id, kind, displayName: id, available: true,
      readings: [
        { quantity: 'Temperature', value: 30, unit: 'Celsius' },
        { quantity: 'TargetTemperature', value: 0, unit: 'Celsius' },
      ],
    });
    const temperatures = { hotendTemp: 30, hotendTarget: 0, bedTemp: 30, bedTarget: 0 };

    it.each(['nozzle', 'extruder', 'tool0', 'T0', 'TempOfNozzle'])('deduplicates only mapped primary nozzle %s', (id) => {
      expect(additionalPrinterSensors([heater(id)], temperatures)).toEqual([]);
    });

    it.each(['bed', 'heater_bed', 'TempOfHotbed'])('deduplicates mapped primary bed %s', (id) => {
      expect(additionalPrinterSensors([heater(id, 'Bed')], temperatures)).toEqual([]);
    });

    it('retains extra nozzles, independent temperatures, other quantities and unknown identities even at identical temperatures', () => {
      const extra = [heater('tool1'), heater('T1'), heater('extruder1'), heater('custom'), chamber, bme, fan, runout,
        { ...chamber, id: 'mcu', kind: 'Mcu' as const }, { ...chamber, id: 'host', kind: 'Host' as const }];
      const primary = { ...heater('extruder'), readings: [...heater('extruder').readings, { quantity: 'Power' as const, value: 42, unit: 'Watt' as const }] };
      expect(additionalPrinterSensors([primary, ...extra], temperatures)).toEqual(extra);
      expect(primaryHeaterPower([primary], 'hotend')).toBe('42 W');
    });

    it('retains primary readings not represented by the legacy controls', () => {
      expect(additionalPrinterSensors([heater('nozzle')], {})).toEqual([heater('nozzle')]);
      expect(additionalPrinterSensors([heater('nozzle')], { hotendTemp: 30 })[0].readings).toEqual([
        { quantity: 'TargetTemperature', value: 0, unit: 'Celsius' },
      ]);
    });

    it('hides a duplicate-only disclosure but preserves reported-empty and unsupported behavior', () => {
      const { rerender } = render(<PrinterSensorsSection printerId="p1" sensors={[heater('nozzle')]} temperatures={temperatures} supportsTelemetry />);
      expect(screen.queryByText('Sensors & fans')).not.toBeInTheDocument();
      rerender(<PrinterSensorsSection printerId="p1" sensors={[]} temperatures={temperatures} supportsTelemetry />);
      expect(screen.getByText('No sensors reported.')).toBeInTheDocument();
      rerender(<PrinterSensorsSection printerId="p1" sensors={undefined} temperatures={temperatures} />);
      expect(screen.queryByText('Sensors & fans')).not.toBeInTheDocument();
    });
  });

  it('rolls back and toasts when the control call is rejected', async () => {
    const user = userEvent.setup();
    mocks.setPrinterSensor.mockRejectedValue(new Error('Read-only sensor'));
    renderPanel([fan], { canControl: true });
    const input = screen.getByRole('spinbutton', { name: 'Set Part Fan Speed' });
    await user.clear(input);
    await user.type(input, '65{Enter}');
    await waitFor(() => expect(mocks.toastError).toHaveBeenCalledTimes(1));
    expect(screen.getByLabelText('Part Fan Speed: 40%')).toBeInTheDocument();
  });

  it('rolls back and toasts when the command result reports failure', async () => {
    const user = userEvent.setup();
    mocks.setPrinterSensor.mockResolvedValue({ success: false, error: 'Printer busy' });
    renderPanel([fan], { canControl: true });
    const input = screen.getByRole('spinbutton', { name: 'Set Part Fan Speed' });
    await user.clear(input);
    await user.type(input, '10{Enter}');
    await waitFor(() => expect(mocks.toastError).toHaveBeenCalledWith('Printer busy'));
    expect(screen.getByLabelText('Part Fan Speed: 40%')).toBeInTheDocument();
  });
});
