/**
 * OrcaSlicer-style Settings Panel
 *
 * Category tabs (Quality, Strength, Speed, Support, Multimaterial, Other)
 * are the primary navigation. A Simple/Advanced toggle controls how many
 * settings appear within each tab.
 */
import React, { useState, useCallback } from 'react';
import { Button } from '@/common/components/ui';
import { CompactSettingRow, SettingSection } from './SettingRow';
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
  RetractionIcon,
  CoolingIcon,
  IroningIcon,
  AccelerationIcon,
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
  /** Initial view mode — 'basic' and 'simple' map to Simple; 'advanced' maps to Advanced */
  initialViewMode?: SettingsViewMode;
  /** Disable all controls */
  disabled?: boolean;
  /** Custom class name */
  className?: string;
  /** Optional function to check if a category has modified settings */
  isCategoryDirty?: (category: SettingsCategory) => boolean;
  /** Raw Orca settings not explicitly modeled in typed controls */
  advancedSettings?: Record<string, unknown>;
  /** Called when dynamic advanced settings change */
  onAdvancedSettingsChange?: (settings: Record<string, unknown>) => void;
}

const CATEGORIES: { id: SettingsCategory; label: string }[] = [
  { id: 'quality', label: 'Quality' },
  { id: 'strength', label: 'Strength' },
  { id: 'speed', label: 'Speed' },
  { id: 'support', label: 'Support' },
  { id: 'multimaterial', label: 'Multimaterial' },
  { id: 'other', label: 'Other' },
];

/**
 * SlicerSettingsPanel — OrcaSlicer-style category-first settings panel.
 *
 * Category tabs are always visible. A small Simple/Advanced toggle controls
 * how many settings each tab renders.
 */
export const SlicerSettingsPanel: React.FC<SlicerSettingsPanelProps> = ({
  settings,
  onChange,
  initialViewMode = 'basic',
  disabled = false,
  className = '',
  isCategoryDirty,
  advancedSettings,
  onAdvancedSettingsChange,
}) => {
  const [isAdvanced, setIsAdvanced] = useState(initialViewMode === 'advanced');
  const [activeCategory, setActiveCategory] = useState<SettingsCategory>('quality');

  const updateSetting = useCallback(<K extends keyof AdvancedSlicerSettings>(
    key: K,
    value: AdvancedSlicerSettings[K]
  ) => {
    onChange({ ...settings, [key]: value });
  }, [settings, onChange]);

  const infillPatternOptions = Object.entries(INFILL_PATTERN_INFO).map(([value, info]) => ({
    value,
    label: info.label,
    icon: <InfillPatternIcon className="w-5 h-5" />,
  }));

  const bedAdhesionOptions = Object.entries(BED_ADHESION_INFO).map(([value, info]) => ({
    value,
    label: info.label,
  }));

  const categoryProps = {
    settings: settings as AdvancedSlicerSettings,
    onUpdate: updateSetting,
    disabled,
    isAdvanced,
    infillPatternOptions,
    bedAdhesionOptions,
    advancedSettings,
    onAdvancedSettingsChange,
  };

  return (
    <div className={`bg-pf-bg-1 rounded-lg ${className}`}>
      {/* Category Tabs — primary navigation */}
      <div className="flex gap-1 p-2 border-b border-pf-border overflow-x-auto" role="tablist" aria-label="Settings categories">
        {CATEGORIES.map((cat) => {
          const isDirty = isCategoryDirty?.(cat.id) ?? false;
          const isActive = activeCategory === cat.id;
          return (
            <Button
              key={cat.id}
              variant="unstyled"
              type="button"
              role="tab"
              aria-selected={isActive}
              aria-controls={`panel-${cat.id}`}
              onClick={() => setActiveCategory(cat.id)}
              disabled={disabled}
              className={`px-3 py-1.5 text-xs font-medium rounded-full whitespace-nowrap relative
                         transition-colors duration-150 cursor-pointer disabled:opacity-50
                         ${isActive
                           ? 'bg-pf-accent text-white'
                           : 'text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary'
                         }
                         ${isDirty ? 'ring-1 ring-pf-accent-orange' : ''}`}
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

      {/* Simple / Advanced density toggle */}
      <div className="flex items-center justify-end gap-1 px-3 py-1.5 border-b border-pf-border-light">
        <span className="text-[10px] text-pf-text-muted mr-1">Show:</span>
        <Button
          variant="unstyled"
          size="sm"
          onClick={() => setIsAdvanced(false)}
          disabled={disabled}
          className={`px-2 py-0.5 text-[10px] font-medium rounded-l-md border transition-colors
                     ${!isAdvanced
                       ? 'bg-pf-accent text-white border-pf-accent'
                       : 'bg-pf-bg-2 text-pf-text-secondary border-pf-border hover:text-pf-text-primary'
                     } disabled:opacity-50`}
        >
          Simple
        </Button>
        <Button
          variant="unstyled"
          size="sm"
          onClick={() => setIsAdvanced(true)}
          disabled={disabled}
          className={`px-2 py-0.5 text-[10px] font-medium rounded-r-md border -ml-px transition-colors
                     ${isAdvanced
                       ? 'bg-pf-accent text-white border-pf-accent'
                       : 'bg-pf-bg-2 text-pf-text-secondary border-pf-border hover:text-pf-text-primary'
                     } disabled:opacity-50`}
        >
          Advanced
        </Button>
      </div>

      {/* Category content */}
      <div className="p-3" id={`panel-${activeCategory}`} role="tabpanel">
        {activeCategory === 'quality' && <QualitySettings {...categoryProps} />}
        {activeCategory === 'strength' && <StrengthSettings {...categoryProps} />}
        {activeCategory === 'speed' && <SpeedSettings {...categoryProps} />}
        {activeCategory === 'support' && <SupportSettings {...categoryProps} />}
        {activeCategory === 'multimaterial' && <MultimaterialSettings {...categoryProps} />}
        {activeCategory === 'other' && <OtherSettings {...categoryProps} />}
      </div>
    </div>
  );
};

/* ─── shared prop shape for every category ─── */
interface CategorySettingsProps {
  settings: AdvancedSlicerSettings;
  onUpdate: <K extends keyof AdvancedSlicerSettings>(key: K, value: AdvancedSlicerSettings[K]) => void;
  disabled: boolean;
  isAdvanced: boolean;
  infillPatternOptions: Array<{ value: string; label: string; icon?: React.ReactNode }>;
  bedAdhesionOptions: Array<{ value: string; label: string }>;
  advancedSettings?: Record<string, unknown>;
  onAdvancedSettingsChange?: (settings: Record<string, unknown>) => void;
}

/* ─── Quality ─── */
const QualitySettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled, isAdvanced }) => (
  <div className="space-y-4">
    <SettingSection icon={<LayerHeightIcon className="w-4 h-4" />} title="Layer height">
      <CompactSettingRow type="number" label="Layer height" value={settings.layerHeight ?? 0.2} onChange={(v) => onUpdate('layerHeight', v)} min={0.04} max={0.4} step={0.01} unit="mm" disabled={disabled} />
      <CompactSettingRow type="number" label="First layer height" value={settings.firstLayerHeight ?? 0.2} onChange={(v) => onUpdate('firstLayerHeight', v)} min={0.1} max={0.4} step={0.01} unit="mm" disabled={disabled} />
    </SettingSection>

    <SettingSection icon={<LineWidthIcon className="w-4 h-4" />} title="Line width">
      <CompactSettingRow type="number" label="Default" value={settings.lineWidthDefault ?? 0.45} onChange={(v) => onUpdate('lineWidthDefault', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
      {isAdvanced && (
        <>
          <CompactSettingRow type="number" label="First layer" value={settings.lineWidthFirstLayer ?? 0.5} onChange={(v) => onUpdate('lineWidthFirstLayer', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
          <CompactSettingRow type="number" label="Outer wall" value={settings.lineWidthOuterWall ?? 0.45} onChange={(v) => onUpdate('lineWidthOuterWall', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
          <CompactSettingRow type="number" label="Inner wall" value={settings.lineWidthInnerWall ?? 0.45} onChange={(v) => onUpdate('lineWidthInnerWall', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
          <CompactSettingRow type="number" label="Top surface" value={settings.lineWidthTopSurface ?? 0.45} onChange={(v) => onUpdate('lineWidthTopSurface', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
          <CompactSettingRow type="number" label="Sparse infill" value={settings.lineWidthSparseInfill ?? 0.45} onChange={(v) => onUpdate('lineWidthSparseInfill', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
          <CompactSettingRow type="number" label="Internal solid infill" value={settings.lineWidthInternalSolidInfill ?? 0.45} onChange={(v) => onUpdate('lineWidthInternalSolidInfill', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
          <CompactSettingRow type="number" label="Support" value={settings.lineWidthSupport ?? 0.45} onChange={(v) => onUpdate('lineWidthSupport', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
        </>
      )}
    </SettingSection>

    {isAdvanced && (
      <>
        <SettingSection icon={<SeamIcon className="w-4 h-4" />} title="Seam">
          <CompactSettingRow type="select" label="Seam position" value={settings.seamPosition ?? 'aligned'} onChange={(v) => onUpdate('seamPosition', v as 'random' | 'aligned' | 'back' | 'nearest')} options={[{ value: 'aligned', label: 'Aligned' }, { value: 'back', label: 'Back' }, { value: 'nearest', label: 'Nearest' }, { value: 'random', label: 'Random' }]} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Staggered inner seams" checked={settings.staggeredInnerSeams ?? false} onChange={(v) => onUpdate('staggeredInnerSeams', v)} disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<PrecisionIcon className="w-4 h-4" />} title="Precision">
          <CompactSettingRow type="number" label="Resolution" value={settings.resolution ?? 0.0125} onChange={(v) => onUpdate('resolution', v)} min={0.001} max={0.1} step={0.001} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Elephant foot compensation" value={settings.elephantFootCompensation ?? 0.1} onChange={(v) => onUpdate('elephantFootCompensation', v)} min={0} max={1} step={0.01} unit="mm" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<WallCountIcon className="w-4 h-4" />} title="Wall generator">
          <CompactSettingRow type="number" label="Min wall thickness" value={settings.minWallThickness ?? 0.8} onChange={(v) => onUpdate('minWallThickness', v)} min={0.4} max={2} step={0.1} unit="mm" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<WallCountIcon className="w-4 h-4" />} title="Walls & surfaces">
          <CompactSettingRow type="checkbox" label="Precise outer wall" checked={settings.preciseWall ?? false} onChange={(v) => onUpdate('preciseWall', v)} disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<InfillDensityIcon className="w-4 h-4" />} title="Flow ratio">
          <CompactSettingRow type="number" label="Outer wall flow ratio" value={settings.outerWallFlowRatio ?? 100} onChange={(v) => onUpdate('outerWallFlowRatio', v)} min={50} max={150} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="number" label="Inner wall flow ratio" value={settings.innerWallFlowRatio ?? 100} onChange={(v) => onUpdate('innerWallFlowRatio', v)} min={50} max={150} step={5} unit="%" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Bridging">
          <CompactSettingRow type="number" label="Max bridge length" value={settings.maxBridgeLength ?? 10} onChange={(v) => onUpdate('maxBridgeLength', v)} min={5} max={50} step={1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Bridge speed reduction" value={settings.bridgeSpeedReduction ?? 50} onChange={(v) => onUpdate('bridgeSpeedReduction', v)} min={10} max={90} step={5} unit="%" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<SupportsIcon className="w-4 h-4" />} title="Overhangs">
          <CompactSettingRow type="number" label="Overhang angle threshold" value={settings.overhangAngle ?? 45} onChange={(v) => onUpdate('overhangAngle', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="Overhang perimeter speed" value={settings.overhangPerimeterSpeed ?? 50} onChange={(v) => onUpdate('overhangPerimeterSpeed', v)} min={10} max={100} step={5} unit="%" disabled={disabled} />
        </SettingSection>
      </>
    )}
  </div>
);

/* ─── Strength ─── */
const StrengthSettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled, isAdvanced, infillPatternOptions }) => (
  <div className="space-y-4">
    <SettingSection icon={<InfillDensityIcon className="w-4 h-4" />} title="Infill">
      <CompactSettingRow type="number" label="Sparse infill density" value={settings.infillDensity} onChange={(v) => onUpdate('infillDensity', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
      <CompactSettingRow type="select" label="Sparse infill pattern" value={settings.infillPattern} onChange={(v) => onUpdate('infillPattern', v as InfillPattern)} options={infillPatternOptions} disabled={disabled} />
      {isAdvanced && (
        <>
          <CompactSettingRow type="number" label="Infill/wall overlap" value={settings.infillOverlap ?? 25} onChange={(v) => onUpdate('infillOverlap', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="number" label="Infill anchor max length" value={settings.infillAnchorMaxLength ?? 10} onChange={(v) => onUpdate('infillAnchorMaxLength', v)} min={0} max={50} step={1} unit="mm" disabled={disabled} />
        </>
      )}
    </SettingSection>

    <SettingSection icon={<WallCountIcon className="w-4 h-4" />} title="Walls">
      <CompactSettingRow type="number" label="Wall loops" value={settings.wallCount} onChange={(v) => onUpdate('wallCount', v)} min={1} max={10} step={1} disabled={disabled} />
      <CompactSettingRow type="number" label="Top shell layers" value={settings.topLayers ?? 4} onChange={(v) => onUpdate('topLayers', v)} min={1} max={10} step={1} disabled={disabled} />
      <CompactSettingRow type="number" label="Bottom shell layers" value={settings.bottomLayers ?? 3} onChange={(v) => onUpdate('bottomLayers', v)} min={1} max={10} step={1} disabled={disabled} />
    </SettingSection>
  </div>
);

/* ─── Speed ─── */
const SpeedSettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled, isAdvanced }) => (
  <div className="space-y-4">
    <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Speed">
      <CompactSettingRow type="number" label="Outer wall" value={settings.outerWallSpeed ?? 100} onChange={(v) => onUpdate('outerWallSpeed', v)} min={10} max={200} step={5} unit="mm/s" disabled={disabled} />
      <CompactSettingRow type="number" label="Inner wall" value={settings.innerWallSpeed ?? 150} onChange={(v) => onUpdate('innerWallSpeed', v)} min={10} max={300} step={5} unit="mm/s" disabled={disabled} />
      {isAdvanced && (
        <>
          <CompactSettingRow type="number" label="Sparse infill" value={settings.sparseInfillSpeed ?? 150} onChange={(v) => onUpdate('sparseInfillSpeed', v)} min={10} max={300} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Internal solid infill" value={settings.solidInfillSpeed ?? 120} onChange={(v) => onUpdate('solidInfillSpeed', v)} min={10} max={300} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Top surface" value={settings.topSurfaceSpeed ?? 100} onChange={(v) => onUpdate('topSurfaceSpeed', v)} min={10} max={200} step={5} unit="mm/s" disabled={disabled} />
        </>
      )}
      <CompactSettingRow type="number" label="Travel" value={settings.travelSpeed ?? 150} onChange={(v) => onUpdate('travelSpeed', v)} min={50} max={500} step={10} unit="mm/s" disabled={disabled} />
      <CompactSettingRow type="number" label="First layer" value={settings.firstLayerSpeed ?? 20} onChange={(v) => onUpdate('firstLayerSpeed', v)} min={5} max={60} step={5} unit="mm/s" disabled={disabled} />
    </SettingSection>

    {isAdvanced && (
      <SettingSection icon={<AccelerationIcon className="w-4 h-4" />} title="Acceleration">
        <CompactSettingRow type="number" label="Normal printing" value={settings.defaultAcceleration ?? 5000} onChange={(v) => onUpdate('defaultAcceleration', v)} min={100} max={20000} step={100} unit="mm/s²" disabled={disabled} />
        <CompactSettingRow type="number" label="Outer wall" value={settings.outerWallAcceleration ?? 2000} onChange={(v) => onUpdate('outerWallAcceleration', v)} min={100} max={10000} step={100} unit="mm/s²" disabled={disabled} />
        <CompactSettingRow type="number" label="Inner wall" value={settings.innerWallAcceleration ?? 5000} onChange={(v) => onUpdate('innerWallAcceleration', v)} min={100} max={20000} step={100} unit="mm/s²" disabled={disabled} />
        <CompactSettingRow type="number" label="Sparse infill" value={settings.infillAcceleration ?? 5000} onChange={(v) => onUpdate('infillAcceleration', v)} min={100} max={20000} step={100} unit="mm/s²" disabled={disabled} />
        <CompactSettingRow type="number" label="Travel" value={settings.travelAcceleration ?? 10000} onChange={(v) => onUpdate('travelAcceleration', v)} min={100} max={30000} step={100} unit="mm/s²" disabled={disabled} />
      </SettingSection>
    )}
  </div>
);

/* ─── Support ─── */
const SupportSettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled, isAdvanced, bedAdhesionOptions }) => (
  <div className="space-y-4">
    <SettingSection icon={<SupportsIcon className="w-4 h-4" />} title="Support">
      <CompactSettingRow type="checkbox" label="Enable support" checked={settings.enableSupports} onChange={(v) => onUpdate('enableSupports', v)} disabled={disabled} />
      {settings.enableSupports && (
        <>
          <CompactSettingRow type="select" label="Type" value={settings.supportType ?? 'normal'} onChange={(v) => onUpdate('supportType', v as 'none' | 'normal' | 'tree' | 'tree_auto')} options={[{ value: 'normal', label: 'Normal' }, { value: 'tree', label: 'Tree' }, { value: 'tree_auto', label: 'Tree (Auto)' }]} disabled={disabled} />
          <CompactSettingRow type="number" label="Threshold angle" value={settings.supportAngle ?? 45} onChange={(v) => onUpdate('supportAngle', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
          {isAdvanced && (
            <>
              <CompactSettingRow type="number" label="Top Z distance" value={settings.supportTopZDistance ?? 0.2} onChange={(v) => onUpdate('supportTopZDistance', v)} min={0} max={1} step={0.05} unit="mm" disabled={disabled} />
              <CompactSettingRow type="number" label="Bottom Z distance" value={settings.supportBottomZDistance ?? 0.2} onChange={(v) => onUpdate('supportBottomZDistance', v)} min={0} max={1} step={0.05} unit="mm" disabled={disabled} />
              <CompactSettingRow type="number" label="X/Y distance" value={settings.supportXYDistance ?? 0.6} onChange={(v) => onUpdate('supportXYDistance', v)} min={0} max={2} step={0.1} unit="mm" disabled={disabled} />
              <CompactSettingRow type="number" label="Interface layers" value={settings.supportInterfaceLayers ?? 2} onChange={(v) => onUpdate('supportInterfaceLayers', v)} min={0} max={10} step={1} disabled={disabled} />
            </>
          )}
        </>
      )}
    </SettingSection>

    <SettingSection icon={<BedAdhesionIcon className="w-4 h-4" />} title="Bed adhesion">
      <CompactSettingRow type="select" label="Brim type" value={settings.bedAdhesion} onChange={(v) => onUpdate('bedAdhesion', v as BedAdhesionType)} options={bedAdhesionOptions} disabled={disabled} />
    </SettingSection>
  </div>
);

/* ─── Multimaterial ─── */
const MultimaterialSettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled }) => (
  <div className="space-y-4">
    <SettingSection icon={<LineWidthIcon className="w-4 h-4" />} title="Filament lanes">
      <div className="space-y-3">
        <p className="text-xs text-pf-text-muted px-3 py-1">
          Select filament profiles for each extruder. Use None for unused extruders.
        </p>
        <CompactSettingRow type="select" label="Extruder 1 (Primary)" value={settings.filament1ProfileId ?? ''} onChange={(v) => onUpdate('filament1ProfileId', v || undefined)} options={[{ value: '', label: 'Select filament profile...' }, { value: 'pla-standard', label: 'PLA - Standard' }, { value: 'petg-standard', label: 'PETG - Durable' }, { value: 'tpu-flexible', label: 'TPU - Flexible' }, { value: 'abs-engineering', label: 'ABS - Engineering' }, { value: 'nylon-tough', label: 'Nylon - Tough' }, { value: 'cf-carbon', label: 'Carbon Fiber Reinforced' }]} disabled={disabled} />
        <CompactSettingRow type="select" label="Extruder 2" value={settings.filament2ProfileId ?? ''} onChange={(v) => onUpdate('filament2ProfileId', v || undefined)} options={[{ value: '', label: 'None (not used)' }, { value: 'pla-standard', label: 'PLA - Standard' }, { value: 'petg-standard', label: 'PETG - Durable' }, { value: 'tpu-flexible', label: 'TPU - Flexible' }, { value: 'abs-engineering', label: 'ABS - Engineering' }, { value: 'nylon-tough', label: 'Nylon - Tough' }, { value: 'cf-carbon', label: 'Carbon Fiber Reinforced' }]} disabled={disabled} />
        <CompactSettingRow type="select" label="Extruder 3" value={settings.filament3ProfileId ?? ''} onChange={(v) => onUpdate('filament3ProfileId', v || undefined)} options={[{ value: '', label: 'None (not used)' }, { value: 'pla-standard', label: 'PLA - Standard' }, { value: 'petg-standard', label: 'PETG - Durable' }, { value: 'tpu-flexible', label: 'TPU - Flexible' }, { value: 'abs-engineering', label: 'ABS - Engineering' }, { value: 'nylon-tough', label: 'Nylon - Tough' }, { value: 'cf-carbon', label: 'Carbon Fiber Reinforced' }]} disabled={disabled} />
      </div>
    </SettingSection>

    <SettingSection icon={<TemperatureIcon className="w-4 h-4" />} title="Purge & wipe tower">
      <CompactSettingRow type="checkbox" label="Purge on layer change" checked={settings.purgeOnLayerChange ?? true} onChange={(v) => onUpdate('purgeOnLayerChange', v)} disabled={disabled} />
      {settings.purgeOnLayerChange && (
        <>
          <CompactSettingRow type="number" label="Purge tower volume" value={settings.purgeTowerVolume ?? 50} onChange={(v) => onUpdate('purgeTowerVolume', v)} min={10} max={500} step={10} unit="mm³" disabled={disabled} />
          <CompactSettingRow type="number" label="Wipe tower width" value={settings.wipeTowerWidth ?? 30} onChange={(v) => onUpdate('wipeTowerWidth', v)} min={10} max={100} step={5} unit="mm" disabled={disabled} />
        </>
      )}
    </SettingSection>
  </div>
);

/* ─── Other ─── */
const OtherSettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled, isAdvanced, advancedSettings, onAdvancedSettingsChange }) => (
  <div className="space-y-1">
    <SettingSection icon={<TemperatureIcon />} title="Temperature">
      <CompactSettingRow type="number" label="Nozzle temperature" value={settings.nozzleTemp ?? 210} onChange={(v) => onUpdate('nozzleTemp', v as number)} min={170} max={300} step={5} unit="°C" disabled={disabled} />
      <CompactSettingRow type="number" label="Bed temperature" value={settings.bedTemp ?? 60} onChange={(v) => onUpdate('bedTemp', v as number)} min={0} max={120} step={5} unit="°C" disabled={disabled} />
      {isAdvanced && (
        <>
          <CompactSettingRow type="number" label="First layer nozzle temp" value={settings.firstLayerNozzleTemp ?? 215} onChange={(v) => onUpdate('firstLayerNozzleTemp', v as number)} min={170} max={300} step={5} unit="°C" disabled={disabled} />
          <CompactSettingRow type="number" label="First layer bed temp" value={settings.firstLayerBedTemp ?? 65} onChange={(v) => onUpdate('firstLayerBedTemp', v as number)} min={0} max={120} step={5} unit="°C" disabled={disabled} />
        </>
      )}
    </SettingSection>

    <SettingSection icon={<RetractionIcon />} title="Retraction">
      <CompactSettingRow type="number" label="Retraction length" value={settings.retractionLength ?? 0.8} onChange={(v) => onUpdate('retractionLength', v as number)} min={0} max={10} step={0.1} unit="mm" disabled={disabled} />
      <CompactSettingRow type="number" label="Retraction speed" value={settings.retractionSpeed ?? 30} onChange={(v) => onUpdate('retractionSpeed', v as number)} min={5} max={120} step={5} unit="mm/s" disabled={disabled} />
      {isAdvanced && (
        <>
          <CompactSettingRow type="number" label="Deretraction speed" value={settings.detractionSpeed ?? 30} onChange={(v) => onUpdate('detractionSpeed', v as number)} min={5} max={120} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Z lift" value={settings.retractionLiftZ ?? 0.2} onChange={(v) => onUpdate('retractionLiftZ', v as number)} min={0} max={2} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Minimum travel" value={settings.retractionMinimumTravel ?? 1} onChange={(v) => onUpdate('retractionMinimumTravel', v as number)} min={0} max={10} step={0.5} unit="mm" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Retract on layer change" checked={settings.retractOnLayerChange ?? false} onChange={(v) => onUpdate('retractOnLayerChange', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Wipe before retract" checked={settings.wipeBeforeRetract ?? false} onChange={(v) => onUpdate('wipeBeforeRetract', v)} disabled={disabled} />
        </>
      )}
    </SettingSection>

    <SettingSection icon={<CoolingIcon />} title="Cooling">
      <CompactSettingRow type="checkbox" label="Enable fan cooling" checked={settings.enableFanCooling ?? true} onChange={(v) => onUpdate('enableFanCooling', v)} disabled={disabled} />
      {settings.enableFanCooling !== false && (
        <>
          <CompactSettingRow type="number" label="Min fan speed" value={settings.minFanSpeed ?? 35} onChange={(v) => onUpdate('minFanSpeed', v as number)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="number" label="Max fan speed" value={settings.maxFanSpeed ?? 100} onChange={(v) => onUpdate('maxFanSpeed', v as number)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          {isAdvanced && (
            <>
              <CompactSettingRow type="number" label="Bridge fan speed" value={settings.bridgeFanSpeed ?? 100} onChange={(v) => onUpdate('bridgeFanSpeed', v as number)} min={0} max={100} step={5} unit="%" disabled={disabled} />
              <CompactSettingRow type="number" label="Full fan at layer" value={settings.fullFanSpeedAtLayer ?? 3} onChange={(v) => onUpdate('fullFanSpeedAtLayer', v as number)} min={1} max={20} step={1} disabled={disabled} />
              <CompactSettingRow type="number" label="Slow down layer time" value={settings.slowDownForLayerTime ?? 5} onChange={(v) => onUpdate('slowDownForLayerTime', v as number)} min={1} max={60} step={1} unit="s" disabled={disabled} />
              <CompactSettingRow type="number" label="Min print speed" value={settings.minPrintSpeed ?? 10} onChange={(v) => onUpdate('minPrintSpeed', v as number)} min={5} max={50} step={5} unit="mm/s" disabled={disabled} />
            </>
          )}
        </>
      )}
    </SettingSection>

    {isAdvanced && (
      <>
        <SettingSection icon={<IroningIcon />} title="Ironing">
          <CompactSettingRow type="checkbox" label="Enable ironing" checked={settings.enableIroning ?? false} onChange={(v) => onUpdate('enableIroning', v)} disabled={disabled} />
          {settings.enableIroning && (
            <>
              <CompactSettingRow type="select" label="Pattern" value={settings.ironingPattern ?? 'zigzag'} onChange={(v) => onUpdate('ironingPattern', v as 'zigzag' | 'concentric')} options={[{ value: 'zigzag', label: 'Zig-Zag' }, { value: 'concentric', label: 'Concentric' }]} disabled={disabled} />
              <CompactSettingRow type="number" label="Flow rate" value={settings.ironingFlowRate ?? 15} onChange={(v) => onUpdate('ironingFlowRate', v as number)} min={0} max={50} step={5} unit="%" disabled={disabled} />
              <CompactSettingRow type="number" label="Spacing" value={settings.ironingSpacing ?? 0.1} onChange={(v) => onUpdate('ironingSpacing', v as number)} min={0.05} max={0.5} step={0.05} unit="mm" disabled={disabled} />
              <CompactSettingRow type="number" label="Speed" value={settings.ironingSpeed ?? 15} onChange={(v) => onUpdate('ironingSpeed', v as number)} min={5} max={100} step={5} unit="mm/s" disabled={disabled} />
            </>
          )}
        </SettingSection>

        <SettingSection icon={<PrecisionIcon />} title="Precision">
          <CompactSettingRow type="checkbox" label="Arc fitting" checked={settings.arcFitting ?? false} onChange={(v) => onUpdate('arcFitting', v)} disabled={disabled} />
          <CompactSettingRow type="number" label="X-Y hole compensation" value={settings.xyHoleCompensation ?? 0} onChange={(v) => onUpdate('xyHoleCompensation', v as number)} min={-1} max={1} step={0.05} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="X-Y contour compensation" value={settings.xyContourCompensation ?? 0} onChange={(v) => onUpdate('xyContourCompensation', v as number)} min={-1} max={1} step={0.05} unit="mm" disabled={disabled} />
        </SettingSection>

        {!!advancedSettings && Object.keys(advancedSettings).length > 0 && (
          <DynamicAdvancedSettingsSection settings={advancedSettings} onChange={onAdvancedSettingsChange} disabled={disabled} />
        )}
      </>
    )}
  </div>
);

export default SlicerSettingsPanel;

type DynamicSettingKind = 'boolean' | 'number' | 'text';

function toLabel(key: string): string {
  const spaced = key
    .replace(/_/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .trim();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

function inferKind(value: unknown): DynamicSettingKind {
  if (typeof value === 'boolean') {
    return 'boolean';
  }

  if (typeof value === 'number') {
    return 'number';
  }

  if (typeof value === 'string') {
    const lower = value.trim().toLowerCase();
    if (lower === 'true' || lower === 'false' || lower === '1' || lower === '0') {
      return 'boolean';
    }
    if (lower !== '' && Number.isFinite(Number(lower))) {
      return 'number';
    }
  }

  return 'text';
}

function toBoolean(value: unknown): boolean {
  if (typeof value === 'boolean') {
    return value;
  }
  if (typeof value === 'number') {
    return value !== 0;
  }
  if (typeof value === 'string') {
    const lower = value.trim().toLowerCase();
    return lower === 'true' || lower === '1' || lower === 'yes' || lower === 'on';
  }
  return false;
}

function toNumber(value: unknown): number {
  if (typeof value === 'number') {
    return value;
  }
  if (typeof value === 'string') {
    const parsed = Number(value.trim());
    if (Number.isFinite(parsed)) {
      return parsed;
    }
  }
  return 0;
}

function toText(value: unknown): string {
  if (typeof value === 'string') {
    return value;
  }
  if (typeof value === 'number' || typeof value === 'boolean') {
    return String(value);
  }

  try {
    return JSON.stringify(value);
  } catch {
    return '';
  }
}

function coerceToOriginalType(original: unknown, next: boolean | number | string): unknown {
  if (typeof original === 'string') {
    return String(next);
  }
  if (typeof original === 'number') {
    return typeof next === 'number' ? next : Number(next);
  }
  if (typeof original === 'boolean') {
    return typeof next === 'boolean' ? next : String(next).toLowerCase() === 'true';
  }
  return next;
}

const DynamicAdvancedSettingsSection: React.FC<{
  settings: Record<string, unknown>;
  onChange?: (settings: Record<string, unknown>) => void;
  disabled: boolean;
}> = ({ settings, onChange, disabled }) => {
  const keys = React.useMemo(() => {
    return Object.keys(settings).sort((a, b) => a.localeCompare(b));
  }, [settings]);

  const update = React.useCallback((key: string, value: boolean | number | string) => {
    if (!onChange) {
      return;
    }

    onChange({
      ...settings,
      [key]: coerceToOriginalType(settings[key], value),
    });
  }, [onChange, settings]);

  return (
    <SettingSection icon={<PrecisionIcon />} title="Additional Orca settings (full coverage)">
      {keys.map((key) => {
        const currentValue = settings[key];
        const kind = inferKind(currentValue);

        if (kind === 'boolean') {
          return (
            <CompactSettingRow
              key={key}
              type="checkbox"
              label={toLabel(key)}
              checked={toBoolean(currentValue)}
              onChange={(v) => update(key, v)}
              disabled={disabled || !onChange}
            />
          );
        }

        if (kind === 'number') {
          return (
            <CompactSettingRow
              key={key}
              type="number"
              label={toLabel(key)}
              value={toNumber(currentValue)}
              onChange={(v) => update(key, v)}
              step={0.01}
              disabled={disabled || !onChange}
            />
          );
        }

        return (
          <CompactSettingRow
            key={key}
            type="text"
            label={toLabel(key)}
            value={toText(currentValue)}
            onChange={(v) => update(key, v)}
            disabled={disabled || !onChange}
          />
        );
      })}
    </SettingSection>
  );
};
