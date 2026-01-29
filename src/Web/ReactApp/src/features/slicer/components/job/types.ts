// Types for slice job components

import type { MaterialType, MaterialPreset } from '@/types/slicer';
import type { 
  HierarchicalProfilesResponse,
  MachineProfileListItem,
  FilamentProfileListItem,
  ProcessProfileListItem 
} from '@/services/slicerProfilesService';

// Re-export imported types for convenience
export type { MaterialType, MaterialPreset } from '@/types/slicer';

// Slicer engine option for dropdown
export interface SlicerEngineOption {
  label: string;
  value: number;
}

// Slicer info with name and version
export interface SlicerInfo {
  name: string;
  version: string;
  engine: number;
}

// Printer model option from hierarchy
export interface PrinterModelOption {
  key: string;
  name: string;
  modelId: string;
}

// Printer basic info
export interface PrinterBasicInfo {
  id: string;
  name: string;
  model?: string;
  modelId?: string;
  manufacturerName?: string;
  modelName?: string;
}

// Model3D basic info for picker
export interface Model3DBasic {
  id: string;
  originalFileName: string;
}

// Printer detailed info with bed dimensions
export interface PrinterDetailedInfo extends PrinterBasicInfo {
  modelMaxX?: number;
  modelMaxY?: number;
  modelMaxZ?: number;
}

// Bed dimensions for 3D viewer
export interface BedDimensions {
  width: number;
  depth: number;
  height: number;
}

// Material presets constant
export const MATERIAL_PRESETS: Record<MaterialType, MaterialPreset> = {
  'PLA': { name: 'PLA', nozzleTemp: 210, bedTemp: 60 },
  'PETG': { name: 'PETG', nozzleTemp: 240, bedTemp: 80 },
  'ABS': { name: 'ABS', nozzleTemp: 245, bedTemp: 100 },
  'TPU': { name: 'TPU', nozzleTemp: 225, bedTemp: 60 },
  'Nylon': { name: 'Nylon', nozzleTemp: 260, bedTemp: 80 },
  'Carbon': { name: 'Carbon', nozzleTemp: 250, bedTemp: 90 },
  'Other': { name: 'Other', nozzleTemp: 220, bedTemp: 60 }
};

// Props for cascading profile selection
export interface CascadingProfileState {
  selectedManufacturer: string;
  selectedPrinterModel: string;
  selectedMachineProfileId: string;
  selectedFilamentProfileId: string;
  selectedProcessPresetId: string;
}

// Re-export profile types for convenience
export type {
  HierarchicalProfilesResponse,
  MachineProfileListItem,
  FilamentProfileListItem,
  ProcessProfileListItem
};
