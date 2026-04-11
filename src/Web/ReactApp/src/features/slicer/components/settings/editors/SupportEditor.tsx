/**
 * Support settings editor for OrcaSlicer
 * Controls support structure generation and configuration
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { SupportIcon } from '../SlicerSettingIcons';
import type { OrcaProcessSettings } from '../slicerSettingsTypes';

interface SupportEditorProps {
  settings: OrcaProcessSettings;
  onChange: <K extends keyof OrcaProcessSettings>(key: K, value: OrcaProcessSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof OrcaProcessSettings) => boolean;
  onReset?: (key: keyof OrcaProcessSettings) => void;
  getOriginalValue?: <K extends keyof OrcaProcessSettings>(key: K) => OrcaProcessSettings[K];
}

export const SupportEditor: React.FC<SupportEditorProps> = ({
  settings,
  onChange,
  disabled,
  hasChanges,
  onReset,
  getOriginalValue,
}) => {
  const supportsDisabled = disabled || !settings.enable_support;

  return (
    <div className="space-y-4">
      <SettingSection title="Support Generation" icon={<SupportIcon />}>
        <SettingRow
          type="checkbox"
          icon={<SupportIcon />}
          label="Enable Supports"
          value={settings.enable_support}
          onChange={(v) => onChange('enable_support', v)}
          disabled={disabled}
          isModified={hasChanges?.('enable_support')}
          onReset={() => onReset?.('enable_support')}
          originalValue={getOriginalValue?.('enable_support')}
          tooltip="Generate support structures for overhangs"
        />
        <SettingRow
          type="select"
          icon={<SupportIcon />}
          label="Support Type"
          value={settings.support_type}
          onChange={(v) => onChange('support_type', v as typeof settings.support_type)}
          options={[
            { value: 'none', label: 'None' },
            { value: 'normal', label: 'Normal' },
            { value: 'tree', label: 'Tree' },
            { value: 'tree_auto', label: 'Tree (Auto)' },
          ]}
          disabled={supportsDisabled}
          isModified={hasChanges?.('support_type')}
          onReset={() => onReset?.('support_type')}
          originalValue={getOriginalValue?.('support_type')}
          tooltip="Support structure type: normal grid or organic tree supports"
        />
      </SettingSection>

      <SettingSection title="Support Density & Angle" icon={<SupportIcon />}>
        <SettingRow
          type="slider"
          icon={<SupportIcon />}
          label="Support Density"
          value={settings.support_base_pattern_spacing}
          onChange={(v) => onChange('support_base_pattern_spacing', v)}
          min={5}
          max={100}
          step={5}
          unit="%"
          disabled={supportsDisabled}
          isModified={hasChanges?.('support_base_pattern_spacing')}
          onReset={() => onReset?.('support_base_pattern_spacing')}
          originalValue={getOriginalValue?.('support_base_pattern_spacing')}
          tooltip="Infill density of support structures (15-20% typical)"
        />
        <SettingRow
          type="slider"
          icon={<SupportIcon />}
          label="Support Angle"
          value={settings.support_threshold_angle}
          onChange={(v) => onChange('support_threshold_angle', v)}
          min={0}
          max={90}
          step={5}
          unit="°"
          disabled={supportsDisabled}
          isModified={hasChanges?.('support_threshold_angle')}
          onReset={() => onReset?.('support_threshold_angle')}
          originalValue={getOriginalValue?.('support_threshold_angle')}
          tooltip="Overhang angle requiring support (45° typical, lower = more support)"
        />
      </SettingSection>

      <SettingSection title="Support Distances" icon={<SupportIcon />}>
        <SettingRow
          type="slider"
          icon={<SupportIcon />}
          label="Top Z Distance"
          value={settings.support_top_z_distance}
          onChange={(v) => onChange('support_top_z_distance', v)}
          min={0}
          max={1}
          step={0.05}
          unit="mm"
          disabled={supportsDisabled}
          isModified={hasChanges?.('support_top_z_distance')}
          onReset={() => onReset?.('support_top_z_distance')}
          originalValue={getOriginalValue?.('support_top_z_distance')}
          tooltip="Gap between support top and model (easier removal, lower quality)"
        />
        <SettingRow
          type="slider"
          icon={<SupportIcon />}
          label="Bottom Z Distance"
          value={settings.support_bottom_z_distance}
          onChange={(v) => onChange('support_bottom_z_distance', v)}
          min={0}
          max={1}
          step={0.05}
          unit="mm"
          disabled={supportsDisabled}
          isModified={hasChanges?.('support_bottom_z_distance')}
          onReset={() => onReset?.('support_bottom_z_distance')}
          originalValue={getOriginalValue?.('support_bottom_z_distance')}
          tooltip="Gap between model and support when model is on top of support"
        />
        <SettingRow
          type="slider"
          icon={<SupportIcon />}
          label="XY Distance"
          value={settings.support_object_xy_distance}
          onChange={(v) => onChange('support_object_xy_distance', v)}
          min={0}
          max={5}
          step={0.1}
          unit="mm"
          disabled={supportsDisabled}
          isModified={hasChanges?.('support_object_xy_distance')}
          onReset={() => onReset?.('support_object_xy_distance')}
          originalValue={getOriginalValue?.('support_object_xy_distance')}
          tooltip="Horizontal gap between support and model perimeter"
        />
      </SettingSection>

      <SettingSection title="Support Interface" icon={<SupportIcon />}>
        <SettingRow
          type="slider"
          icon={<SupportIcon />}
          label="Interface Layers"
          value={settings.support_interface_top_layers}
          onChange={(v) => onChange('support_interface_top_layers', v)}
          min={0}
          max={10}
          step={1}
          unit="layers"
          disabled={supportsDisabled}
          isModified={hasChanges?.('support_interface_top_layers')}
          onReset={() => onReset?.('support_interface_top_layers')}
          originalValue={getOriginalValue?.('support_interface_top_layers')}
          tooltip="Dense interface layers between support and model (better surface)"
        />
        <SettingRow
          type="slider"
          icon={<SupportIcon />}
          label="Base Interface Layers"
          value={settings.support_interface_bottom_layers}
          onChange={(v) => onChange('support_interface_bottom_layers', v)}
          min={0}
          max={10}
          step={1}
          unit="layers"
          disabled={supportsDisabled}
          isModified={hasChanges?.('support_interface_bottom_layers')}
          onReset={() => onReset?.('support_interface_bottom_layers')}
          originalValue={getOriginalValue?.('support_interface_bottom_layers')}
          tooltip="Interface layers between support and build plate"
        />
      </SettingSection>
    </div>
  );
};
