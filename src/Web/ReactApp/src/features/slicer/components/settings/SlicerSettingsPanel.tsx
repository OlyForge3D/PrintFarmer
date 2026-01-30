/**
 * OrcaSlicer-style Settings Panel
 * Implements Basic | Simple | Advanced view modes matching OrcaSlicer's UI
 */
import React, { useState, useCallback } from 'react';
import { Button } from '@/common/components/ui';
import { SettingRow } from './SettingRow';
import {
  InfillDensityIcon,
  InfillPatternIcon,
  WallCountIcon,
  BedAdhesionIcon,
  SupportsIcon,
  LayerHeightIcon,
  LineWidthIcon,
  SeamIcon,
  SpeedIcon,
  TemperatureIcon,
  PrecisionIcon,
} from './SlicerSettingIcons';
import {
  SettingsViewMode,
  SettingsCategory,
  BasicSlicerSettings,
  SimpleSlicerSettings,
  AdvancedSlicerSettings,
  INFILL_PATTERN_INFO,
  BED_ADHESION_INFO,
  InfillPattern,
  BedAdhesionType,
} from './slicerSettingsTypes';

interface SlicerSettingsPanelProps {
  /** Current settings values */
  settings: BasicSlicerSettings | SimpleSlicerSettings | AdvancedSlicerSettings;
  /** Called when any setting changes */
  onChange: (settings: BasicSlicerSettings | SimpleSlicerSettings | AdvancedSlicerSettings) => void;
  /** Initial view mode */
  initialViewMode?: SettingsViewMode;
  /** Disable all controls */
  disabled?: boolean;
  /** Custom class name */
  className?: string;
  /** Optional function to check if a category has modified settings */
  isCategoryDirty?: (category: SettingsCategory) => boolean;
}

/**
 * SlicerSettingsPanel - Full OrcaSlicer-style settings panel with Basic/Simple/Advanced modes
 */
export const SlicerSettingsPanel: React.FC<SlicerSettingsPanelProps> = ({
  settings,
  onChange,
  initialViewMode = 'basic',
  disabled = false,
  className = '',
  isCategoryDirty,
}) => {
  const [viewMode, setViewMode] = useState<SettingsViewMode>(initialViewMode);
  const [activeCategory, setActiveCategory] = useState<SettingsCategory>('quality');

  // Update a single setting
  const updateSetting = useCallback(<K extends keyof AdvancedSlicerSettings>(
    key: K,
    value: AdvancedSlicerSettings[K]
  ) => {
    onChange({ ...settings, [key]: value });
  }, [settings, onChange]);

  // View mode tabs
  const viewModes: { id: SettingsViewMode; label: string }[] = [
    { id: 'basic', label: 'Basic' },
    { id: 'simple', label: 'Simple' },
    { id: 'advanced', label: 'Advanced' },
  ];

  // Category tabs for advanced mode
  const categories: { id: SettingsCategory; label: string }[] = [
    { id: 'quality', label: 'Quality' },
    { id: 'strength', label: 'Strength' },
    { id: 'speed', label: 'Speed' },
    { id: 'support', label: 'Support' },
    { id: 'multimaterial', label: 'Multimaterial' },
    { id: 'other', label: 'Other' },
  ];

  // Infill pattern options
  const infillPatternOptions = Object.entries(INFILL_PATTERN_INFO).map(([value, info]) => ({
    value,
    label: info.label,
    icon: <InfillPatternIcon className="w-5 h-5" />,
  }));

  // Bed adhesion options
  const bedAdhesionOptions = Object.entries(BED_ADHESION_INFO).map(([value, info]) => ({
    value,
    label: info.label,
  }));

  return (
    <div className={`bg-pf-bg-1 rounded-lg ${className}`}>
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
          <BasicSettings
            settings={settings}
            onUpdate={updateSetting}
            disabled={disabled}
            infillPatternOptions={infillPatternOptions}
            bedAdhesionOptions={bedAdhesionOptions}
          />
        )}

        {viewMode === 'simple' && (
          <SimpleSettings
            settings={settings as SimpleSlicerSettings}
            onUpdate={updateSetting}
            disabled={disabled}
            infillPatternOptions={infillPatternOptions}
            bedAdhesionOptions={bedAdhesionOptions}
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

            <AdvancedSettings
              settings={settings as AdvancedSlicerSettings}
              onUpdate={updateSetting}
              disabled={disabled}
              activeCategory={activeCategory}
              infillPatternOptions={infillPatternOptions}
              bedAdhesionOptions={bedAdhesionOptions}
            />
          </>
        )}
      </div>
    </div>
  );
};

/** Basic settings view */
const BasicSettings: React.FC<{
  settings: BasicSlicerSettings;
  onUpdate: <K extends keyof AdvancedSlicerSettings>(key: K, value: AdvancedSlicerSettings[K]) => void;
  disabled: boolean;
  infillPatternOptions: Array<{ value: string; label: string; icon?: React.ReactNode }>;
  bedAdhesionOptions: Array<{ value: string; label: string }>;
}> = ({ settings, onUpdate, disabled, infillPatternOptions, bedAdhesionOptions }) => (
  <div className="divide-y divide-pf-border">
    {/* Infill Density */}
    <SettingRow
      type="slider"
      icon={<InfillDensityIcon />}
      label="Infill Density"
      description="How much material fills the inside of your print"
      tooltip="Higher density = stronger but uses more material and takes longer"
      value={settings.infillDensity}
      onChange={(v) => onUpdate('infillDensity', v)}
      min={0}
      max={100}
      step={5}
      unit="%"
      tickLabels={['0%', '20%', '40%', '60%', '80%', '100%']}
      disabled={disabled}
    />

    {/* Infill Pattern */}
    <SettingRow
      type="select"
      icon={<InfillPatternIcon />}
      label="Infill Pattern"
      description="The shape pattern used to fill your print"
      tooltip="Different patterns offer different strength and speed trade-offs"
      value={settings.infillPattern}
      onChange={(v) => onUpdate('infillPattern', v as InfillPattern)}
      options={infillPatternOptions}
      disabled={disabled}
    />

    {/* Wall Count */}
    <SettingRow
      type="slider"
      icon={<WallCountIcon />}
      label="Wall Count"
      description="The number of outer walls/shells for your print"
      tooltip="More walls = stronger outer surface but longer print time"
      value={settings.wallCount}
      onChange={(v) => onUpdate('wallCount', v)}
      min={1}
      max={6}
      step={1}
      tickLabels={['1', '2', '3', '4', '5', '6']}
      disabled={disabled}
    />

    {/* Bed Adhesion */}
    <SettingRow
      type="radio"
      icon={<BedAdhesionIcon />}
      label="Bed Adhesion"
      description="Choose between skirt or brim for better print adhesion"
      tooltip="Skirt primes the nozzle; Brim helps adhesion for warping-prone prints"
      value={settings.bedAdhesion}
      onChange={(v) => onUpdate('bedAdhesion', v as BedAdhesionType)}
      options={bedAdhesionOptions.filter(o => o.value !== 'raft')}
      disabled={disabled}
    />

    {/* Enable Supports */}
    <SettingRow
      type="checkbox"
      icon={<SupportsIcon />}
      label="Use Supports"
      description="Generate support structures for overhangs"
      tooltip="Enable when your model has overhangs greater than 45°"
      checked={settings.enableSupports}
      onChange={(v) => onUpdate('enableSupports', v)}
      disabled={disabled}
    />
  </div>
);

/** Simple settings view - adds layer height and line width */
const SimpleSettings: React.FC<{
  settings: SimpleSlicerSettings;
  onUpdate: <K extends keyof AdvancedSlicerSettings>(key: K, value: AdvancedSlicerSettings[K]) => void;
  disabled: boolean;
  infillPatternOptions: Array<{ value: string; label: string; icon?: React.ReactNode }>;
  bedAdhesionOptions: Array<{ value: string; label: string }>;
}> = ({ settings, onUpdate, disabled, infillPatternOptions, bedAdhesionOptions }) => (
  <div className="divide-y divide-pf-border">
    {/* Layer Height */}
    <SettingRow
      type="slider"
      icon={<LayerHeightIcon />}
      label="Layer Height"
      description="Height of each printed layer"
      tooltip="Lower = finer detail but longer print; Higher = faster but rougher"
      value={settings.layerHeight ?? 0.2}
      onChange={(v) => onUpdate('layerHeight', v)}
      min={0.08}
      max={0.32}
      step={0.04}
      unit="mm"
      tickLabels={['0.08', '0.12', '0.16', '0.20', '0.24', '0.28', '0.32']}
      disabled={disabled}
    />

    {/* First Layer Height */}
    <SettingRow
      type="number"
      icon={<LayerHeightIcon />}
      label="First Layer Height"
      description="Height of the first layer for better bed adhesion"
      value={settings.firstLayerHeight ?? 0.2}
      onChange={(v) => onUpdate('firstLayerHeight', v)}
      min={0.1}
      max={0.4}
      step={0.05}
      unit="mm"
      disabled={disabled}
    />

    {/* Line Width - Default */}
    <SettingRow
      type="number"
      icon={<LineWidthIcon />}
      label="Line Width"
      description="Default width of extruded lines"
      value={settings.lineWidthDefault ?? 0.45}
      onChange={(v) => onUpdate('lineWidthDefault', v)}
      min={0.2}
      max={0.8}
      step={0.05}
      unit="mm"
      disabled={disabled}
    />

    {/* Include basic settings */}
    <BasicSettings
      settings={settings}
      onUpdate={onUpdate}
      disabled={disabled}
      infillPatternOptions={infillPatternOptions}
      bedAdhesionOptions={bedAdhesionOptions}
    />

    {/* Top/Bottom Layers */}
    <SettingRow
      type="slider"
      icon={<WallCountIcon />}
      label="Top Layers"
      description="Number of solid layers on top surfaces"
      value={settings.topLayers ?? 4}
      onChange={(v) => onUpdate('topLayers', v)}
      min={1}
      max={10}
      step={1}
      disabled={disabled}
    />

    <SettingRow
      type="slider"
      icon={<WallCountIcon />}
      label="Bottom Layers"
      description="Number of solid layers on bottom surfaces"
      value={settings.bottomLayers ?? 3}
      onChange={(v) => onUpdate('bottomLayers', v)}
      min={1}
      max={10}
      step={1}
      disabled={disabled}
    />
  </div>
);

/** Advanced settings view - full control panel by category */
const AdvancedSettings: React.FC<{
  settings: AdvancedSlicerSettings;
  onUpdate: <K extends keyof AdvancedSlicerSettings>(key: K, value: AdvancedSlicerSettings[K]) => void;
  disabled: boolean;
  activeCategory: SettingsCategory;
  infillPatternOptions: Array<{ value: string; label: string; icon?: React.ReactNode }>;
  bedAdhesionOptions: Array<{ value: string; label: string }>;
}> = ({ settings, onUpdate, disabled, activeCategory, infillPatternOptions, bedAdhesionOptions }) => {
  // Render settings based on active category
  switch (activeCategory) {
    case 'quality':
      return (
        <div className="divide-y divide-pf-border">
          <SettingRow
            type="slider"
            icon={<LayerHeightIcon />}
            label="Layer Height"
            description="Height of each printed layer"
            value={settings.layerHeight ?? 0.2}
            onChange={(v) => onUpdate('layerHeight', v)}
            min={0.08}
            max={0.32}
            step={0.04}
            unit="mm"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<LayerHeightIcon />}
            label="First Layer Height"
            value={settings.firstLayerHeight ?? 0.2}
            onChange={(v) => onUpdate('firstLayerHeight', v)}
            min={0.1}
            max={0.4}
            step={0.05}
            unit="mm"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<LineWidthIcon />}
            label="Line Width (Default)"
            value={settings.lineWidthDefault ?? 0.45}
            onChange={(v) => onUpdate('lineWidthDefault', v)}
            min={0.2}
            max={0.8}
            step={0.05}
            unit="mm"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<LineWidthIcon />}
            label="First Layer Line Width"
            value={settings.lineWidthFirstLayer ?? 0.5}
            onChange={(v) => onUpdate('lineWidthFirstLayer', v)}
            min={0.2}
            max={0.8}
            step={0.05}
            unit="mm"
            disabled={disabled}
          />
          <SettingRow
            type="select"
            icon={<SeamIcon />}
            label="Seam Position"
            value={settings.seamPosition ?? 'aligned'}
            onChange={(v) => onUpdate('seamPosition', v as 'random' | 'aligned' | 'back' | 'nearest')}
            options={[
              { value: 'aligned', label: 'Aligned' },
              { value: 'back', label: 'Back' },
              { value: 'nearest', label: 'Nearest' },
              { value: 'random', label: 'Random' },
            ]}
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<PrecisionIcon />}
            label="Resolution"
            value={settings.resolution ?? 0.0125}
            onChange={(v) => onUpdate('resolution', v)}
            min={0.001}
            max={0.1}
            step={0.001}
            unit="mm"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<PrecisionIcon />}
            label="Elephant Foot Compensation"
            description="Compensate for first layer squish"
            value={settings.elephantFootCompensation ?? 0.1}
            onChange={(v) => onUpdate('elephantFootCompensation', v)}
            min={0}
            max={1}
            step={0.05}
            unit="mm"
            disabled={disabled}
          />
        </div>
      );

    case 'strength':
      return (
        <div className="divide-y divide-pf-border">
          <SettingRow
            type="slider"
            icon={<InfillDensityIcon />}
            label="Infill Density"
            value={settings.infillDensity}
            onChange={(v) => onUpdate('infillDensity', v)}
            min={0}
            max={100}
            step={5}
            unit="%"
            disabled={disabled}
          />
          <SettingRow
            type="select"
            icon={<InfillPatternIcon />}
            label="Infill Pattern"
            value={settings.infillPattern}
            onChange={(v) => onUpdate('infillPattern', v as InfillPattern)}
            options={infillPatternOptions}
            disabled={disabled}
          />
          <SettingRow
            type="slider"
            icon={<WallCountIcon />}
            label="Wall Count"
            value={settings.wallCount}
            onChange={(v) => onUpdate('wallCount', v)}
            min={1}
            max={10}
            step={1}
            disabled={disabled}
          />
          <SettingRow
            type="slider"
            icon={<WallCountIcon />}
            label="Top Layers"
            value={settings.topLayers ?? 4}
            onChange={(v) => onUpdate('topLayers', v)}
            min={1}
            max={10}
            step={1}
            disabled={disabled}
          />
          <SettingRow
            type="slider"
            icon={<WallCountIcon />}
            label="Bottom Layers"
            value={settings.bottomLayers ?? 3}
            onChange={(v) => onUpdate('bottomLayers', v)}
            min={1}
            max={10}
            step={1}
            disabled={disabled}
          />
        </div>
      );

    case 'speed':
      return (
        <div className="divide-y divide-pf-border">
          <SettingRow
            type="slider"
            icon={<SpeedIcon />}
            label="Print Speed"
            value={settings.printSpeed ?? 120}
            onChange={(v) => onUpdate('printSpeed', v)}
            min={20}
            max={300}
            step={10}
            unit="mm/s"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<SpeedIcon />}
            label="Outer Wall Speed"
            value={settings.outerWallSpeed ?? 100}
            onChange={(v) => onUpdate('outerWallSpeed', v)}
            min={10}
            max={200}
            step={5}
            unit="mm/s"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<SpeedIcon />}
            label="Inner Wall Speed"
            value={settings.innerWallSpeed ?? 150}
            onChange={(v) => onUpdate('innerWallSpeed', v)}
            min={10}
            max={300}
            step={5}
            unit="mm/s"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<SpeedIcon />}
            label="Infill Speed"
            value={settings.infillSpeed ?? 150}
            onChange={(v) => onUpdate('infillSpeed', v)}
            min={10}
            max={300}
            step={5}
            unit="mm/s"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<SpeedIcon />}
            label="Travel Speed"
            value={settings.travelSpeed ?? 150}
            onChange={(v) => onUpdate('travelSpeed', v)}
            min={50}
            max={500}
            step={10}
            unit="mm/s"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<SpeedIcon />}
            label="First Layer Speed"
            value={settings.firstLayerSpeed ?? 20}
            onChange={(v) => onUpdate('firstLayerSpeed', v)}
            min={5}
            max={60}
            step={5}
            unit="mm/s"
            disabled={disabled}
          />
        </div>
      );

    case 'support':
      return (
        <div className="divide-y divide-pf-border">
          <SettingRow
            type="checkbox"
            icon={<SupportsIcon />}
            label="Enable Supports"
            checked={settings.enableSupports}
            onChange={(v) => onUpdate('enableSupports', v)}
            disabled={disabled}
          />
          {settings.enableSupports && (
            <>
              <SettingRow
                type="select"
                icon={<SupportsIcon />}
                label="Support Type"
                value={settings.supportType ?? 'normal'}
                onChange={(v) => onUpdate('supportType', v as 'none' | 'normal' | 'tree' | 'tree_auto')}
                options={[
                  { value: 'normal', label: 'Normal' },
                  { value: 'tree', label: 'Tree' },
                  { value: 'tree_auto', label: 'Tree (Auto)' },
                ]}
                disabled={disabled}
              />
              <SettingRow
                type="slider"
                icon={<SupportsIcon />}
                label="Support Density"
                value={settings.supportDensity ?? 15}
                onChange={(v) => onUpdate('supportDensity', v)}
                min={5}
                max={50}
                step={5}
                unit="%"
                disabled={disabled}
              />
              <SettingRow
                type="slider"
                icon={<SupportsIcon />}
                label="Support Angle"
                description="Minimum overhang angle to support"
                value={settings.supportAngle ?? 45}
                onChange={(v) => onUpdate('supportAngle', v)}
                min={0}
                max={90}
                step={5}
                unit="°"
                disabled={disabled}
              />
            </>
          )}
          <SettingRow
            type="radio"
            icon={<BedAdhesionIcon />}
            label="Bed Adhesion"
            value={settings.bedAdhesion}
            onChange={(v) => onUpdate('bedAdhesion', v as BedAdhesionType)}
            options={bedAdhesionOptions}
            disabled={disabled}
          />
        </div>
      );

    case 'multimaterial':
      return (
        <div className="py-8 text-center text-pf-text-muted">
          <p>Multimaterial settings coming soon</p>
        </div>
      );

    case 'other':
      return (
        <div className="divide-y divide-pf-border">
          <SettingRow
            type="number"
            icon={<TemperatureIcon />}
            label="Nozzle Temperature"
            value={settings.nozzleTemp ?? 210}
            onChange={(v) => onUpdate('nozzleTemp', v)}
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
            value={settings.bedTemp ?? 60}
            onChange={(v) => onUpdate('bedTemp', v)}
            min={0}
            max={120}
            step={5}
            unit="°C"
            disabled={disabled}
          />
          <SettingRow
            type="checkbox"
            icon={<PrecisionIcon />}
            label="Arc Fitting"
            description="Convert G-code segments to arcs"
            checked={settings.arcFitting ?? false}
            onChange={(v) => onUpdate('arcFitting', v)}
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<PrecisionIcon />}
            label="X-Y Hole Compensation"
            value={settings.xyHoleCompensation ?? 0}
            onChange={(v) => onUpdate('xyHoleCompensation', v)}
            min={-1}
            max={1}
            step={0.05}
            unit="mm"
            disabled={disabled}
          />
          <SettingRow
            type="number"
            icon={<PrecisionIcon />}
            label="X-Y Contour Compensation"
            value={settings.xyContourCompensation ?? 0}
            onChange={(v) => onUpdate('xyContourCompensation', v)}
            min={-1}
            max={1}
            step={0.05}
            unit="mm"
            disabled={disabled}
          />
        </div>
      );

    default:
      return null;
  }
};

export default SlicerSettingsPanel;
