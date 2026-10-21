import { useState } from 'react';
import { Button, Select } from '@/common/components/ui';
import { SnowflakeIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { PrinterValueControl } from '@/features/printers/components/PrinterValueControl';
import { PrinterThermalRow, PrinterThermalTable } from '@/features/printers/components/PrinterSensorsPanel';
import { primaryHeater, primaryHeaterLimits, primaryHeaterPower } from '@/features/printers/utils/additionalPrinterSensors';
import type { PrinterSensor } from '@/types/api';
import { bedPresetOptions, hotendPresetOptions, materialPresets } from '@/features/printers/constants/temperaturePresets';

interface TemperatureControlSectionProps {
  printerId: string;
  hotendTarget?: number;
  bedTarget?: number;
  hotendCurrent?: number;
  bedCurrent?: number;
  maxHotendTemp?: number;
  maxBedTemp?: number;
  temperatureActionPending: boolean;
  canSetTemperatures: boolean;
  canCooldown: boolean;
  onApplyPreset: (preset: string) => void;
  onApplySingleHeaterPreset: (heater: 'hotend' | 'bed', preset: string) => void;
  showHeading?: boolean;
  sensors?: PrinterSensor[];
}

export function TemperatureControlSection({
  printerId, hotendTarget, bedTarget, hotendCurrent, bedCurrent, maxHotendTemp, maxBedTemp,
  temperatureActionPending, canSetTemperatures, canCooldown, onApplyPreset, onApplySingleHeaterPreset,
  showHeading = true, sensors,
}: TemperatureControlSectionProps) {
  const { hasPermission } = useAuth();
  const [sending, setSending] = useState(false);
  const busy = temperatureActionPending || sending;
  const authorized = hasPermission('queue', 'start');
  const disabled = busy || !canSetTemperatures || !authorized;
  const rows = [
    { heater: 'hotend' as const, name: 'Hotend', target: hotendTarget, current: hotendCurrent, max: maxHotendTemp, presets: hotendPresetOptions },
    { heater: 'bed' as const, name: 'Bed', target: bedTarget, current: bedCurrent, max: maxBedTemp, presets: bedPresetOptions },
  ];
  return <div className="min-w-0">
    {showHeading && <h4 className="mb-1 text-xs font-medium text-pf-text-secondary">Temperatures</h4>}
    <div className="mb-1 flex items-center justify-end gap-1">
      <Button type="button" variant="ghost" size="sm" aria-label="Cooldown" title="Cooldown"
        disabled={busy || !canCooldown || !authorized} className="min-h-11 min-w-11 p-0!"
        onClick={() => onApplyPreset('cooldown')} iconCenter={<SnowflakeIcon className="h-4 w-4" />} />
      <Select defaultValue="" aria-label="Apply temperature preset" disabled={disabled}
        containerClassName="w-28!" className="min-h-11 text-xs"
        onChange={event => {
          const [scope, preset] = event.target.value.split(':');
          if (scope === 'hotend' || scope === 'bed') onApplySingleHeaterPreset(scope, preset);
          else if (scope) onApplyPreset(scope);
          event.currentTarget.value = '';
        }}>
        <option value="">Presets</option>
        <optgroup label="Both heaters">
          {materialPresets.map(preset => <option key={preset.value} value={preset.value}>{preset.label}</option>)}
        </optgroup>
        {rows.map(row => <optgroup key={row.heater} label={row.name}>
          {row.presets.map(option => <option key={option.value} value={`${row.heater}:${option.value}`}>{row.name} {option.label}</option>)}
        </optgroup>)}
      </Select>
    </div>
    <PrinterThermalTable>
      {rows.map(row => {
        const sensor = sensors?.find(item => primaryHeater(item) === row.heater);
        const { min, max, step } = primaryHeaterLimits(sensors, row.heater, row.max);
        const power = primaryHeaterPower(sensors, row.heater);
        const unavailable = sensor?.available === false;
        const blocked = !canSetTemperatures || !authorized || unavailable;
        return <PrinterThermalRow key={`${printerId}-${row.heater}`}
          warning={max === undefined ? `${row.name} maximum temperature unknown. Integer commands rely on firmware limits; this endpoint does not enforce an upper bound.` : undefined}
          name={<>
            {row.name}
            {unavailable && <span className="block text-xs font-normal text-pf-text-secondary">Unavailable</span>}
          </>}
          state={<span className="font-mono tabular-nums">{power}</span>}
          current={<span className="font-mono tabular-nums">{Number.isFinite(row.current) ? `${row.current!.toFixed(1)}°C` : ''}</span>}
          target={status => <PrinterValueControl label={`${row.name} target temperature`} current={Number.isFinite(row.target) ? row.target : undefined}
              min={min} max={max} step={step}
              unit="°C" disabled={blocked} pending={busy} status={status}
              onSubmit={async value => {
                setSending(true);
                try {
                  return await apiClient.setTemperatures(printerId, { [row.heater]: value });
                } finally { setSending(false); }
              }} />} />;
      })}
    </PrinterThermalTable>
  </div>;
}
