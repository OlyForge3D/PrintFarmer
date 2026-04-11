/**
 * Temperature settings editor for OrcaSlicer
 * Controls nozzle and bed temperatures for normal and first layer printing
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { TemperatureIcon } from '../SlicerSettingIcons';
import type { OrcaProcessSettings } from '../slicerSettingsTypes';

interface TemperatureEditorProps {
  settings: OrcaProcessSettings;
  onChange: <K extends keyof OrcaProcessSettings>(key: K, value: OrcaProcessSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof OrcaProcessSettings) => boolean;
  onReset?: (key: keyof OrcaProcessSettings) => void;
  getOriginalValue?: <K extends keyof OrcaProcessSettings>(key: K) => OrcaProcessSettings[K];
}

export const TemperatureEditor: React.FC<TemperatureEditorProps> = ({
  settings,
  onChange,
  disabled,
  hasChanges,
  onReset,
  getOriginalValue,
}) => {
  return (
    <div className="space-y-4">
      <SettingSection title="Normal Temperatures" icon={<TemperatureIcon />}>
        <SettingRow
          type="slider"
          icon={<TemperatureIcon />}
          label="Nozzle Temperature"
          value={settings.nozzle_temperature}
          onChange={(v) => onChange('nozzle_temperature', v)}
          min={150}
          max={300}
          step={5}
          unit="°C"
          disabled={disabled}
          isModified={hasChanges?.('nozzle_temperature')}
          onReset={() => onReset?.('nozzle_temperature')}
          originalValue={getOriginalValue?.('nozzle_temperature')}
          tooltip="Extruder temperature for normal printing"
        />
        <SettingRow
          type="slider"
          icon={<TemperatureIcon />}
          label="Bed Temperature"
          value={settings.hot_plate_temp}
          onChange={(v) => onChange('hot_plate_temp', v)}
          min={0}
          max={120}
          step={5}
          unit="°C"
          disabled={disabled}
          isModified={hasChanges?.('hot_plate_temp')}
          onReset={() => onReset?.('hot_plate_temp')}
          originalValue={getOriginalValue?.('hot_plate_temp')}
          tooltip="Heated bed temperature for normal printing"
        />
      </SettingSection>

      <SettingSection title="First Layer Temperatures" icon={<TemperatureIcon />}>
        <SettingRow
          type="slider"
          icon={<TemperatureIcon />}
          label="First Layer Nozzle Temp"
          value={settings.nozzle_temperature_initial_layer}
          onChange={(v) => onChange('nozzle_temperature_initial_layer', v)}
          min={150}
          max={300}
          step={5}
          unit="°C"
          disabled={disabled}
          isModified={hasChanges?.('nozzle_temperature_initial_layer')}
          onReset={() => onReset?.('nozzle_temperature_initial_layer')}
          originalValue={getOriginalValue?.('nozzle_temperature_initial_layer')}
          tooltip="Extruder temperature for first layer (better adhesion)"
        />
        <SettingRow
          type="slider"
          icon={<TemperatureIcon />}
          label="First Layer Bed Temp"
          value={settings.hot_plate_temp_initial_layer}
          onChange={(v) => onChange('hot_plate_temp_initial_layer', v)}
          min={0}
          max={120}
          step={5}
          unit="°C"
          disabled={disabled}
          isModified={hasChanges?.('hot_plate_temp_initial_layer')}
          onReset={() => onReset?.('hot_plate_temp_initial_layer')}
          originalValue={getOriginalValue?.('hot_plate_temp_initial_layer')}
          tooltip="Heated bed temperature for first layer (better adhesion)"
        />
      </SettingSection>
    </div>
  );
};
