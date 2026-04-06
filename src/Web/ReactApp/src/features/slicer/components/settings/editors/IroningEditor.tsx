/**
 * Ironing settings editor for OrcaSlicer
 * Controls top surface ironing for a smoother finish
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { IroningIcon } from '../SlicerSettingIcons';
import type { AdvancedSlicerSettings } from '../slicerSettingsTypes';

interface IroningEditorProps {
  settings: AdvancedSlicerSettings;
  onChange: <K extends keyof AdvancedSlicerSettings>(key: K, value: AdvancedSlicerSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof AdvancedSlicerSettings) => boolean;
  onReset?: (key: keyof AdvancedSlicerSettings) => void;
  getOriginalValue?: <K extends keyof AdvancedSlicerSettings>(key: K) => AdvancedSlicerSettings[K];
}

export const IroningEditor: React.FC<IroningEditorProps> = ({
  settings,
  onChange,
  disabled,
  hasChanges,
  onReset,
  getOriginalValue,
}) => {
  const ironingDisabled = disabled || !settings.enableIroning;

  return (
    <div className="space-y-4">
      <SettingSection title="Ironing" icon={<IroningIcon />}>
        <SettingRow
          type="checkbox"
          icon={<IroningIcon />}
          label="Enable Ironing"
          value={settings.enableIroning}
          onChange={(v) => onChange('enableIroning', v)}
          disabled={disabled}
          isModified={hasChanges?.('enableIroning')}
          onReset={() => onReset?.('enableIroning')}
          originalValue={getOriginalValue?.('enableIroning')}
          tooltip="Iron top surfaces with hot nozzle for smoother finish (adds print time)"
        />
        <SettingRow
          type="select"
          icon={<IroningIcon />}
          label="Ironing Pattern"
          value={settings.ironingPattern}
          onChange={(v) => onChange('ironingPattern', v as typeof settings.ironingPattern)}
          options={[
            { value: 'zigzag', label: 'Zigzag' },
            { value: 'concentric', label: 'Concentric' },
          ]}
          disabled={ironingDisabled}
          isModified={hasChanges?.('ironingPattern')}
          onReset={() => onReset?.('ironingPattern')}
          originalValue={getOriginalValue?.('ironingPattern')}
          tooltip="Path pattern for ironing passes"
        />
      </SettingSection>

      <SettingSection title="Ironing Parameters" icon={<IroningIcon />}>
        <SettingRow
          type="slider"
          icon={<IroningIcon />}
          label="Flow Rate"
          value={settings.ironingFlowRate}
          onChange={(v) => onChange('ironingFlowRate', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={ironingDisabled}
          isModified={hasChanges?.('ironingFlowRate')}
          onReset={() => onReset?.('ironingFlowRate')}
          originalValue={getOriginalValue?.('ironingFlowRate')}
          tooltip="Extrusion flow during ironing (10-20% typical)"
        />
        <SettingRow
          type="slider"
          icon={<IroningIcon />}
          label="Line Spacing"
          value={settings.ironingSpacing}
          onChange={(v) => onChange('ironingSpacing', v)}
          min={0.05}
          max={0.5}
          step={0.01}
          unit="mm"
          disabled={ironingDisabled}
          isModified={hasChanges?.('ironingSpacing')}
          onReset={() => onReset?.('ironingSpacing')}
          originalValue={getOriginalValue?.('ironingSpacing')}
          tooltip="Distance between ironing lines (smaller = smoother but slower)"
        />
        <SettingRow
          type="slider"
          icon={<IroningIcon />}
          label="Speed"
          value={settings.ironingSpeed}
          onChange={(v) => onChange('ironingSpeed', v)}
          min={10}
          max={100}
          step={5}
          unit="mm/s"
          disabled={ironingDisabled}
          isModified={hasChanges?.('ironingSpeed')}
          onReset={() => onReset?.('ironingSpeed')}
          originalValue={getOriginalValue?.('ironingSpeed')}
          tooltip="Speed for ironing passes (slower = smoother)"
        />
        <SettingRow
          type="slider"
          icon={<IroningIcon />}
          label="Pattern Angle"
          value={settings.ironingAngle}
          onChange={(v) => onChange('ironingAngle', v)}
          min={-90}
          max={90}
          step={15}
          unit="°"
          disabled={ironingDisabled}
          isModified={hasChanges?.('ironingAngle')}
          onReset={() => onReset?.('ironingAngle')}
          originalValue={getOriginalValue?.('ironingAngle')}
          tooltip="Angle of ironing pattern (affects surface appearance)"
        />
      </SettingSection>
    </div>
  );
};
