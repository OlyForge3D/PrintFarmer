import { describe, it, expect, vi, beforeEach } from 'vitest';
import { locationService, Location, CreateLocationRequest, UpdateLocationRequest } from '../locationService';
import { apiClient } from '../api';

// Mock the api client
vi.mock('../api', () => ({
  apiClient: {
    getAllLocations: vi.fn(),
    getLocationById: vi.fn(),
    createLocation: vi.fn(),
    updateLocation: vi.fn(),
    deleteLocation: vi.fn(),
  }
}));

describe('locationService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('getAllLocations', () => {
    it('should get all locations', async () => {
      const mockLocations: Location[] = [
        {
          id: '1',
          name: 'Workshop',
          description: 'Main workshop',
          printerCount: 5,
          createdAt: '2024-01-01T00:00:00Z',
          modifiedAt: '2024-01-01T00:00:00Z',
          isActive: true
        },
        {
          id: '2',
          name: 'Lab',
          description: 'Testing lab',
          printerCount: 2,
          createdAt: '2024-01-02T00:00:00Z',
          modifiedAt: '2024-01-02T00:00:00Z',
          isActive: true
        }
      ];

      vi.mocked(apiClient.getAllLocations).mockResolvedValue(mockLocations as never);

      const result = await locationService.getAllLocations();

      expect(result).toEqual(mockLocations);
      expect(apiClient.getAllLocations).toHaveBeenCalled();
    });

    it('should handle empty locations list', async () => {
      vi.mocked(apiClient.getAllLocations).mockResolvedValue([] as never);

      const result = await locationService.getAllLocations();

      expect(result).toEqual([]);
    });
  });

  describe('getLocationById', () => {
    it('should get a location by ID', async () => {
      const mockLocation: Location = {
        id: '1',
        name: 'Workshop',
        description: 'Main workshop',
        printerCount: 5,
        createdAt: '2024-01-01T00:00:00Z',
        modifiedAt: '2024-01-01T00:00:00Z',
        isActive: true
      };

      vi.mocked(apiClient.getLocationById).mockResolvedValue(mockLocation as never);

      const result = await locationService.getLocationById('1');

      expect(result).toEqual(mockLocation);
      expect(apiClient.getLocationById).toHaveBeenCalledWith('1');
    });
  });

  describe('createLocation', () => {
    it('should create a new location', async () => {
      const request: CreateLocationRequest = {
        name: 'New Location',
        description: 'Test location'
      };

      const mockCreatedLocation: Location = {
        id: '3',
        name: 'New Location',
        description: 'Test location',
        printerCount: 0,
        createdAt: '2024-01-03T00:00:00Z',
        modifiedAt: '2024-01-03T00:00:00Z',
        isActive: true
      };

      vi.mocked(apiClient.createLocation).mockResolvedValue(mockCreatedLocation as never);

      const result = await locationService.createLocation(request);

      expect(result).toEqual(mockCreatedLocation);
      expect(apiClient.createLocation).toHaveBeenCalledWith(request);
    });

    it('should create a location without description', async () => {
      const request: CreateLocationRequest = {
        name: 'Simple Location'
      };

      const mockCreatedLocation: Location = {
        id: '4',
        name: 'Simple Location',
        printerCount: 0,
        createdAt: '2024-01-03T00:00:00Z',
        modifiedAt: '2024-01-03T00:00:00Z',
        isActive: true
      };

      vi.mocked(apiClient.createLocation).mockResolvedValue(mockCreatedLocation as never);

      const result = await locationService.createLocation(request);

      expect(result.name).toBe('Simple Location');
      expect(apiClient.createLocation).toHaveBeenCalledWith(request);
    });
  });

  describe('updateLocation', () => {
    it('should update a location', async () => {
      const request: UpdateLocationRequest = {
        name: 'Updated Workshop',
        description: 'Updated description'
      };

      const mockUpdatedLocation: Location = {
        id: '1',
        name: 'Updated Workshop',
        description: 'Updated description',
        printerCount: 5,
        createdAt: '2024-01-01T00:00:00Z',
        modifiedAt: '2024-01-04T00:00:00Z',
        isActive: true
      };

      vi.mocked(apiClient.updateLocation).mockResolvedValue(mockUpdatedLocation as never);

      const result = await locationService.updateLocation('1', request);

      expect(result).toEqual(mockUpdatedLocation);
      expect(apiClient.updateLocation).toHaveBeenCalledWith('1', request);
    });

    it('should update only name', async () => {
      const request: UpdateLocationRequest = {
        name: 'New Name'
      };

      const mockUpdatedLocation: Location = {
        id: '1',
        name: 'New Name',
        description: 'Original description',
        printerCount: 5,
        createdAt: '2024-01-01T00:00:00Z',
        modifiedAt: '2024-01-04T00:00:00Z',
        isActive: true
      };

      vi.mocked(apiClient.updateLocation).mockResolvedValue(mockUpdatedLocation as never);

      const result = await locationService.updateLocation('1', request);

      expect(result.name).toBe('New Name');
      expect(apiClient.updateLocation).toHaveBeenCalledWith('1', request);
    });
  });

  describe('deleteLocation', () => {
    it('should delete a location', async () => {
      vi.mocked(apiClient.deleteLocation).mockResolvedValue(undefined as never);

      await locationService.deleteLocation('1');

      expect(apiClient.deleteLocation).toHaveBeenCalledWith('1');
    });

    it('should handle deletion errors', async () => {
      const error = new Error('Cannot delete location with printers');
      vi.mocked(apiClient.deleteLocation).mockRejectedValue(error);

      await expect(locationService.deleteLocation('1')).rejects.toThrow('Cannot delete location with printers');
    });
  });
});
