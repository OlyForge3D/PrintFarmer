import { apiClient } from '@/services/api';
import type {
  Location,
  LocationTreeNode,
  LocationBreadcrumbItem,
  CreateLocationRequest,
  UpdateLocationRequest,
  MoveLocationRequest,
} from '@/types/api';

export type { Location, LocationTreeNode, LocationBreadcrumbItem, CreateLocationRequest, UpdateLocationRequest, MoveLocationRequest };

/**
 * Find a location node in a tree by ID
 */
export function findNode(nodes: LocationTreeNode[], id: string): LocationTreeNode | undefined {
  for (const node of nodes) {
    if (node.id === id) return node;
    const found = findNode(node.children, id);
    if (found) return found;
  }
  return undefined;
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
