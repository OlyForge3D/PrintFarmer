import { describe, it, expect, vi, beforeEach } from 'vitest';
import { cameraService } from '../cameraService';
import { apiClient } from '../api';
import type { CameraDto, CreateCameraDto, UpdateCameraDto, DisplayCameraDto } from '@/types/api';

// Mock the api client
vi.mock('../api', () => ({
  apiClient: {
    getAllCameras: vi.fn(),
    getEnabledCameras: vi.fn(),
    getDisplayCameras: vi.fn(),
    getCameraById: vi.fn(),
    createCamera: vi.fn(),
    updateCamera: vi.fn(),
    deleteCamera: vi.fn(),
    toggleCamera: vi.fn(),
  }
}));

describe('cameraService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('getAllCameras', () => {
    it('should get all cameras', async () => {
      const mockCameras: CameraDto[] = [
        { id: '1', name: 'Camera 1', streamUrl: 'http://cam1.local', isEnabled: true },
        { id: '2', name: 'Camera 2', streamUrl: 'http://cam2.local', isEnabled: false },
      ];

      vi.mocked(apiClient.getAllCameras).mockResolvedValue(mockCameras as never);

      const result = await cameraService.getAllCameras();

      expect(result).toEqual(mockCameras);
      expect(apiClient.getAllCameras).toHaveBeenCalled();
    });
  });

  describe('getEnabledCameras', () => {
    it('should get only enabled cameras', async () => {
      const mockCameras: CameraDto[] = [
        { id: '1', name: 'Camera 1', streamUrl: 'http://cam1.local', isEnabled: true },
      ];

      vi.mocked(apiClient.getEnabledCameras).mockResolvedValue(mockCameras as never);

      const result = await cameraService.getEnabledCameras();

      expect(result).toEqual(mockCameras);
      expect(apiClient.getEnabledCameras).toHaveBeenCalled();
    });
  });

  describe('getDisplayCameras', () => {
    it('should get display cameras', async () => {
      const mockDisplayCameras: DisplayCameraDto[] = [
        { id: '1', name: 'Camera 1', streamUrl: 'http://cam1.local', type: 'webcam' },
      ];

      vi.mocked(apiClient.getDisplayCameras).mockResolvedValue(mockDisplayCameras as never);

      const result = await cameraService.getDisplayCameras();

      expect(result).toEqual(mockDisplayCameras);
      expect(apiClient.getDisplayCameras).toHaveBeenCalled();
    });
  });

  describe('getCameraById', () => {
    it('should get a camera by ID', async () => {
      const mockCamera: CameraDto = {
        id: '1',
        name: 'Camera 1',
        streamUrl: 'http://cam1.local',
        isEnabled: true
      };

      vi.mocked(apiClient.getCameraById).mockResolvedValue(mockCamera as never);

      const result = await cameraService.getCameraById('1');

      expect(result).toEqual(mockCamera);
      expect(apiClient.getCameraById).toHaveBeenCalledWith('1');
    });
  });

  describe('createCamera', () => {
    it('should create a new camera', async () => {
      const request: CreateCameraDto = {
        name: 'New Camera',
        streamUrl: 'http://newcam.local',
        isEnabled: true
      };

      const mockCreatedCamera: CameraDto = {
        id: '3',
        ...request
      };

      vi.mocked(apiClient.createCamera).mockResolvedValue(mockCreatedCamera as never);

      const result = await cameraService.createCamera(request);

      expect(result).toEqual(mockCreatedCamera);
      expect(apiClient.createCamera).toHaveBeenCalledWith(request);
    });
  });

  describe('updateCamera', () => {
    it('should update an existing camera', async () => {
      const request: UpdateCameraDto = {
        name: 'Updated Camera',
        isEnabled: false
      };

      const mockUpdatedCamera: CameraDto = {
        id: '1',
        name: 'Updated Camera',
        streamUrl: 'http://cam1.local',
        isEnabled: false
      };

      vi.mocked(apiClient.updateCamera).mockResolvedValue(mockUpdatedCamera as never);

      const result = await cameraService.updateCamera('1', request);

      expect(result).toEqual(mockUpdatedCamera);
      expect(apiClient.updateCamera).toHaveBeenCalledWith('1', request);
    });
  });

  describe('deleteCamera', () => {
    it('should delete a camera', async () => {
      vi.mocked(apiClient.deleteCamera).mockResolvedValue(undefined as never);

      await cameraService.deleteCamera('1');

      expect(apiClient.deleteCamera).toHaveBeenCalledWith('1');
    });
  });

  describe('toggleCamera', () => {
    it('should enable a camera', async () => {
      const mockCamera: CameraDto = {
        id: '1',
        name: 'Camera 1',
        streamUrl: 'http://cam1.local',
        isEnabled: true
      };

      vi.mocked(apiClient.toggleCamera).mockResolvedValue(mockCamera as never);

      const result = await cameraService.toggleCamera('1', true);

      expect(result).toEqual(mockCamera);
      expect(apiClient.toggleCamera).toHaveBeenCalledWith('1', true);
    });

    it('should disable a camera', async () => {
      const mockCamera: CameraDto = {
        id: '1',
        name: 'Camera 1',
        streamUrl: 'http://cam1.local',
        isEnabled: false
      };

      vi.mocked(apiClient.toggleCamera).mockResolvedValue(mockCamera as never);

      const result = await cameraService.toggleCamera('1', false);

      expect(result).toEqual(mockCamera);
      expect(apiClient.toggleCamera).toHaveBeenCalledWith('1', false);
    });
  });
});
