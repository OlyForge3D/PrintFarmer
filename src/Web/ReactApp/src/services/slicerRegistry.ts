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

interface WorkerDto {
  id: string;
  serviceId?: string;
  name: string;
  endpointUrl?: string;
  status?: string;
  lastHeartbeat?: string | null;
  version?: string;
  capabilities?: string[];
}

function mapWorkerToSlicer(worker: WorkerDto): SlicerDto {
  return {
    id: worker.serviceId || worker.id,
    name: worker.name,
    slicerType: 'OrcaSlicer',
    version: worker.version,
    host: worker.endpointUrl,
    status: worker.status,
    lastSeen: worker.lastHeartbeat ?? null,
    capabilitiesJson: worker.capabilities ? JSON.stringify(worker.capabilities) : null,
  };
}

class SlicerRegistryClient {
  async getSlicers(): Promise<SlicerDto[]> {
    // Canonical source for UI worker discovery in both monolith and microservices.
    const workers = await apiClient.request<WorkerDto[]>({ method: 'get', url: '/workers' });
    return workers.map(mapWorkerToSlicer);
  }

  async deregisterSlicer(id: string): Promise<void> {
    await apiClient.request<void>({ method: 'delete', url: `/workers/${id}` });
  }
}

export const slicerRegistry = new SlicerRegistryClient();
