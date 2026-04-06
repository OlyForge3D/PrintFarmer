/**
 * Cooling settings editor for OrcaSlicer
 * Controls part cooling fan behavior and layer time management
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { CoolingIcon } from '../SlicerSettingIcons';
import type { AdvancedSlicerSettings } from '../slicerSettingsTypes';

interface CoolingEditorProps {
  settings: AdvancedSlicerSettings;
  onChange: <K extends keyof AdvancedSlicerSettings>(key: K, value: AdvancedSlicerSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof AdvancedSlicerSettings) => boolean;
  onReset?: (key: keyof AdvancedSlicerSettings) => void;
  getOriginalValue?: <K extends keyof AdvancedSlicerSettings>(key: K) => AdvancedSlicerSettings[K];
}

export const CoolingEditor: React.FC<CoolingEditorProps> = ({
  settings,
  onChange,
  disabled,
  hasChanges,
  onReset,
  getOriginalValue,
}) => {
  const coolingDisabled = disabled || !settings.enableFanCooling;

  return (
    <div className="space-y-4">
      <SettingSection title="Fan Cooling" icon={<CoolingIcon />}>
        <SettingRow
          type="checkbox"
          icon={<CoolingIcon />}
          label="Enable Fan Cooling"
          value={settings.enableFanCooling}
          onChange={(v) => onChange('enableFanCooling', v)}
          disabled={disabled}
          isModified={hasChanges?.('enableFanCooling')}
          onReset={() => onReset?.('enableFanCooling')}
          originalValue={getOriginalValue?.('enableFanCooling')}
          tooltip="Enable part cooling fan (disable for materials like ABS)"
        />
      </SettingSection>

      <SettingSection title="Fan Speed" icon={<CoolingIcon />}>
        <SettingRow
          type="slider"
          icon={<CoolingIcon />}
          label="Minimum Fan Speed"
          value={settings.minFanSpeed}
          onChange={(v) => onChange('minFanSpeed', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={coolingDisabled}
          isModified={hasChanges?.('minFanSpeed')}
          onReset={() => onReset?.('minFanSpeed')}
          originalValue={getOriginalValue?.('minFanSpeed')}
          tooltip="Minimum fan speed during print"
        />
        <SettingRow
          type="slider"
          icon={<CoolingIcon />}
          label="Maximum Fan Speed"
          value={settings.maxFanSpeed}
          onChange={(v) => onChange('maxFanSpeed', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={coolingDisabled}
          isModified={hasChanges?.('maxFanSpeed')}
          onReset={() => onReset?.('maxFanSpeed')}
          originalValue={getOriginalValue?.('maxFanSpeed')}
          tooltip="Maximum fan speed during print"
        />
        <SettingRow
          type="slider"
          icon={<CoolingIcon />}
          label="Bridge Fan Speed"
          value={settings.bridgeFanSpeed}
          onChange={(v) => onChange('bridgeFanSpeed', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={coolingDisabled}
          isModified={hasChanges?.('bridgeFanSpeed')}
          onReset={() => onReset?.('bridgeFanSpeed')}
          originalValue={getOriginalValue?.('bridgeFanSpeed')}
          tooltip="Fan speed for bridging (usually 100%)"
        />
      </SettingSection>

      <SettingSection title="Layer Time Management" icon={<CoolingIcon />}>
        <SettingRow
          type="slider"
          icon={<CoolingIcon />}
          label="Full Fan Speed at Layer"
          value={settings.fullFanSpeedAtLayer}
          onChange={(v) => onChange('fullFanSpeedAtLayer', v)}
          min={1}
          max={20}
          step={1}
          unit="layer"
          disabled={coolingDisabled}
          isModified={hasChanges?.('fullFanSpeedAtLayer')}
          onReset={() => onReset?.('fullFanSpeedAtLayer')}
          originalValue={getOriginalValue?.('fullFanSpeedAtLayer')}
          tooltip="Start at min speed, ramp to max by this layer"
        />
        <SettingRow
          type="slider"
          icon={<CoolingIcon />}
          label="Slow Down for Layer Time"
          value={settings.slowDownForLayerTime}
          onChange={(v) => onChange('slowDownForLayerTime', v)}
          min={0}
          max={120}
          step={5}
          unit="s"
          disabled={coolingDisabled}
          isModified={hasChanges?.('slowDownForLayerTime')}
          onReset={() => onReset?.('slowDownForLayerTime')}
          originalValue={getOriginalValue?.('slowDownForLayerTime')}
          tooltip="Slow down print if layer takes less than this time"
        />
        <SettingRow
          type="slider"
          icon={<CoolingIcon />}
          label="Minimum Print Speed"
          value={settings.minPrintSpeed}
          onChange={(v) => onChange('minPrintSpeed', v)}
          min={5}
          max={100}
          step={5}
          unit="mm/s"
          disabled={coolingDisabled}
          isModified={hasChanges?.('minPrintSpeed')}
          onReset={() => onReset?.('minPrintSpeed')}
          originalValue={getOriginalValue?.('minPrintSpeed')}
          tooltip="Minimum speed when slowing for cooling (never go slower)"
        />
      </SettingSection>
    </div>
  );
};
