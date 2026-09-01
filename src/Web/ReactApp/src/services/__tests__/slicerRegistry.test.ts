import { beforeEach, describe, expect, it, vi } from 'vitest';

const axiosTestState = vi.hoisted(() => {
  const get = vi.fn();
  const del = vi.fn();
  const instance = {
    get,
    delete: del,
    interceptors: {
      request: { use: vi.fn() },
      response: { use: vi.fn() },
    },
  };
  return { get, delete: del, instance };
});

vi.mock('axios', async () => {
  const actual = await vi.importActual<typeof import('axios')>('axios');
  return {
    default: {
      ...actual.default,
      create: vi.fn(() => axiosTestState.instance),
      isAxiosError: actual.default.isAxiosError,
    },
  };
});

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: vi.fn(() => 'http://localhost:5245/api'),
}));

describe('slicerRegistry', () => {
  beforeEach(() => {
    vi.resetModules();
    axiosTestState.get.mockReset();
    axiosTestState.delete.mockReset();
  });

  it('requests the canonical worker collection route', async () => {
    axiosTestState.get.mockResolvedValue({
      data: [
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
      ],
    });
    const { slicerRegistry } = await import('../slicerRegistry');

    const result = await slicerRegistry.getSlicers();

    expect(axiosTestState.get).toHaveBeenCalledWith('/workers/');
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

  it('requests the canonical worker deregistration route', async () => {
    axiosTestState.delete.mockResolvedValue({ data: undefined });
    const { slicerRegistry } = await import('../slicerRegistry');

    await slicerRegistry.deregisterSlicer('service-id');

    expect(axiosTestState.delete).toHaveBeenCalledWith('/workers/service-id');
  });
});
