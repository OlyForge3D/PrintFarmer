// OrcaSlicer bundle import/export types

export interface OrcaPrinterPreset {
  name: string;
  inherentFrom?: string;
  printerModel?: string;
  manufacturer?: string;
  bedWidth: number;
  bedDepth: number;
  maxZHeight: number;
  nozzleDiameter: number;
  maxBedTemperature: number;
  maxHotendTemperature: number;
  hasHeatedBed: boolean;
  printerTechnology?: string;
  rawParameters: Record<string, unknown>;
}

export interface OrcaFilamentPreset {
  name: string;
  inherentFrom?: string;
  filamentType?: string;
  nozzleTemperature?: number;
  bedTemperature?: number;
  manufacturer?: string;
  density?: number;
  cost?: number;
  color?: string;
  rawParameters: Record<string, unknown>;
}

export interface OrcaProcessPreset {
  name: string;
  inherentFrom?: string;
  layerHeight: number;
  firstLayerHeight: number;
  infillPercentage: number;
  infillPattern?: string;
  printSpeed?: number;
  infillSpeed?: number;
  outerWallSpeed?: number;
  innerWallSpeed?: number;
  enableSupports: boolean;
  supportType?: string;
  supportAngle?: number;
  perimeters: number;
  topLayers: number;
  bottomLayers: number;
  quality?: string;
  rawParameters: Record<string, unknown>;
}

export interface OrcaBundlePreview {
  printers: OrcaPrinterPreset[];
  filaments: OrcaFilamentPreset[];
  processes: OrcaProcessPreset[];
  metadata: Record<string, string>;
}

export interface ImportOrcaBundleRequest {
  bundleJson: string;
  allowSystemOverride?: boolean;
  setDefaults?: boolean;
  importPrinters?: boolean;
  importFilaments?: boolean;
  importProcesses?: boolean;
  selectedPrinters?: string[];
  selectedFilaments?: string[];
  selectedProcesses?: string[];
}

export interface ImportOrcaBundleResult {
  printersImported: number;
  filamentsImported: number;
  processesImported: number;
  warnings: string[];
  errors: string[];
  success: boolean;
}

export interface ExportOrcaBundleRequest {
  printerModelIds?: string[];
  filamentTypeIds?: string[];
  includeProcessProfiles?: boolean;
  includeMetadata?: boolean;
}

export interface PrinterPresetMatch {
  preset: OrcaPrinterPreset;
  matchedPrinterModelId?: string;
  matchedPrinterModelName?: string;
  matchedManufacturerName?: string;
  confidenceScore: number;
  matchReasons: string[];
}

export interface FilamentPresetMatch {
  preset: OrcaFilamentPreset;
  matchedMaterialType?: string;
  confidenceScore: number;
  matchReasons: string[];
}

export interface ProcessPresetMatch {
  preset: OrcaProcessPreset;
  derivedQuality: string;
  confidenceScore: number;
  matchReasons: string[];
}

export interface OrcaBundleMappingResult {
  printerMatches: PrinterPresetMatch[];
  filamentMatches: FilamentPresetMatch[];
  processMatches: ProcessPresetMatch[];
  totalPresets: number;
  highConfidenceMatches: number;
  mediumConfidenceMatches: number;
  lowConfidenceMatches: number;
}
