import { apiClient } from '@/services/api';

export interface Printer {
  id: string;
  name: string;
  serverUrl: string;
  backend: number;
  locationId?: string;
}

/**
 * Printer location service - delegated to apiClient singleton
 * apiClient handles authentication, correlation IDs, and error handling automatically
 */
export const printerLocationService = {
  /**
   * Get all printers (used for drag and drop assignment)
   */
  async getAllPrinters(): Promise<Printer[]> {
    const raw = await apiClient.getAllPrinterLocations();
    const typedRaw = Array.isArray(raw) ? raw : [];
    // Normalize server-side DTO (Location object) to frontend shape (locationId)
    return typedRaw.map((r: Record<string, unknown>) => ({
      id: r.id as string,
      name: r.name as string,
      backend: r.backend as number,
      // prefer backendUrl, then frontendUrl, then originalServerUrl
      serverUrl: (r.backendUrl || r.frontendUrl || r.originalServerUrl || '') as string,
      locationId: ((r.location as Record<string, unknown> | undefined)?.id as string | undefined),
    }));
  },

  /**
   * Assign a printer to a location
   */
  async assignPrinterToLocation(printerId: string, locationId: string): Promise<Printer> {
    const r = (await apiClient.assignPrinterToLocation(printerId, locationId)) as unknown as Record<string, unknown>;
    return {
      id: (r.id as string) || '',
      name: (r.name as string) || '',
      backend: (r.backend as number) || 0,
      serverUrl: ((r.backendUrl as string) || (r.frontendUrl as string) || (r.originalServerUrl as string) || ''),
      locationId: ((r.location as Record<string, unknown> | undefined)?.id as string | undefined),
    };
  },

  /**
   * Remove a printer from its location (unassign)
   */
  async unassignPrinterFromLocation(printerId: string): Promise<Printer> {
    const r = (await apiClient.removePrinterFromLocation(printerId)) as unknown as Record<string, unknown>;
    return {
      id: (r.id as string) || '',
      name: (r.name as string) || '',
      backend: (r.backend as number) || 0,
      serverUrl: ((r.backendUrl as string) || (r.frontendUrl as string) || (r.originalServerUrl as string) || ''),
      locationId: ((r.location as Record<string, unknown> | undefined)?.id as string | undefined),
    };
  },
};
