/**
 * Speed settings editor for OrcaSlicer
 * Controls print speeds for different features and travel moves
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { SpeedIcon } from '../SlicerSettingIcons';
import type { OrcaProcessSettings } from '../slicerSettingsTypes';

interface SpeedEditorProps {
  settings: OrcaProcessSettings;
  onChange: <K extends keyof OrcaProcessSettings>(key: K, value: OrcaProcessSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof OrcaProcessSettings) => boolean;
  onReset?: (key: keyof OrcaProcessSettings) => void;
  getOriginalValue?: <K extends keyof OrcaProcessSettings>(key: K) => OrcaProcessSettings[K];
}

export const SpeedEditor: React.FC<SpeedEditorProps> = ({
  settings,
  onChange,
  disabled,
  hasChanges,
  onReset,
  getOriginalValue,
}) => {
  return (
    <div className="space-y-4">
      <SettingSection title="Print Speeds" icon={<SpeedIcon />}>
        <SettingRow
          type="slider"
          icon={<SpeedIcon />}
          label="Outer Wall Speed"
          value={settings.outer_wall_speed}
          onChange={(v) => onChange('outer_wall_speed', v)}
          min={10}
          max={500}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('outer_wall_speed')}
          onReset={() => onReset?.('outer_wall_speed')}
          originalValue={getOriginalValue?.('outer_wall_speed')}
          tooltip="Speed for outer perimeter (affects surface quality)"
        />
        <SettingRow
          type="slider"
          icon={<SpeedIcon />}
          label="Inner Wall Speed"
          value={settings.inner_wall_speed}
          onChange={(v) => onChange('inner_wall_speed', v)}
          min={10}
          max={500}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('inner_wall_speed')}
          onReset={() => onReset?.('inner_wall_speed')}
          originalValue={getOriginalValue?.('inner_wall_speed')}
          tooltip="Speed for inner perimeters"
        />
        <SettingRow
          type="slider"
          icon={<SpeedIcon />}
          label="Sparse Infill Speed"
          value={settings.sparse_infill_speed}
          onChange={(v) => onChange('sparse_infill_speed', v)}
          min={10}
          max={500}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('sparse_infill_speed')}
          onReset={() => onReset?.('sparse_infill_speed')}
          originalValue={getOriginalValue?.('sparse_infill_speed')}
          tooltip="Speed for sparse infill patterns"
        />
        <SettingRow
          type="slider"
          icon={<SpeedIcon />}
          label="Solid Infill Speed"
          value={settings.internal_solid_infill_speed}
          onChange={(v) => onChange('internal_solid_infill_speed', v)}
          min={10}
          max={500}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('internal_solid_infill_speed')}
          onReset={() => onReset?.('internal_solid_infill_speed')}
          originalValue={getOriginalValue?.('internal_solid_infill_speed')}
          tooltip="Speed for solid infill regions"
        />
        <SettingRow
          type="slider"
          icon={<SpeedIcon />}
          label="Top Surface Speed"
          value={settings.top_surface_speed}
          onChange={(v) => onChange('top_surface_speed', v)}
          min={10}
          max={500}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('top_surface_speed')}
          onReset={() => onReset?.('top_surface_speed')}
          originalValue={getOriginalValue?.('top_surface_speed')}
          tooltip="Speed for top surface finish"
        />
        <SettingRow
          type="slider"
          icon={<SpeedIcon />}
          label="Travel Speed"
          value={settings.travel_speed}
          onChange={(v) => onChange('travel_speed', v)}
          min={10}
          max={500}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('travel_speed')}
          onReset={() => onReset?.('travel_speed')}
          originalValue={getOriginalValue?.('travel_speed')}
          tooltip="Speed for non-printing travel moves"
        />
      </SettingSection>

      <SettingSection title="First Layer" icon={<SpeedIcon />}>
        <SettingRow
          type="slider"
          icon={<SpeedIcon />}
          label="First Layer Speed"
          value={settings.initial_layer_speed}
          onChange={(v) => onChange('initial_layer_speed', v)}
          min={10}
          max={200}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('initial_layer_speed')}
          onReset={() => onReset?.('initial_layer_speed')}
          originalValue={getOriginalValue?.('initial_layer_speed')}
          tooltip="Reduced speed for first layer (better bed adhesion)"
        />
      </SettingSection>
    </div>
  );
};
