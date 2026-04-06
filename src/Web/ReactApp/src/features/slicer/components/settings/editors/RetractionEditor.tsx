/**
 * Retraction settings editor for OrcaSlicer
 * Controls filament retraction behavior to prevent stringing and oozing
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { RetractionIcon } from '../SlicerSettingIcons';
import type { AdvancedSlicerSettings } from '../slicerSettingsTypes';

interface RetractionEditorProps {
  settings: AdvancedSlicerSettings;
  onChange: <K extends keyof AdvancedSlicerSettings>(key: K, value: AdvancedSlicerSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof AdvancedSlicerSettings) => boolean;
  onReset?: (key: keyof AdvancedSlicerSettings) => void;
  getOriginalValue?: <K extends keyof AdvancedSlicerSettings>(key: K) => AdvancedSlicerSettings[K];
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
          value={settings.retractionLength}
          onChange={(v) => onChange('retractionLength', v)}
          min={0}
          max={10}
          step={0.1}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('retractionLength')}
          onReset={() => onReset?.('retractionLength')}
          originalValue={getOriginalValue?.('retractionLength')}
          tooltip="How much filament to retract (Bowden: 4-6mm, Direct: 0.5-2mm)"
        />
        <SettingRow
          type="slider"
          icon={<RetractionIcon />}
          label="Retraction Speed"
          value={settings.retractionSpeed}
          onChange={(v) => onChange('retractionSpeed', v)}
          min={10}
          max={100}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('retractionSpeed')}
          onReset={() => onReset?.('retractionSpeed')}
          originalValue={getOriginalValue?.('retractionSpeed')}
          tooltip="Speed for pulling filament back"
        />
        <SettingRow
          type="slider"
          icon={<RetractionIcon />}
          label="Detraction Speed"
          value={settings.detractionSpeed}
          onChange={(v) => onChange('detractionSpeed', v)}
          min={10}
          max={100}
          step={5}
          unit="mm/s"
          disabled={disabled}
          isModified={hasChanges?.('detractionSpeed')}
          onReset={() => onReset?.('detractionSpeed')}
          originalValue={getOriginalValue?.('detractionSpeed')}
          tooltip="Speed for re-priming filament after retraction"
        />
        <SettingRow
          type="slider"
          icon={<RetractionIcon />}
          label="Minimum Travel Distance"
          value={settings.retractionMinimumTravel}
          onChange={(v) => onChange('retractionMinimumTravel', v)}
          min={0}
          max={10}
          step={0.1}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('retractionMinimumTravel')}
          onReset={() => onReset?.('retractionMinimumTravel')}
          originalValue={getOriginalValue?.('retractionMinimumTravel')}
          tooltip="Only retract if travel distance exceeds this threshold"
        />
      </SettingSection>

      <SettingSection title="Z-Hop & Options" icon={<RetractionIcon />}>
        <SettingRow
          type="slider"
          icon={<RetractionIcon />}
          label="Retraction Lift Z"
          value={settings.retractionLiftZ}
          onChange={(v) => onChange('retractionLiftZ', v)}
          min={0}
          max={2}
          step={0.1}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('retractionLiftZ')}
          onReset={() => onReset?.('retractionLiftZ')}
          originalValue={getOriginalValue?.('retractionLiftZ')}
          tooltip="Lift nozzle during travel to avoid collisions (Z-hop)"
        />
        <SettingRow
          type="checkbox"
          icon={<RetractionIcon />}
          label="Retract on Layer Change"
          value={settings.retractOnLayerChange}
          onChange={(v) => onChange('retractOnLayerChange', v)}
          disabled={disabled}
          isModified={hasChanges?.('retractOnLayerChange')}
          onReset={() => onReset?.('retractOnLayerChange')}
          originalValue={getOriginalValue?.('retractOnLayerChange')}
          tooltip="Always retract when moving to next layer"
        />
        <SettingRow
          type="checkbox"
          icon={<RetractionIcon />}
          label="Wipe Before Retract"
          value={settings.wipeBeforeRetract}
          onChange={(v) => onChange('wipeBeforeRetract', v)}
          disabled={disabled}
          isModified={hasChanges?.('wipeBeforeRetract')}
          onReset={() => onReset?.('wipeBeforeRetract')}
          originalValue={getOriginalValue?.('wipeBeforeRetract')}
          tooltip="Wipe nozzle on perimeter before retracting"
        />
      </SettingSection>
    </div>
  );
};
