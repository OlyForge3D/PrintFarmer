/**
 * Metadata-driven profile setting renderer.
 *
 * Reads orcaSettingsMetadata.json at build time and renders every field
 * through the existing SettingRow component — zero hand-coded field lists.
 */
import React, { useState, useMemo } from 'react';
import { Button, Textarea } from '@/common/components/ui';
import { SettingRow, SectionHeader, ResetIcon } from './SettingRow';
import { useSlicerViewMode } from '../../hooks/useSlicerViewMode';
import metadata from '../../generated/orcaSettingsMetadata.json';

// ── Metadata type definitions ───────────────────────────────────────────

export interface SettingMetadata {
  key: string;
  type: string;            // bool | float | int | percent | string | enum
  coType: string;           // coFloat | coFloats | coBool | coInt | coString | coEnum | …
  label: string;
  tooltip?: string;
  unit?: string;
  min?: number;
  max?: number;
  mode?: 'simple' | 'advanced' | 'developer';
  default?: string;
  gui_type?: 'color' | 'enum_open';
  enum_values?: string[];
  category?: string;
}

/**
 * Shared infill pattern options — reused by sparse_infill_pattern,
 * top/bottom_surface_pattern, and internal_solid_infill_pattern.
 * Values sourced from OrcaSlicer PrintConfig.cpp s_keys_map_InfillPattern.
 */
const INFILL_PATTERNS: Array<{ value: string; label: string }> = [
  { value: 'rectilinear', label: 'Rectilinear' },
  { value: 'alignedrectilinear', label: 'Aligned Rectilinear' },
  { value: 'monotonic', label: 'Monotonic' },
  { value: 'monotonicline', label: 'Monotonic Lines' },
  { value: 'concentric', label: 'Concentric' },
  { value: 'grid', label: 'Grid' },
  { value: 'triangles', label: 'Triangles' },
  { value: 'tri-hexagon', label: 'Tri-Hexagon' },
  { value: 'cubic', label: 'Cubic' },
  { value: 'adaptivecubic', label: 'Adaptive Cubic' },
  { value: 'quartercubic', label: 'Quarter Cubic' },
  { value: 'supportcubic', label: 'Support Cubic' },
  { value: 'lightning', label: 'Lightning' },
  { value: 'line', label: 'Line' },
  { value: 'honeycomb', label: 'Honeycomb' },
  { value: '3dhoneycomb', label: '3D Honeycomb' },
  { value: 'lateral-honeycomb', label: 'Lateral Honeycomb' },
  { value: 'lateral-lattice', label: 'Lateral Lattice' },
  { value: 'crosshatch', label: 'Cross Hatch' },
  { value: 'zigzag', label: 'Zig-Zag' },
  { value: 'crosszag', label: 'Cross-Zag' },
  { value: 'lockedzag', label: 'Locked-Zag' },
  { value: 'gyroid', label: 'Gyroid' },
  { value: 'hilbertcurve', label: 'Hilbert Curve' },
  { value: 'archimedeanchords', label: 'Archimedean Chords' },
  { value: 'octagramspiral', label: 'Octagram Spiral' },
  { value: 'tpmsd', label: 'TPMS-D' },
  { value: 'tpmsfk', label: 'TPMS-FK' },
];

/** Surface-specific patterns (top/bottom/internal solid) */
const SURFACE_PATTERNS: Array<{ value: string; label: string }> = [
  { value: 'monotonic', label: 'Monotonic' },
  { value: 'monotonicline', label: 'Monotonic Lines' },
  { value: 'concentric', label: 'Concentric' },
  { value: 'rectilinear', label: 'Rectilinear' },
  { value: 'alignedrectilinear', label: 'Aligned Rectilinear' },
  { value: 'hilbertcurve', label: 'Hilbert Curve' },
  { value: 'archimedeanchords', label: 'Archimedean Chords' },
  { value: 'octagramspiral', label: 'Octagram Spiral' },
  { value: 'zigzag', label: 'Zig-Zag' },
];

/** Known enum options for settings that use select dropdowns */
const KNOWN_ENUMS: Record<string, Array<{ value: string; label: string }>> = {
  // ── Machine settings ────────────────────────────────────────────────
  printer_structure: [
    { value: 'undefine', label: 'Undefined' },
    { value: 'corexy', label: 'CoreXY' },
    { value: 'i3', label: 'I3' },
    { value: 'hbot', label: 'Hbot' },
    { value: 'delta', label: 'Delta' },
  ],
  gcode_flavor: [
    { value: 'marlin', label: 'Marlin (legacy)' },
    { value: 'klipper', label: 'Klipper' },
    { value: 'reprapfirmware', label: 'RepRapFirmware' },
    { value: 'marlin2', label: 'Marlin 2' },
    { value: 'reprap', label: 'RepRap/Sprinter' },
    { value: 'repetier', label: 'Repetier' },
    { value: 'smoothie', label: 'Smoothie' },
    { value: 'sailfish', label: 'Sailfish' },
    { value: 'makerware', label: 'MakerWare' },
    { value: 'teacup', label: 'Teacup' },
    { value: 'mach3', label: 'Mach3' },
    { value: 'machinekit', label: 'Machinekit' },
    { value: 'no-extrusion', label: 'No extrusion' },
  ],
  nozzle_type: [
    { value: 'undefine', label: 'Undefined' },
    { value: 'hardened_steel', label: 'Hardened Steel' },
    { value: 'stainless_steel', label: 'Stainless Steel' },
    { value: 'tungsten_carbide', label: 'Tungsten Carbide' },
    { value: 'brass', label: 'Brass' },
  ],
  bed_type: [
    { value: 'Default Plate', label: 'Default Plate' },
    { value: 'SuperTack Plate', label: 'SuperTack Plate' },
    { value: 'Cool Plate', label: 'Cool Plate' },
    { value: 'Engineering Plate', label: 'Engineering Plate' },
    { value: 'High Temp Plate', label: 'High Temp Plate' },
    { value: 'Textured PEI Plate', label: 'Textured PEI Plate' },
    { value: 'Textured Cool Plate', label: 'Textured Cool Plate' },
  ],
  bed_temperature_formula: [
    { value: 'by_first_filament', label: 'By first filament' },
    { value: 'by_highest_temp', label: 'By highest temperature' },
  ],
  enable_power_loss_recovery: [
    { value: 'printer_configuration', label: 'Printer configuration' },
    { value: 'enable', label: 'Enable' },
    { value: 'disable', label: 'Disable' },
  ],
  wipe_tower_type: [
    { value: 'type1', label: 'Normal' },
    { value: 'type2', label: 'Slim' },
  ],
  wipe_tower_wall_type: [
    { value: 'rectangle', label: 'Rectangle' },
    { value: 'cone', label: 'Cone' },
    { value: 'rib', label: 'Rib' },
  ],

  // ── Filament settings ───────────────────────────────────────────────
  filament_type: [
    // ── PLA family ──
    { value: 'PLA', label: 'PLA' },
    { value: 'PLA-AERO', label: 'PLA-AERO' },
    { value: 'PLA-CF', label: 'PLA-CF' },
    // ── ABS family ──
    { value: 'ABS', label: 'ABS' },
    { value: 'ABS-CF', label: 'ABS-CF' },
    { value: 'ABS-GF', label: 'ABS-GF' },
    // ── ASA family ──
    { value: 'ASA', label: 'ASA' },
    { value: 'ASA-AERO', label: 'ASA-AERO' },
    { value: 'ASA-CF', label: 'ASA-CF' },
    { value: 'ASA-GF', label: 'ASA-GF' },
    // ── PET / PETG family ──
    { value: 'PET', label: 'PET' },
    { value: 'PET-CF', label: 'PET-CF' },
    { value: 'PET-GF', label: 'PET-GF' },
    { value: 'PETG', label: 'PETG' },
    { value: 'PETG-CF', label: 'PETG-CF' },
    { value: 'PETG-GF', label: 'PETG-GF' },
    { value: 'PCTG', label: 'PCTG' },
    // ── PA (Nylon) family ──
    { value: 'PA', label: 'PA (Nylon)' },
    { value: 'PA-CF', label: 'PA-CF' },
    { value: 'PA-GF', label: 'PA-GF' },
    { value: 'PA6', label: 'PA6' },
    { value: 'PA6-CF', label: 'PA6-CF' },
    { value: 'PA6-GF', label: 'PA6-GF' },
    { value: 'PA11', label: 'PA11' },
    { value: 'PA11-CF', label: 'PA11-CF' },
    { value: 'PA11-GF', label: 'PA11-GF' },
    { value: 'PA12', label: 'PA12' },
    { value: 'PA12-CF', label: 'PA12-CF' },
    { value: 'PA12-GF', label: 'PA12-GF' },
    { value: 'PAHT', label: 'PAHT' },
    { value: 'PAHT-CF', label: 'PAHT-CF' },
    { value: 'PAHT-GF', label: 'PAHT-GF' },
    // ── PC family ──
    { value: 'PC', label: 'PC' },
    { value: 'PC-ABS', label: 'PC-ABS' },
    { value: 'PC-CF', label: 'PC-CF' },
    { value: 'PC-PBT', label: 'PC-PBT' },
    { value: 'PCL', label: 'PCL' },
    // ── PP / PE family ──
    { value: 'PP', label: 'PP' },
    { value: 'PP-CF', label: 'PP-CF' },
    { value: 'PP-GF', label: 'PP-GF' },
    { value: 'PPA-CF', label: 'PPA-CF' },
    { value: 'PPA-GF', label: 'PPA-GF' },
    { value: 'PE', label: 'PE' },
    { value: 'PE-CF', label: 'PE-CF' },
    { value: 'PE-GF', label: 'PE-GF' },
    // ── PEI family ──
    { value: 'PEI-1010', label: 'PEI-1010' },
    { value: 'PEI-1010-CF', label: 'PEI-1010-CF' },
    { value: 'PEI-1010-GF', label: 'PEI-1010-GF' },
    { value: 'PEI-9085', label: 'PEI-9085' },
    { value: 'PEI-9085-CF', label: 'PEI-9085-CF' },
    { value: 'PEI-9085-GF', label: 'PEI-9085-GF' },
    // ── PEEK / PEKK / PPS high-temp family ──
    { value: 'PEEK', label: 'PEEK' },
    { value: 'PEEK-CF', label: 'PEEK-CF' },
    { value: 'PEEK-GF', label: 'PEEK-GF' },
    { value: 'PEKK', label: 'PEKK' },
    { value: 'PEKK-CF', label: 'PEKK-CF' },
    { value: 'PES', label: 'PES' },
    { value: 'PPS', label: 'PPS' },
    { value: 'PPS-CF', label: 'PPS-CF' },
    { value: 'PPSU', label: 'PPSU' },
    { value: 'PSU', label: 'PSU' },
    // ── Flexible / TPU family ──
    { value: 'TPU', label: 'TPU' },
    { value: 'FLEX', label: 'FLEX' },
    { value: 'EVA', label: 'EVA' },
    { value: 'CoPE', label: 'CoPE' },
    { value: 'SBS', label: 'SBS' },
    // ── Soluble support ──
    { value: 'PVA', label: 'PVA' },
    { value: 'BVOH', label: 'BVOH' },
    { value: 'HIPS', label: 'HIPS' },
    // ── Specialty ──
    { value: 'PHA', label: 'PHA' },
    { value: 'PI', label: 'PI' },
    { value: 'POM', label: 'POM' },
    { value: 'PVB', label: 'PVB' },
    { value: 'PVDF', label: 'PVDF' },
    { value: 'TPI', label: 'TPI' },
  ],
  filament_map_mode: [
    { value: 'Auto For Flush', label: 'Auto (for flush)' },
    { value: 'Auto For Match', label: 'Auto (for match)' },
    { value: 'Manual', label: 'Manual' },
  ],

  // ── Process: Seam & walls ──────────────────────────────────────────
  seam_position: [
    { value: 'nearest', label: 'Nearest' },
    { value: 'aligned', label: 'Aligned' },
    { value: 'aligned_back', label: 'Aligned back' },
    { value: 'back', label: 'Back' },
    { value: 'random', label: 'Random' },
  ],
  seam_slope_type: [
    { value: 'none', label: 'None' },
    { value: 'external', label: 'Contour' },
    { value: 'all', label: 'Contour and hole' },
  ],
  wall_sequence: [
    { value: 'inner wall/outer wall', label: 'Inner/Outer' },
    { value: 'outer wall/inner wall', label: 'Outer/Inner' },
    { value: 'inner-outer-inner wall', label: 'Inner-Outer-Inner' },
  ],
  wall_generator: [
    { value: 'classic', label: 'Classic' },
    { value: 'arachne', label: 'Arachne' },
  ],
  wall_direction: [
    { value: 'ccw', label: 'Counter-clockwise' },
    { value: 'cw', label: 'Clockwise' },
  ],

  // ── Process: Infill patterns ───────────────────────────────────────
  sparse_infill_pattern: INFILL_PATTERNS,
  top_surface_pattern: SURFACE_PATTERNS,
  bottom_surface_pattern: SURFACE_PATTERNS,
  internal_solid_infill_pattern: SURFACE_PATTERNS,

  // ── Process: Ironing ───────────────────────────────────────────────
  ironing_type: [
    { value: 'no ironing', label: 'No ironing' },
    { value: 'top', label: 'Top surfaces' },
    { value: 'topmost', label: 'Topmost surface' },
    { value: 'solid', label: 'All solid surfaces' },
  ],
  ironing_pattern: [
    { value: 'rectilinear', label: 'Rectilinear' },
    { value: 'concentric', label: 'Concentric' },
  ],

  // ── Process: Support ───────────────────────────────────────────────
  support_type: [
    { value: 'normal(auto)', label: 'Normal (auto)' },
    { value: 'tree(auto)', label: 'Tree (auto)' },
    { value: 'normal(manual)', label: 'Normal (manual)' },
    { value: 'tree(manual)', label: 'Tree (manual)' },
  ],
  support_style: [
    { value: 'default', label: 'Default' },
    { value: 'grid', label: 'Grid' },
    { value: 'snug', label: 'Snug' },
    { value: 'organic', label: 'Organic' },
    { value: 'tree_slim', label: 'Tree (slim)' },
    { value: 'tree_strong', label: 'Tree (strong)' },
    { value: 'tree_hybrid', label: 'Tree (hybrid)' },
  ],
  support_base_pattern: [
    { value: 'default', label: 'Default' },
    { value: 'rectilinear', label: 'Rectilinear' },
    { value: 'rectilinear-grid', label: 'Rectilinear Grid' },
    { value: 'honeycomb', label: 'Honeycomb' },
    { value: 'lightning', label: 'Lightning' },
    { value: 'hollow', label: 'Hollow' },
  ],
  support_interface_pattern: [
    { value: 'auto', label: 'Auto' },
    { value: 'rectilinear', label: 'Rectilinear' },
    { value: 'concentric', label: 'Concentric' },
    { value: 'rectilinear_interlaced', label: 'Rectilinear Interlaced' },
    { value: 'grid', label: 'Grid' },
  ],
  support_ironing_pattern: [
    { value: 'rectilinear', label: 'Rectilinear' },
    { value: 'concentric', label: 'Concentric' },
  ],
  support_pillar_connection_mode: [
    { value: 'zigzag', label: 'Zig-zag' },
    { value: 'cross', label: 'Cross' },
    { value: 'dynamic', label: 'Dynamic' },
  ],

  // ── Process: Brim & skirt ──────────────────────────────────────────
  brim_type: [
    { value: 'no_brim', label: 'No brim' },
    { value: 'outer_only', label: 'Outer brim only' },
    { value: 'inner_only', label: 'Inner brim only' },
    { value: 'outer_and_inner', label: 'Outer and inner brim' },
    { value: 'auto_brim', label: 'Auto' },
    { value: 'brim_ears', label: 'Brim ears' },
    { value: 'painted', label: 'Painted' },
  ],
  skirt_type: [
    { value: 'combined', label: 'Combined' },
    { value: 'perobject', label: 'Per object' },
  ],

  // ── Process: Fuzzy skin ────────────────────────────────────────────
  fuzzy_skin: [
    { value: 'none', label: 'None' },
    { value: 'external', label: 'Outside wall' },
    { value: 'all', label: 'All walls' },
    { value: 'allwalls', label: 'All walls (incl. inner)' },
  ],
  fuzzy_skin_mode: [
    { value: 'displacement', label: 'Displacement' },
    { value: 'extrusion', label: 'Extrusion' },
    { value: 'combined', label: 'Combined' },
  ],
  fuzzy_skin_noise_type: [
    { value: 'classic', label: 'Classic' },
    { value: 'perlin', label: 'Perlin' },
    { value: 'billow', label: 'Billow' },
    { value: 'ridgedmulti', label: 'Ridged Multi' },
    { value: 'voronoi', label: 'Voronoi' },
  ],

  // ── Process: Other enums ───────────────────────────────────────────
  slicing_mode: [
    { value: 'regular', label: 'Regular' },
    { value: 'even_odd', label: 'Even-odd' },
    { value: 'close_holes', label: 'Close holes' },
  ],
  print_sequence: [
    { value: 'by layer', label: 'By layer' },
    { value: 'by object', label: 'By object' },
  ],
  print_order: [
    { value: 'default', label: 'Default' },
    { value: 'as_obj_list', label: 'As object list' },
  ],
  timelapse_type: [
    { value: '0', label: 'Traditional' },
    { value: '1', label: 'Smooth' },
  ],
  draft_shield: [
    { value: 'disabled', label: 'Disabled' },
    { value: 'enabled', label: 'Enabled' },
  ],
  counterbore_hole_bridging: [
    { value: 'none', label: 'None' },
    { value: 'partiallybridge', label: 'Partially bridged' },
    { value: 'sacrificiallayer', label: 'Sacrificial layer' },
  ],
  dont_filter_internal_bridges: [
    { value: 'disabled', label: 'Disabled' },
    { value: 'limited', label: 'Limited filtering' },
    { value: 'nofilter', label: 'No filtering' },
  ],
  enable_extra_bridge_layer: [
    { value: 'disabled', label: 'Disabled' },
    { value: 'external_bridge_only', label: 'External bridge only' },
    { value: 'internal_bridge_only', label: 'Internal bridge only' },
    { value: 'apply_to_all', label: 'Apply to all' },
  ],
  ensure_vertical_shell_thickness: [
    { value: 'none', label: 'None' },
    { value: 'ensure_critical_only', label: 'Critical only' },
    { value: 'ensure_moderate', label: 'Moderate' },
    { value: 'ensure_all', label: 'All' },
  ],
  gap_fill_target: [
    { value: 'everywhere', label: 'Everywhere' },
    { value: 'topbottom', label: 'Top and bottom surfaces' },
    { value: 'nowhere', label: 'Nowhere' },
  ],
};

/** Keys that should render as multi-line textareas */
const TEXTAREA_KEYS = new Set([
  'machine_start_gcode', 'machine_end_gcode',
  'machine_pause_gcode', 'template_custom_gcode',
  'change_filament_gcode', 'layer_change_gcode',
  'time_lapse_gcode', 'before_layer_change_gcode',
  'file_start_gcode', 'printing_by_object_gcode',
  'wrapping_detection_gcode', 'change_extrusion_role_gcode',
  'filament_start_gcode', 'filament_end_gcode',
  'adaptive_pressure_advance_model',
  'filament_notes', 'printer_notes',
  'compatible_printers_condition', 'compatible_prints_condition',
]);

/** Keys hidden because they only appear conditionally in OrcaSlicer
 *  (e.g. adaptive PA sub-fields only show when adaptive PA is enabled) */
const CONDITIONAL_HIDDEN_KEYS = new Set([
  'adaptive_pressure_advance_overhangs',
  'adaptive_pressure_advance_bridges',
  'adaptive_pressure_advance_model',
]);

export interface FieldRef {
  key: string;
  compound: boolean;
}

export interface SectionLayout {
  name: string;
  icon: string;
  fields: FieldRef[];
}

export interface TabLayout {
  name: string;
  icon: string;
  sections: SectionLayout[];
}

export interface ProfileTypeMetadata {
  tabs: TabLayout[];
  settings: Record<string, SettingMetadata>;
}

type ProfileType = 'filament' | 'machine' | 'process';
type ViewMode = 'simple' | 'advanced';

// ── Helpers ─────────────────────────────────────────────────────────────

/** Blue-tinted OrcaSlicer section icon */
const OrcaIcon: React.FC<{ icon: string }> = ({ icon }) => (
  <img
    src={`/icons/orca/${icon}.svg`}
    alt=""
    width={16}
    height={16}
    className="shrink-0 filter-[invert(35%)_sepia(90%)_saturate(500%)_hue-rotate(190deg)_brightness(95%)]"
  />
);

/** Map metadata type + gui_type to the SettingRow control type */
function resolveControlType(meta: SettingMetadata): 'checkbox' | 'number' | 'text' | 'color' | 'select' | 'textarea' | 'point' {
  if (meta.gui_type === 'color') return 'color';
  if (TEXTAREA_KEYS.has(meta.key)) return 'textarea';
  if (KNOWN_ENUMS[meta.key] || meta.type === 'enum'
    || (meta.gui_type === 'enum_open' && !['float', 'int', 'percent', 'float_or_percent'].includes(meta.type))
  ) return 'select';
  // coPoints = polygon/multi-point → render as text; coPoint = single X,Y pair
  if (meta.type === 'point' && meta.coType !== 'coPoints') return 'point';
  switch (meta.type) {
    case 'bool':
      return 'checkbox';
    case 'float':
    case 'int':
    case 'percent':
    case 'float_or_percent':
      return 'number';
    default:
      return 'text';
  }
}

/** Coerce raw settings value to a number, falling back to metadata default */
function toNumber(raw: unknown, meta: SettingMetadata): number {
  if (typeof raw === 'number') return raw;
  if (typeof raw === 'string') {
    const n = parseFloat(raw);
    if (!isNaN(n)) return n;
  }
  const d = parseFloat(meta.default ?? '0');
  return isNaN(d) ? 0 : d;
}

function toBool(raw: unknown, meta: SettingMetadata): boolean {
  if (typeof raw === 'boolean') return raw;
  if (typeof raw === 'string') return raw === 'true' || raw === '1';
  return meta.default === 'true';
}

/** Parse a point value "x,y" or "0x0" into [x, y] */
function parsePoint(raw: unknown, meta: SettingMetadata): [number, number] {
  const str = raw != null ? String(raw) : (meta.default ?? '0, 0');
  // Handle "0x0", "0,0", "0, 0" formats
  const parts = str.split(/[x,]\s*/);
  const x = parseFloat(parts[0] ?? '0');
  const y = parseFloat(parts[1] ?? '0');
  return [isNaN(x) ? 0 : x, isNaN(y) ? 0 : y];
}

function toString(raw: unknown, meta: SettingMetadata): string {
  if (raw === undefined || raw === null) return meta.default ?? '';
  if (Array.isArray(raw)) {
    return raw.length > 0 ? raw.join(', ') : (meta.default ?? '');
  }
  return String(raw);
}

// ── MetadataSection ─────────────────────────────────────────────────────

interface MetadataSectionProps {
  section: SectionLayout;
  allSettings: Record<string, SettingMetadata>;
  values: Record<string, unknown>;
  originalValues?: Record<string, unknown>;
  onUpdate: (key: string, value: unknown) => void;
  viewMode: ViewMode;
  disabled: boolean;
}

const MetadataSection: React.FC<MetadataSectionProps> = ({
  section,
  allSettings,
  values,
  originalValues,
  onUpdate,
  viewMode,
  disabled,
}) => {
  // Resolve visible fields: filter by mode and existence in settings dict
  const visibleFields = useMemo(() => {
    return section.fields.filter((f) => {
      const meta = allSettings[f.key];
      if (!meta) return false;
      // Always hide developer-only fields (PrintFarmer has no developer mode)
      if (meta.mode === 'developer') return false;
      if (viewMode === 'simple' && meta.mode === 'advanced') return false;
      // Hide fields that only appear conditionally in OrcaSlicer
      if (CONDITIONAL_HIDDEN_KEYS.has(f.key)) return false;
      return true;
    });
  }, [section.fields, allSettings, viewMode]);

  if (visibleFields.length === 0) return null;

  // Detect paired temperature fields: *_temp_initial_layer + *_temp
  // Build a set of "other layers" keys that are part of a pair (to skip in main loop)
  const pairedOtherKeys = new Set<string>();
  const pairMap = new Map<string, string>(); // initial_layer_key → other_layers_key
  for (let i = 0; i < visibleFields.length - 1; i++) {
    const k = visibleFields[i].key;
    const next = visibleFields[i + 1].key;
    if (k.endsWith('_temp_initial_layer') && next === k.replace('_initial_layer', '')) {
      pairMap.set(k, next);
      pairedOtherKeys.add(next);
    }
  }

  return (
    <div>
      <SectionHeader
        icon={<OrcaIcon icon={section.icon} />}
        title={section.name}
      />
      <div>
        {visibleFields.map((field) => {
          // Skip "other layers" keys that are rendered as part of a pair
          if (pairedOtherKeys.has(field.key)) return null;

          const meta = allSettings[field.key];
          const controlType = resolveControlType(meta);
          // Change tracking: compare current value to original
          const origVal = originalValues?.[field.key];
          const curVal = values[field.key];
          const isModified = originalValues !== undefined && origVal !== undefined && JSON.stringify(curVal) !== JSON.stringify(origVal);
          const resetProps = isModified ? {
            isModified: true,
            originalValue: origVal,
            onReset: () => onUpdate(field.key, origVal),
          } : {};

          // Paired temperature row: render both "First layer" and "Other layers" on the same line
          const otherKey = pairMap.get(field.key);
          if (otherKey) {
            const otherMeta = allSettings[otherKey];
            const otherOrigVal = originalValues?.[otherKey];
            const otherCurVal = values[otherKey];
            const otherIsModified = originalValues !== undefined && otherOrigVal !== undefined && JSON.stringify(otherCurVal) !== JSON.stringify(otherOrigVal);
            const anyModified = isModified || otherIsModified;
            // Derive plate name from key: "cool_plate_temp_initial_layer" → "Cool Plate"
            const plateName = field.key
              .replace('_temp_initial_layer', '')
              .replace(/_/g, ' ')
              .replace(/\b\w/g, (c) => c.toUpperCase())
              .replace('Supertack', 'SuperTack')
              .replace('Eng', 'Engineering')
              .replace('Hot', 'Smooth PEI / High Temp');
            return (
              <div key={field.key} className="flex items-center gap-1.5 py-0.5">
                <div className="w-2/5 shrink-0 truncate">
                  <span
                    className={`text-xs font-medium ${anyModified ? 'text-pf-warning' : 'text-pf-text'}`}
                    title={meta.tooltip}
                  >
                    {plateName}
                  </span>
                </div>
                <div className="w-[30%] shrink-0 flex items-center gap-1">
                  <span className="text-[10px] text-pf-text-muted whitespace-nowrap">First layer</span>
                  <div className="flex items-center flex-1">
                    <input
                      type="number"
                      title={`${plateName} first layer`}
                      className={`w-full py-1 px-2 bg-pf-panel border border-pf-border text-pf-text text-xs text-right rounded-l-lg rounded-r-none border-r-0 hover:border-pf-border-light focus:border-pf-accent-2 focus:outline-hidden`}
                      value={toNumber(values[field.key], meta)}
                      onChange={(e) => onUpdate(field.key, Number(e.target.value))}
                      disabled={disabled}
                    />
                    <span className="text-xs text-pf-text-muted px-1.5 bg-pf-border rounded-r-lg w-8 shrink-0 self-stretch flex items-center">°C</span>
                  </div>
                </div>
                <div className="w-[30%] shrink-0 flex items-center gap-1">
                  <span className="text-[10px] text-pf-text-muted whitespace-nowrap">Other layers</span>
                  <div className="flex items-center flex-1">
                    <input
                      type="number"
                      title={`${plateName} other layers`}
                      className={`w-full py-1 px-2 bg-pf-panel border border-pf-border text-pf-text text-xs text-right rounded-l-lg rounded-r-none border-r-0 hover:border-pf-border-light focus:border-pf-accent-2 focus:outline-hidden`}
                      value={toNumber(values[otherKey], otherMeta)}
                      onChange={(e) => onUpdate(otherKey, Number(e.target.value))}
                      disabled={disabled}
                    />
                    <span className="text-xs text-pf-text-muted px-1.5 bg-pf-border rounded-r-lg w-8 shrink-0 self-stretch flex items-center">°C</span>
                  </div>
                </div>
              </div>
            );
          }

          switch (controlType) {
            case 'checkbox':
              return (
                <SettingRow
                  key={field.key}
                  type="checkbox"
                  label={meta.label}
                  tooltip={meta.tooltip}
                  checked={toBool(values[field.key], meta)}
                  onChange={(v) => onUpdate(field.key, v)}
                  disabled={disabled}
                  {...resetProps}
                />
              );
            case 'number':
              return (
                <SettingRow
                  key={field.key}
                  type="number"
                  label={meta.label}
                  tooltip={meta.tooltip}
                  value={toNumber(values[field.key], meta)}
                  min={meta.min}
                  max={meta.max}
                  step={meta.type === 'int' ? 1 : 0.01}
                  unit={meta.type === 'float_or_percent' ? 'mm or %' : meta.unit}
                  onChange={(v) => onUpdate(field.key, v)}
                  disabled={disabled}
                  {...resetProps}
                />
              );
            case 'color':
              return (
                <SettingRow
                  key={field.key}
                  type="color"
                  label={meta.label}
                  tooltip={meta.tooltip}
                  value={toString(values[field.key], meta)}
                  onChange={(v) => onUpdate(field.key, v)}
                  disabled={disabled}
                  {...resetProps}
                />
              );
            case 'select': {
              const options = KNOWN_ENUMS[field.key]
                ?? meta.enum_values?.map((v: string) => ({ value: v, label: v }))
                ?? [];
              return (
                <SettingRow
                  key={field.key}
                  type="select"
                  label={meta.label}
                  tooltip={meta.tooltip}
                  value={toString(values[field.key], meta)}
                  options={options}
                  onChange={(v) => onUpdate(field.key, v)}
                  disabled={disabled}
                  {...resetProps}
                />
              );
            }
            case 'textarea': {
              const showLabel = visibleFields.length > 1;
              return (
                <div key={field.key} className="py-0.5">
                  {(showLabel || isModified) && (
                    <div className="flex items-center gap-1.5 mb-1">
                      {showLabel && (
                        <span
                          className={`text-xs ${isModified ? 'text-pf-warning font-medium' : 'text-pf-text-secondary'}`}
                          title={meta.tooltip}
                        >
                          {meta.label}
                        </span>
                      )}
                      {isModified && (
                        <Button
                          variant="subtle"
                          type="button"
                          onClick={() => onUpdate(field.key, origVal)}
                          className="p-0.5 text-pf-warning hover:text-pf-warning transition-colors hover:bg-pf-warning/10 rounded shrink-0"
                          title="Reset to original"
                          aria-label={`Reset ${meta.label} to original value`}
                        >
                          <ResetIcon className="w-4 h-4" />
                        </Button>
                      )}
                    </div>
                  )}
                  <Textarea
                    rows={8}
                    value={toString(values[field.key], meta)}
                    onChange={(e) => onUpdate(field.key, e.target.value)}
                    disabled={disabled}
                    className="font-mono text-sm"
                  />
                </div>
              );
            }
            case 'point': {
              const [px, py] = parsePoint(values[field.key], meta);
              return (
                <div key={field.key} className="flex items-center gap-1.5 py-0.5">
                  <div className="flex items-center gap-1.5 w-2/5 shrink-0">
                    <span
                      className={`text-xs truncate ${isModified ? 'text-pf-warning font-medium' : 'text-pf-text-secondary'}`}
                      title={meta.tooltip}
                    >
                      {meta.label}
                    </span>
                  </div>
                  <div className="flex items-center gap-1.5 w-[30%] shrink-0">
                    <div className="flex-1 flex items-center bg-pf-panel border border-pf-border rounded overflow-hidden">
                      <span className="px-1.5 text-xs text-pf-text-muted select-none">X</span>
                      <input
                        type="number"
                        title={`${meta.label} X`}
                        className="flex-1 px-1 py-1 text-xs text-right bg-transparent border-none outline-none"
                        value={px}
                        onChange={(e) => onUpdate(field.key, `${e.target.value},${py}`)}
                        disabled={disabled}
                      />
                    </div>
                    <div className="flex-1 flex items-center bg-pf-panel border border-pf-border rounded overflow-hidden">
                      <span className="px-1.5 text-xs text-pf-text-muted select-none">Y</span>
                      <input
                        type="number"
                        title={`${meta.label} Y`}
                        className="flex-1 px-1 py-1 text-xs text-right bg-transparent border-none outline-none"
                        value={py}
                        onChange={(e) => onUpdate(field.key, `${px},${e.target.value}`)}
                        disabled={disabled}
                      />
                    </div>
                  </div>
                  <div className="w-7 shrink-0 flex justify-center">
                    {isModified && (
                      <Button
                        variant="subtle"
                        type="button"
                        onClick={() => onUpdate(field.key, origVal)}
                        className="p-0.5 text-pf-warning hover:text-pf-warning transition-colors hover:bg-pf-warning/10 rounded"
                        title="Reset to original"
                        aria-label={`Reset ${meta.label} to original value`}
                      >
                        <ResetIcon className="w-4 h-4" />
                      </Button>
                    )}
                  </div>
                </div>
              );
            }
            default:
              return (
                <SettingRow
                  key={field.key}
                  type="text"
                  label={meta.label}
                  tooltip={meta.tooltip}
                  value={toString(values[field.key], meta)}
                  onChange={(v) => onUpdate(field.key, v)}
                  disabled={disabled}
                  {...resetProps}
                />
              );
          }
        })}
      </div>
    </div>
  );
};

// ── MetadataTab ─────────────────────────────────────────────────────────

interface MetadataTabProps {
  tab: TabLayout;
  allSettings: Record<string, SettingMetadata>;
  values: Record<string, unknown>;
  originalValues?: Record<string, unknown>;
  onUpdate: (key: string, value: unknown) => void;
  viewMode: ViewMode;
  disabled: boolean;
}

const MetadataTab: React.FC<MetadataTabProps> = ({
  tab,
  allSettings,
  values,
  originalValues,
  onUpdate,
  viewMode,
  disabled,
}) => (
  <div className="space-y-1">
    {tab.sections.map((section) => (
      <MetadataSection
        key={section.name}
        section={section}
        allSettings={allSettings}
        values={values}
        originalValues={originalValues}
        onUpdate={onUpdate}
        viewMode={viewMode}
        disabled={disabled}
      />
    ))}
  </div>
);

// ── MetadataProfileEditor (top-level) ───────────────────────────────────

export interface MetadataProfileEditorProps {
  profileType: ProfileType;
  settings: Record<string, unknown>;
  originalSettings?: Record<string, unknown>;
  onUpdate: (key: string, value: unknown) => void;
  disabled?: boolean;
  className?: string;
}

export const MetadataProfileEditor: React.FC<MetadataProfileEditorProps> = ({
  profileType,
  settings,
  originalSettings,
  onUpdate,
  disabled = false,
  className = '',
}) => {
  const profileMeta = (metadata as unknown as Record<string, ProfileTypeMetadata>)[profileType];
  const [viewMode, toggleViewMode] = useSlicerViewMode();
  const [activeTabIdx, setActiveTabIdx] = useState(0);

  // Filter tabs to only show those with visible fields in the current view mode
  const visibleTabs = useMemo(() => {
    return profileMeta.tabs.filter((tab) => {
      // A tab is visible if any section has any visible field
      return tab.sections.some((section) =>
        section.fields.some((field) => {
          const meta = profileMeta.settings[field.key];
          if (!meta) return false;
          // Always hide developer-only fields
          if (meta.mode === 'developer') return false;
          // In simple mode, hide advanced fields
          if (viewMode === 'simple' && meta.mode === 'advanced') return false;
          return true;
        })
      );
    });
  }, [profileMeta.tabs, profileMeta.settings, viewMode]);

  // Clamp activeTabIdx when visibleTabs changes (e.g., switching from Advanced to Simple)
  const clampedActiveTabIdx = Math.min(activeTabIdx, Math.max(0, visibleTabs.length - 1));
  const activeTab = visibleTabs[clampedActiveTabIdx] ?? visibleTabs[0];

  return (
    <div className={`bg-pf-bg-1 rounded-lg border border-pf-border flex flex-col ${className}`}>
      {/* Tab bar + Advanced toggle */}
      <div className="flex items-center justify-between px-4 py-2 border-b border-pf-border">
        <div className="flex gap-1 overflow-x-auto">
          {visibleTabs.map((tab, idx) => (
            <Button
              key={tab.name}
              variant="unstyled"
              type="button"
              size="sm"
              onClick={() => setActiveTabIdx(idx)}
              disabled={disabled}
              className={`px-2 py-0.5 text-[10px] font-medium rounded-full whitespace-nowrap
                ${idx === clampedActiveTabIdx
                  ? 'bg-pf-accent-2/15 text-pf-accent-2 ring-1 ring-pf-accent-2/40'
                  : 'text-pf-text-secondary hover:text-pf-text-primary'}`}
            >
              {tab.name}
            </Button>
          ))}
        </div>

        {/* Advanced toggle — matches MachineProfileEditor pattern */}
        <Button
          variant="unstyled"
          type="button"
          onClick={toggleViewMode}
          disabled={disabled}
          className="shrink-0 ml-2 p-0.5 rounded transition-colors hover:bg-pf-bg-2 disabled:opacity-50"
          title={viewMode === 'simple' ? 'Show advanced parameters' : 'Hide advanced parameters'}
          aria-label={`Switch to ${viewMode === 'simple' ? 'Advanced' : 'Simple'} mode`}
        >
          <span className="inline-flex items-center gap-1.5">
            <img src="/icons/orcaslicer-advanced.svg" alt="" className="w-4 h-4" />
            <span
              className={`relative inline-block w-7 h-3.5 rounded-full transition-colors ${
                viewMode === 'advanced' ? 'bg-pf-accent-2' : 'bg-pf-border'
              }`}
            >
              <span
                className={`absolute top-0.5 w-2.5 h-2.5 rounded-full bg-white shadow-sm transition-all ${
                  viewMode === 'advanced' ? 'left-3.5' : 'left-0.5'
                }`}
              />
            </span>
          </span>
        </Button>
      </div>

      {/* Active tab content */}
      <div className="p-2 flex-1 min-h-0 overflow-y-auto">
        <MetadataTab
          tab={activeTab}
          allSettings={profileMeta.settings}
          values={settings}
          originalValues={originalSettings}
          onUpdate={onUpdate}
          viewMode={viewMode}
          disabled={disabled}
        />
      </div>
    </div>
  );
};
