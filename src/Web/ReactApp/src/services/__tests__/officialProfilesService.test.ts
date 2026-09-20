import { AxiosHeaders, type AxiosResponse } from 'axios';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { ImportedProfileNamesDto } from '@/features/tasks/components/profile-wizard/types';
import type { OrcaProcessProfile, SlicerProfileListItem } from '../slicerProfilesService';
import {
  officialProfilesService,
  type BulkProfileImportResult,
  type SelectiveProfileImportResult,
} from '../officialProfilesService';
import { apiClient } from '../api';

vi.mock('../api', () => ({
  apiClient: {
    get: vi.fn(),
    post: vi.fn(),
  },
}));

function createApiResponse<T>(data: T): AxiosResponse<T> {
  return {
    data,
    status: 200,
    statusText: 'OK',
    headers: {},
    config: { headers: new AxiosHeaders() },
  };
}

describe('officialProfilesService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('importSelectedProfilesForModel', () => {
    const modelId = 'model-abc-123';
    const request = {
      manufacturerName: 'Prusa',
      selectedMachineProfiles: ['Prusa MK4 0.4mm'],
      selectedProcessProfiles: ['0.20mm Standard @MK4'],
      selectedFilamentProfiles: ['Generic PLA @MK4'],
    };

    it('should post selective import request and return result', async () => {
      const mockResult: SelectiveProfileImportResult = {
        printerModelId: modelId,
        machineProfilesImported: 1,
        processProfilesImported: 1,
        filamentProfilesImported: 1,
        totalImported: 3,
        skipped: 0,
      };

      vi.mocked(apiClient.post).mockResolvedValue(createApiResponse(mockResult));

      const result = await officialProfilesService.importSelectedProfilesForModel(modelId, request);

      expect(apiClient.post).toHaveBeenCalledWith(
        `/slicer/profiles/import-selected-for-model/${modelId}`,
        request,
      );
      expect(result).toEqual(mockResult);
      expect(result.totalImported).toBe(3);
    });

    it('should propagate errors from the API', async () => {
      vi.mocked(apiClient.post).mockRejectedValue(new Error('Worker unavailable'));

      await expect(
        officialProfilesService.importSelectedProfilesForModel(modelId, request),
      ).rejects.toThrow('Worker unavailable');
    });

    it('should handle import with empty profile selections', async () => {
      const emptyRequest = {
        manufacturerName: 'Prusa',
        selectedMachineProfiles: [],
        selectedProcessProfiles: [],
        selectedFilamentProfiles: [],
      };
      const mockResult: SelectiveProfileImportResult = {
        printerModelId: modelId,
        machineProfilesImported: 0,
        processProfilesImported: 0,
        filamentProfilesImported: 0,
        totalImported: 0,
        skipped: 0,
      };

      vi.mocked(apiClient.post).mockResolvedValue(createApiResponse(mockResult));

      const result = await officialProfilesService.importSelectedProfilesForModel(modelId, emptyRequest);

      expect(result.totalImported).toBe(0);
    });

    it('should handle partial import with skipped profiles', async () => {
      const mockResult: SelectiveProfileImportResult = {
        printerModelId: modelId,
        machineProfilesImported: 1,
        processProfilesImported: 0,
        filamentProfilesImported: 1,
        totalImported: 2,
        skipped: 1,
      };

      vi.mocked(apiClient.post).mockResolvedValue(createApiResponse(mockResult));

      const result = await officialProfilesService.importSelectedProfilesForModel(modelId, request);

      expect(result.totalImported).toBe(2);
      expect(result.skipped).toBe(1);
    });
  });

  describe('getImportedProfileNamesForModel', () => {
    const modelId = 'model-abc-123';

    it('should fetch imported profile names for a model', async () => {
      const mockNames: ImportedProfileNamesDto = {
        machineProfileNames: ['Prusa MK4 0.4mm'],
        processProfileNames: ['0.20mm Standard @MK4', '0.15mm Quality @MK4'],
        filamentProfileNames: ['Generic PLA @MK4'],
      };

      vi.mocked(apiClient.get).mockResolvedValue(createApiResponse(mockNames));

      const result = await officialProfilesService.getImportedProfileNamesForModel(modelId);

      expect(apiClient.get).toHaveBeenCalledWith(
        `/slicer/profiles/imported-names/${modelId}`,
      );
      expect(result).toEqual(mockNames);
      expect(result.processProfileNames).toHaveLength(2);
    });

    it('should return empty lists when no profiles imported', async () => {
      const mockNames: ImportedProfileNamesDto = {
        machineProfileNames: [],
        processProfileNames: [],
        filamentProfileNames: [],
      };

      vi.mocked(apiClient.get).mockResolvedValue(createApiResponse(mockNames));

      const result = await officialProfilesService.getImportedProfileNamesForModel(modelId);

      expect(result.machineProfileNames).toHaveLength(0);
      expect(result.processProfileNames).toHaveLength(0);
      expect(result.filamentProfileNames).toHaveLength(0);
    });

    it('should propagate errors from the API', async () => {
      vi.mocked(apiClient.get).mockRejectedValue(new Error('Not found'));

      await expect(
        officialProfilesService.getImportedProfileNamesForModel(modelId),
      ).rejects.toThrow('Not found');
    });
  });

  describe('getAvailableProfilesFromWorker', () => {
    it('should fetch profiles from the worker', async () => {
      const mockProfiles: OrcaProcessProfile[] = [
        {
          name: '0.20mm Standard',
          quality: 'standard',
          layerHeight: 0.2,
          infillPercentage: 20,
          printSpeed: 50,
          supports: false,
        },
        {
          name: '0.15mm Quality',
          quality: 'quality',
          layerHeight: 0.15,
          infillPercentage: 20,
          printSpeed: 40,
          supports: false,
        },
      ];

      vi.mocked(apiClient.get).mockResolvedValue(createApiResponse(mockProfiles));

      const result = await officialProfilesService.getAvailableProfilesFromWorker();

      expect(apiClient.get).toHaveBeenCalledWith('/slicer/profiles/available-from-worker');
      expect(result).toHaveLength(2);
    });

    it('should propagate worker errors', async () => {
      vi.mocked(apiClient.get).mockRejectedValue(new Error('Worker not connected'));

      await expect(
        officialProfilesService.getAvailableProfilesFromWorker(),
      ).rejects.toThrow('Worker not connected');
    });
  });

  describe('forceReseedSystemProfilesFromWorker', () => {
    it('should trigger reseed and return result', async () => {
      const mockResult = {
        imported: 42,
        deleted: 3,
        message: 'Reseed complete',
        orcaslicerVersion: '2.2.0',
      };

      vi.mocked(apiClient.post).mockResolvedValue(createApiResponse(mockResult));

      const result = await officialProfilesService.forceReseedSystemProfilesFromWorker();

      expect(apiClient.post).toHaveBeenCalledWith(
        '/slicer/profiles/system/orca/force-reseed-from-worker',
      );
      expect(result.imported).toBe(42);
      expect(result.orcaslicerVersion).toBe('2.2.0');
    });
  });

  describe('bulkImportProfilesForPrinter', () => {
    it('should post bulk import request for a registered printer', async () => {
      const printerId = 'printer-xyz';
      const request = { profileIds: ['profile-1', 'profile-2'], makePublic: false };
      const mockResult: BulkProfileImportResult = {
        printerId,
        printerName: 'My Printer',
        totalRequested: 2,
        totalFound: 2,
        imported: 2,
        duplicated: 0,
      };

      vi.mocked(apiClient.post).mockResolvedValue(createApiResponse(mockResult));

      const result = await officialProfilesService.bulkImportProfilesForPrinter(printerId, request);

      expect(apiClient.post).toHaveBeenCalledWith(
        `/slicer/profiles/bulk-import-for-printer/${printerId}`,
        request,
      );
      expect(result.imported).toBe(2);
    });
  });

  describe('getAvailableProfilesForPrinter', () => {
    it('should fetch available profiles for a registered printer', async () => {
      const printerId = 'printer-xyz';
      const mockProfiles: SlicerProfileListItem[] = [
        {
          id: 'p1',
          name: '0.20mm Standard',
          slicerType: 'OrcaSlicer',
          isDefault: false,
          isSystem: true,
          isPublic: true,
          hash: 'profile-hash',
          profileType: 'process',
          quality: 'standard',
          layerHeight: 0.2,
          infillPercentage: 20,
        },
      ];

      vi.mocked(apiClient.get).mockResolvedValue(createApiResponse(mockProfiles));

      const result = await officialProfilesService.getAvailableProfilesForPrinter(printerId);

      expect(apiClient.get).toHaveBeenCalledWith(
        `/slicer/profiles/available-for-printer/${printerId}`,
      );
      expect(result).toHaveLength(1);
    });
  });
});
