// Slicer service interfaces and types
import { apiClient } from './api';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';

export interface SliceRequest {
  modelFile: File;
  slicerEngine: 'prusaslicer' | 'orcaslicer';
  printerId: string;
  profile: SlicerProfile;
}

export interface SlicerProfile {
  layerHeight: number;
  infillPercentage: number;
  printSpeed: number;
  nozzleTemperature: number;
  bedTemperature: number;
  supports: boolean;
  material: string;
  quality: 'draft' | 'standard' | 'fine';
}

export interface SliceResult {
  jobId: string;
  gcodeUrl: string;
  printTime: number; // in seconds
  filamentUsed: number; // in grams
  layerCount: number;
  metadata: {
    slicerVersion: string;
    profileUsed: string;
    estimatedCost: number;
  };
}

export interface SlicingProgress {
  jobId: string;
  progress: number; // 0-100
  status: 'queued' | 'slicing' | 'completed' | 'error';
  message?: string;
}

export interface SlicedModelSummary {
  id: string;
  name: string;
  sizeBytes?: number;
  createdAt?: string;
  updatedAt?: string;
}

class SlicerService {
  async sliceModel(request: SliceRequest): Promise<SliceResult> {
    const formData = new FormData();
    formData.append('modelFile', request.modelFile);
    formData.append('slicerEngine', request.slicerEngine);
    formData.append('printerId', request.printerId);
    formData.append('profile', JSON.stringify(request.profile));

    const response = await apiClient.post<SliceResult>('/slicer/slice', formData);
    return response.data;
  }

  async sliceUploadedModel(modelId: string, slicerEngine: 'prusaslicer' | 'orcaslicer', printerId: string, profile: SlicerProfile): Promise<SliceResult> {
    const formData = new FormData();
    formData.append('slicerEngine', slicerEngine);
    formData.append('printerId', printerId);
    formData.append('profile', JSON.stringify(profile));

    const response = await apiClient.post<SliceResult>(`/slicer/slice-model/${modelId}`, formData);
    return response.data;
  }

  async getAvailableProfiles(printerId: string): Promise<SlicerProfile[]> {
    const response = await apiClient.get<SlicerProfile[]>(`/slicer/profiles?printerId=${printerId}`);
    return response.data;
  }

  async validateModel(file: File): Promise<{ valid: boolean; issues?: string[] }> {
    const formData = new FormData();
    formData.append('modelFile', file);

    const response = await apiClient.post<{ valid: boolean; issues?: string[] }>('/3d-models/validate', formData);
    return response.data;
  }

  // Real-time slicing progress via SSE
  subscribeToSlicingProgress(jobId: string, onProgress: (progress: SlicingProgress) => void): EventSource {
    const baseUrl = getApiBaseUrl();
    const eventSource = new EventSource(`${baseUrl}/slicer/progress/${jobId}`);
    
    eventSource.onmessage = (event) => {
      const data = JSON.parse(event.data);
      onProgress(data);
    };

    eventSource.onerror = () => {
      console.error('Error in slicing progress stream');
    };

    return eventSource;
  }

  async getSlicingJob(jobId: string): Promise<SliceResult> {
    const response = await apiClient.get<SliceResult>(`/slicer/job/${jobId}`);
    return response.data;
  }

  async cancelSlicingJob(jobId: string): Promise<void> {
    await apiClient.post(`/slicer/job/${jobId}/cancel`);
  }

  // Model management - uses XHR for progress tracking
  async uploadModel(
    file: File,
    onProgress?: (progress: number) => void
  ): Promise<{ id: string; url: string }> {
    const formData = new FormData();
    formData.append('modelFile', file);

    const baseUrl = getApiBaseUrl();
    const uploadUrl = `${baseUrl}/3d-models/upload`;

    // XMLHttpRequest is needed for progress tracking with fetch
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();

      if (onProgress) {
        xhr.upload.addEventListener('progress', (e) => {
          if (e.lengthComputable) {
            const progress = Math.round((e.loaded / e.total) * 100);
            onProgress(progress);
          }
        });
      }

      xhr.addEventListener('load', () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          try {
            const response = JSON.parse(xhr.responseText);
            resolve(response);
          } catch {
            reject(new Error('Failed to parse upload response'));
          }
        } else {
          reject(new Error(`Model upload failed: ${xhr.statusText}`));
        }
      });

      xhr.addEventListener('error', () => {
        reject(new Error('Network error during upload'));
      });

      xhr.addEventListener('abort', () => {
        reject(new Error('Upload cancelled'));
      });

      xhr.open('POST', uploadUrl);
      xhr.setRequestHeader('Authorization', `Bearer ${localStorage.getItem('auth-token')}`);
      xhr.send(formData);
    });
  }

  async listModels(): Promise<SlicedModelSummary[]> {
    const response = await apiClient.get<SlicedModelSummary[]>('/3d-models');
    return response.data;
  }

  async deleteModel(modelId: string): Promise<void> {
    await apiClient.delete(`/3d-models/${modelId}`);
  }
}

export const slicerService = new SlicerService();