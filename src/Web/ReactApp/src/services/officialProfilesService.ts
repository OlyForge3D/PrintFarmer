// Service for importing official slicer profiles for registered printers
import { apiClient } from "./api";
import { SlicerProfileListItem } from "./slicerProfilesService";
import { ImportedProfileNamesDto } from "@/features/tasks/components/profile-wizard/types";

export interface BulkProfileImportRequest {
  profileIds: string[];
  makePublic?: boolean;
}

export interface BulkImportFromWorkerRequest {
  profiles: Array<{
    layerHeight: number;
    infillPercentage: number;
    printSpeed: number;
    nozzleTemperature: number;
    bedTemperature: number;
    supports: boolean;
    material: string;
    quality: string;
  }>;
  makePublic?: boolean;
}

export interface BulkProfileImportResult {
  printerId: string;
  printerName: string;
  totalRequested: number;
  totalFound: number;
  imported: number;
  duplicated: number;
}

export interface BulkImportFromWorkerResult {
  printerId: string;
  printerName: string;
  imported: number;
  duplicated: number;
}

/**
 * Request for selective profile import from the Profile Import Wizard.
 */
export interface SelectiveProfileImportRequest {
  manufacturerName: string;
  selectedMachineProfiles: string[];
  selectedProcessProfiles: string[];
  selectedFilamentProfiles: string[];
}

/**
 * Result of selective profile import operation.
 */
export interface SelectiveProfileImportResult {
  printerModelId: string;
  machineProfilesImported: number;
  processProfilesImported: number;
  filamentProfilesImported: number;
  totalImported: number;
  skipped: number;
  error?: string;
}

export const officialProfilesService = {
  /**
   * Get available OrcaSlicer profiles from the worker service
   * These are the actual profiles from OrcaSlicer's local installation
   */
  async getAvailableProfilesFromWorker(): Promise<SlicerProfileListItem[]> {
    const response = await apiClient.get<SlicerProfileListItem[]>('/slicer/profiles/available-from-worker');
    return response.data;
  },

  /**
   * Get system profiles available for a specific registered printer
   * These are profiles previously imported into the database
   */
  async getAvailableProfilesForPrinter(
    printerId: string
  ): Promise<SlicerProfileListItem[]> {
    const response = await apiClient.get<SlicerProfileListItem[]>(`/slicer/profiles/available-for-printer/${printerId}`);
    return response.data;
  },

  /**
   * Bulk import system profiles for a registered printer
   */
  async bulkImportProfilesForPrinter(
    printerId: string,
    request: BulkProfileImportRequest
  ): Promise<BulkProfileImportResult> {
    const response = await apiClient.post<BulkProfileImportResult>(`/slicer/profiles/bulk-import-for-printer/${printerId}`, request);
    return response.data;
  },

  /**
   * Bulk import profiles directly from the OrcaSlicer worker (primary workflow).
   * Fetches profiles from worker, user selects which ones, then import them here.
   */
  async bulkImportFromWorker(
    printerId: string,
    request: BulkImportFromWorkerRequest
  ): Promise<BulkImportFromWorkerResult> {
    const response = await apiClient.post<BulkImportFromWorkerResult>(`/slicer/profiles/bulk-import-from-worker/${printerId}`, request);
    return response.data;
  },

  /**
   * Force reseed system OrcaSlicer profiles from the worker.
   * Clears existing system profiles and fetches fresh ones from OrcaSlicer worker.
   */
  async forceReseedSystemProfilesFromWorker(): Promise<{ imported: number; deleted?: number; message?: string; orcaslicerVersion?: string }> {
    const response = await apiClient.post<{ imported: number; deleted?: number; message?: string; orcaslicerVersion?: string }>('/slicer/profiles/system/orca/force-reseed-from-worker');
    return response.data;
  },

  /**
   * Import selected profiles from OrcaSlicer worker for a specific printer model.
   * Used by the Profile Import Wizard after user selects which profiles to import.
   * 
   * @param modelId - The printer model ID from the catalog
   * @param request - The selective import request containing selected profile names
   * @returns Result with counts of imported/skipped profiles
   */
  async importSelectedProfilesForModel(
    modelId: string,
    request: SelectiveProfileImportRequest
  ): Promise<SelectiveProfileImportResult> {
    const response = await apiClient.post<SelectiveProfileImportResult>(`/slicer/profiles/import-selected-for-model/${modelId}`, request);
    return response.data;
  },

  /**
   * Get names of profiles already imported for a printer model.
   * Used by the Profile Import Wizard to pre-check already-imported profiles.
   * 
   * @param modelId - The printer model ID from the catalog
   * @returns Lists of imported machine, process, and filament profile names
   */
  async getImportedProfileNamesForModel(
    modelId: string
  ): Promise<ImportedProfileNamesDto> {
    const response = await apiClient.get<ImportedProfileNamesDto>(`/slicer/profiles/imported-names/${modelId}`);
    return response.data;
  },
};
