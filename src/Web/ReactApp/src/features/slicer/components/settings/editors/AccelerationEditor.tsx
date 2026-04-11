/**
 * Acceleration settings editor for OrcaSlicer
 * Controls acceleration rates for different print features
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { AccelerationIcon } from '../SlicerSettingIcons';
import type { OrcaProcessSettings } from '../slicerSettingsTypes';

interface AccelerationEditorProps {
  settings: OrcaProcessSettings;
  onChange: <K extends keyof OrcaProcessSettings>(key: K, value: OrcaProcessSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof OrcaProcessSettings) => boolean;
  onReset?: (key: keyof OrcaProcessSettings) => void;
  getOriginalValue?: <K extends keyof OrcaProcessSettings>(key: K) => OrcaProcessSettings[K];
}

export const AccelerationEditor: React.FC<AccelerationEditorProps> = ({
  settings,
  onChange,
  disabled,
  hasChanges,
  onReset,
  getOriginalValue,
}) => {
  return (
    <div className="space-y-4">
      <SettingSection title="Acceleration Settings" icon={<AccelerationIcon />}>
        <SettingRow
          type="slider"
          icon={<AccelerationIcon />}
          label="Default Acceleration"
          value={settings.default_acceleration}
          onChange={(v) => onChange('default_acceleration', v)}
          min={100}
          max={10000}
          step={100}
          unit="mm/s²"
          disabled={disabled}
          isModified={hasChanges?.('default_acceleration')}
          onReset={() => onReset?.('default_acceleration')}
          originalValue={getOriginalValue?.('default_acceleration')}
          tooltip="Default acceleration for all features"
        />
        <SettingRow
          type="slider"
          icon={<AccelerationIcon />}
          label="Outer Wall Acceleration"
          value={settings.outer_wall_acceleration}
          onChange={(v) => onChange('outer_wall_acceleration', v)}
          min={100}
          max={10000}
          step={100}
          unit="mm/s²"
          disabled={disabled}
          isModified={hasChanges?.('outer_wall_acceleration')}
          onReset={() => onReset?.('outer_wall_acceleration')}
          originalValue={getOriginalValue?.('outer_wall_acceleration')}
          tooltip="Acceleration for outer perimeter (lower = smoother surface)"
        />
        <SettingRow
          type="slider"
          icon={<AccelerationIcon />}
          label="Inner Wall Acceleration"
          value={settings.inner_wall_acceleration}
          onChange={(v) => onChange('inner_wall_acceleration', v)}
          min={100}
          max={10000}
          step={100}
          unit="mm/s²"
          disabled={disabled}
          isModified={hasChanges?.('inner_wall_acceleration')}
          onReset={() => onReset?.('inner_wall_acceleration')}
          originalValue={getOriginalValue?.('inner_wall_acceleration')}
          tooltip="Acceleration for inner perimeters"
        />
        <SettingRow
          type="slider"
          icon={<AccelerationIcon />}
          label="Top Surface Acceleration"
          value={settings.top_surface_acceleration}
          onChange={(v) => onChange('top_surface_acceleration', v)}
          min={100}
          max={10000}
          step={100}
          unit="mm/s²"
          disabled={disabled}
          isModified={hasChanges?.('top_surface_acceleration')}
          onReset={() => onReset?.('top_surface_acceleration')}
          originalValue={getOriginalValue?.('top_surface_acceleration')}
          tooltip="Acceleration for top surface finish"
        />
        <SettingRow
          type="slider"
          icon={<AccelerationIcon />}
          label="Infill Acceleration"
          value={settings.sparse_infill_acceleration}
          onChange={(v) => onChange('sparse_infill_acceleration', v)}
          min={100}
          max={10000}
          step={100}
          unit="mm/s²"
          disabled={disabled}
          isModified={hasChanges?.('sparse_infill_acceleration')}
          onReset={() => onReset?.('sparse_infill_acceleration')}
          originalValue={getOriginalValue?.('sparse_infill_acceleration')}
          tooltip="Acceleration for infill patterns (can be higher for speed)"
        />
        <SettingRow
          type="slider"
          icon={<AccelerationIcon />}
          label="Travel Acceleration"
          value={settings.travel_acceleration}
          onChange={(v) => onChange('travel_acceleration', v)}
          min={100}
          max={10000}
          step={100}
          unit="mm/s²"
          disabled={disabled}
          isModified={hasChanges?.('travel_acceleration')}
          onReset={() => onReset?.('travel_acceleration')}
          originalValue={getOriginalValue?.('travel_acceleration')}
          tooltip="Acceleration for travel moves (typically highest)"
        />
      </SettingSection>
    </div>
  );
};
