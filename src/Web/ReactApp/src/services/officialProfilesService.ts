// Service for importing official slicer profiles for registered printers
import { getApiBaseUrl } from "@/utils/apiUrlHelpers";
import { SlicerProfileListItem } from "./slicerProfilesService";

export interface BulkProfileImportRequest {
  profileIds: string[];
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
};

export default officialProfilesService;
