import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { toast } from 'sonner';
import { useConfirmBedClear } from '@/features/printers/hooks/useAutoDispatch';
import { acknowledgeBedClearAndStart } from '@/services/api/autoDispatchApi';
import type { AutoDispatchStatus } from '@/types/api';

// No requests or printer motion: only the hook and local persistence are real.
vi.mock('@/services/api/autoDispatchApi', () => ({
  acknowledgeBedClearAndStart: vi.fn(),
}));
vi.mock('@/services/printer-signalr', () => ({ printerSignalRService: {} }));
vi.mock('sonner', () => ({ toast: { error: vi.fn() } }));

const acknowledge = vi.mocked(acknowledgeBedClearAndStart);
const uuidV4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const reviewedStatus: AutoDispatchStatus = {
  printerId: 'printer-1',
  enabled: true,
  state: 'Paused',
  queueDepth: 1,
  nextJobKind: 'FilamentCalibration',
  nextJobId: 'job-1',
  nextJobETag: '"job-revision-1"',
  dispatchStateETag: '"dispatch-revision-1"',
  nextJobPrinterConfigRevision: 7,
};
const storageKey = 'printfarmer:bed-clear:job-1:"job-revision-1":"dispatch-revision-1"';

function renderConfirmation(retry: false | number = false) {
  const queryClient = new QueryClient({
    defaultOptions: { mutations: { retry, retryDelay: 0, gcTime: 0 } },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  return renderHook(() => useConfirmBedClear(), { wrapper });
}

describe('bed-clear idempotency on HTTP LAN', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    acknowledge.mockReset();
    acknowledge.mockResolvedValue({ kind: 'accepted', httpStatus: 202 });
    localStorage.clear();
    vi.stubGlobal('crypto', { getRandomValues: vi.fn(crypto.getRandomValues.bind(crypto)) });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('persists one secure key and reuses it for manual retry and remount', async () => {
    const failure = new Error('Network Error');
    acknowledge.mockRejectedValueOnce(failure);
    const { result, unmount } = renderConfirmation();
    expect(crypto.getRandomValues).not.toHaveBeenCalled();

    await act(async () => {
      await expect(result.current.mutateAsync(reviewedStatus)).rejects.toBe(failure);
    });
    const key = acknowledge.mock.calls[0][0].idempotencyKey;
    expect(key).toMatch(uuidV4);
    expect(localStorage.getItem(storageKey)).toBe(key);

    await act(async () => { await result.current.mutateAsync(reviewedStatus); });
    expect(acknowledge.mock.calls[1][0]).toEqual(acknowledge.mock.calls[0][0]);
    expect(crypto.getRandomValues).toHaveBeenCalledTimes(1);
    unmount();

    // A persisted key is independent of future crypto availability.
    vi.stubGlobal('crypto', {});
    const reopened = renderConfirmation();
    await act(async () => { await reopened.result.current.mutateAsync(reviewedStatus); });
    expect(acknowledge.mock.calls[2][0].idempotencyKey).toBe(key);
    expect(localStorage.getItem(storageKey)).toBe(key);
  });

  it('keeps the same key across automatic mutation retries', async () => {
    acknowledge.mockRejectedValueOnce(new Error('Network Error'));
    const { result } = renderConfirmation(1);

    await act(async () => { await result.current.mutateAsync(reviewedStatus); });

    expect(acknowledge).toHaveBeenCalledTimes(2);
    expect(acknowledge.mock.calls[1][0]).toEqual(acknowledge.mock.calls[0][0]);
    expect(acknowledge.mock.calls[0][0].idempotencyKey).toMatch(uuidV4);
    expect(crypto.getRandomValues).toHaveBeenCalledTimes(1);
  });

  it.each(['HTTP LAN', 'no crypto'] as const)(
    'preserves a stored legacy non-UUID key verbatim with %s',
    async (environment) => {
      const getRandomValues = crypto.getRandomValues;
      const legacyKey = '1789300000000-a1b2c3d4';
      localStorage.setItem(storageKey, legacyKey);
      if (environment === 'no crypto') vi.stubGlobal('crypto', undefined);
      const { result } = renderConfirmation();

      await act(async () => { await result.current.mutateAsync(reviewedStatus); });
      await act(async () => { await result.current.mutateAsync(reviewedStatus); });

      expect(acknowledge).toHaveBeenCalledTimes(2);
      for (const [request] of acknowledge.mock.calls) {
        expect(request.idempotencyKey).toBe(legacyKey);
      }
      expect(localStorage.getItem(storageKey)).toBe(legacyKey);
      expect(getRandomValues).not.toHaveBeenCalled();
    },
  );

  it.each(['nextJobId', 'nextJobETag', 'dispatchStateETag'] as const)(
    'generates a new key only when the reviewed %s changes',
    async (field) => {
      const { result } = renderConfirmation();
      await act(async () => { await result.current.mutateAsync(reviewedStatus); });
      const firstKey = acknowledge.mock.calls[0][0].idempotencyKey;

      await act(async () => {
        await result.current.mutateAsync({ ...reviewedStatus, [field]: 'new-revision' });
      });

      const nextKey = acknowledge.mock.calls[1][0].idempotencyKey;
      expect(nextKey).toMatch(uuidV4);
      expect(nextKey).not.toBe(firstKey);
      expect(localStorage.getItem(storageKey)).toBe(firstKey);
      expect(crypto.getRandomValues).toHaveBeenCalledTimes(2);
    },
  );

  it('surfaces missing secure randomness through mutation error handling without sending or storing a key', async () => {
    vi.stubGlobal('crypto', {});
    const { result } = renderConfirmation();

    act(() => { result.current.mutate(reviewedStatus); });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).toHaveBeenCalledWith(
      expect.stringContaining('no cryptographically secure random source available'),
    );
    expect(acknowledge).not.toHaveBeenCalled();
    expect(localStorage.getItem(storageKey)).toBeNull();
  });
});
