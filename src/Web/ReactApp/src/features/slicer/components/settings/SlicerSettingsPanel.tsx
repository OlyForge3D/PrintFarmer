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
      <CompactSettingRow type="number" label="Sparse infill density" value={settings.infillDensity} onChange={(v) => onUpdate('infillDensity', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
      <CompactSettingRow type="select" label="Sparse infill pattern" value={settings.infillPattern} onChange={(v) => onUpdate('infillPattern', v as InfillPattern)} options={infillPatternOptions} disabled={disabled} />
      <CompactSettingRow type="select" label="Internal solid infill pattern" value={settings.internalSolidInfillPattern ?? 'monotonic'} onChange={(v) => onUpdate('internalSolidInfillPattern', v)} options={[{ value: 'monotonic', label: 'Monotonic' }, { value: 'rectilinear', label: 'Rectilinear' }, { value: 'concentric', label: 'Concentric' }, { value: 'hilbertcurve', label: 'Hilbert Curve' }, { value: 'archimedeanChords', label: 'Archimedean Chords' }]} disabled={disabled} />
      {isAdvanced && (
        <>
          <CompactSettingRow type="number" label="Infill/wall overlap" value={settings.infillOverlap ?? 25} onChange={(v) => onUpdate('infillOverlap', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="number" label="Infill anchor max length" value={settings.infillAnchorMaxLength ?? 10} onChange={(v) => onUpdate('infillAnchorMaxLength', v)} min={0} max={50} step={1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Sparse infill direction" value={settings.infillDirection ?? 45} onChange={(v) => onUpdate('infillDirection', v)} min={0} max={360} step={5} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="Solid infill direction" value={settings.solidInfillDirection ?? 0} onChange={(v) => onUpdate('solidInfillDirection', v)} min={0} max={360} step={5} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="Infill combination max layer height" value={settings.infillCombinationMaxLayerHeight ?? 0} onChange={(v) => onUpdate('infillCombinationMaxLayerHeight', v)} min={0} max={1} step={0.05} unit="mm" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Infill combination" checked={settings.infillCombination ?? false} onChange={(v) => onUpdate('infillCombination', v)} disabled={disabled} />
          <CompactSettingRow type="number" label="Minimum sparse infill area" value={settings.minimumSparseInfillArea ?? 15} onChange={(v) => onUpdate('minimumSparseInfillArea', v)} min={0} max={100} step={1} unit="mm²" disabled={disabled} />
          <CompactSettingRow type="select" label="Gap fill" value={settings.gapFillTarget ?? 'everywhere'} onChange={(v) => onUpdate('gapFillTarget', v as GapFillTarget)} options={[{ value: 'everywhere', label: 'Everywhere' }, { value: 'topbottom', label: 'Top/Bottom only' }, { value: 'nowhere', label: 'Nowhere' }]} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Align infill to model" checked={settings.alignInfillDirectionToModel ?? false} onChange={(v) => onUpdate('alignInfillDirectionToModel', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Sparse infill rotation" checked={settings.sparseInfillRotateTemplate ?? false} onChange={(v) => onUpdate('sparseInfillRotateTemplate', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Solid infill rotation" checked={settings.solidInfillRotateTemplate ?? false} onChange={(v) => onUpdate('solidInfillRotateTemplate', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Symmetric infill Y axis" checked={settings.symmetricInfillYAxis ?? false} onChange={(v) => onUpdate('symmetricInfillYAxis', v)} disabled={disabled} />
          <CompactSettingRow type="number" label="Infill lock depth" value={settings.infillLockDepth ?? 0} onChange={(v) => onUpdate('infillLockDepth', v)} min={0} max={10} step={1} disabled={disabled} />
          <CompactSettingRow type="number" label="Infill shift step" value={settings.infillShiftStep ?? 0} onChange={(v) => onUpdate('infillShiftStep', v)} min={0} max={100} step={1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Infill overhang angle" value={settings.infillOverhangAngle ?? 0} onChange={(v) => onUpdate('infillOverhangAngle', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
        </>
      )}
    </SettingSection>

    <SettingSection icon={<WallCountIcon className="w-4 h-4" />} title="Walls & shells">
      <CompactSettingRow type="number" label="Wall loops" value={settings.wallCount} onChange={(v) => onUpdate('wallCount', v)} min={1} max={10} step={1} disabled={disabled} />
      <CompactSettingRow type="number" label="Top shell layers" value={settings.topLayers ?? 4} onChange={(v) => onUpdate('topLayers', v)} min={1} max={10} step={1} disabled={disabled} />
      <CompactSettingRow type="number" label="Bottom shell layers" value={settings.bottomLayers ?? 3} onChange={(v) => onUpdate('bottomLayers', v)} min={1} max={10} step={1} disabled={disabled} />
      <CompactSettingRow type="number" label="Top shell thickness" value={settings.topShellThickness ?? 0.8} onChange={(v) => onUpdate('topShellThickness', v)} min={0} max={5} step={0.1} unit="mm" disabled={disabled} />
      <CompactSettingRow type="number" label="Bottom shell thickness" value={settings.bottomShellThickness ?? 0.8} onChange={(v) => onUpdate('bottomShellThickness', v)} min={0} max={5} step={0.1} unit="mm" disabled={disabled} />
      <CompactSettingRow type="checkbox" label="Fill multiline" checked={settings.fillMultiline ?? false} onChange={(v) => onUpdate('fillMultiline', v)} disabled={disabled} />
      {isAdvanced && (
        <>
          <CompactSettingRow type="checkbox" label="Alternate extra wall" checked={settings.alternateExtraWall ?? false} onChange={(v) => onUpdate('alternateExtraWall', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Detect thin walls" checked={settings.detectThinWall ?? true} onChange={(v) => onUpdate('detectThinWall', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Detect narrow internal solid" checked={settings.detectNarrowInternalSolidInfill ?? true} onChange={(v) => onUpdate('detectNarrowInternalSolidInfill', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Insert solid layers" checked={settings.extraSolidInfills ?? false} onChange={(v) => onUpdate('extraSolidInfills', v)} disabled={disabled} />
          <CompactSettingRow type="number" label="Top/bottom overlap" value={settings.topBottomInfillWallOverlap ?? 25} onChange={(v) => onUpdate('topBottomInfillWallOverlap', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="select" label="Ensure vertical shell" value={settings.ensureVerticalShellThickness ?? 'none'} onChange={(v) => onUpdate('ensureVerticalShellThickness', v)} options={[{ value: 'none', label: 'None' }, { value: 'limited', label: 'Limited' }, { value: 'all', label: 'All' }]} disabled={disabled} />
        </>
      )}
    </SettingSection>

    <SettingSection icon={<InfillDensityIcon className="w-4 h-4" />} title="Top/bottom shells">
      <CompactSettingRow type="number" label="Top surface density" value={settings.topSurfaceDensity ?? 100} onChange={(v) => onUpdate('topSurfaceDensity', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
      <CompactSettingRow type="select" label="Top surface pattern" value={settings.topSurfacePattern ?? 'monotonic'} onChange={(v) => onUpdate('topSurfacePattern', v)} options={[{ value: 'monotonic', label: 'Monotonic' }, { value: 'monotoniclines', label: 'Monotonic lines' }, { value: 'rectilinear', label: 'Rectilinear' }, { value: 'concentric', label: 'Concentric' }]} disabled={disabled} />
      <CompactSettingRow type="number" label="Bottom surface density" value={settings.bottomSurfaceDensity ?? 100} onChange={(v) => onUpdate('bottomSurfaceDensity', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
      <CompactSettingRow type="select" label="Bottom surface pattern" value={settings.bottomSurfacePattern ?? 'monotonic'} onChange={(v) => onUpdate('bottomSurfacePattern', v)} options={[{ value: 'monotonic', label: 'Monotonic' }, { value: 'rectilinear', label: 'Rectilinear' }, { value: 'concentric', label: 'Concentric' }]} disabled={disabled} />
    </SettingSection>

    {isAdvanced && (
      <>
        <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Bridging (strength)">
          <CompactSettingRow type="number" label="External bridge direction" value={settings.bridgeAngle ?? 0} onChange={(v) => onUpdate('bridgeAngle', v)} min={0} max={360} step={5} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="External bridge density" value={settings.bridgeDensity ?? 100} onChange={(v) => onUpdate('bridgeDensity', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="number" label="Internal bridge direction" value={settings.internalBridgeAngle ?? 0} onChange={(v) => onUpdate('internalBridgeAngle', v)} min={0} max={360} step={5} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="Internal bridge density" value={settings.internalBridgeDensity ?? 100} onChange={(v) => onUpdate('internalBridgeDensity', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<InfillDensityIcon className="w-4 h-4" />} title="Advanced infill">
          <CompactSettingRow type="number" label="Skeleton infill density" value={settings.skeletonInfillDensity ?? 0} onChange={(v) => onUpdate('skeletonInfillDensity', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="number" label="Skeleton line width" value={settings.skeletonInfillLineWidth ?? 0.45} onChange={(v) => onUpdate('skeletonInfillLineWidth', v)} min={0.1} max={2} step={0.05} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Skin infill density" value={settings.skinInfillDensity ?? 0} onChange={(v) => onUpdate('skinInfillDensity', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="number" label="Skin infill depth" value={settings.skinInfillDepth ?? 1} onChange={(v) => onUpdate('skinInfillDepth', v)} min={0} max={10} step={1} disabled={disabled} />
          <CompactSettingRow type="number" label="Skin line width" value={settings.skinInfillLineWidth ?? 0.45} onChange={(v) => onUpdate('skinInfillLineWidth', v)} min={0.1} max={2} step={0.05} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Lateral lattice angle 1" value={settings.lateralLatticeAngle1 ?? 45} onChange={(v) => onUpdate('lateralLatticeAngle1', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="Lateral lattice angle 2" value={settings.lateralLatticeAngle2 ?? -45} onChange={(v) => onUpdate('lateralLatticeAngle2', v)} min={-90} max={90} step={5} unit="°" disabled={disabled} />
        </SettingSection>
      </>
    )}
  </div>
);

/* ─── Speed ─── */
const SpeedSettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled, isAdvanced }) => (
  <div className="space-y-4">
    <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Speed">
      {isAdvanced && (
        <CompactSettingRow type="number" label="Print speed" value={settings.printSpeed ?? 100} onChange={(v) => onUpdate('printSpeed', v)} min={10} max={300} step={5} unit="mm/s" disabled={disabled} />
      )}
      <CompactSettingRow type="number" label="Outer wall" value={settings.outerWallSpeed ?? 100} onChange={(v) => onUpdate('outerWallSpeed', v)} min={10} max={200} step={5} unit="mm/s" disabled={disabled} />
      <CompactSettingRow type="number" label="Inner wall" value={settings.innerWallSpeed ?? 150} onChange={(v) => onUpdate('innerWallSpeed', v)} min={10} max={300} step={5} unit="mm/s" disabled={disabled} />
      {isAdvanced && (
        <>
          <CompactSettingRow type="number" label="Sparse infill" value={settings.sparseInfillSpeed ?? 150} onChange={(v) => onUpdate('sparseInfillSpeed', v)} min={10} max={300} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Infill" value={settings.infillSpeed ?? 150} onChange={(v) => onUpdate('infillSpeed', v)} min={10} max={300} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Internal solid infill" value={settings.solidInfillSpeed ?? 120} onChange={(v) => onUpdate('solidInfillSpeed', v)} min={10} max={300} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Top surface" value={settings.topSurfaceSpeed ?? 100} onChange={(v) => onUpdate('topSurfaceSpeed', v)} min={10} max={200} step={5} unit="mm/s" disabled={disabled} />
        </>
      )}
      <CompactSettingRow type="number" label="Travel" value={settings.travelSpeed ?? 150} onChange={(v) => onUpdate('travelSpeed', v)} min={50} max={500} step={10} unit="mm/s" disabled={disabled} />
      <CompactSettingRow type="number" label="First layer" value={settings.firstLayerSpeed ?? 20} onChange={(v) => onUpdate('firstLayerSpeed', v)} min={5} max={60} step={5} unit="mm/s" disabled={disabled} />
    </SettingSection>

    {isAdvanced && (
      <>
        <SettingSection icon={<AccelerationIcon className="w-4 h-4" />} title="Acceleration">
          <CompactSettingRow type="number" label="Normal printing" value={settings.defaultAcceleration ?? 5000} onChange={(v) => onUpdate('defaultAcceleration', v)} min={100} max={20000} step={100} unit="mm/s²" disabled={disabled} />
          <CompactSettingRow type="number" label="Outer wall" value={settings.outerWallAcceleration ?? 2000} onChange={(v) => onUpdate('outerWallAcceleration', v)} min={100} max={10000} step={100} unit="mm/s²" disabled={disabled} />
          <CompactSettingRow type="number" label="Inner wall" value={settings.innerWallAcceleration ?? 5000} onChange={(v) => onUpdate('innerWallAcceleration', v)} min={100} max={20000} step={100} unit="mm/s²" disabled={disabled} />
          <CompactSettingRow type="number" label="Sparse infill" value={settings.infillAcceleration ?? 5000} onChange={(v) => onUpdate('infillAcceleration', v)} min={100} max={20000} step={100} unit="mm/s²" disabled={disabled} />
          <CompactSettingRow type="number" label="Top surface" value={settings.topSurfaceAcceleration ?? 3000} onChange={(v) => onUpdate('topSurfaceAcceleration', v)} min={100} max={20000} step={100} unit="mm/s²" disabled={disabled} />
          <CompactSettingRow type="number" label="Travel" value={settings.travelAcceleration ?? 10000} onChange={(v) => onUpdate('travelAcceleration', v)} min={100} max={30000} step={100} unit="mm/s²" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Additional speeds">
          <CompactSettingRow type="number" label="Bridge" value={settings.bridgeSpeed ?? 25} onChange={(v) => onUpdate('bridgeSpeed', v)} min={5} max={100} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Internal bridge" value={settings.internalBridgeSpeed ?? 25} onChange={(v) => onUpdate('internalBridgeSpeed', v)} min={5} max={150} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Gap infill" value={settings.gapInfillSpeed ?? 30} onChange={(v) => onUpdate('gapInfillSpeed', v)} min={5} max={200} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Support" value={settings.supportSpeed ?? 50} onChange={(v) => onUpdate('supportSpeed', v)} min={5} max={200} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Support interface" value={settings.supportInterfaceSpeed ?? 33} onChange={(v) => onUpdate('supportInterfaceSpeed', v)} min={5} max={200} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Small perimeters" value={settings.smallPerimeterSpeed ?? 50} onChange={(v) => onUpdate('smallPerimeterSpeed', v)} min={5} max={200} step={5} unit="mm/s or %" disabled={disabled} />
          <CompactSettingRow type="number" label="Small perimeter threshold" value={settings.smallPerimeterThreshold ?? 0} onChange={(v) => onUpdate('smallPerimeterThreshold', v)} min={0} max={100} step={5} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Ironing speed" value={settings.filamentIroningSpeed ?? 15} onChange={(v) => onUpdate('filamentIroningSpeed', v)} min={5} max={100} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="First layer travel" value={settings.initialLayerTravelSpeed ?? 50} onChange={(v) => onUpdate('initialLayerTravelSpeed', v)} min={10} max={200} step={5} unit="mm/s" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<AccelerationIcon className="w-4 h-4" />} title="Additional acceleration">
          <CompactSettingRow type="number" label="Bridge" value={settings.bridgeAcceleration ?? 1000} onChange={(v) => onUpdate('bridgeAcceleration', v)} min={100} max={20000} step={100} unit="mm/s²" disabled={disabled} />
          <CompactSettingRow type="number" label="First layer" value={settings.initialLayerAcceleration ?? 1000} onChange={(v) => onUpdate('initialLayerAcceleration', v)} min={100} max={20000} step={100} unit="mm/s²" disabled={disabled} />
          <CompactSettingRow type="number" label="Internal solid infill" value={settings.internalSolidInfillAcceleration ?? 5000} onChange={(v) => onUpdate('internalSolidInfillAcceleration', v)} min={100} max={20000} step={100} unit="mm/s²" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Jerk">
          <CompactSettingRow type="number" label="Default jerk" value={settings.defaultJerk ?? 0} onChange={(v) => onUpdate('defaultJerk', v)} min={0} max={50} step={1} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Outer wall" value={settings.outerWallJerk ?? 0} onChange={(v) => onUpdate('outerWallJerk', v)} min={0} max={50} step={1} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Inner wall" value={settings.innerWallJerk ?? 0} onChange={(v) => onUpdate('innerWallJerk', v)} min={0} max={50} step={1} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Infill" value={settings.infillJerk ?? 0} onChange={(v) => onUpdate('infillJerk', v)} min={0} max={50} step={1} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Top surface" value={settings.topSurfaceJerk ?? 0} onChange={(v) => onUpdate('topSurfaceJerk', v)} min={0} max={50} step={1} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Travel" value={settings.travelJerk ?? 0} onChange={(v) => onUpdate('travelJerk', v)} min={0} max={50} step={1} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="First layer" value={settings.initialLayerJerk ?? 0} onChange={(v) => onUpdate('initialLayerJerk', v)} min={0} max={50} step={1} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Accel to decel" checked={settings.accelToDecelEnable ?? false} onChange={(v) => onUpdate('accelToDecelEnable', v)} disabled={disabled} />
          {settings.accelToDecelEnable && (
            <CompactSettingRow type="number" label="Accel to decel factor" value={settings.accelToDecelFactor ?? 50} onChange={(v) => onUpdate('accelToDecelFactor', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          )}
          <CompactSettingRow type="number" label="Junction deviation" value={settings.defaultJunctionDeviation ?? 0.013} onChange={(v) => onUpdate('defaultJunctionDeviation', v)} min={0} max={0.1} step={0.001} unit="mm" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<SpeedIcon className="w-4 h-4" />} title="Overhang speed">
          <CompactSettingRow type="checkbox" label="Slow down for overhangs" checked={settings.enableOverhangSpeed ?? true} onChange={(v) => onUpdate('enableOverhangSpeed', v)} disabled={disabled} />
          {settings.enableOverhangSpeed !== false && (
            <>
              <CompactSettingRow type="number" label="25% overhang" value={settings.overhang1_4Speed ?? 0} onChange={(v) => onUpdate('overhang1_4Speed', v)} min={0} max={200} step={5} unit="mm/s" disabled={disabled} />
              <CompactSettingRow type="number" label="50% overhang" value={settings.overhang2_4Speed ?? 0} onChange={(v) => onUpdate('overhang2_4Speed', v)} min={0} max={200} step={5} unit="mm/s" disabled={disabled} />
              <CompactSettingRow type="number" label="75% overhang" value={settings.overhang3_4Speed ?? 0} onChange={(v) => onUpdate('overhang3_4Speed', v)} min={0} max={200} step={5} unit="mm/s" disabled={disabled} />
              <CompactSettingRow type="number" label="100% overhang" value={settings.overhang4_4Speed ?? 0} onChange={(v) => onUpdate('overhang4_4Speed', v)} min={0} max={200} step={5} unit="mm/s" disabled={disabled} />
            </>
          )}
          <CompactSettingRow type="checkbox" label="Slow down for curled perimeters" checked={settings.slowdownForCurledPerimeters ?? false} onChange={(v) => onUpdate('slowdownForCurledPerimeters', v)} disabled={disabled} />
          <CompactSettingRow type="number" label="Slow layers" value={settings.slowDownLayers ?? 0} onChange={(v) => onUpdate('slowDownLayers', v)} min={0} max={20} step={1} disabled={disabled} />
        </SettingSection>
      </>
    )}
  </div>
);

/* ─── Support ─── */
const SupportSettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled, isAdvanced }) => (
  <div className="space-y-4">
    <SettingSection icon={<SupportsIcon className="w-4 h-4" />} title="Support">
      <CompactSettingRow type="checkbox" label="Enable support" checked={settings.enableSupports} onChange={(v) => onUpdate('enableSupports', v)} disabled={disabled} />
      {settings.enableSupports && (
        <>
          <CompactSettingRow type="select" label="Type" value={settings.supportType ?? 'normal'} onChange={(v) => onUpdate('supportType', v as 'none' | 'normal' | 'tree' | 'tree_auto')} options={[{ value: 'normal', label: 'Normal' }, { value: 'tree', label: 'Tree' }, { value: 'tree_auto', label: 'Tree (Auto)' }]} disabled={disabled} />
          <CompactSettingRow type="number" label="Threshold angle" value={settings.supportAngle ?? 45} onChange={(v) => onUpdate('supportAngle', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
          <CompactSettingRow type="number" label="Threshold overlap" value={settings.supportThresholdOverlap ?? 0} onChange={(v) => onUpdate('supportThresholdOverlap', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="On build plate only" checked={settings.supportOnBuildPlateOnly ?? false} onChange={(v) => onUpdate('supportOnBuildPlateOnly', v)} disabled={disabled} />
        </>
      )}
    </SettingSection>

    {settings.enableSupports && (
      <SettingSection icon={<SupportsIcon className="w-4 h-4" />} title="Filament for supports">
        <CompactSettingRow type="number" label="Support/raft base" value={settings.supportFilament ?? 0} onChange={(v) => onUpdate('supportFilament', v)} min={0} max={10} step={1} disabled={disabled} />
        <CompactSettingRow type="number" label="Support/raft interface" value={settings.supportInterfaceFilament ?? 0} onChange={(v) => onUpdate('supportInterfaceFilament', v)} min={0} max={10} step={1} disabled={disabled} />
      </SettingSection>
    )}

    {settings.enableSupports && (settings.supportType === 'tree' || settings.supportType === 'tree_auto') && (
      <SettingSection icon={<SupportsIcon className="w-4 h-4" />} title="Tree support brim">
        <CompactSettingRow type="checkbox" label="Auto brim" checked={settings.treeSupportAutoBrim ?? true} onChange={(v) => onUpdate('treeSupportAutoBrim', v)} disabled={disabled} />
        <CompactSettingRow type="number" label="Tree support brim width" value={settings.treeSupportBrimWidth ?? 3} onChange={(v) => onUpdate('treeSupportBrimWidth', v)} min={0} max={20} step={1} unit="mm" disabled={disabled} />
      </SettingSection>
    )}

    {isAdvanced && settings.enableSupports && (
      <>
        <SettingSection icon={<SupportsIcon className="w-4 h-4" />} title="Support (advanced)">
          <CompactSettingRow type="select" label="Style" value={settings.supportStyle ?? 'default'} onChange={(v) => onUpdate('supportStyle', v as SupportStyle)} options={[{ value: 'default', label: 'Default' }, { value: 'grid', label: 'Grid' }, { value: 'snug', label: 'Snug' }, { value: 'organic', label: 'Organic' }]} disabled={disabled} />
          <CompactSettingRow type="number" label="Top Z distance" value={settings.supportTopZDistance ?? 0.2} onChange={(v) => onUpdate('supportTopZDistance', v)} min={0} max={1} step={0.05} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Bottom Z distance" value={settings.supportBottomZDistance ?? 0.2} onChange={(v) => onUpdate('supportBottomZDistance', v)} min={0} max={1} step={0.05} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="X/Y distance" value={settings.supportXYDistance ?? 0.6} onChange={(v) => onUpdate('supportXYDistance', v)} min={0} max={2} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Top interface layers" value={settings.supportInterfaceLayers ?? 2} onChange={(v) => onUpdate('supportInterfaceLayers', v)} min={0} max={10} step={1} disabled={disabled} />
          <CompactSettingRow type="number" label="Bottom interface layers" value={settings.supportInterfaceBottomLayers ?? 0} onChange={(v) => onUpdate('supportInterfaceBottomLayers', v)} min={0} max={10} step={1} disabled={disabled} />
          <CompactSettingRow type="number" label="Density" value={settings.supportDensity ?? 15} onChange={(v) => onUpdate('supportDensity', v)} min={5} max={100} step={5} unit="%" disabled={disabled} />
          <CompactSettingRow type="number" label="Base interface layers" value={settings.supportBaseInterfaceLayers ?? 0} onChange={(v) => onUpdate('supportBaseInterfaceLayers', v)} min={0} max={10} step={1} disabled={disabled} />
          <CompactSettingRow type="select" label="Base pattern" value={settings.supportBasePattern ?? 'default'} onChange={(v) => onUpdate('supportBasePattern', v)} options={[{ value: 'default', label: 'Default' }, { value: 'rectilinear', label: 'Rectilinear' }, { value: 'rectilinear_grid', label: 'Grid' }, { value: 'honeycomb', label: 'Honeycomb' }]} disabled={disabled} />
          <CompactSettingRow type="number" label="Base pattern spacing" value={settings.supportBasePatternSpacing ?? 2.5} onChange={(v) => onUpdate('supportBasePatternSpacing', v)} min={0.5} max={10} step={0.5} unit="mm" disabled={disabled} />
          <CompactSettingRow type="select" label="Interface pattern" value={settings.supportInterfacePattern ?? 'auto'} onChange={(v) => onUpdate('supportInterfacePattern', v)} options={[{ value: 'auto', label: 'Auto' }, { value: 'rectilinear', label: 'Rectilinear' }, { value: 'concentric', label: 'Concentric' }]} disabled={disabled} />
          <CompactSettingRow type="number" label="Top interface spacing" value={settings.supportInterfaceSpacing ?? 0.5} onChange={(v) => onUpdate('supportInterfaceSpacing', v)} min={0} max={5} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Bottom interface spacing" value={settings.supportBottomInterfaceSpacing ?? 0.5} onChange={(v) => onUpdate('supportBottomInterfaceSpacing', v)} min={0} max={5} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Expansion" value={settings.supportExpansion ?? 0} onChange={(v) => onUpdate('supportExpansion', v)} min={0} max={10} step={0.5} unit="mm" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Avoid interface filament for base" checked={settings.supportInterfaceNotForBody ?? false} onChange={(v) => onUpdate('supportInterfaceNotForBody', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Interface loop pattern" checked={settings.supportInterfaceLoopPattern ?? false} onChange={(v) => onUpdate('supportInterfaceLoopPattern', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Independent layer height" checked={settings.independentSupportLayerHeight ?? false} onChange={(v) => onUpdate('independentSupportLayerHeight', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Don't support bridges" checked={settings.bridgeNoSupport ?? false} onChange={(v) => onUpdate('bridgeNoSupport', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Ignore small overhangs" checked={settings.supportRemoveSmallOverhang ?? true} onChange={(v) => onUpdate('supportRemoveSmallOverhang', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Critical regions only" checked={settings.supportCriticalRegionsOnly ?? false} onChange={(v) => onUpdate('supportCriticalRegionsOnly', v)} disabled={disabled} />
          <CompactSettingRow type="number" label="Object first layer gap" value={settings.supportObjectFirstLayerGap ?? 0} onChange={(v) => onUpdate('supportObjectFirstLayerGap', v)} min={0} max={2} step={0.1} unit="mm" disabled={disabled} />
        </SettingSection>

        {(settings.supportType === 'tree' || settings.supportType === 'tree_auto') && (
          <SettingSection icon={<SupportsIcon className="w-4 h-4" />} title="Tree support (advanced)">
            <CompactSettingRow type="number" label="Branch angle" value={settings.treeSupportBranchAngle ?? 40} onChange={(v) => onUpdate('treeSupportBranchAngle', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
            <CompactSettingRow type="number" label="Branch diameter" value={settings.treeSupportBranchDiameter ?? 5} onChange={(v) => onUpdate('treeSupportBranchDiameter', v)} min={1} max={20} step={0.5} unit="mm" disabled={disabled} />
            <CompactSettingRow type="number" label="Branch distance" value={settings.treeSupportBranchDistance ?? 5} onChange={(v) => onUpdate('treeSupportBranchDistance', v)} min={1} max={20} step={0.5} unit="mm" disabled={disabled} />
            <CompactSettingRow type="number" label="Tip diameter" value={settings.treeSupportTipDiameter ?? 0.8} onChange={(v) => onUpdate('treeSupportTipDiameter', v)} min={0.2} max={5} step={0.1} unit="mm" disabled={disabled} />
            <CompactSettingRow type="number" label="Branch density" value={settings.treeSupportTopRate ?? 30} onChange={(v) => onUpdate('treeSupportTopRate', v)} min={5} max={100} step={5} unit="%" disabled={disabled} />
            <CompactSettingRow type="number" label="Wall loops" value={settings.treeSupportWallCount ?? 0} onChange={(v) => onUpdate('treeSupportWallCount', v)} min={0} max={5} step={1} disabled={disabled} />
            <CompactSettingRow type="checkbox" label="With infill" checked={settings.treeSupportWithInfill ?? false} onChange={(v) => onUpdate('treeSupportWithInfill', v)} disabled={disabled} />
            <CompactSettingRow type="number" label="Preferred branch angle" value={settings.treeSupportAngleSlow ?? 25} onChange={(v) => onUpdate('treeSupportAngleSlow', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
            <CompactSettingRow type="number" label="Diameter angle" value={settings.treeSupportBranchDiameterAngle ?? 5} onChange={(v) => onUpdate('treeSupportBranchDiameterAngle', v)} min={0} max={15} step={1} unit="°" disabled={disabled} />
            <CompactSettingRow type="number" label="Organic branch angle" value={settings.treeSupportBranchAngleOrganic ?? 40} onChange={(v) => onUpdate('treeSupportBranchAngleOrganic', v)} min={0} max={90} step={5} unit="°" disabled={disabled} />
            <CompactSettingRow type="number" label="Organic branch diameter" value={settings.treeSupportBranchDiameterOrganic ?? 2} onChange={(v) => onUpdate('treeSupportBranchDiameterOrganic', v)} min={0.5} max={10} step={0.5} unit="mm" disabled={disabled} />
            <CompactSettingRow type="number" label="Organic branch distance" value={settings.treeSupportBranchDistanceOrganic ?? 1} onChange={(v) => onUpdate('treeSupportBranchDistanceOrganic', v)} min={0.5} max={10} step={0.5} unit="mm" disabled={disabled} />
          </SettingSection>
        )}

        <SettingSection icon={<SupportsIcon className="w-4 h-4" />} title="Support ironing">
          <CompactSettingRow type="checkbox" label="Iron support interface" checked={settings.supportIroning ?? false} onChange={(v) => onUpdate('supportIroning', v)} disabled={disabled} />
          {settings.supportIroning && (
            <>
              <CompactSettingRow type="number" label="Flow" value={settings.supportIroningFlow ?? 15} onChange={(v) => onUpdate('supportIroningFlow', v)} min={0} max={100} step={5} unit="%" disabled={disabled} />
              <CompactSettingRow type="number" label="Spacing" value={settings.supportIroningSpacing ?? 0.1} onChange={(v) => onUpdate('supportIroningSpacing', v)} min={0.01} max={1} step={0.01} unit="mm" disabled={disabled} />
              <CompactSettingRow type="select" label="Pattern" value={settings.supportIroningPattern ?? 'rectilinear'} onChange={(v) => onUpdate('supportIroningPattern', v)} options={[{ value: 'rectilinear', label: 'Rectilinear' }, { value: 'concentric', label: 'Concentric' }]} disabled={disabled} />
            </>
          )}
        </SettingSection>

        <SettingSection icon={<BedAdhesionIcon className="w-4 h-4" />} title="Raft">
          <CompactSettingRow type="number" label="Raft layers" value={settings.raftLayers ?? 0} onChange={(v) => onUpdate('raftLayers', v)} min={0} max={10} step={1} disabled={disabled} />
          {(settings.raftLayers ?? 0) > 0 && (
            <>
              <CompactSettingRow type="number" label="Contact Z distance" value={settings.raftContactDistance ?? 0.1} onChange={(v) => onUpdate('raftContactDistance', v)} min={0} max={1} step={0.05} unit="mm" disabled={disabled} />
              <CompactSettingRow type="number" label="Expansion" value={settings.raftExpansion ?? 1.5} onChange={(v) => onUpdate('raftExpansion', v)} min={0} max={10} step={0.5} unit="mm" disabled={disabled} />
              <CompactSettingRow type="number" label="First layer density" value={settings.raftFirstLayerDensity ?? 90} onChange={(v) => onUpdate('raftFirstLayerDensity', v)} min={10} max={100} step={5} unit="%" disabled={disabled} />
              <CompactSettingRow type="number" label="First layer expansion" value={settings.raftFirstLayerExpansion ?? 2} onChange={(v) => onUpdate('raftFirstLayerExpansion', v)} min={0} max={10} step={0.5} unit="mm" disabled={disabled} />
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
      <CompactSettingRow type="checkbox" label="Enable" checked={settings.purgeOnLayerChange ?? true} onChange={(v) => onUpdate('purgeOnLayerChange', v)} disabled={disabled} />
      {settings.purgeOnLayerChange && (
        <>
          <CompactSettingRow type="number" label="Width" value={settings.wipeTowerWidth ?? 30} onChange={(v) => onUpdate('wipeTowerWidth', v)} min={10} max={100} step={5} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Prime volume" value={settings.purgeTowerVolume ?? 50} onChange={(v) => onUpdate('purgeTowerVolume', v)} min={10} max={500} step={10} unit="mm³" disabled={disabled} />
        </>
      )}
    </SettingSection>

    <SettingSection icon={<TemperatureIcon className="w-4 h-4" />} title="Flush options">
      <CompactSettingRow type="checkbox" label="Flush into objects' infill" checked={settings.flushIntoInfill ?? false} onChange={(v) => onUpdate('flushIntoInfill', v)} disabled={disabled} />
      <CompactSettingRow type="checkbox" label="Flush into this object" checked={settings.flushIntoObjects ?? false} onChange={(v) => onUpdate('flushIntoObjects', v)} disabled={disabled} />
      <CompactSettingRow type="checkbox" label="Flush into objects' support" checked={settings.flushIntoSupport ?? false} onChange={(v) => onUpdate('flushIntoSupport', v)} disabled={disabled} />
    </SettingSection>

    {isAdvanced && (
      <>
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
      </>
    )}
  </div>
);

/* ─── Other ─── */
const OtherSettings: React.FC<CategorySettingsProps> = ({ settings, onUpdate, disabled, isAdvanced, advancedSettings, onAdvancedSettingsChange }) => (
  <div className="space-y-1">
    <SettingSection icon={<BedAdhesionIcon />} title="Skirt">
      <CompactSettingRow type="number" label="Skirt loops" value={settings.skirtLoops ?? 1} onChange={(v) => onUpdate('skirtLoops', v)} min={0} max={10} step={1} disabled={disabled} />
      <CompactSettingRow type="number" label="Skirt height" value={settings.skirtHeight ?? 1} onChange={(v) => onUpdate('skirtHeight', v)} min={0} max={10} step={1} disabled={disabled} />
      {isAdvanced && (
        <>
          <CompactSettingRow type="number" label="Skirt distance" value={settings.skirtDistance ?? 6} onChange={(v) => onUpdate('skirtDistance', v)} min={0} max={20} step={1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Skirt speed" value={settings.skirtSpeed ?? 50} onChange={(v) => onUpdate('skirtSpeed', v)} min={5} max={200} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Skirt start angle" value={settings.skirtStartAngle ?? 0} onChange={(v) => onUpdate('skirtStartAngle', v)} min={0} max={360} step={15} unit="°" disabled={disabled} />
        </>
      )}
    </SettingSection>

    <SettingSection icon={<BedAdhesionIcon />} title="Brim">
      <CompactSettingRow type="select" label="Brim type" value={settings.brimType ?? 'auto_brim'} onChange={(v) => onUpdate('brimType', v as BrimType)} options={[{ value: 'no_brim', label: 'No brim' }, { value: 'outer_only', label: 'Outer only' }, { value: 'inner_only', label: 'Inner only' }, { value: 'outer_and_inner', label: 'Outer and inner' }, { value: 'auto_brim', label: 'Auto' }]} disabled={disabled} />
      <CompactSettingRow type="number" label="Brim width" value={settings.brimWidth ?? 5} onChange={(v) => onUpdate('brimWidth', v)} min={0} max={20} step={1} unit="mm" disabled={disabled} />
      {isAdvanced && (
        <>
          <CompactSettingRow type="number" label="Brim-object gap" value={settings.brimObjectGap ?? 0} onChange={(v) => onUpdate('brimObjectGap', v)} min={0} max={2} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Brim ears" checked={settings.brimEars ?? false} onChange={(v) => onUpdate('brimEars', v)} disabled={disabled} />
          {settings.brimEars && (
            <>
              <CompactSettingRow type="number" label="Ear max angle" value={settings.brimEarsMaxAngle ?? 125} onChange={(v) => onUpdate('brimEarsMaxAngle', v)} min={0} max={180} step={5} unit="°" disabled={disabled} />
              <CompactSettingRow type="number" label="Ear detection radius" value={settings.brimEarsDetectionLength ?? 1} onChange={(v) => onUpdate('brimEarsDetectionLength', v)} min={0.5} max={5} step={0.5} unit="mm" disabled={disabled} />
            </>
          )}
          <CompactSettingRow type="checkbox" label="Brim follows compensated outline" checked={settings.brimUseEfcOutline ?? false} onChange={(v) => onUpdate('brimUseEfcOutline', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Combine brims" checked={settings.combineBrims ?? true} onChange={(v) => onUpdate('combineBrims', v)} disabled={disabled} />
        </>
      )}
    </SettingSection>

    <SettingSection icon={<PrecisionIcon />} title="Special mode">
      <CompactSettingRow type="select" label="Print sequence" value={settings.printSequence ?? 'by_layer'} onChange={(v) => onUpdate('printSequence', v as 'by_layer' | 'by_object')} options={[{ value: 'by_layer', label: 'By layer' }, { value: 'by_object', label: 'By object' }]} disabled={disabled} />
      <CompactSettingRow type="checkbox" label="Spiral vase" checked={settings.spiralVase ?? false} onChange={(v) => onUpdate('spiralVase', v)} disabled={disabled} />
    </SettingSection>

    <SettingSection icon={<PrecisionIcon />} title="Fuzzy skin">
      <CompactSettingRow type="checkbox" label="Fuzzy skin" checked={settings.fuzzySkin ?? false} onChange={(v) => onUpdate('fuzzySkin', v)} disabled={disabled} />
      {settings.fuzzySkin && (
        <>
          <CompactSettingRow type="select" label="Fuzzy skin generator mode" value={settings.fuzzySkinMode ?? 'none'} onChange={(v) => onUpdate('fuzzySkinMode', v as FuzzySkinMode)} options={[{ value: 'none', label: 'None' }, { value: 'external', label: 'External' }, { value: 'all', label: 'All walls' }, { value: 'allWalls', label: 'All walls (alternate)' }]} disabled={disabled} />
          <CompactSettingRow type="select" label="Fuzzy skin noise type" value={settings.fuzzySkinNoiseType ?? 'classic'} onChange={(v) => onUpdate('fuzzySkinNoiseType', v as FuzzySkinNoiseType)} options={[{ value: 'classic', label: 'Classic' }, { value: 'perlin', label: 'Perlin' }]} disabled={disabled} />
          <CompactSettingRow type="number" label="Fuzzy skin point distance" value={settings.fuzzySkinPointDistance ?? 0.8} onChange={(v) => onUpdate('fuzzySkinPointDistance', v)} min={0.1} max={5} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Fuzzy skin thickness" value={settings.fuzzySkinThickness ?? 0.3} onChange={(v) => onUpdate('fuzzySkinThickness', v)} min={0.05} max={2} step={0.05} unit="mm" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Apply fuzzy skin to first layer" checked={settings.fuzzySkinFirstLayer ?? false} onChange={(v) => onUpdate('fuzzySkinFirstLayer', v)} disabled={disabled} />
          {isAdvanced && (
            <>
              <CompactSettingRow type="number" label="Octaves" value={settings.fuzzySkinOctaves ?? 4} onChange={(v) => onUpdate('fuzzySkinOctaves', v)} min={1} max={8} step={1} disabled={disabled} />
              <CompactSettingRow type="number" label="Persistence" value={settings.fuzzySkinPersistence ?? 0.5} onChange={(v) => onUpdate('fuzzySkinPersistence', v)} min={0} max={1} step={0.1} disabled={disabled} />
              <CompactSettingRow type="number" label="Scale" value={settings.fuzzySkinScale ?? 1} onChange={(v) => onUpdate('fuzzySkinScale', v)} min={0.1} max={10} step={0.1} disabled={disabled} />
            </>
          )}
        </>
      )}
    </SettingSection>

    {isAdvanced && (
      <>
        <SettingSection icon={<PrecisionIcon />} title="Slicing">
          <CompactSettingRow type="select" label="Slicing mode" value={settings.slicingMode ?? 'regular'} onChange={(v) => onUpdate('slicingMode', v as SlicingMode)} options={[{ value: 'regular', label: 'Regular' }, { value: 'even_odd', label: 'Even-Odd' }, { value: 'close_holes', label: 'Close holes' }]} disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<TemperatureIcon />} title="Temperature">
          <CompactSettingRow type="number" label="Nozzle temperature" value={settings.nozzleTemp ?? 210} onChange={(v) => onUpdate('nozzleTemp', v as number)} min={170} max={300} step={5} unit="°C" disabled={disabled} />
          <CompactSettingRow type="number" label="Bed temperature" value={settings.bedTemp ?? 60} onChange={(v) => onUpdate('bedTemp', v as number)} min={0} max={120} step={5} unit="°C" disabled={disabled} />
          <CompactSettingRow type="number" label="First layer nozzle temp" value={settings.firstLayerNozzleTemp ?? 215} onChange={(v) => onUpdate('firstLayerNozzleTemp', v as number)} min={170} max={300} step={5} unit="°C" disabled={disabled} />
          <CompactSettingRow type="number" label="First layer bed temp" value={settings.firstLayerBedTemp ?? 65} onChange={(v) => onUpdate('firstLayerBedTemp', v as number)} min={0} max={120} step={5} unit="°C" disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<RetractionIcon />} title="Retraction">
          <CompactSettingRow type="number" label="Retraction length" value={settings.retractionLength ?? 0.8} onChange={(v) => onUpdate('retractionLength', v as number)} min={0} max={10} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Retraction speed" value={settings.retractionSpeed ?? 30} onChange={(v) => onUpdate('retractionSpeed', v as number)} min={5} max={120} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Deretraction speed" value={settings.detractionSpeed ?? 30} onChange={(v) => onUpdate('detractionSpeed', v as number)} min={5} max={120} step={5} unit="mm/s" disabled={disabled} />
          <CompactSettingRow type="number" label="Z lift" value={settings.retractionLiftZ ?? 0.2} onChange={(v) => onUpdate('retractionLiftZ', v as number)} min={0} max={2} step={0.1} unit="mm" disabled={disabled} />
          <CompactSettingRow type="number" label="Minimum travel" value={settings.retractionMinimumTravel ?? 1} onChange={(v) => onUpdate('retractionMinimumTravel', v as number)} min={0} max={10} step={0.5} unit="mm" disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Retract on layer change" checked={settings.retractOnLayerChange ?? false} onChange={(v) => onUpdate('retractOnLayerChange', v)} disabled={disabled} />
          <CompactSettingRow type="checkbox" label="Wipe before retract" checked={settings.wipeBeforeRetract ?? false} onChange={(v) => onUpdate('wipeBeforeRetract', v)} disabled={disabled} />
        </SettingSection>

        <SettingSection icon={<CoolingIcon />} title="Cooling">
          <CompactSettingRow type="checkbox" label="Enable fan cooling" checked={settings.enableFanCooling ?? true} onChange={(v) => onUpdate('enableFanCooling', v)} disabled={disabled} />
          {settings.enableFanCooling !== false && (
            <>
              <CompactSettingRow type="number" label="Min fan speed" value={settings.minFanSpeed ?? 35} onChange={(v) => onUpdate('minFanSpeed', v as number)} min={0} max={100} step={5} unit="%" disabled={disabled} />
              <CompactSettingRow type="number" label="Max fan speed" value={settings.maxFanSpeed ?? 100} onChange={(v) => onUpdate('maxFanSpeed', v as number)} min={0} max={100} step={5} unit="%" disabled={disabled} />
              <CompactSettingRow type="number" label="Bridge fan speed" value={settings.bridgeFanSpeed ?? 100} onChange={(v) => onUpdate('bridgeFanSpeed', v as number)} min={0} max={100} step={5} unit="%" disabled={disabled} />
              <CompactSettingRow type="number" label="Full fan at layer" value={settings.fullFanSpeedAtLayer ?? 3} onChange={(v) => onUpdate('fullFanSpeedAtLayer', v as number)} min={1} max={20} step={1} disabled={disabled} />
              <CompactSettingRow type="number" label="Slow down layer time" value={settings.slowDownForLayerTime ?? 5} onChange={(v) => onUpdate('slowDownForLayerTime', v as number)} min={1} max={60} step={1} unit="s" disabled={disabled} />
              <CompactSettingRow type="number" label="Min print speed" value={settings.minPrintSpeed ?? 10} onChange={(v) => onUpdate('minPrintSpeed', v as number)} min={5} max={50} step={5} unit="mm/s" disabled={disabled} />
            </>
          )}
        </SettingSection>

        <SettingSection icon={<IroningIcon />} title="Ironing">
          <CompactSettingRow type="checkbox" label="Enable ironing" checked={settings.enableIroning ?? false} onChange={(v) => onUpdate('enableIroning', v)} disabled={disabled} />
          {settings.enableIroning && (
            <>
              <CompactSettingRow type="select" label="Pattern" value={settings.ironingPattern ?? 'zigzag'} onChange={(v) => onUpdate('ironingPattern', v as 'zigzag' | 'concentric')} options={[{ value: 'zigzag', label: 'Zig-Zag' }, { value: 'concentric', label: 'Concentric' }]} disabled={disabled} />
              <CompactSettingRow type="number" label="Flow rate" value={settings.ironingFlowRate ?? 15} onChange={(v) => onUpdate('ironingFlowRate', v as number)} min={0} max={50} step={5} unit="%" disabled={disabled} />
              <CompactSettingRow type="number" label="Spacing" value={settings.ironingSpacing ?? 0.1} onChange={(v) => onUpdate('ironingSpacing', v as number)} min={0.05} max={0.5} step={0.05} unit="mm" disabled={disabled} />
              <CompactSettingRow type="number" label="Speed" value={settings.ironingSpeed ?? 15} onChange={(v) => onUpdate('ironingSpeed', v as number)} min={5} max={100} step={5} unit="mm/s" disabled={disabled} />
              <CompactSettingRow type="number" label="Angle" value={settings.ironingAngle ?? 45} onChange={(v) => onUpdate('ironingAngle', v as number)} min={0} max={360} step={5} unit="°" disabled={disabled} />
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
