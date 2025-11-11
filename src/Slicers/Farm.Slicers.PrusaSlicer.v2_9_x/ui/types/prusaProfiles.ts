// PrusaSlicer 2.9.x types - stub for future implementation

export interface PrusaPrinterPreset {
  name: string;
  inherentFrom?: string;
  manufacturer?: string;
  bedWidth: number;
  bedDepth: number;
  maxZHeight: number;
  nozzleDiameter: number;
  maxBedTemperature: number;
  maxHotendTemperature: number;
  hasHeatedBed: boolean;
  rawParameters: Record<string, unknown>;
}

export interface PrusaMaterialPreset {
  name: string;
  inherentFrom?: string;
  materialType?: string;
  nozzleTemperature?: number;
  bedTemperature?: number;
  manufacturer?: string;
  density?: number;
  cost?: number;
  color?: string;
  rawParameters: Record<string, unknown>;
}

export interface PrusaBundlePreview {
  printers: PrusaPrinterPreset[];
  materials: PrusaMaterialPreset[];
  processes: Record<string, unknown>[];
  metadata: Record<string, string>;
}
