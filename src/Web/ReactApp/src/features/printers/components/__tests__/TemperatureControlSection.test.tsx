import type { ComponentProps } from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { TemperatureControlSection } from '../TemperatureControlSection';

function renderSection(overrides: Partial<ComponentProps<typeof TemperatureControlSection>> = {}) {
  const props: ComponentProps<typeof TemperatureControlSection> = {
    hotendTemp: 210,
    bedTemp: 60,
    hotendTarget: 210,
    bedTarget: 60,
    hotendCurrent: 205,
    bedCurrent: 58,
    temperatureActionPending: false,
    canSetTemperatures: true,
    canCooldown: true,
    onHotendTempChange: vi.fn(),
    onBedTempChange: vi.fn(),
    onHotendTempKeyDown: vi.fn(),
    onBedTempKeyDown: vi.fn(),
    onApplyPreset: vi.fn(),
    onApplySingleHeaterPreset: vi.fn(),
    ...overrides,
  };

  render(<TemperatureControlSection {...props} />);
  return props;
}

describe('TemperatureControlSection', () => {
  it('applies the main material preset', async () => {
    const user = userEvent.setup();
    const props = renderSection();

    await user.selectOptions(screen.getByRole('combobox', { name: /apply temperature preset/i }), 'PLA');

    expect(props.onApplyPreset).toHaveBeenCalledWith('PLA');
  });

  it('applies row-specific heater presets', async () => {
    const user = userEvent.setup();
    const props = renderSection();
    const presetSelectors = screen.getAllByRole('combobox');

    await user.selectOptions(presetSelectors[1], 'ABS');
    await user.selectOptions(presetSelectors[2], 'PETG');

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
});
