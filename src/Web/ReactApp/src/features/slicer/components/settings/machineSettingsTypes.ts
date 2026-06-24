/**
 * OrcaSlicer machine/printer settings type definitions.
 * Uses OrcaSlicer native snake_case property names throughout.
 *
 * Source of truth: orcaSettingsMetadata.json → machine (105 unique keys)
 */

// =============================================================================
// VIEW MODE & CATEGORY TYPES
// =============================================================================

/** View modes for machine settings panel complexity */
export type MachineSettingsViewMode = 'simple' | 'advanced';

/** Category tabs in the machine settings panel, matching metadata tab names */
export type MachineSettingsCategory =
  | 'basic_information'
  | 'machine_gcode'
  | 'extruder'
  | 'motion_ability'
  | 'multimaterial'
  | 'notes';

// =============================================================================
// MAIN SETTINGS INTERFACE
// =============================================================================

/**
 * OrcaSlicer machine profile settings using native snake_case property names.
 * All properties are optional — profiles may contain partial settings.
 */
export interface OrcaMachineSettings {

  // ---------------------------------------------------------------------------
  // Basic information tab — Printable space
  // ---------------------------------------------------------------------------

  bed_exclude_area?: string;
  printable_height?: number;
  support_multi_bed_types?: boolean;
  best_object_pos?: string;
  z_offset?: number;
  preferred_orientation?: number;

  // Basic information tab — Advanced

  printer_structure?: string;
  gcode_flavor?: string;
  pellet_modded_printer?: boolean;
  bbl_use_printhost?: boolean;
  scan_first_layer?: boolean;
  enable_power_loss_recovery?: string;
  disable_m73?: boolean;
  thumbnails?: string;
  use_relative_e_distances?: boolean;
  use_firmware_retraction?: boolean;
  time_cost?: number;

  // Basic information tab — Cooling Fan

  fan_speedup_time?: string;
  fan_speedup_overhangs?: string;
  fan_kickstart?: number;

  // Basic information tab — Extruder Clearance

  extruder_clearance_radius?: number;
  extruder_clearance_height_to_rod?: number;
  extruder_clearance_height_to_lid?: number;

  // Basic information tab — Adaptive bed mesh

  bed_mesh_min?: string;
  bed_mesh_max?: string;
  bed_mesh_probe_distance?: string;
  adaptive_bed_mesh_margin?: number;

  // Basic information tab — Accessory

  nozzle_type?: string;
  nozzle_hrc?: number;
  auxiliary_fan?: boolean;
  support_chamber_temp_control?: boolean;
  support_air_filtration?: boolean;

  // ---------------------------------------------------------------------------
  // Machine G-code tab
  // ---------------------------------------------------------------------------

  file_start_gcode?: string;
  machine_start_gcode?: string;
  machine_end_gcode?: string;
  printing_by_object_gcode?: string;
  before_layer_change_gcode?: string;
  layer_change_gcode?: string;
  time_lapse_gcode?: string;
  wrapping_detection_gcode?: string;
  change_filament_gcode?: string;
  change_extrusion_role_gcode?: string;
  machine_pause_gcode?: string;
  template_custom_gcode?: string;

  // ---------------------------------------------------------------------------
  // Extruder tab — Basic information
  // ---------------------------------------------------------------------------

  nozzle_diameter?: number;
  nozzle_volume?: number;
  extruder_printable_height?: number;
  extruder_printable_area?: string;

  // Extruder tab — Layer height limits

  min_layer_height?: number;
  max_layer_height?: number;

  // Extruder tab — Position

  extruder_offset?: string;

  // Extruder tab — Retraction

  retraction_length?: number;
  retract_restart_extra?: number;
  retraction_speed?: number;
  deretraction_speed?: number;
  retraction_minimum_travel?: number;
  retract_when_changing_layer?: boolean;
  wipe?: boolean;
  wipe_distance?: number;
  retract_before_wipe?: number;

  // Extruder tab — Z-Hop

  retract_lift_enforce?: string;
  z_hop_types?: string;
  z_hop?: number;
  travel_slope?: number;
  retract_lift_above?: number;
  retract_lift_below?: number;

  // Extruder tab — Retraction when switching material

  retract_length_toolchange?: number;
  retract_restart_extra_toolchange?: number;
  long_retractions_when_cut?: boolean;
  retraction_distances_when_cut?: number;

  // ---------------------------------------------------------------------------
  // Motion ability tab — Advanced
  // ---------------------------------------------------------------------------

  emit_machine_limits_to_gcode?: boolean;

  // Motion ability tab — Resonance Avoidance

  resonance_avoidance?: boolean;
  min_resonance_avoidance_speed?: string;
  max_resonance_avoidance_speed?: string;

  // Motion ability tab — Speed limitation

  machine_max_speed_x?: number;
  machine_max_speed_y?: number;
  machine_max_speed_z?: number;
  machine_max_speed_e?: number;

  // Motion ability tab — Acceleration limitation

  machine_max_acceleration_x?: number;
  machine_max_acceleration_y?: number;
  machine_max_acceleration_z?: number;
  machine_max_acceleration_e?: number;
  machine_max_acceleration_extruding?: number;
  machine_max_acceleration_retracting?: number;
  machine_max_acceleration_travel?: number;

  // Motion ability tab — Jerk limitation

  machine_max_junction_deviation?: number;
  machine_max_jerk_x?: number;
  machine_max_jerk_y?: number;
  machine_max_jerk_z?: number;
  machine_max_jerk_e?: number;

  // ---------------------------------------------------------------------------
  // Multimaterial tab — Single extruder multi-material setup
  // ---------------------------------------------------------------------------

  single_extruder_multi_material?: boolean;
  manual_filament_change?: boolean;
  bed_temperature_formula?: string;

  // Multimaterial tab — Wipe tower

  wipe_tower_type?: string;
  purge_in_prime_tower?: boolean;
  enable_filament_ramming?: boolean;

  // Multimaterial tab — Single extruder multi-material parameters

  cooling_tube_retraction?: number;
  cooling_tube_length?: number;
  parking_pos_retraction?: number;
  extra_loading_move?: number;
  high_current_on_filament_swap?: boolean;

  // Multimaterial tab — Advanced

  machine_load_filament_time?: number;
  machine_unload_filament_time?: number;
  machine_tool_change_time?: number;

  // ---------------------------------------------------------------------------
  // Notes tab
  // ---------------------------------------------------------------------------

  printer_notes?: string;

  // ---------------------------------------------------------------------------
  // Additional settings (in metadata settings dict but not in tab hierarchy)
  // ---------------------------------------------------------------------------

  // Machine limits (developer mode)
  machine_min_extruding_rate?: number;
  machine_min_travel_rate?: number;

  // Quality
  wipe_before_external_loop?: boolean;
  wipe_on_loops?: boolean;

  // Speed
  wipe_speed?: string;

  // Extruder change
  retraction_distances_when_ec?: number;

  // Wipe tower settings
  wipe_tower_bridging?: number;
  wipe_tower_cone_angle?: number;
  wipe_tower_extra_flow?: number;
  wipe_tower_extra_rib_length?: number;
  wipe_tower_extra_spacing?: number;
  wipe_tower_filament?: number;
  wipe_tower_fillet_wall?: boolean;
  wipe_tower_max_purge_speed?: number;
  wipe_tower_no_sparse_layers?: boolean;
  wipe_tower_rib_width?: number;
  wipe_tower_rotation_angle?: number;
  wipe_tower_wall_type?: string;
  wipe_tower_x?: number;
  wipe_tower_y?: number;

  // --- OrcaSlicer 2.4.0 additions ---
  input_shaping_damp_x?: number;
  input_shaping_damp_y?: number;
  input_shaping_emit?: boolean;
  input_shaping_freq_x?: number;
  input_shaping_freq_y?: number;
  input_shaping_type?: string;
  parallel_printheads_count?: number;
  part_cooling_fan_min_pwm?: number;
  tool_change_on_wipe_tower?: boolean;
  use_3mf?: boolean;

}

// =============================================================================
// CATEGORY ARRAY
// =============================================================================

/** Ordered list of machine settings categories matching OrcaSlicer tab layout */
export const MACHINE_SETTING_CATEGORIES: MachineSettingsCategory[] = [
  'basic_information',
  'machine_gcode',
  'extruder',
  'motion_ability',
  'multimaterial',
  'notes',
];

// =============================================================================
// MODE MAP — simple vs advanced for each setting
// =============================================================================

/**
 * Maps every machine setting to its view mode.
 *
 * Simple: core printer identity, bed shape, nozzle, layer limits, basic
 *         retraction, and speed caps — the settings most users configure.
 * Advanced: G-code macros, resonance, multi-material, detailed motion, etc.
 */
export const MACHINE_SETTINGS_MODE_MAP: Record<keyof OrcaMachineSettings, MachineSettingsViewMode> = {
  // Basic information — Printable space
  bed_exclude_area: 'simple',
  printable_height: 'simple',
  support_multi_bed_types: 'simple',
  best_object_pos: 'advanced',
  z_offset: 'advanced',
  preferred_orientation: 'advanced',

  // Basic information — Advanced
  printer_structure: 'advanced',
  gcode_flavor: 'simple',
  pellet_modded_printer: 'advanced',
  bbl_use_printhost: 'advanced',
  scan_first_layer: 'advanced',
  enable_power_loss_recovery: 'advanced',
  disable_m73: 'advanced',
  thumbnails: 'advanced',
  use_relative_e_distances: 'advanced',
  use_firmware_retraction: 'advanced',
  time_cost: 'advanced',

  // Basic information — Cooling Fan
  fan_speedup_time: 'advanced',
  fan_speedup_overhangs: 'advanced',
  fan_kickstart: 'advanced',

  // Basic information — Extruder Clearance
  extruder_clearance_radius: 'advanced',
  extruder_clearance_height_to_rod: 'advanced',
  extruder_clearance_height_to_lid: 'advanced',

  // Basic information — Adaptive bed mesh
  bed_mesh_min: 'advanced',
  bed_mesh_max: 'advanced',
  bed_mesh_probe_distance: 'advanced',
  adaptive_bed_mesh_margin: 'advanced',

  // Basic information — Accessory
  nozzle_type: 'simple',
  nozzle_hrc: 'advanced',
  auxiliary_fan: 'advanced',
  support_chamber_temp_control: 'advanced',
  support_air_filtration: 'advanced',

  // Machine G-code (all advanced)
  file_start_gcode: 'advanced',
  machine_start_gcode: 'advanced',
  machine_end_gcode: 'advanced',
  printing_by_object_gcode: 'advanced',
  before_layer_change_gcode: 'advanced',
  layer_change_gcode: 'advanced',
  time_lapse_gcode: 'advanced',
  wrapping_detection_gcode: 'advanced',
  change_filament_gcode: 'advanced',
  change_extrusion_role_gcode: 'advanced',
  machine_pause_gcode: 'advanced',
  template_custom_gcode: 'advanced',

  // Extruder — Basic information
  nozzle_diameter: 'simple',
  nozzle_volume: 'advanced',
  extruder_printable_height: 'advanced',
  extruder_printable_area: 'simple',

  // Extruder — Layer height limits
  min_layer_height: 'simple',
  max_layer_height: 'simple',

  // Extruder — Position
  extruder_offset: 'advanced',

  // Extruder — Retraction
  retraction_length: 'simple',
  retract_restart_extra: 'advanced',
  retraction_speed: 'simple',
  deretraction_speed: 'advanced',
  retraction_minimum_travel: 'advanced',
  retract_when_changing_layer: 'advanced',
  wipe: 'advanced',
  wipe_distance: 'advanced',
  retract_before_wipe: 'advanced',

  // Extruder — Z-Hop
  retract_lift_enforce: 'advanced',
  z_hop_types: 'advanced',
  z_hop: 'advanced',
  travel_slope: 'advanced',
  retract_lift_above: 'advanced',
  retract_lift_below: 'advanced',

  // Extruder — Retraction when switching material
  retract_length_toolchange: 'advanced',
  retract_restart_extra_toolchange: 'advanced',
  long_retractions_when_cut: 'advanced',
  retraction_distances_when_cut: 'advanced',

  // Motion ability — Advanced
  emit_machine_limits_to_gcode: 'advanced',

  // Motion ability — Resonance Avoidance
  resonance_avoidance: 'advanced',
  min_resonance_avoidance_speed: 'advanced',
  max_resonance_avoidance_speed: 'advanced',

  // Motion ability — Speed limitation
  machine_max_speed_x: 'simple',
  machine_max_speed_y: 'simple',
  machine_max_speed_z: 'simple',
  machine_max_speed_e: 'simple',

  // Motion ability — Acceleration limitation
  machine_max_acceleration_x: 'advanced',
  machine_max_acceleration_y: 'advanced',
  machine_max_acceleration_z: 'advanced',
  machine_max_acceleration_e: 'advanced',
  machine_max_acceleration_extruding: 'advanced',
  machine_max_acceleration_retracting: 'advanced',
  machine_max_acceleration_travel: 'advanced',

  // Motion ability — Jerk limitation
  machine_max_junction_deviation: 'advanced',
  machine_max_jerk_x: 'advanced',
  machine_max_jerk_y: 'advanced',
  machine_max_jerk_z: 'advanced',
  machine_max_jerk_e: 'advanced',

  // Multimaterial — Single extruder multi-material setup
  single_extruder_multi_material: 'advanced',
  manual_filament_change: 'advanced',
  bed_temperature_formula: 'advanced',

  // Multimaterial — Wipe tower
  wipe_tower_type: 'advanced',
  purge_in_prime_tower: 'advanced',
  enable_filament_ramming: 'advanced',

  // Multimaterial — Single extruder multi-material parameters
  cooling_tube_retraction: 'advanced',
  cooling_tube_length: 'advanced',
  parking_pos_retraction: 'advanced',
  extra_loading_move: 'advanced',
  high_current_on_filament_swap: 'advanced',

  // Multimaterial — Advanced
  machine_load_filament_time: 'advanced',
  machine_unload_filament_time: 'advanced',
  machine_tool_change_time: 'advanced',

  // Notes
  printer_notes: 'advanced',

  // Additional settings (not in tab hierarchy)
  machine_min_extruding_rate: 'advanced',
  machine_min_travel_rate: 'advanced',
  wipe_before_external_loop: 'advanced',
  wipe_on_loops: 'advanced',
  wipe_speed: 'advanced',
  retraction_distances_when_ec: 'advanced',
  wipe_tower_bridging: 'advanced',
  wipe_tower_cone_angle: 'advanced',
  wipe_tower_extra_flow: 'advanced',
  wipe_tower_extra_rib_length: 'advanced',
  wipe_tower_extra_spacing: 'advanced',
  wipe_tower_filament: 'advanced',
  wipe_tower_fillet_wall: 'advanced',
  wipe_tower_max_purge_speed: 'advanced',
  wipe_tower_no_sparse_layers: 'advanced',
  wipe_tower_rib_width: 'advanced',
  wipe_tower_rotation_angle: 'advanced',
  wipe_tower_wall_type: 'advanced',
  wipe_tower_x: 'advanced',
  wipe_tower_y: 'advanced',

  // --- OrcaSlicer 2.4.0 additions ---
  input_shaping_damp_x: 'advanced',
  input_shaping_damp_y: 'advanced',
  input_shaping_emit: 'advanced',
  input_shaping_freq_x: 'advanced',
  input_shaping_freq_y: 'advanced',
  input_shaping_type: 'advanced',
  parallel_printheads_count: 'advanced',
  part_cooling_fan_min_pwm: 'advanced',
  tool_change_on_wipe_tower: 'advanced',
  use_3mf: 'advanced',

};

// =============================================================================
// CATEGORY MAP — each setting → its parent category
// =============================================================================

/** Maps every machine setting key to its category (derived from metadata tab) */
export const MACHINE_SETTINGS_CATEGORY_MAP: Record<keyof OrcaMachineSettings, MachineSettingsCategory> = {
  // Basic information tab
  bed_exclude_area: 'basic_information',
  printable_height: 'basic_information',
  support_multi_bed_types: 'basic_information',
  best_object_pos: 'basic_information',
  z_offset: 'basic_information',
  preferred_orientation: 'basic_information',
  printer_structure: 'basic_information',
  gcode_flavor: 'basic_information',
  pellet_modded_printer: 'basic_information',
  bbl_use_printhost: 'basic_information',
  scan_first_layer: 'basic_information',
  enable_power_loss_recovery: 'basic_information',
  disable_m73: 'basic_information',
  thumbnails: 'basic_information',
  use_relative_e_distances: 'basic_information',
  use_firmware_retraction: 'basic_information',
  time_cost: 'basic_information',
  fan_speedup_time: 'basic_information',
  fan_speedup_overhangs: 'basic_information',
  fan_kickstart: 'basic_information',
  extruder_clearance_radius: 'basic_information',
  extruder_clearance_height_to_rod: 'basic_information',
  extruder_clearance_height_to_lid: 'basic_information',
  bed_mesh_min: 'basic_information',
  bed_mesh_max: 'basic_information',
  bed_mesh_probe_distance: 'basic_information',
  adaptive_bed_mesh_margin: 'basic_information',
  nozzle_type: 'basic_information',
  nozzle_hrc: 'basic_information',
  auxiliary_fan: 'basic_information',
  support_chamber_temp_control: 'basic_information',
  support_air_filtration: 'basic_information',

  // Machine G-code tab
  file_start_gcode: 'machine_gcode',
  machine_start_gcode: 'machine_gcode',
  machine_end_gcode: 'machine_gcode',
  printing_by_object_gcode: 'machine_gcode',
  before_layer_change_gcode: 'machine_gcode',
  layer_change_gcode: 'machine_gcode',
  time_lapse_gcode: 'machine_gcode',
  wrapping_detection_gcode: 'machine_gcode',
  change_filament_gcode: 'machine_gcode',
  change_extrusion_role_gcode: 'machine_gcode',
  machine_pause_gcode: 'machine_gcode',
  template_custom_gcode: 'machine_gcode',

  // Extruder tab
  nozzle_diameter: 'extruder',
  nozzle_volume: 'extruder',
  extruder_printable_height: 'extruder',
  extruder_printable_area: 'extruder',
  min_layer_height: 'extruder',
  max_layer_height: 'extruder',
  extruder_offset: 'extruder',
  retraction_length: 'extruder',
  retract_restart_extra: 'extruder',
  retraction_speed: 'extruder',
  deretraction_speed: 'extruder',
  retraction_minimum_travel: 'extruder',
  retract_when_changing_layer: 'extruder',
  wipe: 'extruder',
  wipe_distance: 'extruder',
  retract_before_wipe: 'extruder',
  retract_lift_enforce: 'extruder',
  z_hop_types: 'extruder',
  z_hop: 'extruder',
  travel_slope: 'extruder',
  retract_lift_above: 'extruder',
  retract_lift_below: 'extruder',
  retract_length_toolchange: 'extruder',
  retract_restart_extra_toolchange: 'extruder',
  long_retractions_when_cut: 'extruder',
  retraction_distances_when_cut: 'extruder',

  // Motion ability tab
  emit_machine_limits_to_gcode: 'motion_ability',
  resonance_avoidance: 'motion_ability',
  min_resonance_avoidance_speed: 'motion_ability',
  max_resonance_avoidance_speed: 'motion_ability',
  machine_max_speed_x: 'motion_ability',
  machine_max_speed_y: 'motion_ability',
  machine_max_speed_z: 'motion_ability',
  machine_max_speed_e: 'motion_ability',
  machine_max_acceleration_x: 'motion_ability',
  machine_max_acceleration_y: 'motion_ability',
  machine_max_acceleration_z: 'motion_ability',
  machine_max_acceleration_e: 'motion_ability',
  machine_max_acceleration_extruding: 'motion_ability',
  machine_max_acceleration_retracting: 'motion_ability',
  machine_max_acceleration_travel: 'motion_ability',
  machine_max_junction_deviation: 'motion_ability',
  machine_max_jerk_x: 'motion_ability',
  machine_max_jerk_y: 'motion_ability',
  machine_max_jerk_z: 'motion_ability',
  machine_max_jerk_e: 'motion_ability',

  // Multimaterial tab
  single_extruder_multi_material: 'multimaterial',
  manual_filament_change: 'multimaterial',
  bed_temperature_formula: 'multimaterial',
  wipe_tower_type: 'multimaterial',
  purge_in_prime_tower: 'multimaterial',
  enable_filament_ramming: 'multimaterial',
  cooling_tube_retraction: 'multimaterial',
  cooling_tube_length: 'multimaterial',
  parking_pos_retraction: 'multimaterial',
  extra_loading_move: 'multimaterial',
  high_current_on_filament_swap: 'multimaterial',
  machine_load_filament_time: 'multimaterial',
  machine_unload_filament_time: 'multimaterial',
  machine_tool_change_time: 'multimaterial',

  // Notes tab
  printer_notes: 'notes',

  // Additional settings (not in tab hierarchy)
  machine_min_extruding_rate: 'motion_ability',
  machine_min_travel_rate: 'motion_ability',
  wipe_before_external_loop: 'extruder',
  wipe_on_loops: 'extruder',
  wipe_speed: 'extruder',
  retraction_distances_when_ec: 'extruder',
  wipe_tower_bridging: 'multimaterial',
  wipe_tower_cone_angle: 'multimaterial',
  wipe_tower_extra_flow: 'multimaterial',
  wipe_tower_extra_rib_length: 'multimaterial',
  wipe_tower_extra_spacing: 'multimaterial',
  wipe_tower_filament: 'multimaterial',
  wipe_tower_fillet_wall: 'multimaterial',
  wipe_tower_max_purge_speed: 'multimaterial',
  wipe_tower_no_sparse_layers: 'multimaterial',
  wipe_tower_rib_width: 'multimaterial',
  wipe_tower_rotation_angle: 'multimaterial',
  wipe_tower_wall_type: 'multimaterial',
  wipe_tower_x: 'multimaterial',
  wipe_tower_y: 'multimaterial',

  // --- OrcaSlicer 2.4.0 additions ---
  input_shaping_damp_x: 'motion_ability',
  input_shaping_damp_y: 'motion_ability',
  input_shaping_emit: 'motion_ability',
  input_shaping_freq_x: 'motion_ability',
  input_shaping_freq_y: 'motion_ability',
  input_shaping_type: 'motion_ability',
  parallel_printheads_count: 'basic_information',
  part_cooling_fan_min_pwm: 'basic_information',
  tool_change_on_wipe_tower: 'multimaterial',
  use_3mf: 'basic_information',

};

// =============================================================================
// DEFAULT SETTINGS — typical 220×220×250 FDM printer
// =============================================================================

/** Sensible defaults for a generic 220×220×250 FDM printer (e.g. Ender-3 class) */
export const DEFAULT_MACHINE_SETTINGS: Partial<OrcaMachineSettings> = {
  // Build volume
  printable_height: 250,
  bed_exclude_area: '',
  support_multi_bed_types: false,
  z_offset: 0,

  // Core identity
  printer_structure: 'i3',
  gcode_flavor: 'marlin',
  nozzle_type: 'brass',
  nozzle_hrc: 0,

  // Firmware behaviour
  pellet_modded_printer: false,
  bbl_use_printhost: false,
  scan_first_layer: false,
  enable_power_loss_recovery: 'disable',
  disable_m73: false,
  use_relative_e_distances: false,
  use_firmware_retraction: false,
  time_cost: 0,

  // Cooling fan
  fan_kickstart: 0,

  // Extruder clearance
  extruder_clearance_radius: 40,
  extruder_clearance_height_to_rod: 36,
  extruder_clearance_height_to_lid: 120,

  // Adaptive bed mesh
  adaptive_bed_mesh_margin: 0,

  // Accessory
  auxiliary_fan: false,
  support_chamber_temp_control: false,
  support_air_filtration: false,

  // G-code macros
  machine_start_gcode: '',
  machine_end_gcode: '',
  file_start_gcode: '',
  before_layer_change_gcode: '',
  layer_change_gcode: '',
  change_filament_gcode: '',

  // Extruder
  nozzle_diameter: 0.4,
  nozzle_volume: 0,
  min_layer_height: 0.07,
  max_layer_height: 0.32,

  // Retraction
  retraction_length: 0.8,
  retract_restart_extra: 0,
  retraction_speed: 30,
  deretraction_speed: 0,
  retraction_minimum_travel: 2,
  retract_when_changing_layer: false,
  wipe: false,
  wipe_distance: 1,
  retract_before_wipe: 0,

  // Z-Hop
  z_hop: 0,
  travel_slope: 3,
  retract_lift_above: 0,
  retract_lift_below: 0,

  // Toolchange retraction
  retract_length_toolchange: 10,
  retract_restart_extra_toolchange: 0,
  long_retractions_when_cut: false,
  retraction_distances_when_cut: 18,

  // Motion limits
  emit_machine_limits_to_gcode: true,
  resonance_avoidance: false,
  machine_max_speed_x: 500,
  machine_max_speed_y: 500,
  machine_max_speed_z: 12,
  machine_max_speed_e: 120,
  machine_max_acceleration_x: 5000,
  machine_max_acceleration_y: 5000,
  machine_max_acceleration_z: 500,
  machine_max_acceleration_e: 5000,
  machine_max_acceleration_extruding: 5000,
  machine_max_acceleration_retracting: 5000,
  machine_max_acceleration_travel: 5000,
  machine_max_junction_deviation: 0.08,
  machine_max_jerk_x: 9,
  machine_max_jerk_y: 9,
  machine_max_jerk_z: 3,
  machine_max_jerk_e: 2.5,

  // Multi-material
  single_extruder_multi_material: false,
  manual_filament_change: false,
  purge_in_prime_tower: true,
  enable_filament_ramming: true,
  high_current_on_filament_swap: false,
  cooling_tube_retraction: 91.5,
  cooling_tube_length: 5,
  parking_pos_retraction: 92,
  extra_loading_move: -2,
  machine_load_filament_time: 0,
  machine_unload_filament_time: 0,
  machine_tool_change_time: 0,

  // Notes
  printer_notes: '',

  // Additional settings
  machine_min_extruding_rate: 0,
  machine_min_travel_rate: 0,
  wipe_before_external_loop: false,
  wipe_on_loops: false,
  wipe_speed: '80%',
  retraction_distances_when_ec: 10,
  wipe_tower_bridging: 10,
  wipe_tower_cone_angle: 30,
  wipe_tower_extra_flow: 100,
  wipe_tower_extra_rib_length: 1,
  wipe_tower_extra_spacing: 100,
  wipe_tower_filament: 0,
  wipe_tower_fillet_wall: false,
  wipe_tower_max_purge_speed: 90,
  wipe_tower_no_sparse_layers: false,
  wipe_tower_rib_width: 0.5,
  wipe_tower_rotation_angle: 0,
  wipe_tower_wall_type: 'rectangle',
  wipe_tower_x: 170,
  wipe_tower_y: 140,
};
