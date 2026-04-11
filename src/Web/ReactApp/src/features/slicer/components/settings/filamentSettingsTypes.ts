/**
 * OrcaSlicer Filament settings type definitions
 * Uses native OrcaSlicer snake_case property names aligned with the SimplyPrint filament catalog.
 */

/** View modes for filament settings panel complexity */
export type FilamentSettingsViewMode = 'simple' | 'advanced';

/** Category tabs matching SimplyPrint / OrcaSlicer filament profile tabs */
export type FilamentCategory =
  | 'filament'
  | 'cooling'
  | 'setting_overrides'
  | 'advanced'
  | 'multimaterial'
  | 'dependencies'
  | 'notes';

/**
 * OrcaSlicer filament profile settings — flat interface using native snake_case keys.
 * All properties are optional; use DEFAULT_ORCA_FILAMENT_SETTINGS for sensible defaults.
 * Properties not present in the SimplyPrint catalog are grouped at the bottom and
 * mapped to 'advanced' mode.
 */
export interface OrcaFilamentSettings {
  // ── Profile / header fields (deduplicated — appear once, shown on every tab) ──
  name?: string;
  default_material_type?: string;
  for_material_types?: string;
  for_nozzle_size?: number;
  for_nozzle_type?: string;
  for_nozzle_volume_type?: string;

  // ── Filament tab ─────────────────────────────────────────────────────────────
  filament_type?: string;
  filament_vendor?: string;
  filament_soluble?: boolean;
  filament_is_support?: boolean;
  filament_change_length?: number;
  required_nozzle_HRC?: number;
  default_filament_colour?: string;
  filament_diameter?: number;
  filament_adhesiveness_category?: number;
  filament_density?: number;
  filament_shrink?: number;
  filament_shrinkage_compensation_z?: number;
  temperature_vitrification?: number;
  idle_temperature?: number;
  nozzle_temperature_range_low?: number;
  nozzle_temperature_range_high?: number;
  pellet_flow_coefficient?: number;
  filament_flow_ratio?: number;
  enable_pressure_advance?: boolean;
  pressure_advance?: number;
  adaptive_pressure_advance?: boolean;
  adaptive_pressure_advance_overhangs?: boolean;
  adaptive_pressure_advance_bridges?: number;
  adaptive_pressure_advance_model?: string;
  chamber_temperature?: number;
  activate_chamber_temp_control?: boolean;
  nozzle_temperature_initial_layer?: number;
  nozzle_temperature?: number;
  hot_plate_temp_initial_layer?: number;
  hot_plate_temp?: number;
  filament_adaptive_volumetric_speed?: boolean;
  filament_max_volumetric_speed?: number;

  // ── Cooling tab ──────────────────────────────────────────────────────────────
  close_fan_the_first_x_layers?: number;
  full_fan_speed_layer?: number;
  fan_min_speed?: number;
  fan_cooling_layer_time?: number;
  fan_max_speed?: number;
  slow_down_layer_time?: number;
  reduce_fan_stop_start_freq?: boolean;
  slow_down_for_layer_cooling?: boolean;
  dont_slow_down_outer_wall?: boolean;
  slow_down_min_speed?: number;
  enable_overhang_bridge_fan?: boolean;
  overhang_fan_speed?: number;
  internal_bridge_fan_speed?: number;
  support_material_interface_fan_speed?: number;
  ironing_fan_speed?: number;
  additional_cooling_fan_speed?: number;
  activate_air_filtration?: boolean;
  during_print_exhaust_fan_speed?: number;
  complete_print_exhaust_fan_speed?: number;

  // ── Setting Overrides tab ────────────────────────────────────────────────────
  filament_retraction_length?: number;
  filament_z_hop?: number;
  filament_retract_lift_above?: number;
  filament_retract_lift_below?: number;
  filament_retraction_speed?: number;
  filament_deretraction_speed?: number;
  filament_retract_restart_extra?: number;
  filament_retraction_minimum_travel?: number;
  filament_retract_when_changing_layer?: boolean;
  filament_wipe?: boolean;
  filament_wipe_distance?: number;
  filament_retract_before_wipe?: number;
  filament_long_retractions_when_cut?: boolean;
  filament_retraction_distances_when_cut?: number;
  filament_ironing_flow?: number;
  filament_ironing_spacing?: number;
  filament_ironing_inset?: number;
  filament_ironing_speed?: number;

  // ── Multimaterial tab ────────────────────────────────────────────────────────
  filament_minimal_purge_on_wipe_tower?: number;
  filament_tower_interface_pre_extrusion_dist?: number;
  filament_tower_interface_pre_extrusion_length?: number;
  filament_tower_ironing_area?: number;
  filament_tower_interface_purge_volume?: number;
  filament_tower_interface_print_temp?: number;
  long_retractions_when_ec?: boolean;
  retraction_distances_when_ec?: number;
  filament_loading_speed_start?: number;
  filament_loading_speed?: number;
  filament_unloading_speed_start?: number;
  filament_unloading_speed?: number;
  filament_toolchange_delay?: number;
  filament_cooling_moves?: number;
  filament_cooling_initial_speed?: number;
  filament_cooling_final_speed?: number;
  filament_stamping_loading_speed?: number;
  filament_stamping_distance?: number;
  filament_ramming_parameters?: string;
  filament_multitool_ramming?: boolean;
  filament_multitool_ramming_volume?: number;
  filament_multitool_ramming_flow?: number;

  // ── Dependencies tab ─────────────────────────────────────────────────────────
  compatible_printers?: string[];
  compatible_printers_condition?: string;

  // ── Notes tab ────────────────────────────────────────────────────────────────
  filament_notes?: string;

  // ── Advanced extras (not in SimplyPrint catalog) ─────────────────────────────
  cost?: number;                              // cost per kg
  pressure_advance_smooth_time?: number;      // seconds
  fan_cooling?: boolean;                      // explicit fan enable flag
  close_loop_fan_power?: number;              // closed-loop fan power 0-100%
  enable_volumetric_extrusion?: boolean;
  filament_load_time?: number;                // seconds
  filament_unload_time?: number;              // seconds
  filament_start_gcode?: string;
  filament_end_gcode?: string;
  outer_wall_flow_ratio?: number;
  inner_wall_flow_ratio?: number;
  top_solid_infill_flow_ratio?: number;
  bottom_solid_infill_flow_ratio?: number;
  internal_solid_infill_flow_ratio?: number;
  sparse_infill_flow_ratio?: number;
  gap_fill_flow_ratio?: number;
  support_flow_ratio?: number;
  support_interface_flow_ratio?: number;
  overhang_flow_ratio?: number;
  first_layer_flow_ratio?: number;
  set_other_flow_ratios?: boolean;
  fan_kickstart?: number;                     // seconds
  fan_speedup_time?: number;                  // seconds
  fan_speedup_overhangs?: boolean;
  overhang_fan_threshold?: string;
  interlocking_beam?: boolean;
  interlocking_beam_layer_count?: number;
  interlocking_beam_width?: number;           // mm
  interlocking_boundary_avoidance?: number;   // mm
  interlocking_depth?: number;                // mm
  interlocking_orientation?: number;          // degrees
  mmu_segmented_region_max_width?: number;    // mm
  mmu_segmented_region_interlocking_depth?: number; // mm
  wipe_tower_interface_flow_ratio?: number;
  wipe_tower_interface_speed?: number;        // mm/s
  bed_temperature_formula?: string;
  filament_flush_temp?: number;               // °C
  filament_flush_volumetric_speed?: number;   // mm³/s
  slow_down_layers?: number;
}

/** Sensible OrcaSlicer defaults for a generic PLA filament */
export const DEFAULT_ORCA_FILAMENT_SETTINGS: Partial<OrcaFilamentSettings> = {
  filament_type: 'PLA',
  filament_diameter: 1.75,
  filament_density: 1.24,
  filament_flow_ratio: 1.0,
  nozzle_temperature: 200,
  nozzle_temperature_initial_layer: 210,
  hot_plate_temp: 60,
  hot_plate_temp_initial_layer: 65,
  chamber_temperature: 0,
  enable_pressure_advance: false,
  pressure_advance: 0.04,
  pressure_advance_smooth_time: 0.04,
  filament_max_volumetric_speed: 12,
  close_fan_the_first_x_layers: 1,
  full_fan_speed_layer: 3,
  fan_min_speed: 35,
  fan_max_speed: 100,
  fan_cooling_layer_time: 10,
  slow_down_layer_time: 5,
  slow_down_min_speed: 10,
  enable_overhang_bridge_fan: true,
  overhang_fan_speed: 100,
  filament_retraction_length: 0.8,
  filament_z_hop: 0.2,
  filament_retraction_speed: 30,
  filament_deretraction_speed: 30,
  filament_retraction_minimum_travel: 1,
  cost: 16,
};

/** Material presets for quick selection */
export const MATERIAL_PRESETS: Record<string, Partial<OrcaFilamentSettings>> = {
  PLA: {
    filament_type: 'PLA',
    filament_density: 1.24,
    cost: 16,
    nozzle_temperature: 210,
    hot_plate_temp: 60,
    nozzle_temperature_initial_layer: 215,
    hot_plate_temp_initial_layer: 65,
    fan_max_speed: 100,
    chamber_temperature: 0,
  },
  PETG: {
    filament_type: 'PETG',
    filament_density: 1.27,
    cost: 18,
    nozzle_temperature: 240,
    hot_plate_temp: 80,
    nozzle_temperature_initial_layer: 245,
    hot_plate_temp_initial_layer: 85,
    fan_max_speed: 50,
    chamber_temperature: 0,
  },
  ABS: {
    filament_type: 'ABS',
    filament_density: 1.04,
    cost: 16,
    nozzle_temperature: 250,
    hot_plate_temp: 100,
    nozzle_temperature_initial_layer: 255,
    hot_plate_temp_initial_layer: 105,
    fan_max_speed: 0,
    chamber_temperature: 45,
  },
  ASA: {
    filament_type: 'ASA',
    filament_density: 1.07,
    cost: 22,
    nozzle_temperature: 255,
    hot_plate_temp: 100,
    nozzle_temperature_initial_layer: 260,
    hot_plate_temp_initial_layer: 105,
    fan_max_speed: 0,
    chamber_temperature: 45,
  },
  TPU: {
    filament_type: 'TPU',
    filament_density: 1.21,
    cost: 24,
    nozzle_temperature: 225,
    hot_plate_temp: 50,
    nozzle_temperature_initial_layer: 230,
    hot_plate_temp_initial_layer: 55,
    fan_max_speed: 50,
    filament_retraction_length: 0.4,
    pressure_advance: 0.06,
  },
  'PA-CF': {
    filament_type: 'PA-CF',
    filament_density: 1.15,
    cost: 50,
    nozzle_temperature: 280,
    hot_plate_temp: 90,
    nozzle_temperature_initial_layer: 285,
    hot_plate_temp_initial_layer: 95,
    fan_max_speed: 0,
    chamber_temperature: 55,
    filament_max_volumetric_speed: 8,
  },
};

/**
 * Maps each OrcaFilamentSettings key to its display mode.
 * 'simple' keys are shown when the user selects Basic view;
 * 'advanced' keys are hidden until Advanced view is selected.
 */
export const ORCA_FILAMENT_MODE_MAP: Record<string, 'simple' | 'advanced'> = {
  // Profile / header
  name: 'simple',
  default_material_type: 'simple',
  for_material_types: 'simple',
  for_nozzle_size: 'simple',
  for_nozzle_type: 'simple',
  for_nozzle_volume_type: 'simple',

  // Filament tab — simple
  filament_type: 'simple',
  required_nozzle_HRC: 'simple',
  filament_diameter: 'simple',
  filament_adhesiveness_category: 'simple',
  temperature_vitrification: 'simple',
  idle_temperature: 'simple',
  nozzle_temperature_range_low: 'simple',
  nozzle_temperature_range_high: 'simple',
  pellet_flow_coefficient: 'simple',
  enable_pressure_advance: 'simple',
  chamber_temperature: 'simple',
  activate_chamber_temp_control: 'simple',
  nozzle_temperature_initial_layer: 'simple',
  nozzle_temperature: 'simple',
  hot_plate_temp_initial_layer: 'simple',
  hot_plate_temp: 'simple',
  filament_adaptive_volumetric_speed: 'simple',

  // Filament tab — advanced only
  filament_vendor: 'advanced',
  filament_soluble: 'advanced',
  filament_is_support: 'advanced',
  filament_change_length: 'advanced',
  default_filament_colour: 'advanced',
  filament_density: 'advanced',
  filament_shrink: 'advanced',
  filament_shrinkage_compensation_z: 'advanced',
  filament_flow_ratio: 'advanced',
  pressure_advance: 'advanced',
  adaptive_pressure_advance: 'advanced',
  adaptive_pressure_advance_overhangs: 'advanced',
  adaptive_pressure_advance_bridges: 'advanced',
  adaptive_pressure_advance_model: 'advanced',
  filament_max_volumetric_speed: 'advanced',

  // Cooling tab — simple
  close_fan_the_first_x_layers: 'simple',
  fan_min_speed: 'simple',
  fan_cooling_layer_time: 'simple',
  fan_max_speed: 'simple',
  slow_down_layer_time: 'simple',
  reduce_fan_stop_start_freq: 'simple',
  slow_down_for_layer_cooling: 'simple',
  dont_slow_down_outer_wall: 'simple',
  enable_overhang_bridge_fan: 'simple',
  additional_cooling_fan_speed: 'simple',
  activate_air_filtration: 'simple',
  during_print_exhaust_fan_speed: 'simple',
  complete_print_exhaust_fan_speed: 'simple',

  // Cooling tab — advanced only
  full_fan_speed_layer: 'advanced',
  slow_down_min_speed: 'advanced',
  overhang_fan_speed: 'advanced',
  internal_bridge_fan_speed: 'advanced',
  support_material_interface_fan_speed: 'advanced',
  ironing_fan_speed: 'advanced',

  // Setting Overrides tab — simple
  filament_retraction_length: 'simple',
  filament_z_hop: 'simple',
  filament_long_retractions_when_cut: 'simple',
  filament_retraction_distances_when_cut: 'simple',

  // Setting Overrides tab — advanced only
  filament_retract_lift_above: 'advanced',
  filament_retract_lift_below: 'advanced',
  filament_retraction_speed: 'advanced',
  filament_deretraction_speed: 'advanced',
  filament_retract_restart_extra: 'advanced',
  filament_retraction_minimum_travel: 'advanced',
  filament_retract_when_changing_layer: 'advanced',
  filament_wipe: 'advanced',
  filament_wipe_distance: 'advanced',
  filament_retract_before_wipe: 'advanced',
  filament_ironing_flow: 'advanced',
  filament_ironing_spacing: 'advanced',
  filament_ironing_inset: 'advanced',
  filament_ironing_speed: 'advanced',

  // Multimaterial tab — all advanced
  filament_minimal_purge_on_wipe_tower: 'advanced',
  filament_tower_interface_pre_extrusion_dist: 'advanced',
  filament_tower_interface_pre_extrusion_length: 'advanced',
  filament_tower_ironing_area: 'advanced',
  filament_tower_interface_purge_volume: 'advanced',
  filament_tower_interface_print_temp: 'advanced',
  long_retractions_when_ec: 'advanced',
  retraction_distances_when_ec: 'advanced',
  filament_loading_speed_start: 'advanced',
  filament_loading_speed: 'advanced',
  filament_unloading_speed_start: 'advanced',
  filament_unloading_speed: 'advanced',
  filament_toolchange_delay: 'advanced',
  filament_cooling_moves: 'advanced',
  filament_cooling_initial_speed: 'advanced',
  filament_cooling_final_speed: 'advanced',
  filament_stamping_loading_speed: 'advanced',
  filament_stamping_distance: 'advanced',
  filament_ramming_parameters: 'advanced',
  filament_multitool_ramming: 'advanced',
  filament_multitool_ramming_volume: 'advanced',
  filament_multitool_ramming_flow: 'advanced',

  // Dependencies / Notes / Advanced extras — all advanced
  compatible_printers: 'advanced',
  compatible_printers_condition: 'advanced',
  filament_notes: 'advanced',
  cost: 'advanced',
  pressure_advance_smooth_time: 'advanced',
  fan_cooling: 'advanced',
  close_loop_fan_power: 'advanced',
  enable_volumetric_extrusion: 'advanced',
  filament_load_time: 'advanced',
  filament_unload_time: 'advanced',
  filament_start_gcode: 'advanced',
  filament_end_gcode: 'advanced',
  outer_wall_flow_ratio: 'advanced',
  inner_wall_flow_ratio: 'advanced',
  top_solid_infill_flow_ratio: 'advanced',
  bottom_solid_infill_flow_ratio: 'advanced',
  internal_solid_infill_flow_ratio: 'advanced',
  sparse_infill_flow_ratio: 'advanced',
  gap_fill_flow_ratio: 'advanced',
  support_flow_ratio: 'advanced',
  support_interface_flow_ratio: 'advanced',
  overhang_flow_ratio: 'advanced',
  first_layer_flow_ratio: 'advanced',
  set_other_flow_ratios: 'advanced',
  fan_kickstart: 'advanced',
  fan_speedup_time: 'advanced',
  fan_speedup_overhangs: 'advanced',
  overhang_fan_threshold: 'advanced',
  interlocking_beam: 'advanced',
  interlocking_beam_layer_count: 'advanced',
  interlocking_beam_width: 'advanced',
  interlocking_boundary_avoidance: 'advanced',
  interlocking_depth: 'advanced',
  interlocking_orientation: 'advanced',
  mmu_segmented_region_max_width: 'advanced',
  mmu_segmented_region_interlocking_depth: 'advanced',
  wipe_tower_interface_flow_ratio: 'advanced',
  wipe_tower_interface_speed: 'advanced',
  bed_temperature_formula: 'advanced',
  filament_flush_temp: 'advanced',
  filament_flush_volumetric_speed: 'advanced',
  slow_down_layers: 'advanced',
};

/**
 * Maps each OrcaFilamentSettings key to its UI category tab.
 */
export const ORCA_FILAMENT_CATEGORY_MAP: Record<string, FilamentCategory> = {
  // Profile / header — shown on every tab, canonical home is 'filament'
  name: 'filament',
  default_material_type: 'filament',
  for_material_types: 'filament',
  for_nozzle_size: 'filament',
  for_nozzle_type: 'filament',
  for_nozzle_volume_type: 'filament',

  // Filament tab
  filament_type: 'filament',
  filament_vendor: 'filament',
  filament_soluble: 'filament',
  filament_is_support: 'filament',
  filament_change_length: 'filament',
  required_nozzle_HRC: 'filament',
  default_filament_colour: 'filament',
  filament_diameter: 'filament',
  filament_adhesiveness_category: 'filament',
  filament_density: 'filament',
  filament_shrink: 'filament',
  filament_shrinkage_compensation_z: 'filament',
  temperature_vitrification: 'filament',
  idle_temperature: 'filament',
  nozzle_temperature_range_low: 'filament',
  nozzle_temperature_range_high: 'filament',
  pellet_flow_coefficient: 'filament',
  filament_flow_ratio: 'filament',
  enable_pressure_advance: 'filament',
  pressure_advance: 'filament',
  adaptive_pressure_advance: 'filament',
  adaptive_pressure_advance_overhangs: 'filament',
  adaptive_pressure_advance_bridges: 'filament',
  adaptive_pressure_advance_model: 'filament',
  chamber_temperature: 'filament',
  activate_chamber_temp_control: 'filament',
  nozzle_temperature_initial_layer: 'filament',
  nozzle_temperature: 'filament',
  hot_plate_temp_initial_layer: 'filament',
  hot_plate_temp: 'filament',
  filament_adaptive_volumetric_speed: 'filament',
  filament_max_volumetric_speed: 'filament',

  // Cooling tab
  close_fan_the_first_x_layers: 'cooling',
  full_fan_speed_layer: 'cooling',
  fan_min_speed: 'cooling',
  fan_cooling_layer_time: 'cooling',
  fan_max_speed: 'cooling',
  slow_down_layer_time: 'cooling',
  reduce_fan_stop_start_freq: 'cooling',
  slow_down_for_layer_cooling: 'cooling',
  dont_slow_down_outer_wall: 'cooling',
  slow_down_min_speed: 'cooling',
  enable_overhang_bridge_fan: 'cooling',
  overhang_fan_speed: 'cooling',
  internal_bridge_fan_speed: 'cooling',
  support_material_interface_fan_speed: 'cooling',
  ironing_fan_speed: 'cooling',
  additional_cooling_fan_speed: 'cooling',
  activate_air_filtration: 'cooling',
  during_print_exhaust_fan_speed: 'cooling',
  complete_print_exhaust_fan_speed: 'cooling',

  // Setting Overrides tab
  filament_retraction_length: 'setting_overrides',
  filament_z_hop: 'setting_overrides',
  filament_retract_lift_above: 'setting_overrides',
  filament_retract_lift_below: 'setting_overrides',
  filament_retraction_speed: 'setting_overrides',
  filament_deretraction_speed: 'setting_overrides',
  filament_retract_restart_extra: 'setting_overrides',
  filament_retraction_minimum_travel: 'setting_overrides',
  filament_retract_when_changing_layer: 'setting_overrides',
  filament_wipe: 'setting_overrides',
  filament_wipe_distance: 'setting_overrides',
  filament_retract_before_wipe: 'setting_overrides',
  filament_long_retractions_when_cut: 'setting_overrides',
  filament_retraction_distances_when_cut: 'setting_overrides',
  filament_ironing_flow: 'setting_overrides',
  filament_ironing_spacing: 'setting_overrides',
  filament_ironing_inset: 'setting_overrides',
  filament_ironing_speed: 'setting_overrides',

  // Multimaterial tab
  filament_minimal_purge_on_wipe_tower: 'multimaterial',
  filament_tower_interface_pre_extrusion_dist: 'multimaterial',
  filament_tower_interface_pre_extrusion_length: 'multimaterial',
  filament_tower_ironing_area: 'multimaterial',
  filament_tower_interface_purge_volume: 'multimaterial',
  filament_tower_interface_print_temp: 'multimaterial',
  long_retractions_when_ec: 'multimaterial',
  retraction_distances_when_ec: 'multimaterial',
  filament_loading_speed_start: 'multimaterial',
  filament_loading_speed: 'multimaterial',
  filament_unloading_speed_start: 'multimaterial',
  filament_unloading_speed: 'multimaterial',
  filament_toolchange_delay: 'multimaterial',
  filament_cooling_moves: 'multimaterial',
  filament_cooling_initial_speed: 'multimaterial',
  filament_cooling_final_speed: 'multimaterial',
  filament_stamping_loading_speed: 'multimaterial',
  filament_stamping_distance: 'multimaterial',
  filament_ramming_parameters: 'multimaterial',
  filament_multitool_ramming: 'multimaterial',
  filament_multitool_ramming_volume: 'multimaterial',
  filament_multitool_ramming_flow: 'multimaterial',

  // Dependencies tab
  compatible_printers: 'dependencies',
  compatible_printers_condition: 'dependencies',

  // Notes tab
  filament_notes: 'notes',

  // Advanced extras (not in SimplyPrint catalog)
  cost: 'advanced',
  pressure_advance_smooth_time: 'advanced',
  fan_cooling: 'advanced',
  close_loop_fan_power: 'advanced',
  enable_volumetric_extrusion: 'advanced',
  filament_load_time: 'advanced',
  filament_unload_time: 'advanced',
  filament_start_gcode: 'advanced',
  filament_end_gcode: 'advanced',
  outer_wall_flow_ratio: 'advanced',
  inner_wall_flow_ratio: 'advanced',
  top_solid_infill_flow_ratio: 'advanced',
  bottom_solid_infill_flow_ratio: 'advanced',
  internal_solid_infill_flow_ratio: 'advanced',
  sparse_infill_flow_ratio: 'advanced',
  gap_fill_flow_ratio: 'advanced',
  support_flow_ratio: 'advanced',
  support_interface_flow_ratio: 'advanced',
  overhang_flow_ratio: 'advanced',
  first_layer_flow_ratio: 'advanced',
  set_other_flow_ratios: 'advanced',
  fan_kickstart: 'advanced',
  fan_speedup_time: 'advanced',
  fan_speedup_overhangs: 'advanced',
  overhang_fan_threshold: 'advanced',
  interlocking_beam: 'advanced',
  interlocking_beam_layer_count: 'advanced',
  interlocking_beam_width: 'advanced',
  interlocking_boundary_avoidance: 'advanced',
  interlocking_depth: 'advanced',
  interlocking_orientation: 'advanced',
  mmu_segmented_region_max_width: 'advanced',
  mmu_segmented_region_interlocking_depth: 'advanced',
  wipe_tower_interface_flow_ratio: 'advanced',
  wipe_tower_interface_speed: 'advanced',
  bed_temperature_formula: 'advanced',
  filament_flush_temp: 'advanced',
  filament_flush_volumetric_speed: 'advanced',
  slow_down_layers: 'advanced',
};
