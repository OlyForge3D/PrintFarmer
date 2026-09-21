import { describe, it, expect, vi, beforeEach } from 'vitest';
import { locationService } from '../locationService';
import type {
  CreateLocationRequest,
  Location,
  LocationBreadcrumbItem,
  LocationTreeNode,
  MoveLocationRequest,
  UpdateLocationRequest,
} from '../locationService';
import { apiClient } from '../api';

// Mock the api client
vi.mock('../api', () => ({
  apiClient: {
    getAllLocations: vi.fn(),
    getLocationById: vi.fn(),
    getLocationTree: vi.fn(),
    getLocationAncestors: vi.fn(),
    getLocationDescendants: vi.fn(),
    createLocation: vi.fn(),
    updateLocation: vi.fn(),
    moveLocation: vi.fn(),
    deleteLocation: vi.fn(),
  }
}));

const createLocationFixture = (overrides: Partial<Location> = {}): Location => ({
  id: '1',
  name: 'Workshop',
  description: 'Main workshop',
  depth: 0,
  sortOrder: 0,
  printerCount: 5,
  totalPrinterCount: 5,
  createdAt: '2024-01-01T00:00:00Z',
  modifiedAt: '2024-01-01T00:00:00Z',
  isActive: true,
  ...overrides,
});

describe('locationService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('getAllLocations', () => {
    it('should get all locations', async () => {
      const mockLocations: Location[] = [
        createLocationFixture(),
        createLocationFixture({
          id: '2',
          name: 'Lab',
          description: 'Testing lab',
          printerCount: 2,
          totalPrinterCount: 2,
          createdAt: '2024-01-02T00:00:00Z',
          modifiedAt: '2024-01-02T00:00:00Z',
        }),
      ];

      vi.mocked(apiClient.getAllLocations).mockResolvedValue(mockLocations);

      const result = await locationService.getAllLocations();

      expect(result).toEqual(mockLocations);
      expect(apiClient.getAllLocations).toHaveBeenCalled();
    });

    it('should handle empty locations list', async () => {
      vi.mocked(apiClient.getAllLocations).mockResolvedValue([]);

      const result = await locationService.getAllLocations();

      expect(result).toEqual([]);
    });
  });

  describe('getLocationById', () => {
    it('should get a location by ID', async () => {
      const mockLocation = createLocationFixture();

      vi.mocked(apiClient.getLocationById).mockResolvedValue(mockLocation);

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

      const mockCreatedLocation = createLocationFixture({
        id: '3',
        name: 'New Location',
        description: 'Test location',
        printerCount: 0,
        totalPrinterCount: 0,
        createdAt: '2024-01-03T00:00:00Z',
        modifiedAt: '2024-01-03T00:00:00Z',
      });

      vi.mocked(apiClient.createLocation).mockResolvedValue(mockCreatedLocation);

      const result = await locationService.createLocation(request);

      expect(result).toEqual(mockCreatedLocation);
      expect(apiClient.createLocation).toHaveBeenCalledWith(request);
    });

    it('should create a location without description', async () => {
      const request: CreateLocationRequest = {
        name: 'Simple Location'
      };

      const mockCreatedLocation = createLocationFixture({
        id: '4',
        name: 'Simple Location',
        description: undefined,
        printerCount: 0,
        totalPrinterCount: 0,
        createdAt: '2024-01-03T00:00:00Z',
        modifiedAt: '2024-01-03T00:00:00Z',
      });

      vi.mocked(apiClient.createLocation).mockResolvedValue(mockCreatedLocation);

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

      const mockUpdatedLocation = createLocationFixture({
        name: 'Updated Workshop',
        description: 'Updated description',
        modifiedAt: '2024-01-04T00:00:00Z',
      });

      vi.mocked(apiClient.updateLocation).mockResolvedValue(mockUpdatedLocation);

      const result = await locationService.updateLocation('1', request);

      expect(result).toEqual(mockUpdatedLocation);
      expect(apiClient.updateLocation).toHaveBeenCalledWith('1', request);
    });

    it('should update only name', async () => {
      const request: UpdateLocationRequest = {
        name: 'New Name'
      };

      const mockUpdatedLocation = createLocationFixture({
        name: 'New Name',
        description: 'Original description',
        modifiedAt: '2024-01-04T00:00:00Z',
      });

      vi.mocked(apiClient.updateLocation).mockResolvedValue(mockUpdatedLocation);

      const result = await locationService.updateLocation('1', request);

      expect(result.name).toBe('New Name');
      expect(apiClient.updateLocation).toHaveBeenCalledWith('1', request);
    });
  });

  describe('deleteLocation', () => {
    it('should delete a location', async () => {
      vi.mocked(apiClient.deleteLocation).mockResolvedValue(undefined);

      await locationService.deleteLocation('1');

      expect(apiClient.deleteLocation).toHaveBeenCalledWith('1');
    });

    it('should handle deletion errors', async () => {
      const error = new Error('Cannot delete location with printers');
      vi.mocked(apiClient.deleteLocation).mockRejectedValue(error);

      await expect(locationService.deleteLocation('1')).rejects.toThrow('Cannot delete location with printers');
    });
  });

  describe('getLocationTree', () => {
    it('should get the full location tree', async () => {
      const mockTree: LocationTreeNode[] = [
        {
          id: '1',
          name: 'Building A',
          description: 'Main building',
          parentId: null,
          path: '/Building A',
          depth: 0,
          sortOrder: 0,
          printerCount: 2,
          totalPrinterCount: 5,
          children: [
            {
              id: '2',
              name: 'Floor 1',
              description: '',
              parentId: '1',
              path: '/Building A/Floor 1',
              depth: 1,
              sortOrder: 0,
              printerCount: 3,
              totalPrinterCount: 3,
              children: [],
            },
          ],
        },
      ];

      vi.mocked(apiClient.getLocationTree).mockResolvedValue(mockTree);

      const result = await locationService.getLocationTree();

      expect(result).toEqual(mockTree);
      expect(apiClient.getLocationTree).toHaveBeenCalled();
    });

    it('should return empty array for no locations', async () => {
      vi.mocked(apiClient.getLocationTree).mockResolvedValue([]);

      const result = await locationService.getLocationTree();

      expect(result).toEqual([]);
      expect(apiClient.getLocationTree).toHaveBeenCalled();
    });

    it('should propagate errors from apiClient', async () => {
      vi.mocked(apiClient.getLocationTree).mockRejectedValue(new Error('Server error'));

      await expect(locationService.getLocationTree()).rejects.toThrow('Server error');
    });
  });

  describe('getLocationAncestors', () => {
    it('should get ancestors for a location', async () => {
      const mockAncestors: LocationBreadcrumbItem[] = [
        { id: '1', name: 'Building A', depth: 0 },
        { id: '2', name: 'Floor 1', depth: 1 },
        { id: '3', name: 'Room 101', depth: 2 },
      ];

      vi.mocked(apiClient.getLocationAncestors).mockResolvedValue(mockAncestors);

      const result = await locationService.getLocationAncestors('3');

      expect(result).toEqual(mockAncestors);
      expect(apiClient.getLocationAncestors).toHaveBeenCalledWith('3');
    });

    it('should return empty array for root location', async () => {
      vi.mocked(apiClient.getLocationAncestors).mockResolvedValue([]);

      const result = await locationService.getLocationAncestors('root-id');

      expect(result).toEqual([]);
      expect(apiClient.getLocationAncestors).toHaveBeenCalledWith('root-id');
    });

    it('should propagate errors from apiClient', async () => {
      vi.mocked(apiClient.getLocationAncestors).mockRejectedValue(new Error('Not found'));

      await expect(locationService.getLocationAncestors('missing-id')).rejects.toThrow('Not found');
    });
  });

  describe('moveLocation', () => {
    it('should move a location to a new parent', async () => {
      const request: MoveLocationRequest = {
        newParentId: '5',
      };

      const mockMoved: Location = {
        id: '3',
        name: 'Room 101',
        description: '',
        parentId: '5',
        path: '/Building B/Room 101',
        depth: 1,
        sortOrder: 2,
        printerCount: 1,
        totalPrinterCount: 1,
        createdAt: '2024-01-01T00:00:00Z',
        modifiedAt: '2024-01-05T00:00:00Z',
        isActive: true,
      };

      vi.mocked(apiClient.moveLocation).mockResolvedValue(mockMoved);

      const result = await locationService.moveLocation('3', request);

      expect(result).toEqual(mockMoved);
      expect(apiClient.moveLocation).toHaveBeenCalledWith('3', request);
    });

    it('should move a location to root (null parent)', async () => {
      const request: MoveLocationRequest = {
        newParentId: null,
      };

      const mockMoved: Location = {
        id: '3',
        name: 'Room 101',
        description: '',
        parentId: null,
        path: '/Room 101',
        depth: 0,
        sortOrder: 0,
        printerCount: 1,
        totalPrinterCount: 1,
        createdAt: '2024-01-01T00:00:00Z',
        modifiedAt: '2024-01-05T00:00:00Z',
        isActive: true,
      };

      vi.mocked(apiClient.moveLocation).mockResolvedValue(mockMoved);

      const result = await locationService.moveLocation('3', request);

      expect(result.parentId).toBeNull();
      expect(result.depth).toBe(0);
      expect(apiClient.moveLocation).toHaveBeenCalledWith('3', request);
    });

    it('should propagate circular reference errors', async () => {
      const request: MoveLocationRequest = {
        newParentId: '3',
      };

      vi.mocked(apiClient.moveLocation).mockRejectedValue(
        new Error('Circular reference detected'),
      );

      await expect(locationService.moveLocation('1', request)).rejects.toThrow(
        'Circular reference detected',
      );
    });
  });
});
