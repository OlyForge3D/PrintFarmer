/**
 * Speed settings editor for OrcaSlicer
 * Controls print speeds for different features and travel moves
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { SpeedIcon } from '../SlicerSettingIcons';
import type { AdvancedSlicerSettings } from '../slicerSettingsTypes';

interface SpeedEditorProps {
  settings: AdvancedSlicerSettings;
  onChange: <K extends keyof AdvancedSlicerSettings>(key: K, value: AdvancedSlicerSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof AdvancedSlicerSettings) => boolean;
  onReset?: (key: keyof AdvancedSlicerSettings) => void;
  getOriginalValue?: <K extends keyof AdvancedSlicerSettings>(key: K) => AdvancedSlicerSettings[K];
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
          label="Print Speed"
          value={settings.printSpeed}
          onChange={(v) => onChange('printSpeed', v)}
          min={10}
          max={500}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('printSpeed')}
          onReset={() => onReset?.('printSpeed')}
          originalValue={getOriginalValue?.('printSpeed')}
          tooltip="Default printing speed for all features"
        />
        <SettingRow
          type="slider"
          icon={<SpeedIcon />}
          label="Outer Wall Speed"
          value={settings.outerWallSpeed}
          onChange={(v) => onChange('outerWallSpeed', v)}
          min={10}
          max={500}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('outerWallSpeed')}
          onReset={() => onReset?.('outerWallSpeed')}
          originalValue={getOriginalValue?.('outerWallSpeed')}
          tooltip="Speed for outer perimeter (affects surface quality)"
        />
        <SettingRow
          type="slider"
          icon={<SpeedIcon />}
          label="Inner Wall Speed"
          value={settings.innerWallSpeed}
          onChange={(v) => onChange('innerWallSpeed', v)}
          min={10}
          max={500}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('innerWallSpeed')}
          onReset={() => onReset?.('innerWallSpeed')}
          originalValue={getOriginalValue?.('innerWallSpeed')}
          tooltip="Speed for inner perimeters"
        />
        <SettingRow
          type="slider"
          icon={<SpeedIcon />}
          label="Sparse Infill Speed"
          value={settings.sparseInfillSpeed}
          onChange={(v) => onChange('sparseInfillSpeed', v)}
          min={10}
          max={500}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('sparseInfillSpeed')}
          onReset={() => onReset?.('sparseInfillSpeed')}
          originalValue={getOriginalValue?.('sparseInfillSpeed')}
          tooltip="Speed for sparse infill patterns"
        />
        <SettingRow
          type="slider"
          icon={<SpeedIcon />}
          label="Solid Infill Speed"
          value={settings.solidInfillSpeed}
          onChange={(v) => onChange('solidInfillSpeed', v)}
          min={10}
          max={500}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('solidInfillSpeed')}
          onReset={() => onReset?.('solidInfillSpeed')}
          originalValue={getOriginalValue?.('solidInfillSpeed')}
          tooltip="Speed for solid infill regions"
        />
        <SettingRow
          type="slider"
          icon={<SpeedIcon />}
          label="Top Surface Speed"
          value={settings.topSurfaceSpeed}
          onChange={(v) => onChange('topSurfaceSpeed', v)}
          min={10}
          max={500}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('topSurfaceSpeed')}
          onReset={() => onReset?.('topSurfaceSpeed')}
          originalValue={getOriginalValue?.('topSurfaceSpeed')}
          tooltip="Speed for top surface finish"
        />
        <SettingRow
          type="slider"
          icon={<SpeedIcon />}
          label="Travel Speed"
          value={settings.travelSpeed}
          onChange={(v) => onChange('travelSpeed', v)}
          min={10}
          max={500}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('travelSpeed')}
          onReset={() => onReset?.('travelSpeed')}
          originalValue={getOriginalValue?.('travelSpeed')}
          tooltip="Speed for non-printing travel moves"
        />
      </SettingSection>

      <SettingSection title="First Layer" icon={<SpeedIcon />}>
        <SettingRow
          type="slider"
          icon={<SpeedIcon />}
          label="First Layer Speed"
          value={settings.firstLayerSpeed}
          onChange={(v) => onChange('firstLayerSpeed', v)}
          min={10}
          max={200}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('firstLayerSpeed')}
          onReset={() => onReset?.('firstLayerSpeed')}
          originalValue={getOriginalValue?.('firstLayerSpeed')}
          tooltip="Reduced speed for first layer (better bed adhesion)"
        />
      </SettingSection>
    </div>
  );
};
