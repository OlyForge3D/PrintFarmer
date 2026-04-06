/**
 * Acceleration settings editor for OrcaSlicer
 * Controls acceleration rates for different print features
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { AccelerationIcon } from '../SlicerSettingIcons';
import type { AdvancedSlicerSettings } from '../slicerSettingsTypes';

interface AccelerationEditorProps {
  settings: AdvancedSlicerSettings;
  onChange: <K extends keyof AdvancedSlicerSettings>(key: K, value: AdvancedSlicerSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof AdvancedSlicerSettings) => boolean;
  onReset?: (key: keyof AdvancedSlicerSettings) => void;
  getOriginalValue?: <K extends keyof AdvancedSlicerSettings>(key: K) => AdvancedSlicerSettings[K];
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
          value={settings.defaultAcceleration}
          onChange={(v) => onChange('defaultAcceleration', v)}
          min={100}
          max={10000}
          step={100}
          unit="mm/s²"
          disabled={disabled}
          isModified={hasChanges?.('defaultAcceleration')}
          onReset={() => onReset?.('defaultAcceleration')}
          originalValue={getOriginalValue?.('defaultAcceleration')}
          tooltip="Default acceleration for all features"
        />
        <SettingRow
          type="slider"
          icon={<AccelerationIcon />}
          label="Outer Wall Acceleration"
          value={settings.outerWallAcceleration}
          onChange={(v) => onChange('outerWallAcceleration', v)}
          min={100}
          max={10000}
          step={100}
          unit="mm/s²"
          disabled={disabled}
          isModified={hasChanges?.('outerWallAcceleration')}
          onReset={() => onReset?.('outerWallAcceleration')}
          originalValue={getOriginalValue?.('outerWallAcceleration')}
          tooltip="Acceleration for outer perimeter (lower = smoother surface)"
        />
        <SettingRow
          type="slider"
          icon={<AccelerationIcon />}
          label="Inner Wall Acceleration"
          value={settings.innerWallAcceleration}
          onChange={(v) => onChange('innerWallAcceleration', v)}
          min={100}
          max={10000}
          step={100}
          unit="mm/s²"
          disabled={disabled}
          isModified={hasChanges?.('innerWallAcceleration')}
          onReset={() => onReset?.('innerWallAcceleration')}
          originalValue={getOriginalValue?.('innerWallAcceleration')}
          tooltip="Acceleration for inner perimeters"
        />
        <SettingRow
          type="slider"
          icon={<AccelerationIcon />}
          label="Top Surface Acceleration"
          value={settings.topSurfaceAcceleration}
          onChange={(v) => onChange('topSurfaceAcceleration', v)}
          min={100}
          max={10000}
          step={100}
          unit="mm/s²"
          disabled={disabled}
          isModified={hasChanges?.('topSurfaceAcceleration')}
          onReset={() => onReset?.('topSurfaceAcceleration')}
          originalValue={getOriginalValue?.('topSurfaceAcceleration')}
          tooltip="Acceleration for top surface finish"
        />
        <SettingRow
          type="slider"
          icon={<AccelerationIcon />}
          label="Infill Acceleration"
          value={settings.infillAcceleration}
          onChange={(v) => onChange('infillAcceleration', v)}
          min={100}
          max={10000}
          step={100}
          unit="mm/s²"
          disabled={disabled}
          isModified={hasChanges?.('infillAcceleration')}
          onReset={() => onReset?.('infillAcceleration')}
          originalValue={getOriginalValue?.('infillAcceleration')}
          tooltip="Acceleration for infill patterns (can be higher for speed)"
        />
        <SettingRow
          type="slider"
          icon={<AccelerationIcon />}
          label="Travel Acceleration"
          value={settings.travelAcceleration}
          onChange={(v) => onChange('travelAcceleration', v)}
          min={100}
          max={10000}
          step={100}
          unit="mm/s²"
          disabled={disabled}
          isModified={hasChanges?.('travelAcceleration')}
          onReset={() => onReset?.('travelAcceleration')}
          originalValue={getOriginalValue?.('travelAcceleration')}
          tooltip="Acceleration for travel moves (typically highest)"
        />
      </SettingSection>
    </div>
  );
};
