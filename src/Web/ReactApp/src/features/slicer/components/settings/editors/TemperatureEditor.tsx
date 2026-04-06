/**
 * Temperature settings editor for OrcaSlicer
 * Controls nozzle and bed temperatures for normal and first layer printing
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { TemperatureIcon } from '../SlicerSettingIcons';
import type { AdvancedSlicerSettings } from '../slicerSettingsTypes';

interface TemperatureEditorProps {
  settings: AdvancedSlicerSettings;
  onChange: <K extends keyof AdvancedSlicerSettings>(key: K, value: AdvancedSlicerSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof AdvancedSlicerSettings) => boolean;
  onReset?: (key: keyof AdvancedSlicerSettings) => void;
  getOriginalValue?: <K extends keyof AdvancedSlicerSettings>(key: K) => AdvancedSlicerSettings[K];
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
          value={settings.nozzleTemp}
          onChange={(v) => onChange('nozzleTemp', v)}
          min={150}
          max={300}
          step={5}
          unit="°C"
          disabled={disabled}
          isModified={hasChanges?.('nozzleTemp')}
          onReset={() => onReset?.('nozzleTemp')}
          originalValue={getOriginalValue?.('nozzleTemp')}
          tooltip="Extruder temperature for normal printing"
        />
        <SettingRow
          type="slider"
          icon={<TemperatureIcon />}
          label="Bed Temperature"
          value={settings.bedTemp}
          onChange={(v) => onChange('bedTemp', v)}
          min={0}
          max={120}
          step={5}
          unit="°C"
          disabled={disabled}
          isModified={hasChanges?.('bedTemp')}
          onReset={() => onReset?.('bedTemp')}
          originalValue={getOriginalValue?.('bedTemp')}
          tooltip="Heated bed temperature for normal printing"
        />
      </SettingSection>

      <SettingSection title="First Layer Temperatures" icon={<TemperatureIcon />}>
        <SettingRow
          type="slider"
          icon={<TemperatureIcon />}
          label="First Layer Nozzle Temp"
          value={settings.firstLayerNozzleTemp}
          onChange={(v) => onChange('firstLayerNozzleTemp', v)}
          min={150}
          max={300}
          step={5}
          unit="°C"
          disabled={disabled}
          isModified={hasChanges?.('firstLayerNozzleTemp')}
          onReset={() => onReset?.('firstLayerNozzleTemp')}
          originalValue={getOriginalValue?.('firstLayerNozzleTemp')}
          tooltip="Extruder temperature for first layer (better adhesion)"
        />
        <SettingRow
          type="slider"
          icon={<TemperatureIcon />}
          label="First Layer Bed Temp"
          value={settings.firstLayerBedTemp}
          onChange={(v) => onChange('firstLayerBedTemp', v)}
          min={0}
          max={120}
          step={5}
          unit="°C"
          disabled={disabled}
          isModified={hasChanges?.('firstLayerBedTemp')}
          onReset={() => onReset?.('firstLayerBedTemp')}
          originalValue={getOriginalValue?.('firstLayerBedTemp')}
          tooltip="Heated bed temperature for first layer (better adhesion)"
        />
      </SettingSection>
    </div>
  );
};
