/**
 * OrcaSlicer Machine settings type definitions
 * Maps to OrcaSlicer's machine/printer profile settings
 */

/** View modes for machine settings panel complexity */
export type MachineSettingsViewMode = 'basic' | 'advanced';

/** Category tabs for machine settings in advanced mode */
export type MachineSettingsCategory = 
  | 'general'
  | 'extruder'
  | 'printbed'
  | 'capabilities'
  | 'gcode';

/**
 * Basic machine settings - shown in Basic mode
 */
export interface BasicMachineSettings {
  // Basic info
  name: string;
  inherits?: string;
  
  // Build volume
  buildVolumeX: number;       // mm
  buildVolumeY: number;       // mm
  buildVolumeZ: number;       // mm
  printableArea: string;      // e.g., "0x0,220x0,220x220,0x220"
  
  // Nozzle
  nozzleDiameter: number;     // mm (0.2, 0.4, 0.6, 0.8, etc.)
  
  // Basic capabilities
  maxPrintSpeed: number;      // mm/s
}

/**
 * Advanced machine settings - full OrcaSlicer parameter set
 */
export interface AdvancedMachineSettings extends BasicMachineSettings {
  // General printer settings
  printerModel: string;
  printerVariant: string;
  printerNotes: string;
  thumbnailSize: string;          // e.g., "300x300,32x32"
  useRelativeEDistances: boolean;
  useFirmwareRetraction: boolean;
  
  // Build volume extended
  buildVolumeOrigin: 'center' | 'corner';
  maxLayerHeight: number;         // mm
  minLayerHeight: number;         // mm
  bedShape: 'rectangular' | 'circular';
  
  // Extruder settings
  extruderCount: number;
  extruderOffset: string;         // e.g., "0x0" for single, "0x0,40x0" for dual
  retractionLength: number;       // mm
  retractionSpeed: number;        // mm/s
  retractionLiftZ: number;        // mm (Z hop)
  retractionLiftAbove: number;    // mm (min Z for lift)
  retractionLiftBelow: number;    // mm (max Z for lift, 0 = unlimited)
  detractionSpeed: number;        // mm/s (unretract)
  longRetractionWhenCut: number;  // mm (for filament cutter)
  extrusionMultiplier: number;    // ratio (0.9 - 1.1 typical)
  
  // Nozzle extended
  nozzleType: 'brass' | 'hardened_steel' | 'stainless_steel' | 'custom';
  nozzleHrc: number;              // Hardness Rockwell C (for wear calculation)
  
  // Print bed
  bedType: 'textured_pei' | 'smooth_pei' | 'glass' | 'spring_steel' | 'custom';
  hasBedProbe: boolean;
  probeType: 'bltouch' | 'inductive' | 'capacitive' | 'manual' | 'none';
  meshBedLeveling: boolean;
  bedCustomTexture: string;       // path to texture image
  bedCustomModel: string;         // path to 3D model
  
  // Capabilities
  hasHeatedBed: boolean;
  hasHeatedChamber: boolean;
  maxBedTemperature: number;      // °C
  maxChamberTemperature: number;  // °C
  maxHotendTemperature: number;   // °C
  supportMultiMaterial: boolean;
  supportArcMovement: boolean;    // G2/G3 support
  arcResolution: number;          // mm (for arc fitting)
  
  // Motion system
  motionType: 'cartesian' | 'corexy' | 'delta' | 'belt';
  maxAccelerationX: number;       // mm/s²
  maxAccelerationY: number;       // mm/s²
  maxAccelerationZ: number;       // mm/s²
  maxAccelerationE: number;       // mm/s² (extruder)
  maxJerkX: number;               // mm/s
  maxJerkY: number;               // mm/s
  maxJerkZ: number;               // mm/s
  maxJerkE: number;               // mm/s (extruder)
  maxFeedrateX: number;           // mm/s
  maxFeedrateY: number;           // mm/s
  maxFeedrateZ: number;           // mm/s
  maxFeedrateE: number;           // mm/s
  
  // Cooling
  coolingFanCount: number;
  hasChamberFan: boolean;
  hasAuxiliaryFan: boolean;
  fanMaxSpeed: number;            // RPM or PWM max
  
  // G-code flavor
  gcodeDialect: 'marlin' | 'marlin2' | 'klipper' | 'reprap' | 'smoothie' | 'mach3' | 'custom';
  startGcode: string;             // machine start g-code
  endGcode: string;               // machine end g-code
  beforeLayerChangeGcode: string;
  afterLayerChangeGcode: string;
  toolChangeGcode: string;
  pauseGcode: string;
  
  // Printer-specific features
  silentMode: boolean;
  silentModeMaxSpeed: number;     // mm/s when silent
  powerLossRecovery: boolean;
  filamentSensor: boolean;
  autoLevelingEnabled: boolean;
  
  // Timelapses / Octoprint
  timelapseType: 'none' | 'regular' | 'layered';
  octoprintHost?: string;
  octoprintApiKey?: string;
  
  // Physical dimensions (for visualization)
  printerWidth: number;           // mm (for clearance)
  printerDepth: number;           // mm
  printerHeight: number;          // mm
}

/** Default values for basic machine settings */
export const DEFAULT_BASIC_MACHINE_SETTINGS: BasicMachineSettings = {
  name: 'Custom Printer',
  buildVolumeX: 220,
  buildVolumeY: 220,
  buildVolumeZ: 250,
  printableArea: '0x0,220x0,220x220,0x220',
  nozzleDiameter: 0.4,
  maxPrintSpeed: 250,
};

/** Default values for advanced machine settings */
export const DEFAULT_ADVANCED_MACHINE_SETTINGS: AdvancedMachineSettings = {
  ...DEFAULT_BASIC_MACHINE_SETTINGS,
  printerModel: '',
  printerVariant: '',
  printerNotes: '',
  thumbnailSize: '300x300,32x32',
  useRelativeEDistances: false,
  useFirmwareRetraction: false,
  
  // Build volume
  buildVolumeOrigin: 'corner',
  maxLayerHeight: 0.28,
  minLayerHeight: 0.08,
  bedShape: 'rectangular',
  
  // Extruder
  extruderCount: 1,
  extruderOffset: '0x0',
  retractionLength: 0.8,
  retractionSpeed: 35,
  retractionLiftZ: 0.2,
  retractionLiftAbove: 0,
  retractionLiftBelow: 0,
  detractionSpeed: 25,
  longRetractionWhenCut: 6,
  extrusionMultiplier: 1.0,
  
  // Nozzle
  nozzleType: 'brass',
  nozzleHrc: 0,
  
  // Print bed
  bedType: 'textured_pei',
  hasBedProbe: true,
  probeType: 'inductive',
  meshBedLeveling: true,
  bedCustomTexture: '',
  bedCustomModel: '',
  
  // Capabilities
  hasHeatedBed: true,
  hasHeatedChamber: false,
  maxBedTemperature: 110,
  maxChamberTemperature: 0,
  maxHotendTemperature: 300,
  supportMultiMaterial: false,
  supportArcMovement: true,
  arcResolution: 0.25,
  
  // Motion
  motionType: 'cartesian',
  maxAccelerationX: 3000,
  maxAccelerationY: 3000,
  maxAccelerationZ: 500,
  maxAccelerationE: 5000,
  maxJerkX: 8,
  maxJerkY: 8,
  maxJerkZ: 0.4,
  maxJerkE: 2.5,
  maxFeedrateX: 300,
  maxFeedrateY: 300,
  maxFeedrateZ: 10,
  maxFeedrateE: 120,
  
  // Cooling
  coolingFanCount: 1,
  hasChamberFan: false,
  hasAuxiliaryFan: false,
  fanMaxSpeed: 255,
  
  // G-code
  gcodeDialect: 'marlin2',
  startGcode: '; Start G-code\nG28 ; Home all axes\nG1 Z5 F3000 ; Lift nozzle',
  endGcode: '; End G-code\nM104 S0 ; Turn off hotend\nM140 S0 ; Turn off bed\nG28 X Y ; Home X and Y\nM84 ; Disable motors',
  beforeLayerChangeGcode: '',
  afterLayerChangeGcode: '',
  toolChangeGcode: '',
  pauseGcode: 'M601 ; Pause',
  
  // Features
  silentMode: false,
  silentModeMaxSpeed: 100,
  powerLossRecovery: false,
  filamentSensor: false,
  autoLevelingEnabled: true,
  
  // Timelapse
  timelapseType: 'none',
  octoprintHost: undefined,
  octoprintApiKey: undefined,
  
  // Physical
  printerWidth: 400,
  printerDepth: 400,
  printerHeight: 500,
};

/**
 * Map settings to their category
 * Used to determine which tab shows dirty indicator
 */
export const MACHINE_SETTING_TO_CATEGORY_MAP: Record<keyof AdvancedMachineSettings, MachineSettingsCategory> = {
  // General
  name: 'general',
  inherits: 'general',
  printerModel: 'general',
  printerVariant: 'general',
  printerNotes: 'general',
  thumbnailSize: 'general',
  useRelativeEDistances: 'gcode',
  useFirmwareRetraction: 'gcode',
  
  // Build volume (general)
  buildVolumeX: 'general',
  buildVolumeY: 'general',
  buildVolumeZ: 'general',
  printableArea: 'general',
  buildVolumeOrigin: 'general',
  maxLayerHeight: 'general',
  minLayerHeight: 'general',
  bedShape: 'printbed',
  
  // Extruder
  extruderCount: 'extruder',
  extruderOffset: 'extruder',
  nozzleDiameter: 'extruder',
  nozzleType: 'extruder',
  nozzleHrc: 'extruder',
  retractionLength: 'extruder',
  retractionSpeed: 'extruder',
  retractionLiftZ: 'extruder',
  retractionLiftAbove: 'extruder',
  retractionLiftBelow: 'extruder',
  detractionSpeed: 'extruder',
  longRetractionWhenCut: 'extruder',
  extrusionMultiplier: 'extruder',
  
  // Print bed
  bedType: 'printbed',
  hasBedProbe: 'printbed',
  probeType: 'printbed',
  meshBedLeveling: 'printbed',
  bedCustomTexture: 'printbed',
  bedCustomModel: 'printbed',
  
  // Capabilities
  hasHeatedBed: 'capabilities',
  hasHeatedChamber: 'capabilities',
  maxBedTemperature: 'capabilities',
  maxChamberTemperature: 'capabilities',
  maxHotendTemperature: 'capabilities',
  supportMultiMaterial: 'capabilities',
  supportArcMovement: 'capabilities',
  arcResolution: 'capabilities',
  motionType: 'capabilities',
  maxAccelerationX: 'capabilities',
  maxAccelerationY: 'capabilities',
  maxAccelerationZ: 'capabilities',
  maxAccelerationE: 'capabilities',
  maxJerkX: 'capabilities',
  maxJerkY: 'capabilities',
  maxJerkZ: 'capabilities',
  maxJerkE: 'capabilities',
  maxFeedrateX: 'capabilities',
  maxFeedrateY: 'capabilities',
  maxFeedrateZ: 'capabilities',
  maxFeedrateE: 'capabilities',
  maxPrintSpeed: 'capabilities',
  coolingFanCount: 'capabilities',
  hasChamberFan: 'capabilities',
  hasAuxiliaryFan: 'capabilities',
  fanMaxSpeed: 'capabilities',
  silentMode: 'capabilities',
  silentModeMaxSpeed: 'capabilities',
  powerLossRecovery: 'capabilities',
  filamentSensor: 'capabilities',
  autoLevelingEnabled: 'capabilities',
  
  // G-code
  gcodeDialect: 'gcode',
  startGcode: 'gcode',
  endGcode: 'gcode',
  beforeLayerChangeGcode: 'gcode',
  afterLayerChangeGcode: 'gcode',
  toolChangeGcode: 'gcode',
  pauseGcode: 'gcode',
  
  // Timelapse / Other
  timelapseType: 'general',
  octoprintHost: 'general',
  octoprintApiKey: 'general',
  printerWidth: 'general',
  printerDepth: 'general',
  printerHeight: 'general',
};

/** Common printer presets (manufacturer defaults) */
export const PRINTER_PRESETS: Record<string, Partial<AdvancedMachineSettings>> = {
  'Prusa MK4': {
    name: 'Prusa MK4',
    printerModel: 'MK4',
    buildVolumeX: 250,
    buildVolumeY: 210,
    buildVolumeZ: 220,
    nozzleDiameter: 0.4,
    maxPrintSpeed: 500,
    motionType: 'cartesian',
    gcodeDialect: 'marlin2',
    hasHeatedBed: true,
    maxBedTemperature: 120,
    maxHotendTemperature: 290,
    filamentSensor: true,
    powerLossRecovery: true,
  },
  'Prusa CORE One': {
    name: 'Prusa CORE One',
    printerModel: 'CORE One',
    buildVolumeX: 250,
    buildVolumeY: 220,
    buildVolumeZ: 270,
    nozzleDiameter: 0.4,
    maxPrintSpeed: 600,
    motionType: 'corexy',
    gcodeDialect: 'marlin2',
    hasHeatedBed: true,
    hasHeatedChamber: true,
    maxBedTemperature: 120,
    maxChamberTemperature: 55,
    maxHotendTemperature: 300,
    filamentSensor: true,
    powerLossRecovery: true,
  },
  'Voron 2.4': {
    name: 'Voron 2.4',
    printerModel: 'Voron 2.4',
    buildVolumeX: 350,
    buildVolumeY: 350,
    buildVolumeZ: 350,
    nozzleDiameter: 0.4,
    maxPrintSpeed: 500,
    motionType: 'corexy',
    gcodeDialect: 'klipper',
    hasHeatedBed: true,
    hasHeatedChamber: true,
    maxBedTemperature: 120,
    maxHotendTemperature: 300,
  },
  'Bambu Lab X1C': {
    name: 'Bambu Lab X1C',
    printerModel: 'X1 Carbon',
    buildVolumeX: 256,
    buildVolumeY: 256,
    buildVolumeZ: 256,
    nozzleDiameter: 0.4,
    maxPrintSpeed: 500,
    motionType: 'corexy',
    gcodeDialect: 'marlin2',
    hasHeatedBed: true,
    hasHeatedChamber: true,
    maxBedTemperature: 110,
    maxChamberTemperature: 60,
    maxHotendTemperature: 300,
    supportMultiMaterial: true,
    filamentSensor: true,
  },
  'Creality Ender 3 V3': {
    name: 'Creality Ender 3 V3',
    printerModel: 'Ender 3 V3',
    buildVolumeX: 220,
    buildVolumeY: 220,
    buildVolumeZ: 250,
    nozzleDiameter: 0.4,
    maxPrintSpeed: 250,
    motionType: 'cartesian',
    gcodeDialect: 'marlin2',
    hasHeatedBed: true,
    maxBedTemperature: 100,
    maxHotendTemperature: 260,
  },
};

/** G-code dialect labels */
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
