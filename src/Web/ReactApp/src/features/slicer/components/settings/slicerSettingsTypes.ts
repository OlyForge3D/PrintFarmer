/**
 * OrcaSlicer process settings type definitions.
 * Uses OrcaSlicer native snake_case property names throughout.
 */

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

  seam_position?: string;
  seam_gap?: number;
  seam_slope_type?: string;
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

  ironing_type?: string;
  ironing_pattern?: 'rectilinear' | 'concentric';
  ironing_flow?: number;
  ironing_spacing?: number;
  ironing_angle?: number;
  ironing_angle_fixed?: number;
  ironing_inset?: number;

  // Quality tab — Wall generator (Arachne)

  wall_generator?: string;
  wall_transition_angle?: number;
  wall_transition_filter_deviation?: number;
  wall_transition_length?: number;
  wall_distribution_count?: number;
  initial_layer_min_bead_width?: number;
  min_bead_width?: number;
  min_feature_size?: number;
  min_length_factor?: number;
  wall_sequence?: string;
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
  sparse_infill_pattern?: string;
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
  gap_fill_target?: string;
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
  support_type?: string;
  support_style?: string;
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

  // Multimaterial tab — Filament for Features (per-feature filament assignment, OrcaSlicer 2.4.0+)

  outer_wall_filament_id?: number;
  inner_wall_filament_id?: number;
  sparse_infill_filament_id?: number;
  internal_solid_filament_id?: number;
  top_surface_filament_id?: number;
  bottom_surface_filament_id?: number;

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

  brim_type?: string;
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
  slicing_mode?: string;
  enable_wrapping_detection?: boolean;

  // Others tab — Fuzzy skin

  fuzzy_skin?: string;
  fuzzy_skin_mode?: string;
  fuzzy_skin_noise_type?: string;
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

