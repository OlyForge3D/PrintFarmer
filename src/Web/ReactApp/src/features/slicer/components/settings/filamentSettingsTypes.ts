/**
 * OrcaSlicer Filament settings type definitions
 * Maps to OrcaSlicer's filament profile settings
 */

/** View modes for filament settings panel complexity */
export type FilamentSettingsViewMode = 'basic' | 'advanced';

/** Category tabs for filament settings in advanced mode */
export type FilamentSettingsCategory = 
  | 'temperature' 
  | 'flow' 
  | 'cooling' 
  | 'retraction' 
  | 'other';

/**
 * Basic filament settings - shown in Basic mode
 */
export interface BasicFilamentSettings {
  // Basic info
  name: string;
  material: string;
  color?: string;
  density: number;            // g/cm³
  cost: number;               // cost per kg
  
  // Temperature
  nozzleTemperature: number;  // °C
  bedTemperature: number;     // °C
}

/**
 * Advanced filament settings - full OrcaSlicer parameter set
 */
export interface AdvancedFilamentSettings extends BasicFilamentSettings {
  // Extended temperature settings
  firstLayerNozzleTemperature: number;
  firstLayerBedTemperature: number;
  maxVolumetricSpeed: number;       // mm³/s
  chamberTemperature: number;       // °C
  
  // Flow settings
  flowRatio: number;                // multiplier (e.g., 0.95 - 1.05)
  printSpeed: number;               // mm/s override
  
  // Pressure advance / linear advance
  enablePressureAdvance: boolean;
  pressureAdvance: number;          // 0.0 - 1.0 typical
  pressureAdvanceSmoothTime: number; // seconds
  
  // Retraction settings (filament-specific)
  retractionLength: number;         // mm
  retractionSpeed: number;          // mm/s
  detractionSpeed: number;          // mm/s
  retractionMinimumTravel: number;  // mm
  retractOnLayerChange: boolean;
  wipeBeforeRetract: boolean;
  retractionLiftZ: number;          // mm - Z hop
  
  // Cooling settings (filament-specific)
  enableFanCooling: boolean;
  minFanSpeed: number;              // 0-100%
  maxFanSpeed: number;              // 0-100%
  bridgeFanSpeed: number;           // 0-100%
  fullFanSpeedAtLayer: number;
  slowDownForLayerTime: number;     // seconds
  minPrintSpeed: number;            // mm/s when slowing for cooling
  closeLoopFanPower: number;        // for printers with closed-loop fans
  auxFanSpeed: number;              // auxiliary fan 0-100%
  exhaustFanSpeed: number;          // exhaust/enclosure fan 0-100%
  
  // Volumetric settings
  enableVolumetricExtrusion: boolean;
  maxVolumetricExtrusionRate: number; // mm³/s
  
  // Advanced / Other
  filamentLoadTime: number;         // seconds
  filamentUnloadTime: number;       // seconds
  filamentRammingParameters: string; // advanced ramming config
  toolchangeDelay: number;          // seconds
  startGcode: string;               // filament-specific start g-code
  endGcode: string;                 // filament-specific end g-code
}

/** Default values for basic filament settings */
export const DEFAULT_BASIC_FILAMENT_SETTINGS: BasicFilamentSettings = {
  name: 'Custom Filament',
  material: 'PLA',
  color: '#3B82F6',
  density: 1.24,
  cost: 16,
  nozzleTemperature: 210,
  bedTemperature: 60,
};

/** Default values for advanced filament settings */
export const DEFAULT_ADVANCED_FILAMENT_SETTINGS: AdvancedFilamentSettings = {
  ...DEFAULT_BASIC_FILAMENT_SETTINGS,
  firstLayerNozzleTemperature: 215,
  firstLayerBedTemperature: 65,
  maxVolumetricSpeed: 12,
  chamberTemperature: 0,
  
  flowRatio: 1.0,
  printSpeed: 0, // 0 = use process profile speed
  
  enablePressureAdvance: false,
  pressureAdvance: 0.04,
  pressureAdvanceSmoothTime: 0.04,
  
  retractionLength: 0.8,
  retractionSpeed: 30,
  detractionSpeed: 30,
  retractionMinimumTravel: 1,
  retractOnLayerChange: false,
  wipeBeforeRetract: false,
  retractionLiftZ: 0.2,
  
  enableFanCooling: true,
  minFanSpeed: 35,
  maxFanSpeed: 100,
  bridgeFanSpeed: 100,
  fullFanSpeedAtLayer: 3,
  slowDownForLayerTime: 5,
  minPrintSpeed: 10,
  closeLoopFanPower: 100,
  auxFanSpeed: 0,
  exhaustFanSpeed: 0,
  
  enableVolumetricExtrusion: false,
  maxVolumetricExtrusionRate: 12,
  
  filamentLoadTime: 0,
  filamentUnloadTime: 0,
  filamentRammingParameters: '',
  toolchangeDelay: 0,
  startGcode: '',
  endGcode: '',
};

/** Material presets for quick selection */
export const MATERIAL_PRESETS: Record<string, Partial<AdvancedFilamentSettings>> = {
  PLA: {
    material: 'PLA',
    density: 1.24,
    cost: 16,
    nozzleTemperature: 210,
    bedTemperature: 60,
    firstLayerNozzleTemperature: 215,
    firstLayerBedTemperature: 65,
    enableFanCooling: true,
    maxFanSpeed: 100,
    chamberTemperature: 0,
  },
  PETG: {
    material: 'PETG',
    density: 1.27,
    cost: 18,
    nozzleTemperature: 240,
    bedTemperature: 80,
    firstLayerNozzleTemperature: 245,
    firstLayerBedTemperature: 85,
    enableFanCooling: true,
    maxFanSpeed: 50,
    chamberTemperature: 0,
  },
  ABS: {
    material: 'ABS',
    density: 1.04,
    cost: 16,
    nozzleTemperature: 250,
    bedTemperature: 100,
    firstLayerNozzleTemperature: 255,
    firstLayerBedTemperature: 105,
    enableFanCooling: false,
    maxFanSpeed: 0,
    chamberTemperature: 45,
  },
  ASA: {
    material: 'ASA',
    density: 1.07,
    cost: 22,
    nozzleTemperature: 255,
    bedTemperature: 100,
    firstLayerNozzleTemperature: 260,
    firstLayerBedTemperature: 105,
    enableFanCooling: false,
    maxFanSpeed: 0,
    chamberTemperature: 45,
  },
  TPU: {
    material: 'TPU',
    density: 1.21,
    cost: 24,
    nozzleTemperature: 225,
    bedTemperature: 50,
    firstLayerNozzleTemperature: 230,
    firstLayerBedTemperature: 55,
    enableFanCooling: true,
    maxFanSpeed: 50,
    retractionLength: 0.4, // shorter for flexible
    pressureAdvance: 0.06,
  },
  'PA-CF': {
    material: 'PA-CF',
    density: 1.15,
    cost: 50,
    nozzleTemperature: 280,
    bedTemperature: 90,
    firstLayerNozzleTemperature: 285,
    firstLayerBedTemperature: 95,
    enableFanCooling: false,
    maxFanSpeed: 0,
    chamberTemperature: 55,
    maxVolumetricSpeed: 8,
  },
};

/**
 * Maps each filament setting key to its category tab for dirty indicator tracking.
 */
export const FILAMENT_SETTING_TO_CATEGORY_MAP: Record<string, FilamentSettingsCategory> = {
  // Temperature tab
  nozzleTemperature: 'temperature',
  bedTemperature: 'temperature',
  firstLayerNozzleTemperature: 'temperature',
  firstLayerBedTemperature: 'temperature',
  chamberTemperature: 'temperature',
  maxVolumetricSpeed: 'temperature',
  
  // Flow tab
  flowRatio: 'flow',
  printSpeed: 'flow',
  enablePressureAdvance: 'flow',
  pressureAdvance: 'flow',
  pressureAdvanceSmoothTime: 'flow',
  enableVolumetricExtrusion: 'flow',
  maxVolumetricExtrusionRate: 'flow',
  
  // Cooling tab
  enableFanCooling: 'cooling',
  minFanSpeed: 'cooling',
  maxFanSpeed: 'cooling',
  bridgeFanSpeed: 'cooling',
  fullFanSpeedAtLayer: 'cooling',
  slowDownForLayerTime: 'cooling',
  minPrintSpeed: 'cooling',
  closeLoopFanPower: 'cooling',
  auxFanSpeed: 'cooling',
  exhaustFanSpeed: 'cooling',
  
  // Retraction tab
  retractionLength: 'retraction',
  retractionSpeed: 'retraction',
  detractionSpeed: 'retraction',
  retractionMinimumTravel: 'retraction',
  retractOnLayerChange: 'retraction',
  wipeBeforeRetract: 'retraction',
  retractionLiftZ: 'retraction',
  
  // Other tab
  name: 'other',
  material: 'other',
  color: 'other',
  density: 'other',
  cost: 'other',
  filamentLoadTime: 'other',
  filamentUnloadTime: 'other',
  filamentRammingParameters: 'other',
  toolchangeDelay: 'other',
  startGcode: 'other',
  endGcode: 'other',
};
