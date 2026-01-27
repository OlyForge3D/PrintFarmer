// Service for importing official slicer profiles for registered printers
import { getApiBaseUrl } from "@/common/utils/apiUrlHelpers";
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

const base = `${getApiBaseUrl()}/slicer/profiles`;

async function handle<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const text = await res.text();
    throw new Error(text || `Request failed (${res.status})`);
  }
  return res.json() as Promise<T>;
}

function getAuthHeaders(): HeadersInit {
  const headers: HeadersInit = {
    "Content-Type": "application/json",
  };
  const token = localStorage.getItem("auth-token");
  if (token) {
    headers["Authorization"] = `Bearer ${token}`;
  }
  return headers;
}

export const officialProfilesService = {
  /**
   * Get available OrcaSlicer profiles from the worker service
   * These are the actual profiles from OrcaSlicer's local installation
   */
  async getAvailableProfilesFromWorker(): Promise<SlicerProfileListItem[]> {
    const res = await fetch(`${base}/available-from-worker`, {
      headers: getAuthHeaders(),
    });
    return handle<SlicerProfileListItem[]>(res);
  },

  /**
   * Get system profiles available for a specific registered printer
   * These are profiles previously imported into the database
   */
  async getAvailableProfilesForPrinter(
    printerId: string
  ): Promise<SlicerProfileListItem[]> {
    const res = await fetch(`${base}/available-for-printer/${printerId}`, {
      headers: getAuthHeaders(),
    });
    return handle<SlicerProfileListItem[]>(res);
  },

  /**
   * Bulk import system profiles for a registered printer
   */
  async bulkImportProfilesForPrinter(
    printerId: string,
    request: BulkProfileImportRequest
  ): Promise<BulkProfileImportResult> {
    const res = await fetch(`${base}/bulk-import-for-printer/${printerId}`, {
      method: "POST",
      headers: getAuthHeaders(),
      body: JSON.stringify(request),
    });
    return handle<BulkProfileImportResult>(res);
  },

  /**
   * Bulk import profiles directly from the OrcaSlicer worker (primary workflow).
   * Fetches profiles from worker, user selects which ones, then import them here.
   */
  async bulkImportFromWorker(
    printerId: string,
    request: BulkImportFromWorkerRequest
  ): Promise<BulkImportFromWorkerResult> {
    const res = await fetch(`${base}/bulk-import-from-worker/${printerId}`, {
      method: "POST",
      headers: getAuthHeaders(),
      body: JSON.stringify(request),
    });
    return handle<BulkImportFromWorkerResult>(res);
  },

  /**
   * Force reseed system OrcaSlicer profiles from the worker.
   * Clears existing system profiles and fetches fresh ones from OrcaSlicer worker.
   */
  async forceReseedSystemProfilesFromWorker(): Promise<{ imported: number; deleted?: number; message?: string; orcaslicerVersion?: string }> {
    const res = await fetch(`${base}/system/orca/force-reseed-from-worker`, {
      method: "POST",
      headers: getAuthHeaders(),
    });
    return handle<{ imported: number; deleted?: number; message?: string; orcaslicerVersion?: string }>(res);
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
    const res = await fetch(`${base}/import-selected-for-model/${modelId}`, {
      method: "POST",
      headers: getAuthHeaders(),
      body: JSON.stringify(request),
    });
    return handle<SelectiveProfileImportResult>(res);
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
    const res = await fetch(`${base}/imported-names/${modelId}`, {
      headers: getAuthHeaders(),
    });
    return handle<ImportedProfileNamesDto>(res);
  },
};

export default officialProfilesService;
