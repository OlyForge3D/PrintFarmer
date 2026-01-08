import axios, { AxiosInstance } from 'axios';

export interface Printer {
  id: string;
  name: string;
  serverUrl: string;
  backend: number;
  locationId?: string;
}

class PrinterLocationService {
  private api: AxiosInstance;

  constructor() {
    // Use the same API base URL pattern as the main ApiClient
    const rawBase = import.meta.env.VITE_API_BASE_URL as string | undefined;
    const apiBaseUrl = (() => {
      if (!rawBase || rawBase.trim() === '') return '/api';
      const trimmed = rawBase.replace(/\/$/, ''); // drop trailing slash
      if (/\/(api)(\/|$)/.test(trimmed)) return trimmed;
      return `${trimmed}/api`;
    })();

    this.api = axios.create({
      baseURL: apiBaseUrl,
      headers: {
        'Content-Type': 'application/json',
      },
    });

    // Add auth token if available
    this.api.interceptors.request.use((config) => {
      const token = localStorage.getItem('auth-token');
      if (token) {
        config.headers.Authorization = `Bearer ${token}`;
      }
      return config;
    });
  }

  /**
   * Get all printers (used for drag and drop assignment)
   */
  async getAllPrinters(): Promise<Printer[]> {
    interface ServerPrinterDto {
      id: string;
      name: string;
      backend: number;
      backendUrl?: string;
      frontendUrl?: string;
      originalServerUrl?: string;
      location?: { id?: string } | null;
    }

    const response = await this.api.get<ServerPrinterDto[]>('/printers');
    const raw = Array.isArray(response.data) ? response.data : [];
    // Normalize server-side DTO (Location object) to frontend shape (locationId)
    return raw.map((r) => ({
      id: r.id,
      name: r.name,
      backend: r.backend,
      // prefer backendUrl, then frontendUrl, then originalServerUrl
      serverUrl: r.backendUrl || r.frontendUrl || r.originalServerUrl || '',
      locationId: r.location ? r.location.id : undefined,
    }));
  }

  /**
   * Assign a printer to a location
   */
  async assignPrinterToLocation(printerId: string, locationId: string): Promise<Printer> {
    const resp = await this.api.post<ServerPrinterDto>(`/printers/${printerId}/location`, { locationId });
    const r = resp.data;
    return {
      id: r.id,
      name: r.name,
      backend: r.backend,
      serverUrl: r.backendUrl || r.frontendUrl || r.originalServerUrl || '',
      locationId: r.location ? r.location.id : undefined,
    };
  }

  /**
   * Remove a printer from its location (unassign)
   */
  async unassignPrinterFromLocation(printerId: string): Promise<Printer> {
    const resp = await this.api.delete<ServerPrinterDto>(`/printers/${printerId}/location`);
    const r = resp.data;
    return {
      id: r.id,
      name: r.name,
      backend: r.backend,
      serverUrl: r.backendUrl || r.frontendUrl || r.originalServerUrl || '',
      locationId: r.location ? r.location.id : undefined,
    };
  }
}

export const printerLocationService = new PrinterLocationService();
