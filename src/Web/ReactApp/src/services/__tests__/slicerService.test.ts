import { describe, it, expect, vi, beforeEach } from 'vitest';
import { slicerService, SliceRequest } from '../slicerService';
import { apiClient } from '../api';

// Mock the api client
vi.mock('../api', () => ({
  apiClient: {
    post: vi.fn(),
    get: vi.fn(),
  }
}));

// Mock getApiBaseUrl
vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: vi.fn(() => 'http://localhost:5245')
}));

describe('slicerService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('sliceModel', () => {
    it('should slice a model with provided parameters', async () => {
      const mockFile = new File(['test'], 'test.stl', { type: 'application/octet-stream' });
      const request: SliceRequest = {
        modelFile: mockFile,
        slicerEngine: 'orcaslicer',
        printerId: 'printer-123',
        profile: {
          layerHeight: 0.2,
          infillPercentage: 20,
          printSpeed: 50,
          nozzleTemperature: 215,
          bedTemperature: 60,
          supports: false,
          material: 'PLA',
          quality: 'standard'
        }
      };

      const mockResult = {
        jobId: 'job-123',
        gcodeUrl: '/api/gcode/job-123.gcode',
        printTime: 3600,
        filamentUsed: 100,
        layerCount: 200,
        metadata: {
          slicerVersion: '1.0.0',
          profileUsed: 'standard',
          estimatedCost: 5.50
        }
      };

      vi.mocked(apiClient.post).mockResolvedValue({ data: mockResult } as never);

      const result = await slicerService.sliceModel(request);

      expect(result).toEqual(mockResult);
      expect(apiClient.post).toHaveBeenCalledWith('/slicer/slice', expect.any(FormData));
    });
  });

  describe('sliceUploadedModel', () => {
    it('should slice an uploaded model', async () => {
      const mockResult = {
        jobId: 'job-456',
        gcodeUrl: '/api/gcode/job-456.gcode',
        printTime: 7200,
        filamentUsed: 200,
        layerCount: 400,
        metadata: {
          slicerVersion: '1.0.0',
          profileUsed: 'fine',
          estimatedCost: 11.00
        }
      };

      vi.mocked(apiClient.post).mockResolvedValue({ data: mockResult } as never);

      const result = await slicerService.sliceUploadedModel(
        'model-123',
        'prusaslicer',
        'printer-456',
        {
          layerHeight: 0.1,
          infillPercentage: 30,
          printSpeed: 40,
          nozzleTemperature: 210,
          bedTemperature: 60,
          supports: true,
          material: 'PETG',
          quality: 'fine'
        }
      );

      expect(result).toEqual(mockResult);
      expect(apiClient.post).toHaveBeenCalledWith(
        '/slicer/slice-model/model-123',
        expect.any(FormData)
      );
    });
  });

  describe('getAvailableProfiles', () => {
    it('should fetch available profiles for a printer', async () => {
      const mockProfiles = [
        {
          layerHeight: 0.2,
          infillPercentage: 20,
          printSpeed: 50,
          nozzleTemperature: 215,
          bedTemperature: 60,
          supports: false,
          material: 'PLA',
          quality: 'standard' as const
        },
        {
          layerHeight: 0.1,
          infillPercentage: 30,
          printSpeed: 40,
          nozzleTemperature: 215,
          bedTemperature: 60,
          supports: true,
          material: 'PLA',
          quality: 'fine' as const
        }
      ];

      vi.mocked(apiClient.get).mockResolvedValue({ data: mockProfiles } as never);

      const result = await slicerService.getAvailableProfiles('printer-123');

      expect(result).toEqual(mockProfiles);
      expect(apiClient.get).toHaveBeenCalledWith('/slicer/profiles?printerId=printer-123');
    });
  });

  describe('validateModel', () => {
    it('should validate a model file', async () => {
      const mockFile = new File(['test'], 'test.stl', { type: 'application/octet-stream' });
      const mockValidation = {
        valid: true,
        issues: []
      };

      vi.mocked(apiClient.post).mockResolvedValue({ data: mockValidation } as never);

      const result = await slicerService.validateModel(mockFile);

      expect(result).toEqual(mockValidation);
      expect(apiClient.post).toHaveBeenCalledWith('/3d-models/validate', expect.any(FormData));
    });

    it('should return validation issues', async () => {
      const mockFile = new File(['test'], 'invalid.stl', { type: 'application/octet-stream' });
      const mockValidation = {
        valid: false,
        issues: ['File is corrupted', 'Missing manifold geometry']
      };

      vi.mocked(apiClient.post).mockResolvedValue({ data: mockValidation } as never);

      const result = await slicerService.validateModel(mockFile);

      expect(result.valid).toBe(false);
      expect(result.issues).toHaveLength(2);
    });
  });

  describe('subscribeToSlicingProgress', () => {
    it('should create an EventSource for slicing progress', () => {
      const mockOnProgress = vi.fn();
      
      // Mock EventSource class
      class MockEventSource {
        onmessage: ((event: MessageEvent) => void) | null = null;
        onerror: (() => void) | null = null;
        close = vi.fn();
      }
      
      global.EventSource = MockEventSource as never;

      const eventSource = slicerService.subscribeToSlicingProgress('job-123', mockOnProgress);

      expect(eventSource).toBeDefined();
      expect(eventSource).toBeInstanceOf(MockEventSource);
    });
  });
});
