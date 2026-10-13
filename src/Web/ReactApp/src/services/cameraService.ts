import { apiClient } from '@/services/api';
import type {
  CameraDto,
  CameraConfigDto,
  CameraListFilter,
  CreateCameraDto,
  DetectCameraEndpointsRequest,
  DetectCameraEndpointsResponse,
  UpdateCameraDto,
  DisplayCameraDto,
  RemoveCameraFromPrinterResponse,
} from '@/types/api';

/**
 * Camera service - manages standalone webcams and provides combined camera views
 */
export const cameraService = {
  async getAllCameras(filter?: CameraListFilter): Promise<CameraDto[]> {
    return apiClient.getAllCameras(filter);
  },

  async getEnabledCameras(filter?: CameraListFilter): Promise<CameraDto[]> {
    return apiClient.getEnabledCameras(filter);
  },

  async getDisplayCameras(filter?: CameraListFilter): Promise<DisplayCameraDto[]> {
    return apiClient.getDisplayCameras(filter);
  },

  async getCameraById(id: string): Promise<CameraDto> {
    return apiClient.getCameraById(id);
  },

  async getCameraConfig(id: string): Promise<CameraConfigDto> {
    return apiClient.getCameraConfig(id);
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

  async getCamerasByPrinter(printerId: string): Promise<CameraDto[]> {
    return apiClient.getCamerasByPrinter(printerId);
  },

  async linkCameraToPrinter(
    cameraId: string,
    printerId: string,
    isPrimary?: boolean
  ): Promise<CameraDto> {
    return apiClient.linkCameraToPrinter(cameraId, printerId, isPrimary);
  },

  async unlinkCameraFromPrinter(cameraId: string, printerId: string): Promise<void> {
    return apiClient.unlinkCameraFromPrinter(cameraId, printerId);
  },

  /** Edit Printer removal: unlinks, and deletes the camera when it was auto-discovered and is now orphaned. */
  async removeCameraFromPrinter(
    printerId: string,
    cameraId: string
  ): Promise<RemoveCameraFromPrinterResponse> {
    return apiClient.removeCameraFromPrinter(printerId, cameraId);
  },

  async detectCameraEndpoints(
    request: DetectCameraEndpointsRequest
  ): Promise<DetectCameraEndpointsResponse> {
    return apiClient.detectCameraEndpoints(request);
  },
};
