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
  | 'physical'
  | 'gcode'
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

  // Per-Feature Flow Ratios
  outerWallFlowRatio?: number;            // multiplier
  innerWallFlowRatio?: number;            // multiplier
  topSolidInfillFlowRatio?: number;       // multiplier
  bottomSolidInfillFlowRatio?: number;    // multiplier
  internalSolidInfillFlowRatio?: number;  // multiplier
  sparseInfillFlowRatio?: number;         // multiplier
  gapFillFlowRatio?: number;             // multiplier
  supportFlowRatio?: number;             // multiplier
  supportInterfaceFlowRatio?: number;     // multiplier
  overhangFlowRatio?: number;            // multiplier
  firstLayerFlowRatio?: number;          // multiplier
  setOtherFlowRatios?: boolean;          // when true, all ratios follow flowRatio

  // Shrinkage Compensation
  filamentShrink?: number;               // % XY shrinkage
  filamentShrinkageCompensationZ?: number; // % Z shrinkage

  // Advanced Cooling
  fanKickstart?: number;                 // seconds
  fanSpeedupTime?: number;               // seconds
  fanSpeedupOverhangs?: boolean;
  overhangFanSpeed?: number;             // 0-100%
  overhangFanThreshold?: string;         // threshold description

  // Filament Ironing (per-filament)
  filamentIroningFlow?: number;          // ratio
  filamentIroningInset?: number;         // mm
  filamentIroningSpacing?: number;       // mm
  filamentIroningSpeed?: number;         // mm/s

  // Interlocking Beam
  interlockingBeam?: boolean;
  interlockingBeamLayerCount?: number;
  interlockingBeamWidth?: number;        // mm
  interlockingBoundaryAvoidance?: number; // mm
  interlockingDepth?: number;            // mm
  interlockingOrientation?: number;      // degrees
  mmuSegmentedRegionMaxWidth?: number;   // mm
  mmuSegmentedRegionInterlockingDepth?: number; // mm

  // Cooling Moves (MMU/AMS)
  filamentCoolingMoves?: number;
  filamentCoolingInitialSpeed?: number;  // mm/s
  filamentCoolingFinalSpeed?: number;    // mm/s

  // Ramming & Stamping
  filamentChangeLength?: number;         // mm
  filamentStampingDistance?: number;      // mm
  filamentStampingLoadingSpeed?: number; // mm/s
  filamentMultitoolRamming?: boolean;
  filamentMultitoolRammingFlow?: number; // ratio
  filamentMultitoolRammingVolume?: number; // mm³

  // Wipe Tower Per-Filament
  filamentMinimalPurge?: number;         // mm³
  wipeTowerInterfaceFlowRatio?: number;  // ratio
  wipeTowerInterfaceSpeed?: number;      // mm/s
  wipeTowerIroningArea?: number;         // mm²

  // Metadata
  filamentVendor?: string;
  filamentNotes?: string;
  filamentIsSupport?: boolean;
  filamentSoluble?: boolean;

  // Misc Advanced
  temperatureVitrification?: number;     // °C (glass transition)
  activateAirFiltration?: boolean;
  bedTemperatureFormula?: string;        // formula string
  filamentFlushTemp?: number;            // °C
  filamentFlushVolumetricSpeed?: number; // mm³/s
  filamentLoadingSpeed?: number;         // mm/s
  filamentLoadingSpeedStart?: number;    // mm/s
  filamentUnloadingSpeed?: number;       // mm/s
  filamentUnloadingSpeedStart?: number;  // mm/s
  slowDownLayers?: number;              // number of initial slow layers
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

  // Per-Feature Flow Ratios
  outerWallFlowRatio: 1.0,
  innerWallFlowRatio: 1.0,
  topSolidInfillFlowRatio: 1.0,
  bottomSolidInfillFlowRatio: 1.0,
  internalSolidInfillFlowRatio: 1.0,
  sparseInfillFlowRatio: 1.0,
  gapFillFlowRatio: 1.0,
  supportFlowRatio: 1.0,
  supportInterfaceFlowRatio: 1.0,
  overhangFlowRatio: 1.0,
  firstLayerFlowRatio: 1.0,
  setOtherFlowRatios: false,

  // Shrinkage Compensation
  filamentShrink: 0,
  filamentShrinkageCompensationZ: 0,

  // Advanced Cooling
  fanKickstart: 0,
  fanSpeedupTime: 0,
  fanSpeedupOverhangs: false,
  overhangFanSpeed: 0,
  overhangFanThreshold: '',

  // Filament Ironing
  filamentIroningFlow: 0.15,
  filamentIroningInset: 0.25,
  filamentIroningSpacing: 0.1,
  filamentIroningSpeed: 15,

  // Interlocking Beam
  interlockingBeam: false,
  interlockingBeamLayerCount: 2,
  interlockingBeamWidth: 0.8,
  interlockingBoundaryAvoidance: 2,
  interlockingDepth: 0.4,
  interlockingOrientation: 22.5,
  mmuSegmentedRegionMaxWidth: 0,
  mmuSegmentedRegionInterlockingDepth: 0,

  // Cooling Moves (MMU/AMS)
  filamentCoolingMoves: 0,
  filamentCoolingInitialSpeed: 0,
  filamentCoolingFinalSpeed: 0,

  // Ramming & Stamping
  filamentChangeLength: 0,
  filamentStampingDistance: 0,
  filamentStampingLoadingSpeed: 0,
  filamentMultitoolRamming: false,
  filamentMultitoolRammingFlow: 0,
  filamentMultitoolRammingVolume: 0,

  // Wipe Tower Per-Filament
  filamentMinimalPurge: 0,
  wipeTowerInterfaceFlowRatio: 1.0,
  wipeTowerInterfaceSpeed: 0,
  wipeTowerIroningArea: 0,

  // Metadata
  filamentVendor: '',
  filamentNotes: '',
  filamentIsSupport: false,
  filamentSoluble: false,

  // Misc Advanced
  temperatureVitrification: 0,
  activateAirFiltration: false,
  bedTemperatureFormula: '',
  filamentFlushTemp: 0,
  filamentFlushVolumetricSpeed: 0,
  filamentLoadingSpeed: 0,
  filamentLoadingSpeedStart: 0,
  filamentUnloadingSpeed: 0,
  filamentUnloadingSpeedStart: 0,
  slowDownLayers: 0,
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
  
  // Physical tab
  density: 'physical',
  cost: 'physical',
  
  // G-code tab
  startGcode: 'gcode',
  endGcode: 'gcode',
  
  // Other tab
  name: 'other',
  material: 'other',
  color: 'other',
  filamentLoadTime: 'other',
  filamentUnloadTime: 'other',
  filamentRammingParameters: 'other',
  toolchangeDelay: 'other',

  // Per-Feature Flow Ratios
  outerWallFlowRatio: 'flow',
  innerWallFlowRatio: 'flow',
  topSolidInfillFlowRatio: 'flow',
  bottomSolidInfillFlowRatio: 'flow',
  internalSolidInfillFlowRatio: 'flow',
  sparseInfillFlowRatio: 'flow',
  gapFillFlowRatio: 'flow',
  supportFlowRatio: 'flow',
  supportInterfaceFlowRatio: 'flow',
  overhangFlowRatio: 'flow',
  firstLayerFlowRatio: 'flow',
  setOtherFlowRatios: 'flow',

  // Shrinkage Compensation
  filamentShrink: 'physical',
  filamentShrinkageCompensationZ: 'physical',

  // Advanced Cooling
  fanKickstart: 'cooling',
  fanSpeedupTime: 'cooling',
  fanSpeedupOverhangs: 'cooling',
  overhangFanSpeed: 'cooling',
  overhangFanThreshold: 'cooling',
  slowDownLayers: 'cooling',

  // Filament Ironing
  filamentIroningFlow: 'flow',
  filamentIroningInset: 'flow',
  filamentIroningSpacing: 'flow',
  filamentIroningSpeed: 'flow',

  // Interlocking Beam
  interlockingBeam: 'other',
  interlockingBeamLayerCount: 'other',
  interlockingBeamWidth: 'other',
  interlockingBoundaryAvoidance: 'other',
  interlockingDepth: 'other',
  interlockingOrientation: 'other',
  mmuSegmentedRegionMaxWidth: 'other',
  mmuSegmentedRegionInterlockingDepth: 'other',

  // Cooling Moves (MMU/AMS)
  filamentCoolingMoves: 'other',
  filamentCoolingInitialSpeed: 'other',
  filamentCoolingFinalSpeed: 'other',

  // Ramming & Stamping
  filamentChangeLength: 'other',
  filamentStampingDistance: 'other',
  filamentStampingLoadingSpeed: 'other',
  filamentMultitoolRamming: 'other',
  filamentMultitoolRammingFlow: 'other',
  filamentMultitoolRammingVolume: 'other',

  // Wipe Tower Per-Filament
  filamentMinimalPurge: 'other',
  wipeTowerInterfaceFlowRatio: 'other',
  wipeTowerInterfaceSpeed: 'other',
  wipeTowerIroningArea: 'other',

  // Metadata
  filamentVendor: 'other',
  filamentNotes: 'other',
  filamentIsSupport: 'other',
  filamentSoluble: 'other',

  // Misc Advanced – temperature-related
  temperatureVitrification: 'temperature',
  filamentFlushTemp: 'temperature',
  filamentFlushVolumetricSpeed: 'temperature',

  // Misc Advanced – other
  activateAirFiltration: 'other',
  bedTemperatureFormula: 'other',
  filamentLoadingSpeed: 'other',
  filamentLoadingSpeedStart: 'other',
  filamentUnloadingSpeed: 'other',
  filamentUnloadingSpeedStart: 'other',
};

/**
 * Maps filament settings to their OrcaSlicer mode (comSimple/comAdvanced).
 * Settings in 'simple' mode are shown in Basic view.
 * Settings in 'advanced' mode are shown only in Advanced view.
 */
export const FILAMENT_SETTING_MODE_MAP: Record<string, 'simple' | 'advanced'> = {
  // Simple (Basic) settings
  name: 'simple',
  material: 'simple',
  color: 'simple',
  nozzleTemperature: 'simple',
  bedTemperature: 'simple',
  density: 'simple',
  cost: 'simple',
  flowRatio: 'simple',
  enableFanCooling: 'simple',
  minFanSpeed: 'simple',
  maxFanSpeed: 'simple',
  // Everything else is advanced
};
