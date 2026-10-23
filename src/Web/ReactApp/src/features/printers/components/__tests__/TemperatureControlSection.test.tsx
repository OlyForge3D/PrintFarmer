import type { ComponentProps } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { TemperatureControlSection } from '@/features/printers/components/TemperatureControlSection';
import { PrintProgressBar } from '@/features/printers/components/PrintProgressBar';
import type { PrinterSensor } from '@/types/api';
import { additionalPrinterSensors } from '@/features/printers/utils/additionalPrinterSensors';

const mocks = vi.hoisted(() => ({ setTemperatures: vi.fn(), setPrinterSensor: vi.fn(), error: vi.fn(), allowed: true }));
vi.mock('@/services/api', () => ({ apiClient: { setTemperatures: mocks.setTemperatures, setPrinterSensor: mocks.setPrinterSensor } }));
vi.mock('sonner', () => ({ toast: { error: mocks.error } }));
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: () => ({ hasPermission: () => mocks.allowed }) }));

function baseProps(overrides: Partial<ComponentProps<typeof TemperatureControlSection>> = {}): ComponentProps<typeof TemperatureControlSection> {
  return {
    printerId: 'p1',
    hotendTarget: 210,
    bedTarget: 60,
    hotendCurrent: 205,
    bedCurrent: 58,
    temperatureActionPending: false,
    canSetTemperatures: true,
    canCooldown: true,
    onApplyPreset: vi.fn(),
    onApplySingleHeaterPreset: vi.fn(),
    ...overrides,
  };
}

function renderSection(overrides: Partial<ComponentProps<typeof TemperatureControlSection>> = {}) {
  const props = baseProps(overrides);
  render(<TemperatureControlSection {...props} />);
  return props;
}

describe('TemperatureControlSection', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.allowed = true;
    mocks.setTemperatures.mockResolvedValue({ success: true });
    mocks.setPrinterSensor.mockResolvedValue({ success: true });
  });
  const tools: PrinterSensor[] = ['extruder', 'extruder1', 'extruder2', 'extruder3'].map((id, index) => ({
    id, kind: 'Nozzle', displayName: id, available: true,
    readings: [{ quantity: 'Temperature', value: 200 + index, unit: 'Celsius' }],
    writable: [{ quantity: 'TargetTemperature', min: 10, max: 250 + index * 10, step: 0.5, unit: 'Celsius', allowZero: true }],
  }));

  it('addresses the active physical heater and allows zero below its heating minimum', async () => {
    const user = userEvent.setup();
    renderSection({ activeExtruderId: 'extruder3', sensors: tools });
    const input = screen.getByRole('spinbutton', { name: 'Hotend target temperature' });
    expect(input).toHaveAttribute('max', '280');
    await user.clear(input);
    await user.type(input, '0{Enter}');
    await waitFor(() => expect(mocks.setPrinterSensor).toHaveBeenCalledExactlyOnceWith(
      'p1', 'extruder3', { quantity: 'TargetTemperature', value: 0 }));
    expect(mocks.setTemperatures).not.toHaveBeenCalled();
  });

  it('discards an unsent draft when the active tool changes and restores tool zero as an additional row', async () => {
    const props = baseProps({ activeExtruderId: 'extruder', sensors: tools });
    const { rerender } = render(<TemperatureControlSection {...props} />);
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Hotend target temperature' }), { target: { value: '240' } });
    rerender(<TemperatureControlSection {...props} activeExtruderId="extruder2" hotendTarget={230} />);
    const input = screen.getByRole('spinbutton', { name: 'Hotend target temperature' });
    expect(input).toHaveValue(230);
    expect(input).toHaveAttribute('max', '270');
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(mocks.setPrinterSensor).not.toHaveBeenCalled();
    expect(additionalPrinterSensors(tools, { activeExtruderId: 'extruder2', hotendTemp: 202 }).map(sensor => sensor.id))
      .toEqual(['extruder', 'extruder1', 'extruder3']);
  });

  it('disables presets during printing or pause without disabling manual targets or cooldown', () => {
    renderSection({ presetsDisabled: true });
    expect(screen.getByRole('button', { name: /apply temperature preset/i })).toBeDisabled();
    expect(screen.getByLabelText('Hotend target temperature')).toBeEnabled();
    expect(screen.getByLabelText('Cooldown')).toBeEnabled();
  });

  it('fails closed when active heater identity is explicitly unknown', () => {
    renderSection({ activeExtruderId: null, sensors: tools });
    expect(screen.getByLabelText('Hotend target temperature')).toBeDisabled();
    expect(additionalPrinterSensors(tools, { activeExtruderId: null })).toHaveLength(4);
  });
  it('renders the Temps heading by default and omits it when showHeading is false', () => {
    const { unmount } = render(<TemperatureControlSection {...baseProps()} />);
    expect(screen.getByText('Temperatures')).toBeInTheDocument();
    unmount();

    render(<TemperatureControlSection {...baseProps()} showHeading={false} />);
    expect(screen.queryByText('Temperatures')).not.toBeInTheDocument();
  });

  it.each([
    { hotend: 205, bed: 60, online: true, on: true },
    { hotend: 50, bed: 35, online: true, on: false },
    { hotend: 0, bed: 0, online: true, on: false },
    { hotend: undefined, bed: undefined, online: true, on: false },
    { hotend: 205, bed: 60, online: false, on: false },
  ])('matches compact heater glyph paths, colors and on/off opacity for %o', ({ hotend, bed, online, on }) => {
    const { container } = render(<>
      <PrintProgressBar progress={0} jobName={null} isActive={false} showTemperatures isOnline={online} hotendTemp={hotend} bedTemp={bed} />
      <TemperatureControlSection {...baseProps({ hotendCurrent: hotend, bedCurrent: bed, isOnline: online })} />
    </>);
    for (const [kind, title] of [['Nozzle', 'Hotend temperature'], ['Bed', 'Bed temperature']]) {
      const detailed = container.querySelector(`[data-sensor-glyph="${kind}"] svg`)!;
      const compact = screen.getByTitle(title).querySelector('svg')!;
      expect(detailed.querySelector('path')).toHaveAttribute('d', compact.querySelector('path')!.getAttribute('d'));
      expect(detailed).toHaveClass(on ? 'opacity-100' : 'opacity-40');
      for (const color of ['text-pf-error', 'text-pf-accent']) {
        expect(detailed.classList.contains(color)).toBe(compact.classList.contains(color));
      }
    }
    expect(mocks.setTemperatures).not.toHaveBeenCalled();
  });

  it('mutes unavailable primary heaters even when a stale reading is hot', () => {
    const { container } = render(<TemperatureControlSection {...baseProps({ sensors: [
      { id: 'extruder', displayName: 'Hotend', kind: 'Nozzle', available: false, readings: [] },
      { id: 'bed', displayName: 'Bed', kind: 'Bed', available: false, readings: [] },
    ] })} />);
    for (const svg of container.querySelectorAll('[data-sensor-glyph] svg')) {
      expect(svg).toHaveClass('opacity-40');
      expect(svg).not.toHaveClass('text-pf-error', 'text-pf-accent');
    }
  });

  it('applies the main material preset', async () => {
    const user = userEvent.setup();
    const props = renderSection();

    await user.click(screen.getByRole('button', { name: /apply temperature preset/i }));
    await user.click(screen.getByRole('menuitem', { name: /^Both heaters PLA,/ }));

    expect(props.onApplyPreset).toHaveBeenCalledWith('PLA');
  });

  it('applies row-specific heater presets', async () => {
    const user = userEvent.setup();
    const props = renderSection();
    const presets = screen.getByRole('button', { name: 'Apply temperature preset' });
    await user.click(presets);
    await user.click(screen.getByRole('menuitem', { name: /^Hotend ABS,/ }));
    await user.click(presets);
    await user.click(screen.getByRole('menuitem', { name: /^Bed PETG,/ }));

    expect(props.onApplySingleHeaterPreset).toHaveBeenNthCalledWith(1, 'hotend', 'ABS');
    expect(props.onApplySingleHeaterPreset).toHaveBeenNthCalledWith(2, 'bed', 'PETG');
  });

  it('disables preset actions and inputs when temperature controls are unavailable', () => {
    renderSection({ canSetTemperatures: false, canCooldown: false });

    expect(screen.getByLabelText(/cooldown/i)).toBeDisabled();
    expect(screen.getByRole('button', { name: /apply temperature preset/i })).toBeDisabled();
    expect(screen.getByLabelText(/hotend target temperature/i)).toBeDisabled();
    expect(screen.getByLabelText(/bed target temperature/i)).toBeDisabled();
  });

  it('disables the full section while a temperature action is pending', () => {
    renderSection({ temperatureActionPending: true });

    expect(screen.getByLabelText(/cooldown/i)).toBeDisabled();
    expect(screen.getByRole('button', { name: /apply temperature preset/i })).toBeDisabled();
    expect(screen.getByLabelText(/hotend target temperature/i)).toBeDisabled();
    expect(screen.getByLabelText(/bed target temperature/i)).toBeDisabled();
  });

  it.each(['{Enter}{Tab}', '{Tab}'])('sends only the edited heater once on %s, never a stale companion target', async keys => {
    const user = userEvent.setup();
    renderSection();
    const input = screen.getByRole('spinbutton', { name: 'Hotend target temperature' });
    expect(input).toHaveValue(210);
    await user.clear(input);
    await user.type(input, `220${keys}`);
    await waitFor(() => expect(mocks.setTemperatures).toHaveBeenCalledExactlyOnceWith('p1', { hotend: 220 }));
    expect(screen.getByText('205.0°C')).toBeVisible();
    expect(screen.getByText('Accepted 220°C · awaiting telemetry')).toBeVisible();
  });

  it('keeps missing cells blank and genuine zeros visible', () => {
    renderSection({ hotendTarget: undefined, hotendCurrent: undefined, bedTarget: 0, bedCurrent: 0 });
    expect(screen.getByRole('spinbutton', { name: 'Hotend target temperature' })).toHaveValue(null);
    expect(screen.getByRole('spinbutton', { name: 'Bed target temperature' })).toHaveValue(0);
    expect(screen.getByText('0.0°C')).toBeInTheDocument();
    expect(screen.queryByText('---')).not.toBeInTheDocument();
    expect(screen.queryByText('—')).not.toBeInTheDocument();
  });

  it('uses declared heater bounds and step and does not send invalid targets', async () => {
    const user = userEvent.setup();
    renderSection({ sensors: [{ id: 'extruder', kind: 'Nozzle', displayName: 'Nozzle', available: true, readings: [],
      writable: [{ quantity: 'TargetTemperature', min: 10, max: 250, step: 0.5, unit: 'Celsius' }] }] });
    const input = screen.getByRole('spinbutton', { name: 'Hotend target temperature' });
    expect(input).toHaveAttribute('min', '10');
    expect(input).toHaveAttribute('max', '250');
    expect(input).toHaveAttribute('step', '0.5');
    await user.clear(input);
    await user.type(input, '250.5{Enter}{Tab}');
    expect(mocks.setTemperatures).not.toHaveBeenCalled();
    expect(screen.getByText(/Nothing sent/)).toBeVisible();
  });

  it('retains a failed target for explicit retry and shows a persistent error', async () => {
    const user = userEvent.setup();
    mocks.setTemperatures.mockResolvedValueOnce({ success: false, error: 'Busy heater' });
    renderSection();
    const input = screen.getByRole('spinbutton', { name: 'Bed target temperature' });
    await user.clear(input);
    await user.type(input, '65{Enter}');
    await screen.findByText(/Busy heater.*retry/);
    await user.tab();
    expect(mocks.setTemperatures).toHaveBeenCalledTimes(1);
    expect(input).toHaveValue(65);
    await user.click(input);
    await user.keyboard('{Enter}');
    await screen.findByText('Accepted 65°C · awaiting telemetry');
    expect(mocks.setTemperatures).toHaveBeenLastCalledWith('p1', { bed: 65 });
  });

  it.each([
    [300, 250, 300],
    [Number.NaN, 250, 250],
    [Number.POSITIVE_INFINITY, 250, 250],
    [-1, 250, 250],
    [0, 250, 0],
    [Number.NaN, Number.POSITIVE_INFINITY, undefined],
    [-1, -1, undefined],
  ])('resolves descriptor maximum %s and persisted maximum %s to %s without inventing a limit', (descriptorMax, persistedMax, expectedMax) => {
    renderSection({ maxHotendTemp: persistedMax, maxBedTemp: 100, sensors: [{
      id: 'extruder', kind: 'Nozzle', displayName: 'Nozzle', available: true, readings: [],
      writable: [{ quantity: 'TargetTemperature', min: 0, max: descriptorMax, step: 0.5, unit: 'Celsius' }],
    }] });
    const input = screen.getByRole('spinbutton', { name: 'Hotend target temperature' });
    if (expectedMax === undefined) {
      expect(input).not.toHaveAttribute('max');
      expect(input).toHaveAttribute('step', '1');
      expect(input).toHaveAccessibleDescription(/Hotend maximum temperature unknown.*firmware limits/);
    } else {
      expect(input).toHaveAttribute('max', String(expectedMax));
      expect(input).not.toHaveAccessibleDescription(/maximum temperature unknown/);
    }
    expect(screen.getByRole('spinbutton', { name: 'Bed target temperature' })).not.toHaveAccessibleDescription(/maximum temperature unknown/);
  });

  it.each([undefined, 250])('rejects a negative descriptor maximum even above its minimum and falls back to %s', persistedMax => {
    renderSection({ maxHotendTemp: persistedMax, sensors: [{
      id: 'extruder', kind: 'Nozzle', displayName: 'Nozzle', available: true, readings: [],
      writable: [{ quantity: 'TargetTemperature', min: -20, max: -10, step: 1, unit: 'Celsius' }],
    }] });
    const input = screen.getByRole('spinbutton', { name: 'Hotend target temperature' });
    if (persistedMax === undefined) {
      expect(input).not.toHaveAttribute('max');
      expect(input).toHaveAccessibleDescription(/maximum temperature unknown/);
    } else {
      expect(input).toHaveAttribute('max', '250');
      expect(input).not.toHaveAccessibleDescription(/maximum temperature unknown/);
    }
  });

  it('keeps the entire primary section blocked during a direct command without misreporting a disconnect', async () => {
    let finish!: (value: { success: boolean }) => void;
    mocks.setTemperatures.mockReturnValue(new Promise(resolve => { finish = resolve; }));
    renderSection();
    const input = screen.getByRole('spinbutton', { name: 'Hotend target temperature' });
    fireEvent.change(input, { target: { value: '220' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(screen.getByRole('spinbutton', { name: 'Bed target temperature' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Cooldown' })).toBeDisabled();
    expect(screen.getByText('Sending…')).toBeVisible();
    expect(screen.queryByText(/outcome unknown/)).not.toBeInTheDocument();
    finish({ success: true });
    await screen.findByText('Accepted 220°C · awaiting telemetry');
    expect(input).toBeEnabled();
  });

  it('enforces permission even when capability allows temperatures', () => {
    mocks.allowed = false;
    renderSection();
    for (const input of screen.getAllByRole('spinbutton')) expect(input).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Apply temperature preset' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Cooldown' })).toBeDisabled();
  });

  it('opens all preset scopes without commands, roves focus and dismisses back to the trigger', async () => {
    const user = userEvent.setup();
    const props = renderSection();
    const trigger = screen.getByRole('button', { name: 'Apply temperature preset' });
    trigger.focus();
    await user.keyboard('{ArrowDown}');
    expect(screen.getAllByRole('menuitem')).toHaveLength(15);
    expect(screen.getByRole('group', { name: 'Both heaters' })).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Hotend' })).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Bed' })).toBeInTheDocument();
    expect(screen.getByRole('menuitem', { name: /^Both heaters ABS,/ })).toHaveFocus();
    await user.keyboard('{ArrowDown}{End}');
    expect(screen.getByRole('menuitem', { name: /^Bed Cooldown,/ })).toHaveFocus();
    await user.keyboard('{Home}{Escape}');
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
    expect(props.onApplyPreset).not.toHaveBeenCalled();
    expect(props.onApplySingleHeaterPreset).not.toHaveBeenCalled();
    expect(mocks.setTemperatures).not.toHaveBeenCalled();
  });

  it('activates exactly one preset by keyboard and closes without replay on pending changes', async () => {
    const user = userEvent.setup();
    const props = baseProps();
    const { rerender } = render(<TemperatureControlSection {...props} />);
    const trigger = screen.getByRole('button', { name: 'Apply temperature preset' });
    trigger.focus();
    await user.keyboard('{ArrowDown}{ArrowDown}{Enter}');
    expect(props.onApplyPreset).toHaveBeenCalledExactlyOnceWith('PLA');
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
    await user.click(trigger);
    rerender(<TemperatureControlSection {...props} temperatureActionPending />);
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
    rerender(<TemperatureControlSection {...props} />);
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
    expect(props.onApplyPreset).toHaveBeenCalledTimes(1);
  });
});
