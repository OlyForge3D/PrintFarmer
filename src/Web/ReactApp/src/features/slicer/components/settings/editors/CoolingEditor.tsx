/**
 * Cooling settings editor for OrcaSlicer
 * Controls part cooling fan behavior and layer time management
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { CoolingIcon } from '../SlicerSettingIcons';
import type { OrcaProcessSettings } from '../slicerSettingsTypes';

interface CoolingEditorProps {
  settings: OrcaProcessSettings;
  onChange: <K extends keyof OrcaProcessSettings>(key: K, value: OrcaProcessSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof OrcaProcessSettings) => boolean;
  onReset?: (key: keyof OrcaProcessSettings) => void;
  getOriginalValue?: <K extends keyof OrcaProcessSettings>(key: K) => OrcaProcessSettings[K];
}

export const CoolingEditor: React.FC<CoolingEditorProps> = ({
  settings,
  onChange,
  disabled,
  hasChanges,
  onReset,
  getOriginalValue,
}) => {
  const coolingDisabled = disabled || !settings.fan_cooling;

  return (
    <div className="space-y-4">
      <SettingSection title="Fan Cooling" icon={<CoolingIcon />}>
        <SettingRow
          type="checkbox"
          icon={<CoolingIcon />}
          label="Enable Fan Cooling"
          value={settings.fan_cooling}
          onChange={(v) => onChange('fan_cooling', v)}
          disabled={disabled}
          isModified={hasChanges?.('fan_cooling')}
          onReset={() => onReset?.('fan_cooling')}
          originalValue={getOriginalValue?.('fan_cooling')}
          tooltip="Enable part cooling fan (disable for materials like ABS)"
        />
      </SettingSection>

      <SettingSection title="Fan Speed" icon={<CoolingIcon />}>
        <SettingRow
          type="slider"
          icon={<CoolingIcon />}
          label="Minimum Fan Speed"
          value={settings.fan_min_speed}
          onChange={(v) => onChange('fan_min_speed', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={coolingDisabled}
          isModified={hasChanges?.('fan_min_speed')}
          onReset={() => onReset?.('fan_min_speed')}
          originalValue={getOriginalValue?.('fan_min_speed')}
          tooltip="Minimum fan speed during print"
        />
        <SettingRow
          type="slider"
          icon={<CoolingIcon />}
          label="Maximum Fan Speed"
          value={settings.fan_max_speed}
          onChange={(v) => onChange('fan_max_speed', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={coolingDisabled}
          isModified={hasChanges?.('fan_max_speed')}
          onReset={() => onReset?.('fan_max_speed')}
          originalValue={getOriginalValue?.('fan_max_speed')}
          tooltip="Maximum fan speed during print"
        />
        <SettingRow
          type="slider"
          icon={<CoolingIcon />}
          label="Overhang Fan Speed"
          value={settings.overhang_fan_speed}
          onChange={(v) => onChange('overhang_fan_speed', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={coolingDisabled}
          isModified={hasChanges?.('overhang_fan_speed')}
          onReset={() => onReset?.('overhang_fan_speed')}
          originalValue={getOriginalValue?.('overhang_fan_speed')}
          tooltip="Fan speed for bridging and overhangs (usually 100%)"
        />
      </SettingSection>

      <SettingSection title="Layer Time Management" icon={<CoolingIcon />}>
        <SettingRow
          type="slider"
          icon={<CoolingIcon />}
          label="Full Fan Speed at Layer"
          value={settings.full_fan_speed_layer}
          onChange={(v) => onChange('full_fan_speed_layer', v)}
          min={1}
          max={20}
          step={1}
          unit="layer"
          disabled={coolingDisabled}
          isModified={hasChanges?.('full_fan_speed_layer')}
          onReset={() => onReset?.('full_fan_speed_layer')}
          originalValue={getOriginalValue?.('full_fan_speed_layer')}
          tooltip="Start at min speed, ramp to max by this layer"
        />
        <SettingRow
          type="slider"
          icon={<CoolingIcon />}
          label="Slow Down for Layer Time"
          value={settings.slow_down_layer_time}
          onChange={(v) => onChange('slow_down_layer_time', v)}
          min={0}
          max={120}
          step={5}
          unit="s"
          disabled={coolingDisabled}
          isModified={hasChanges?.('slow_down_layer_time')}
          onReset={() => onReset?.('slow_down_layer_time')}
          originalValue={getOriginalValue?.('slow_down_layer_time')}
          tooltip="Slow down print if layer takes less than this time"
        />
        <SettingRow
          type="slider"
          icon={<CoolingIcon />}
          label="Minimum Print Speed"
          value={settings.slow_down_min_speed}
          onChange={(v) => onChange('slow_down_min_speed', v)}
          min={5}
          max={100}
          step={5}
          unit="mm/s"
          disabled={coolingDisabled}
          isModified={hasChanges?.('slow_down_min_speed')}
          onReset={() => onReset?.('slow_down_min_speed')}
          originalValue={getOriginalValue?.('slow_down_min_speed')}
          tooltip="Minimum speed when slowing for cooling (never go slower)"
        />
      </SettingSection>
    </div>
  );
};
