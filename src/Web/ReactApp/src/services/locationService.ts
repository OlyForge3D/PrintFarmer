import { apiClient } from '@/services/api';

export interface Location {
  id: string;
  name: string;
  description?: string;
  parentId?: string | null;
  path?: string;
  depth: number;
  sortOrder: number;
  printerCount: number;
  totalPrinterCount: number;
  createdAt: string;
  modifiedAt: string;
  isActive: boolean;
}

export interface LocationTreeNode {
  id: string;
  name: string;
  description?: string;
  parentId?: string | null;
  path?: string;
  depth: number;
  sortOrder: number;
  printerCount: number;
  totalPrinterCount: number;
  children: LocationTreeNode[];
}

export interface LocationBreadcrumbItem {
  id: string;
  name: string;
  depth: number;
}

export interface MoveLocationRequest {
  newParentId?: string | null;
  sortOrder?: number;
}

export interface CreateLocationRequest {
  name: string;
  description?: string;
  parentId?: string | null;
  sortOrder?: number;
}

export interface UpdateLocationRequest {
  name?: string;
  description?: string;
  sortOrder?: number;
}

/**
 * Location service - delegated to apiClient singleton
 * apiClient handles authentication, correlation IDs, and error handling automatically
 */
export const locationService = {
  async getAllLocations(): Promise<Location[]> {
    return apiClient.getAllLocations();
  },

  async getLocationById(id: string): Promise<Location> {
    return apiClient.getLocationById(id);
  },

  async getLocationTree(): Promise<LocationTreeNode[]> {
    return apiClient.getLocationTree();
  },

  async getLocationAncestors(id: string): Promise<LocationBreadcrumbItem[]> {
    return apiClient.getLocationAncestors(id);
  },

  async getLocationDescendants(id: string): Promise<Location[]> {
    return apiClient.getLocationDescendants(id);
  },

  async createLocation(request: CreateLocationRequest): Promise<Location> {
    return apiClient.createLocation(request);
  },

  async updateLocation(id: string, request: UpdateLocationRequest): Promise<Location> {
    return apiClient.updateLocation(id, request);
  },

  async moveLocation(id: string, request: MoveLocationRequest): Promise<Location> {
    return apiClient.moveLocation(id, request);
  },

  async deleteLocation(id: string): Promise<void> {
    return apiClient.deleteLocation(id);
  },
};
