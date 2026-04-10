/**
 * OrcaSlicer settings type definitions
 * Maps to OrcaSlicer's process profile settings
 */

/** View modes for settings panel complexity */
export type SettingsViewMode = 'simple' | 'advanced';

/** Category tabs in advanced mode */
export type SettingsCategory = 'quality' | 'strength' | 'speed' | 'support' | 'multimaterial' | 'other';

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
export type SupportType = 'none' | 'normal' | 'tree' | 'tree_auto';

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

/** OrcaSlicer setting mode - matches comSimple/comAdvanced from PrintConfig.cpp */
export type OrcaSettingMode = 'simple' | 'advanced' | 'develop';

/** Simple settings — the base typed settings for Simple mode */
export interface SimpleSlicerSettings {
  infillDensity: number;           // 0-100%
  infillPattern: InfillPattern;
  wallCount: number;               // 1-10 perimeters
  bedAdhesion: BedAdhesionType;
  enableSupports: boolean;
  layerHeight: number;             // mm (0.08-0.32 typical)
  firstLayerHeight: number;        // mm
  lineWidthDefault: number;        // mm
  lineWidthFirstLayer: number;     // mm
  topLayers: number;
  bottomLayers: number;
}

/** Advanced settings - full OrcaSlicer parameter set */
export interface AdvancedSlicerSettings extends SimpleSlicerSettings {
  // Line width per feature
  lineWidthOuterWall: number;
  lineWidthInnerWall: number;
  lineWidthTopSurface: number;
  lineWidthSparseInfill: number;
  lineWidthInternalSolidInfill: number;
  lineWidthSupport: number;
  
  // Seam settings
  seamPosition: SeamPosition;
  seamGap: number;                 // mm or %
  scarfJointSeam: ScarfJointSeam;
  staggeredInnerSeams: boolean;
  
  // Scarf joint settings (beta)
  conditionalScarfJoint: boolean;
  conditionalAngleThreshold: number;
  conditionalOverhangThreshold: number;
  scarfJointSpeed: number;
  scarfStartHeight: number;
  scarfAroundEntireWall: boolean;
  scarfLength: number;
  scarfSteps: number;
  scarfJointFlowRatio: number;
  scarfJointForInnerWalls: boolean;
  
  // Wipe settings
  roleBaseWipeSpeed: boolean;
  wipeSpeed: number;
  wipeOnLoops: boolean;
  wipeBeforeExternalLoop: boolean;
  
  // Precision settings
  sliceGapClosingRadius: number;
  resolution: number;
  arcFitting: boolean;
  xyHoleCompensation: number;
  xyContourCompensation: number;
  elephantFootCompensation: number;
  elephantFootCompensationLayers: number;
  preciseWall: boolean;
  preciseZHeight: boolean;
  convertHolesToPolyholes: boolean;
  polyholeDetectionMargin: number;
  
  // Speed settings
  printSpeed: number;
  outerWallSpeed: number;
  innerWallSpeed: number;
  infillSpeed: number;
  sparseInfillSpeed: number;
  solidInfillSpeed: number;
  topSurfaceSpeed: number;
  travelSpeed: number;
  firstLayerSpeed: number;
  
  // Acceleration & jerk
  outerWallAcceleration: number;
  innerWallAcceleration: number;
  topSurfaceAcceleration: number;
  infillAcceleration: number;
  travelAcceleration: number;
  defaultAcceleration: number;
  
  // Temperature
  nozzleTemp: number;
  bedTemp: number;
  firstLayerNozzleTemp: number;
  firstLayerBedTemp: number;
  
  // Retraction settings
  retractionLength: number;        // mm
  retractionSpeed: number;         // mm/s
  detractionSpeed: number;         // mm/s
  retractionMinimumTravel: number; // mm
  retractOnLayerChange: boolean;
  wipeBeforeRetract: boolean;
  retractionLiftZ: number;         // mm - Z hop
  
  // Cooling settings
  enableFanCooling: boolean;
  minFanSpeed: number;             // 0-100%
  maxFanSpeed: number;             // 0-100%
  bridgeFanSpeed: number;          // 0-100%
  fullFanSpeedAtLayer: number;
  slowDownForLayerTime: number;    // seconds
  minPrintSpeed: number;           // mm/s when slowing for cooling
  
  // Ironing settings
  enableIroning: boolean;
  ironingPattern: 'zigzag' | 'concentric';
  ironingFlowRate: number;         // % of normal flow
  ironingSpacing: number;          // mm
  ironingSpeed: number;            // mm/s
  ironingAngle: number;            // degrees
  
  // Strength settings
  infillOverlap: number;           // %
  infillAnchorMaxLength: number;   // mm
  
  // Support settings (when enabled)
  supportType: SupportType;
  supportDensity: number;
  supportAngle: number;
  supportTopZDistance: number;
  supportBottomZDistance: number;
  supportInterfaceLayers: number;
  supportXYDistance: number;
  supportBaseInterfaceLayers: number;
  
  // Multimaterial settings
  filament1ProfileId?: string;
  filament2ProfileId?: string;
  filament3ProfileId?: string;
  purgeOnLayerChange?: boolean;
  purgeTowerVolume?: number;
  wipeTowerWidth?: number;
  
  // Wall generator & Walls & surfaces settings
  minWallThickness?: number;
  
  // Flow ratio settings
  outerWallFlowRatio?: number;
  innerWallFlowRatio?: number;
  
  // Bridging settings
  maxBridgeLength?: number;
  bridgeSpeedReduction?: number;
  
  // Overhangs settings
  overhangAngle?: number;
  overhangPerimeterSpeed?: number;

  // =========================================================================
  // OrcaSlicer Process Settings — Full Parity
  // All properties below match OrcaSlicer PrintConfig.cpp definitions.
  // Optional because not all backends populate every setting.
  // =========================================================================

  // --- Quality: Simple mode settings ---
  firstLayerSequenceChoice?: string;
  onlyOneWallFirstLayer?: boolean;
  onlyOneWallTop?: boolean;
  otherLayersSequenceChoice?: string;
  treeSupportAutoBrim?: boolean;
  treeSupportBrimWidth?: number;

  // --- Quality: Advanced mode settings ---
  bridgeFlow?: number;
  counterboreHoleBridging?: string;
  detectOverhangWall?: boolean;
  dontFilterInternalBridges?: boolean;
  enableExtraBridgeLayer?: boolean;
  extraPerimetersOnOverhangs?: boolean;
  filamentIroningFlow?: number;
  filamentIroningInset?: number;
  filamentIroningSpacing?: number;
  holeToPolyholeTwisted?: boolean;
  initialLayerMinBeadWidth?: number;
  interfaceShells?: boolean;
  internalBridgeFlow?: number;
  ironingAngleFixed?: number;
  ironingInset?: number;
  ironingType?: IroningType;
  isInfillFirst?: boolean;
  makeOverhangPrintable?: boolean;
  makeOverhangPrintableAngle?: number;
  makeOverhangPrintableHoleSize?: number;
  maxTravelDetourDistance?: number;
  minBeadWidth?: number;
  minFeatureSize?: number;
  minLengthFactor?: number;
  minWidthTopSurface?: number;
  overhangReverse?: boolean;
  overhangReverseInternalOnly?: boolean;
  overhangReverseThreshold?: number;
  printFlowRatio?: number;
  reduceCrossingWall?: boolean;
  smallAreaInfillFlowCompensation?: boolean;
  thickBridges?: boolean;
  thickInternalBridges?: boolean;
  wallDirection?: number;
  wallDistributionCount?: number;
  wallGenerator?: WallGenerator;
  wallSequence?: WallSequence;
  wallTransitionAngle?: number;
  wallTransitionFilterDeviation?: number;
  wallTransitionLength?: number;

  // --- Strength: Simple mode settings ---
  bottomShellThickness?: number;
  bottomSurfaceDensity?: number;
  bottomSurfacePattern?: string;
  fillMultiline?: boolean;
  internalSolidInfillPattern?: string;
  topShellThickness?: number;
  topSurfaceDensity?: number;

  // --- Strength: Advanced mode settings ---
  alignInfillDirectionToModel?: boolean;
  alternateExtraWall?: boolean;
  bridgeAngle?: number;
  bridgeDensity?: number;
  detectNarrowInternalSolidInfill?: boolean;
  detectThinWall?: boolean;
  ensureVerticalShellThickness?: string;
  extraSolidInfills?: boolean;
  gapFillTarget?: GapFillTarget;
  infillCombination?: boolean;
  infillCombinationMaxLayerHeight?: number;
  infillDirection?: number;
  infillLockDepth?: number;
  infillOverhangAngle?: number;
  infillShiftStep?: number;
  internalBridgeAngle?: number;
  internalBridgeDensity?: number;
  lateralLatticeAngle1?: number;
  lateralLatticeAngle2?: number;
  minimumSparseInfillArea?: number;
  skeletonInfillDensity?: number;
  skeletonInfillLineWidth?: number;
  skinInfillDensity?: number;
  skinInfillDepth?: number;
  skinInfillLineWidth?: number;
  solidInfillDirection?: number;
  solidInfillRotateTemplate?: boolean;
  sparseInfillRotateTemplate?: boolean;
  symmetricInfillYAxis?: boolean;
  topBottomInfillWallOverlap?: number;

  // --- Speed: Advanced mode settings (ALL speed settings are Advanced in OrcaSlicer) ---
  accelToDecelEnable?: boolean;
  accelToDecelFactor?: number;
  bridgeAcceleration?: number;
  bridgeSpeed?: number;
  defaultJerk?: number;
  defaultJunctionDeviation?: number;
  enableOverhangSpeed?: boolean;
  filamentIroningSpeed?: number;
  gapInfillSpeed?: number;
  infillJerk?: number;
  initialLayerAcceleration?: number;
  initialLayerJerk?: number;
  initialLayerTravelSpeed?: number;
  innerWallJerk?: number;
  internalBridgeSpeed?: number;
  internalSolidInfillAcceleration?: number;
  outerWallJerk?: number;
  overhang1_4Speed?: number;
  overhang2_4Speed?: number;
  overhang3_4Speed?: number;
  overhang4_4Speed?: number;
  slowDownLayers?: number;
  slowdownForCurledPerimeters?: boolean;
  smallPerimeterSpeed?: number;
  smallPerimeterThreshold?: number;
  supportInterfaceSpeed?: number;
  supportSpeed?: number;
  topSurfaceJerk?: number;
  travelJerk?: number;

  // --- Support: Simple mode settings ---
  brimType?: BrimType;
  brimWidth?: number;
  supportFilament?: number;
  supportInterfaceNotForBody?: boolean;
  supportOnBuildPlateOnly?: boolean;
  supportThresholdOverlap?: number;

  // --- Support: Advanced mode settings ---
  bridgeNoSupport?: boolean;
  brimEars?: boolean;
  brimEarsDetectionLength?: number;
  brimEarsMaxAngle?: number;
  brimObjectGap?: number;
  brimUseEfcOutline?: boolean;
  combineBrims?: boolean;
  independentSupportLayerHeight?: boolean;
  raftContactDistance?: number;
  raftExpansion?: number;
  raftFirstLayerDensity?: number;
  raftFirstLayerExpansion?: number;
  raftLayers?: number;
  skirtStartAngle?: number;
  supportBasePattern?: string;
  supportBasePatternSpacing?: number;
  supportBottomInterfaceSpacing?: number;
  supportCriticalRegionsOnly?: boolean;
  supportExpansion?: number;
  supportInterfaceBottomLayers?: number;
  supportInterfaceFilament?: number;
  supportInterfaceLoopPattern?: boolean;
  supportInterfacePattern?: string;
  supportInterfaceSpacing?: number;
  supportIroning?: boolean;
  supportIroningFlow?: number;
  supportIroningPattern?: string;
  supportIroningSpacing?: number;
  supportObjectFirstLayerGap?: number;
  supportRemoveSmallOverhang?: boolean;
  supportStyle?: SupportStyle;
  treeSupportAngleSlow?: number;
  treeSupportBranchAngle?: number;
  treeSupportBranchAngleOrganic?: number;
  treeSupportBranchDiameter?: number;
  treeSupportBranchDiameterAngle?: number;
  treeSupportBranchDiameterOrganic?: number;
  treeSupportBranchDistance?: number;
  treeSupportBranchDistanceOrganic?: number;
  treeSupportTipDiameter?: number;
  treeSupportTopRate?: number;
  treeSupportWallCount?: number;
  treeSupportWithInfill?: boolean;

  // --- Basic mode: Skirt ---
  skirtLoops?: number;
  skirtHeight?: number;
  skirtDistance?: number;
  skirtSpeed?: number;

  // --- Basic mode: Special mode ---
  spiralVase?: boolean;
  smoothSpiral?: boolean;
  printSequence?: 'by_layer' | 'by_object';
  timelapse?: string;
  addLineNumber?: boolean;

  // --- Basic mode: Strength - Top surface pattern ---
  topSurfacePattern?: string;

  // --- Basic mode: Multimaterial ---
  wipeTowerEnable?: boolean;
  flushIntoInfill?: boolean;
  flushIntoObjects?: boolean;
  flushIntoSupport?: boolean;

  // --- Others: Simple mode settings (Fuzzy Skin) ---
  fuzzySkin?: boolean;
  fuzzySkinFirstLayer?: boolean;
  fuzzySkinMode?: FuzzySkinMode;
  fuzzySkinNoiseType?: FuzzySkinNoiseType;
  fuzzySkinPointDistance?: number;
  fuzzySkinThickness?: number;

  // --- Others: Advanced mode settings ---
  fuzzySkinOctaves?: number;
  fuzzySkinPersistence?: number;
  fuzzySkinScale?: number;

  // --- Other: Advanced ---
  slicingMode?: SlicingMode;
}

/** Default values for basic settings */
/** Default values for simple settings */
export const DEFAULT_SIMPLE_SETTINGS: SimpleSlicerSettings = {
  infillDensity: 20,
  infillPattern: 'crosshatch',
  wallCount: 3,
  bedAdhesion: 'skirt',
  enableSupports: false,
  layerHeight: 0.2,
  firstLayerHeight: 0.2,
  lineWidthDefault: 0.45,
  lineWidthFirstLayer: 0.5,
  topLayers: 4,
  bottomLayers: 3,
};

/** Default values for advanced settings */
export const DEFAULT_ADVANCED_SETTINGS: AdvancedSlicerSettings = {
  ...DEFAULT_SIMPLE_SETTINGS,
  lineWidthOuterWall: 0.45,
  lineWidthInnerWall: 0.45,
  lineWidthTopSurface: 0.45,
  lineWidthSparseInfill: 0.45,
  lineWidthInternalSolidInfill: 0.45,
  lineWidthSupport: 0.45,
  seamPosition: 'aligned',
  seamGap: 0,
  scarfJointSeam: 'none',
  staggeredInnerSeams: false,
  conditionalScarfJoint: false,
  conditionalAngleThreshold: 0,
  conditionalOverhangThreshold: 0,
  scarfJointSpeed: 0,
  scarfStartHeight: 0,
  scarfAroundEntireWall: false,
  scarfLength: 10,
  scarfSteps: 10,
  scarfJointFlowRatio: 1.0,
  scarfJointForInnerWalls: false,
  roleBaseWipeSpeed: false,
  wipeSpeed: 80,
  wipeOnLoops: true,
  wipeBeforeExternalLoop: false,
  sliceGapClosingRadius: 0.05,
  resolution: 0.01,
  arcFitting: false,
  xyHoleCompensation: 0,
  xyContourCompensation: 0,
  elephantFootCompensation: 0.15,
  elephantFootCompensationLayers: 1,
  preciseWall: true,
  preciseZHeight: false,
  convertHolesToPolyholes: false,
  polyholeDetectionMargin: 0.01,
  printSpeed: 200,
  outerWallSpeed: 60,
  innerWallSpeed: 200,
  infillSpeed: 200,
  sparseInfillSpeed: 200,
  solidInfillSpeed: 200,
  topSurfaceSpeed: 60,
  travelSpeed: 250,
  firstLayerSpeed: 40,
  outerWallAcceleration: 500,
  innerWallAcceleration: 1000,
  topSurfaceAcceleration: 500,
  infillAcceleration: 2000,
  travelAcceleration: 5000,
  defaultAcceleration: 5000,
  nozzleTemp: 220,
  bedTemp: 60,
  firstLayerNozzleTemp: 220,
  firstLayerBedTemp: 60,
  retractionLength: 1.0,
  retractionSpeed: 40,
  detractionSpeed: 40,
  retractionMinimumTravel: 1.0,
  retractOnLayerChange: false,
  wipeBeforeRetract: false,
  retractionLiftZ: 0,
  enableFanCooling: true,
  minFanSpeed: 30,
  maxFanSpeed: 100,
  bridgeFanSpeed: 100,
  fullFanSpeedAtLayer: 3,
  slowDownForLayerTime: 5,
  minPrintSpeed: 10,
  enableIroning: false,
  ironingPattern: 'zigzag',
  ironingFlowRate: 15,
  ironingSpacing: 0.1,
  ironingSpeed: 15,
  ironingAngle: -1,
  infillOverlap: 10,
  infillAnchorMaxLength: 10,
  supportType: 'none',
  supportDensity: 15,
  supportAngle: 45,
  supportTopZDistance: 0.2,
  supportBottomZDistance: 0.2,
  supportInterfaceLayers: 2,
  supportXYDistance: 0.35,
  supportBaseInterfaceLayers: 0,
};

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
// CHANGE TRACKING SYSTEM
// =============================================================================

import { useState, useCallback, useMemo } from 'react';

/**
 * Maps each setting key to its category tab for dirty indicator tracking.
 * Used to determine which tabs should be highlighted when settings change.
 */
export const SETTING_TO_CATEGORY_MAP: Record<string, SettingsCategory> = {
  // Quality tab
  layerHeight: 'quality',
  firstLayerHeight: 'quality',
  lineWidthDefault: 'quality',
  lineWidthFirstLayer: 'quality',
  lineWidthOuterWall: 'quality',
  lineWidthInnerWall: 'quality',
  lineWidthTopSurface: 'quality',
  lineWidthSparseInfill: 'quality',
  lineWidthInternalSolidInfill: 'quality',
  lineWidthSupport: 'quality',
  seamPosition: 'quality',
  seamGap: 'quality',
  scarfJointSeam: 'quality',
  staggeredInnerSeams: 'quality',
  conditionalScarfJoint: 'quality',
  conditionalAngleThreshold: 'quality',
  conditionalOverhangThreshold: 'quality',
  scarfJointSpeed: 'quality',
  scarfStartHeight: 'quality',
  scarfAroundEntireWall: 'quality',
  scarfLength: 'quality',
  scarfSteps: 'quality',
  scarfJointFlowRatio: 'quality',
  scarfJointForInnerWalls: 'quality',
  roleBaseWipeSpeed: 'quality',
  wipeSpeed: 'quality',
  wipeOnLoops: 'quality',
  wipeBeforeExternalLoop: 'quality',
  sliceGapClosingRadius: 'quality',
  resolution: 'quality',
  arcFitting: 'quality',
  xyHoleCompensation: 'quality',
  xyContourCompensation: 'quality',
  elephantFootCompensation: 'quality',
  elephantFootCompensationLayers: 'quality',
  preciseWall: 'quality',
  preciseZHeight: 'quality',
  convertHolesToPolyholes: 'quality',
  polyholeDetectionMargin: 'quality',
  
  // Strength tab
  infillDensity: 'strength',
  infillPattern: 'strength',
  wallCount: 'strength',
  topLayers: 'strength',
  bottomLayers: 'strength',
  infillOverlap: 'strength',
  infillAnchorMaxLength: 'strength',
  
  // Speed tab
  printSpeed: 'speed',
  outerWallSpeed: 'speed',
  innerWallSpeed: 'speed',
  infillSpeed: 'speed',
  sparseInfillSpeed: 'speed',
  solidInfillSpeed: 'speed',
  topSurfaceSpeed: 'speed',
  travelSpeed: 'speed',
  firstLayerSpeed: 'speed',
  outerWallAcceleration: 'speed',
  innerWallAcceleration: 'speed',
  topSurfaceAcceleration: 'speed',
  infillAcceleration: 'speed',
  travelAcceleration: 'speed',
  defaultAcceleration: 'speed',
  
  // Support tab
  enableSupports: 'support',
  supportType: 'support',
  supportDensity: 'support',
  supportAngle: 'support',
  supportTopZDistance: 'support',
  supportBottomZDistance: 'support',
  supportInterfaceLayers: 'support',
  supportXYDistance: 'support',
  supportBaseInterfaceLayers: 'support',
  bedAdhesion: 'support',
  
  // Other tab - Temperature
  nozzleTemp: 'other',
  bedTemp: 'other',
  firstLayerNozzleTemp: 'other',
  firstLayerBedTemp: 'other',
  
  // Other tab - Retraction
  retractionLength: 'other',
  retractionSpeed: 'other',
  detractionSpeed: 'other',
  retractionMinimumTravel: 'other',
  retractOnLayerChange: 'other',
  wipeBeforeRetract: 'other',
  retractionLiftZ: 'other',
  
  // Other tab - Cooling
  enableFanCooling: 'other',
  minFanSpeed: 'other',
  maxFanSpeed: 'other',
  bridgeFanSpeed: 'other',
  fullFanSpeedAtLayer: 'other',
  slowDownForLayerTime: 'other',
  minPrintSpeed: 'other',
  
  // Other tab - Ironing
  enableIroning: 'other',
  ironingPattern: 'other',
  ironingFlowRate: 'other',
  ironingSpacing: 'other',
  ironingSpeed: 'other',
  ironingAngle: 'other',
  
  // Multimaterial tab
  filament1ProfileId: 'multimaterial',
  filament2ProfileId: 'multimaterial',
  filament3ProfileId: 'multimaterial',
  purgeOnLayerChange: 'multimaterial',
  purgeTowerVolume: 'multimaterial',
  wipeTowerWidth: 'multimaterial',
  wipeTowerEnable: 'multimaterial',
  flushIntoInfill: 'multimaterial',
  flushIntoSupport: 'multimaterial',
  
  // Quality tab - Wall generator, Walls & surfaces, Flow ratio, Bridging, Overhangs
  minWallThickness: 'quality',
  outerWallFlowRatio: 'quality',
  innerWallFlowRatio: 'quality',
  maxBridgeLength: 'quality',
  bridgeSpeedReduction: 'quality',
  overhangAngle: 'quality',
  overhangPerimeterSpeed: 'quality',

  // Quality — new OrcaSlicer settings
  firstLayerSequenceChoice: 'quality',
  onlyOneWallFirstLayer: 'quality',
  onlyOneWallTop: 'quality',
  otherLayersSequenceChoice: 'quality',
  treeSupportAutoBrim: 'quality',
  treeSupportBrimWidth: 'quality',
  bridgeFlow: 'quality',
  counterboreHoleBridging: 'quality',
  detectOverhangWall: 'quality',
  dontFilterInternalBridges: 'quality',
  enableExtraBridgeLayer: 'quality',
  extraPerimetersOnOverhangs: 'quality',
  filamentIroningFlow: 'quality',
  filamentIroningInset: 'quality',
  filamentIroningSpacing: 'quality',
  holeToPolyholeTwisted: 'quality',
  initialLayerMinBeadWidth: 'quality',
  interfaceShells: 'quality',
  internalBridgeFlow: 'quality',
  ironingAngleFixed: 'quality',
  ironingInset: 'quality',
  ironingType: 'quality',
  isInfillFirst: 'quality',
  makeOverhangPrintable: 'quality',
  makeOverhangPrintableAngle: 'quality',
  makeOverhangPrintableHoleSize: 'quality',
  maxTravelDetourDistance: 'quality',
  minBeadWidth: 'quality',
  minFeatureSize: 'quality',
  minLengthFactor: 'quality',
  minWidthTopSurface: 'quality',
  overhangReverse: 'quality',
  overhangReverseInternalOnly: 'quality',
  overhangReverseThreshold: 'quality',
  printFlowRatio: 'quality',
  reduceCrossingWall: 'quality',
  smallAreaInfillFlowCompensation: 'quality',
  thickBridges: 'quality',
  thickInternalBridges: 'quality',
  wallDirection: 'quality',
  wallDistributionCount: 'quality',
  wallGenerator: 'quality',
  wallSequence: 'quality',
  wallTransitionAngle: 'quality',
  wallTransitionFilterDeviation: 'quality',
  wallTransitionLength: 'quality',

  // Strength — new OrcaSlicer settings
  bottomShellThickness: 'strength',
  bottomSurfaceDensity: 'strength',
  bottomSurfacePattern: 'strength',
  fillMultiline: 'strength',
  internalSolidInfillPattern: 'strength',
  topShellThickness: 'strength',
  topSurfaceDensity: 'strength',
  topSurfacePattern: 'strength',
  alignInfillDirectionToModel: 'strength',
  alternateExtraWall: 'strength',
  bridgeAngle: 'strength',
  bridgeDensity: 'strength',
  detectNarrowInternalSolidInfill: 'strength',
  detectThinWall: 'strength',
  ensureVerticalShellThickness: 'strength',
  extraSolidInfills: 'strength',
  gapFillTarget: 'strength',
  infillCombination: 'strength',
  infillCombinationMaxLayerHeight: 'strength',
  infillDirection: 'strength',
  infillLockDepth: 'strength',
  infillOverhangAngle: 'strength',
  infillShiftStep: 'strength',
  internalBridgeAngle: 'strength',
  internalBridgeDensity: 'strength',
  lateralLatticeAngle1: 'strength',
  lateralLatticeAngle2: 'strength',
  minimumSparseInfillArea: 'strength',
  skeletonInfillDensity: 'strength',
  skeletonInfillLineWidth: 'strength',
  skinInfillDensity: 'strength',
  skinInfillDepth: 'strength',
  skinInfillLineWidth: 'strength',
  solidInfillDirection: 'strength',
  solidInfillRotateTemplate: 'strength',
  sparseInfillRotateTemplate: 'strength',
  symmetricInfillYAxis: 'strength',
  topBottomInfillWallOverlap: 'strength',

  // Speed — new OrcaSlicer settings
  accelToDecelEnable: 'speed',
  accelToDecelFactor: 'speed',
  bridgeAcceleration: 'speed',
  bridgeSpeed: 'speed',
  defaultJerk: 'speed',
  defaultJunctionDeviation: 'speed',
  enableOverhangSpeed: 'speed',
  filamentIroningSpeed: 'speed',
  gapInfillSpeed: 'speed',
  infillJerk: 'speed',
  initialLayerAcceleration: 'speed',
  initialLayerJerk: 'speed',
  initialLayerTravelSpeed: 'speed',
  innerWallJerk: 'speed',
  internalBridgeSpeed: 'speed',
  internalSolidInfillAcceleration: 'speed',
  outerWallJerk: 'speed',
  overhang1_4Speed: 'speed',
  overhang2_4Speed: 'speed',
  overhang3_4Speed: 'speed',
  overhang4_4Speed: 'speed',
  slowDownLayers: 'speed',
  slowdownForCurledPerimeters: 'speed',
  smallPerimeterSpeed: 'speed',
  smallPerimeterThreshold: 'speed',
  supportInterfaceSpeed: 'speed',
  supportSpeed: 'speed',
  topSurfaceJerk: 'speed',
  travelJerk: 'speed',

  // Support — new OrcaSlicer settings
  brimType: 'support',
  brimWidth: 'support',
  supportFilament: 'support',
  supportInterfaceNotForBody: 'support',
  supportOnBuildPlateOnly: 'support',
  supportThresholdOverlap: 'support',
  bridgeNoSupport: 'support',
  brimEars: 'support',
  brimEarsDetectionLength: 'support',
  brimEarsMaxAngle: 'support',
  brimObjectGap: 'support',
  brimUseEfcOutline: 'support',
  combineBrims: 'support',
  independentSupportLayerHeight: 'support',
  raftContactDistance: 'support',
  raftExpansion: 'support',
  raftFirstLayerDensity: 'support',
  raftFirstLayerExpansion: 'support',
  raftLayers: 'support',
  skirtStartAngle: 'support',
  supportBasePattern: 'support',
  supportBasePatternSpacing: 'support',
  supportBottomInterfaceSpacing: 'support',
  supportCriticalRegionsOnly: 'support',
  supportExpansion: 'support',
  supportInterfaceBottomLayers: 'support',
  supportInterfaceFilament: 'support',
  supportInterfaceLoopPattern: 'support',
  supportInterfacePattern: 'support',
  supportInterfaceSpacing: 'support',
  supportIroning: 'support',
  supportIroningFlow: 'support',
  supportIroningPattern: 'support',
  supportIroningSpacing: 'support',
  supportObjectFirstLayerGap: 'support',
  supportRemoveSmallOverhang: 'support',
  supportStyle: 'support',
  treeSupportAngleSlow: 'support',
  treeSupportBranchAngle: 'support',
  treeSupportBranchAngleOrganic: 'support',
  treeSupportBranchDiameter: 'support',
  treeSupportBranchDiameterAngle: 'support',
  treeSupportBranchDiameterOrganic: 'support',
  treeSupportBranchDistance: 'support',
  treeSupportBranchDistanceOrganic: 'support',
  treeSupportTipDiameter: 'support',
  treeSupportTopRate: 'support',
  treeSupportWallCount: 'support',
  treeSupportWithInfill: 'support',

  // Others — new OrcaSlicer settings (Fuzzy Skin)
  fuzzySkin: 'other',
  fuzzySkinFirstLayer: 'other',
  fuzzySkinMode: 'other',
  fuzzySkinNoiseType: 'other',
  fuzzySkinPointDistance: 'other',
  fuzzySkinThickness: 'other',
  fuzzySkinOctaves: 'other',
  fuzzySkinPersistence: 'other',
  fuzzySkinScale: 'other',
  slicingMode: 'other',
  skirtLoops: 'other',
  skirtHeight: 'other',
  spiralVase: 'other',
  printSequence: 'other',
};

/**
 * Maps each setting to its OrcaSlicer mode (simple/advanced/develop).
 * Settings NOT in this map default to 'advanced'.
 * Source: OrcaSlicer PrintConfig.cpp def->mode values.
 */
export const SETTING_MODE_MAP: Record<string, OrcaSettingMode> = {
  // Quality — Simple (per SimplyPrint)
  layerHeight: 'simple',
  firstLayerHeight: 'simple',
  seamPosition: 'simple',
  scarfJointFlowRatio: 'simple',
  preciseWall: 'simple',
  onlyOneWallFirstLayer: 'simple',
  onlyOneWallTop: 'simple',

  // Strength — Simple
  wallCount: 'simple',
  topLayers: 'simple',
  topShellThickness: 'simple',
  topSurfaceDensity: 'simple',
  topSurfacePattern: 'simple',
  bottomLayers: 'simple',
  bottomShellThickness: 'simple',
  bottomSurfaceDensity: 'simple',
  bottomSurfacePattern: 'simple',
  infillDensity: 'simple',
  fillMultiline: 'simple',
  infillPattern: 'simple',
  internalSolidInfillPattern: 'simple',

  // Speed — NO simple settings (all Advanced per SimplyPrint)

  // Support — Simple
  enableSupports: 'simple',
  supportType: 'simple',
  supportAngle: 'simple',
  supportThresholdOverlap: 'simple',
  supportOnBuildPlateOnly: 'simple',
  supportFilament: 'simple',
  supportInterfaceFilament: 'simple',
  treeSupportAutoBrim: 'simple',
  treeSupportBrimWidth: 'simple',

  // Multimaterial — Simple
  wipeTowerEnable: 'simple',
  wipeTowerWidth: 'simple',
  wipeTowerPrimeVolume: 'simple',
  preheatSteps: 'simple',
  flushIntoInfill: 'simple',
  flushIntoObject: 'simple',
  flushIntoSupport: 'simple',

  // Others — Simple
  skirtLoops: 'simple',
  skirtHeight: 'simple',
  brimType: 'simple',
  brimWidth: 'simple',
  printSequence: 'simple',
  spiralVase: 'simple',
  smoothSpiral: 'simple',
  timelapse: 'simple',
  fuzzySkin: 'simple',
  fuzzySkinMode: 'simple',
  fuzzySkinNoiseType: 'simple',
  fuzzySkinPointDistance: 'simple',
  fuzzySkinThickness: 'simple',
  fuzzySkinFirstLayer: 'simple',
  addLineNumber: 'simple',
};

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
 * 
 * @param initialSettings - The initial/original settings to track against
 * @returns TrackedSettingsState with utilities for change management
 * 
 * @example
 * ```tsx
 * const { current, hasChanges, resetToOriginal, isDirty } = useTrackedSettings(profileSettings);
 * 
 * // Check if layer height was modified
 * if (hasChanges('layerHeight')) {
 *   // Show reset icon
 * }
 * 
 * // Check if Quality tab has any changes
 * if (isCategoryDirty('quality')) {
 *   // Highlight the tab
 * }
 * ```
 */
export function useTrackedSettings<T extends Record<string, unknown>>(
  initialSettings: T
): TrackedSettingsState<T> {
  // Store deep clone of original to prevent mutation
  const [original] = useState<T>(() => deepClone(initialSettings));
  const [current, setCurrent] = useState<T>(() => deepClone(initialSettings));

  /**
   * Check if a specific setting has been modified from original
   */
  const hasChanges = useCallback((key: keyof T): boolean => {
    return !deepEqual(original[key], current[key]);
  }, [original, current]);

  /**
   * Get all keys that have been modified
   */
  const getChangedKeys = useCallback((): (keyof T)[] => {
    return (Object.keys(current) as (keyof T)[]).filter(key => hasChanges(key));
  }, [current, hasChanges]);

  /**
   * Reset a single setting to its original value
   */
  const resetToOriginal = useCallback((key: keyof T): void => {
    setCurrent(prev => ({
      ...prev,
      [key]: deepClone(original[key]),
    }));
  }, [original]);

  /**
   * Reset all settings to original values
   */
  const resetAll = useCallback((): void => {
    setCurrent(deepClone(original));
  }, [original]);

  /**
   * Update a single setting value
   */
  const updateSetting = useCallback(<K extends keyof T>(key: K, value: T[K]): void => {
    setCurrent(prev => ({
      ...prev,
      [key]: value,
    }));
  }, []);

  /**
   * Update multiple settings at once
   */
  const updateSettings = useCallback((updates: Partial<T>): void => {
    setCurrent(prev => ({
      ...prev,
      ...updates,
    }));
  }, []);

  /**
   * Get the original value for a setting
   */
  const getOriginalValue = useCallback(<K extends keyof T>(key: K): T[K] => {
    return original[key];
  }, [original]);

  /**
   * Compute whether any setting is dirty
   */
  const isDirty = useMemo((): boolean => {
    return getChangedKeys().length > 0;
  }, [getChangedKeys]);

  /**
   * Compute map of changed keys grouped by category
   */
  const changedKeysPerCategory = useMemo((): Map<SettingsCategory, Set<keyof T>> => {
    const categoryMap = new Map<SettingsCategory, Set<keyof T>>();
    
    // Initialize all categories with empty sets
    const categories: SettingsCategory[] = ['quality', 'strength', 'speed', 'support', 'multimaterial', 'other'];
    categories.forEach(cat => categoryMap.set(cat, new Set()));
    
    // Group changed keys by category
    const changedKeys = getChangedKeys();
    changedKeys.forEach(key => {
      const category = SETTING_TO_CATEGORY_MAP[key as string] || 'other';
      categoryMap.get(category)?.add(key);
    });
    
    return categoryMap;
  }, [getChangedKeys]);

  /**
   * Check if a specific category has any modified settings
   */
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
 * Type guard to check if a value is a valid settings category
 */
export function isValidCategory(value: string): value is SettingsCategory {
  return ['quality', 'strength', 'speed', 'support', 'multimaterial', 'other'].includes(value);
}
