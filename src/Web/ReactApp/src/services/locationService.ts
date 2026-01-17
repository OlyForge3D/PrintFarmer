import { apiClient } from '@/services/api';

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

/**
 * Location service - delegated to apiClient singleton
 * apiClient handles authentication, correlation IDs, and error handling automatically
 */
export const locationService = {
  /**
   * Get all active locations
   */
  async getAllLocations(): Promise<Location[]> {
    return apiClient.getAllLocations();
  },

  /**
   * Get a specific location by ID
   */
  async getLocationById(id: string): Promise<Location> {
    return apiClient.getLocationById(id);
  },

  /**
   * Create a new location
   */
  async createLocation(request: CreateLocationRequest): Promise<Location> {
    return apiClient.createLocation(request);
  },

  /**
   * Update an existing location
   */
  async updateLocation(id: string, request: UpdateLocationRequest): Promise<Location> {
    return apiClient.updateLocation(id, request);
  },

  /**
   * Delete a location (soft delete)
   */
  async deleteLocation(id: string): Promise<void> {
    return apiClient.deleteLocation(id);
  },
};
