/**
 * Support settings editor for OrcaSlicer
 * Controls support structure generation and configuration
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { SupportIcon } from '../SlicerSettingIcons';
import type { AdvancedSlicerSettings } from '../slicerSettingsTypes';

interface SupportEditorProps {
  settings: AdvancedSlicerSettings;
  onChange: <K extends keyof AdvancedSlicerSettings>(key: K, value: AdvancedSlicerSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof AdvancedSlicerSettings) => boolean;
  onReset?: (key: keyof AdvancedSlicerSettings) => void;
  getOriginalValue?: <K extends keyof AdvancedSlicerSettings>(key: K) => AdvancedSlicerSettings[K];
}

export const SupportEditor: React.FC<SupportEditorProps> = ({
  settings,
  onChange,
  disabled,
  hasChanges,
  onReset,
  getOriginalValue,
}) => {
  const supportsDisabled = disabled || !settings.enableSupports;

  return (
    <div className="space-y-4">
      <SettingSection title="Support Generation" icon={<SupportIcon />}>
        <SettingRow
          type="checkbox"
          icon={<SupportIcon />}
          label="Enable Supports"
          value={settings.enableSupports}
          onChange={(v) => onChange('enableSupports', v)}
          disabled={disabled}
          isModified={hasChanges?.('enableSupports')}
          onReset={() => onReset?.('enableSupports')}
          originalValue={getOriginalValue?.('enableSupports')}
          tooltip="Generate support structures for overhangs"
        />
        <SettingRow
          type="select"
          icon={<SupportIcon />}
          label="Support Type"
          value={settings.supportType}
          onChange={(v) => onChange('supportType', v as typeof settings.supportType)}
          options={[
            { value: 'none', label: 'None' },
            { value: 'normal', label: 'Normal' },
            { value: 'tree', label: 'Tree' },
            { value: 'tree_auto', label: 'Tree (Auto)' },
          ]}
          disabled={supportsDisabled}
          isModified={hasChanges?.('supportType')}
          onReset={() => onReset?.('supportType')}
          originalValue={getOriginalValue?.('supportType')}
          tooltip="Support structure type: normal grid or organic tree supports"
        />
      </SettingSection>

      <SettingSection title="Support Density & Angle" icon={<SupportIcon />}>
        <SettingRow
          type="slider"
          icon={<SupportIcon />}
          label="Support Density"
          value={settings.supportDensity}
          onChange={(v) => onChange('supportDensity', v)}
          min={5}
          max={100}
          step={5}
          unit="%"
          disabled={supportsDisabled}
          isModified={hasChanges?.('supportDensity')}
          onReset={() => onReset?.('supportDensity')}
          originalValue={getOriginalValue?.('supportDensity')}
          tooltip="Infill density of support structures (15-20% typical)"
        />
        <SettingRow
          type="slider"
          icon={<SupportIcon />}
          label="Support Angle"
          value={settings.supportAngle}
          onChange={(v) => onChange('supportAngle', v)}
          min={0}
          max={90}
          step={5}
          unit="°"
          disabled={supportsDisabled}
          isModified={hasChanges?.('supportAngle')}
          onReset={() => onReset?.('supportAngle')}
          originalValue={getOriginalValue?.('supportAngle')}
          tooltip="Overhang angle requiring support (45° typical, lower = more support)"
        />
      </SettingSection>

      <SettingSection title="Support Distances" icon={<SupportIcon />}>
        <SettingRow
          type="slider"
          icon={<SupportIcon />}
          label="Top Z Distance"
          value={settings.supportTopZDistance}
          onChange={(v) => onChange('supportTopZDistance', v)}
          min={0}
          max={1}
          step={0.05}
          unit="mm"
          disabled={supportsDisabled}
          isModified={hasChanges?.('supportTopZDistance')}
          onReset={() => onReset?.('supportTopZDistance')}
          originalValue={getOriginalValue?.('supportTopZDistance')}
          tooltip="Gap between support top and model (easier removal, lower quality)"
        />
        <SettingRow
          type="slider"
          icon={<SupportIcon />}
          label="Bottom Z Distance"
          value={settings.supportBottomZDistance}
          onChange={(v) => onChange('supportBottomZDistance', v)}
          min={0}
          max={1}
          step={0.05}
          unit="mm"
          disabled={supportsDisabled}
          isModified={hasChanges?.('supportBottomZDistance')}
          onReset={() => onReset?.('supportBottomZDistance')}
          originalValue={getOriginalValue?.('supportBottomZDistance')}
          tooltip="Gap between model and support when model is on top of support"
        />
        <SettingRow
          type="slider"
          icon={<SupportIcon />}
          label="XY Distance"
          value={settings.supportXYDistance}
          onChange={(v) => onChange('supportXYDistance', v)}
          min={0}
          max={5}
          step={0.1}
          unit="mm"
          disabled={supportsDisabled}
          isModified={hasChanges?.('supportXYDistance')}
          onReset={() => onReset?.('supportXYDistance')}
          originalValue={getOriginalValue?.('supportXYDistance')}
          tooltip="Horizontal gap between support and model perimeter"
        />
      </SettingSection>

      <SettingSection title="Support Interface" icon={<SupportIcon />}>
        <SettingRow
          type="slider"
          icon={<SupportIcon />}
          label="Interface Layers"
          value={settings.supportInterfaceLayers}
          onChange={(v) => onChange('supportInterfaceLayers', v)}
          min={0}
          max={10}
          step={1}
          unit="layers"
          disabled={supportsDisabled}
          isModified={hasChanges?.('supportInterfaceLayers')}
          onReset={() => onReset?.('supportInterfaceLayers')}
          originalValue={getOriginalValue?.('supportInterfaceLayers')}
          tooltip="Dense interface layers between support and model (better surface)"
        />
        <SettingRow
          type="slider"
          icon={<SupportIcon />}
          label="Base Interface Layers"
          value={settings.supportBaseInterfaceLayers}
          onChange={(v) => onChange('supportBaseInterfaceLayers', v)}
          min={0}
          max={10}
          step={1}
          unit="layers"
          disabled={supportsDisabled}
          isModified={hasChanges?.('supportBaseInterfaceLayers')}
          onReset={() => onReset?.('supportBaseInterfaceLayers')}
          originalValue={getOriginalValue?.('supportBaseInterfaceLayers')}
          tooltip="Interface layers between support and build plate"
        />
      </SettingSection>
    </div>
  );
};
