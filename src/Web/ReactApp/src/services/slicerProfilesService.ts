// Service for interacting with slicer profile API endpoints (Phase 6)
// Provides list, import, export, and set-default operations.

// Get API base URL with proper environment handling
const getApiBaseUrl = (): string => {
  const rawBase = import.meta.env.VITE_API_BASE_URL as string | undefined;
  if (!rawBase || rawBase.trim() === '') return '/api/slicer/profiles';
  const trimmed = rawBase.replace(/\/$/, '');
  if (/\/(api)(\/|$)/.test(trimmed)) return `${trimmed}/slicer/profiles`;
  return `${trimmed}/api/slicer/profiles`;
};

export interface SlicerProfileListItem {
  id: string;
  name: string;
  slicerType: string;
  material: string;
  quality: string;
  layerHeight: number;
  infillPercentage: number;
  isDefault: boolean;
  isSystem: boolean;
  isPublic: boolean;
  hash: string;
}

export interface ImportSlicerProfileRequest {
  rawJson: string;
  name?: string;
  description?: string;
  slicerType: string; // PrusaSlicer, OrcaSlicer, etc.
  allowSystemOverride?: boolean;
  setDefault?: boolean;
  isPublic?: boolean;
}

export interface SlicerProfileExtended {
  id: string;
  name: string;
  description?: string | null;
  slicerType: string;
  layerHeight: number;
  infillPercentage: number;
  printSpeed: number;
  nozzleTemperature: number;
  bedTemperature: number;
  enableSupports: boolean;
  material: string;
  quality: string;
  isDefault: boolean;
  isPublic: boolean;
  isSystem: boolean;
  hash: string;
  createdAt: string;
  updatedAt: string;
  metadata: Record<string, unknown>;
}

export interface SlicerProfileExportDto {
  id: string;
  name: string;
  slicerType: string;
  hash: string;
  rawJson: string;
  metadata: Record<string, unknown>;
}

const base = getApiBaseUrl();

async function handle<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const text = await res.text();
    throw new Error(text || `Request failed (${res.status})`);
  }
  return res.json() as Promise<T>;
}

function getAuthHeaders(): HeadersInit {
  const headers: HeadersInit = {
    'Content-Type': 'application/json',
  };
  const token = localStorage.getItem('auth-token');
  if (token) {
    headers['Authorization'] = `Bearer ${token}`;
  }
  return headers;
}

export const slicerProfilesService = {
  async listExtended(): Promise<SlicerProfileListItem[]> {
    const res = await fetch(`${base}/extended`, {
      headers: getAuthHeaders()
    });
    return handle<SlicerProfileListItem[]>(res);
  },
  async importProfile(req: ImportSlicerProfileRequest): Promise<SlicerProfileExtended> {
    const res = await fetch(`${base}/import`, {
      method: 'POST',
      headers: getAuthHeaders(),
      body: JSON.stringify(req)
    });
    return handle<SlicerProfileExtended>(res);
  },
  async exportProfile(id: string): Promise<SlicerProfileExportDto> {
    const res = await fetch(`${base}/${id}/export`, {
      headers: getAuthHeaders()
    });
    return handle<SlicerProfileExportDto>(res);
  },
  async setDefault(id: string): Promise<void> {
    const res = await fetch(`${base}/${id}/set-default`, {
      method: 'POST',
      headers: getAuthHeaders()
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error(text || `Failed to set default (${res.status})`);
    }
  }
};

export default slicerProfilesService;
