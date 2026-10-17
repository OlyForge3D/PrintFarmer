import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PrinterSensorsPanel } from '../PrinterSensorsPanel';
import type { PrinterSensor } from '@/types/api';

const mocks = vi.hoisted(() => ({
  setPrinterSensor: vi.fn(),
  toastError: vi.fn(),
}));

vi.mock('@/services/api', () => ({ apiClient: { setPrinterSensor: mocks.setPrinterSensor } }));
vi.mock('sonner', () => ({ toast: { error: mocks.toastError } }));

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
    for (const name of ['Temperatures', 'Environment', 'Fans', 'Filament']) {
      expect(screen.getByRole('region', { name })).toBeInTheDocument();
    }
    expect(screen.getByLabelText('Chamber Temperature: 31.2°C')).toBeInTheDocument();
    expect(screen.getByLabelText('Enclosure Humidity: 45%')).toBeInTheDocument();
    expect(screen.getByLabelText('Enclosure Pressure: 1013 hPa')).toBeInTheDocument();
    expect(screen.getByLabelText('Runout Filament: Empty')).toBeInTheDocument();
    expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument();
  });

  it('renders inputs only for writable readings in a mixed list', () => {
    renderPanel([fan, hotendFan], { canControl: true });
    expect(screen.getAllByRole('spinbutton')).toHaveLength(1);
    const input = screen.getByRole('spinbutton', { name: 'Set Part fan Speed' });
    expect(input).toHaveAttribute('min', '0');
    expect(input).toHaveAttribute('max', '100');
    expect(input).toHaveAttribute('step', '1');
    expect(screen.getByLabelText('Part fan RPM: 3200 RPM')).toBeInTheDocument();
  });

  it('marks unavailable sensors and disables their control', () => {
    renderPanel([{ ...fan, available: false }], { canControl: true });
    expect(screen.getByText('Unavailable')).toBeInTheDocument();
    expect(screen.getByRole('spinbutton')).toBeDisabled();
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
    renderPanel([fan], { canControl: true });
    const input = screen.getByRole('spinbutton', { name: 'Set Part fan Speed' });
    await user.clear(input);
    await user.type(input, '65');
    await user.click(screen.getByRole('button', { name: /Apply Set Part fan Speed/ }));
    expect(mocks.setPrinterSensor).toHaveBeenCalledWith('p1', 'fan part', { quantity: 'FanSpeed', value: 65 });
    expect(screen.getByLabelText('Part fan Speed: 65%')).toBeInTheDocument();
    resolve({ success: true });
    await waitFor(() => expect(screen.getByLabelText('Part fan Speed: 40%')).toBeInTheDocument());
  });

  it('rejects out-of-range values without calling the API', async () => {
    const user = userEvent.setup();
    renderPanel([fan], { canControl: true });
    const input = screen.getByRole('spinbutton', { name: 'Set Part fan Speed' });
    await user.clear(input);
    await user.type(input, '150');
    expect(screen.getByRole('button', { name: /Apply/ })).toBeDisabled();
    await user.keyboard('{Enter}');
    expect(mocks.setPrinterSensor).not.toHaveBeenCalled();
  });

  it('rolls back and toasts when the control call is rejected', async () => {
    const user = userEvent.setup();
    mocks.setPrinterSensor.mockRejectedValue(new Error('Read-only sensor'));
    renderPanel([fan], { canControl: true });
    const input = screen.getByRole('spinbutton', { name: 'Set Part fan Speed' });
    await user.clear(input);
    await user.type(input, '65{Enter}');
    await waitFor(() => expect(mocks.toastError).toHaveBeenCalledTimes(1));
    expect(screen.getByLabelText('Part fan Speed: 40%')).toBeInTheDocument();
  });

  it('rolls back and toasts when the command result reports failure', async () => {
    const user = userEvent.setup();
    mocks.setPrinterSensor.mockResolvedValue({ success: false, error: 'Printer busy' });
    renderPanel([fan], { canControl: true });
    const input = screen.getByRole('spinbutton', { name: 'Set Part fan Speed' });
    await user.clear(input);
    await user.type(input, '10{Enter}');
    await waitFor(() => expect(mocks.toastError).toHaveBeenCalledWith('Printer busy'));
    expect(screen.getByLabelText('Part fan Speed: 40%')).toBeInTheDocument();
  });
});
