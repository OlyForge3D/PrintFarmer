/**
 * Retraction settings editor for OrcaSlicer
 * Controls filament retraction behavior to prevent stringing and oozing
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { RetractionIcon } from '../SlicerSettingIcons';
import type { OrcaProcessSettings } from '../slicerSettingsTypes';

interface RetractionEditorProps {
  settings: OrcaProcessSettings;
  onChange: <K extends keyof OrcaProcessSettings>(key: K, value: OrcaProcessSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof OrcaProcessSettings) => boolean;
  onReset?: (key: keyof OrcaProcessSettings) => void;
  getOriginalValue?: <K extends keyof OrcaProcessSettings>(key: K) => OrcaProcessSettings[K];
}

export const RetractionEditor: React.FC<RetractionEditorProps> = ({
  settings,
  onChange,
  disabled,
  hasChanges,
  onReset,
  getOriginalValue,
}) => {
  return (
    <div className="space-y-4">
      <SettingSection title="Retraction Distance & Speed" icon={<RetractionIcon />}>
        <SettingRow
          type="slider"
          icon={<RetractionIcon />}
          label="Retraction Length"
          value={settings.filament_retraction_length}
          onChange={(v) => onChange('filament_retraction_length', v)}
          min={0}
          max={10}
          step={0.1}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('filament_retraction_length')}
          onReset={() => onReset?.('filament_retraction_length')}
          originalValue={getOriginalValue?.('filament_retraction_length')}
          tooltip="How much filament to retract (Bowden: 4-6mm, Direct: 0.5-2mm)"
        />
        <SettingRow
          type="slider"
          icon={<RetractionIcon />}
          label="Retraction Speed"
          value={settings.filament_retraction_speed}
          onChange={(v) => onChange('filament_retraction_speed', v)}
          min={10}
          max={100}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('filament_retraction_speed')}
          onReset={() => onReset?.('filament_retraction_speed')}
          originalValue={getOriginalValue?.('filament_retraction_speed')}
          tooltip="Speed for pulling filament back"
        />
        <SettingRow
          type="slider"
          icon={<RetractionIcon />}
          label="Detraction Speed"
          value={settings.filament_deretraction_speed}
          onChange={(v) => onChange('filament_deretraction_speed', v)}
          min={10}
          max={100}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('filament_deretraction_speed')}
          onReset={() => onReset?.('filament_deretraction_speed')}
          originalValue={getOriginalValue?.('filament_deretraction_speed')}
          tooltip="Speed for re-priming filament after retraction"
        />
        <SettingRow
          type="slider"
          icon={<RetractionIcon />}
          label="Minimum Travel Distance"
          value={settings.filament_retraction_minimum_travel}
          onChange={(v) => onChange('filament_retraction_minimum_travel', v)}
          min={0}
          max={10}
          step={0.1}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('filament_retraction_minimum_travel')}
          onReset={() => onReset?.('filament_retraction_minimum_travel')}
          originalValue={getOriginalValue?.('filament_retraction_minimum_travel')}
          tooltip="Only retract if travel distance exceeds this threshold"
        />
      </SettingSection>

      <SettingSection title="Z-Hop & Options" icon={<RetractionIcon />}>
        <SettingRow
          type="slider"
          icon={<RetractionIcon />}
          label="Retraction Lift Z"
          value={settings.filament_z_hop}
          onChange={(v) => onChange('filament_z_hop', v)}
          min={0}
          max={2}
          step={0.1}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('filament_z_hop')}
          onReset={() => onReset?.('filament_z_hop')}
          originalValue={getOriginalValue?.('filament_z_hop')}
          tooltip="Lift nozzle during travel to avoid collisions (Z-hop)"
        />
        <SettingRow
          type="checkbox"
          icon={<RetractionIcon />}
          label="Retract on Layer Change"
          value={settings.filament_retract_when_changing_layer}
          onChange={(v) => onChange('filament_retract_when_changing_layer', v)}
          disabled={disabled}
          isModified={hasChanges?.('filament_retract_when_changing_layer')}
          onReset={() => onReset?.('filament_retract_when_changing_layer')}
          originalValue={getOriginalValue?.('filament_retract_when_changing_layer')}
          tooltip="Always retract when moving to next layer"
        />
        <SettingRow
          type="checkbox"
          icon={<RetractionIcon />}
          label="Wipe Before Retract"
          value={settings.filament_retract_before_wipe}
          onChange={(v) => onChange('filament_retract_before_wipe', v)}
          disabled={disabled}
          isModified={hasChanges?.('filament_retract_before_wipe')}
          onReset={() => onReset?.('filament_retract_before_wipe')}
          originalValue={getOriginalValue?.('filament_retract_before_wipe')}
          tooltip="Wipe nozzle on perimeter before retracting"
        />
      </SettingSection>
    </div>
  );
};
