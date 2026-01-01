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
    const response = await this.api.get<Printer[]>('/printers');
    return Array.isArray(response.data) ? response.data : [];
  }

  /**
   * Assign a printer to a location
   */
  async assignPrinterToLocation(printerId: string, locationId: string): Promise<void> {
    await this.api.post(`/printers/${printerId}/location`, { locationId });
  }

  /**
   * Remove a printer from its location (unassign)
   */
  async unassignPrinterFromLocation(printerId: string): Promise<void> {
    await this.api.delete(`/printers/${printerId}/location`);
  }
}

export const printerLocationService = new PrinterLocationService();
