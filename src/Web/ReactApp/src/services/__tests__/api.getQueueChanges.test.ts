import { beforeEach, describe, expect, it, vi } from 'vitest';

const axiosTestState = vi.hoisted(() => {
  const get = vi.fn();
  const instance = {
    get,
    interceptors: {
      request: { use: vi.fn() },
      response: { use: vi.fn() },
    },
  };
  return { get, instance };
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

describe('ApiClient.getQueueChanges', () => {
  beforeEach(() => {
    vi.resetModules();
    axiosTestState.get.mockReset();
  });

  it('returns the feed unchanged on a normal 200 response', async () => {
    axiosTestState.get.mockResolvedValueOnce({
      data: { afterSequence: 0, nextSequence: 3, hasMore: false, events: [] },
    });
    const { ApiClient } = await import('../api');
    const client = new ApiClient();

    const feed = await client.getQueueChanges(0, 100);

    expect(feed).toEqual({
      afterSequence: 0,
      nextSequence: 3,
      hasMore: false,
      events: [],
    });
    expect(feed.expired).toBeUndefined();
  });

  it('translates a 410 cursor_expired response into a synthetic expired feed instead of throwing', async () => {
    const goneError = {
      isAxiosError: true,
      response: {
        status: 410,
        data: { error: 'cursor_expired', detail: 'expired', currentSequence: 42 },
      },
    };
    axiosTestState.get.mockRejectedValueOnce(goneError);
    const { ApiClient } = await import('../api');
    const client = new ApiClient();

    const feed = await client.getQueueChanges(5, 100);

    expect(feed.expired).toBe(true);
    expect(feed.currentSequence).toBe(42);
    expect(feed.nextSequence).toBe(42);
    expect(feed.hasMore).toBe(false);
    expect(feed.events).toEqual([]);
  });

  it('falls back to afterSequence when a 410 response has no currentSequence body', async () => {
    const goneError = {
      isAxiosError: true,
      response: { status: 410, data: {} },
    };
    axiosTestState.get.mockRejectedValueOnce(goneError);
    const { ApiClient } = await import('../api');
    const client = new ApiClient();

    const feed = await client.getQueueChanges(7, 100);

    expect(feed.expired).toBe(true);
    expect(feed.currentSequence).toBe(7);
  });

  it('rethrows non-410 errors instead of swallowing them', async () => {
    const serverError = {
      isAxiosError: true,
      response: { status: 500, data: { error: 'internal' } },
    };
    axiosTestState.get.mockRejectedValueOnce(serverError);
    const { ApiClient } = await import('../api');
    const client = new ApiClient();

    await expect(client.getQueueChanges(0, 100)).rejects.toBe(serverError);
  });
});
