/**
 * OrcaSlicer process settings type definitions.
 * Uses OrcaSlicer native snake_case property names throughout.
 */

import { useState, useCallback, useMemo } from 'react';

// =============================================================================
// VIEW MODE & CATEGORY TYPES
// =============================================================================

/** View modes for settings panel complexity */
export type SettingsViewMode = 'simple' | 'advanced';

/** Type alias for process settings view mode */
export type ProcessSettingsViewMode = 'simple' | 'advanced';

/** Category tabs in the settings panel */
export type SettingsCategory = 'quality' | 'strength' | 'speed' | 'support' | 'multimaterial' | 'others';

// =============================================================================
// ENUM TYPES
// =============================================================================

/** Infill patterns supported by OrcaSlicer */
export type InfillPattern =
  | 'grid'
  | 'triangles'
  | 'stars'
  | 'cubic'
  | 'line'
  | 'concentric'
  | 'honeycomb'
  | 'honeycomb3d'
  | 'gyroid'
  | 'hilbertcurve'
  | 'archimedeanchords'
  | 'octagramspiral'
  | 'adaptivecubic'
  | 'supportcubic'
  | 'lightning'
  | 'crosshatch';

/** Bed adhesion types */
export type BedAdhesionType = 'none' | 'skirt' | 'brim' | 'raft';

/** Support types */
export type SupportType = 'none' | 'normal' | 'tree' | 'tree_auto' | 'normal(auto)' | 'tree(auto)' | 'normal(manual)' | 'tree(manual)';

/** Seam position options */
export type SeamPosition = 'random' | 'aligned' | 'back' | 'nearest';

/** Scarf joint seam options */
export type ScarfJointSeam = 'none' | 'contour' | 'all';

/** Wall generator types */
export type WallGenerator = 'classic' | 'arachne';

/** Wall printing sequence */
export type WallSequence = 'inner wall/outer wall' | 'outer wall/inner wall' | 'inner-outer-inner wall';

/** Ironing type options */
export type IroningType = 'no_ironing' | 'top' | 'topmost' | 'all_solid';

/** Brim type options */
export type BrimType = 'no_brim' | 'outer_only' | 'inner_only' | 'outer_and_inner' | 'auto_brim';

/** Support style options */
export type SupportStyle = 'default' | 'grid' | 'snug' | 'organic';

/** Fuzzy skin mode options */
export type FuzzySkinMode = 'none' | 'external' | 'all';

/** Fuzzy skin noise type */
export type FuzzySkinNoiseType = 'classic' | 'perlin';

/** Gap fill target options */
export type GapFillTarget = 'everywhere' | 'topbottom' | 'nowhere';

/** Slicing mode options */
export type SlicingMode = 'regular' | 'even_odd' | 'close_holes';

// =============================================================================
// MAIN SETTINGS INTERFACE
// =============================================================================

/**
 * OrcaSlicer process profile settings using native snake_case property names.
 * All properties are optional — profiles may contain partial settings.
 */
export interface OrcaProcessSettings {

  // ---------------------------------------------------------------------------
  // Quality tab — Layer / Line width
  // ---------------------------------------------------------------------------

  layer_height?: number;
  initial_layer_print_height?: number;
  line_width?: number;
  initial_layer_line_width?: number;
  outer_wall_line_width?: number;
  inner_wall_line_width?: number;
  top_surface_line_width?: number;
  sparse_infill_line_width?: number;
  internal_solid_infill_line_width?: number;
  support_line_width?: number;

  // Quality tab — Seam

  seam_position?: SeamPosition;
  seam_gap?: number;
  seam_slope_type?: ScarfJointSeam;
  staggered_inner_seams?: boolean;
  seam_slope_conditional?: boolean;
  scarf_angle_threshold?: number;
  scarf_overhang_threshold?: number;
  scarf_joint_speed?: number;
  seam_slope_start_height?: number;
  seam_slope_entire_loop?: boolean;
  seam_slope_min_length?: number;
  seam_slope_steps?: number;
  scarf_joint_flow_ratio?: number;
  seam_slope_inner_walls?: boolean;

  // Quality tab — Wipe

  role_based_wipe_speed?: boolean;
  wipe_speed?: number;
  wipe_on_loops?: boolean;
  wipe_before_external_loop?: boolean;

  // Quality tab — Precision / Compensation

  slice_closing_radius?: number;
  resolution?: number;
  enable_arc_fitting?: boolean;
  xy_hole_compensation?: number;
  xy_contour_compensation?: number;
  elefant_foot_compensation?: number;
  elefant_foot_compensation_layers?: number;
  precise_outer_wall?: boolean;
  precise_z_height?: boolean;
  hole_to_polyhole?: boolean;
  hole_to_polyhole_threshold?: number;
  hole_to_polyhole_twisted?: boolean;

  // Quality tab — Ironing

  ironing_type?: IroningType;
  ironing_pattern?: 'rectilinear' | 'concentric';
  ironing_flow?: number;
  ironing_spacing?: number;
  ironing_angle?: number;
  ironing_angle_fixed?: number;
  ironing_inset?: number;

  // Quality tab — Wall generator (Arachne)

  wall_generator?: WallGenerator;
  wall_transition_angle?: number;
  wall_transition_filter_deviation?: number;
  wall_transition_length?: number;
  wall_distribution_count?: number;
  initial_layer_min_bead_width?: number;
  min_bead_width?: number;
  min_feature_size?: number;
  min_length_factor?: number;
  wall_sequence?: WallSequence;
  wall_direction?: number;
  min_wall_thickness?: number;

  // Quality tab — Flow ratios

  print_flow_ratio?: number;
  outer_wall_flow_ratio?: number;
  inner_wall_flow_ratio?: number;
  top_solid_infill_flow_ratio?: number;
  bottom_solid_infill_flow_ratio?: number;
  set_other_flow_ratios?: boolean;
  first_layer_flow_ratio?: number;
  overhang_flow_ratio?: number;
  sparse_infill_flow_ratio?: number;
  internal_solid_infill_flow_ratio?: number;
  gap_fill_flow_ratio?: number;
  support_flow_ratio?: number;
  support_interface_flow_ratio?: number;

  // Quality tab — Single-wall / Sequence

  only_one_wall_first_layer?: boolean;
  only_one_wall_top?: boolean;
  min_width_top_surface?: number;
  reduce_crossing_wall?: boolean;
  max_travel_detour_distance?: number;
  first_layer_sequence_choice?: string;
  other_layers_sequence_choice?: string;
  is_infill_first?: boolean;

  // Quality tab — Bridging

  bridge_flow?: number;
  internal_bridge_flow?: number;
  bridge_density?: number;
  internal_bridge_density?: number;
  thick_bridges?: boolean;
  thick_internal_bridges?: boolean;
  enable_extra_bridge_layer?: boolean;
  dont_filter_internal_bridges?: boolean;
  counterbore_hole_bridging?: string;

  // Quality tab — Overhangs

  detect_overhang_wall?: boolean;
  make_overhang_printable?: boolean;
  make_overhang_printable_angle?: number;
  make_overhang_printable_hole_size?: number;
  extra_perimeters_on_overhangs?: boolean;
  overhang_reverse?: boolean;
  overhang_reverse_internal_only?: boolean;
  overhang_reverse_threshold?: number;

  // Quality tab — Small area infill flow compensation

  small_area_infill_flow_compensation?: boolean;
  small_area_infill_flow_compensation_model?: string;

  // Quality tab — Filament ironing (per-filament overrides)

  filament_ironing_flow?: number;
  filament_ironing_inset?: number;
  filament_ironing_spacing?: number;
  filament_ironing_speed?: number;

  // ---------------------------------------------------------------------------
  // Strength tab — Walls
  // ---------------------------------------------------------------------------

  wall_loops?: number;
  alternate_extra_wall?: boolean;
  detect_thin_wall?: boolean;

  // Strength tab — Top/bottom shells

  top_shell_layers?: number;
  top_shell_thickness?: number;
  top_surface_density?: number;
  top_surface_pattern?: string;
  bottom_shell_layers?: number;
  bottom_shell_thickness?: number;
  bottom_surface_density?: number;
  bottom_surface_pattern?: string;
  top_bottom_infill_wall_overlap?: number;

  // Strength tab — Infill

  sparse_infill_density?: number;
  sparse_infill_pattern?: InfillPattern;
  fill_multiline?: boolean;
  infill_direction?: number;
  sparse_infill_rotate_template?: boolean;
  skin_infill_density?: number;
  skeleton_infill_density?: number;
  infill_lock_depth?: number;
  skin_infill_depth?: number;
  skin_infill_line_width?: number;
  skeleton_infill_line_width?: number;
  symmetric_infill_y_axis?: boolean;
  infill_shift_step?: number;
  lateral_lattice_angle_1?: number;
  lateral_lattice_angle_2?: number;
  infill_overhang_angle?: number;
  infill_wall_overlap?: number;
  infill_anchor_max?: number;
  infill_anchor?: number;
  internal_solid_infill_pattern?: string;
  solid_infill_direction?: number;
  solid_infill_rotate_template?: boolean;
  gap_fill_target?: GapFillTarget;
  filter_out_gap_fill?: number;
  align_infill_direction_to_model?: boolean;
  extra_solid_infills?: boolean;
  bridge_angle?: number;
  internal_bridge_angle?: number;
  minimum_sparse_infill_area?: number;
  infill_combination?: boolean;
  infill_combination_max_layer_height?: number;
  detect_narrow_internal_solid_infill?: boolean;
  ensure_vertical_shell_thickness?: string;

  // ---------------------------------------------------------------------------
  // Speed tab — Print speeds
  // ---------------------------------------------------------------------------

  initial_layer_speed?: number;
  initial_layer_infill_speed?: number;
  initial_layer_travel_speed?: number;
  slow_down_layers?: number;
  outer_wall_speed?: number;
  inner_wall_speed?: number;
  small_perimeter_speed?: number;
  small_perimeter_threshold?: number;
  sparse_infill_speed?: number;
  internal_solid_infill_speed?: number;
  top_surface_speed?: number;
  gap_infill_speed?: number;
  ironing_speed?: number;
  support_speed?: number;
  support_interface_speed?: number;
  bridge_speed?: number;
  internal_bridge_speed?: number;
  travel_speed?: number;

  // Speed tab — Overhang speeds

  enable_overhang_speed?: boolean;
  slowdown_for_curled_perimeters?: boolean;
  overhang_speed_classic?: number;
  overhang_1_4_speed?: number;
  overhang_2_4_speed?: number;
  overhang_3_4_speed?: number;
  overhang_4_4_speed?: number;

  // Speed tab — Acceleration

  default_acceleration?: number;
  outer_wall_acceleration?: number;
  inner_wall_acceleration?: number;
  bridge_acceleration?: number;
  sparse_infill_acceleration?: number;
  internal_solid_infill_acceleration?: number;
  initial_layer_acceleration?: number;
  top_surface_acceleration?: number;
  travel_acceleration?: number;
  accel_to_decel_enable?: boolean;
  accel_to_decel_factor?: number;

  // Speed tab — Jerk / Junction deviation

  default_junction_deviation?: number;
  default_jerk?: number;
  outer_wall_jerk?: number;
  inner_wall_jerk?: number;
  infill_jerk?: number;
  top_surface_jerk?: number;
  initial_layer_jerk?: number;
  travel_jerk?: number;

  // Speed tab — Volumetric flow rate

  max_volumetric_extrusion_rate_slope?: number;
  max_volumetric_extrusion_rate_slope_segment_length?: number;
  extrusion_rate_smoothing_external_perimeter_only?: boolean;

  // Speed tab — Slow-down for cooling

  slow_down_layer_time?: number;
  slow_down_min_speed?: number;

  // ---------------------------------------------------------------------------
  // Support tab — Main support
  // ---------------------------------------------------------------------------

  enable_support?: boolean;
  support_type?: SupportType;
  support_style?: SupportStyle;
  support_threshold_angle?: number;
  support_threshold_overlap?: number;
  support_on_build_plate_only?: boolean;
  support_critical_regions_only?: boolean;
  support_remove_small_overhang?: boolean;
  support_angle?: number;

  // Support tab — Raft

  raft_layers?: number;
  raft_contact_distance?: number;
  raft_expansion?: number;
  raft_first_layer_density?: number;
  raft_first_layer_expansion?: number;

  // Support tab — Support filaments

  support_filament?: number;
  support_interface_filament?: number;
  support_interface_not_for_body?: boolean;

  // Support tab — Support ironing

  support_ironing?: boolean;
  support_ironing_flow?: number;
  support_ironing_pattern?: string;
  support_ironing_spacing?: number;

  // Support tab — Support geometry

  support_top_z_distance?: number;
  support_bottom_z_distance?: number;
  tree_support_wall_count?: number;
  support_base_pattern_spacing?: number;
  support_base_pattern?: string;
  support_interface_top_layers?: number;
  support_interface_bottom_layers?: number;
  support_interface_pattern?: string;
  support_interface_spacing?: number;
  support_bottom_interface_spacing?: number;
  support_expansion?: number;
  support_interface_loop_pattern?: boolean;
  support_object_xy_distance?: number;
  support_object_first_layer_gap?: number;
  bridge_no_support?: boolean;
  max_bridge_length?: number;
  independent_support_layer_height?: boolean;

  // Support tab — Tree support

  tree_support_tip_diameter?: number;
  tree_support_branch_distance?: number;
  tree_support_branch_distance_organic?: number;
  tree_support_top_rate?: number;
  tree_support_branch_diameter?: number;
  tree_support_branch_diameter_organic?: number;
  tree_support_branch_diameter_angle?: number;
  tree_support_branch_angle?: number;
  tree_support_branch_angle_organic?: number;
  tree_support_angle_slow?: number;
  tree_support_auto_brim?: boolean;
  tree_support_brim_width?: number;
  tree_support_with_infill?: boolean;

  // ---------------------------------------------------------------------------
  // Multimaterial tab — Prime / Wipe tower
  // ---------------------------------------------------------------------------

  enable_prime_tower?: boolean;
  prime_tower_width?: number;
  purge_on_layer_change?: boolean;
  prime_volume?: number;
  preheat_steps?: number;
  flush_into_infill?: boolean;
  flush_into_objects?: boolean;
  flush_into_support?: boolean;
  prime_tower_skip_points?: boolean;
  enable_tower_interface_features?: boolean;
  enable_tower_interface_cooldown_during_tower?: boolean;
  prime_tower_enable_framework?: boolean;
  prime_tower_brim_width?: number;
  prime_tower_infill_gap?: number;
  wipe_tower_rotation_angle?: number;
  wipe_tower_bridging?: number;
  wipe_tower_extra_spacing?: number;
  wipe_tower_extra_flow?: number;
  wipe_tower_max_purge_speed?: number;
  wipe_tower_cone_angle?: number;
  wipe_tower_extra_rib_length?: number;
  wipe_tower_rib_width?: number;
  wipe_tower_fillet_wall?: boolean;
  wipe_tower_no_sparse_layers?: boolean;

  // Multimaterial tab — Filament assignment

  wall_filament?: string;
  sparse_infill_filament?: string;
  solid_infill_filament?: string;

  // Multimaterial tab — Other

  single_extruder_multi_material_priming?: boolean;
  ooze_prevention?: boolean;
  standby_temperature_delta?: number;
  preheat_time?: number;
  interlocking_beam?: boolean;
  interface_shells?: boolean;
  mmu_segmented_region_max_width?: number;
  mmu_segmented_region_interlocking_depth?: number;
  interlocking_beam_width?: number;
  interlocking_orientation?: number;
  interlocking_beam_layer_count?: number;
  interlocking_depth?: number;
  interlocking_boundary_avoidance?: number;

  // ---------------------------------------------------------------------------
  // Others tab — Skirt
  // ---------------------------------------------------------------------------

  skirt_loops?: number;
  skirt_height?: number;
  min_skirt_length?: number;
  skirt_distance?: number;
  skirt_start_angle?: number;
  skirt_speed?: number;
  single_loop_draft_shield?: boolean;

  // Others tab — Brim

  brim_type?: BrimType;
  brim_width?: number;
  brim_object_gap?: number;
  brim_use_efc_outline?: boolean;
  combine_brims?: boolean;
  brim_ears_max_angle?: number;
  brim_ears_detection_length?: number;

  // Others tab — Special modes

  spiral_mode?: boolean;
  spiral_mode_smooth?: boolean;
  spiral_mode_max_xy_smoothing?: number;
  spiral_starting_flow_ratio?: number;
  spiral_finishing_flow_ratio?: number;
  print_sequence?: 'by_layer' | 'by_object';
  slicing_mode?: SlicingMode;
  enable_wrapping_detection?: boolean;

  // Others tab — Fuzzy skin

  fuzzy_skin?: FuzzySkinMode;
  fuzzy_skin_mode?: FuzzySkinMode;
  fuzzy_skin_noise_type?: FuzzySkinNoiseType;
  fuzzy_skin_point_distance?: number;
  fuzzy_skin_thickness?: number;
  fuzzy_skin_scale?: number;
  fuzzy_skin_octaves?: number;
  fuzzy_skin_persistence?: number;
  fuzzy_skin_first_layer?: boolean;

  // Others tab — GCode / misc

  reduce_infill_retraction?: boolean;
  gcode_add_line_number?: boolean;
  gcode_comments?: boolean;
  gcode_label_objects?: boolean;
  exclude_object?: boolean;
  notes?: string;
  timelapse?: string;

  // ---------------------------------------------------------------------------
  // Extended — Temperature (filament profile settings kept for legacy compat)
  // ---------------------------------------------------------------------------

  nozzle_temperature?: number;
  nozzle_temperature_initial_layer?: number;
  hot_plate_temp?: number;
  hot_plate_temp_initial_layer?: number;

  // Extended — Retraction

  filament_retraction_length?: number;
  filament_retraction_speed?: number;
  filament_deretraction_speed?: number;
  filament_retraction_minimum_travel?: number;
  filament_retract_when_changing_layer?: boolean;
  filament_retract_before_wipe?: boolean;
  filament_z_hop?: number;

  // Extended — Cooling

  fan_cooling?: boolean;
  fan_min_speed?: number;
  fan_max_speed?: number;
  overhang_fan_speed?: number;
  full_fan_speed_layer?: number;
}

// =============================================================================
// DEFAULTS
// =============================================================================

/** Default values for OrcaSlicer process settings */
export const DEFAULT_ORCA_PROCESS_SETTINGS: OrcaProcessSettings = {
  // Quality — Layer / Line width
  layer_height: 0.2,
  initial_layer_print_height: 0.2,
  line_width: 0.45,
  initial_layer_line_width: 0.5,
  outer_wall_line_width: 0.45,
  inner_wall_line_width: 0.45,
  top_surface_line_width: 0.45,
  sparse_infill_line_width: 0.45,
  internal_solid_infill_line_width: 0.45,
  support_line_width: 0.45,
  // Quality — Seam
  seam_position: 'aligned',
  seam_gap: 0,
  seam_slope_type: 'none',
  staggered_inner_seams: false,
  seam_slope_conditional: false,
  scarf_angle_threshold: 0,
  scarf_overhang_threshold: 0,
  scarf_joint_speed: 0,
  seam_slope_start_height: 0,
  seam_slope_entire_loop: false,
  seam_slope_min_length: 10,
  seam_slope_steps: 10,
  scarf_joint_flow_ratio: 1.0,
  seam_slope_inner_walls: false,
  // Quality — Wipe
  role_based_wipe_speed: false,
  wipe_speed: 80,
  wipe_on_loops: true,
  wipe_before_external_loop: false,
  // Quality — Precision
  slice_closing_radius: 0.05,
  resolution: 0.01,
  enable_arc_fitting: false,
  xy_hole_compensation: 0,
  xy_contour_compensation: 0,
  elefant_foot_compensation: 0.15,
  elefant_foot_compensation_layers: 1,
  precise_outer_wall: true,
  precise_z_height: false,
  hole_to_polyhole: false,
  hole_to_polyhole_threshold: 0.01,
  // Quality — Ironing
  ironing_type: 'no_ironing',
  ironing_pattern: 'rectilinear',
  ironing_flow: 15,
  ironing_spacing: 0.1,
  ironing_angle: -1,
  // Quality — Flow ratios
  print_flow_ratio: 1.0,
  only_one_wall_first_layer: false,
  only_one_wall_top: false,
  // Strength — Walls
  wall_loops: 3,
  // Strength — Top/bottom shells
  top_shell_layers: 4,
  bottom_shell_layers: 3,
  // Strength — Infill
  sparse_infill_density: 20,
  sparse_infill_pattern: 'crosshatch',
  infill_wall_overlap: 10,
  infill_anchor_max: 10,
  // Speed — Print speeds
  outer_wall_speed: 60,
  inner_wall_speed: 200,
  sparse_infill_speed: 200,
  internal_solid_infill_speed: 200,
  top_surface_speed: 60,
  travel_speed: 250,
  initial_layer_speed: 40,
  // Speed — Acceleration
  outer_wall_acceleration: 500,
  inner_wall_acceleration: 1000,
  top_surface_acceleration: 500,
  sparse_infill_acceleration: 2000,
  travel_acceleration: 5000,
  default_acceleration: 5000,
  // Speed — Slow-down
  slow_down_layer_time: 5,
  slow_down_min_speed: 10,
  // Support
  enable_support: false,
  support_type: 'none',
  support_threshold_angle: 45,
  support_top_z_distance: 0.2,
  support_bottom_z_distance: 0.2,
  support_interface_top_layers: 2,
  support_object_xy_distance: 0.35,
  support_interface_bottom_layers: 0,
  support_base_pattern_spacing: 15,
  // Others — Skirt/Brim
  skirt_loops: 1,
  brim_type: 'no_brim',
  brim_width: 0,
  // Extended — Temperature
  nozzle_temperature: 220,
  nozzle_temperature_initial_layer: 220,
  hot_plate_temp: 60,
  hot_plate_temp_initial_layer: 60,
  // Extended — Retraction
  filament_retraction_length: 1.0,
  filament_retraction_speed: 40,
  filament_deretraction_speed: 40,
  filament_retraction_minimum_travel: 1.0,
  filament_retract_when_changing_layer: false,
  filament_retract_before_wipe: false,
  filament_z_hop: 0,
  // Extended — Cooling
  fan_cooling: true,
  fan_min_speed: 30,
  fan_max_speed: 100,
  overhang_fan_speed: 100,
  full_fan_speed_layer: 3,
};

// =============================================================================
// HELPER INFO OBJECTS
// =============================================================================

/** Infill pattern display names and descriptions */
export const INFILL_PATTERN_INFO: Record<InfillPattern, { label: string; description: string }> = {
  grid: { label: 'Grid', description: 'Simple perpendicular lines' },
  triangles: { label: 'Triangles', description: 'Triangular pattern for moderate strength' },
  stars: { label: 'Stars', description: 'Star-shaped pattern' },
  cubic: { label: 'Cubic', description: '3D cubic pattern for strength' },
  line: { label: 'Lines', description: 'Simple parallel lines, fastest' },
  concentric: { label: 'Concentric', description: 'Follows the outline-solid shape' },
  honeycomb: { label: 'Honeycomb', description: 'Hexagonal pattern, strong and efficient' },
  honeycomb3d: { label: '3D Honeycomb', description: '3D hexagonal structure' },
  gyroid: { label: 'Gyroid', description: 'Complex 3D pattern, excellent strength-to-weight' },
  hilbertcurve: { label: 'Hilbert Curve', description: 'Space-filling curve pattern' },
  archimedeanchords: { label: 'Archimedean Chords', description: 'Spiral-based pattern' },
  octagramspiral: { label: 'Octagram Spiral', description: 'Eight-pointed spiral' },
  adaptivecubic: { label: 'Adaptive Cubic', description: 'Varies density based on geometry' },
  supportcubic: { label: 'Support Cubic', description: 'Optimized for support structures' },
  lightning: { label: 'Lightning', description: 'Tree-like structure, very low material usage' },
  crosshatch: { label: 'Cross Hatch', description: 'Diagonal crosshatch pattern' },
};

/** Bed adhesion option info */
export const BED_ADHESION_INFO: Record<BedAdhesionType, { label: string; description: string }> = {
  none: { label: 'None', description: 'No additional adhesion helper' },
  skirt: { label: 'Skirt', description: 'Outline around the print to prime the nozzle' },
  brim: { label: 'Brim', description: 'Wide flat layer around the base for better adhesion' },
  raft: { label: 'Raft', description: 'Full platform under the print for maximum adhesion' },
};

// =============================================================================
// CATEGORY MAP
// =============================================================================

/**
 * Maps each native snake_case setting key to its UI category tab.
 * Based on SimplyPrint tab assignments; extras assigned to best-fit category.
 */
export const ORCA_PROCESS_CATEGORY_MAP: Record<string, SettingsCategory> = {
  // Quality tab
  layer_height: 'quality',
  initial_layer_print_height: 'quality',
  line_width: 'quality',
  initial_layer_line_width: 'quality',
  outer_wall_line_width: 'quality',
  inner_wall_line_width: 'quality',
  top_surface_line_width: 'quality',
  sparse_infill_line_width: 'quality',
  internal_solid_infill_line_width: 'quality',
  support_line_width: 'quality',
  seam_position: 'quality',
  seam_gap: 'quality',
  seam_slope_type: 'quality',
  staggered_inner_seams: 'quality',
  seam_slope_conditional: 'quality',
  scarf_angle_threshold: 'quality',
  scarf_overhang_threshold: 'quality',
  scarf_joint_speed: 'quality',
  seam_slope_start_height: 'quality',
  seam_slope_entire_loop: 'quality',
  seam_slope_min_length: 'quality',
  seam_slope_steps: 'quality',
  scarf_joint_flow_ratio: 'quality',
  seam_slope_inner_walls: 'quality',
  role_based_wipe_speed: 'quality',
  wipe_speed: 'quality',
  wipe_on_loops: 'quality',
  wipe_before_external_loop: 'quality',
  slice_closing_radius: 'quality',
  resolution: 'quality',
  enable_arc_fitting: 'quality',
  xy_hole_compensation: 'quality',
  xy_contour_compensation: 'quality',
  elefant_foot_compensation: 'quality',
  elefant_foot_compensation_layers: 'quality',
  precise_outer_wall: 'quality',
  precise_z_height: 'quality',
  hole_to_polyhole: 'quality',
  hole_to_polyhole_threshold: 'quality',
  hole_to_polyhole_twisted: 'quality',
  ironing_type: 'quality',
  ironing_pattern: 'quality',
  ironing_flow: 'quality',
  ironing_spacing: 'quality',
  ironing_angle: 'quality',
  ironing_angle_fixed: 'quality',
  ironing_inset: 'quality',
  wall_generator: 'quality',
  wall_transition_angle: 'quality',
  wall_transition_filter_deviation: 'quality',
  wall_transition_length: 'quality',
  wall_distribution_count: 'quality',
  initial_layer_min_bead_width: 'quality',
  min_bead_width: 'quality',
  min_feature_size: 'quality',
  min_length_factor: 'quality',
  wall_sequence: 'quality',
  wall_direction: 'quality',
  min_wall_thickness: 'quality',
  print_flow_ratio: 'quality',
  outer_wall_flow_ratio: 'quality',
  inner_wall_flow_ratio: 'quality',
  top_solid_infill_flow_ratio: 'quality',
  bottom_solid_infill_flow_ratio: 'quality',
  set_other_flow_ratios: 'quality',
  first_layer_flow_ratio: 'quality',
  overhang_flow_ratio: 'quality',
  sparse_infill_flow_ratio: 'quality',
  internal_solid_infill_flow_ratio: 'quality',
  gap_fill_flow_ratio: 'quality',
  support_flow_ratio: 'quality',
  support_interface_flow_ratio: 'quality',
  only_one_wall_first_layer: 'quality',
  only_one_wall_top: 'quality',
  min_width_top_surface: 'quality',
  reduce_crossing_wall: 'quality',
  max_travel_detour_distance: 'quality',
  first_layer_sequence_choice: 'quality',
  other_layers_sequence_choice: 'quality',
  is_infill_first: 'quality',
  bridge_flow: 'quality',
  internal_bridge_flow: 'quality',
  bridge_density: 'quality',
  internal_bridge_density: 'quality',
  thick_bridges: 'quality',
  thick_internal_bridges: 'quality',
  enable_extra_bridge_layer: 'quality',
  dont_filter_internal_bridges: 'quality',
  counterbore_hole_bridging: 'quality',
  detect_overhang_wall: 'quality',
  make_overhang_printable: 'quality',
  make_overhang_printable_angle: 'quality',
  make_overhang_printable_hole_size: 'quality',
  extra_perimeters_on_overhangs: 'quality',
  overhang_reverse: 'quality',
  overhang_reverse_internal_only: 'quality',
  overhang_reverse_threshold: 'quality',
  small_area_infill_flow_compensation: 'quality',
  small_area_infill_flow_compensation_model: 'quality',
  filament_ironing_flow: 'quality',
  filament_ironing_inset: 'quality',
  filament_ironing_spacing: 'quality',
  filament_ironing_speed: 'quality',

  // Strength tab
  wall_loops: 'strength',
  alternate_extra_wall: 'strength',
  detect_thin_wall: 'strength',
  top_shell_layers: 'strength',
  top_shell_thickness: 'strength',
  top_surface_density: 'strength',
  top_surface_pattern: 'strength',
  bottom_shell_layers: 'strength',
  bottom_shell_thickness: 'strength',
  bottom_surface_density: 'strength',
  bottom_surface_pattern: 'strength',
  top_bottom_infill_wall_overlap: 'strength',
  sparse_infill_density: 'strength',
  sparse_infill_pattern: 'strength',
  fill_multiline: 'strength',
  infill_direction: 'strength',
  sparse_infill_rotate_template: 'strength',
  skin_infill_density: 'strength',
  skeleton_infill_density: 'strength',
  infill_lock_depth: 'strength',
  skin_infill_depth: 'strength',
  skin_infill_line_width: 'strength',
  skeleton_infill_line_width: 'strength',
  symmetric_infill_y_axis: 'strength',
  infill_shift_step: 'strength',
  lateral_lattice_angle_1: 'strength',
  lateral_lattice_angle_2: 'strength',
  infill_overhang_angle: 'strength',
  infill_wall_overlap: 'strength',
  infill_anchor_max: 'strength',
  infill_anchor: 'strength',
  internal_solid_infill_pattern: 'strength',
  solid_infill_direction: 'strength',
  solid_infill_rotate_template: 'strength',
  gap_fill_target: 'strength',
  filter_out_gap_fill: 'strength',
  align_infill_direction_to_model: 'strength',
  extra_solid_infills: 'strength',
  bridge_angle: 'strength',
  internal_bridge_angle: 'strength',
  minimum_sparse_infill_area: 'strength',
  infill_combination: 'strength',
  infill_combination_max_layer_height: 'strength',
  detect_narrow_internal_solid_infill: 'strength',
  ensure_vertical_shell_thickness: 'strength',

  // Speed tab
  initial_layer_speed: 'speed',
  initial_layer_infill_speed: 'speed',
  initial_layer_travel_speed: 'speed',
  slow_down_layers: 'speed',
  outer_wall_speed: 'speed',
  inner_wall_speed: 'speed',
  small_perimeter_speed: 'speed',
  small_perimeter_threshold: 'speed',
  sparse_infill_speed: 'speed',
  internal_solid_infill_speed: 'speed',
  top_surface_speed: 'speed',
  gap_infill_speed: 'speed',
  ironing_speed: 'speed',
  support_speed: 'speed',
  support_interface_speed: 'speed',
  bridge_speed: 'speed',
  internal_bridge_speed: 'speed',
  travel_speed: 'speed',
  enable_overhang_speed: 'speed',
  slowdown_for_curled_perimeters: 'speed',
  overhang_speed_classic: 'speed',
  overhang_1_4_speed: 'speed',
  overhang_2_4_speed: 'speed',
  overhang_3_4_speed: 'speed',
  overhang_4_4_speed: 'speed',
  default_acceleration: 'speed',
  outer_wall_acceleration: 'speed',
  inner_wall_acceleration: 'speed',
  bridge_acceleration: 'speed',
  sparse_infill_acceleration: 'speed',
  internal_solid_infill_acceleration: 'speed',
  initial_layer_acceleration: 'speed',
  top_surface_acceleration: 'speed',
  travel_acceleration: 'speed',
  accel_to_decel_enable: 'speed',
  accel_to_decel_factor: 'speed',
  default_junction_deviation: 'speed',
  default_jerk: 'speed',
  outer_wall_jerk: 'speed',
  inner_wall_jerk: 'speed',
  infill_jerk: 'speed',
  top_surface_jerk: 'speed',
  initial_layer_jerk: 'speed',
  travel_jerk: 'speed',
  max_volumetric_extrusion_rate_slope: 'speed',
  max_volumetric_extrusion_rate_slope_segment_length: 'speed',
  extrusion_rate_smoothing_external_perimeter_only: 'speed',
  slow_down_layer_time: 'speed',
  slow_down_min_speed: 'speed',

  // Support tab
  enable_support: 'support',
  support_type: 'support',
  support_style: 'support',
  support_threshold_angle: 'support',
  support_threshold_overlap: 'support',
  support_on_build_plate_only: 'support',
  support_critical_regions_only: 'support',
  support_remove_small_overhang: 'support',
  support_angle: 'support',
  raft_layers: 'support',
  raft_contact_distance: 'support',
  raft_expansion: 'support',
  raft_first_layer_density: 'support',
  raft_first_layer_expansion: 'support',
  support_filament: 'support',
  support_interface_filament: 'support',
  support_interface_not_for_body: 'support',
  support_ironing: 'support',
  support_ironing_flow: 'support',
  support_ironing_pattern: 'support',
  support_ironing_spacing: 'support',
  support_top_z_distance: 'support',
  support_bottom_z_distance: 'support',
  tree_support_wall_count: 'support',
  support_base_pattern_spacing: 'support',
  support_base_pattern: 'support',
  support_interface_top_layers: 'support',
  support_interface_bottom_layers: 'support',
  support_interface_pattern: 'support',
  support_interface_spacing: 'support',
  support_bottom_interface_spacing: 'support',
  support_expansion: 'support',
  support_interface_loop_pattern: 'support',
  support_object_xy_distance: 'support',
  support_object_first_layer_gap: 'support',
  bridge_no_support: 'support',
  max_bridge_length: 'support',
  independent_support_layer_height: 'support',
  tree_support_tip_diameter: 'support',
  tree_support_branch_distance: 'support',
  tree_support_branch_distance_organic: 'support',
  tree_support_top_rate: 'support',
  tree_support_branch_diameter: 'support',
  tree_support_branch_diameter_organic: 'support',
  tree_support_branch_diameter_angle: 'support',
  tree_support_branch_angle: 'support',
  tree_support_branch_angle_organic: 'support',
  tree_support_angle_slow: 'support',
  tree_support_auto_brim: 'support',
  tree_support_brim_width: 'support',
  tree_support_with_infill: 'support',

  // Multimaterial tab
  enable_prime_tower: 'multimaterial',
  prime_tower_width: 'multimaterial',
  purge_on_layer_change: 'multimaterial',
  prime_volume: 'multimaterial',
  preheat_steps: 'multimaterial',
  flush_into_infill: 'multimaterial',
  flush_into_objects: 'multimaterial',
  flush_into_support: 'multimaterial',
  prime_tower_skip_points: 'multimaterial',
  enable_tower_interface_features: 'multimaterial',
  enable_tower_interface_cooldown_during_tower: 'multimaterial',
  prime_tower_enable_framework: 'multimaterial',
  prime_tower_brim_width: 'multimaterial',
  prime_tower_infill_gap: 'multimaterial',
  wipe_tower_rotation_angle: 'multimaterial',
  wipe_tower_bridging: 'multimaterial',
  wipe_tower_extra_spacing: 'multimaterial',
  wipe_tower_extra_flow: 'multimaterial',
  wipe_tower_max_purge_speed: 'multimaterial',
  wipe_tower_cone_angle: 'multimaterial',
  wipe_tower_extra_rib_length: 'multimaterial',
  wipe_tower_rib_width: 'multimaterial',
  wipe_tower_fillet_wall: 'multimaterial',
  wipe_tower_no_sparse_layers: 'multimaterial',
  wall_filament: 'multimaterial',
  sparse_infill_filament: 'multimaterial',
  solid_infill_filament: 'multimaterial',
  single_extruder_multi_material_priming: 'multimaterial',
  ooze_prevention: 'multimaterial',
  standby_temperature_delta: 'multimaterial',
  preheat_time: 'multimaterial',
  interlocking_beam: 'multimaterial',
  interface_shells: 'multimaterial',
  mmu_segmented_region_max_width: 'multimaterial',
  mmu_segmented_region_interlocking_depth: 'multimaterial',
  interlocking_beam_width: 'multimaterial',
  interlocking_orientation: 'multimaterial',
  interlocking_beam_layer_count: 'multimaterial',
  interlocking_depth: 'multimaterial',
  interlocking_boundary_avoidance: 'multimaterial',

  // Others tab
  skirt_loops: 'others',
  skirt_height: 'others',
  min_skirt_length: 'others',
  skirt_distance: 'others',
  skirt_start_angle: 'others',
  skirt_speed: 'others',
  single_loop_draft_shield: 'others',
  brim_type: 'others',
  brim_width: 'others',
  brim_object_gap: 'others',
  brim_use_efc_outline: 'others',
  combine_brims: 'others',
  brim_ears_max_angle: 'others',
  brim_ears_detection_length: 'others',
  spiral_mode: 'others',
  spiral_mode_smooth: 'others',
  spiral_mode_max_xy_smoothing: 'others',
  spiral_starting_flow_ratio: 'others',
  spiral_finishing_flow_ratio: 'others',
  print_sequence: 'others',
  slicing_mode: 'others',
  enable_wrapping_detection: 'others',
  fuzzy_skin: 'others',
  fuzzy_skin_mode: 'others',
  fuzzy_skin_noise_type: 'others',
  fuzzy_skin_point_distance: 'others',
  fuzzy_skin_thickness: 'others',
  fuzzy_skin_scale: 'others',
  fuzzy_skin_octaves: 'others',
  fuzzy_skin_persistence: 'others',
  fuzzy_skin_first_layer: 'others',
  reduce_infill_retraction: 'others',
  gcode_add_line_number: 'others',
  gcode_comments: 'others',
  gcode_label_objects: 'others',
  exclude_object: 'others',
  notes: 'others',
  timelapse: 'others',

  // Extended — temperature, retraction, cooling
  nozzle_temperature: 'others',
  nozzle_temperature_initial_layer: 'others',
  hot_plate_temp: 'others',
  hot_plate_temp_initial_layer: 'others',
  filament_retraction_length: 'others',
  filament_retraction_speed: 'others',
  filament_deretraction_speed: 'others',
  filament_retraction_minimum_travel: 'others',
  filament_retract_when_changing_layer: 'others',
  filament_retract_before_wipe: 'others',
  filament_z_hop: 'others',
  fan_cooling: 'others',
  fan_min_speed: 'others',
  fan_max_speed: 'others',
  overhang_fan_speed: 'others',
  full_fan_speed_layer: 'others',
};

// =============================================================================
// MODE MAP
// =============================================================================

/**
 * Maps each native snake_case setting key to its UI complexity mode.
 * Source: SimplyPrint process settings catalog.
 * Keys absent from this map default to 'advanced'.
 */
export const ORCA_PROCESS_MODE_MAP: Record<string, ProcessSettingsViewMode> = {
  // Quality — Simple
  layer_height: 'simple',
  initial_layer_print_height: 'simple',
  scarf_joint_flow_ratio: 'simple',
  precise_outer_wall: 'simple',
  only_one_wall_first_layer: 'simple',
  only_one_wall_top: 'simple',

  // Strength — Simple
  wall_loops: 'simple',
  top_shell_layers: 'simple',
  top_shell_thickness: 'simple',
  top_surface_density: 'simple',
  bottom_shell_layers: 'simple',
  bottom_shell_thickness: 'simple',
  bottom_surface_density: 'simple',
  sparse_infill_density: 'simple',
  fill_multiline: 'simple',

  // Support — Simple
  enable_support: 'simple',
  support_threshold_angle: 'simple',
  support_threshold_overlap: 'simple',
  support_on_build_plate_only: 'simple',
  tree_support_auto_brim: 'simple',
  tree_support_brim_width: 'simple',

  // Multimaterial — Simple
  enable_prime_tower: 'simple',
  prime_tower_width: 'simple',
  prime_volume: 'simple',
  preheat_steps: 'simple',
  flush_into_infill: 'simple',
  flush_into_objects: 'simple',
  flush_into_support: 'simple',

  // Others — Simple
  skirt_loops: 'simple',
  skirt_height: 'simple',
  brim_width: 'simple',
  spiral_mode: 'simple',
  spiral_mode_smooth: 'simple',
  fuzzy_skin_point_distance: 'simple',
  fuzzy_skin_thickness: 'simple',
  fuzzy_skin_first_layer: 'simple',
  gcode_add_line_number: 'simple',
};

// =============================================================================
// CHANGE TRACKING SYSTEM
// =============================================================================

/**
 * Deep equality check for comparing setting values.
 * Handles primitives, arrays, and nested objects.
 */
export function deepEqual<T>(a: T, b: T): boolean {
  if (a === b) return true;
  if (a === null || b === null) return a === b;
  if (typeof a !== typeof b) return false;

  if (typeof a === 'object') {
    if (Array.isArray(a) && Array.isArray(b)) {
      if (a.length !== b.length) return false;
      return a.every((item, index) => deepEqual(item, b[index]));
    }

    if (Array.isArray(a) !== Array.isArray(b)) return false;

    const keysA = Object.keys(a as object);
    const keysB = Object.keys(b as object);
    if (keysA.length !== keysB.length) return false;

    return keysA.every(key =>
      deepEqual(
        (a as Record<string, unknown>)[key],
        (b as Record<string, unknown>)[key]
      )
    );
  }

  return false;
}

/**
 * Deep clone an object to ensure original values are preserved.
 */
export function deepClone<T>(obj: T): T {
  if (obj === null || typeof obj !== 'object') return obj;
  if (Array.isArray(obj)) return obj.map(item => deepClone(item)) as T;

  const cloned = {} as T;
  for (const key in obj) {
    if (Object.prototype.hasOwnProperty.call(obj, key)) {
      cloned[key] = deepClone(obj[key]);
    }
  }
  return cloned;
}

/**
 * Interface for tracked settings state.
 * Provides utilities for change detection and reset functionality.
 */
export interface TrackedSettingsState<T extends Record<string, unknown>> {
  /** The original settings values (immutable reference) */
  original: T;
  /** The current settings values (mutable) */
  current: T;
  /** Check if a specific setting has been modified */
  hasChanges: (key: keyof T) => boolean;
  /** Get list of all modified setting keys */
  getChangedKeys: () => (keyof T)[];
  /** Reset a single setting to its original value */
  resetToOriginal: (key: keyof T) => void;
  /** Reset all settings to original values */
  resetAll: () => void;
  /** True if any setting has been modified */
  isDirty: boolean;
  /** Map of category -> set of changed keys in that category */
  changedKeysPerCategory: Map<SettingsCategory, Set<keyof T>>;
  /** Check if a specific category tab has any modified settings */
  isCategoryDirty: (category: SettingsCategory) => boolean;
  /** Update a single setting value */
  updateSetting: <K extends keyof T>(key: K, value: T[K]) => void;
  /** Update multiple settings at once */
  updateSettings: (updates: Partial<T>) => void;
  /** Get the original value for a setting (for display/tooltip) */
  getOriginalValue: <K extends keyof T>(key: K) => T[K];
}

/**
 * React hook for tracking changes to settings objects.
 * Enables reset-to-original functionality and dirty state detection per category.
 */
export function useTrackedSettings<T extends Record<string, unknown>>(
  initialSettings: T
): TrackedSettingsState<T> {
  const [original] = useState<T>(() => deepClone(initialSettings));
  const [current, setCurrent] = useState<T>(() => deepClone(initialSettings));

  const hasChanges = useCallback((key: keyof T): boolean => {
    return !deepEqual(original[key], current[key]);
  }, [original, current]);

  const getChangedKeys = useCallback((): (keyof T)[] => {
    return (Object.keys(current) as (keyof T)[]).filter(key => hasChanges(key));
  }, [current, hasChanges]);

  const resetToOriginal = useCallback((key: keyof T): void => {
    setCurrent(prev => ({
      ...prev,
      [key]: deepClone(original[key]),
    }));
  }, [original]);

  const resetAll = useCallback((): void => {
    setCurrent(deepClone(original));
  }, [original]);

  const updateSetting = useCallback(<K extends keyof T>(key: K, value: T[K]): void => {
    setCurrent(prev => ({
      ...prev,
      [key]: value,
    }));
  }, []);

  const updateSettings = useCallback((updates: Partial<T>): void => {
    setCurrent(prev => ({
      ...prev,
      ...updates,
    }));
  }, []);

  const getOriginalValue = useCallback(<K extends keyof T>(key: K): T[K] => {
    return original[key];
  }, [original]);

  const isDirty = useMemo((): boolean => {
    return getChangedKeys().length > 0;
  }, [getChangedKeys]);

  const changedKeysPerCategory = useMemo((): Map<SettingsCategory, Set<keyof T>> => {
    const categoryMap = new Map<SettingsCategory, Set<keyof T>>();

    const categories: SettingsCategory[] = ['quality', 'strength', 'speed', 'support', 'multimaterial', 'others'];
    categories.forEach(cat => categoryMap.set(cat, new Set()));

    const changedKeys = getChangedKeys();
    changedKeys.forEach(key => {
      const category = ORCA_PROCESS_CATEGORY_MAP[key as string] ?? 'others';
      categoryMap.get(category)?.add(key);
    });

    return categoryMap;
  }, [getChangedKeys]);

  const isCategoryDirty = useCallback((category: SettingsCategory): boolean => {
    const categoryKeys = changedKeysPerCategory.get(category);
    return categoryKeys !== undefined && categoryKeys.size > 0;
  }, [changedKeysPerCategory]);

  return {
    original,
    current,
    hasChanges,
    getChangedKeys,
    resetToOriginal,
    resetAll,
    isDirty,
    changedKeysPerCategory,
    isCategoryDirty,
    updateSetting,
    updateSettings,
    getOriginalValue,
  };
}

/**
 * Type guard to check if a value is a valid settings category.
 */
export function isValidCategory(value: string): value is SettingsCategory {
  return ['quality', 'strength', 'speed', 'support', 'multimaterial', 'others'].includes(value);
}
