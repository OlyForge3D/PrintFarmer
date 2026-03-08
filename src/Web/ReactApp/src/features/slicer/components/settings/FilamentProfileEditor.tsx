/**
 * OrcaSlicer-style Filament Profile Editor
 * Implements Basic | Advanced view modes matching OrcaSlicer's UI
 */
import React, { useState, useCallback } from 'react';
import { Button } from '@/common/components/ui';
import { SettingRow } from './SettingRow';
import {
  TemperatureIcon,
  CoolingIcon,
  RetractionIcon,
  SpeedIcon,
  PrecisionIcon,
} from './SlicerSettingIcons';
import type {
  FilamentSettingsViewMode,
  FilamentSettingsCategory,
  BasicFilamentSettings,
  AdvancedFilamentSettings,
} from './filamentSettingsTypes';
import { MATERIAL_PRESETS } from './filamentSettingsTypes';

interface FilamentProfileEditorProps {
  /** Current settings values */
  settings: BasicFilamentSettings | AdvancedFilamentSettings;
  /** Called when any setting changes */
  onChange: (settings: BasicFilamentSettings | AdvancedFilamentSettings) => void;
  /** Initial view mode */
  initialViewMode?: FilamentSettingsViewMode;
  /** Disable all controls */
  disabled?: boolean;
  /** Custom class name */
  className?: string;
  /** Optional function to check if a category has modified settings */
  isCategoryDirty?: (category: FilamentSettingsCategory) => boolean;
}

// Icon components
const FlowIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M12 3v18" />
    <path d="M8 12l4 4 4-4" />
    <ellipse cx="12" cy="20" rx="6" ry="2" fill="currentColor" opacity="0.3" />
  </svg>
);

const FilamentIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <circle cx="12" cy="12" r="8" />
    <circle cx="12" cy="12" r="3" />
    <path d="M12 4v3" />
    <path d="M12 17v3" />
    <path d="M4 12h3" />
    <path d="M17 12h3" />
  </svg>
);

const ColorIcon: React.FC<{ className?: string }> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <circle cx="12" cy="12" r="9" />
    <path d="M12 3a9 9 0 0 1 0 18" fill="currentColor" opacity="0.3" />
  </svg>
);

/**
 * FilamentProfileEditor - Full OrcaSlicer-style filament settings panel
 */
export const FilamentProfileEditor: React.FC<FilamentProfileEditorProps> = ({
  settings,
  onChange,
  initialViewMode = 'basic',
  disabled = false,
  className = '',
  isCategoryDirty,
}) => {
  const [viewMode, setViewMode] = useState<FilamentSettingsViewMode>(initialViewMode);
  const [activeCategory, setActiveCategory] = useState<FilamentSettingsCategory>('temperature');

  // Update a single setting
  const updateSetting = useCallback(<K extends keyof AdvancedFilamentSettings>(
    key: K,
    value: AdvancedFilamentSettings[K]
  ) => {
    onChange({ ...settings, [key]: value });
  }, [settings, onChange]);

  // Apply material preset
  const applyMaterialPreset = useCallback((material: string) => {
    const preset = MATERIAL_PRESETS[material];
    if (preset) {
      onChange({ ...settings, ...preset });
    }
  }, [settings, onChange]);

  // View mode tabs
  const viewModes: { id: FilamentSettingsViewMode; label: string }[] = [
    { id: 'basic', label: 'Basic' },
    { id: 'advanced', label: 'Advanced' },
  ];

  // Category tabs for advanced mode
  const categories: { id: FilamentSettingsCategory; label: string }[] = [
    { id: 'temperature', label: 'Temperature' },
    { id: 'flow', label: 'Flow' },
    { id: 'cooling', label: 'Cooling' },
    { id: 'retraction', label: 'Retraction' },
    { id: 'other', label: 'Other' },
  ];

  // Material options for dropdown
  const materialOptions = Object.keys(MATERIAL_PRESETS).map(mat => ({
    value: mat,
    label: mat,
  }));

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
        {viewMode === 'basic' && (
          <BasicFilamentSettings
            settings={settings}
            onUpdate={updateSetting}
            onMaterialChange={applyMaterialPreset}
            disabled={disabled}
            materialOptions={materialOptions}
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
                    className={`px-3 py-1.5 text-xs font-medium rounded-full whitespace-nowrap relative
                               ${isDirty ? 'ring-1 ring-pf-accent-orange ring-offset-1 ring-offset-pf-surface' : ''}`}
                  >
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

            <AdvancedFilamentSettingsPanel
              settings={settings as AdvancedFilamentSettings}
              onUpdate={updateSetting}
              disabled={disabled}
              activeCategory={activeCategory}
              materialOptions={materialOptions}
              onMaterialChange={applyMaterialPreset}
            />
          </>
        )}
      </div>
    </div>
  );
};

/** Basic filament settings view */
const BasicFilamentSettings: React.FC<{
  settings: BasicFilamentSettings;
  onUpdate: <K extends keyof AdvancedFilamentSettings>(key: K, value: AdvancedFilamentSettings[K]) => void;
  onMaterialChange: (material: string) => void;
  disabled: boolean;
  materialOptions: Array<{ value: string; label: string }>;
}> = ({ settings, onUpdate, onMaterialChange, disabled, materialOptions }) => (
  <div className="divide-y divide-pf-border">
    {/* Material Type */}
    <SettingRow
      type="select"
      icon={<FilamentIcon />}
      label="Material"
      description="Select material type to apply preset temperatures"
      value={settings.material}
      onChange={(v) => onMaterialChange(v)}
      options={materialOptions}
      disabled={disabled}
    />

    {/* Filament Name */}
    <SettingRow
      type="text"
      icon={<FilamentIcon />}
      label="Name"
      description="Custom name for this filament profile"
      value={settings.name}
      onChange={(v) => onUpdate('name', v)}
      disabled={disabled}
    />

    {/* Color */}
    <SettingRow
      type="color"
      icon={<ColorIcon />}
      label="Color"
      description="Filament color for visualization"
      value={settings.color ?? '#3B82F6'}
      onChange={(v) => onUpdate('color', v)}
      disabled={disabled}
    />

    {/* Nozzle Temperature */}
    <SettingRow
      type="slider"
      icon={<TemperatureIcon />}
      label="Nozzle Temperature"
      description="Print temperature for this filament"
      value={settings.nozzleTemperature}
      onChange={(v) => onUpdate('nozzleTemperature', v)}
      min={170}
      max={300}
      step={5}
      unit="°C"
      disabled={disabled}
    />

    {/* Bed Temperature */}
    <SettingRow
      type="slider"
      icon={<TemperatureIcon />}
      label="Bed Temperature"
      value={settings.bedTemperature}
      onChange={(v) => onUpdate('bedTemperature', v)}
      min={0}
      max={120}
      step={5}
      unit="°C"
      disabled={disabled}
    />
  </div>
);

/** Advanced filament settings view */
const AdvancedFilamentSettingsPanel: React.FC<{
  settings: AdvancedFilamentSettings;
  onUpdate: <K extends keyof AdvancedFilamentSettings>(key: K, value: AdvancedFilamentSettings[K]) => void;
  disabled: boolean;
  activeCategory: FilamentSettingsCategory;
  materialOptions: Array<{ value: string; label: string }>;
  onMaterialChange: (material: string) => void;
}> = ({ settings, onUpdate, disabled, activeCategory, materialOptions, onMaterialChange }) => {
  switch (activeCategory) {
    case 'temperature':
      return (
        <div className="divide-y divide-pf-border">
          <SettingRow
            type="number"
            icon={<TemperatureIcon />}
            label="Nozzle Temperature"
            value={settings.nozzleTemperature}
            onChange={(v) => onUpdate('nozzleTemperature', v)}
            min={170}
            max={300}
            step={5}
            unit="°C"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<TemperatureIcon />}
            label="First Layer Nozzle Temp"
            value={settings.firstLayerNozzleTemperature ?? 215}
            onChange={(v) => onUpdate('firstLayerNozzleTemperature', v)}
            min={170}
            max={300}
            step={5}
            unit="°C"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<TemperatureIcon />}
            label="Bed Temperature"
            value={settings.bedTemperature}
            onChange={(v) => onUpdate('bedTemperature', v)}
            min={0}
            max={120}
            step={5}
            unit="°C"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<TemperatureIcon />}
            label="First Layer Bed Temp"
            value={settings.firstLayerBedTemperature ?? 65}
            onChange={(v) => onUpdate('firstLayerBedTemperature', v)}
            min={0}
            max={120}
            step={5}
            unit="°C"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<TemperatureIcon />}
            label="Chamber Temperature"
            description="Heated enclosure temperature (0 = disabled)"
            value={settings.chamberTemperature ?? 0}
            onChange={(v) => onUpdate('chamberTemperature', v)}
            min={0}
            max={80}
            step={5}
            unit="°C"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<SpeedIcon />}
            label="Max Volumetric Speed"
            description="Maximum extrusion rate for this filament"
            value={settings.maxVolumetricSpeed ?? 12}
            onChange={(v) => onUpdate('maxVolumetricSpeed', v)}
            min={1}
            max={30}
            step={0.5}
            unit="mm³/s"
            disabled={disabled}
          />
        </div>
      );

    case 'flow':
      return (
        <div className="divide-y divide-pf-border">
          <SettingRow
            type="number"
            icon={<FlowIcon />}
            label="Flow Ratio"
            description="Extrusion multiplier (1.0 = 100%)"
            value={settings.flowRatio ?? 1.0}
            onChange={(v) => onUpdate('flowRatio', v)}
            min={0.85}
            max={1.15}
            step={0.01}
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<SpeedIcon />}
            label="Print Speed Override"
            description="Speed override for this filament (0 = use process)"
            value={settings.printSpeed ?? 0}
            onChange={(v) => onUpdate('printSpeed', v)}
            min={0}
            max={300}
            step={5}
            unit="mm/s"
            disabled={disabled}
          />
          
          {/* Pressure Advance Section */}
          <SettingRow
            type="checkbox"
            icon={<PrecisionIcon />}
            label="Enable Pressure Advance"
            description="Compensate for filament pressure lag"
            checked={settings.enablePressureAdvance ?? false}
            onChange={(v) => onUpdate('enablePressureAdvance', v)}
            disabled={disabled}
          />
          {settings.enablePressureAdvance && (
            <>
              <SettingRow
                type="number"
                icon={<PrecisionIcon />}
                label="Pressure Advance"
                description="PA value (Klipper: 0.02-0.08 typical)"
                value={settings.pressureAdvance ?? 0.04}
                onChange={(v) => onUpdate('pressureAdvance', v)}
                min={0}
                max={2}
                step={0.005}
                disabled={disabled}
              />
              <SettingRow
                type="number"
                icon={<PrecisionIcon />}
                label="PA Smooth Time"
                description="Smoothing time for pressure advance"
                value={settings.pressureAdvanceSmoothTime ?? 0.04}
                onChange={(v) => onUpdate('pressureAdvanceSmoothTime', v)}
                min={0}
                max={0.2}
                step={0.005}
                unit="s"
                disabled={disabled}
              />
            </>
          )}

          {/* Volumetric Extrusion */}
          <SettingRow
            type="checkbox"
            icon={<FlowIcon />}
            label="Enable Volumetric Extrusion"
            checked={settings.enableVolumetricExtrusion ?? false}
            onChange={(v) => onUpdate('enableVolumetricExtrusion', v)}
            disabled={disabled}
          />
          {settings.enableVolumetricExtrusion && (
            <SettingRow
              type="number"
              icon={<FlowIcon />}
              label="Max Volumetric Rate"
              value={settings.maxVolumetricExtrusionRate ?? 12}
              onChange={(v) => onUpdate('maxVolumetricExtrusionRate', v)}
              min={1}
              max={30}
              step={0.5}
              unit="mm³/s"
              disabled={disabled}
            />
          )}
        </div>
      );

    case 'cooling':
      return (
        <div className="divide-y divide-pf-border">
          <SettingRow
            type="checkbox"
            icon={<CoolingIcon />}
            label="Enable Fan Cooling"
            checked={settings.enableFanCooling ?? true}
            onChange={(v) => onUpdate('enableFanCooling', v)}
            disabled={disabled}
          />
          {settings.enableFanCooling !== false && (
            <>
              <SettingRow
                type="slider"
                icon={<CoolingIcon />}
                label="Min Fan Speed"
                value={settings.minFanSpeed ?? 35}
                onChange={(v) => onUpdate('minFanSpeed', v)}
                min={0}
                max={100}
                step={5}
                unit="%"
                disabled={disabled}
              />
              <SettingRow
                type="slider"
                icon={<CoolingIcon />}
                label="Max Fan Speed"
                value={settings.maxFanSpeed ?? 100}
                onChange={(v) => onUpdate('maxFanSpeed', v)}
                min={0}
                max={100}
                step={5}
                unit="%"
                disabled={disabled}
              />
              <SettingRow
                type="slider"
                icon={<CoolingIcon />}
                label="Bridge Fan Speed"
                value={settings.bridgeFanSpeed ?? 100}
                onChange={(v) => onUpdate('bridgeFanSpeed', v)}
                min={0}
                max={100}
                step={5}
                unit="%"
                disabled={disabled}
              />
              <SettingRow
                type="number"
                icon={<CoolingIcon />}
                label="Full Fan Speed at Layer"
                description="Layer to reach full fan speed"
                value={settings.fullFanSpeedAtLayer ?? 3}
                onChange={(v) => onUpdate('fullFanSpeedAtLayer', v)}
                min={1}
                max={20}
                step={1}
                disabled={disabled}
              />
              <SettingRow
                type="number"
                icon={<CoolingIcon />}
                label="Slow Down for Layer Time"
                description="Slow down if layer prints faster"
                value={settings.slowDownForLayerTime ?? 5}
                onChange={(v) => onUpdate('slowDownForLayerTime', v)}
                min={1}
                max={60}
                step={1}
                unit="s"
                disabled={disabled}
              />
              <SettingRow
                type="number"
                icon={<SpeedIcon />}
                label="Min Print Speed"
                description="Minimum speed when slowing for cooling"
                value={settings.minPrintSpeed ?? 10}
                onChange={(v) => onUpdate('minPrintSpeed', v)}
                min={5}
                max={50}
                step={5}
                unit="mm/s"
                disabled={disabled}
              />
              <SettingRow
                type="slider"
                icon={<CoolingIcon />}
                label="Auxiliary Fan Speed"
                description="Aux/chamber fan (0 = off)"
                value={settings.auxFanSpeed ?? 0}
                onChange={(v) => onUpdate('auxFanSpeed', v)}
                min={0}
                max={100}
                step={5}
                unit="%"
                disabled={disabled}
              />
              <SettingRow
                type="slider"
                icon={<CoolingIcon />}
                label="Exhaust Fan Speed"
                description="Exhaust/enclosure fan"
                value={settings.exhaustFanSpeed ?? 0}
                onChange={(v) => onUpdate('exhaustFanSpeed', v)}
                min={0}
                max={100}
                step={5}
                unit="%"
                disabled={disabled}
              />
            </>
          )}
        </div>
      );

    case 'retraction':
      return (
        <div className="divide-y divide-pf-border">
          <SettingRow
            type="number"
            icon={<RetractionIcon />}
            label="Retraction Length"
            description="How much filament to retract"
            value={settings.retractionLength ?? 0.8}
            onChange={(v) => onUpdate('retractionLength', v)}
            min={0}
            max={10}
            step={0.1}
            unit="mm"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<RetractionIcon />}
            label="Retraction Speed"
            value={settings.retractionSpeed ?? 30}
            onChange={(v) => onUpdate('retractionSpeed', v)}
            min={5}
            max={120}
            step={5}
            unit="mm/s"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<RetractionIcon />}
            label="Deretraction Speed"
            description="Speed to push filament back"
            value={settings.detractionSpeed ?? 30}
            onChange={(v) => onUpdate('detractionSpeed', v)}
            min={5}
            max={120}
            step={5}
            unit="mm/s"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<RetractionIcon />}
            label="Retraction Z Lift"
            description="Z hop during retraction"
            value={settings.retractionLiftZ ?? 0.2}
            onChange={(v) => onUpdate('retractionLiftZ', v)}
            min={0}
            max={2}
            step={0.1}
            unit="mm"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<RetractionIcon />}
            label="Min Travel for Retraction"
            description="Minimum travel to trigger retraction"
            value={settings.retractionMinimumTravel ?? 1}
            onChange={(v) => onUpdate('retractionMinimumTravel', v)}
            min={0}
            max={10}
            step={0.5}
            unit="mm"
            disabled={disabled}
          />
          <SettingRow
            type="checkbox"
            icon={<RetractionIcon />}
            label="Retract on Layer Change"
            checked={settings.retractOnLayerChange ?? false}
            onChange={(v) => onUpdate('retractOnLayerChange', v)}
            disabled={disabled}
          />
          <SettingRow
            type="checkbox"
            icon={<RetractionIcon />}
            label="Wipe Before Retract"
            checked={settings.wipeBeforeRetract ?? false}
            onChange={(v) => onUpdate('wipeBeforeRetract', v)}
            disabled={disabled}
          />
        </div>
      );

    case 'other':
      return (
        <div className="divide-y divide-pf-border">
          <SettingRow
            type="select"
            icon={<FilamentIcon />}
            label="Material"
            value={settings.material}
            onChange={(v) => onMaterialChange(v)}
            options={materialOptions}
            disabled={disabled}
          />
          <SettingRow
            type="text"
            icon={<FilamentIcon />}
            label="Name"
            value={settings.name}
            onChange={(v) => onUpdate('name', v)}
            disabled={disabled}
          />
          <SettingRow
            type="color"
            icon={<ColorIcon />}
            label="Color"
            value={settings.color ?? '#3B82F6'}
            onChange={(v) => onUpdate('color', v)}
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<FilamentIcon />}
            label="Density"
            description="Material density for cost calculation"
            value={settings.density ?? 1.24}
            onChange={(v) => onUpdate('density', v)}
            min={0.5}
            max={3}
            step={0.01}
            unit="g/cm³"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<FilamentIcon />}
            label="Cost per kg"
            value={settings.cost ?? 20}
            onChange={(v) => onUpdate('cost', v)}
            min={0}
            max={500}
            step={1}
            unit="$"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<RetractionIcon />}
            label="Filament Load Time"
            description="Time to load filament"
            value={settings.filamentLoadTime ?? 0}
            onChange={(v) => onUpdate('filamentLoadTime', v)}
            min={0}
            max={60}
            step={1}
            unit="s"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<RetractionIcon />}
            label="Filament Unload Time"
            description="Time to unload filament"
            value={settings.filamentUnloadTime ?? 0}
            onChange={(v) => onUpdate('filamentUnloadTime', v)}
            min={0}
            max={60}
            step={1}
            unit="s"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<SpeedIcon />}
            label="Toolchange Delay"
            description="Extra delay after toolchange"
            value={settings.toolchangeDelay ?? 0}
            onChange={(v) => onUpdate('toolchangeDelay', v)}
            min={0}
            max={30}
            step={0.5}
            unit="s"
            disabled={disabled}
          />
        </div>
      );

    default:
      return null;
  }
};

export default FilamentProfileEditor;
