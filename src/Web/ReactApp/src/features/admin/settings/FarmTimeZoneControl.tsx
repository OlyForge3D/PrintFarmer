import { useMemo, useState } from 'react';
import { Button, Input, Select } from '@/common/components/ui';
import type { SettingFieldControlProps } from '@/common/components/SettingsPagelet';

function browserTimeZones(): string[] | null {
  const intl: typeof Intl & { supportedValuesOf?: (key: 'timeZone') => string[] } = Intl;
  if (!intl.supportedValuesOf) return null;
  try {
    return intl.supportedValuesOf('timeZone');
  } catch (error) {
    if (!(error instanceof RangeError)) throw error;
    return null;
  }
}

export function FarmTimeZoneControl({ id, label, value, required, error, onChange }: SettingFieldControlProps) {
  const zones = useMemo(() => browserTimeZones(), []);
  const [filter, setFilter] = useState('');
  const [manual, setManual] = useState(zones === null);
  const selected = typeof value === 'string' ? value : '';
  const query = filter.trim().toLowerCase().replaceAll('_', ' ');
  const matches = (zones ?? []).filter(zone => zone.toLowerCase().replaceAll('_', ' ').includes(query));
  // Keep the exact saved identifier, including aliases absent from browser lists.
  const options = [...new Set([selected, 'UTC', ...matches])].filter(Boolean);
  const describedBy = [`${id}-help`, ...(error ? [`${id}-error`] : [])].join(' ');

  return (
    <div className="space-y-2">
      {!manual && (
        <Input
          type="search"
          aria-label="Filter timezones"
          placeholder="Filter by region or city"
          value={filter}
          onChange={event => setFilter(event.currentTarget.value)}
          className="text-base sm:text-sm"
          aria-controls={id}
        />
      )}
      {manual ? (
        <Input
          id={id}
          name={id}
          aria-label={label}
          aria-required={required || undefined}
          aria-invalid={Boolean(error) || undefined}
          aria-describedby={describedBy}
          invalid={Boolean(error)}
          value={selected}
          placeholder="America/Los_Angeles"
          onChange={event => onChange(event.currentTarget.value)}
          className="text-base sm:text-sm"
          autoComplete="off"
          spellCheck={false}
        />
      ) : (
        <Select
          id={id}
          name={id}
          aria-label={label}
          aria-required={required || undefined}
          aria-invalid={Boolean(error) || undefined}
          aria-describedby={describedBy}
          invalid={Boolean(error)}
          value={selected}
          onChange={event => onChange(event.currentTarget.value)}
          className="text-base sm:text-sm"
        >
          {!selected && <option value="">Choose a timezone</option>}
          {options.map(zone => <option key={zone} value={zone}>{zone}</option>)}
        </Select>
      )}
      <p id={`${id}-help`} className="text-xs text-pf-text-secondary">
        {zones === null
          ? 'This browser cannot list timezones. Enter an IANA timezone or UTC; the server validates it when you save.'
          : manual
            ? 'Enter an IANA timezone or UTC. The server validates it when you save.'
            : 'Choose a timezone. UTC and your current value stay available while filtering.'}
      </p>
      {!manual && query && (
        <p role="status" className="text-xs text-pf-text-secondary">
          {matches.length === 0 ? 'No matching timezones. Try another city or enter an IANA timezone manually.' : `${matches.length} matching timezones.`}
        </p>
      )}
      {zones !== null && (
        <Button type="button" variant="secondary" size="sm" onClick={() => setManual(current => !current)}>
          {manual ? 'Choose from timezone list' : 'Enter timezone manually'}
        </Button>
      )}
    </div>
  );
}
