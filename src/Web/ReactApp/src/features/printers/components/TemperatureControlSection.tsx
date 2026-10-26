import { useState, type ReactNode } from 'react';
import { Button } from '@/common/components/ui';
import { SnowflakeIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { PrinterValueControl } from '@/features/printers/components/PrinterValueControl';
import { PrinterSensorGlyph, PrinterThermalRow, PrinterThermalTable } from '@/features/printers/components/PrinterSensorsPanel';
import { TemperaturePresetMenu } from '@/features/printers/components/TemperaturePresetMenu';
import { HEATER_LABELS } from '@/features/printers/constants/temperaturePresets';
import { primaryHeater, primaryHeaterLimits, primaryHeaterPower } from '@/features/printers/utils/additionalPrinterSensors';
import type { PrinterSensor, PrinterTemperatures } from '@/types/api';

interface TemperatureControlSectionProps {
  printerId: string;
  activeExtruderId?: string | null;
  presetsDisabled?: boolean;
  hotendTarget?: number;
  bedTarget?: number;
  hotendCurrent?: number;
  bedCurrent?: number;
  reportedTemperatures?: PrinterTemperatures;
  maxHotendTemp?: number;
  maxBedTemp?: number;
  temperatureActionPending: boolean;
  canSetTemperatures: boolean;
  canCooldown: boolean;
  isOnline?: boolean;
  onApplyPreset: (preset: string) => void;
  onApplySingleHeaterPreset: (heater: 'hotend' | 'bed', preset: string) => void;
  showHeading?: boolean;
  sensors?: PrinterSensor[];
  additionalRows?: ReactNode;
}

export function TemperatureControlSection({
  printerId, hotendTarget, bedTarget, hotendCurrent, bedCurrent, maxHotendTemp, maxBedTemp,
  temperatureActionPending, canSetTemperatures, canCooldown, onApplyPreset, onApplySingleHeaterPreset,
  showHeading = true, sensors, additionalRows, isOnline = true, reportedTemperatures, activeExtruderId, presetsDisabled = false,
}: TemperatureControlSectionProps) {
  const { hasPermission } = useAuth();
  const [sending, setSending] = useState(false);
  const busy = temperatureActionPending || sending;
  const authorized = hasPermission('queue', 'start');
  const disabled = busy || !canSetTemperatures || !authorized;
  const rows = [
    { heater: 'hotend' as const, name: HEATER_LABELS.hotend, target: hotendTarget, current: hotendCurrent, max: maxHotendTemp },
    { heater: 'bed' as const, name: HEATER_LABELS.bed, target: bedTarget, current: bedCurrent, max: maxBedTemp },
  ];
  return <div className="min-w-0">
    {showHeading && <h4 className="mb-1 text-xs font-medium text-pf-text-secondary">Temperatures</h4>}
    <div className="mb-1 flex items-center justify-end gap-1" data-temperature-toolbar>
      <Button type="button" variant="ghost" size="sm" aria-label="Cooldown" title="Cooldown"
        disabled={busy || !canCooldown || !authorized} className="h-8! min-h-8! w-8 p-0!"
        onClick={() => onApplyPreset('cooldown')} iconCenter={<SnowflakeIcon className="h-4 w-4" />} />
      <TemperaturePresetMenu disabled={disabled || presetsDisabled} onApplyPreset={onApplyPreset} onApplySingleHeaterPreset={onApplySingleHeaterPreset} />
    </div>
    <PrinterThermalTable>
      {rows.map(row => {
        const sensor = sensors?.find(item => primaryHeater(item, activeExtruderId) === row.heater);
        const { min, max, allowZero } = primaryHeaterLimits(sensors, row.heater, row.max, activeExtruderId);
        const power = primaryHeaterPower(sensors, row.heater, activeExtruderId);
        const unavailable = sensor?.available === false;
        const blocked = !canSetTemperatures || !authorized;
        const reportedCurrent = reportedTemperatures === undefined
          ? row.current
          : reportedTemperatures[row.heater === 'hotend' ? 'hotendTemp' : 'bedTemp'];
        return <PrinterThermalRow key={`${printerId}-${row.heater}-${row.heater === 'hotend' ? activeExtruderId : ''}`}
          warning={max === undefined ? `${row.name} maximum temperature unknown. Integer commands rely on firmware limits; this endpoint does not enforce an upper bound.` : undefined}
          name={<span className="flex min-w-0 items-center gap-1 @[441px]/sensors:gap-2" data-sensor-name>
            <PrinterSensorGlyph kind={row.heater === 'hotend' ? 'Nozzle' : 'Bed'}
              temperature={isOnline && !unavailable ? reportedCurrent : undefined} />
            <span>{row.name}{unavailable && <span className="block text-xs font-normal text-pf-text-secondary">Unavailable</span>}</span>
          </span>}
          state={<span>{power}</span>}
          current={<span>{Number.isFinite(row.current) ? `${row.current!.toFixed(1)}°C` : ''}</span>}
          target={status => <PrinterValueControl label={`${row.name} target temperature`} current={Number.isFinite(row.target) ? row.target : undefined}
              min={Math.ceil(min)} max={max} step={1} allowZero={allowZero}
              unit="°C" disabled={blocked} pending={busy} status={status}
              onSubmit={async value => {
                setSending(true);
                try {
                  return await apiClient.setTemperatures(printerId, { [row.heater]: value });
                } finally { setSending(false); }
              }} />} />;
      })}
      {additionalRows}
    </PrinterThermalTable>
  </div>;
}
