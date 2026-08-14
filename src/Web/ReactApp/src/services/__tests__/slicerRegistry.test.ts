import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '../api';
import { slicerRegistry } from '../slicerRegistry';

vi.mock('../api', () => ({
  apiClient: {
    request: vi.fn(),
  },
}));

describe('slicerRegistry', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('requests the canonical worker collection route', async () => {
    vi.mocked(apiClient.request).mockResolvedValue([
      {
        id: 'worker-id',
        serviceId: 'service-id',
        name: 'Orca worker',
        endpointUrl: 'http://orcaslicer-worker:8080',
        status: 'Online',
        lastHeartbeat: '2026-08-14T01:00:00Z',
        version: '2.4.2',
        capabilities: ['orcaslicer'],
      },
    ]);

    const result = await slicerRegistry.getSlicers();

    expect(apiClient.request).toHaveBeenCalledWith({ method: 'get', url: '/workers/' });
    expect(result).toEqual([
      {
        id: 'service-id',
        name: 'Orca worker',
        slicerType: 'OrcaSlicer',
        version: '2.4.2',
        host: 'http://orcaslicer-worker:8080',
        status: 'Online',
        lastSeen: '2026-08-14T01:00:00Z',
        capabilitiesJson: '["orcaslicer"]',
      },
    ]);
  });
});
