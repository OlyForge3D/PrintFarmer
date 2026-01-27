// Service for interacting with slicer profile API endpoints (Phase 6)
// Provides list, import, export, and set-default operations.

import { apiClient } from './api';

// Base interface for all profile types
export interface IProfileListItem {
  id: string;
  name: string;
  slicerType: string;
  isDefault: boolean;
  isSystem: boolean;
  isPublic: boolean;
  hash: string;
  profileType: 'process' | 'filament' | 'machine';
}

// Process profile list item
export interface ProcessProfileListItem extends IProfileListItem {
  profileType: 'process';
  quality: string;
  layerHeight: number;
  infillPercentage: number;
  nozzleDiameter?: number;
  material?: string;
}

// Filament profile list item
export interface FilamentProfileListItem extends IProfileListItem {
  profileType: 'filament';
  material: string;
  nozzleTemperature?: number;
  bedTemperature?: number;
  printSpeed: number;
}

// Machine profile list item
export interface MachineProfileListItem extends IProfileListItem {
  profileType: 'machine';
  manufacturer: string;
  nozzleDiameter?: number;
}

// Union type for all profile types
export type SlicerProfileListItem = ProcessProfileListItem | FilamentProfileListItem | MachineProfileListItem;

// Response structure with profiles organized by type
export interface ExtendedProfilesResponse {
  processProfiles: ProcessProfileListItem[];
  filamentProfiles: FilamentProfileListItem[];
  machineProfiles: MachineProfileListItem[];
}

export interface ImportSlicerProfileRequest {
  rawJson: string;
  name?: string;
  description?: string;
  slicerType: string; // PrusaSlicer, OrcaSlicer, etc.
  allowSystemOverride?: boolean;
  setDefault?: boolean;
  isPublic?: boolean;
}

export interface SlicerProfileExtended {
  id: string;
  name: string;
  description?: string | null;
  slicerType: string;
  layerHeight: number;
  infillPercentage: number;
  printSpeed: number;
  nozzleTemperature: number;
  bedTemperature: number;
  enableSupports: boolean;
  material: string;
  quality: string;
  isDefault: boolean;
  isPublic: boolean;
  isSystem: boolean;
  hash: string;
  createdAt: string;
  updatedAt: string;
  metadata: Record<string, unknown>;
}

export interface SlicerProfileExportDto {
  id: string;
  name: string;
  slicerType: string;
  hash: string;
  rawJson: string;
  metadata: Record<string, unknown>;
}

// Hierarchy structures for organized profile display
export interface PrinterModelProfilesDto {
  name: string;
  modelId: string;
  machineProfiles: MachineProfileListItem[];
  filamentProfiles: FilamentProfileListItem[];
  processProfiles: ProcessProfileListItem[];
}

export interface ManufacturerProfilesDto {
  name: string;
  models: Record<string, PrinterModelProfilesDto>;
}

export interface HierarchicalProfilesResponse {
  byHierarchy: Record<string, ManufacturerProfilesDto>;
  machineProfiles: Record<string, MachineProfileListItem[]>;
  filamentProfiles: Record<string, FilamentProfileListItem[]>;
  processProfiles: Record<string, ProcessProfileListItem[]>;
}

export interface BulkDeleteResultDto {
  machineProfilesDeleted: number;
  processProfilesDeleted: number;
  filamentProfilesDeleted: number;
  totalDeleted: number;
  notFound: number;
}

export const slicerProfilesService = {
  async listExtended(): Promise<ExtendedProfilesResponse> {
    const res = await apiClient.get<ExtendedProfilesResponse>('/slicer/profiles/extended');
    return res.data;
  },
  async listHierarchical(machineProfileId?: string): Promise<HierarchicalProfilesResponse> {
    // Use /hierarchy endpoint which returns hierarchical profile data with byHierarchy
    // Optional machineProfileId filter to support CompatiblePrinters filtering
    const url = machineProfileId 
      ? `/slicer/profiles/hierarchy?machineProfileId=${machineProfileId}`
      : '/slicer/profiles/hierarchy';
      
    const res = await apiClient.get<HierarchicalProfilesResponse>(url);
    return res.data;
  },
  async importProfile(req: ImportSlicerProfileRequest): Promise<SlicerProfileExtended> {
    const res = await apiClient.post<SlicerProfileExtended>('/slicer/profiles/import', req);
    return res.data;
  },
  async exportProfile(id: string): Promise<SlicerProfileExportDto> {
    const res = await apiClient.get<SlicerProfileExportDto>(`/slicer/profiles/${id}/export`);
    return res.data;
  },
  async setDefault(id: string): Promise<void> {
    await apiClient.post<void>(`/slicer/profiles/${id}/set-default`);
  },
  async bulkDelete(profileIds: string[]): Promise<BulkDeleteResultDto> {
    const res = await apiClient.post<BulkDeleteResultDto>('/slicer/profiles/bulk-delete', profileIds);
    return res.data;
  }
};
