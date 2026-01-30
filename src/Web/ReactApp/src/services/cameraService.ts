import { apiClient } from '@/services/api';
import type { CameraDto, CreateCameraDto, UpdateCameraDto, DisplayCameraDto } from '@/types/api';

/**
 * Camera service - manages standalone webcams and provides combined camera views
 */
export const cameraService = {
  async getAllCameras(): Promise<CameraDto[]> {
    return apiClient.getAllCameras();
  },

  async getEnabledCameras(): Promise<CameraDto[]> {
    return apiClient.getEnabledCameras();
  },

  async getDisplayCameras(): Promise<DisplayCameraDto[]> {
    return apiClient.getDisplayCameras();
  },

  async getCameraById(id: string): Promise<CameraDto> {
    return apiClient.getCameraById(id);
  },

  async createCamera(request: CreateCameraDto): Promise<CameraDto> {
    return apiClient.createCamera(request);
  },

  async updateCamera(id: string, request: UpdateCameraDto): Promise<CameraDto> {
    return apiClient.updateCamera(id, request);
  },

  async deleteCamera(id: string): Promise<void> {
    return apiClient.deleteCamera(id);
  },

  async toggleCamera(id: string, isEnabled: boolean): Promise<CameraDto> {
    return apiClient.toggleCamera(id, isEnabled);
  },
};
