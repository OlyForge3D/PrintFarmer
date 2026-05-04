/**
 * Shared type definitions, constants, and helpers for the metadata-driven
 * profile setting renderer.
 *
 * Extracted from MetadataProfileRenderer.tsx so that MetadataSettingRow,
 * MetadataSection, and MetadataTabRenderer can each be imported standalone.
 */
import { getInfillIcon } from '@/features/slicer/components/settings/InfillPatternIcons';

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

export interface FieldRef {
  key: string;
  compound: boolean;
  compound_label?: string;
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

export type ProfileType = 'filament' | 'machine' | 'process';
export type ViewMode = 'simple' | 'advanced';

// ── Infill pattern option lists ─────────────────────────────────────────

/**
 * Shared infill pattern options — reused by sparse_infill_pattern,
 * top/bottom_surface_pattern, and internal_solid_infill_pattern.
 * Values sourced from OrcaSlicer PrintConfig.cpp s_keys_map_InfillPattern.
 */
export const INFILL_PATTERNS: Array<{ value: string; label: string; icon?: React.ReactNode }> = [
  { value: 'rectilinear', label: 'Rectilinear', icon: getInfillIcon('rectilinear') },
  { value: 'alignedrectilinear', label: 'Aligned Rectilinear', icon: getInfillIcon('alignedrectilinear') },
  { value: 'monotonic', label: 'Monotonic', icon: getInfillIcon('monotonic') },
  { value: 'monotonicline', label: 'Monotonic Lines', icon: getInfillIcon('monotonicline') },
  { value: 'concentric', label: 'Concentric', icon: getInfillIcon('concentric') },
  { value: 'grid', label: 'Grid', icon: getInfillIcon('grid') },
  { value: 'triangles', label: 'Triangles', icon: getInfillIcon('triangles') },
  { value: 'tri-hexagon', label: 'Tri-Hexagon', icon: getInfillIcon('tri-hexagon') },
  { value: 'cubic', label: 'Cubic', icon: getInfillIcon('cubic') },
  { value: 'adaptivecubic', label: 'Adaptive Cubic', icon: getInfillIcon('adaptivecubic') },
  { value: 'quartercubic', label: 'Quarter Cubic', icon: getInfillIcon('quartercubic') },
  { value: 'supportcubic', label: 'Support Cubic', icon: getInfillIcon('supportcubic') },
  { value: 'lightning', label: 'Lightning', icon: getInfillIcon('lightning') },
  { value: 'line', label: 'Line', icon: getInfillIcon('line') },
  { value: 'honeycomb', label: 'Honeycomb', icon: getInfillIcon('honeycomb') },
  { value: '3dhoneycomb', label: '3D Honeycomb', icon: getInfillIcon('3dhoneycomb') },
  { value: 'lateral-honeycomb', label: 'Lateral Honeycomb', icon: getInfillIcon('lateral-honeycomb') },
  { value: 'lateral-lattice', label: 'Lateral Lattice', icon: getInfillIcon('lateral-lattice') },
  { value: 'crosshatch', label: 'Cross Hatch', icon: getInfillIcon('crosshatch') },
  { value: 'zigzag', label: 'Zig-Zag', icon: getInfillIcon('zigzag') },
  { value: 'crosszag', label: 'Cross-Zag', icon: getInfillIcon('crosszag') },
  { value: 'lockedzag', label: 'Locked-Zag', icon: getInfillIcon('lockedzag') },
  { value: 'gyroid', label: 'Gyroid', icon: getInfillIcon('gyroid') },
  { value: 'hilbertcurve', label: 'Hilbert Curve', icon: getInfillIcon('hilbertcurve') },
  { value: 'archimedeanchords', label: 'Archimedean Chords', icon: getInfillIcon('archimedeanchords') },
  { value: 'octagramspiral', label: 'Octagram Spiral', icon: getInfillIcon('octagramspiral') },
  { value: 'tpmsd', label: 'TPMS-D', icon: getInfillIcon('tpmsd') },
  { value: 'tpmsfk', label: 'TPMS-FK', icon: getInfillIcon('tpmsfk') },
];

/** Surface-specific patterns (top/bottom/internal solid) */
export const SURFACE_PATTERNS: Array<{ value: string; label: string; icon?: React.ReactNode }> = [
  { value: 'monotonic', label: 'Monotonic', icon: getInfillIcon('monotonic') },
  { value: 'monotonicline', label: 'Monotonic Lines', icon: getInfillIcon('monotonicline') },
  { value: 'concentric', label: 'Concentric', icon: getInfillIcon('concentric') },
  { value: 'rectilinear', label: 'Rectilinear', icon: getInfillIcon('rectilinear') },
  { value: 'alignedrectilinear', label: 'Aligned Rectilinear', icon: getInfillIcon('alignedrectilinear') },
  { value: 'hilbertcurve', label: 'Hilbert Curve', icon: getInfillIcon('hilbertcurve') },
  { value: 'archimedeanchords', label: 'Archimedean Chords', icon: getInfillIcon('archimedeanchords') },
  { value: 'octagramspiral', label: 'Octagram Spiral', icon: getInfillIcon('octagramspiral') },
  { value: 'zigzag', label: 'Zig-Zag', icon: getInfillIcon('zigzag') },
];

/** Known enum options for settings that use select dropdowns */
export const KNOWN_ENUMS: Record<string, Array<{ value: string; label: string; icon?: React.ReactNode }>> = {
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
export const TEXTAREA_KEYS = new Set([
  'machine_start_gcode', 'machine_end_gcode',
  'machine_pause_gcode', 'template_custom_gcode',
  'change_filament_gcode', 'layer_change_gcode',
  'time_lapse_gcode', 'before_layer_change_gcode',
  'file_start_gcode', 'printing_by_object_gcode',
  'wrapping_detection_gcode', 'change_extrusion_role_gcode',
  'filament_start_gcode', 'filament_end_gcode',
  'adaptive_pressure_advance_model',
  'filament_notes', 'printer_notes',
  'filename_format', 'notes',
  'compatible_printers_condition', 'compatible_prints_condition',
]);

/** Keys hidden because they only appear conditionally in OrcaSlicer */
export const CONDITIONAL_HIDDEN_KEYS = new Set([
  'adaptive_pressure_advance_overhangs',
  'adaptive_pressure_advance_bridges',
  'adaptive_pressure_advance_model',
]);

// ── Helpers ─────────────────────────────────────────────────────────────

/** Map metadata type + gui_type to the SettingRow control type */
export function resolveControlType(meta: SettingMetadata): 'checkbox' | 'number' | 'text' | 'color' | 'select' | 'textarea' | 'point' | 'coFloats' {
  if (meta.gui_type === 'color') return 'color';
  if (TEXTAREA_KEYS.has(meta.key)) return 'textarea';
  if (KNOWN_ENUMS[meta.key] || meta.type === 'enum'
    || (meta.gui_type === 'enum_open' && !['float', 'int', 'percent', 'float_or_percent'].includes(meta.type))
  ) return 'select';
  // coPoints = polygon/multi-point → render as text; coPoint = single X,Y pair
  if (meta.type === 'point' && meta.coType !== 'coPoints') return 'point';
  // coFloats = multi-extruder array (e.g. "500,200")
  if (meta.coType === 'coFloats') return 'coFloats';
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
export function toNumber(raw: unknown, meta: SettingMetadata): number {
  if (typeof raw === 'number') return raw;
  if (typeof raw === 'string') {
    const n = parseFloat(raw);
    if (!isNaN(n)) return n;
  }
  const d = parseFloat(meta.default ?? '0');
  return isNaN(d) ? 0 : d;
}

export function toBool(raw: unknown, meta: SettingMetadata): boolean {
  if (typeof raw === 'boolean') return raw;
  if (typeof raw === 'string') return raw === 'true' || raw === '1';
  return meta.default === 'true';
}

/** Parse a point value "x,y" or "0x0" into [x, y] */
export function parsePoint(raw: unknown, meta: SettingMetadata): [number, number] {
  const str = raw != null ? String(raw) : (meta.default ?? '0, 0');
  const parts = str.split(/[x,]\s*/);
  const x = parseFloat(parts[0] ?? '0');
  const y = parseFloat(parts[1] ?? '0');
  return [isNaN(x) ? 0 : x, isNaN(y) ? 0 : y];
}

/** Parse a coFloats value "500,200" or "500., 200" into an array of numbers */
export function parseCoFloats(raw: unknown, meta: SettingMetadata): number[] {
  const str = raw != null ? String(raw) : (meta.default ?? '0');
  const parts = str.split(',').map((s) => s.trim());
  return parts.map((p) => {
    const n = parseFloat(p);
    return isNaN(n) ? 0 : n;
  });
}

export function toString(raw: unknown, meta: SettingMetadata): string {
  if (raw === undefined || raw === null) return meta.default ?? '';
  if (Array.isArray(raw)) {
    return raw.length > 0 ? raw.join(', ') : (meta.default ?? '');
  }
  return String(raw);
}
