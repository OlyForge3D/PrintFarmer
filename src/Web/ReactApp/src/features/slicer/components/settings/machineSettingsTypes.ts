/**
 * OrcaSlicer Machine settings type definitions
 * Uses OrcaSlicer native snake_case property names.
 */

/** View mode for settings panel complexity */
export type MachineSettingsViewMode = 'simple' | 'advanced';

/** Category tabs matching OrcaSlicer / SimplyPrint tab layout */
export type MachineCategory =
  | 'basic_information'
  | 'machine_gcode'
  | 'multimaterial'
  | 'extruder'
  | 'motion_ability'
  | 'notes';

/**
 * Unified machine settings interface using OrcaSlicer native snake_case keys.
 * All properties are optional to support partial profiles and progressive editing.
 */
export interface OrcaMachineSettings {
  // ── Printable Space ───────────────────────────────────────────────────────
  /** Excluded bed area coordinates */
  bed_exclude_area?: string;
  /** Maximum printable height / build volume Z (mm) */
  printable_height?: number;
  /** Whether the printer supports multiple bed surface types */
  support_multi_bed_types?: boolean;
  /** Best object position X (0-1 normalized) */
  best_object_pos_x?: number;
  /** Best object position Y (0-1 normalized) */
  best_object_pos_y?: number;
  /** Z offset adjustment (mm) */
  z_offset?: number;
  /** Preferred model orientation for slicing (degrees) */
  preferred_orientation?: number;

  // ── Advanced ──────────────────────────────────────────────────────────────
  /** Printer structure type (CoreXY, Cartesian, etc.) */
  printer_structure?: string;
  /** Whether the printer has a pellet extruder mod */
  pellet_modded_printer?: boolean;
  /** Disable M73 remaining print time command */
  disable_m73?: boolean;
  /** G-code thumbnail sizes (e.g., "300x300,32x32") */
  thumbnails?: string;
  /** Use relative E distances in G-code */
  use_relative_e_distances?: boolean;
  /** Use firmware-side retraction (G10/G11) */
  use_firmware_retraction?: boolean;
  /** Time cost multiplier for time estimates */
  time_cost?: number;

  // ── Cooling Fan ───────────────────────────────────────────────────────────
  /** Fan speed-up time (seconds) */
  fan_speedup_time?: number;
  /** Fan kick-start duration (seconds) */
  fan_kickstart?: number;
  /** Only speed up fan for overhangs */
  fan_speedup_overhangs?: boolean;

  // ── Extruder Clearance ────────────────────────────────────────────────────
  extruder_clearance_radius?: number;
  extruder_clearance_height_to_rod?: number;
  extruder_clearance_height_to_lid?: number;

  // ── Adaptive Bed Mesh ─────────────────────────────────────────────────────
  bed_mesh_min_x?: number;
  bed_mesh_min_y?: number;
  bed_mesh_max_x?: number;
  bed_mesh_max_y?: number;
  probe_point_dist_x?: number;
  probe_point_dist_y?: number;
  adaptive_bed_mesh_margin?: number;

  // ── Accessory ─────────────────────────────────────────────────────────────
  /** Nozzle tip size shown in Basic view (mm) */
  nozzle_size?: number;
  /** Standard or high-flow nozzle volume type */
  nozzle_volume_type?: 'standard' | 'high_flow';
  /** Nozzle hardness (Rockwell C scale) */
  nozzle_hrc?: number;
  /** Whether the printer supports closed-loop chamber temperature control */
  support_chamber_temp_control?: boolean;
  /** Whether the printer has an active air filtration system */
  support_air_filtration?: boolean;
  /** Enable first-layer scan (camera-equipped printers) */
  scan_first_layer?: boolean;
  /** Disable M73 remaining print time command */
  disable_m73?: boolean;
  /** G-code thumbnail sizes (e.g., "300x300,32x32") */
  thumbnails?: string;
  /** Use relative E distances in G-code */
  use_relative_e_distances?: boolean;
  /** Use firmware-side retraction (G10/G11) */
  use_firmware_retraction?: boolean;
  /** Time cost multiplier for time estimates */
  time_cost?: number;
  /** Enable fan speed-up only on overhangs */
  fan_speedup_overhangs?: boolean;
  /** Fan kick-start pulse duration (s) */
  fan_kickstart?: number;
  /** Extruder clearance radius from nozzle tip (mm) */
  extruder_clearance_radius?: number;
  /** Extruder clearance height to X/Y rod (mm) */
  extruder_clearance_height_to_rod?: number;
  /** Extruder clearance height to printer lid (mm) */
  extruder_clearance_height_to_lid?: number;
  /** Margin around mesh area for adaptive bed meshing (mm) */
  adaptive_bed_mesh_margin?: number;
  /** Enable auxiliary part-cooling fan */
  auxiliary_fan?: boolean;

  // ── Machine G-code ────────────────────────────────────────────────────────
  /** G-code injected at the start of every file */
  file_start_gcode?: string;
  /** G-code used to detect filament clumping / wrapping */
  wrapping_detection_gcode?: string;

  // ── Multimaterial ─────────────────────────────────────────────────────────
  /** Single extruder operating in multi-material mode */
  single_extruder_multi_material?: boolean;
  /** Use manual filament-change procedure instead of automated swap */
  manual_filament_change?: boolean;
  /** Purge excess filament into the prime/wipe tower */
  purge_in_prime_tower?: boolean;
  /** Enable filament tip ramming on retract */
  enable_filament_ramming?: boolean;
  /** Retraction distance to reach the cooling tube (mm) */
  cooling_tube_retraction?: number;
  /** Length of the cooling tube (mm) */
  cooling_tube_length?: number;
  /** Filament parking position retraction distance (mm) */
  parking_pos_retraction?: number;
  /** Extra loading move distance (mm) */
  extra_loading_move?: number;
  /** Run extruder at high current during filament swap */
  high_current_on_filament_swap?: boolean;
  /** Time to load filament into the extruder (s) */
  machine_load_filament_time?: number;
  /** Time to unload filament from the extruder (s) */
  machine_unload_filament_time?: number;
  /** Tool-change overhead time (s) */
  machine_tool_change_time?: number;

  // ── Extruder ──────────────────────────────────────────────────────────────
  /** Retraction length (mm) */
  retraction_length?: number;
  /** Z-hop height on retract (mm) */
  z_hop?: number;
  /** Long retraction distance when using filament cutter (mm) */
  long_retractions_when_cut?: number;
  /** Comma-separated retraction distances for cut operation (mm) */
  retraction_distances_when_cut?: string;
  /** Nozzle internal diameter (mm) */
  nozzle_diameter?: number;
  /** Nozzle melt-zone volume (mm³) */
  nozzle_volume?: number;
  /** Maximum printable height per extruder (mm) */
  extruder_printable_height?: number;
  /** Minimum layer height (mm) */
  min_layer_height?: number;
  /** Maximum layer height (mm) */
  max_layer_height?: number;
  /** Extra filament length on unretract (mm) */
  retract_restart_extra?: number;
  /** Retraction speed (mm/s) */
  retraction_speed?: number;
  /** Deretraction / unretract speed (mm/s) */
  deretraction_speed?: number;
  /** Minimum travel distance that triggers retraction (mm) */
  retraction_minimum_travel?: number;
  /** Retract when changing layer */
  retract_when_changing_layer?: boolean;
  /** Wipe nozzle while retracting */
  wipe?: boolean;
  /** Wipe move distance (mm) */
  wipe_distance?: number;
  /** Percentage of retraction to complete before wipe begins (%) */
  retract_before_wipe?: number;
  /** Slope travel moves to avoid stringing over Z */
  travel_slope?: boolean;
  /** Only apply Z-hop above this Z height (mm) */
  retract_lift_above?: number;
  /** Only apply Z-hop below this Z height; 0 = unlimited (mm) */
  retract_lift_below?: number;
  /** Retraction length on tool change (mm) */
  retract_length_toolchange?: number;
  /** Extra restart length after tool change (mm) */
  retract_restart_extra_toolchange?: number;

  // ── Motion ability ────────────────────────────────────────────────────────
  /** Write machine speed/acceleration limits to G-code header */
  emit_machine_limits_to_gcode?: boolean;
  /** Enable input-shaping / resonance avoidance */
  resonance_avoidance?: boolean;
  /** Minimum speed for resonance avoidance (mm/s) */
  min_resonance_avoidance_speed?: number;
  /** Maximum speed for resonance avoidance (mm/s) */
  max_resonance_avoidance_speed?: number;

  // ── Notes ─────────────────────────────────────────────────────────────────
  /** Free-form notes stored in the machine profile */
  printer_notes?: string;

  // ── Advanced extras (not in SimplyPrint catalog) ──────────────────────────

  // Metadata
  inherits?: string;
  printer_model?: string;
  printer_variant?: string;
  thumbnails_format?: string;

  // Build volume
  bed_size_x?: number;
  bed_size_y?: number;
  printable_area?: string;
  bed_origin?: 'center' | 'corner';
  bed_shape?: 'rectangular' | 'circular';

  // Nozzle
  nozzle_type?: 'brass' | 'hardened_steel' | 'stainless_steel' | 'custom';

  // Extruder
  extruder_count?: number;
  extruder_offset?: string;
  extrusion_multiplier?: number;
  extruder_type?: 'direct_drive' | 'bowden';
  extruder_colour?: string;
  extruder_printable_area?: string;
  retract_lift_enforce?: string;
  z_hop_types?: string;
  wipe_speed?: number;
  wipe_before_external_loop?: boolean;
  wipe_on_loops?: boolean;

  // Print bed
  bed_type?: 'textured_pei' | 'smooth_pei' | 'glass' | 'spring_steel' | 'custom';
  has_bed_probe?: boolean;
  probe_type?: 'bltouch' | 'inductive' | 'capacitive' | 'manual' | 'none';
  mesh_bed_leveling?: boolean;
  bed_custom_texture?: string;
  bed_custom_model?: string;

  // Capabilities
  has_heated_bed?: boolean;
  has_heated_chamber?: boolean;
  max_bed_temperature?: number;
  max_chamber_temperature?: number;
  max_hotend_temperature?: number;
  support_multi_material?: boolean;
  support_arc_movement?: boolean;
  arc_resolution?: number;
  has_scarf_joint_seam?: boolean;
  single_extruder_multi_material_priming?: boolean;

  // Motion limits
  motion_type?: 'cartesian' | 'corexy' | 'delta' | 'belt';
  machine_max_acceleration_x?: number;
  machine_max_acceleration_y?: number;
  machine_max_acceleration_z?: number;
  machine_max_acceleration_e?: number;
  machine_max_jerk_x?: number;
  machine_max_jerk_y?: number;
  machine_max_jerk_z?: number;
  machine_max_jerk_e?: number;
  machine_max_speed_x?: number;
  machine_max_speed_y?: number;
  machine_max_speed_z?: number;
  machine_max_speed_e?: number;
  max_print_speed?: number;
  machine_max_acceleration_travel?: number;
  max_junction_deviation?: number;

  // Cooling
  cooling_fan_count?: number;
  has_chamber_fan?: boolean;
  fan_max_speed?: number;

  // G-code (beyond Machine G-code tab)
  gcode_flavor?: 'marlin' | 'marlin2' | 'klipper' | 'reprap' | 'smoothie' | 'mach3' | 'custom';
  machine_start_gcode?: string;
  machine_end_gcode?: string;
  before_layer_change_gcode?: string;
  layer_change_gcode?: string;
  toolchange_gcode?: string;
  pause_print_gcode?: string;
  printing_by_object_gcode?: string;
  timelapse_gcode?: string;

  // Features / misc
  silent_mode?: boolean;
  silent_mode_max_speed?: number;
  power_loss_recovery?: boolean;
  filament_sensor?: boolean;
  auto_leveling?: boolean;
  timelapse_type?: 'none' | 'regular' | 'layered';
  octoprint_host?: string;
  octoprint_api_key?: string;

  // Physical dimensions (for visualization)
  printer_width?: number;
  printer_depth?: number;
  printer_height?: number;

  // Travel
  travel_speed?: number;
  travel_acceleration?: number;
  travel_jerk?: number;

  // Wipe tower
  wipe_tower_type?: 'sparse' | 'dense';
  wipe_tower_wall_type?: 'single' | 'double';
  wipe_tower_bridging?: number;
  wipe_tower_cone_angle?: number;
  wipe_tower_rotation_angle?: number;
  wipe_tower_extra_flow?: number;
  wipe_tower_extra_spacing?: number;
  wipe_tower_filament?: number;
  wipe_tower_max_purge_speed?: number;
  wipe_tower_no_sparse_layers?: boolean;
  wipe_tower_fillet_wall?: boolean;
  wipe_tower_rib_width?: number;
  wipe_tower_extra_rib_length?: number;
}

/**
 * Maps each OrcaMachineSettings key to its display mode.
 * Keys present in both simple and advanced SimplyPrint views are 'simple'.
 * Everything else (advanced-only SP keys and our extras) is 'advanced'.
 */
export const ORCA_MACHINE_MODE_MAP: Record<string, 'simple' | 'advanced'> = {
  // ── simple (visible in Basic view) ────────────────────────────────────────
  nozzle_size: 'simple',
  nozzle_volume_type: 'simple',
  printable_height: 'simple',
  support_multi_bed_types: 'simple',
  pellet_modded_printer: 'simple',
  nozzle_hrc: 'simple',
  support_chamber_temp_control: 'simple',
  support_air_filtration: 'simple',
  retraction_length: 'simple',
  z_hop: 'simple',
  long_retractions_when_cut: 'simple',
  retraction_distances_when_cut: 'simple',

  // ── advanced (SP catalog, advanced-only tab entries) ──────────────────────
  z_offset: 'advanced',
  preferred_orientation: 'advanced',
  scan_first_layer: 'advanced',
  disable_m73: 'advanced',
  thumbnails: 'advanced',
  use_relative_e_distances: 'advanced',
  use_firmware_retraction: 'advanced',
  time_cost: 'advanced',
  fan_speedup_overhangs: 'advanced',
  fan_kickstart: 'advanced',
  extruder_clearance_radius: 'advanced',
  extruder_clearance_height_to_rod: 'advanced',
  extruder_clearance_height_to_lid: 'advanced',
  adaptive_bed_mesh_margin: 'advanced',
  auxiliary_fan: 'advanced',
  file_start_gcode: 'advanced',
  wrapping_detection_gcode: 'advanced',
  single_extruder_multi_material: 'advanced',
  manual_filament_change: 'advanced',
  purge_in_prime_tower: 'advanced',
  enable_filament_ramming: 'advanced',
  cooling_tube_retraction: 'advanced',
  cooling_tube_length: 'advanced',
  parking_pos_retraction: 'advanced',
  extra_loading_move: 'advanced',
  high_current_on_filament_swap: 'advanced',
  machine_load_filament_time: 'advanced',
  machine_unload_filament_time: 'advanced',
  machine_tool_change_time: 'advanced',
  nozzle_diameter: 'advanced',
  nozzle_volume: 'advanced',
  extruder_printable_height: 'advanced',
  min_layer_height: 'advanced',
  max_layer_height: 'advanced',
  retract_restart_extra: 'advanced',
  retraction_speed: 'advanced',
  deretraction_speed: 'advanced',
  retraction_minimum_travel: 'advanced',
  retract_when_changing_layer: 'advanced',
  wipe: 'advanced',
  wipe_distance: 'advanced',
  retract_before_wipe: 'advanced',
  travel_slope: 'advanced',
  retract_lift_above: 'advanced',
  retract_lift_below: 'advanced',
  retract_length_toolchange: 'advanced',
  retract_restart_extra_toolchange: 'advanced',
  emit_machine_limits_to_gcode: 'advanced',
  resonance_avoidance: 'advanced',
  min_resonance_avoidance_speed: 'advanced',
  max_resonance_avoidance_speed: 'advanced',
  printer_notes: 'advanced',

  // ── advanced (extras not in SP catalog) ───────────────────────────────────
  inherits: 'advanced',
  printer_model: 'advanced',
  printer_variant: 'advanced',
  thumbnails_format: 'advanced',
  bed_size_x: 'advanced',
  bed_size_y: 'advanced',
  printable_area: 'advanced',
  bed_origin: 'advanced',
  bed_shape: 'advanced',
  nozzle_type: 'advanced',
  extruder_count: 'advanced',
  extruder_offset: 'advanced',
  extrusion_multiplier: 'advanced',
  extruder_type: 'advanced',
  extruder_colour: 'advanced',
  extruder_printable_area: 'advanced',
  retract_lift_enforce: 'advanced',
  z_hop_types: 'advanced',
  wipe_speed: 'advanced',
  wipe_before_external_loop: 'advanced',
  wipe_on_loops: 'advanced',
  bed_type: 'advanced',
  has_bed_probe: 'advanced',
  probe_type: 'advanced',
  mesh_bed_leveling: 'advanced',
  bed_custom_texture: 'advanced',
  bed_custom_model: 'advanced',
  has_heated_bed: 'advanced',
  has_heated_chamber: 'advanced',
  max_bed_temperature: 'advanced',
  max_chamber_temperature: 'advanced',
  max_hotend_temperature: 'advanced',
  support_multi_material: 'advanced',
  support_arc_movement: 'advanced',
  arc_resolution: 'advanced',
  has_scarf_joint_seam: 'advanced',
  single_extruder_multi_material_priming: 'advanced',
  motion_type: 'advanced',
  machine_max_acceleration_x: 'advanced',
  machine_max_acceleration_y: 'advanced',
  machine_max_acceleration_z: 'advanced',
  machine_max_acceleration_e: 'advanced',
  machine_max_jerk_x: 'advanced',
  machine_max_jerk_y: 'advanced',
  machine_max_jerk_z: 'advanced',
  machine_max_jerk_e: 'advanced',
  machine_max_speed_x: 'advanced',
  machine_max_speed_y: 'advanced',
  machine_max_speed_z: 'advanced',
  machine_max_speed_e: 'advanced',
  max_print_speed: 'advanced',
  machine_max_acceleration_travel: 'advanced',
  max_junction_deviation: 'advanced',
  cooling_fan_count: 'advanced',
  has_chamber_fan: 'advanced',
  fan_max_speed: 'advanced',
  gcode_flavor: 'advanced',
  machine_start_gcode: 'advanced',
  machine_end_gcode: 'advanced',
  before_layer_change_gcode: 'advanced',
  layer_change_gcode: 'advanced',
  toolchange_gcode: 'advanced',
  pause_print_gcode: 'advanced',
  printing_by_object_gcode: 'advanced',
  timelapse_gcode: 'advanced',
  silent_mode: 'advanced',
  silent_mode_max_speed: 'advanced',
  power_loss_recovery: 'advanced',
  filament_sensor: 'advanced',
  auto_leveling: 'advanced',
  timelapse_type: 'advanced',
  octoprint_host: 'advanced',
  octoprint_api_key: 'advanced',
  printer_width: 'advanced',
  printer_depth: 'advanced',
  printer_height: 'advanced',
  travel_speed: 'advanced',
  travel_acceleration: 'advanced',
  travel_jerk: 'advanced',
  wipe_tower_type: 'advanced',
  wipe_tower_wall_type: 'advanced',
  wipe_tower_bridging: 'advanced',
  wipe_tower_cone_angle: 'advanced',
  wipe_tower_rotation_angle: 'advanced',
  wipe_tower_extra_flow: 'advanced',
  wipe_tower_extra_spacing: 'advanced',
  wipe_tower_filament: 'advanced',
  wipe_tower_max_purge_speed: 'advanced',
  wipe_tower_no_sparse_layers: 'advanced',
  wipe_tower_fillet_wall: 'advanced',
  wipe_tower_rib_width: 'advanced',
  wipe_tower_extra_rib_length: 'advanced',
};

/**
 * Maps each OrcaMachineSettings key to its SimplyPrint/OrcaSlicer tab category.
 * Extras not in the SP catalog are assigned to the most appropriate category.
 */
export const ORCA_MACHINE_CATEGORY_MAP: Record<string, MachineCategory> = {
  // basic_information
  nozzle_size: 'basic_information',
  nozzle_volume_type: 'basic_information',
  printable_height: 'basic_information',
  support_multi_bed_types: 'basic_information',
  pellet_modded_printer: 'basic_information',
  nozzle_hrc: 'basic_information',
  support_chamber_temp_control: 'basic_information',
  support_air_filtration: 'basic_information',
  z_offset: 'basic_information',
  preferred_orientation: 'basic_information',
  scan_first_layer: 'basic_information',
  disable_m73: 'basic_information',
  thumbnails: 'basic_information',
  use_relative_e_distances: 'basic_information',
  use_firmware_retraction: 'basic_information',
  time_cost: 'basic_information',
  fan_speedup_overhangs: 'basic_information',
  fan_kickstart: 'basic_information',
  extruder_clearance_radius: 'basic_information',
  extruder_clearance_height_to_rod: 'basic_information',
  extruder_clearance_height_to_lid: 'basic_information',
  adaptive_bed_mesh_margin: 'basic_information',
  auxiliary_fan: 'basic_information',

  // machine_gcode
  file_start_gcode: 'machine_gcode',
  wrapping_detection_gcode: 'machine_gcode',
  gcode_flavor: 'machine_gcode',
  machine_start_gcode: 'machine_gcode',
  machine_end_gcode: 'machine_gcode',
  before_layer_change_gcode: 'machine_gcode',
  layer_change_gcode: 'machine_gcode',
  toolchange_gcode: 'machine_gcode',
  pause_print_gcode: 'machine_gcode',
  printing_by_object_gcode: 'machine_gcode',
  timelapse_gcode: 'machine_gcode',

  // multimaterial
  single_extruder_multi_material: 'multimaterial',
  manual_filament_change: 'multimaterial',
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
  support_multi_material: 'multimaterial',
  single_extruder_multi_material_priming: 'multimaterial',
  wipe_tower_type: 'multimaterial',
  wipe_tower_wall_type: 'multimaterial',
  wipe_tower_bridging: 'multimaterial',
  wipe_tower_cone_angle: 'multimaterial',
  wipe_tower_rotation_angle: 'multimaterial',
  wipe_tower_extra_flow: 'multimaterial',
  wipe_tower_extra_spacing: 'multimaterial',
  wipe_tower_filament: 'multimaterial',
  wipe_tower_max_purge_speed: 'multimaterial',
  wipe_tower_no_sparse_layers: 'multimaterial',
  wipe_tower_fillet_wall: 'multimaterial',
  wipe_tower_rib_width: 'multimaterial',
  wipe_tower_extra_rib_length: 'multimaterial',

  // extruder
  retraction_length: 'extruder',
  z_hop: 'extruder',
  long_retractions_when_cut: 'extruder',
  retraction_distances_when_cut: 'extruder',
  nozzle_diameter: 'extruder',
  nozzle_volume: 'extruder',
  extruder_printable_height: 'extruder',
  min_layer_height: 'extruder',
  max_layer_height: 'extruder',
  retract_restart_extra: 'extruder',
  retraction_speed: 'extruder',
  deretraction_speed: 'extruder',
  retraction_minimum_travel: 'extruder',
  retract_when_changing_layer: 'extruder',
  wipe: 'extruder',
  wipe_distance: 'extruder',
  retract_before_wipe: 'extruder',
  travel_slope: 'extruder',
  retract_lift_above: 'extruder',
  retract_lift_below: 'extruder',
  retract_length_toolchange: 'extruder',
  retract_restart_extra_toolchange: 'extruder',
  extruder_count: 'extruder',
  extruder_offset: 'extruder',
  extrusion_multiplier: 'extruder',
  extruder_type: 'extruder',
  extruder_colour: 'extruder',
  extruder_printable_area: 'extruder',
  retract_lift_enforce: 'extruder',
  z_hop_types: 'extruder',
  wipe_speed: 'extruder',
  wipe_before_external_loop: 'extruder',
  wipe_on_loops: 'extruder',

  // motion_ability
  emit_machine_limits_to_gcode: 'motion_ability',
  resonance_avoidance: 'motion_ability',
  min_resonance_avoidance_speed: 'motion_ability',
  max_resonance_avoidance_speed: 'motion_ability',
  motion_type: 'motion_ability',
  machine_max_acceleration_x: 'motion_ability',
  machine_max_acceleration_y: 'motion_ability',
  machine_max_acceleration_z: 'motion_ability',
  machine_max_acceleration_e: 'motion_ability',
  machine_max_jerk_x: 'motion_ability',
  machine_max_jerk_y: 'motion_ability',
  machine_max_jerk_z: 'motion_ability',
  machine_max_jerk_e: 'motion_ability',
  machine_max_speed_x: 'motion_ability',
  machine_max_speed_y: 'motion_ability',
  machine_max_speed_z: 'motion_ability',
  machine_max_speed_e: 'motion_ability',
  max_print_speed: 'motion_ability',
  machine_max_acceleration_travel: 'motion_ability',
  max_junction_deviation: 'motion_ability',
  support_arc_movement: 'motion_ability',
  arc_resolution: 'motion_ability',
  travel_speed: 'motion_ability',
  travel_acceleration: 'motion_ability',
  travel_jerk: 'motion_ability',

  // notes
  printer_notes: 'notes',

  // extras → basic_information (metadata, build volume, bed, capabilities, misc)
  inherits: 'basic_information',
  printer_model: 'basic_information',
  printer_variant: 'basic_information',
  thumbnails_format: 'basic_information',
  bed_size_x: 'basic_information',
  bed_size_y: 'basic_information',
  printable_area: 'basic_information',
  bed_origin: 'basic_information',
  bed_shape: 'basic_information',
  nozzle_type: 'basic_information',
  bed_type: 'basic_information',
  has_bed_probe: 'basic_information',
  probe_type: 'basic_information',
  mesh_bed_leveling: 'basic_information',
  bed_custom_texture: 'basic_information',
  bed_custom_model: 'basic_information',
  has_heated_bed: 'basic_information',
  has_heated_chamber: 'basic_information',
  max_bed_temperature: 'basic_information',
  max_chamber_temperature: 'basic_information',
  max_hotend_temperature: 'basic_information',
  has_scarf_joint_seam: 'basic_information',
  cooling_fan_count: 'basic_information',
  has_chamber_fan: 'basic_information',
  fan_max_speed: 'basic_information',
  silent_mode: 'basic_information',
  silent_mode_max_speed: 'basic_information',
  power_loss_recovery: 'basic_information',
  filament_sensor: 'basic_information',
  auto_leveling: 'basic_information',
  timelapse_type: 'basic_information',
  octoprint_host: 'basic_information',
  octoprint_api_key: 'basic_information',
  printer_width: 'basic_information',
  printer_depth: 'basic_information',
  printer_height: 'basic_information',
};

/** Sensible OrcaSlicer defaults for a generic Cartesian FDM printer */
export const DEFAULT_ORCA_MACHINE_SETTINGS: Partial<OrcaMachineSettings> = {
  // Build volume
  printable_height: 250,
  bed_size_x: 220,
  bed_size_y: 220,
  printable_area: '0x0,220x0,220x220,0x220',
  bed_shape: 'rectangular',
  bed_origin: 'corner',

  // Nozzle
  nozzle_size: 0.4,
  nozzle_diameter: 0.4,
  nozzle_volume_type: 'standard',
  nozzle_hrc: 0,
  nozzle_type: 'brass',

  // Layer heights
  min_layer_height: 0.08,
  max_layer_height: 0.28,

  // Retraction
  retraction_length: 0.8,
  retraction_speed: 35,
  deretraction_speed: 25,
  retract_restart_extra: 0,
  retraction_minimum_travel: 2.0,
  retract_when_changing_layer: false,
  retract_length_toolchange: 10,
  retract_restart_extra_toolchange: 0,

  // Z-hop
  z_hop: 0.2,
  retract_lift_above: 0,
  retract_lift_below: 0,

  // Wipe
  wipe: false,
  wipe_distance: 0,
  retract_before_wipe: 0,
  wipe_speed: 80,

  // Extruder clearance
  extruder_clearance_radius: 45,
  extruder_clearance_height_to_rod: 36,
  extruder_clearance_height_to_lid: 40,
  extruder_count: 1,
  extruder_offset: '0x0',
  extrusion_multiplier: 1.0,
  extruder_type: 'direct_drive',
  extruder_colour: '#FF8000',

  // G-code
  use_relative_e_distances: false,
  use_firmware_retraction: false,
  gcode_flavor: 'marlin2',
  machine_start_gcode: '; Start G-code\nG28 ; Home all axes\nG1 Z5 F3000 ; Lift nozzle',
  machine_end_gcode: '; End G-code\nM104 S0 ; Turn off hotend\nM140 S0 ; Turn off bed\nG28 X Y ; Home X and Y\nM84 ; Disable motors',
  thumbnails: '300x300,32x32',
  thumbnails_format: 'PNG',
  scan_first_layer: false,

  // Bed
  bed_type: 'textured_pei',
  has_bed_probe: true,
  probe_type: 'inductive',
  mesh_bed_leveling: true,

  // Capabilities
  has_heated_bed: true,
  has_heated_chamber: false,
  max_bed_temperature: 110,
  max_chamber_temperature: 0,
  max_hotend_temperature: 300,
  support_multi_bed_types: false,
  pellet_modded_printer: false,
  support_chamber_temp_control: false,
  support_air_filtration: false,
  auxiliary_fan: false,

  // Motion
  motion_type: 'cartesian',
  machine_max_acceleration_x: 3000,
  machine_max_acceleration_y: 3000,
  machine_max_acceleration_z: 500,
  machine_max_acceleration_e: 5000,
  machine_max_jerk_x: 8,
  machine_max_jerk_y: 8,
  machine_max_jerk_z: 0.4,
  machine_max_jerk_e: 2.5,
  machine_max_speed_x: 300,
  machine_max_speed_y: 300,
  machine_max_speed_z: 10,
  machine_max_speed_e: 120,
  max_print_speed: 250,
  machine_max_acceleration_travel: 5000,
  max_junction_deviation: 0.013,
  emit_machine_limits_to_gcode: true,

  // Cooling
  cooling_fan_count: 1,
  has_chamber_fan: false,

  // Multi-material
  single_extruder_multi_material: false,
  single_extruder_multi_material_priming: false,
  machine_load_filament_time: 0,
  machine_unload_filament_time: 0,
  machine_tool_change_time: 0,
  wipe_tower_type: 'sparse',
  wipe_tower_wall_type: 'single',

  // Features
  power_loss_recovery: false,
  filament_sensor: false,
  auto_leveling: true,

  // Physical dimensions
  printer_width: 400,
  printer_depth: 400,
  printer_height: 500,
};

/** Common printer presets with OrcaSlicer native keys */
export const PRINTER_PRESETS: Record<string, Partial<OrcaMachineSettings>> = {
  'Prusa MK4': {
    printer_model: 'MK4',
    bed_size_x: 250,
    bed_size_y: 210,
    printable_height: 220,
    nozzle_size: 0.4,
    nozzle_diameter: 0.4,
    max_print_speed: 500,
    motion_type: 'cartesian',
    gcode_flavor: 'marlin2',
    has_heated_bed: true,
    max_bed_temperature: 120,
    max_hotend_temperature: 290,
    filament_sensor: true,
    power_loss_recovery: true,
  },
  'Prusa CORE One': {
    printer_model: 'CORE One',
    bed_size_x: 250,
    bed_size_y: 220,
    printable_height: 270,
    nozzle_size: 0.4,
    nozzle_diameter: 0.4,
    max_print_speed: 600,
    motion_type: 'corexy',
    gcode_flavor: 'marlin2',
    has_heated_bed: true,
    has_heated_chamber: true,
    max_bed_temperature: 120,
    max_chamber_temperature: 55,
    max_hotend_temperature: 300,
    filament_sensor: true,
    power_loss_recovery: true,
  },
  'Voron 2.4': {
    printer_model: 'Voron 2.4',
    bed_size_x: 350,
    bed_size_y: 350,
    printable_height: 350,
    nozzle_size: 0.4,
    nozzle_diameter: 0.4,
    max_print_speed: 500,
    motion_type: 'corexy',
    gcode_flavor: 'klipper',
    has_heated_bed: true,
    has_heated_chamber: true,
    max_bed_temperature: 120,
    max_hotend_temperature: 300,
  },
  'Bambu Lab X1C': {
    printer_model: 'X1 Carbon',
    bed_size_x: 256,
    bed_size_y: 256,
    printable_height: 256,
    nozzle_size: 0.4,
    nozzle_diameter: 0.4,
    max_print_speed: 500,
    motion_type: 'corexy',
    gcode_flavor: 'marlin2',
    has_heated_bed: true,
    has_heated_chamber: true,
    max_bed_temperature: 110,
    max_chamber_temperature: 60,
    max_hotend_temperature: 300,
    support_multi_material: true,
    filament_sensor: true,
  },
  'Creality Ender 3 V3': {
    printer_model: 'Ender 3 V3',
    bed_size_x: 220,
    bed_size_y: 220,
    printable_height: 250,
    nozzle_size: 0.4,
    nozzle_diameter: 0.4,
    max_print_speed: 250,
    motion_type: 'cartesian',
    gcode_flavor: 'marlin2',
    has_heated_bed: true,
    max_bed_temperature: 100,
    max_hotend_temperature: 260,
  },
};

/** G-code flavor labels */
export const GCODE_DIALECT_LABELS: Record<string, string> = {
  marlin: 'Marlin 1.x',
  marlin2: 'Marlin 2.x',
  klipper: 'Klipper',
  reprap: 'RepRap Firmware',
  smoothie: 'Smoothieware',
  mach3: 'Mach3/LinuxCNC',
  custom: 'Custom',
};

/** Motion type labels */
export const MOTION_TYPE_LABELS: Record<string, string> = {
  cartesian: 'Cartesian (Bed Y)',
  corexy: 'CoreXY',
  delta: 'Delta',
  belt: 'Belt Printer',
};

/** Nozzle type labels */
export const NOZZLE_TYPE_LABELS: Record<string, string> = {
  brass: 'Brass',
  hardened_steel: 'Hardened Steel',
  stainless_steel: 'Stainless Steel',
  custom: 'Custom/Other',
};

/** Bed type labels */
export const BED_TYPE_LABELS: Record<string, string> = {
  textured_pei: 'Textured PEI',
  smooth_pei: 'Smooth PEI',
  glass: 'Glass',
  spring_steel: 'Spring Steel',
  custom: 'Custom',
};

/** Probe type labels */
export const PROBE_TYPE_LABELS: Record<string, string> = {
  bltouch: 'BLTouch / CRTouch',
  inductive: 'Inductive',
  capacitive: 'Capacitive',
  manual: 'Manual',
  none: 'None',
};
