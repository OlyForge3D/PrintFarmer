import { describe, it, expect, beforeEach } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { clearSensitiveUserQueries } from '@/common/auth/sensitiveQueryCache';
import { getAuthEpoch } from '@/common/auth/authEpoch';

describe('clearSensitiveUserQueries', () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
  });

  it('removes user-owned sensitive query cache entries', async () => {
    queryClient.setQueryData(['notifications'], [{ id: '1' }]);
    queryClient.setQueryData(['notifications', 'unread-count'], 3);
    queryClient.setQueryData(['notifications', 'preferences'], { email: true });
    queryClient.setQueryData(['settings', 'user'], { theme: 'dark' });
    queryClient.setQueryData(['passkeys'], [{ id: 'pk-1' }]);
    queryClient.setQueryData(['apiKeys', 'user-a'], [{ id: 'key-1' }]);
    queryClient.setQueryData(['printables', 'oauth-status'], { connected: true });
    queryClient.setQueryData(['printables', 'liked-models'], [{ id: 'model-1' }]);
    queryClient.setQueryData(['printables', 'download-history'], [{ id: 'dl-1' }]);
    queryClient.setQueryData(['slice-jobs'], [{ id: 'job-1' }]);

    await clearSensitiveUserQueries(queryClient);

    expect(queryClient.getQueryData(['notifications'])).toBeUndefined();
    expect(queryClient.getQueryData(['notifications', 'unread-count'])).toBeUndefined();
    expect(queryClient.getQueryData(['notifications', 'preferences'])).toBeUndefined();
    expect(queryClient.getQueryData(['settings', 'user'])).toBeUndefined();
    expect(queryClient.getQueryData(['passkeys'])).toBeUndefined();
    expect(queryClient.getQueryData(['apiKeys', 'user-a'])).toBeUndefined();
    expect(queryClient.getQueryData(['printables', 'oauth-status'])).toBeUndefined();
    expect(queryClient.getQueryData(['printables', 'liked-models'])).toBeUndefined();
    expect(queryClient.getQueryData(['printables', 'download-history'])).toBeUndefined();
    expect(queryClient.getQueryData(['slice-jobs'])).toBeUndefined();
  });

  it('does not clear unrelated public/shared caches', async () => {
    queryClient.setQueryData(['printers'], [{ id: 'p-1' }]);
    queryClient.setQueryData(['settings', 'farm'], { name: 'My Farm' });
    queryClient.setQueryData(['manufacturers'], [{ id: 'm-1' }]);
    queryClient.setQueryData(['apiKeySettings'], { hashingEnabled: true });

    await clearSensitiveUserQueries(queryClient);

    expect(queryClient.getQueryData(['printers'])).toEqual([{ id: 'p-1' }]);
    expect(queryClient.getQueryData(['settings', 'farm'])).toEqual({ name: 'My Farm' });
    expect(queryClient.getQueryData(['manufacturers'])).toEqual([{ id: 'm-1' }]);
    expect(queryClient.getQueryData(['apiKeySettings'])).toEqual({ hashingEnabled: true });
  });

  it('cancels in-flight sensitive fetches before removing the cache entry', async () => {
    let resolveFetch: (value: { stale: boolean }) => void = () => {};
    const fetchPromise = new Promise<{ stale: boolean }>((resolve) => {
      resolveFetch = resolve;
    });

    // Kick off a "background refetch" for notification preferences that
    // hasn't resolved yet — simulating the race called out in #762.
    const inFlight = queryClient.fetchQuery({
      queryKey: ['notifications', 'preferences'],
      queryFn: () => fetchPromise,
    });

    await clearSensitiveUserQueries(queryClient);

    // Now let the stale in-flight response resolve.
    resolveFetch({ stale: true });
    await inFlight.catch(() => {
      // cancelled queries reject with a CancelledError — that is expected.
    });

    // The stale response must not have repopulated the cache.
    expect(queryClient.getQueryData(['notifications', 'preferences'])).toBeUndefined();
  });

  it('bumps the shared auth epoch on every call', async () => {
    const before = getAuthEpoch();

    await clearSensitiveUserQueries(queryClient);
    expect(getAuthEpoch()).toBe(before + 1);

    await clearSensitiveUserQueries(queryClient);
    expect(getAuthEpoch()).toBe(before + 2);
  });
});
