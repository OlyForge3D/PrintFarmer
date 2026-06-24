/**
 * OrcaSlicer filament settings type definitions.
 * Uses OrcaSlicer native snake_case property names throughout.
 * Generated from orcaSettingsMetadata.json (108 filament settings).
 */

// =============================================================================
// VIEW MODE & CATEGORY TYPES
// =============================================================================

/** View modes for filament settings panel complexity */
export type FilamentSettingsViewMode = 'simple' | 'advanced';

/** Category tabs in the filament settings panel, matching OrcaSlicer Tab.cpp */
export type FilamentSettingsCategory =
  | 'filament'
  | 'cooling'
  | 'advanced'
  | 'multimaterial'
  | 'dependencies'
  | 'notes'
  | 'overrides';

export const FILAMENT_SETTING_CATEGORIES: readonly FilamentSettingsCategory[] = [
  'filament',
  'cooling',
  'advanced',
  'multimaterial',
  'dependencies',
  'notes',
  'overrides',
] as const;

// =============================================================================
// MAIN SETTINGS INTERFACE
// =============================================================================

/**
 * OrcaSlicer filament profile settings using native snake_case property names.
 * All properties are optional — profiles may contain partial settings.
 * Compound fields (per-extruder, semicolon-delimited) are typed as string.
 */
export interface OrcaFilamentSettings {

  // ---------------------------------------------------------------------------
  // Filament tab — Basic information
  // ---------------------------------------------------------------------------

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
  filament_cost?: number;
  temperature_vitrification?: number;
  idle_temperature?: number;
  /** Compound (per-extruder) */
  nozzle_temperature_range_low?: string;
  /** Compound (per-extruder) */
  nozzle_temperature_range_high?: string;

  // Filament tab — Flow ratio and Pressure Advance

  pellet_flow_coefficient?: number;
  filament_flow_ratio?: number;
  enable_pressure_advance?: boolean;
  pressure_advance?: number;
  adaptive_pressure_advance?: boolean;
  adaptive_pressure_advance_overhangs?: boolean;
  adaptive_pressure_advance_bridges?: number;
  /** Compound (per-extruder) */
  adaptive_pressure_advance_model?: string;

  // Filament tab — Print chamber temperature

  chamber_temperature?: number;
  activate_chamber_temp_control?: boolean;

  // Filament tab — Print temperature

  /** Compound (per-extruder) */
  nozzle_temperature_initial_layer?: string;
  /** Compound (per-extruder) */
  nozzle_temperature?: string;

  // Filament tab — Bed temperature

  /** Compound (per-extruder) */
  supertack_plate_temp_initial_layer?: string;
  /** Compound (per-extruder) */
  supertack_plate_temp?: string;
  /** Compound (per-extruder) */
  cool_plate_temp_initial_layer?: string;
  /** Compound (per-extruder) */
  cool_plate_temp?: string;
  /** Compound (per-extruder) */
  textured_cool_plate_temp_initial_layer?: string;
  /** Compound (per-extruder) */
  textured_cool_plate_temp?: string;
  /** Compound (per-extruder) */
  eng_plate_temp_initial_layer?: string;
  /** Compound (per-extruder) */
  eng_plate_temp?: string;
  /** Compound (per-extruder) */
  hot_plate_temp_initial_layer?: string;
  /** Compound (per-extruder) */
  hot_plate_temp?: string;
  /** Compound (per-extruder) */
  textured_plate_temp_initial_layer?: string;
  /** Compound (per-extruder) */
  textured_plate_temp?: string;

  // Filament tab — Volumetric speed limitation

  filament_adaptive_volumetric_speed?: boolean;
  filament_max_volumetric_speed?: number;

  // ---------------------------------------------------------------------------
  // Cooling tab — Cooling for specific layer
  // ---------------------------------------------------------------------------

  close_fan_the_first_x_layers?: number;
  full_fan_speed_layer?: number;

  // Cooling tab — Part cooling fan

  /** Compound (per-extruder) */
  fan_min_speed?: string;
  /** Compound (per-extruder) */
  fan_cooling_layer_time?: string;
  /** Compound (per-extruder) */
  fan_max_speed?: string;
  /** Compound (per-extruder) */
  slow_down_layer_time?: string;
  reduce_fan_stop_start_freq?: boolean;
  slow_down_for_layer_cooling?: boolean;
  dont_slow_down_outer_wall?: boolean;
  slow_down_min_speed?: number;
  enable_overhang_bridge_fan?: boolean;
  overhang_fan_threshold?: string;
  overhang_fan_speed?: number;
  internal_bridge_fan_speed?: number;
  support_material_interface_fan_speed?: number;
  ironing_fan_speed?: number;

  // Cooling tab — Auxiliary part cooling fan

  additional_cooling_fan_speed?: number;

  // Cooling tab — Exhaust fan

  activate_air_filtration?: boolean;
  /** Compound (per-extruder) */
  during_print_exhaust_fan_speed?: string;
  /** Compound (per-extruder) */
  complete_print_exhaust_fan_speed?: string;

  // ---------------------------------------------------------------------------
  // Advanced tab — G-code
  // ---------------------------------------------------------------------------

  /** Compound (per-extruder) */
  filament_start_gcode?: string;
  /** Compound (per-extruder) */
  filament_end_gcode?: string;

  // ---------------------------------------------------------------------------
  // Multimaterial tab — Wipe tower parameters
  // ---------------------------------------------------------------------------

  filament_minimal_purge_on_wipe_tower?: number;
  filament_tower_interface_pre_extrusion_dist?: number;
  filament_tower_interface_pre_extrusion_length?: number;
  filament_tower_ironing_area?: number;
  filament_tower_interface_purge_volume?: number;
  filament_tower_interface_print_temp?: number;

  // Multimaterial tab — Multi Filament

  long_retractions_when_ec?: boolean;
  retraction_distances_when_ec?: number;

  // Multimaterial tab — Tool change (single extruder MM printers)

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

  // Multimaterial tab — Tool change (multi extruder MM printers)

  filament_multitool_ramming?: boolean;
  filament_multitool_ramming_volume?: number;
  filament_multitool_ramming_flow?: number;

  // ---------------------------------------------------------------------------
  // Dependencies tab
  // ---------------------------------------------------------------------------

  /** Compound (per-extruder) */
  compatible_printers_condition?: string;
  /** Compound (per-extruder) */
  compatible_prints_condition?: string;

  // ---------------------------------------------------------------------------
  // Notes tab
  // ---------------------------------------------------------------------------

  /** Compound (per-extruder) */
  filament_notes?: string;

  // ---------------------------------------------------------------------------
  // Setting Overrides — Ironing overrides
  // ---------------------------------------------------------------------------

  filament_ironing_flow?: number;
  filament_ironing_inset?: number;
  filament_ironing_spacing?: number;
  filament_ironing_speed?: number;

  // ---------------------------------------------------------------------------
  // Internal / metadata fields (not shown in tabs)
  // ---------------------------------------------------------------------------

  filament_colour?: string;
  filament_colour_type?: string;
  filament_extruder_id?: number;
  filament_extruder_variant?: string;
  filament_flush_temp?: number;
  filament_flush_volumetric_speed?: number;
  filament_ids?: string;
  filament_map?: number;
  filament_map_mode?: string;
  filament_multi_colour?: string;
  filament_preset?: string;
  filament_printable?: number;
  filament_ramming_parameters?: string;
  filament_self_index?: number;
  filament_settings_id?: string;

  // --- OrcaSlicer 2.4.0 additions ---
  activate_air_filtration_during_print?: boolean;
  activate_air_filtration_on_completion?: boolean;
  filament_change_extrusion_role_gcode?: string;
  filament_cooling_before_tower?: number;

}

// =============================================================================
// ALL SETTING KEYS (union type)
// =============================================================================

export type OrcaFilamentSettingKey = keyof OrcaFilamentSettings;

// =============================================================================
// MODE MAP — simple vs advanced per key
// =============================================================================

/**
 * Maps each filament setting key to its view mode.
 * Simple: core fields visible in basic view (type, temps, flow, cooling basics).
 * Advanced: everything else (pressure advance, volumetric, multi-material, G-code, etc.).
 */
export const FILAMENT_SETTINGS_MODE_MAP: Record<OrcaFilamentSettingKey, FilamentSettingsViewMode> = {
  // --- Simple mode settings ---
  filament_type: 'simple',
  filament_vendor: 'simple',
  filament_density: 'simple',
  filament_cost: 'simple',
  filament_diameter: 'simple',
  default_filament_colour: 'simple',
  nozzle_temperature: 'simple',
  nozzle_temperature_initial_layer: 'simple',
  filament_flow_ratio: 'simple',
  fan_min_speed: 'simple',
  fan_max_speed: 'simple',
  slow_down_min_speed: 'simple',

  // --- Advanced mode settings ---

  // Filament tab — Basic information (advanced)
  filament_soluble: 'advanced',
  filament_is_support: 'advanced',
  filament_change_length: 'advanced',
  required_nozzle_HRC: 'advanced',
  filament_adhesiveness_category: 'advanced',
  filament_shrink: 'advanced',
  filament_shrinkage_compensation_z: 'advanced',
  temperature_vitrification: 'advanced',
  idle_temperature: 'advanced',
  nozzle_temperature_range_low: 'advanced',
  nozzle_temperature_range_high: 'advanced',

  // Filament tab — Pressure Advance
  pellet_flow_coefficient: 'advanced',
  enable_pressure_advance: 'advanced',
  pressure_advance: 'advanced',
  adaptive_pressure_advance: 'advanced',
  adaptive_pressure_advance_overhangs: 'advanced',
  adaptive_pressure_advance_bridges: 'advanced',
  adaptive_pressure_advance_model: 'advanced',

  // Filament tab — Chamber temperature
  chamber_temperature: 'advanced',
  activate_chamber_temp_control: 'advanced',

  // Filament tab — Bed temperature
  supertack_plate_temp_initial_layer: 'advanced',
  supertack_plate_temp: 'advanced',
  cool_plate_temp_initial_layer: 'advanced',
  cool_plate_temp: 'advanced',
  textured_cool_plate_temp_initial_layer: 'advanced',
  textured_cool_plate_temp: 'advanced',
  eng_plate_temp_initial_layer: 'advanced',
  eng_plate_temp: 'advanced',
  hot_plate_temp_initial_layer: 'advanced',
  hot_plate_temp: 'advanced',
  textured_plate_temp_initial_layer: 'advanced',
  textured_plate_temp: 'advanced',

  // Filament tab — Volumetric speed
  filament_adaptive_volumetric_speed: 'advanced',
  filament_max_volumetric_speed: 'advanced',

  // Cooling tab
  close_fan_the_first_x_layers: 'advanced',
  full_fan_speed_layer: 'advanced',
  fan_cooling_layer_time: 'advanced',
  slow_down_layer_time: 'advanced',
  reduce_fan_stop_start_freq: 'advanced',
  slow_down_for_layer_cooling: 'advanced',
  dont_slow_down_outer_wall: 'advanced',
  enable_overhang_bridge_fan: 'advanced',
  overhang_fan_threshold: 'advanced',
  overhang_fan_speed: 'advanced',
  internal_bridge_fan_speed: 'advanced',
  support_material_interface_fan_speed: 'advanced',
  ironing_fan_speed: 'advanced',
  additional_cooling_fan_speed: 'advanced',
  activate_air_filtration: 'advanced',
  during_print_exhaust_fan_speed: 'advanced',
  complete_print_exhaust_fan_speed: 'advanced',

  // Advanced tab — G-code
  filament_start_gcode: 'advanced',
  filament_end_gcode: 'advanced',

  // Multimaterial tab
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
  filament_multitool_ramming: 'advanced',
  filament_multitool_ramming_volume: 'advanced',
  filament_multitool_ramming_flow: 'advanced',

  // Dependencies tab
  compatible_printers_condition: 'advanced',
  compatible_prints_condition: 'advanced',

  // Notes tab
  filament_notes: 'advanced',

  // Setting Overrides — Ironing
  filament_ironing_flow: 'advanced',
  filament_ironing_inset: 'advanced',
  filament_ironing_spacing: 'advanced',
  filament_ironing_speed: 'advanced',

  // Internal / metadata
  filament_colour: 'advanced',
  filament_colour_type: 'advanced',
  filament_extruder_id: 'advanced',
  filament_extruder_variant: 'advanced',
  filament_flush_temp: 'advanced',
  filament_flush_volumetric_speed: 'advanced',
  filament_ids: 'advanced',
  filament_map: 'advanced',
  filament_map_mode: 'advanced',
  filament_multi_colour: 'advanced',
  filament_preset: 'advanced',
  filament_printable: 'advanced',
  filament_ramming_parameters: 'advanced',
  filament_self_index: 'advanced',
  filament_settings_id: 'advanced',

  // --- OrcaSlicer 2.4.0 additions ---
  activate_air_filtration_during_print: 'simple',
  activate_air_filtration_on_completion: 'simple',
  filament_change_extrusion_role_gcode: 'advanced',
  filament_cooling_before_tower: 'advanced',

};

// =============================================================================
// CATEGORY MAP — maps each key to its tab category
// =============================================================================

export const FILAMENT_SETTINGS_CATEGORY_MAP: Record<OrcaFilamentSettingKey, FilamentSettingsCategory> = {
  // Filament tab — Basic information
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
  filament_cost: 'filament',
  temperature_vitrification: 'filament',
  idle_temperature: 'filament',
  nozzle_temperature_range_low: 'filament',
  nozzle_temperature_range_high: 'filament',

  // Filament tab — Flow ratio and Pressure Advance
  pellet_flow_coefficient: 'filament',
  filament_flow_ratio: 'filament',
  enable_pressure_advance: 'filament',
  pressure_advance: 'filament',
  adaptive_pressure_advance: 'filament',
  adaptive_pressure_advance_overhangs: 'filament',
  adaptive_pressure_advance_bridges: 'filament',
  adaptive_pressure_advance_model: 'filament',

  // Filament tab — Chamber temperature
  chamber_temperature: 'filament',
  activate_chamber_temp_control: 'filament',

  // Filament tab — Print temperature
  nozzle_temperature_initial_layer: 'filament',
  nozzle_temperature: 'filament',

  // Filament tab — Bed temperature
  supertack_plate_temp_initial_layer: 'filament',
  supertack_plate_temp: 'filament',
  cool_plate_temp_initial_layer: 'filament',
  cool_plate_temp: 'filament',
  textured_cool_plate_temp_initial_layer: 'filament',
  textured_cool_plate_temp: 'filament',
  eng_plate_temp_initial_layer: 'filament',
  eng_plate_temp: 'filament',
  hot_plate_temp_initial_layer: 'filament',
  hot_plate_temp: 'filament',
  textured_plate_temp_initial_layer: 'filament',
  textured_plate_temp: 'filament',

  // Filament tab — Volumetric speed limitation
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
  overhang_fan_threshold: 'cooling',
  overhang_fan_speed: 'cooling',
  internal_bridge_fan_speed: 'cooling',
  support_material_interface_fan_speed: 'cooling',
  ironing_fan_speed: 'cooling',
  additional_cooling_fan_speed: 'cooling',
  activate_air_filtration: 'cooling',
  during_print_exhaust_fan_speed: 'cooling',
  complete_print_exhaust_fan_speed: 'cooling',

  // Advanced tab — G-code
  filament_start_gcode: 'advanced',
  filament_end_gcode: 'advanced',

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
  filament_multitool_ramming: 'multimaterial',
  filament_multitool_ramming_volume: 'multimaterial',
  filament_multitool_ramming_flow: 'multimaterial',

  // Dependencies tab
  compatible_printers_condition: 'dependencies',
  compatible_prints_condition: 'dependencies',

  // Notes tab
  filament_notes: 'notes',

  // Setting Overrides — Ironing
  filament_ironing_flow: 'overrides',
  filament_ironing_inset: 'overrides',
  filament_ironing_spacing: 'overrides',
  filament_ironing_speed: 'overrides',

  // Internal / metadata fields
  filament_colour: 'filament',
  filament_colour_type: 'filament',
  filament_extruder_id: 'filament',
  filament_extruder_variant: 'filament',
  filament_flush_temp: 'multimaterial',
  filament_flush_volumetric_speed: 'multimaterial',
  filament_ids: 'filament',
  filament_map: 'filament',
  filament_map_mode: 'filament',
  filament_multi_colour: 'filament',
  filament_preset: 'filament',
  filament_printable: 'filament',
  filament_ramming_parameters: 'multimaterial',
  filament_self_index: 'filament',
  filament_settings_id: 'filament',

  // --- OrcaSlicer 2.4.0 additions ---
  activate_air_filtration_during_print: 'cooling',
  activate_air_filtration_on_completion: 'cooling',
  filament_change_extrusion_role_gcode: 'advanced',
  filament_cooling_before_tower: 'advanced',

};

// =============================================================================
// DEFAULT SETTINGS — Typical PLA values
// =============================================================================

export const DEFAULT_FILAMENT_SETTINGS: Partial<OrcaFilamentSettings> = {
  // Basic information
  filament_type: 'PLA',
  filament_vendor: '',
  filament_soluble: false,
  filament_is_support: false,
  filament_diameter: 1.75,
  filament_density: 1.24,
  filament_cost: 0,
  filament_shrink: 100,
  filament_shrinkage_compensation_z: 100,
  temperature_vitrification: 56,
  idle_temperature: 0,

  // Flow ratio and Pressure Advance
  filament_flow_ratio: 1,
  pellet_flow_coefficient: 0.4157,
  enable_pressure_advance: false,
  pressure_advance: 0.02,
  adaptive_pressure_advance: false,
  adaptive_pressure_advance_overhangs: false,
  adaptive_pressure_advance_bridges: 0,

  // Chamber temperature
  chamber_temperature: 0,
  activate_chamber_temp_control: false,

  // Print temperature
  nozzle_temperature: '200',
  nozzle_temperature_initial_layer: '200',
  nozzle_temperature_range_low: '190',
  nozzle_temperature_range_high: '240',

  // Bed temperature (PLA defaults per plate type)
  supertack_plate_temp: '35',
  supertack_plate_temp_initial_layer: '35',
  cool_plate_temp: '35',
  cool_plate_temp_initial_layer: '35',
  textured_cool_plate_temp: '35',
  textured_cool_plate_temp_initial_layer: '35',
  eng_plate_temp: '45',
  eng_plate_temp_initial_layer: '45',
  hot_plate_temp: '60',
  hot_plate_temp_initial_layer: '60',
  textured_plate_temp: '45',
  textured_plate_temp_initial_layer: '45',

  // Volumetric speed
  filament_adaptive_volumetric_speed: false,
  filament_max_volumetric_speed: 12,

  // Cooling — layer-specific
  close_fan_the_first_x_layers: 1,
  full_fan_speed_layer: 0,

  // Cooling — part fan
  fan_min_speed: '35',
  fan_max_speed: '100',
  fan_cooling_layer_time: '60',
  slow_down_layer_time: '5',
  slow_down_min_speed: 10,
  slow_down_for_layer_cooling: true,
  reduce_fan_stop_start_freq: false,
  dont_slow_down_outer_wall: false,
  enable_overhang_bridge_fan: true,
  overhang_fan_speed: 100,
  internal_bridge_fan_speed: -1,
  support_material_interface_fan_speed: -1,
  ironing_fan_speed: -1,

  // Cooling — auxiliary / exhaust
  additional_cooling_fan_speed: 0,
  activate_air_filtration: false,
  during_print_exhaust_fan_speed: '60',
  complete_print_exhaust_fan_speed: '80',

  // Multimaterial — wipe tower
  filament_minimal_purge_on_wipe_tower: 15,
  filament_tower_interface_pre_extrusion_dist: 10,
  filament_tower_interface_pre_extrusion_length: 0,
  filament_tower_ironing_area: 4,
  filament_tower_interface_purge_volume: 20,
  filament_tower_interface_print_temp: -1,

  // Multimaterial — multi filament
  long_retractions_when_ec: false,
  retraction_distances_when_ec: 10,

  // Multimaterial — tool change (single extruder)
  filament_loading_speed_start: 3,
  filament_loading_speed: 28,
  filament_unloading_speed_start: 100,
  filament_unloading_speed: 90,
  filament_toolchange_delay: 0,
  filament_cooling_moves: 4,
  filament_cooling_initial_speed: 2.2,
  filament_cooling_final_speed: 3.4,
  filament_stamping_loading_speed: 0,
  filament_stamping_distance: 0,

  // Multimaterial — tool change (multi extruder)
  filament_multitool_ramming: false,
  filament_multitool_ramming_volume: 10,
  filament_multitool_ramming_flow: 10,
};
