import { client } from '@/services/api/httpClient';
import type {
  QueueChangeFeed,
  QueueChangeWatermark,
  QueueSubscriptionResources,
} from '@/types/api';

/**
 * Job-queue change-feed API used by the SignalR realtime bridge
 * (`printer-signalr.ts`, `QueueRealtimeBridge.tsx`). Both are statically
 * imported by `App.tsx`, so this module calls the shared axios client
 * directly rather than delegating to the `ApiClient` monolith, keeping it
 * out of that monolith's eager import graph. See issue #2343.
 */

function isAxiosLikeError(
  error: unknown
): error is { isAxiosError: true; response?: { status: number; data?: unknown } } {
  return (
    error !== null &&
    typeof error === 'object' &&
    (error as { isAxiosError?: unknown }).isAxiosError === true
  );
}

export async function getQueueChanges(
  afterSequence = 0,
  limit = 100
): Promise<QueueChangeFeed> {
  try {
    const response = await client.get<QueueChangeFeed>('/job-queue/changes', {
      params: { afterSequence, limit },
    });
    return response.data;
  } catch (error) {
    // 410 Gone: the requested cursor is older than the retention window.
    // The server still returns a structured body (error: "cursor_expired",
    // currentSequence) — surface it as a QueueChangeFeed with expired=true so
    // callers resynchronize instead of treating this like a network failure.
    if (isAxiosLikeError(error) && error.response?.status === 410) {
      const body = error.response.data as { currentSequence?: number } | undefined;
      const currentSequence = body?.currentSequence ?? afterSequence;
      return {
        afterSequence,
        nextSequence: currentSequence,
        hasMore: false,
        events: [],
        expired: true,
        currentSequence,
      };
    }
    throw error;
  }
}

export async function getQueueSubscriptionResources(): Promise<QueueSubscriptionResources> {
  const response = await client.get<QueueSubscriptionResources>(
    '/job-queue/subscription-resources'
  );
  return response.data;
}

/**
 * Fetches the current outbox watermark so the SignalR client can seed its
 * change-feed cursor at connect time instead of replaying the entire
 * durable outbox history from sequence 0 on every fresh page load
 * (issue #1727).
 */
export async function getQueueChangeWatermark(): Promise<QueueChangeWatermark> {
  const response = await client.get<QueueChangeWatermark>(
    '/job-queue/changes/watermark'
  );
  return response.data;
}
