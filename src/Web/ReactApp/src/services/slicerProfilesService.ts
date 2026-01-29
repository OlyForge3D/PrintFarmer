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

// === OrcaSlicer Worker Profile Types (System Profiles) ===
// These are returned from the OrcaSlicer worker API for incremental loading

/**
 * Machine profile from OrcaSlicer worker (system profile).
 * Contains printer-specific configuration like bed size, nozzle, etc.
 */
export interface OrcaMachineProfile {
  name: string;
  manufacturer: string;
  description?: string;
  nozzleDiameter?: number;
  printerModel?: string;
  instantiation?: boolean;
  inherits?: string;
  settings?: Record<string, unknown>;
}

/**
 * Filament profile from OrcaSlicer worker (system profile).
 * Contains material-specific settings like temperature, speed, etc.
 */
export interface OrcaFilamentProfile {
  name: string;
  material: string;
  manufacturer?: string;
  description?: string;
  nozzleTemperature: number;
  bedTemperature: number;
  printSpeed: number;
  compatiblePrinters: string[];
  instantiation?: boolean;
  inherits?: string;
  settings?: Record<string, unknown>;
}

/**
 * Process profile from OrcaSlicer worker (system profile).
 * Contains quality/speed settings like layer height, infill, supports, etc.
 */
export interface OrcaProcessProfile {
  name: string;
  quality: string;
  layerHeight: number;
  infillPercentage: number;
  printSpeed: number;
  supports: boolean;
  description?: string;
  compatiblePrinters: string[];
  instantiation?: boolean;
  inherits?: string;
  settings?: Record<string, unknown>;
}

/**
 * Request body for fetching profiles compatible with specific machines.
 */
export interface ForMachinesRequest {
  machineNames: string[];
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
  },

  // === Incremental Loading Methods (Phase 1) ===
  // These methods fetch profiles on-demand from the OrcaSlicer worker
  // instead of loading all profiles upfront

  /**
   * Get machine profiles for a specific printer model ID.
   * Uses the catalog's OrcaSlicer alias to find matching profiles.
   * @param modelId - The printer model GUID from the catalog
   * @returns Machine profiles for the specified model
   */
  async getMachineProfilesForModel(modelId: string): Promise<OrcaMachineProfile[]> {
    const res = await apiClient.get<OrcaMachineProfile[]>(`/slicer/profiles/machine/for-model/${modelId}`);
    return res.data;
  },

  /**
   * Get machine profiles by manufacturer and model name.
   * Direct query when you know the exact manufacturer/model strings.
   * @param manufacturer - Manufacturer name (e.g., "Prusa", "Elegoo")
   * @param model - Model name (e.g., "CORE One", "Neptune 4")
   * @returns Machine profiles matching the manufacturer/model
   */
  async getMachineProfilesByName(manufacturer: string, model: string): Promise<OrcaMachineProfile[]> {
    const res = await apiClient.get<OrcaMachineProfile[]>(
      `/slicer/profiles/machine/${encodeURIComponent(manufacturer)}/${encodeURIComponent(model)}`
    );
    return res.data;
  },

  /**
   * Get filament profiles compatible with specific machine profiles.
   * Uses OrcaSlicer's compatible_printers matching.
   * @param machineNames - Array of machine profile names (e.g., ["Prusa CORE One 0.4 nozzle"])
   * @returns Filament profiles compatible with the specified machines
   */
  async getFilamentProfilesForMachines(machineNames: string[]): Promise<OrcaFilamentProfile[]> {
    const res = await apiClient.post<OrcaFilamentProfile[]>(
      '/slicer/profiles/filament/for-machines',
      { machineNames } as ForMachinesRequest
    );
    return res.data;
  },

  /**
   * Get process profiles compatible with specific machine profiles.
   * Uses OrcaSlicer's compatible_printers matching.
   * @param machineNames - Array of machine profile names (e.g., ["Prusa CORE One 0.4 nozzle"])
   * @returns Process profiles compatible with the specified machines
   */
  async getProcessProfilesForMachines(machineNames: string[]): Promise<OrcaProcessProfile[]> {
    const res = await apiClient.post<OrcaProcessProfile[]>(
      '/slicer/profiles/process/for-machines',
      { machineNames } as ForMachinesRequest
    );
    return res.data;
  },

  /**
   * Get template filament profiles from OrcaFilamentLibrary.
   * These are universal profiles not tied to specific printers.
   * @returns Universal filament profiles
   */
  async getFilamentTemplates(): Promise<OrcaFilamentProfile[]> {
    const res = await apiClient.get<OrcaFilamentProfile[]>('/slicer/profiles/filament/templates');
    return res.data;
  }
};
