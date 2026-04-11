/**
 * Line width settings editor for OrcaSlicer
 * Controls extrusion width for different features
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { LineWidthIcon } from '../SlicerSettingIcons';
import type { OrcaProcessSettings } from '../slicerSettingsTypes';

interface LineWidthEditorProps {
  settings: OrcaProcessSettings;
  onChange: <K extends keyof OrcaProcessSettings>(key: K, value: OrcaProcessSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof OrcaProcessSettings) => boolean;
  onReset?: (key: keyof OrcaProcessSettings) => void;
  getOriginalValue?: <K extends keyof OrcaProcessSettings>(key: K) => OrcaProcessSettings[K];
}

export const LineWidthEditor: React.FC<LineWidthEditorProps> = ({
  settings,
  onChange,
  disabled,
  hasChanges,
  onReset,
  getOriginalValue,
}) => {
  return (
    <div className="space-y-4">
      <SettingSection title="Default & First Layer" icon={<LineWidthIcon />}>
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="Default Line Width"
          value={settings.line_width}
          onChange={(v) => onChange('line_width', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('line_width')}
          onReset={() => onReset?.('line_width')}
          originalValue={getOriginalValue?.('line_width')}
          tooltip="Default extrusion width (typically nozzle diameter or slightly larger)"
        />
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="First Layer Line Width"
          value={settings.initial_layer_line_width}
          onChange={(v) => onChange('initial_layer_line_width', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('initial_layer_line_width')}
          onReset={() => onReset?.('initial_layer_line_width')}
          originalValue={getOriginalValue?.('initial_layer_line_width')}
          tooltip="Extrusion width for first layer (wider = better adhesion)"
        />
      </SettingSection>

      <SettingSection title="Wall Line Widths" icon={<LineWidthIcon />}>
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="Outer Wall Line Width"
          value={settings.outer_wall_line_width}
          onChange={(v) => onChange('outer_wall_line_width', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('outer_wall_line_width')}
          onReset={() => onReset?.('outer_wall_line_width')}
          originalValue={getOriginalValue?.('outer_wall_line_width')}
          tooltip="Extrusion width for outer perimeter (affects surface quality)"
        />
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="Inner Wall Line Width"
          value={settings.inner_wall_line_width}
          onChange={(v) => onChange('inner_wall_line_width', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('inner_wall_line_width')}
          onReset={() => onReset?.('inner_wall_line_width')}
          originalValue={getOriginalValue?.('inner_wall_line_width')}
          tooltip="Extrusion width for inner perimeters"
        />
      </SettingSection>

      <SettingSection title="Infill Line Widths" icon={<LineWidthIcon />}>
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="Sparse Infill Line Width"
          value={settings.sparse_infill_line_width}
          onChange={(v) => onChange('sparse_infill_line_width', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('sparse_infill_line_width')}
          onReset={() => onReset?.('sparse_infill_line_width')}
          originalValue={getOriginalValue?.('sparse_infill_line_width')}
          tooltip="Extrusion width for sparse infill patterns"
        />
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="Internal Solid Infill Width"
          value={settings.internal_solid_infill_line_width}
          onChange={(v) => onChange('internal_solid_infill_line_width', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('internal_solid_infill_line_width')}
          onReset={() => onReset?.('internal_solid_infill_line_width')}
          originalValue={getOriginalValue?.('internal_solid_infill_line_width')}
          tooltip="Extrusion width for solid internal infill"
        />
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="Top Surface Line Width"
          value={settings.top_surface_line_width}
          onChange={(v) => onChange('top_surface_line_width', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('top_surface_line_width')}
          onReset={() => onReset?.('top_surface_line_width')}
          originalValue={getOriginalValue?.('top_surface_line_width')}
          tooltip="Extrusion width for top surface finish"
        />
      </SettingSection>

      <SettingSection title="Support" icon={<LineWidthIcon />}>
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="Support Line Width"
          value={settings.support_line_width}
          onChange={(v) => onChange('support_line_width', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('support_line_width')}
          onReset={() => onReset?.('support_line_width')}
          originalValue={getOriginalValue?.('support_line_width')}
          tooltip="Extrusion width for support structures"
        />
      </SettingSection>
    </div>
  );
};
