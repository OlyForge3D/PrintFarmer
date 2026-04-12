/**
 * OrcaSlicer-style Settings Panel
 *
 * Category tabs (Quality, Strength, Speed, Support, Multimaterial, Others)
 * are the primary navigation. A Simple/Advanced toggle controls how many
 * settings appear within each tab.
 *
 * Uses OrcaProcessSettings (native snake_case keys) from slicerSettingsTypes.ts.
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
  ProcessSettingsViewMode,
  SettingsCategory,
  OrcaProcessSettings,
  INFILL_PATTERN_INFO,
  InfillPattern,
  ScarfJointSeam,
  WallGenerator,
  WallSequence,
  IroningType,
  BrimType,
  SupportStyle,
  FuzzySkinMode,
  FuzzySkinNoiseType,
  GapFillTarget,
  SlicingMode,
} from './slicerSettingsTypes';

interface SlicerSettingsPanelProps {
  /** Current settings values */
  settings: OrcaProcessSettings;
  /** Called when any setting changes */
  onChange: (settings: Partial<OrcaProcessSettings>) => void;
  /** Initial view mode */
  initialViewMode?: ProcessSettingsViewMode;
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

const ALL_CATEGORIES: { id: SettingsCategory; label: string }[] = [
  { id: 'quality', label: 'Quality' },
  { id: 'strength', label: 'Strength' },
  { id: 'speed', label: 'Speed' },
  { id: 'support', label: 'Support' },
  { id: 'multimaterial', label: 'Multimaterial' },
  { id: 'others', label: 'Others' },
];

/** Simple mode hides Speed tab (matches SimplyPrint/OrcaSlicer Simple mode) */
const SIMPLE_CATEGORIES = ALL_CATEGORIES.filter((c) => c.id !== 'speed');

function getCategoriesForMode(mode: ProcessSettingsViewMode) {
  if (mode === 'advanced') return ALL_CATEGORIES;
  return SIMPLE_CATEGORIES;
}

/**
 * SlicerSettingsPanel — OrcaSlicer-style category-first settings panel.
 *
 * Category tabs are always visible. A small Simple/Advanced toggle controls
 * how many settings each tab renders.
 */
export const SlicerSettingsPanel: React.FC<SlicerSettingsPanelProps> = ({
  settings,
  onChange,
  initialViewMode = 'simple',
  disabled = false,
  className = '',
  isCategoryDirty,
  advancedSettings,
  onAdvancedSettingsChange,
}) => {
  const [viewMode, setViewMode] = useState<ProcessSettingsViewMode>(initialViewMode);
  const [activeCategory, setActiveCategory] = useState<SettingsCategory>('quality');

  const isAdvanced = viewMode === 'advanced';
  const categories = getCategoriesForMode(viewMode);

  // Reset active category if it's hidden in the current mode (e.g. Speed hidden in Simple)
  React.useEffect(() => {
    if (!categories.some((c) => c.id === activeCategory)) {
      setActiveCategory(categories[0].id);
    }
  }, [viewMode, categories, activeCategory]);

  const updateSetting = useCallback(<K extends keyof OrcaProcessSettings>(
    key: K,
    value: OrcaProcessSettings[K]
  ) => {
    onChange({ ...settings, [key]: value });
  }, [settings, onChange]);

  const infillPatternOptions = Object.entries(INFILL_PATTERN_INFO).map(([value, info]) => ({
    value,
    label: info.label,
    icon: <InfillPatternIcon className="w-5 h-5" />,
  }));

  const categoryProps = {
    settings,
    onUpdate: updateSetting,
    disabled,
    isAdvanced,
    infillPatternOptions,
    advancedSettings,
    onAdvancedSettingsChange,
  };

  const modeButtonClass = (mode: ProcessSettingsViewMode, pos: 'left' | 'right') => {
    const roundCls = pos === 'left' ? 'rounded-l-md' : 'rounded-r-md -ml-px';
    const active = viewMode === mode;
    return `px-2 py-0.5 text-[10px] font-medium ${roundCls} border transition-colors ${
      active
        ? 'bg-pf-accent text-white border-pf-accent'
        : 'bg-pf-bg-2 text-pf-text-secondary border-pf-border hover:text-pf-text-primary'
    } disabled:opacity-50`;
  };

  return (
    <div className={`bg-pf-bg-1 rounded-lg ${className}`}>
      {/* Mode toggle: Simple / Advanced */}
      <div className="flex items-center justify-between px-3 py-1.5 border-b border-pf-border">
        <span className="text-[10px] text-pf-text-muted">
          {categories.length} {categories.length === 1 ? 'tab' : 'tabs'}
        </span>
        <div className="flex items-center gap-0">
          <Button variant="unstyled" size="sm" onClick={() => setViewMode('simple')} disabled={disabled} className={modeButtonClass('simple', 'left')}>
            Simple
          </Button>
          <Button variant="unstyled" size="sm" onClick={() => setViewMode('advanced')} disabled={disabled} className={modeButtonClass('advanced', 'right')}>
            Advanced
          </Button>
        </div>
      </div>

      {/* Category tabs + panel */}
      <div className="flex gap-1 p-2 border-b border-pf-border overflow-x-auto" role="tablist">
        {categories.map((cat) => {
          const isDirty = isCategoryDirty?.(cat.id) ?? false;
          const isActive = activeCategory === cat.id;
          return (
            <Button
              key={cat.id}
              variant="unstyled"
              type="button"
              role="tab"
              aria-selected={isActive ? 'true' : undefined}
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

      <div className="p-3" id={`panel-${activeCategory}`} role="tabpanel">
        {activeCategory === 'quality' && <QualitySettings {...categoryProps} />}
        {activeCategory === 'strength' && <StrengthSettings {...categoryProps} />}
        {activeCategory === 'speed' && <SpeedSettings {...categoryProps} />}
        {activeCategory === 'support' && <SupportSettings {...categoryProps} />}
        {activeCategory === 'multimaterial' && <MultimaterialSettings {...categoryProps} />}
        {activeCategory === 'others' && <OtherSettings {...categoryProps} />}
      </div>
    </div>
  );
};

/* ─── shared prop shape for every category ─── */
interface CategorySettingsProps {
  settings: OrcaProcessSettings;
  onUpdate: <K extends keyof OrcaProcessSettings>(key: K, value: OrcaProcessSettings[K]) => void;
  disabled: boolean;
  isAdvanced: boolean;
  infillPatternOptions: Array<{ value: string; label: string; icon?: React.ReactNode }>;
  advancedSettings?: Record<string, unknown>;
  onAdvancedSettingsChange?: (settings: Record<string, unknown>) => void;
}

/* ─── Quality ─── */
const QualitySettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled, isAdvanced }) => (
  <div className="space-y-4">
    <SettingSection icon={<LayerHeightIcon className="w-4 h-4" />} title="Layer height">
      <CompactSettingRow type="number" label="Layer height" value={settings.layer_height ?? 0.2} onChange={(v) => onUpdate('layer_height', v)} min={0.04} max={0.4} step={0.01} unit="mm" disabled={disabled} />
      <CompactSettingRow type="number" label="First layer height" value={settings.initial_layer_print_height ?? 0.2} onChange={(v) => onUpdate('initial_layer_print_height', v)} min={0.1} max={0.4} step={0.01} unit="mm" disabled={disabled} />
    </SettingSection>

    {isAdvanced && (
      <SettingSection icon={<LineWidthIcon className="w-4 h-4" />} title="Line width">
        <CompactSettingRow type="number" label="Default" value={settings.line_width ?? 0.45} onChange={(v) => onUpdate('line_width', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
        <CompactSettingRow type="number" label="First layer" value={settings.initial_layer_line_width ?? 0.5} onChange={(v) => onUpdate('initial_layer_line_width', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
        <CompactSettingRow type="number" label="Outer wall" value={settings.outer_wall_line_width ?? 0.45} onChange={(v) => onUpdate('outer_wall_line_width', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
        <CompactSettingRow type="number" label="Inner wall" value={settings.inner_wall_line_width ?? 0.45} onChange={(v) => onUpdate('inner_wall_line_width', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
        <CompactSettingRow type="number" label="Top surface" value={settings.top_surface_line_width ?? 0.45} onChange={(v) => onUpdate('top_surface_line_width', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
        <CompactSettingRow type="number" label="Sparse infill" value={settings.sparse_infill_line_width ?? 0.45} onChange={(v) => onUpdate('sparse_infill_line_width', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
        <CompactSettingRow type="number" label="Internal solid infill" value={settings.internal_solid_infill_line_width ?? 0.45} onChange={(v) => onUpdate('internal_solid_infill_line_width', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
        <CompactSettingRow type="number" label="Support" value={settings.support_line_width ?? 0.45} onChange={(v) => onUpdate('support_line_width', v)} min={0.2} max={1.0} step={0.01} unit="mm or %" disabled={disabled} />
      </SettingSection>
    )}

    <SettingSection icon={<SeamIcon className="w-4 h-4" />} title="Seam">
      <CompactSettingRow type="select" label="Seam position" value={settings.seam_position ?? 'aligned'} onChange={(v) => onUpdate('seam_position', v as 'random' | 'aligned' | 'back' | 'nearest')} options={[{ value: 'aligned', label: 'Aligned' }, { value: 'back', label: 'Back' }, { value: 'nearest', label: 'Nearest' }, { value: 'random', label: 'Random' }]} disabled={disabled} />
      <CompactSettingRow type="number" label="Scarf joint flow ratio" value={settings.scarf_joint_flow_ratio ?? 1.0} onChange={(v) => onUpdate('scarf_joint_flow_ratio', v)} min={0.5} max={2.0} step={0.05} disabled={disabled} />
    </SettingSection>

    <SettingSection icon={<WallCountIcon className="w-4 h-4" />} title="Surface quality">
      <CompactSettingRow type="checkbox" label="Only one wall on first layer" checked={settings.only_one_wall_first_layer ?? false} onChange={(v) => onUpdate('only_one_wall_first_layer', v)} disabled={disabled} />
      <CompactSettingRow type="checkbox" label="Only one wall on top surfaces" checked={settings.only_one_wall_top ?? false} onChange={(v) => onUpdate('only_one_wall_top', v)} disabled={disabled} />
      <CompactSettingRow type="checkbox" label="Precise outer wall" checked={settings.precise_outer_wall ?? false} onChange={(v) => onUpdate('precise_outer_wall', v)} disabled={disabled} />
    </SettingSection>

    {isAdvanced && (
      <SettingSection icon={<LayerHeightIcon className="w-4 h-4" />} title="Sequence">
        <CompactSettingRow type="select" label="First layer filament sequence" value={settings.first_layer_sequence_choice ?? 'default'} onChange={(v) => onUpdate('first_layer_sequence_choice', v)} options={[{ value: 'default', label: 'Default' }, { value: 'customizable', label: 'Customizable' }]} disabled={disabled} />
        <CompactSettingRow type="select" label="Other layers filament sequence" value={settings.other_layers_sequence_choice ?? 'default'} onChange={(v) => onUpdate('other_layers_sequence_choice', v)} options={[{ value: 'default', label: 'Default' }, { value: 'customizable', label: 'Customizable' }]} disabled={disabled} />
      </SettingSection>
    )}

    {isAdvanced && (
      <>
        <SettingSection icon={<SeamIcon className="w-4 h-4" />} title="Seam (advanced)">
          <CompactSettingRow type="number" label="Seam gap" value={settings.seam_gap ?? 0} onChange={(v) => onUpdate('seam_gap', v)} min={0} max={5} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Staggered inner seams" checked={settings.staggered_inner_seams ?? false} onChange={(v) => onUpdate('staggered_inner_seams', v)} disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<SeamIcon className="w-4 h-4" />} title="Scarf joint seam">
          <CompactSettingRow type="select" label="Scarf joint seam" value={settings.seam_slope_type ?? 'none'} onChange={(v) => onUpdate('seam_slope_type', v as ScarfJointSeam)} options={[{ value: 'none', label: 'None' }, { value: 'contour', label: 'Contour' }, { value: 'all', label: 'All walls' }]} disabled={disabled} />
          {settings.seam_slope_type !== 'none' && (
            <>
              <CompactSettingRow type="number" label="Scarf min length" value={settings.seam_slope_min_length ?? 10} onChange={(v) => onUpdate('seam_slope_min_length', v)} min={1} max={50} step={1} unit="mm" disabled={disabled} />
              <CompactSettingRow type="number" label="Scarf steps" value={settings.seam_slope_steps ?? 10} onChange={(v) => onUpdate('seam_slope_steps', v)} min={1} max={50} step={1} disabled={disabled} />
              <CompactSettingRow type="number" label="Scarf start height" value={settings.seam_slope_start_height ?? 0} onChange={(v) => onUpdate('seam_slope_start_height', v)} min={0} max={10} step={0.1} unit="mm" disabled={disabled} />
              <CompactSettingRow type="number" label="Scarf joint speed" value={settings.scarf_joint_speed ?? 0} onChange={(v) => onUpdate('scarf_joint_speed', v)} min={0} max={200} step={5} unit="mm/s" disabled={disabled} />
              <CompactSettingRow type="number" label="Scarf joint flow ratio" value={settings.scarf_joint_flow_ratio ?? 1.0} onChange={(v) => onUpdate('scarf_joint_flow_ratio', v)} min={0.5} max={2.0} step={0.05} disabled={disabled} />
              <CompactSettingRow type="checkbox" label="Scarf around entire wall" checked={settings.seam_slope_entire_loop ?? false} onChange={(v) => onUpdate('seam_slope_entire_loop', v)} disabled={disabled} />
              <CompactSettingRow type="checkbox" label="Scarf joint for inner walls" checked={settings.seam_slope_inner_walls ?? false} onChange={(v) => onUpdate('seam_slope_inner_walls', v)} disabled={disabled} />
              <CompactSettingRow type="checkbox" label="Conditional scarf joint" checked={settings.seam_slope_conditional ?? false} onChange={(v) => onUpdate('seam_slope_conditional', v)} disabled={disabled} />
              {settings.seam_slope_conditional && (
                <>
                  <CompactSettingRow type="number" label="Angle threshold" value={settings.scarf_angle_threshold ?? 0} onChange={(v) => onUpdate('scarf_angle_threshold', v)} min={0} max={180} step={5} unit="°" disabled={disabled} />
                  <CompactSettingRow type="number" label="Overhang threshold" value={settings.scarf_overhang_threshold ?? 0} onChange={(v) => onUpdate('scarf_overhang_threshold', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
                </>
              )}
            </>
          )}
        </SettingSection>

        <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Wipe">
          <CompactSettingRow type="checkbox" label="Role-based wipe speed" checked={settings.role_based_wipe_speed ?? false} onChange={(v) => onUpdate('role_based_wipe_speed', v)} disabled={disabled} />
          <CompactSettingRow type="number" label="Wipe speed" value={settings.wipe_speed ?? 80} onChange={(v) => onUpdate('wipe_speed', v)} min={10} max={200} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Wipe on loops" checked={settings.wipe_on_loops ?? true} onChange={(v) => onUpdate('wipe_on_loops', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Wipe before external loop" checked={settings.wipe_before_external_loop ?? false} onChange={(v) => onUpdate('wipe_before_external_loop', v)} disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<PrecisionIcon className="w-4 h-4" />} title="Precision">
          <CompactSettingRow type="number" label="Resolution" value={settings.resolution ?? 0.0125} onChange={(v) => onUpdate('resolution', v)} min={0.001} max={0.1} step={0.001} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Slice gap closing radius" value={settings.slice_closing_radius ?? 0.05} onChange={(v) => onUpdate('slice_closing_radius', v)} min={0} max={1} step={0.01} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Elephant foot compensation" value={settings.elefant_foot_compensation ?? 0.1} onChange={(v) => onUpdate('elefant_foot_compensation', v)} min={0} max={1} step={0.01} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Elephant foot comp. layers" value={settings.elefant_foot_compensation_layers ?? 1} onChange={(v) => onUpdate('elefant_foot_compensation_layers', v)} min={0} max={10} step={1} disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<WallCountIcon className="w-4 h-4" />} title="Wall generator">
          <CompactSettingRow type="number" label="Min wall thickness" value={settings.min_wall_thickness ?? 0.8} onChange={(v) => onUpdate('min_wall_thickness', v)} min={0.4} max={2} step={0.1} unit="mm" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<WallCountIcon className="w-4 h-4" />} title="Walls & surfaces">
          <CompactSettingRow type="checkbox" label="Precise outer wall" checked={settings.precise_outer_wall ?? false} onChange={(v) => onUpdate('precise_outer_wall', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Precise Z height" checked={settings.precise_z_height ?? false} onChange={(v) => onUpdate('precise_z_height', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Convert holes to polyholes" checked={settings.hole_to_polyhole ?? false} onChange={(v) => onUpdate('hole_to_polyhole', v)} disabled={disabled} />
          {settings.hole_to_polyhole && (
            <>
              <CompactSettingRow type="number" label="Polyhole detection margin" value={settings.hole_to_polyhole_threshold ?? 0.01} onChange={(v) => onUpdate('hole_to_polyhole_threshold', v)} min={0} max={0.1} step={0.005} unit="mm" disabled={disabled} />
              <CompactSettingRow type="checkbox" label="Polyhole twist" checked={settings.hole_to_polyhole_twisted ?? true} onChange={(v) => onUpdate('hole_to_polyhole_twisted', v)} disabled={disabled} />
            </>
          )}
        </SettingSection>

        <SettingSection icon={<InfillDensityIcon className="w-4 h-4" />} title="Flow ratio">
          <CompactSettingRow type="number" label="Outer wall flow ratio" value={settings.outer_wall_flow_ratio ?? 1.0} onChange={(v) => onUpdate('outer_wall_flow_ratio', v)} min={0.5} max={1.5} step={0.05} disabled={disabled} />
          <CompactSettingRow type="number" label="Inner wall flow ratio" value={settings.inner_wall_flow_ratio ?? 1.0} onChange={(v) => onUpdate('inner_wall_flow_ratio', v)} min={0.5} max={1.5} step={0.05} disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Bridging">
          <CompactSettingRow type="number" label="Max bridge length" value={settings.max_bridge_length ?? 10} onChange={(v) => onUpdate('max_bridge_length', v)} min={5} max={50} step={1} unit="mm" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<WallCountIcon className="w-4 h-4" />} title="Wall generator (Arachne)">
          <CompactSettingRow type="select" label="Wall generator" value={settings.wall_generator ?? 'arachne'} onChange={(v) => onUpdate('wall_generator', v as WallGenerator)} options={[{ value: 'classic', label: 'Classic' }, { value: 'arachne', label: 'Arachne' }]} disabled={disabled} />
          <CompactSettingRow type="select" label="Wall printing order" value={settings.wall_sequence ?? 'inner wall/outer wall'} onChange={(v) => onUpdate('wall_sequence', v as WallSequence)} options={[{ value: 'inner wall/outer wall', label: 'Inner/Outer' }, { value: 'outer wall/inner wall', label: 'Outer/Inner' }, { value: 'inner-outer-inner wall', label: 'Inner-Outer-Inner' }]} disabled={disabled} />
          <CompactSettingRow type="number" label="Min wall width" value={settings.min_bead_width ?? 0.85} onChange={(v) => onUpdate('min_bead_width', v)} min={0} max={2} step={0.01} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Min feature size" value={settings.min_feature_size ?? 0.25} onChange={(v) => onUpdate('min_feature_size', v)} min={0} max={1} step={0.01} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="First layer min wall width" value={settings.initial_layer_min_bead_width ?? 0.85} onChange={(v) => onUpdate('initial_layer_min_bead_width', v)} min={0} max={2} step={0.01} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Wall distribution count" value={settings.wall_distribution_count ?? 1} onChange={(v) => onUpdate('wall_distribution_count', v)} min={1} max={10} step={1} disabled={disabled} />
          <CompactSettingRow type="number" label="Min wall length" value={settings.min_length_factor ?? 0.5} onChange={(v) => onUpdate('min_length_factor', v)} min={0} max={5} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Wall transition length" value={settings.wall_transition_length ?? 0.4} onChange={(v) => onUpdate('wall_transition_length', v)} min={0} max={2} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Wall transition angle" value={settings.wall_transition_angle ?? 10} onChange={(v) => onUpdate('wall_transition_angle', v)} min={1} max={45} step={1} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="Wall transition filter margin" value={settings.wall_transition_filter_deviation ?? 25} onChange={(v) => onUpdate('wall_transition_filter_deviation', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<LayerHeightIcon className="w-4 h-4" />} title="Advanced quality">
          <CompactSettingRow type="number" label="Flow ratio" value={settings.print_flow_ratio ?? 1.0} onChange={(v) => onUpdate('print_flow_ratio', v)} min={0.5} max={2.0} step={0.01} disabled={disabled} />
          <CompactSettingRow type="number" label="Bridge flow ratio" value={settings.bridge_flow ?? 1.0} onChange={(v) => onUpdate('bridge_flow', v)} min={0.5} max={2.0} step={0.05} disabled={disabled} />
          <CompactSettingRow type="number" label="Internal bridge flow" value={settings.internal_bridge_flow ?? 1.0} onChange={(v) => onUpdate('internal_bridge_flow', v)} min={0.5} max={2.0} step={0.05} disabled={disabled} />
          <CompactSettingRow type="number" label="One wall threshold" value={settings.min_width_top_surface ?? 0} onChange={(v) => onUpdate('min_width_top_surface', v)} min={0} max={2} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Interface shells" checked={settings.interface_shells ?? false} onChange={(v) => onUpdate('interface_shells', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Print infill first" checked={settings.is_infill_first ?? false} onChange={(v) => onUpdate('is_infill_first', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Thick external bridges" checked={settings.thick_bridges ?? false} onChange={(v) => onUpdate('thick_bridges', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Thick internal bridges" checked={settings.thick_internal_bridges ?? false} onChange={(v) => onUpdate('thick_internal_bridges', v)} disabled={disabled} />
          <CompactSettingRow type="select" label="Bridge counterbore holes" value={settings.counterbore_hole_bridging ?? 'none'} onChange={(v) => onUpdate('counterbore_hole_bridging', v)} options={[{ value: 'none', label: 'None' }, { value: 'partially', label: 'Partially bridged' }, { value: 'sacrificial', label: 'Sacrificial layer' }]} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Extra bridge layers (beta)" checked={settings.enable_extra_bridge_layer ?? false} onChange={(v) => onUpdate('enable_extra_bridge_layer', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Filter internal bridges" checked={settings.dont_filter_internal_bridges ?? false} onChange={(v) => onUpdate('dont_filter_internal_bridges', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Small area flow compensation" checked={settings.small_area_infill_flow_compensation ?? false} onChange={(v) => onUpdate('small_area_infill_flow_compensation', v)} disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<WallCountIcon className="w-4 h-4" />} title="Overhang handling">
          <CompactSettingRow type="checkbox" label="Detect overhang walls" checked={settings.detect_overhang_wall ?? true} onChange={(v) => onUpdate('detect_overhang_wall', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Extra perimeters on overhangs" checked={settings.extra_perimeters_on_overhangs ?? false} onChange={(v) => onUpdate('extra_perimeters_on_overhangs', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Make overhangs printable" checked={settings.make_overhang_printable ?? false} onChange={(v) => onUpdate('make_overhang_printable', v)} disabled={disabled} />
          {settings.make_overhang_printable && (
            <>
              <CompactSettingRow type="number" label="Max overhang angle" value={settings.make_overhang_printable_angle ?? 55} onChange={(v) => onUpdate('make_overhang_printable_angle', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
              <CompactSettingRow type="number" label="Hole area threshold" value={settings.make_overhang_printable_hole_size ?? 0} onChange={(v) => onUpdate('make_overhang_printable_hole_size', v)} min={0} max={100} step={1} unit="mm²" disabled={disabled} />
            </>
          )}
          <CompactSettingRow type="checkbox" label="Reverse on even" checked={settings.overhang_reverse ?? false} onChange={(v) => onUpdate('overhang_reverse', v)} disabled={disabled} />
          {settings.overhang_reverse && (
            <>
              <CompactSettingRow type="checkbox" label="Reverse internal only" checked={settings.overhang_reverse_internal_only ?? false} onChange={(v) => onUpdate('overhang_reverse_internal_only', v)} disabled={disabled} />
              <CompactSettingRow type="number" label="Reverse threshold" value={settings.overhang_reverse_threshold ?? 0} onChange={(v) => onUpdate('overhang_reverse_threshold', v)} min={0} max={100} step={5} unit="mm/s" disabled={disabled} />
            </>
          )}
          <CompactSettingRow type="checkbox" label="Avoid crossing walls" checked={settings.reduce_crossing_wall ?? false} onChange={(v) => onUpdate('reduce_crossing_wall', v)} disabled={disabled} />
          {settings.reduce_crossing_wall && (
            <CompactSettingRow type="number" label="Max detour length" value={settings.max_travel_detour_distance ?? 0} onChange={(v) => onUpdate('max_travel_detour_distance', v)} min={0} max={100} step={1} unit="mm" disabled={disabled} />
          )}
          <CompactSettingRow type="number" label="Wall direction" value={settings.wall_direction ?? 0} onChange={(v) => onUpdate('wall_direction', v)} min={0} max={360} step={15} unit="°" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<IroningIcon className="w-4 h-4" />} title="Ironing (quality)">
          <CompactSettingRow type="select" label="Ironing type" value={settings.ironing_type ?? 'no_ironing'} onChange={(v) => onUpdate('ironing_type', v as IroningType)} options={[{ value: 'no_ironing', label: 'No ironing' }, { value: 'top', label: 'Top surfaces' }, { value: 'topmost', label: 'Topmost surface' }, { value: 'all_solid', label: 'All solid layers' }]} disabled={disabled} />
          {settings.ironing_type && settings.ironing_type !== 'no_ironing' && (
            <>
              <CompactSettingRow type="number" label="Ironing flow" value={settings.filament_ironing_flow ?? 15} onChange={(v) => onUpdate('filament_ironing_flow', v)} min={0} max={100} step={1} unit="%" disabled={disabled} />
              <CompactSettingRow type="number" label="Ironing spacing" value={settings.filament_ironing_spacing ?? 0.1} onChange={(v) => onUpdate('filament_ironing_spacing', v)} min={0.01} max={1} step={0.01} unit="mm" disabled={disabled} />
              <CompactSettingRow type="number" label="Ironing inset" value={settings.ironing_inset ?? 0.25} onChange={(v) => onUpdate('ironing_inset', v)} min={0} max={2} step={0.05} unit="mm" disabled={disabled} />
              <CompactSettingRow type="number" label="Fixed ironing angle" value={settings.ironing_angle_fixed ?? 45} onChange={(v) => onUpdate('ironing_angle_fixed', v)} min={0} max={360} step={5} unit="°" disabled={disabled} />
              <CompactSettingRow type="number" label="Filament ironing inset" value={settings.filament_ironing_inset ?? 0} onChange={(v) => onUpdate('filament_ironing_inset', v)} min={0} max={2} step={0.05} unit="mm" disabled={disabled} />
            </>
          )}
        </SettingSection>
      </>
    )}
  </div>
);

/* ─── Strength ─── */
const StrengthSettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled, isAdvanced, infillPatternOptions }) => (
  <div className="space-y-4">
    <SettingSection icon={<InfillDensityIcon className="w-4 h-4" />} title="Infill">
      <CompactSettingRow type="number" label="Sparse infill density" value={settings.sparse_infill_density ?? 15} onChange={(v) => onUpdate('sparse_infill_density', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
      <CompactSettingRow type="select" label="Sparse infill pattern" value={settings.sparse_infill_pattern ?? 'grid'} onChange={(v) => onUpdate('sparse_infill_pattern', v as InfillPattern)} options={infillPatternOptions} disabled={disabled} />
      <CompactSettingRow type="select" label="Internal solid infill pattern" value={settings.internal_solid_infill_pattern ?? 'monotonic'} onChange={(v) => onUpdate('internal_solid_infill_pattern', v)} options={[{ value: 'monotonic', label: 'Monotonic' }, { value: 'rectilinear', label: 'Rectilinear' }, { value: 'concentric', label: 'Concentric' }, { value: 'hilbertcurve', label: 'Hilbert Curve' }, { value: 'archimedeanChords', label: 'Archimedean Chords' }]} disabled={disabled} />
      {isAdvanced && (
        <>
          <CompactSettingRow type="number" label="Infill/wall overlap" value={settings.infill_wall_overlap ?? 25} onChange={(v) => onUpdate('infill_wall_overlap', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="number" label="Infill anchor max length" value={settings.infill_anchor_max ?? 10} onChange={(v) => onUpdate('infill_anchor_max', v)} min={0} max={50} step={1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Sparse infill direction" value={settings.infill_direction ?? 45} onChange={(v) => onUpdate('infill_direction', v)} min={0} max={360} step={5} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="Solid infill direction" value={settings.solid_infill_direction ?? 0} onChange={(v) => onUpdate('solid_infill_direction', v)} min={0} max={360} step={5} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="Infill combination max layer height" value={settings.infill_combination_max_layer_height ?? 0} onChange={(v) => onUpdate('infill_combination_max_layer_height', v)} min={0} max={1} step={0.05} unit="mm" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Infill combination" checked={settings.infill_combination ?? false} onChange={(v) => onUpdate('infill_combination', v)} disabled={disabled} />
          <CompactSettingRow type="number" label="Minimum sparse infill area" value={settings.minimum_sparse_infill_area ?? 15} onChange={(v) => onUpdate('minimum_sparse_infill_area', v)} min={0} max={100} step={1} unit="mm²" disabled={disabled} />
          <CompactSettingRow type="select" label="Gap fill" value={settings.gap_fill_target ?? 'everywhere'} onChange={(v) => onUpdate('gap_fill_target', v as GapFillTarget)} options={[{ value: 'everywhere', label: 'Everywhere' }, { value: 'topbottom', label: 'Top/Bottom only' }, { value: 'nowhere', label: 'Nowhere' }]} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Align infill to model" checked={settings.align_infill_direction_to_model ?? false} onChange={(v) => onUpdate('align_infill_direction_to_model', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Sparse infill rotation" checked={settings.sparse_infill_rotate_template ?? false} onChange={(v) => onUpdate('sparse_infill_rotate_template', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Solid infill rotation" checked={settings.solid_infill_rotate_template ?? false} onChange={(v) => onUpdate('solid_infill_rotate_template', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Symmetric infill Y axis" checked={settings.symmetric_infill_y_axis ?? false} onChange={(v) => onUpdate('symmetric_infill_y_axis', v)} disabled={disabled} />
          <CompactSettingRow type="number" label="Infill lock depth" value={settings.infill_lock_depth ?? 0} onChange={(v) => onUpdate('infill_lock_depth', v)} min={0} max={10} step={1} disabled={disabled} />
          <CompactSettingRow type="number" label="Infill shift step" value={settings.infill_shift_step ?? 0} onChange={(v) => onUpdate('infill_shift_step', v)} min={0} max={100} step={1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Infill overhang angle" value={settings.infill_overhang_angle ?? 0} onChange={(v) => onUpdate('infill_overhang_angle', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
        </>
      )}
    </SettingSection>

    <SettingSection icon={<WallCountIcon className="w-4 h-4" />} title="Walls & shells">
      <CompactSettingRow type="number" label="Wall loops" value={settings.wall_loops ?? 2} onChange={(v) => onUpdate('wall_loops', v)} min={1} max={10} step={1} disabled={disabled} />
      <CompactSettingRow type="number" label="Top shell layers" value={settings.top_shell_layers ?? 4} onChange={(v) => onUpdate('top_shell_layers', v)} min={1} max={10} step={1} disabled={disabled} />
      <CompactSettingRow type="number" label="Bottom shell layers" value={settings.bottom_shell_layers ?? 3} onChange={(v) => onUpdate('bottom_shell_layers', v)} min={1} max={10} step={1} disabled={disabled} />
      <CompactSettingRow type="number" label="Top shell thickness" value={settings.top_shell_thickness ?? 0.8} onChange={(v) => onUpdate('top_shell_thickness', v)} min={0} max={5} step={0.1} unit="mm" disabled={disabled} />
      <CompactSettingRow type="number" label="Bottom shell thickness" value={settings.bottom_shell_thickness ?? 0.8} onChange={(v) => onUpdate('bottom_shell_thickness', v)} min={0} max={5} step={0.1} unit="mm" disabled={disabled} />
      <CompactSettingRow type="checkbox" label="Fill multiline" checked={settings.fill_multiline ?? false} onChange={(v) => onUpdate('fill_multiline', v)} disabled={disabled} />
      {isAdvanced && (
        <>
          <CompactSettingRow type="checkbox" label="Alternate extra wall" checked={settings.alternate_extra_wall ?? false} onChange={(v) => onUpdate('alternate_extra_wall', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Detect thin walls" checked={settings.detect_thin_wall ?? true} onChange={(v) => onUpdate('detect_thin_wall', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Detect narrow internal solid" checked={settings.detect_narrow_internal_solid_infill ?? true} onChange={(v) => onUpdate('detect_narrow_internal_solid_infill', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Insert solid layers" checked={settings.extra_solid_infills ?? false} onChange={(v) => onUpdate('extra_solid_infills', v)} disabled={disabled} />
          <CompactSettingRow type="number" label="Top/bottom overlap" value={settings.top_bottom_infill_wall_overlap ?? 25} onChange={(v) => onUpdate('top_bottom_infill_wall_overlap', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="select" label="Ensure vertical shell" value={settings.ensure_vertical_shell_thickness ?? 'none'} onChange={(v) => onUpdate('ensure_vertical_shell_thickness', v)} options={[{ value: 'none', label: 'None' }, { value: 'limited', label: 'Limited' }, { value: 'all', label: 'All' }]} disabled={disabled} />
        </>
      )}
    </SettingSection>

    <SettingSection icon={<InfillDensityIcon className="w-4 h-4" />} title="Top/bottom shells">
      <CompactSettingRow type="number" label="Top surface density" value={settings.top_surface_density ?? 100} onChange={(v) => onUpdate('top_surface_density', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
      <CompactSettingRow type="select" label="Top surface pattern" value={settings.top_surface_pattern ?? 'monotonic'} onChange={(v) => onUpdate('top_surface_pattern', v)} options={[{ value: 'monotonic', label: 'Monotonic' }, { value: 'monotoniclines', label: 'Monotonic lines' }, { value: 'rectilinear', label: 'Rectilinear' }, { value: 'concentric', label: 'Concentric' }]} disabled={disabled} />
      <CompactSettingRow type="number" label="Bottom surface density" value={settings.bottom_surface_density ?? 100} onChange={(v) => onUpdate('bottom_surface_density', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
      <CompactSettingRow type="select" label="Bottom surface pattern" value={settings.bottom_surface_pattern ?? 'monotonic'} onChange={(v) => onUpdate('bottom_surface_pattern', v)} options={[{ value: 'monotonic', label: 'Monotonic' }, { value: 'rectilinear', label: 'Rectilinear' }, { value: 'concentric', label: 'Concentric' }]} disabled={disabled} />
    </SettingSection>

    {isAdvanced && (
      <>
        <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Bridging (strength)">
          <CompactSettingRow type="number" label="External bridge direction" value={settings.bridge_angle ?? 0} onChange={(v) => onUpdate('bridge_angle', v)} min={0} max={360} step={5} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="External bridge density" value={settings.bridge_density ?? 100} onChange={(v) => onUpdate('bridge_density', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="number" label="Internal bridge direction" value={settings.internal_bridge_angle ?? 0} onChange={(v) => onUpdate('internal_bridge_angle', v)} min={0} max={360} step={5} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="Internal bridge density" value={settings.internal_bridge_density ?? 100} onChange={(v) => onUpdate('internal_bridge_density', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<InfillDensityIcon className="w-4 h-4" />} title="Advanced infill">
          <CompactSettingRow type="number" label="Skeleton infill density" value={settings.skeleton_infill_density ?? 0} onChange={(v) => onUpdate('skeleton_infill_density', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="number" label="Skeleton line width" value={settings.skeleton_infill_line_width ?? 0.45} onChange={(v) => onUpdate('skeleton_infill_line_width', v)} min={0.1} max={2} step={0.05} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Skin infill density" value={settings.skin_infill_density ?? 0} onChange={(v) => onUpdate('skin_infill_density', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="number" label="Skin infill depth" value={settings.skin_infill_depth ?? 1} onChange={(v) => onUpdate('skin_infill_depth', v)} min={0} max={10} step={1} disabled={disabled} />
          <CompactSettingRow type="number" label="Skin line width" value={settings.skin_infill_line_width ?? 0.45} onChange={(v) => onUpdate('skin_infill_line_width', v)} min={0.1} max={2} step={0.05} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Lateral lattice angle 1" value={settings.lateral_lattice_angle_1 ?? 45} onChange={(v) => onUpdate('lateral_lattice_angle_1', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="Lateral lattice angle 2" value={settings.lateral_lattice_angle_2 ?? -45} onChange={(v) => onUpdate('lateral_lattice_angle_2', v)} min={-90} max={90} step={5} unit="°" disabled={disabled} />
        </SettingSection>
      </>
    )}
  </div>
);

/* ─── Speed ─── */
const SpeedSettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled, isAdvanced }) => (
  <div className="space-y-4">
    <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Print speeds">
      <CompactSettingRow type="number" label="Outer wall" value={settings.outer_wall_speed ?? 60} onChange={(v) => onUpdate('outer_wall_speed', v)} min={5} max={500} step={5} unit="mm/s" disabled={disabled} />
      <CompactSettingRow type="number" label="Inner wall" value={settings.inner_wall_speed ?? 100} onChange={(v) => onUpdate('inner_wall_speed', v)} min={5} max={500} step={5} unit="mm/s" disabled={disabled} />
      <CompactSettingRow type="number" label="Sparse infill" value={settings.sparse_infill_speed ?? 120} onChange={(v) => onUpdate('sparse_infill_speed', v)} min={5} max={500} step={5} unit="mm/s" disabled={disabled} />
      <CompactSettingRow type="number" label="Internal solid infill" value={settings.internal_solid_infill_speed ?? 100} onChange={(v) => onUpdate('internal_solid_infill_speed', v)} min={5} max={500} step={5} unit="mm/s" disabled={disabled} />
      <CompactSettingRow type="number" label="Top surface" value={settings.top_surface_speed ?? 50} onChange={(v) => onUpdate('top_surface_speed', v)} min={5} max={300} step={5} unit="mm/s" disabled={disabled} />
      <CompactSettingRow type="number" label="Travel" value={settings.travel_speed ?? 200} onChange={(v) => onUpdate('travel_speed', v)} min={50} max={600} step={10} unit="mm/s" disabled={disabled} />
      <CompactSettingRow type="number" label="First layer" value={settings.initial_layer_speed ?? 30} onChange={(v) => onUpdate('initial_layer_speed', v)} min={5} max={100} step={5} unit="mm/s" disabled={disabled} />
    </SettingSection>

    <SettingSection icon={<AccelerationIcon className="w-4 h-4" />} title="Acceleration">
      <CompactSettingRow type="number" label="Default" value={settings.default_acceleration ?? 500} onChange={(v) => onUpdate('default_acceleration', v)} min={0} max={10000} step={100} unit="mm/s²" disabled={disabled} />
      <CompactSettingRow type="number" label="Outer wall" value={settings.outer_wall_acceleration ?? 500} onChange={(v) => onUpdate('outer_wall_acceleration', v)} min={0} max={10000} step={100} unit="mm/s²" disabled={disabled} />
      <CompactSettingRow type="number" label="Inner wall" value={settings.inner_wall_acceleration ?? 500} onChange={(v) => onUpdate('inner_wall_acceleration', v)} min={0} max={10000} step={100} unit="mm/s²" disabled={disabled} />
      <CompactSettingRow type="number" label="Sparse infill" value={settings.sparse_infill_acceleration ?? 500} onChange={(v) => onUpdate('sparse_infill_acceleration', v)} min={0} max={10000} step={100} unit="mm/s²" disabled={disabled} />
      <CompactSettingRow type="number" label="Top surface" value={settings.top_surface_acceleration ?? 500} onChange={(v) => onUpdate('top_surface_acceleration', v)} min={0} max={10000} step={100} unit="mm/s²" disabled={disabled} />
      <CompactSettingRow type="number" label="Travel" value={settings.travel_acceleration ?? 500} onChange={(v) => onUpdate('travel_acceleration', v)} min={0} max={10000} step={100} unit="mm/s²" disabled={disabled} />
    </SettingSection>

    {isAdvanced && (
      <>
        <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Additional speeds">
          <CompactSettingRow type="number" label="Bridge" value={settings.bridge_speed ?? 30} onChange={(v) => onUpdate('bridge_speed', v)} min={5} max={200} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Internal bridge" value={settings.internal_bridge_speed ?? 60} onChange={(v) => onUpdate('internal_bridge_speed', v)} min={5} max={200} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Gap infill" value={settings.gap_infill_speed ?? 30} onChange={(v) => onUpdate('gap_infill_speed', v)} min={5} max={200} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Support" value={settings.support_speed ?? 60} onChange={(v) => onUpdate('support_speed', v)} min={5} max={300} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Support interface" value={settings.support_interface_speed ?? 40} onChange={(v) => onUpdate('support_interface_speed', v)} min={5} max={200} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Small perimeters" value={settings.small_perimeter_speed ?? 50} onChange={(v) => onUpdate('small_perimeter_speed', v)} min={5} max={200} step={5} unit="mm/s or %" disabled={disabled} />
          <CompactSettingRow type="number" label="Small perimeter threshold" value={settings.small_perimeter_threshold ?? 6} onChange={(v) => onUpdate('small_perimeter_threshold', v)} min={0} max={30} step={1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Ironing speed" value={settings.filament_ironing_speed ?? 30} onChange={(v) => onUpdate('filament_ironing_speed', v)} min={5} max={100} step={5} unit="mm/s or %" disabled={disabled} />
          <CompactSettingRow type="number" label="First layer travel" value={settings.initial_layer_travel_speed ?? 200} onChange={(v) => onUpdate('initial_layer_travel_speed', v)} min={50} max={600} step={10} unit="mm/s or %" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<AccelerationIcon className="w-4 h-4" />} title="Additional acceleration">
          <CompactSettingRow type="number" label="Bridge" value={settings.bridge_acceleration ?? 500} onChange={(v) => onUpdate('bridge_acceleration', v)} min={0} max={10000} step={100} unit="mm/s²" disabled={disabled} />
          <CompactSettingRow type="number" label="First layer" value={settings.initial_layer_acceleration ?? 500} onChange={(v) => onUpdate('initial_layer_acceleration', v)} min={0} max={10000} step={100} unit="mm/s²" disabled={disabled} />
          <CompactSettingRow type="number" label="Internal solid infill" value={settings.internal_solid_infill_acceleration ?? 500} onChange={(v) => onUpdate('internal_solid_infill_acceleration', v)} min={0} max={10000} step={100} unit="mm/s²" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Jerk / Junction deviation">
          <CompactSettingRow type="number" label="Default jerk" value={settings.default_jerk ?? 0} onChange={(v) => onUpdate('default_jerk', v)} min={0} max={50} step={1} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Outer wall jerk" value={settings.outer_wall_jerk ?? 0} onChange={(v) => onUpdate('outer_wall_jerk', v)} min={0} max={50} step={1} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Inner wall jerk" value={settings.inner_wall_jerk ?? 0} onChange={(v) => onUpdate('inner_wall_jerk', v)} min={0} max={50} step={1} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Infill jerk" value={settings.infill_jerk ?? 0} onChange={(v) => onUpdate('infill_jerk', v)} min={0} max={50} step={1} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Top surface jerk" value={settings.top_surface_jerk ?? 0} onChange={(v) => onUpdate('top_surface_jerk', v)} min={0} max={50} step={1} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Travel jerk" value={settings.travel_jerk ?? 0} onChange={(v) => onUpdate('travel_jerk', v)} min={0} max={50} step={1} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="First layer jerk" value={settings.initial_layer_jerk ?? 0} onChange={(v) => onUpdate('initial_layer_jerk', v)} min={0} max={50} step={1} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Accel to decel" checked={settings.accel_to_decel_enable ?? false} onChange={(v) => onUpdate('accel_to_decel_enable', v)} disabled={disabled} />
          {settings.accel_to_decel_enable && (
            <CompactSettingRow type="number" label="Accel to decel factor" value={settings.accel_to_decel_factor ?? 50} onChange={(v) => onUpdate('accel_to_decel_factor', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          )}
          <CompactSettingRow type="number" label="Junction deviation" value={settings.default_junction_deviation ?? 0.013} onChange={(v) => onUpdate('default_junction_deviation', v)} min={0} max={0.1} step={0.001} unit="mm" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Overhang speed">
          <CompactSettingRow type="checkbox" label="Enable overhang speed" checked={settings.enable_overhang_speed ?? false} onChange={(v) => onUpdate('enable_overhang_speed', v)} disabled={disabled} />
          {settings.enable_overhang_speed && (
            <>
              <CompactSettingRow type="number" label="25% overhang" value={settings.overhang_1_4_speed ?? 0} onChange={(v) => onUpdate('overhang_1_4_speed', v)} min={0} max={200} step={5} unit="mm/s" disabled={disabled} />
              <CompactSettingRow type="number" label="50% overhang" value={settings.overhang_2_4_speed ?? 0} onChange={(v) => onUpdate('overhang_2_4_speed', v)} min={0} max={200} step={5} unit="mm/s" disabled={disabled} />
              <CompactSettingRow type="number" label="75% overhang" value={settings.overhang_3_4_speed ?? 0} onChange={(v) => onUpdate('overhang_3_4_speed', v)} min={0} max={200} step={5} unit="mm/s" disabled={disabled} />
              <CompactSettingRow type="number" label="100% overhang" value={settings.overhang_4_4_speed ?? 0} onChange={(v) => onUpdate('overhang_4_4_speed', v)} min={0} max={200} step={5} unit="mm/s" disabled={disabled} />
            </>
          )}
          <CompactSettingRow type="checkbox" label="Slow down for curled perimeters" checked={settings.slowdown_for_curled_perimeters ?? false} onChange={(v) => onUpdate('slowdown_for_curled_perimeters', v)} disabled={disabled} />
          <CompactSettingRow type="number" label="Slow layers" value={settings.slow_down_layers ?? 0} onChange={(v) => onUpdate('slow_down_layers', v)} min={0} max={20} step={1} disabled={disabled} />
        </SettingSection>
      </>
    )}
  </div>
);

/* ─── Support ─── */
const SupportSettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled, isAdvanced }) => (
  <div className="space-y-4">
    <SettingSection icon={<SupportsIcon className="w-4 h-4" />} title="Support">
      <CompactSettingRow type="checkbox" label="Enable support" checked={settings.enable_support ?? false} onChange={(v) => onUpdate('enable_support', v)} disabled={disabled} />
      {settings.enable_support && (
        <>
          <CompactSettingRow type="select" label="Type" value={settings.support_type ?? 'normal(auto)'} onChange={(v) => onUpdate('support_type', v as 'none' | 'normal(auto)' | 'tree(auto)' | 'normal(manual)' | 'tree(manual)')} options={[{ value: 'normal(auto)', label: 'Normal (Auto)' }, { value: 'tree(auto)', label: 'Tree (Auto)' }, { value: 'normal(manual)', label: 'Normal (Manual)' }, { value: 'tree(manual)', label: 'Tree (Manual)' }]} disabled={disabled} />
          <CompactSettingRow type="number" label="Threshold angle" value={settings.support_threshold_angle ?? 30} onChange={(v) => onUpdate('support_threshold_angle', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="Threshold overlap" value={settings.support_threshold_overlap ?? 0} onChange={(v) => onUpdate('support_threshold_overlap', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="On build plate only" checked={settings.support_on_build_plate_only ?? false} onChange={(v) => onUpdate('support_on_build_plate_only', v)} disabled={disabled} />
        </>
      )}
    </SettingSection>

    {settings.enable_support && (
      <SettingSection icon={<SupportsIcon className="w-4 h-4" />} title="Filament for supports">
        <CompactSettingRow type="number" label="Support/raft base" value={settings.support_filament ?? 0} onChange={(v) => onUpdate('support_filament', v)} min={0} max={10} step={1} disabled={disabled} />
        <CompactSettingRow type="number" label="Support/raft interface" value={settings.support_interface_filament ?? 0} onChange={(v) => onUpdate('support_interface_filament', v)} min={0} max={10} step={1} disabled={disabled} />
      </SettingSection>
    )}

    {settings.enable_support && settings.support_type?.startsWith('tree') && (
      <SettingSection icon={<SupportsIcon className="w-4 h-4" />} title="Tree support brim">
        <CompactSettingRow type="checkbox" label="Auto brim" checked={settings.tree_support_auto_brim ?? true} onChange={(v) => onUpdate('tree_support_auto_brim', v)} disabled={disabled} />
        <CompactSettingRow type="number" label="Tree support brim width" value={settings.tree_support_brim_width ?? 3} onChange={(v) => onUpdate('tree_support_brim_width', v)} min={0} max={20} step={1} unit="mm" disabled={disabled} />
      </SettingSection>
    )}

    {isAdvanced && settings.enable_support && (
      <>
        <SettingSection icon={<SupportsIcon className="w-4 h-4" />} title="Support (advanced)">
          <CompactSettingRow type="select" label="Style" value={settings.support_style ?? 'default'} onChange={(v) => onUpdate('support_style', v as SupportStyle)} options={[{ value: 'default', label: 'Default' }, { value: 'grid', label: 'Grid' }, { value: 'snug', label: 'Snug' }, { value: 'organic', label: 'Organic' }]} disabled={disabled} />
          <CompactSettingRow type="number" label="Top Z distance" value={settings.support_top_z_distance ?? 0.2} onChange={(v) => onUpdate('support_top_z_distance', v)} min={0} max={1} step={0.05} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Bottom Z distance" value={settings.support_bottom_z_distance ?? 0.2} onChange={(v) => onUpdate('support_bottom_z_distance', v)} min={0} max={1} step={0.05} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="X/Y distance" value={settings.support_object_xy_distance ?? 0.6} onChange={(v) => onUpdate('support_object_xy_distance', v)} min={0} max={2} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Top interface layers" value={settings.support_interface_top_layers ?? 2} onChange={(v) => onUpdate('support_interface_top_layers', v)} min={0} max={10} step={1} disabled={disabled} />
          <CompactSettingRow type="number" label="Bottom interface layers" value={settings.support_interface_bottom_layers ?? 0} onChange={(v) => onUpdate('support_interface_bottom_layers', v)} min={0} max={10} step={1} disabled={disabled} />
          <CompactSettingRow type="select" label="Base pattern" value={settings.support_base_pattern ?? 'default'} onChange={(v) => onUpdate('support_base_pattern', v)} options={[{ value: 'default', label: 'Default' }, { value: 'rectilinear', label: 'Rectilinear' }, { value: 'rectilinear_grid', label: 'Grid' }, { value: 'honeycomb', label: 'Honeycomb' }]} disabled={disabled} />
          <CompactSettingRow type="number" label="Base pattern spacing" value={settings.support_base_pattern_spacing ?? 2.5} onChange={(v) => onUpdate('support_base_pattern_spacing', v)} min={0.5} max={10} step={0.5} unit="mm" disabled={disabled} />
          <CompactSettingRow type="select" label="Interface pattern" value={settings.support_interface_pattern ?? 'auto'} onChange={(v) => onUpdate('support_interface_pattern', v)} options={[{ value: 'auto', label: 'Auto' }, { value: 'rectilinear', label: 'Rectilinear' }, { value: 'concentric', label: 'Concentric' }]} disabled={disabled} />
          <CompactSettingRow type="number" label="Top interface spacing" value={settings.support_interface_spacing ?? 0.5} onChange={(v) => onUpdate('support_interface_spacing', v)} min={0} max={5} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Bottom interface spacing" value={settings.support_bottom_interface_spacing ?? 0.5} onChange={(v) => onUpdate('support_bottom_interface_spacing', v)} min={0} max={5} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Expansion" value={settings.support_expansion ?? 0} onChange={(v) => onUpdate('support_expansion', v)} min={0} max={10} step={0.5} unit="mm" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Avoid interface filament for base" checked={settings.support_interface_not_for_body ?? false} onChange={(v) => onUpdate('support_interface_not_for_body', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Interface loop pattern" checked={settings.support_interface_loop_pattern ?? false} onChange={(v) => onUpdate('support_interface_loop_pattern', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Independent layer height" checked={settings.independent_support_layer_height ?? false} onChange={(v) => onUpdate('independent_support_layer_height', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Don't support bridges" checked={settings.bridge_no_support ?? false} onChange={(v) => onUpdate('bridge_no_support', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Ignore small overhangs" checked={settings.support_remove_small_overhang ?? true} onChange={(v) => onUpdate('support_remove_small_overhang', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Critical regions only" checked={settings.support_critical_regions_only ?? false} onChange={(v) => onUpdate('support_critical_regions_only', v)} disabled={disabled} />
          <CompactSettingRow type="number" label="Object first layer gap" value={settings.support_object_first_layer_gap ?? 0} onChange={(v) => onUpdate('support_object_first_layer_gap', v)} min={0} max={2} step={0.1} unit="mm" disabled={disabled} />
        </SettingSection>

        {settings.support_type?.startsWith('tree') && (
          <SettingSection icon={<SupportsIcon className="w-4 h-4" />} title="Tree support (advanced)">
            <CompactSettingRow type="number" label="Branch angle" value={settings.tree_support_branch_angle ?? 40} onChange={(v) => onUpdate('tree_support_branch_angle', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
            <CompactSettingRow type="number" label="Branch diameter" value={settings.tree_support_branch_diameter ?? 5} onChange={(v) => onUpdate('tree_support_branch_diameter', v)} min={1} max={20} step={0.5} unit="mm" disabled={disabled} />
            <CompactSettingRow type="number" label="Branch distance" value={settings.tree_support_branch_distance ?? 5} onChange={(v) => onUpdate('tree_support_branch_distance', v)} min={1} max={20} step={0.5} unit="mm" disabled={disabled} />
            <CompactSettingRow type="number" label="Tip diameter" value={settings.tree_support_tip_diameter ?? 0.8} onChange={(v) => onUpdate('tree_support_tip_diameter', v)} min={0.2} max={5} step={0.1} unit="mm" disabled={disabled} />
            <CompactSettingRow type="number" label="Branch density" value={settings.tree_support_top_rate ?? 30} onChange={(v) => onUpdate('tree_support_top_rate', v)} min={5} max={100} step={5} unit="%" disabled={disabled} />
            <CompactSettingRow type="number" label="Wall loops" value={settings.tree_support_wall_count ?? 0} onChange={(v) => onUpdate('tree_support_wall_count', v)} min={0} max={5} step={1} disabled={disabled} />
            <CompactSettingRow type="checkbox" label="With infill" checked={settings.tree_support_with_infill ?? false} onChange={(v) => onUpdate('tree_support_with_infill', v)} disabled={disabled} />
            <CompactSettingRow type="number" label="Preferred branch angle" value={settings.tree_support_angle_slow ?? 25} onChange={(v) => onUpdate('tree_support_angle_slow', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
            <CompactSettingRow type="number" label="Diameter angle" value={settings.tree_support_branch_diameter_angle ?? 5} onChange={(v) => onUpdate('tree_support_branch_diameter_angle', v)} min={0} max={15} step={1} unit="°" disabled={disabled} />
            <CompactSettingRow type="number" label="Organic branch angle" value={settings.tree_support_branch_angle_organic ?? 40} onChange={(v) => onUpdate('tree_support_branch_angle_organic', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
            <CompactSettingRow type="number" label="Organic branch diameter" value={settings.tree_support_branch_diameter_organic ?? 2} onChange={(v) => onUpdate('tree_support_branch_diameter_organic', v)} min={0.5} max={10} step={0.5} unit="mm" disabled={disabled} />
            <CompactSettingRow type="number" label="Organic branch distance" value={settings.tree_support_branch_distance_organic ?? 1} onChange={(v) => onUpdate('tree_support_branch_distance_organic', v)} min={0.5} max={10} step={0.5} unit="mm" disabled={disabled} />
          </SettingSection>
        )}

        <SettingSection icon={<SupportsIcon className="w-4 h-4" />} title="Support ironing">
          <CompactSettingRow type="checkbox" label="Iron support interface" checked={settings.support_ironing ?? false} onChange={(v) => onUpdate('support_ironing', v)} disabled={disabled} />
          {settings.support_ironing && (
            <>
              <CompactSettingRow type="number" label="Flow" value={settings.support_ironing_flow ?? 15} onChange={(v) => onUpdate('support_ironing_flow', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
              <CompactSettingRow type="number" label="Spacing" value={settings.support_ironing_spacing ?? 0.1} onChange={(v) => onUpdate('support_ironing_spacing', v)} min={0.01} max={1} step={0.01} unit="mm" disabled={disabled} />
              <CompactSettingRow type="select" label="Pattern" value={settings.support_ironing_pattern ?? 'rectilinear'} onChange={(v) => onUpdate('support_ironing_pattern', v)} options={[{ value: 'rectilinear', label: 'Rectilinear' }, { value: 'concentric', label: 'Concentric' }]} disabled={disabled} />
            </>
          )}
        </SettingSection>

        <SettingSection icon={<BedAdhesionIcon className="w-4 h-4" />} title="Raft">
          <CompactSettingRow type="number" label="Raft layers" value={settings.raft_layers ?? 0} onChange={(v) => onUpdate('raft_layers', v)} min={0} max={10} step={1} disabled={disabled} />
          {(settings.raft_layers ?? 0) > 0 && (
            <>
              <CompactSettingRow type="number" label="Contact Z distance" value={settings.raft_contact_distance ?? 0.1} onChange={(v) => onUpdate('raft_contact_distance', v)} min={0} max={1} step={0.05} unit="mm" disabled={disabled} />
              <CompactSettingRow type="number" label="Expansion" value={settings.raft_expansion ?? 1.5} onChange={(v) => onUpdate('raft_expansion', v)} min={0} max={10} step={0.5} unit="mm" disabled={disabled} />
              <CompactSettingRow type="number" label="First layer density" value={settings.raft_first_layer_density ?? 90} onChange={(v) => onUpdate('raft_first_layer_density', v)} min={10} max={100} step={5} unit="%" disabled={disabled} />
              <CompactSettingRow type="number" label="First layer expansion" value={settings.raft_first_layer_expansion ?? 2} onChange={(v) => onUpdate('raft_first_layer_expansion', v)} min={0} max={10} step={0.5} unit="mm" disabled={disabled} />
            </>
          )}
        </SettingSection>
      </>
    )}
  </div>
);

/* ─── Multimaterial ─── */
const MultimaterialSettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled, isAdvanced }) => (
  <div className="space-y-4">
    <SettingSection icon={<LineWidthIcon className="w-4 h-4" />} title="Prime tower">
      <CompactSettingRow type="checkbox" label="Enable" checked={settings.enable_prime_tower ?? true} onChange={(v) => onUpdate('enable_prime_tower', v)} disabled={disabled} />
      {settings.enable_prime_tower && (
        <>
          <CompactSettingRow type="number" label="Width" value={settings.prime_tower_width ?? 30} onChange={(v) => onUpdate('prime_tower_width', v)} min={10} max={100} step={5} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Prime volume" value={settings.prime_volume ?? 50} onChange={(v) => onUpdate('prime_volume', v)} min={10} max={500} step={10} unit="mm³" disabled={disabled} />
        </>
      )}
    </SettingSection>

    <SettingSection icon={<TemperatureIcon className="w-4 h-4" />} title="Flush options">
      <CompactSettingRow type="checkbox" label="Flush into objects' infill" checked={settings.flush_into_infill ?? false} onChange={(v) => onUpdate('flush_into_infill', v)} disabled={disabled} />
      <CompactSettingRow type="checkbox" label="Flush into this object" checked={settings.flush_into_objects ?? false} onChange={(v) => onUpdate('flush_into_objects', v)} disabled={disabled} />
      <CompactSettingRow type="checkbox" label="Flush into objects' support" checked={settings.flush_into_support ?? false} onChange={(v) => onUpdate('flush_into_support', v)} disabled={disabled} />
    </SettingSection>

    {isAdvanced && (
      <>
        <SettingSection icon={<LineWidthIcon className="w-4 h-4" />} title="Filament assignment">
          <div className="space-y-3">
            <p className="text-xs text-pf-text-muted px-3 py-1">
              Assign filament slot numbers for each zone type. 0 means use the active filament.
            </p>
            <CompactSettingRow type="number" label="Wall filament" value={Number(settings.wall_filament ?? 0)} onChange={(v) => onUpdate('wall_filament', String(v))} min={0} max={10} step={1} disabled={disabled} />
            <CompactSettingRow type="number" label="Sparse infill filament" value={Number(settings.sparse_infill_filament ?? 0)} onChange={(v) => onUpdate('sparse_infill_filament', String(v))} min={0} max={10} step={1} disabled={disabled} />
            <CompactSettingRow type="number" label="Solid infill filament" value={Number(settings.solid_infill_filament ?? 0)} onChange={(v) => onUpdate('solid_infill_filament', String(v))} min={0} max={10} step={1} disabled={disabled} />
          </div>
        </SettingSection>
      </>
    )}
  </div>
);

/* ─── Others ─── */
const OtherSettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled, isAdvanced, advancedSettings, onAdvancedSettingsChange }) => (
  <div className="space-y-1">
    <SettingSection icon={<BedAdhesionIcon />} title="Skirt">
      <CompactSettingRow type="number" label="Skirt loops" value={settings.skirt_loops ?? 1} onChange={(v) => onUpdate('skirt_loops', v)} min={0} max={10} step={1} disabled={disabled} />
      <CompactSettingRow type="number" label="Skirt height" value={settings.skirt_height ?? 1} onChange={(v) => onUpdate('skirt_height', v)} min={0} max={10} step={1} disabled={disabled} />
      {isAdvanced && (
        <>
          <CompactSettingRow type="number" label="Skirt distance" value={settings.skirt_distance ?? 6} onChange={(v) => onUpdate('skirt_distance', v)} min={0} max={20} step={1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Skirt speed" value={settings.skirt_speed ?? 50} onChange={(v) => onUpdate('skirt_speed', v)} min={5} max={200} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Skirt start angle" value={settings.skirt_start_angle ?? 0} onChange={(v) => onUpdate('skirt_start_angle', v)} min={0} max={360} step={15} unit="°" disabled={disabled} />
        </>
      )}
    </SettingSection>

    <SettingSection icon={<BedAdhesionIcon />} title="Brim">
      <CompactSettingRow type="select" label="Brim type" value={settings.brim_type ?? 'auto_brim'} onChange={(v) => onUpdate('brim_type', v as BrimType)} options={[{ value: 'no_brim', label: 'No brim' }, { value: 'outer_only', label: 'Outer only' }, { value: 'inner_only', label: 'Inner only' }, { value: 'outer_and_inner', label: 'Outer and inner' }, { value: 'auto_brim', label: 'Auto' }]} disabled={disabled} />
      <CompactSettingRow type="number" label="Brim width" value={settings.brim_width ?? 5} onChange={(v) => onUpdate('brim_width', v)} min={0} max={20} step={1} unit="mm" disabled={disabled} />
      {isAdvanced && (
        <>
          <CompactSettingRow type="number" label="Brim-object gap" value={settings.brim_object_gap ?? 0} onChange={(v) => onUpdate('brim_object_gap', v)} min={0} max={2} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Ear max angle" value={settings.brim_ears_max_angle ?? 125} onChange={(v) => onUpdate('brim_ears_max_angle', v)} min={0} max={180} step={5} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="Ear detection radius" value={settings.brim_ears_detection_length ?? 1} onChange={(v) => onUpdate('brim_ears_detection_length', v)} min={0.5} max={5} step={0.5} unit="mm" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Brim follows compensated outline" checked={settings.brim_use_efc_outline ?? false} onChange={(v) => onUpdate('brim_use_efc_outline', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Combine brims" checked={settings.combine_brims ?? true} onChange={(v) => onUpdate('combine_brims', v)} disabled={disabled} />
        </>
      )}
    </SettingSection>

    <SettingSection icon={<PrecisionIcon />} title="Special mode">
      <CompactSettingRow type="select" label="Print sequence" value={settings.print_sequence ?? 'by_layer'} onChange={(v) => onUpdate('print_sequence', v as 'by_layer' | 'by_object')} options={[{ value: 'by_layer', label: 'By layer' }, { value: 'by_object', label: 'By object' }]} disabled={disabled} />
      <CompactSettingRow type="checkbox" label="Spiral vase" checked={settings.spiral_mode ?? false} onChange={(v) => onUpdate('spiral_mode', v)} disabled={disabled} />
    </SettingSection>

    <SettingSection icon={<PrecisionIcon />} title="Fuzzy skin">
      <CompactSettingRow type="select" label="Fuzzy skin" value={settings.fuzzy_skin ?? 'none'} onChange={(v) => onUpdate('fuzzy_skin', v as FuzzySkinMode)} options={[{ value: 'none', label: 'None' }, { value: 'external', label: 'External' }, { value: 'all', label: 'All walls' }, { value: 'allWalls', label: 'All walls (alternate)' }]} disabled={disabled} />
      {settings.fuzzy_skin && settings.fuzzy_skin !== 'none' && (
        <>
          <CompactSettingRow type="select" label="Fuzzy skin generator mode" value={settings.fuzzy_skin_mode ?? 'none'} onChange={(v) => onUpdate('fuzzy_skin_mode', v as FuzzySkinMode)} options={[{ value: 'none', label: 'None' }, { value: 'external', label: 'External' }, { value: 'all', label: 'All walls' }]} disabled={disabled} />
          <CompactSettingRow type="select" label="Fuzzy skin noise type" value={settings.fuzzy_skin_noise_type ?? 'classic'} onChange={(v) => onUpdate('fuzzy_skin_noise_type', v as FuzzySkinNoiseType)} options={[{ value: 'classic', label: 'Classic' }, { value: 'perlin', label: 'Perlin' }]} disabled={disabled} />
          <CompactSettingRow type="number" label="Fuzzy skin point distance" value={settings.fuzzy_skin_point_distance ?? 0.8} onChange={(v) => onUpdate('fuzzy_skin_point_distance', v)} min={0.1} max={5} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Fuzzy skin thickness" value={settings.fuzzy_skin_thickness ?? 0.3} onChange={(v) => onUpdate('fuzzy_skin_thickness', v)} min={0.05} max={2} step={0.05} unit="mm" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Apply fuzzy skin to first layer" checked={settings.fuzzy_skin_first_layer ?? false} onChange={(v) => onUpdate('fuzzy_skin_first_layer', v)} disabled={disabled} />
          {isAdvanced && (
            <>
              <CompactSettingRow type="number" label="Octaves" value={settings.fuzzy_skin_octaves ?? 4} onChange={(v) => onUpdate('fuzzy_skin_octaves', v)} min={1} max={8} step={1} disabled={disabled} />
              <CompactSettingRow type="number" label="Persistence" value={settings.fuzzy_skin_persistence ?? 0.5} onChange={(v) => onUpdate('fuzzy_skin_persistence', v)} min={0} max={1} step={0.1} disabled={disabled} />
              <CompactSettingRow type="number" label="Scale" value={settings.fuzzy_skin_scale ?? 1} onChange={(v) => onUpdate('fuzzy_skin_scale', v)} min={0.1} max={10} step={0.1} disabled={disabled} />
            </>
          )}
        </>
      )}
    </SettingSection>

    {isAdvanced && (
      <>
        <SettingSection icon={<PrecisionIcon />} title="Slicing">
          <CompactSettingRow type="select" label="Slicing mode" value={settings.slicing_mode ?? 'regular'} onChange={(v) => onUpdate('slicing_mode', v as SlicingMode)} options={[{ value: 'regular', label: 'Regular' }, { value: 'even_odd', label: 'Even-Odd' }, { value: 'close_holes', label: 'Close holes' }]} disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<TemperatureIcon />} title="Temperature">
          <CompactSettingRow type="number" label="Nozzle temperature" value={settings.nozzle_temperature ?? 210} onChange={(v) => onUpdate('nozzle_temperature', v as number)} min={170} max={300} step={5} unit="°C" disabled={disabled} />
          <CompactSettingRow type="number" label="Bed temperature" value={settings.hot_plate_temp ?? 60} onChange={(v) => onUpdate('hot_plate_temp', v as number)} min={0} max={120} step={5} unit="°C" disabled={disabled} />
          <CompactSettingRow type="number" label="First layer nozzle temp" value={settings.nozzle_temperature_initial_layer ?? 215} onChange={(v) => onUpdate('nozzle_temperature_initial_layer', v as number)} min={170} max={300} step={5} unit="°C" disabled={disabled} />
          <CompactSettingRow type="number" label="First layer bed temp" value={settings.hot_plate_temp_initial_layer ?? 65} onChange={(v) => onUpdate('hot_plate_temp_initial_layer', v as number)} min={0} max={120} step={5} unit="°C" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<RetractionIcon />} title="Retraction">
          <CompactSettingRow type="number" label="Retraction length" value={settings.filament_retraction_length ?? 0.8} onChange={(v) => onUpdate('filament_retraction_length', v as number)} min={0} max={10} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Retraction speed" value={settings.filament_retraction_speed ?? 30} onChange={(v) => onUpdate('filament_retraction_speed', v as number)} min={5} max={120} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Deretraction speed" value={settings.filament_deretraction_speed ?? 30} onChange={(v) => onUpdate('filament_deretraction_speed', v as number)} min={5} max={120} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Z lift" value={settings.filament_z_hop ?? 0.2} onChange={(v) => onUpdate('filament_z_hop', v as number)} min={0} max={2} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Minimum travel" value={settings.filament_retraction_minimum_travel ?? 1} onChange={(v) => onUpdate('filament_retraction_minimum_travel', v as number)} min={0} max={10} step={0.5} unit="mm" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Retract on layer change" checked={settings.filament_retract_when_changing_layer ?? false} onChange={(v) => onUpdate('filament_retract_when_changing_layer', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Wipe before retract" checked={settings.filament_retract_before_wipe ?? false} onChange={(v) => onUpdate('filament_retract_before_wipe', v)} disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<CoolingIcon />} title="Cooling">
          <CompactSettingRow type="checkbox" label="Enable fan cooling" checked={settings.fan_cooling ?? true} onChange={(v) => onUpdate('fan_cooling', v)} disabled={disabled} />
          {settings.fan_cooling !== false && (
            <>
              <CompactSettingRow type="number" label="Min fan speed" value={settings.fan_min_speed ?? 35} onChange={(v) => onUpdate('fan_min_speed', v as number)} min={0} max={100} step={5} unit="%" disabled={disabled} />
              <CompactSettingRow type="number" label="Max fan speed" value={settings.fan_max_speed ?? 100} onChange={(v) => onUpdate('fan_max_speed', v as number)} min={0} max={100} step={5} unit="%" disabled={disabled} />
              <CompactSettingRow type="number" label="Full fan at layer" value={settings.full_fan_speed_layer ?? 3} onChange={(v) => onUpdate('full_fan_speed_layer', v as number)} min={1} max={20} step={1} disabled={disabled} />
              <CompactSettingRow type="number" label="Slow down layer time" value={settings.slow_down_layer_time ?? 5} onChange={(v) => onUpdate('slow_down_layer_time', v as number)} min={1} max={60} step={1} unit="s" disabled={disabled} />
              <CompactSettingRow type="number" label="Min print speed" value={settings.slow_down_min_speed ?? 10} onChange={(v) => onUpdate('slow_down_min_speed', v as number)} min={5} max={50} step={5} unit="mm/s" disabled={disabled} />
            </>
          )}
        </SettingSection>

        <SettingSection icon={<PrecisionIcon />} title="Precision">
          <CompactSettingRow type="checkbox" label="Arc fitting" checked={settings.enable_arc_fitting ?? false} onChange={(v) => onUpdate('enable_arc_fitting', v)} disabled={disabled} />
          <CompactSettingRow type="number" label="X-Y hole compensation" value={settings.xy_hole_compensation ?? 0} onChange={(v) => onUpdate('xy_hole_compensation', v as number)} min={-1} max={1} step={0.05} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="X-Y contour compensation" value={settings.xy_contour_compensation ?? 0} onChange={(v) => onUpdate('xy_contour_compensation', v as number)} min={-1} max={1} step={0.05} unit="mm" disabled={disabled} />
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
