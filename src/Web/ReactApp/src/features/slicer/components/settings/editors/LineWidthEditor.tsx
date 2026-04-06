/**
 * Line width settings editor for OrcaSlicer
 * Controls extrusion width for different features
 */
import React from 'react';
import { SettingRow, SettingSection } from '../SettingRow';
import { LineWidthIcon } from '../SlicerSettingIcons';
import type { AdvancedSlicerSettings } from '../slicerSettingsTypes';

interface LineWidthEditorProps {
  settings: AdvancedSlicerSettings;
  onChange: <K extends keyof AdvancedSlicerSettings>(key: K, value: AdvancedSlicerSettings[K]) => void;
  disabled?: boolean;
  hasChanges?: (key: keyof AdvancedSlicerSettings) => boolean;
  onReset?: (key: keyof AdvancedSlicerSettings) => void;
  getOriginalValue?: <K extends keyof AdvancedSlicerSettings>(key: K) => AdvancedSlicerSettings[K];
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
          value={settings.lineWidthDefault}
          onChange={(v) => onChange('lineWidthDefault', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('lineWidthDefault')}
          onReset={() => onReset?.('lineWidthDefault')}
          originalValue={getOriginalValue?.('lineWidthDefault')}
          tooltip="Default extrusion width (typically nozzle diameter or slightly larger)"
        />
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="First Layer Line Width"
          value={settings.lineWidthFirstLayer}
          onChange={(v) => onChange('lineWidthFirstLayer', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('lineWidthFirstLayer')}
          onReset={() => onReset?.('lineWidthFirstLayer')}
          originalValue={getOriginalValue?.('lineWidthFirstLayer')}
          tooltip="Extrusion width for first layer (wider = better adhesion)"
        />
      </SettingSection>

      <SettingSection title="Wall Line Widths" icon={<LineWidthIcon />}>
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="Outer Wall Line Width"
          value={settings.lineWidthOuterWall}
          onChange={(v) => onChange('lineWidthOuterWall', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('lineWidthOuterWall')}
          onReset={() => onReset?.('lineWidthOuterWall')}
          originalValue={getOriginalValue?.('lineWidthOuterWall')}
          tooltip="Extrusion width for outer perimeter (affects surface quality)"
        />
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="Inner Wall Line Width"
          value={settings.lineWidthInnerWall}
          onChange={(v) => onChange('lineWidthInnerWall', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('lineWidthInnerWall')}
          onReset={() => onReset?.('lineWidthInnerWall')}
          originalValue={getOriginalValue?.('lineWidthInnerWall')}
          tooltip="Extrusion width for inner perimeters"
        />
      </SettingSection>

      <SettingSection title="Infill Line Widths" icon={<LineWidthIcon />}>
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="Sparse Infill Line Width"
          value={settings.lineWidthSparseInfill}
          onChange={(v) => onChange('lineWidthSparseInfill', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('lineWidthSparseInfill')}
          onReset={() => onReset?.('lineWidthSparseInfill')}
          originalValue={getOriginalValue?.('lineWidthSparseInfill')}
          tooltip="Extrusion width for sparse infill patterns"
        />
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="Internal Solid Infill Width"
          value={settings.lineWidthInternalSolidInfill}
          onChange={(v) => onChange('lineWidthInternalSolidInfill', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('lineWidthInternalSolidInfill')}
          onReset={() => onReset?.('lineWidthInternalSolidInfill')}
          originalValue={getOriginalValue?.('lineWidthInternalSolidInfill')}
          tooltip="Extrusion width for solid internal infill"
        />
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="Top Surface Line Width"
          value={settings.lineWidthTopSurface}
          onChange={(v) => onChange('lineWidthTopSurface', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('lineWidthTopSurface')}
          onReset={() => onReset?.('lineWidthTopSurface')}
          originalValue={getOriginalValue?.('lineWidthTopSurface')}
          tooltip="Extrusion width for top surface finish"
        />
      </SettingSection>

      <SettingSection title="Support" icon={<LineWidthIcon />}>
        <SettingRow
          type="slider"
          icon={<LineWidthIcon />}
          label="Support Line Width"
          value={settings.lineWidthSupport}
          onChange={(v) => onChange('lineWidthSupport', v)}
          min={0.1}
          max={1.0}
          step={0.01}
          unit="mm"
          disabled={disabled}
          isModified={hasChanges?.('lineWidthSupport')}
          onReset={() => onReset?.('lineWidthSupport')}
          originalValue={getOriginalValue?.('lineWidthSupport')}
          tooltip="Extrusion width for support structures"
        />
      </SettingSection>
    </div>
  );
};
