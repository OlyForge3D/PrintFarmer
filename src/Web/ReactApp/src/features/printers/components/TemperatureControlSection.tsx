import { Button, TemperatureControlRow, Select } from '@/common/components/ui';
import { NozzleIcon, BedIcon, SnowflakeIcon } from '@/common/components/icons/MdiIcons';
import {
  bedPresetOptions,
  hotendPresetOptions,
  materialPresets,
} from '@/features/printers/constants/temperaturePresets';

function formatTemperature(temp: number | undefined): string {
  if (temp === undefined || temp === null) return '---';
  return `${temp.toFixed(1)}°C`;
}

interface TemperatureControlSectionProps {
  hotendTemp: number | string;
  bedTemp: number | string;
  hotendTarget?: number;
  bedTarget?: number;
  hotendCurrent?: number;
  bedCurrent?: number;
  temperatureActionPending: boolean;
  canSetTemperatures: boolean;
  canCooldown: boolean;
  onHotendTempChange: (value: number | string) => void;
  onBedTempChange: (value: number | string) => void;
  onHotendTempKeyDown: (e: React.KeyboardEvent) => void;
  onBedTempKeyDown: (e: React.KeyboardEvent) => void;
  onApplyPreset: (preset: string) => void;
  onApplySingleHeaterPreset: (heater: 'hotend' | 'bed', preset: string) => void;
}

export function TemperatureControlSection({
  hotendTemp,
  bedTemp,
  hotendTarget = 0,
  bedTarget = 0,
  hotendCurrent,
  bedCurrent,
  temperatureActionPending,
  canSetTemperatures,
  canCooldown,
  onHotendTempChange,
  onBedTempChange,
  onHotendTempKeyDown,
  onBedTempKeyDown,
  onApplyPreset,
  onApplySingleHeaterPreset,
}: TemperatureControlSectionProps) {
  return (
    <div className="mb-2">
      <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide mb-1 -ml-1">
        Temps
      </div>
      <div className="space-y-1">
        <div className="flex justify-end gap-1 items-stretch h-8 pb-1">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={temperatureActionPending || !canCooldown}
            onClick={() => onApplyPreset('cooldown')}
            title="Cooldown"
            aria-label="Cooldown"
            className="shrink-0 px-2!"
            iconCenter={
              <SnowflakeIcon
                className={`h-4 w-4 ${
                  hotendTarget > 0 || bedTarget > 0
                    ? 'text-pf-accent'
                    : 'text-pf-text-secondary'
                }`}
              />
            }
          />
          <div className="relative w-24">
            <Select
              value=""
              disabled={temperatureActionPending || !canSetTemperatures}
              onChange={(e) => {
                const value = e.target.value;
                if (value) {
                  onApplyPreset(value);
                }
              }}
              className="h-8 text-[10px] uppercase tracking-wide font-semibold pr-6! border-transparent! bg-transparent! enabled:hover:[background:rgba(255,255,255,0.10)] focus:border-transparent focus:ring-0"
            >
              <option value="">PRESETS</option>
              {materialPresets.map((preset) => (
                <option key={preset.value} value={preset.value}>
                  {preset.label}
                </option>
              ))}
            </Select>
          </div>
        </div>

        <div className="grid grid-cols-[minmax(0,1fr)_3rem_4.75rem_5rem_1.5rem] gap-2 pb-1 text-[10px] uppercase tracking-wide text-pf-text-secondary">
          <span>Name</span>
          <span className="text-right">State</span>
          <span className="text-right">Current</span>
          <span className="text-right">Target</span>
          <span></span>
        </div>

        <TemperatureControlRow
          icon={
            <NozzleIcon
              className="w-4 h-4 text-pf-error"
              isOn={hotendTarget > 0}
            />
          }
          label="Hotend"
          stateLabel={hotendTarget > 0 ? 'on' : 'off'}
          liveReading={formatTemperature(hotendCurrent)}
          value={hotendTemp}
          onChange={(e) => onHotendTempChange(e.target.value === '' ? '' : Number(e.target.value))}
          onKeyDown={onHotendTempKeyDown}
          disabled={temperatureActionPending || !canSetTemperatures}
          presetOptions={hotendPresetOptions}
          onPresetSelect={(preset) => onApplySingleHeaterPreset('hotend', preset)}
        />

        <TemperatureControlRow
          icon={
            <BedIcon
              className="w-4 h-4 text-pf-accent"
              isOn={bedTarget > 0}
            />
          }
          label="Bed"
          stateLabel={bedTarget > 0 ? 'on' : 'off'}
          liveReading={formatTemperature(bedCurrent)}
          value={bedTemp}
          onChange={(e) => onBedTempChange(e.target.value === '' ? '' : Number(e.target.value))}
          onKeyDown={onBedTempKeyDown}
          disabled={temperatureActionPending || !canSetTemperatures}
          presetOptions={bedPresetOptions}
          onPresetSelect={(preset) => onApplySingleHeaterPreset('bed', preset)}
        />
      </div>
    </div>
  );
}
