/**
 * OrcaSlicer settings type definitions
 * Maps to OrcaSlicer's process profile settings
 */

/** View modes for settings panel complexity */
export type SettingsViewMode = 'basic' | 'simple' | 'advanced';

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

/** Basic settings - shown in Basic mode */
export interface BasicSlicerSettings {
  infillDensity: number;           // 0-100%
  infillPattern: InfillPattern;
  wallCount: number;               // 1-10 perimeters
  bedAdhesion: BedAdhesionType;
  enableSupports: boolean;
}

/** Simple settings - extends Basic with layer/line width controls */
export interface SimpleSlicerSettings extends BasicSlicerSettings {
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
  topSurfaceSpeed: number;
  travelSpeed: number;
  firstLayerSpeed: number;
  
  // Temperature
  nozzleTemp: number;
  bedTemp: number;
  firstLayerNozzleTemp: number;
  firstLayerBedTemp: number;
  
  // Support settings (when enabled)
  supportType: SupportType;
  supportDensity: number;
  supportAngle: number;
  supportTopZDistance: number;
  supportBottomZDistance: number;
  supportInterfaceLayers: number;
}

/** Default values for basic settings */
export const DEFAULT_BASIC_SETTINGS: BasicSlicerSettings = {
  infillDensity: 20,
  infillPattern: 'crosshatch',
  wallCount: 3,
  bedAdhesion: 'skirt',
  enableSupports: false,
};

/** Default values for simple settings */
export const DEFAULT_SIMPLE_SETTINGS: SimpleSlicerSettings = {
  ...DEFAULT_BASIC_SETTINGS,
  layerHeight: 0.2,
  firstLayerHeight: 0.2,
  lineWidthDefault: 0.45,
  lineWidthFirstLayer: 0.5,
  topLayers: 4,
  bottomLayers: 3,
};

/** Infill pattern display names and descriptions */
export const INFILL_PATTERN_INFO: Record<InfillPattern, { label: string; description: string }> = {
  grid: { label: 'Grid', description: 'Simple perpendicular lines' },
  triangles: { label: 'Triangles', description: 'Triangular pattern for moderate strength' },
  stars: { label: 'Stars', description: 'Star-shaped pattern' },
  cubic: { label: 'Cubic', description: '3D cubic pattern for strength' },
  line: { label: 'Lines', description: 'Simple parallel lines, fastest' },
  concentric: { label: 'Concentric', description: 'Follows the outline shape' },
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
