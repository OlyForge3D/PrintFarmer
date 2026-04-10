/**
 * OrcaSlicer-style Machine Profile Editor
 * Implements Basic | Advanced view modes matching OrcaSlicer's UI
 */
import React, { useState, useCallback } from 'react';
import { Button } from '@/common/components/ui';
import { SettingRow } from './SettingRow';
import {
  SpeedIcon,
  AccelerationIcon,
} from './SlicerSettingIcons';
import type {
  MachineSettingsViewMode,
  MachineSettingsCategory,
  BasicMachineSettings,
  AdvancedMachineSettings,
} from './machineSettingsTypes';
import {
  PRINTER_PRESETS,
  GCODE_DIALECT_LABELS,
  MOTION_TYPE_LABELS,
  NOZZLE_TYPE_LABELS,
  BED_TYPE_LABELS,
  PROBE_TYPE_LABELS,
} from './machineSettingsTypes';

interface MachineProfileEditorProps {
  /** Current settings values */
  settings: BasicMachineSettings | AdvancedMachineSettings;
  /** Called when any setting changes */
  onChange: (settings: BasicMachineSettings | AdvancedMachineSettings) => void;
  /** Initial view mode */
  initialViewMode?: MachineSettingsViewMode;
  /** Disable all controls */
  disabled?: boolean;
  /** Custom class name */
  className?: string;
  /** Optional function to check if a category has modified settings */
  isCategoryDirty?: (category: MachineSettingsCategory) => boolean;
}

// Icon components for Machine settings
const BuildVolumeIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
    <polyline points="3.27 6.96 12 12.01 20.73 6.96" />
    <line x1="12" y1="22.08" x2="12" y2="12" />
  </svg>
);

const NozzleIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M12 3v6" />
    <path d="M9 9h6v4l-3 8-3-8V9z" />
    <circle cx="12" cy="21" r="1" fill="currentColor" />
  </svg>
);

const BedIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="2" y="14" width="20" height="4" rx="1" />
    <path d="M4 14v-2a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v2" />
    <path d="M12 10V6" />
    <circle cx="12" cy="5" r="1" />
  </svg>
);

const GcodeIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <polyline points="4 17 10 11 4 5" />
    <line x1="12" y1="19" x2="20" y2="19" />
  </svg>
);

const CapabilitiesIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <circle cx="12" cy="12" r="3" />
    <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z" />
  </svg>
);

/**
 * MachineProfileEditor - Full OrcaSlicer-style machine/printer settings panel
 */
export const MachineProfileEditor: React.FC<MachineProfileEditorProps> = ({
  settings,
  onChange,
  initialViewMode = 'simple',
  disabled = false,
  className = '',
  isCategoryDirty,
}) => {
  const [viewMode, setViewMode] = useState<MachineSettingsViewMode>(initialViewMode);
  const [activeCategory, setActiveCategory] = useState<MachineSettingsCategory>('general');

  // Update a single setting
  const updateSetting = useCallback(<K extends keyof AdvancedMachineSettings>(
    key: K,
    value: AdvancedMachineSettings[K]
  ) => {
    onChange({ ...settings, [key]: value });
  }, [settings, onChange]);

  // Apply printer preset
  const applyPreset = useCallback((presetName: string) => {
    const preset = PRINTER_PRESETS[presetName];
    if (preset) {
      onChange({ ...settings, ...preset });
    }
  }, [settings, onChange]);

  // View mode tabs
  const viewModes: { id: MachineSettingsViewMode; label: string }[] = [
    { id: 'simple', label: 'Simple' },
    { id: 'advanced', label: 'Advanced' },
  ];

  // Category tabs for advanced mode
  const categories: { id: MachineSettingsCategory; label: string; icon: React.ReactNode }[] = [
    { id: 'general', label: 'General', icon: <BuildVolumeIcon className="w-4 h-4" /> },
    { id: 'extruder', label: 'Extruder', icon: <NozzleIcon className="w-4 h-4" /> },
    { id: 'printbed', label: 'Print Bed', icon: <BedIcon className="w-4 h-4" /> },
    { id: 'capabilities', label: 'Capabilities', icon: <CapabilitiesIcon className="w-4 h-4" /> },
    { id: 'gcode', label: 'G-code', icon: <GcodeIcon className="w-4 h-4" /> },
  ];

  // Preset options for dropdown
  const presetOptions = [
    { value: '', label: 'Custom' },
    ...Object.keys(PRINTER_PRESETS).map(name => ({ value: name, label: name })),
  ];

  // Nozzle diameter options
  const nozzleDiameterOptions = [
    { value: '0.2', label: '0.2mm' },
    { value: '0.25', label: '0.25mm' },
    { value: '0.4', label: '0.4mm' },
    { value: '0.5', label: '0.5mm' },
    { value: '0.6', label: '0.6mm' },
    { value: '0.8', label: '0.8mm' },
    { value: '1.0', label: '1.0mm' },
  ];

  return (
    <div className={`bg-pf-bg-1 rounded-lg border border-pf-border ${className}`}>
      {/* View Mode Tabs */}
      <div className="flex border-b border-pf-border">
        {viewModes.map((mode) => (
          <Button
            key={mode.id}
            variant={viewMode === mode.id ? 'tab' : 'subtle'}
            type="button"
            onClick={() => setViewMode(mode.id)}
            disabled={disabled}
            className={`flex-1 px-4 py-3 text-sm font-medium rounded-none
                       ${viewMode === mode.id ? 'rounded-t-lg' : ''}`}
          >
            {mode.label}
          </Button>
        ))}
      </div>

      {/* Settings Content */}
      <div className="p-4">
        {viewMode === 'simple' && (
          <BasicMachineSettingsPanel
            settings={settings}
            onUpdate={updateSetting}
            onPresetChange={applyPreset}
            disabled={disabled}
            presetOptions={presetOptions}
            nozzleDiameterOptions={nozzleDiameterOptions}
          />
        )}

        {viewMode === 'advanced' && (
          <>
            {/* Category Tabs */}
            <div className="flex gap-1 mb-4 overflow-x-auto pb-2">
              {categories.map((cat) => {
                const isDirty = isCategoryDirty?.(cat.id) ?? false;
                return (
                  <Button
                    key={cat.id}
                    variant={activeCategory === cat.id ? 'tab' : 'subtle'}
                    type="button"
                    onClick={() => setActiveCategory(cat.id)}
                    disabled={disabled}
                    className={`flex items-center gap-1 px-3 py-1.5 text-xs font-medium rounded-full whitespace-nowrap relative
                               ${isDirty ? 'ring-1 ring-pf-accent-orange ring-offset-1 ring-offset-pf-surface' : ''}`}
                  >
                    {cat.icon}
                    {cat.label}
                    {isDirty && (
                      <span
                        className="absolute -top-0.5 -right-0.5 w-2 h-2 rounded-full bg-pf-accent-orange"
                        aria-label="Has modified settings"
                      />
                    )}
                  </Button>
                );
              })}
            </div>

            <AdvancedMachineSettingsPanel
              settings={settings as AdvancedMachineSettings}
              onUpdate={updateSetting}
              disabled={disabled}
              activeCategory={activeCategory}
              presetOptions={presetOptions}
              onPresetChange={applyPreset}
              nozzleDiameterOptions={nozzleDiameterOptions}
            />
          </>
        )}
      </div>
    </div>
  );
};

/** Basic machine settings view */
const BasicMachineSettingsPanel: React.FC<{
  settings: BasicMachineSettings;
  onUpdate: <K extends keyof AdvancedMachineSettings>(key: K, value: AdvancedMachineSettings[K]) => void;
  onPresetChange: (preset: string) => void;
  disabled: boolean;
  presetOptions: Array<{ value: string; label: string }>;
  nozzleDiameterOptions: Array<{ value: string; label: string }>;
}> = ({ settings, onUpdate, onPresetChange, disabled, presetOptions, nozzleDiameterOptions }) => (
  <div className="divide-y divide-pf-border">
    {/* Printer Preset */}
    <SettingRow
      type="select"
      label="Printer Preset"
      icon={<BuildVolumeIcon className="w-5 h-5" />}
      value=""
      options={presetOptions}
      onChange={onPresetChange}
      disabled={disabled}
      description="Apply a preset configuration"
    />

    {/* Printer Name */}
    <SettingRow
      type="text"
      label="Printer Name"
      icon={<BuildVolumeIcon className="w-5 h-5" />}
      value={settings.name}
      onChange={(v) => onUpdate('name', v)}
      disabled={disabled}
      description="Display name for this printer"
    />

    {/* Build Volume */}
    <div className="py-3">
      <h4 className="text-sm font-medium text-pf-text-primary mb-3 flex items-center gap-2">
        <BuildVolumeIcon className="w-5 h-5" />
        Build Volume (mm)
      </h4>
      <div className="grid grid-cols-3 gap-4">
        <SettingRow
          type="number"
          label="X"
          value={settings.buildVolumeX}
          min={50}
          max={1000}
          step={1}
          onChange={(v) => onUpdate('buildVolumeX', v)}
          disabled={disabled}
        />
        <SettingRow
          type="number"
          label="Y"
          value={settings.buildVolumeY}
          min={50}
          max={1000}
          step={1}
          onChange={(v) => onUpdate('buildVolumeY', v)}
          disabled={disabled}
        />
        <SettingRow
          type="number"
          label="Z"
          value={settings.buildVolumeZ}
          min={50}
          max={1000}
          step={1}
          onChange={(v) => onUpdate('buildVolumeZ', v)}
          disabled={disabled}
        />
      </div>
    </div>

    {/* Nozzle Diameter */}
    <SettingRow
      type="select"
      label="Nozzle Diameter"
      icon={<NozzleIcon className="w-5 h-5" />}
      value={String(settings.nozzleDiameter)}
      options={nozzleDiameterOptions}
      onChange={(v) => onUpdate('nozzleDiameter', parseFloat(v))}
      disabled={disabled}
      description="Nozzle bore diameter"
    />

    {/* Max Print Speed */}
    <SettingRow
      type="slider"
      label="Max Print Speed"
      icon={<SpeedIcon className="w-5 h-5" />}
      value={settings.maxPrintSpeed}
      min={50}
      max={800}
      step={10}
      unit="mm/s"
      onChange={(v) => onUpdate('maxPrintSpeed', v)}
      disabled={disabled}
      description="Maximum supported print speed"
    />
  </div>
);

/** Advanced machine settings panel with categories */
const AdvancedMachineSettingsPanel: React.FC<{
  settings: AdvancedMachineSettings;
  onUpdate: <K extends keyof AdvancedMachineSettings>(key: K, value: AdvancedMachineSettings[K]) => void;
  disabled: boolean;
  activeCategory: MachineSettingsCategory;
  presetOptions: Array<{ value: string; label: string }>;
  onPresetChange: (preset: string) => void;
  nozzleDiameterOptions: Array<{ value: string; label: string }>;
}> = ({ settings, onUpdate, disabled, activeCategory, presetOptions, onPresetChange, nozzleDiameterOptions }) => {
  // Motion type options
  const motionTypeOptions = Object.entries(MOTION_TYPE_LABELS).map(([value, label]) => ({ value, label }));
  
  // G-code dialect options
  const gcodeDialectOptions = Object.entries(GCODE_DIALECT_LABELS).map(([value, label]) => ({ value, label }));
  
  // Nozzle type options
  const nozzleTypeOptions = Object.entries(NOZZLE_TYPE_LABELS).map(([value, label]) => ({ value, label }));
  
  // Bed type options
  const bedTypeOptions = Object.entries(BED_TYPE_LABELS).map(([value, label]) => ({ value, label }));
  
  // Probe type options
  const probeTypeOptions = Object.entries(PROBE_TYPE_LABELS).map(([value, label]) => ({ value, label }));

  return (
    <div className="divide-y divide-pf-border">
      {/* General Category */}
      {activeCategory === 'general' && (
        <>
          {/* Printer Preset */}
          <SettingRow
            type="select"
            label="Printer Preset"
            icon={<BuildVolumeIcon className="w-5 h-5" />}
            value=""
            options={presetOptions}
            onChange={onPresetChange}
            disabled={disabled}
            description="Apply a preset configuration"
          />

          {/* Printer Name */}
          <SettingRow
            type="text"
            label="Printer Name"
            icon={<BuildVolumeIcon className="w-5 h-5" />}
            value={settings.name}
            onChange={(v) => onUpdate('name', v)}
            disabled={disabled}
          />

          {/* Printer Model */}
          <SettingRow
            type="text"
            label="Printer Model"
            value={settings.printerModel || ''}
            onChange={(v) => onUpdate('printerModel', v)}
            disabled={disabled}
          />

          {/* Printer Variant */}
          <SettingRow
            type="text"
            label="Variant"
            value={settings.printerVariant || ''}
            onChange={(v) => onUpdate('printerVariant', v)}
            disabled={disabled}
          />

          {/* Build Volume */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3 flex items-center gap-2">
              <BuildVolumeIcon className="w-5 h-5" />
              Build Volume (mm)
            </h4>
            <div className="grid grid-cols-3 gap-4">
              <SettingRow
                type="number"
                label="X"
                value={settings.buildVolumeX}
                min={50}
                max={1000}
                step={1}
                onChange={(v) => onUpdate('buildVolumeX', v)}
                disabled={disabled}
              />
              <SettingRow
                type="number"
                label="Y"
                value={settings.buildVolumeY}
                min={50}
                max={1000}
                step={1}
                onChange={(v) => onUpdate('buildVolumeY', v)}
                disabled={disabled}
              />
              <SettingRow
                type="number"
                label="Z"
                value={settings.buildVolumeZ}
                min={50}
                max={1000}
                step={1}
                onChange={(v) => onUpdate('buildVolumeZ', v)}
                disabled={disabled}
              />
            </div>
          </div>

          {/* Build Volume Origin */}
          <SettingRow
            type="select"
            label="Build Volume Origin"
            value={settings.buildVolumeOrigin || 'corner'}
            options={[
              { value: 'corner', label: 'Corner (0,0)' },
              { value: 'center', label: 'Center' },
            ]}
            onChange={(v) => onUpdate('buildVolumeOrigin', v as 'corner' | 'center')}
            disabled={disabled}
          />

          {/* Layer height limits */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3">Layer Height Limits</h4>
            <div className="grid grid-cols-2 gap-4">
              <SettingRow
                type="number"
                label="Min Layer Height"
                value={settings.minLayerHeight || 0.08}
                min={0.04}
                max={0.2}
                step={0.01}
                unit="mm"
                onChange={(v) => onUpdate('minLayerHeight', v)}
                disabled={disabled}
              />
              <SettingRow
                type="number"
                label="Max Layer Height"
                value={settings.maxLayerHeight || 0.28}
                min={0.1}
                max={1.0}
                step={0.01}
                unit="mm"
                onChange={(v) => onUpdate('maxLayerHeight', v)}
                disabled={disabled}
              />
            </div>
          </div>

          {/* Thumbnail Size */}
          <SettingRow
            type="text"
            label="Thumbnail Size"
            value={settings.thumbnailSize || '300x300,32x32'}
            onChange={(v) => onUpdate('thumbnailSize', v)}
            disabled={disabled}
            tooltip="e.g., 300x300,32x32 for multiple sizes"
          />
        </>
      )}

      {/* Extruder Category */}
      {activeCategory === 'extruder' && (
        <>
          {/* Extruder Count */}
          <SettingRow
            type="number"
            label="Extruder Count"
            value={settings.extruderCount || 1}
            min={1}
            max={8}
            step={1}
            onChange={(v) => onUpdate('extruderCount', v)}
            disabled={disabled}
          />

          {/* Nozzle Settings */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3 flex items-center gap-2">
              <NozzleIcon className="w-5 h-5" />
              Nozzle
            </h4>
            <div className="space-y-3">
              <SettingRow
                type="select"
                label="Nozzle Diameter"
                value={String(settings.nozzleDiameter)}
                options={nozzleDiameterOptions}
                onChange={(v) => onUpdate('nozzleDiameter', parseFloat(v))}
                disabled={disabled}
              />
              <SettingRow
                type="select"
                label="Nozzle Type"
                value={settings.nozzleType || 'brass'}
                options={nozzleTypeOptions}
                onChange={(v) => onUpdate('nozzleType', v as AdvancedMachineSettings['nozzleType'])}
                disabled={disabled}
              />
            </div>
          </div>

          {/* Retraction Settings */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3">Retraction</h4>
            <div className="space-y-3">
              <SettingRow
                type="slider"
                label="Retraction Length"
                value={settings.retractionLength || 0.8}
                min={0}
                max={10}
                step={0.1}
                unit="mm"
                onChange={(v) => onUpdate('retractionLength', v)}
                disabled={disabled}
              />
              <SettingRow
                type="slider"
                label="Retraction Speed"
                value={settings.retractionSpeed || 35}
                min={5}
                max={100}
                step={1}
                unit="mm/s"
                onChange={(v) => onUpdate('retractionSpeed', v)}
                disabled={disabled}
              />
              <SettingRow
                type="slider"
                label="Detraction Speed"
                value={settings.detractionSpeed || 25}
                min={5}
                max={100}
                step={1}
                unit="mm/s"
                onChange={(v) => onUpdate('detractionSpeed', v)}
                disabled={disabled}
              />
              <SettingRow
                type="slider"
                label="Z Hop"
                value={settings.retractionLiftZ || 0.2}
                min={0}
                max={2}
                step={0.05}
                unit="mm"
                onChange={(v) => onUpdate('retractionLiftZ', v)}
                disabled={disabled}
              />
            </div>
          </div>

          {/* Extrusion Multiplier */}
          <SettingRow
            type="slider"
            label="Extrusion Multiplier"
            value={settings.extrusionMultiplier || 1.0}
            min={0.8}
            max={1.2}
            step={0.01}
            onChange={(v) => onUpdate('extrusionMultiplier', v)}
            disabled={disabled}
            description="Flow rate multiplier"
          />

          {/* Advanced Retraction */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3">Advanced Retraction</h4>
            <div className="space-y-3">
              <div className="grid grid-cols-2 gap-4">
                <SettingRow
                  type="number"
                  label="Restart Extra"
                  value={settings.retractionRestartExtra ?? 0}
                  min={0}
                  max={2}
                  step={0.01}
                  unit="mm"
                  onChange={(v) => onUpdate('retractionRestartExtra', v)}
                  disabled={disabled}
                />
                <SettingRow
                  type="number"
                  label="Restart Extra (Toolchange)"
                  value={settings.retractionRestartExtraToolchange ?? 0}
                  min={0}
                  max={5}
                  step={0.1}
                  unit="mm"
                  onChange={(v) => onUpdate('retractionRestartExtraToolchange', v)}
                  disabled={disabled}
                />
              </div>
              <SettingRow
                type="number"
                label="Retraction Length (Toolchange)"
                value={settings.retractionLengthToolchange ?? 10}
                min={0}
                max={20}
                step={0.5}
                unit="mm"
                onChange={(v) => onUpdate('retractionLengthToolchange', v)}
                disabled={disabled}
              />
              <SettingRow
                type="slider"
                label="Retract Before Wipe %"
                value={settings.retractBeforeWipePercent ?? 0}
                min={0}
                max={100}
                step={1}
                unit="%"
                onChange={(v) => onUpdate('retractBeforeWipePercent', v)}
                disabled={disabled}
              />
            </div>
          </div>

          {/* Wipe */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3">Wipe</h4>
            <div className="space-y-3">
              <div className="grid grid-cols-2 gap-4">
                <SettingRow
                  type="number"
                  label="Wipe Distance"
                  value={settings.wipeDistance ?? 0}
                  min={0}
                  max={10}
                  step={0.5}
                  unit="mm"
                  onChange={(v) => onUpdate('wipeDistance', v)}
                  disabled={disabled}
                />
                <SettingRow
                  type="number"
                  label="Wipe Speed"
                  value={settings.wipeSpeed ?? 80}
                  min={0}
                  max={200}
                  step={5}
                  unit="mm/s"
                  onChange={(v) => onUpdate('wipeSpeed', v)}
                  disabled={disabled}
                />
              </div>
              <SettingRow
                type="checkbox"
                label="Wipe Before External Loop"
                checked={settings.wipeBeforeExternalLoop ?? false}
                onChange={(v) => onUpdate('wipeBeforeExternalLoop', v)}
                disabled={disabled}
              />
              <SettingRow
                type="checkbox"
                label="Wipe On Loops"
                checked={settings.wipeOnLoops ?? false}
                onChange={(v) => onUpdate('wipeOnLoops', v)}
                disabled={disabled}
              />
            </div>
          </div>

          {/* Z-Hop */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3">Z-Hop</h4>
            <div className="space-y-3">
              <SettingRow
                type="text"
                label="Z Hop Types"
                value={settings.zHopTypes ?? ''}
                onChange={(v) => onUpdate('zHopTypes', v)}
                disabled={disabled}
                description="Per-surface z-hop control"
              />
              <div className="grid grid-cols-2 gap-4">
                <SettingRow
                  type="number"
                  label="Retraction Lift Above"
                  value={settings.retractionLiftAbove || 0}
                  min={0}
                  max={10}
                  step={0.1}
                  unit="mm"
                  onChange={(v) => onUpdate('retractionLiftAbove', v)}
                  disabled={disabled}
                />
                <SettingRow
                  type="number"
                  label="Retraction Lift Below"
                  value={settings.retractionLiftBelow || 0}
                  min={0}
                  max={10}
                  step={0.1}
                  unit="mm"
                  onChange={(v) => onUpdate('retractionLiftBelow', v)}
                  disabled={disabled}
                />
              </div>
              <SettingRow
                type="text"
                label="Retraction Lift Enforce"
                value={settings.retractionLiftEnforce ?? ''}
                onChange={(v) => onUpdate('retractionLiftEnforce', v)}
                disabled={disabled}
              />
            </div>
          </div>

          {/* Extruder Clearance */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3">Extruder Clearance</h4>
            <div className="space-y-3">
              <SettingRow
                type="select"
                label="Extruder Type"
                value={settings.extruderType ?? 'direct_drive'}
                options={[
                  { value: 'direct_drive', label: 'Direct Drive' },
                  { value: 'bowden', label: 'Bowden' },
                ]}
                onChange={(v) => onUpdate('extruderType', v as 'direct_drive' | 'bowden')}
                disabled={disabled}
              />
              <SettingRow
                type="number"
                label="Clearance Radius"
                value={settings.extruderClearanceRadius ?? 45}
                min={0}
                max={100}
                step={1}
                unit="mm"
                onChange={(v) => onUpdate('extruderClearanceRadius', v)}
                disabled={disabled}
              />
              <div className="grid grid-cols-2 gap-4">
                <SettingRow
                  type="number"
                  label="Clearance Height to Lid"
                  value={settings.extruderClearanceHeightToLid ?? 40}
                  min={0}
                  max={200}
                  step={1}
                  unit="mm"
                  onChange={(v) => onUpdate('extruderClearanceHeightToLid', v)}
                  disabled={disabled}
                />
                <SettingRow
                  type="number"
                  label="Clearance Height to Rod"
                  value={settings.extruderClearanceHeightToRod ?? 36}
                  min={0}
                  max={200}
                  step={1}
                  unit="mm"
                  onChange={(v) => onUpdate('extruderClearanceHeightToRod', v)}
                  disabled={disabled}
                />
              </div>
            </div>
          </div>

          {/* Wipe Tower (Multi-Material) */}
          {settings.supportMultiMaterial && (
            <div className="py-3">
              <h4 className="text-sm font-medium text-pf-text-primary mb-3">Wipe Tower</h4>
              <div className="space-y-3">
                <div className="grid grid-cols-2 gap-4">
                  <SettingRow
                    type="select"
                    label="Tower Type"
                    value={settings.wipeTowerType ?? 'sparse'}
                    options={[
                      { value: 'sparse', label: 'Sparse' },
                      { value: 'dense', label: 'Dense' },
                    ]}
                    onChange={(v) => onUpdate('wipeTowerType', v as 'sparse' | 'dense')}
                    disabled={disabled}
                  />
                  <SettingRow
                    type="select"
                    label="Wall Type"
                    value={settings.wipeTowerWallType ?? 'single'}
                    options={[
                      { value: 'single', label: 'Single' },
                      { value: 'double', label: 'Double' },
                    ]}
                    onChange={(v) => onUpdate('wipeTowerWallType', v as 'single' | 'double')}
                    disabled={disabled}
                  />
                </div>
                <div className="grid grid-cols-2 gap-4">
                  <SettingRow
                    type="number"
                    label="Bridging"
                    value={settings.wipeTowerBridging ?? 10}
                    min={1}
                    max={20}
                    step={0.5}
                    unit="mm"
                    onChange={(v) => onUpdate('wipeTowerBridging', v)}
                    disabled={disabled}
                  />
                  <SettingRow
                    type="number"
                    label="Cone Angle"
                    value={settings.wipeTowerConeAngle ?? 0}
                    min={0}
                    max={90}
                    step={5}
                    unit="°"
                    onChange={(v) => onUpdate('wipeTowerConeAngle', v)}
                    disabled={disabled}
                  />
                </div>
                <div className="grid grid-cols-2 gap-4">
                  <SettingRow
                    type="number"
                    label="Rotation Angle"
                    value={settings.wipeTowerRotationAngle ?? 0}
                    min={0}
                    max={360}
                    step={15}
                    unit="°"
                    onChange={(v) => onUpdate('wipeTowerRotationAngle', v)}
                    disabled={disabled}
                  />
                  <SettingRow
                    type="number"
                    label="Max Purge Speed"
                    value={settings.wipeTowerMaxPurgeSpeed ?? 60}
                    min={10}
                    max={200}
                    step={5}
                    unit="mm/s"
                    onChange={(v) => onUpdate('wipeTowerMaxPurgeSpeed', v)}
                    disabled={disabled}
                  />
                </div>
                <div className="grid grid-cols-2 gap-4">
                  <SettingRow
                    type="number"
                    label="Extra Flow"
                    value={settings.wipeTowerExtraFlow ?? 1.0}
                    min={1}
                    max={2}
                    step={0.05}
                    onChange={(v) => onUpdate('wipeTowerExtraFlow', v)}
                    disabled={disabled}
                  />
                  <SettingRow
                    type="number"
                    label="Extra Spacing"
                    value={settings.wipeTowerExtraSpacing ?? 100}
                    min={100}
                    max={300}
                    step={5}
                    onChange={(v) => onUpdate('wipeTowerExtraSpacing', v)}
                    disabled={disabled}
                  />
                </div>
                <SettingRow
                  type="checkbox"
                  label="No Sparse Layers"
                  checked={settings.wipeTowerNoSparseLayers ?? false}
                  onChange={(v) => onUpdate('wipeTowerNoSparseLayers', v)}
                  disabled={disabled}
                />
                <SettingRow
                  type="checkbox"
                  label="Fillet Wall"
                  checked={settings.wipeTowerFilletWall ?? false}
                  onChange={(v) => onUpdate('wipeTowerFilletWall', v)}
                  disabled={disabled}
                />
                <div className="grid grid-cols-2 gap-4">
                  <SettingRow
                    type="number"
                    label="Rib Width"
                    value={settings.wipeTowerRibWidth ?? 0}
                    min={0}
                    max={5}
                    step={0.1}
                    unit="mm"
                    onChange={(v) => onUpdate('wipeTowerRibWidth', v)}
                    disabled={disabled}
                  />
                  <SettingRow
                    type="number"
                    label="Extra Rib Length"
                    value={settings.wipeTowerExtraRibLength ?? 0}
                    min={0}
                    max={10}
                    step={0.5}
                    unit="mm"
                    onChange={(v) => onUpdate('wipeTowerExtraRibLength', v)}
                    disabled={disabled}
                  />
                </div>
              </div>
            </div>
          )}

          {/* Load/Unload Times */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3">Filament Load/Unload</h4>
            <div className="grid grid-cols-3 gap-4">
              <SettingRow
                type="number"
                label="Load Time"
                value={settings.machineLoadFilamentTime ?? 0}
                min={0}
                max={60}
                step={1}
                unit="s"
                onChange={(v) => onUpdate('machineLoadFilamentTime', v)}
                disabled={disabled}
              />
              <SettingRow
                type="number"
                label="Unload Time"
                value={settings.machineUnloadFilamentTime ?? 0}
                min={0}
                max={60}
                step={1}
                unit="s"
                onChange={(v) => onUpdate('machineUnloadFilamentTime', v)}
                disabled={disabled}
              />
              <SettingRow
                type="number"
                label="Tool Change Time"
                value={settings.machineToolChangeTime ?? 0}
                min={0}
                max={60}
                step={1}
                unit="s"
                onChange={(v) => onUpdate('machineToolChangeTime', v)}
                disabled={disabled}
              />
            </div>
          </div>
        </>
      )}

      {/* Print Bed Category */}
      {activeCategory === 'printbed' && (
        <>
          {/* Bed Shape */}
          <SettingRow
            type="select"
            label="Bed Shape"
            icon={<BedIcon className="w-5 h-5" />}
            value={settings.bedShape || 'rectangular'}
            options={[
              { value: 'rectangular', label: 'Rectangular' },
              { value: 'circular', label: 'Circular (Delta)' },
            ]}
            onChange={(v) => onUpdate('bedShape', v as 'rectangular' | 'circular')}
            disabled={disabled}
          />

          {/* Bed Surface Type */}
          <SettingRow
            type="select"
            label="Bed Surface Type"
            value={settings.bedType || 'textured_pei'}
            options={bedTypeOptions}
            onChange={(v) => onUpdate('bedType', v as AdvancedMachineSettings['bedType'])}
            disabled={disabled}
          />

          {/* Has Bed Probe */}
          <SettingRow
            type="checkbox"
            label="Has Bed Probe"
            checked={settings.hasBedProbe ?? true}
            onChange={(v) => onUpdate('hasBedProbe', v)}
            disabled={disabled}
          />

          {settings.hasBedProbe && (
            <>
              {/* Probe Type */}
              <SettingRow
                type="select"
                label="Probe Type"
                value={settings.probeType || 'inductive'}
                options={probeTypeOptions}
                onChange={(v) => onUpdate('probeType', v as AdvancedMachineSettings['probeType'])}
                disabled={disabled}
              />

              {/* Mesh Bed Leveling */}
              <SettingRow
                type="checkbox"
                label="Mesh Bed Leveling"
                checked={settings.meshBedLeveling ?? true}
                onChange={(v) => onUpdate('meshBedLeveling', v)}
                disabled={disabled}
              />
            </>
          )}

          {/* Custom Bed Texture */}
          <SettingRow
            type="text"
            label="Custom Bed Texture"
            value={settings.bedCustomTexture || ''}
            onChange={(v) => onUpdate('bedCustomTexture', v)}
            disabled={disabled}
            tooltip="Path to bed texture image for visualization"
          />
        </>
      )}

      {/* Capabilities Category */}
      {activeCategory === 'capabilities' && (
        <>
          {/* Temperature Limits */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3">Temperature Limits</h4>
            <div className="space-y-3">
              <SettingRow
                type="checkbox"
                label="Heated Bed"
                checked={settings.hasHeatedBed ?? true}
                onChange={(v) => onUpdate('hasHeatedBed', v)}
                disabled={disabled}
              />

              {settings.hasHeatedBed && (
                <SettingRow
                  type="number"
                  label="Max Bed Temperature"
                  value={settings.maxBedTemperature || 110}
                  min={50}
                  max={200}
                  step={5}
                  unit="°C"
                  onChange={(v) => onUpdate('maxBedTemperature', v)}
                  disabled={disabled}
                />
              )}

              <SettingRow
                type="checkbox"
                label="Heated Chamber"
                checked={settings.hasHeatedChamber ?? false}
                onChange={(v) => onUpdate('hasHeatedChamber', v)}
                disabled={disabled}
              />

              {settings.hasHeatedChamber && (
                <SettingRow
                  type="number"
                  label="Max Chamber Temperature"
                  value={settings.maxChamberTemperature || 0}
                  min={0}
                  max={100}
                  step={5}
                  unit="°C"
                  onChange={(v) => onUpdate('maxChamberTemperature', v)}
                  disabled={disabled}
                />
              )}

              <SettingRow
                type="number"
                label="Max Hotend Temperature"
                value={settings.maxHotendTemperature || 300}
                min={200}
                max={500}
                step={10}
                unit="°C"
                onChange={(v) => onUpdate('maxHotendTemperature', v)}
                disabled={disabled}
              />
            </div>
          </div>

          {/* Motion System */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3 flex items-center gap-2">
              <AccelerationIcon className="w-5 h-5" />
              Motion System
            </h4>
            <div className="space-y-3">
              <SettingRow
                type="select"
                label="Motion Type"
                value={settings.motionType || 'cartesian'}
                options={motionTypeOptions}
                onChange={(v) => onUpdate('motionType', v as AdvancedMachineSettings['motionType'])}
                disabled={disabled}
              />
              <SettingRow
                type="slider"
                label="Max Print Speed"
                icon={<SpeedIcon className="w-5 h-5" />}
                value={settings.maxPrintSpeed}
                min={50}
                max={800}
                step={10}
                unit="mm/s"
                onChange={(v) => onUpdate('maxPrintSpeed', v)}
                disabled={disabled}
              />
              <div className="grid grid-cols-2 gap-4">
                <SettingRow
                  type="number"
                  label="Max Accel X"
                  value={settings.maxAccelerationX || 3000}
                  min={100}
                  max={20000}
                  step={100}
                  unit="mm/s²"
                  onChange={(v) => onUpdate('maxAccelerationX', v)}
                  disabled={disabled}
                />
                <SettingRow
                  type="number"
                  label="Max Accel Y"
                  value={settings.maxAccelerationY || 3000}
                  min={100}
                  max={20000}
                  step={100}
                  unit="mm/s²"
                  onChange={(v) => onUpdate('maxAccelerationY', v)}
                  disabled={disabled}
                />
              </div>
            </div>
          </div>

          {/* Features */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3 flex items-center gap-2">
              <CapabilitiesIcon className="w-5 h-5" />
              Features
            </h4>
            <div className="space-y-3">
              <SettingRow
                type="checkbox"
                label="Multi-Material Support"
                checked={settings.supportMultiMaterial ?? false}
                onChange={(v) => onUpdate('supportMultiMaterial', v)}
                disabled={disabled}
              />
              <SettingRow
                type="checkbox"
                label="Arc Movement (G2/G3)"
                checked={settings.supportArcMovement ?? true}
                onChange={(v) => onUpdate('supportArcMovement', v)}
                disabled={disabled}
              />
              <SettingRow
                type="checkbox"
                label="Filament Sensor"
                checked={settings.filamentSensor ?? false}
                onChange={(v) => onUpdate('filamentSensor', v)}
                disabled={disabled}
              />
              <SettingRow
                type="checkbox"
                label="Power Loss Recovery"
                checked={settings.powerLossRecovery ?? false}
                onChange={(v) => onUpdate('powerLossRecovery', v)}
                disabled={disabled}
              />
              <SettingRow
                type="checkbox"
                label="Auto Bed Leveling"
                checked={settings.autoLevelingEnabled ?? true}
                onChange={(v) => onUpdate('autoLevelingEnabled', v)}
                disabled={disabled}
              />
            </div>
          </div>

          {/* Travel */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3">Travel</h4>
            <div className="space-y-3">
              <div className="grid grid-cols-2 gap-4">
                <SettingRow
                  type="number"
                  label="Travel Speed"
                  value={settings.travelSpeed ?? 200}
                  min={50}
                  max={500}
                  step={10}
                  unit="mm/s"
                  onChange={(v) => onUpdate('travelSpeed', v)}
                  disabled={disabled}
                />
                <SettingRow
                  type="number"
                  label="Travel Acceleration"
                  value={settings.travelAcceleration ?? 5000}
                  min={100}
                  max={20000}
                  step={100}
                  unit="mm/s²"
                  onChange={(v) => onUpdate('travelAcceleration', v)}
                  disabled={disabled}
                />
              </div>
              <SettingRow
                type="number"
                label="Travel Jerk"
                value={settings.travelJerk ?? 8}
                min={0}
                max={20}
                step={0.5}
                unit="mm/s"
                onChange={(v) => onUpdate('travelJerk', v)}
                disabled={disabled}
              />
              <SettingRow
                type="checkbox"
                label="Travel Slope"
                checked={settings.travelSlope ?? false}
                onChange={(v) => onUpdate('travelSlope', v)}
                disabled={disabled}
              />
            </div>
          </div>

          {/* Machine Limits Extended */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3">Machine Limits Extended</h4>
            <div className="space-y-3">
              <div className="grid grid-cols-2 gap-4">
                <SettingRow
                  type="number"
                  label="Max Accel Travel"
                  value={settings.maxAccelerationTravel ?? 5000}
                  min={100}
                  max={20000}
                  step={100}
                  unit="mm/s²"
                  onChange={(v) => onUpdate('maxAccelerationTravel', v)}
                  disabled={disabled}
                />
                <SettingRow
                  type="number"
                  label="Max Junction Deviation"
                  value={settings.maxJunctionDeviation ?? 0.013}
                  min={0}
                  max={1}
                  step={0.001}
                  unit="mm"
                  onChange={(v) => onUpdate('maxJunctionDeviation', v)}
                  disabled={disabled}
                />
              </div>
              <SettingRow
                type="checkbox"
                label="Emit Machine Limits to G-code"
                checked={settings.emitMachineLimitsToGcode ?? true}
                onChange={(v) => onUpdate('emitMachineLimitsToGcode', v)}
                disabled={disabled}
              />
            </div>
          </div>

          {/* Additional Features */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3">Additional Features</h4>
            <div className="space-y-3">
              <div className="grid grid-cols-2 gap-4">
                <SettingRow
                  type="number"
                  label="Nozzle Volume"
                  value={settings.nozzleVolume ?? 0}
                  min={0}
                  max={50}
                  step={0.5}
                  unit="mm³"
                  onChange={(v) => onUpdate('nozzleVolume', v)}
                  disabled={disabled}
                />
                <SettingRow
                  type="select"
                  label="Nozzle Volume Type"
                  value={settings.nozzleVolumeType ?? 'standard'}
                  options={[
                    { value: 'standard', label: 'Standard' },
                    { value: 'high_flow', label: 'High Flow' },
                  ]}
                  onChange={(v) => onUpdate('nozzleVolumeType', v as 'standard' | 'high_flow')}
                  disabled={disabled}
                />
              </div>
              <SettingRow
                type="checkbox"
                label="Has Scarf Joint Seam"
                checked={settings.hasScarfJointSeam ?? false}
                onChange={(v) => onUpdate('hasScarfJointSeam', v)}
                disabled={disabled}
              />
              <SettingRow
                type="checkbox"
                label="Single Extruder Multi Material"
                checked={settings.singleExtruderMultiMaterial ?? false}
                onChange={(v) => onUpdate('singleExtruderMultiMaterial', v)}
                disabled={disabled}
              />
            </div>
          </div>
        </>
      )}

      {/* G-code Category */}
      {activeCategory === 'gcode' && (
        <>
          {/* G-code Flavor */}
          <SettingRow
            type="select"
            label="G-code Flavor"
            icon={<GcodeIcon className="w-5 h-5" />}
            value={settings.gcodeDialect || 'marlin2'}
            options={gcodeDialectOptions}
            onChange={(v) => onUpdate('gcodeDialect', v as AdvancedMachineSettings['gcodeDialect'])}
            disabled={disabled}
          />

          {/* Firmware Features */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3">Firmware Features</h4>
            <div className="space-y-3">
              <SettingRow
                type="checkbox"
                label="Use Relative E Distances"
                checked={settings.useRelativeEDistances ?? false}
                onChange={(v) => onUpdate('useRelativeEDistances', v)}
                disabled={disabled}
              />
              <SettingRow
                type="checkbox"
                label="Use Firmware Retraction"
                checked={settings.useFirmwareRetraction ?? false}
                onChange={(v) => onUpdate('useFirmwareRetraction', v)}
                disabled={disabled}
              />
            </div>
          </div>

          {/* G-code Extended */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3">Extended G-code Features</h4>
            <div className="space-y-3">
              <SettingRow
                type="text"
                label="Thumbnails Format"
                value={settings.thumbnailsFormat ?? 'PNG'}
                onChange={(v) => onUpdate('thumbnailsFormat', v)}
                disabled={disabled}
                description="e.g., PNG or QOI"
              />
              <SettingRow
                type="checkbox"
                label="Scan First Layer"
                checked={settings.scanFirstLayer ?? false}
                onChange={(v) => onUpdate('scanFirstLayer', v)}
                disabled={disabled}
              />
              <SettingRow
                type="text"
                label="Timelapse G-code"
                value={settings.timelapseGcode ?? ''}
                onChange={(v) => onUpdate('timelapseGcode', v)}
                disabled={disabled}
              />
              <SettingRow
                type="text"
                label="Printing By Object G-code"
                value={settings.printingByObjectGcode ?? ''}
                onChange={(v) => onUpdate('printingByObjectGcode', v)}
                disabled={disabled}
                description="G-code for sequential printing"
              />
            </div>
          </div>

          {/* Custom G-code */}
          <div className="py-3">
            <h4 className="text-sm font-medium text-pf-text-primary mb-3">Custom G-code</h4>
            <p className="text-xs text-pf-text-muted mb-3">
              Note: Full G-code editor with multi-line support coming soon.
            </p>
            <SettingRow
              type="text"
              label="Pause G-code"
              value={settings.pauseGcode || 'M601'}
              onChange={(v) => onUpdate('pauseGcode', v)}
              disabled={disabled}
              tooltip="G-code to execute when print is paused"
            />
          </div>
        </>
      )}
    </div>
  );
};

export default MachineProfileEditor;
