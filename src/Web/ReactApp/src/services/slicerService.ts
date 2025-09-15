// Slicer service interfaces and types
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
  private getBaseUrl(): string {
    const envBaseUrl = import.meta.env.VITE_API_BASE_URL;
    const baseUrl = envBaseUrl || '/api';
    
    // Additional validation
    if (!baseUrl || baseUrl === 'undefined') {
      console.warn('Invalid baseUrl detected, using /api fallback:', baseUrl);
      return '/api';
    }
    
    return baseUrl;
  }

  async sliceModel(request: SliceRequest): Promise<SliceResult> {
    const formData = new FormData();
    formData.append('modelFile', request.modelFile);
    formData.append('slicerEngine', request.slicerEngine);
    formData.append('printerId', request.printerId);
    formData.append('profile', JSON.stringify(request.profile));

    const response = await fetch(`${this.getBaseUrl()}/3d-models`, {
      method: 'POST',
      body: formData,
      headers: {
        'Authorization': `Bearer ${localStorage.getItem('auth-token')}`
      }
    });

    if (!response.ok) {
      throw new Error(`Slicing failed: ${response.statusText}`);
    }

    return response.json();
  }

  async getAvailableProfiles(printerId: string): Promise<SlicerProfile[]> {
    const baseUrl = this.getBaseUrl();
    const response = await fetch(`${baseUrl}/slicer/profiles?printerId=${printerId}`);
    if (!response.ok) {
      throw new Error(`Failed to fetch profiles: ${response.statusText}`);
    }
    return response.json();
  }

  async validateModel(file: File): Promise<{ valid: boolean; issues?: string[] }> {
    const formData = new FormData();
    formData.append('modelFile', file);

    const baseUrl = this.getBaseUrl();
    const response = await fetch(`${baseUrl}/slicer/validate`, {
      method: 'POST',
      body: formData
    });

    if (!response.ok) {
      throw new Error(`Model validation failed: ${response.statusText}`);
    }

    return response.json();
  }

  // Real-time slicing progress via SSE
  subscribeToSlicingProgress(jobId: string, onProgress: (progress: SlicingProgress) => void): EventSource {
    const baseUrl = this.getBaseUrl();
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
    const baseUrl = this.getBaseUrl();
    const response = await fetch(`${baseUrl}/slicer/job/${jobId}`);
    if (!response.ok) {
      throw new Error(`Failed to fetch slicing job: ${response.statusText}`);
    }
    return response.json();
  }

  async cancelSlicingJob(jobId: string): Promise<void> {
    const baseUrl = this.getBaseUrl();
    const response = await fetch(`${baseUrl}/slicer/job/${jobId}/cancel`, {
      method: 'POST'
    });
    if (!response.ok) {
      throw new Error(`Failed to cancel slicing job: ${response.statusText}`);
    }
  }

  // Model management
  async uploadModel(file: File): Promise<{ id: string; url: string }> {
    const formData = new FormData();
    formData.append('modelFile', file);

    const baseUrl = this.getBaseUrl();
    const uploadUrl = `${baseUrl}/3d-models`;

    const response = await fetch(uploadUrl, {
      method: 'POST',
      body: formData
    });

    if (!response.ok) {
      throw new Error(`Model upload failed: ${response.statusText}`);
    }

    return response.json();
  }

  async listModels(): Promise<SlicedModelSummary[]> {
    const baseUrl = this.getBaseUrl();
    const response = await fetch(`${baseUrl}/3d-models`);
    if (!response.ok) {
      throw new Error(`Failed to fetch models: ${response.statusText}`);
    }
    return response.json();
  }

  async deleteModel(modelId: string): Promise<void> {
    const baseUrl = this.getBaseUrl();
    const response = await fetch(`${baseUrl}/3d-models/${modelId}`, {
      method: 'DELETE'
    });
    if (!response.ok) {
      throw new Error(`Failed to delete model: ${response.statusText}`);
    }
  }
}

export const slicerService = new SlicerService();