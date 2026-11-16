import { useState } from 'react';
import { fireEvent, render, screen, within } from '@testing-library/react';
import '@testing-library/jest-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SettingsPagelet, type SettingValue } from '@/common/components/SettingsPagelet';
import { getSectionRenderer } from '@/features/admin/settings/section-renderers';

const zones = ['America/Los_Angeles', 'Asia/Kolkata', 'Europe/London'];
const metadata = {
  key: 'FarmIdentity', className: 'FarmIdentitySettings',
  properties: [
    { name: 'farmName', type: 'String', attributes: [], display: { name: 'Farm name' } },
    { name: 'timeZone', type: 'String', attributes: [], display: { name: 'Farm timezone', required: true } },
  ],
};

function listSupport(implementation?: () => string[]) {
  vi.stubGlobal('Intl', Object.create(Intl, { supportedValuesOf: { value: implementation } }));
}

function renderSetting(initial = 'UTC', error?: string) {
  const onChange = vi.fn();
  function Form() {
    const [timeZone, setTimeZone] = useState<SettingValue>(initial);
    return (
      <SettingsPagelet
        compact
        metadata={metadata}
        values={{ farmName: 'Workshop', timeZone }}
        fieldRenderers={getSectionRenderer(metadata)?.fieldRenderers}
        fieldErrors={error ? { timeZone: error } : undefined}
        onChange={(field, value) => {
          onChange(field, value);
          if (field === 'timeZone') setTimeZone(value);
        }}
      />
    );
  }
  render(<Form />);
  return onChange;
}

describe('Farm Identity timezone field through the section registry', () => {
  beforeEach(() => listSupport(() => zones));
  afterEach(() => vi.unstubAllGlobals());

  it('renders a labelled required dropdown and leaves other metadata fields unchanged', () => {
    const onChange = renderSetting();
    expect(screen.getByRole('combobox', { name: 'Farm timezone' })).toHaveValue('UTC');
    expect(screen.getByRole('combobox')).toHaveAttribute('aria-required', 'true');
    expect(screen.getByLabelText('Farm name')).toHaveValue('Workshop');
    expect(screen.getByLabelText('Farm timezone')).toHaveAttribute('id', 'FarmIdentity.timeZone');
    expect(screen.getAllByRole('option').map(option => option.getAttribute('value'))).toEqual(['UTC', ...zones]);
    expect(onChange).not.toHaveBeenCalled();
  });

  it('filters by city with spaces and retains UTC and the exact saved alias without changing it', () => {
    const onChange = renderSetting('US/Pacific');
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: '  LOS ANGELES ' } });
    expect(screen.getAllByRole('option').map(option => option.getAttribute('value')))
      .toEqual(['US/Pacific', 'UTC', 'America/Los_Angeles']);
    expect(screen.getByRole('combobox')).toHaveValue('US/Pacific');
    expect(screen.getByRole('status')).toHaveTextContent('1 matching timezones.');
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'America/Los_Angeles' } });
    expect(onChange).toHaveBeenCalledExactlyOnceWith('timeZone', 'America/Los_Angeles');
    expect(screen.getByRole('combobox')).toHaveValue('America/Los_Angeles');
  });

  it('shows no-match recovery without clearing a server-supported zone absent from the browser list', () => {
    const onChange = renderSetting('America/Coyhaique');
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'not-a-place' } });
    expect(screen.getByRole('status')).toHaveTextContent('No matching timezones.');
    expect(screen.getByRole('combobox')).toHaveValue('America/Coyhaique');
    expect(screen.getAllByRole('option')).toHaveLength(2);
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: '' } });
    expect(screen.getAllByRole('option')).toHaveLength(zones.length + 2);
  });

  it('does not silently replace an empty value with UTC', () => {
    const onChange = renderSetting('');
    expect(screen.getByRole('combobox')).toHaveValue('');
    expect(screen.getByRole('option', { name: 'Choose a timezone' })).toHaveValue('');
    expect(onChange).not.toHaveBeenCalled();
  });

  it('offers manual IANA entry and returns to the list without losing a new alias', () => {
    const onChange = renderSetting();
    fireEvent.click(screen.getByRole('button', { name: 'Enter timezone manually' }));
    const input = screen.getByRole('textbox', { name: 'Farm timezone' });
    expect(input).toHaveValue('UTC');
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.change(input, { target: { value: 'Asia/Calcutta' } });
    expect(onChange).toHaveBeenCalledExactlyOnceWith('timeZone', 'Asia/Calcutta');
    fireEvent.click(screen.getByRole('button', { name: 'Choose from timezone list' }));
    expect(screen.getByRole('combobox')).toHaveValue('Asia/Calcutta');
    expect(within(screen.getByRole('combobox')).getByRole('option', { name: 'Asia/Calcutta' })).toBeInTheDocument();
    expect(onChange).toHaveBeenCalledTimes(1);
  });

  it.each(['missing', 'unsupported'] as const)('explains %s enumeration and preserves manual entry', support => {
    listSupport(support === 'missing' ? undefined : () => { throw new RangeError('Unsupported key'); });
    const onChange = renderSetting('US/Pacific');
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Farm timezone' })).toHaveValue('US/Pacific');
    expect(screen.getByText(/This browser cannot list timezones/)).toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.change(screen.getByRole('textbox', { name: 'Farm timezone' }), { target: { value: 'UTC' } });
    expect(onChange).toHaveBeenCalledExactlyOnceWith('timeZone', 'UTC');
  });

  it('associates server validation errors in both modes and never filters away an invalid draft', () => {
    renderSetting('Not/A_Zone', 'Choose a valid IANA timezone.');
    const select = screen.getByRole('combobox');
    expect(select).toHaveValue('Not/A_Zone');
    expect(select).toHaveAttribute('aria-invalid', 'true');
    expect(select).toHaveAccessibleDescription(/Choose a valid IANA timezone/);
    expect(screen.getByRole('alert')).toHaveTextContent('Choose a valid IANA timezone.');
    fireEvent.click(screen.getByRole('button', { name: 'Enter timezone manually' }));
    expect(screen.getByRole('textbox', { name: 'Farm timezone' })).toHaveAccessibleDescription(/Choose a valid IANA timezone/);
    expect(screen.getByRole('textbox', { name: 'Farm timezone' })).toHaveAttribute('aria-required', 'true');
  });
});
