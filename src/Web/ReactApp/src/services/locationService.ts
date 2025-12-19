import axios, { AxiosInstance } from 'axios';

export interface Location {
  id: string;
  name: string;
  description?: string;
  printerCount: number;
  createdAt: string;
  modifiedAt: string;
  isActive: boolean;
}

export interface CreateLocationRequest {
  name: string;
  description?: string;
}

export interface UpdateLocationRequest {
  name?: string;
  description?: string;
}

class LocationService {
  private api: AxiosInstance;

  constructor() {
    // Use the same API base URL pattern as the main ApiClient
    const rawBase = import.meta.env.VITE_API_BASE_URL as string | undefined;
    const apiBaseUrl = (() => {
      if (!rawBase || rawBase.trim() === '') return '/api';
      const trimmed = rawBase.replace(/\/$/, ''); // drop trailing slash
      // If it already ends with '/api' or contains '/api/' path segment, keep as-is
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
   * Get all active locations
   */
  async getAllLocations(): Promise<Location[]> {
    const response = await this.api.get<Location[]>('/api/locations');
    return response.data;
  }

  /**
   * Get a specific location by ID
   */
  async getLocationById(id: string): Promise<Location> {
    const response = await this.api.get<Location>(`/api/locations/${id}`);
    return response.data;
  }

  /**
   * Create a new location
   */
  async createLocation(request: CreateLocationRequest): Promise<Location> {
    const response = await this.api.post<Location>('/api/locations', request);
    return response.data;
  }

  /**
   * Update an existing location
   */
  async updateLocation(id: string, request: UpdateLocationRequest): Promise<Location> {
    const response = await this.api.put<Location>(`/api/locations/${id}`, request);
    return response.data;
  }

  /**
   * Delete a location (soft delete)
   */
  async deleteLocation(id: string): Promise<void> {
    await this.api.delete(`/api/locations/${id}`);
  }
}

export const locationService = new LocationService();
