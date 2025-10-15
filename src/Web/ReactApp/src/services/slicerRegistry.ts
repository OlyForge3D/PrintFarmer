import { apiClient } from '@/services/api';

export interface SlicerDto {
  id: string;
  name: string;
  slicerType?: string;
  version?: string;
  host?: string;
  uiManifestUrl?: string;
  status?: string;
  lastSeen?: string | null;
  tags?: string[];
  maxConcurrentJobs?: number;
  capabilitiesJson?: string | null;
}

class SlicerRegistryClient {
  async getSlicers(): Promise<SlicerDto[]> {
    const resp = await apiClient.request<SlicerDto[]>({ method: 'get', url: '/slicers' });
    return resp;
  }

  async deregisterSlicer(id: string): Promise<void> {
    await apiClient.request<void>({ method: 'post', url: `/slicers/${id}/deregister` });
  }
}

export const slicerRegistry = new SlicerRegistryClient();
