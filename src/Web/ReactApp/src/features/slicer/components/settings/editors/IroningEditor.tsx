/**
 * Ironing settings editor for OrcaSlicer
 * Controls top surface ironing for a smoother finish
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { IroningIcon } from '../SlicerSettingIcons';
import type { OrcaProcessSettings } from '../slicerSettingsTypes';

interface IroningEditorProps {
  settings: OrcaProcessSettings;
  onChange: <K extends keyof OrcaProcessSettings>(key: K, value: OrcaProcessSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof OrcaProcessSettings) => boolean;
  onReset?: (key: keyof OrcaProcessSettings) => void;
  getOriginalValue?: <K extends keyof OrcaProcessSettings>(key: K) => OrcaProcessSettings[K];
}

export const IroningEditor: React.FC<IroningEditorProps> = ({
  settings,
  onChange,
  disabled,
  hasChanges,
  onReset,
  getOriginalValue,
}) => {
  const ironingDisabled = disabled || settings.ironing_type === 'no_ironing';

  return (
    <div className="space-y-4">
      <SettingSection title="Ironing" icon={<IroningIcon />}>
        <SettingRow
          type="select"
          icon={<IroningIcon />}
          label="Ironing Type"
          value={settings.ironing_type}
          onChange={(v) => onChange('ironing_type', v as typeof settings.ironing_type)}
          options={[
            { value: 'no_ironing', label: 'Disabled' },
            { value: 'top', label: 'Top Surface Only' },
            { value: 'topmost', label: 'Topmost Layer Only' },
            { value: 'all_solid', label: 'All Solid Layers' },
          ]}
          disabled={disabled}
          isModified={hasChanges?.('ironing_type')}
          onReset={() => onReset?.('ironing_type')}
          originalValue={getOriginalValue?.('ironing_type')}
          tooltip="Iron top surfaces with hot nozzle for smoother finish (adds print time)"
        />
        <SettingRow
          type="select"
          icon={<IroningIcon />}
          label="Ironing Pattern"
          value={settings.ironing_pattern}
          onChange={(v) => onChange('ironing_pattern', v as typeof settings.ironing_pattern)}
          options={[
            { value: 'rectilinear', label: 'Rectilinear' },
            { value: 'concentric', label: 'Concentric' },
          ]}
          disabled={ironingDisabled}
          isModified={hasChanges?.('ironing_pattern')}
          onReset={() => onReset?.('ironing_pattern')}
          originalValue={getOriginalValue?.('ironing_pattern')}
          tooltip="Path pattern for ironing passes"
        />
      </SettingSection>

      <SettingSection title="Ironing Parameters" icon={<IroningIcon />}>
        <SettingRow
          type="slider"
          icon={<IroningIcon />}
          label="Flow Rate"
          value={settings.ironing_flow}
          onChange={(v) => onChange('ironing_flow', v)}
          min={0}
          max={100}
          step={5}
          unit="%"
          disabled={ironingDisabled}
          isModified={hasChanges?.('ironing_flow')}
          onReset={() => onReset?.('ironing_flow')}
          originalValue={getOriginalValue?.('ironing_flow')}
          tooltip="Extrusion flow during ironing (10-20% typical)"
        />
        <SettingRow
          type="slider"
          icon={<IroningIcon />}
          label="Line Spacing"
          value={settings.ironing_spacing}
          onChange={(v) => onChange('ironing_spacing', v)}
          min={0.05}
          max={0.5}
          step={0.01}
          unit="mm"
          disabled={ironingDisabled}
          isModified={hasChanges?.('ironing_spacing')}
          onReset={() => onReset?.('ironing_spacing')}
          originalValue={getOriginalValue?.('ironing_spacing')}
          tooltip="Distance between ironing lines (smaller = smoother but slower)"
        />
        <SettingRow
          type="slider"
          icon={<IroningIcon />}
          label="Speed"
          value={settings.ironing_speed}
          onChange={(v) => onChange('ironing_speed', v)}
          min={10}
          max={100}
          step={5}
          unit="mm/s"
          disabled={ironingDisabled}
          isModified={hasChanges?.('ironing_speed')}
          onReset={() => onReset?.('ironing_speed')}
          originalValue={getOriginalValue?.('ironing_speed')}
          tooltip="Speed for ironing passes (slower = smoother)"
        />
        <SettingRow
          type="slider"
          icon={<IroningIcon />}
          label="Pattern Angle"
          value={settings.ironing_angle}
          onChange={(v) => onChange('ironing_angle', v)}
          min={-90}
          max={90}
          step={15}
          unit="°"
          disabled={ironingDisabled}
          isModified={hasChanges?.('ironing_angle')}
          onReset={() => onReset?.('ironing_angle')}
          originalValue={getOriginalValue?.('ironing_angle')}
          tooltip="Angle of ironing pattern (affects surface appearance)"
        />
      </SettingSection>
    </div>
  );
};
