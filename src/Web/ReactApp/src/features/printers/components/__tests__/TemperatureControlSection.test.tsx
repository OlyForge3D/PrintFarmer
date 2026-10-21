import type { ComponentProps } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { TemperatureControlSection } from '@/features/printers/components/TemperatureControlSection';

const mocks = vi.hoisted(() => ({ setTemperatures: vi.fn(), error: vi.fn(), allowed: true }));
vi.mock('@/services/api', () => ({ apiClient: { setTemperatures: mocks.setTemperatures } }));
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
  });
  it('renders the Temps heading by default and omits it when showHeading is false', () => {
    const { unmount } = render(<TemperatureControlSection {...baseProps()} />);
    expect(screen.getByText('Temperatures')).toBeInTheDocument();
    unmount();

    render(<TemperatureControlSection {...baseProps()} showHeading={false} />);
    expect(screen.queryByText('Temperatures')).not.toBeInTheDocument();
  });

  it('applies the main material preset', async () => {
    const user = userEvent.setup();
    const props = renderSection();

    await user.selectOptions(screen.getByRole('combobox', { name: /apply temperature preset/i }), 'PLA');

    expect(props.onApplyPreset).toHaveBeenCalledWith('PLA');
  });

  it('applies row-specific heater presets', async () => {
    const user = userEvent.setup();
    const props = renderSection();
    const presets = screen.getByRole('combobox', { name: 'Apply temperature preset' });
    await user.selectOptions(presets, 'hotend:ABS');
    await user.selectOptions(presets, 'bed:PETG');

    expect(props.onApplySingleHeaterPreset).toHaveBeenNthCalledWith(1, 'hotend', 'ABS');
    expect(props.onApplySingleHeaterPreset).toHaveBeenNthCalledWith(2, 'bed', 'PETG');
  });

  it('disables preset actions and inputs when temperature controls are unavailable', () => {
    renderSection({ canSetTemperatures: false, canCooldown: false });

    expect(screen.getByLabelText(/cooldown/i)).toBeDisabled();
    expect(screen.getByRole('combobox', { name: /apply temperature preset/i })).toBeDisabled();
    expect(screen.getByLabelText(/hotend target temperature/i)).toBeDisabled();
    expect(screen.getByLabelText(/bed target temperature/i)).toBeDisabled();
  });

  it('disables the full section while a temperature action is pending', () => {
    renderSection({ temperatureActionPending: true });

    expect(screen.getByLabelText(/cooldown/i)).toBeDisabled();
    expect(screen.getByRole('combobox', { name: /apply temperature preset/i })).toBeDisabled();
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
    for (const select of screen.getAllByRole('combobox')) expect(select).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Cooldown' })).toBeDisabled();
  });
});
