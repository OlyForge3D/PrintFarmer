import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AutoDispatchDetailedStatus } from '@/types/api';

const axiosTestState = vi.hoisted(() => {
  const get = vi.fn();
  const post = vi.fn();
  const put = vi.fn();
  const instance = {
    get,
    post,
    put,
    interceptors: {
      request: { use: vi.fn() },
      response: { use: vi.fn() },
    },
  };
  return { get, post, put, instance };
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

describe('autoDispatchApi', () => {
  beforeEach(() => {
    vi.resetModules();
    axiosTestState.get.mockReset();
    axiosTestState.post.mockReset();
    axiosTestState.put.mockReset();
  });

  it('should fetch global auto-dispatch status from the auto-dispatch route', async () => {
    const mockResponse = {
      data: {
        globalEnabled: true,
        printers: [],
      },
    };
    axiosTestState.get.mockResolvedValue(mockResponse);
    const { getAutoDispatchStatus } = await import('../api/autoDispatchApi');

    const result = await getAutoDispatchStatus();

    expect(axiosTestState.get).toHaveBeenCalledWith('/auto-dispatch/status');
    expect(result).toEqual(mockResponse.data);
  });

  it('should post ready confirmations through the canonical auto-dispatch helper', async () => {
    const mockResponse = {
      data: {
        status: {
          printerId: 'printer-1',
          enabled: true,
          state: 'Ready',
          queueDepth: 1,
        },
        nextJob: null,
        filamentCheck: null,
      },
    };
    axiosTestState.post.mockResolvedValue(mockResponse);
    const { confirmAutoDispatchReady } = await import('../api/autoDispatchApi');

    const result = await confirmAutoDispatchReady('printer-1', 'dispatch-etag');

    expect(axiosTestState.post).toHaveBeenCalledWith(
      '/auto-dispatch/printer-1/ready',
      undefined,
      expect.objectContaining({
        headers: { 'If-Match': '"dispatch-etag"' },
        validateStatus: expect.any(Function),
      })
    );
    const config = axiosTestState.post.mock.calls[0]?.[2];
    expect(config.validateStatus(200)).toBe(true);
    expect(config.validateStatus(202)).toBe(true);
    expect(config.validateStatus(409)).toBe(true);
    expect(config.validateStatus(500)).toBe(false);
    expect(result).toEqual(mockResponse.data);
  });

  it('should return reconciliation-pending ready responses from HTTP 202', async () => {
    const mockResponse = {
      status: 202,
      data: {
        status: {
          printerId: 'printer-1',
          enabled: true,
          state: 'PendingReady',
          queueDepth: 1,
        },
        dispatchInitiated: true,
        dispatchOutcome: 'Unknown',
        dispatchReconciliationPending: true,
      },
    };
    axiosTestState.post.mockResolvedValue(mockResponse);
    const { confirmAutoDispatchReady } = await import('../api/autoDispatchApi');

    const result = await confirmAutoDispatchReady('printer-1', 'dispatch-etag');

    expect(result).toEqual(mockResponse.data);
  });

  it('should make filament override confirmation explicit in the ready request', async () => {
    const mockResponse = {
      data: {
        status: {
          printerId: 'printer-1',
          enabled: true,
          state: 'Ready',
          queueDepth: 1,
        },
        dispatchInitiated: true,
        filamentOverrideApplied: true,
      },
    };
    axiosTestState.post.mockResolvedValue(mockResponse);
    const { confirmAutoDispatchReady } = await import('../api/autoDispatchApi');

    await confirmAutoDispatchReady(
      'printer-1',
      'dispatch-etag',
      true,
      'job-etag',
      'filament-check-etag'
    );

    expect(axiosTestState.post).toHaveBeenCalledWith(
      '/auto-dispatch/printer-1/ready?confirmFilamentOverride=true',
      undefined,
      expect.objectContaining({
        headers: {
          'If-Match': '"dispatch-etag"',
          'X-Job-If-Match': '"job-etag"',
          'X-Filament-Check-If-Match': '"filament-check-etag"',
        },
      })
    );
  });

  it('should reject non-filament 409 responses as real conflicts', async () => {
    axiosTestState.post.mockResolvedValue({
      status: 409,
      data: {
        error: 'queue_empty',
        detail: 'The reviewed queue head no longer exists.',
      },
    });
    const { confirmAutoDispatchReady } = await import('../api/autoDispatchApi');

    await expect(
      confirmAutoDispatchReady(
        'printer-1',
        'dispatch-etag',
        true,
        'job-etag',
        'filament-check-etag'
      )
    ).rejects.toMatchObject({
      statusCode: 409,
      message: 'The reviewed queue head no longer exists.',
    });
  });

  it('should post skip requests to the auto-dispatch route', async () => {
    axiosTestState.post.mockResolvedValue({ data: undefined });
    const { skipAutoDispatchJob } = await import('../api/autoDispatchApi');

    await skipAutoDispatchJob('printer-1', 'dispatch-etag', 'job-etag');

    expect(axiosTestState.post).toHaveBeenCalledWith(
      '/auto-dispatch/printer-1/skip',
      undefined,
      {
        headers: {
          'If-Match': '"dispatch-etag"',
          'X-Job-If-Match': '"job-etag"',
        },
      }
    );
  });

  it('should post cancel requests to the auto-dispatch route', async () => {
    axiosTestState.post.mockResolvedValue({ data: undefined });
    const { cancelAutoDispatch } = await import('../api/autoDispatchApi');

    await cancelAutoDispatch('printer-1', 'dispatch-etag');

    expect(axiosTestState.post).toHaveBeenCalledWith(
      '/auto-dispatch/printer-1/cancel',
      undefined,
      { headers: { 'If-Match': '"dispatch-etag"' } }
    );
  });

  it('should post pre-clear requests to the auto-dispatch route', async () => {
    const mockResponse = {
      data: {
        printerId: 'printer-1',
        enabled: true,
        state: 'None',
        queueDepth: 0,
        bedPreConfirmed: true,
      },
    };
    axiosTestState.post.mockResolvedValue(mockResponse);
    const { preClearAutoDispatchBed } = await import('../api/autoDispatchApi');

    const result = await preClearAutoDispatchBed('printer-1', 'dispatch-etag');

    expect(axiosTestState.post).toHaveBeenCalledWith(
      '/auto-dispatch/printer-1/pre-clear',
      undefined,
      { headers: { 'If-Match': '"dispatch-etag"' } }
    );
    expect(result).toEqual(mockResponse.data);
  });

  it('should put per-printer enabled changes to the auto-dispatch route', async () => {
    axiosTestState.put.mockResolvedValue({ data: undefined });
    const { setAutoDispatchEnabled } = await import('../api/autoDispatchApi');

    await setAutoDispatchEnabled('printer-1', true, 'dispatch-etag', 'printer-etag');

    expect(axiosTestState.put).toHaveBeenCalledWith(
      '/auto-dispatch/printer-1/enabled',
      { enabled: true },
      {
        headers: {
          'If-Match': '"dispatch-etag"',
          'X-Printer-If-Match': '"printer-etag"',
        },
      }
    );
  });

  it('should put global enabled changes to the auto-dispatch route', async () => {
    axiosTestState.put.mockResolvedValue({ data: undefined });
    const { setAutoDispatchGlobalEnabled } = await import('../api/autoDispatchApi');

    const statuses: AutoDispatchDetailedStatus[] = [
      {
        printerId: 'printer-1',
        printerName: 'Printer 1',
        enabled: true,
        isReady: true,
        queueDepth: 1,
        readyGateChecks: [],
        state: 'Ready',
        dispatchStateETag: 'dispatch-etag',
        printerETag: 'printer-etag',
      },
    ];
    await setAutoDispatchGlobalEnabled(false, statuses);

    expect(axiosTestState.put).toHaveBeenCalledWith('/auto-dispatch/enabled', {
      enabled: false,
      expectedVersions: {
        'printer-1': {
          dispatchStateETag: 'dispatch-etag',
          printerETag: 'printer-etag',
        },
      },
    });
  });
});
