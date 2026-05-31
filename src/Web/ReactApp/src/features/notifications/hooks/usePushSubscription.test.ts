import { renderHook, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { usePushSubscription } from './usePushSubscription';

function mockPushManager(existingSubscription: PushSubscription | null) {
  const pushManager = {
    getSubscription: vi.fn().mockResolvedValue(existingSubscription),
    subscribe: vi.fn(),
  };
  const registration = { pushManager };

  Object.defineProperty(navigator, 'serviceWorker', {
    value: { ready: Promise.resolve(registration) },
    writable: true,
    configurable: true,
  });
  Object.defineProperty(window, 'PushManager', {
    value: class {},
    writable: true,
    configurable: true,
  });
  Object.defineProperty(window, 'Notification', {
    value: { requestPermission: vi.fn() },
    writable: true,
    configurable: true,
  });

  return { pushManager, registration };
}

describe('usePushSubscription', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('sets isSubscribed to true on mount when browser has existing subscription', async () => {
    const fakeSubscription = { endpoint: 'https://push.example.com/sub1' } as unknown as PushSubscription;
    mockPushManager(fakeSubscription);

    const { result } = renderHook(() => usePushSubscription());

    await waitFor(() => {
      expect(result.current.isSubscribed).toBe(true);
    });
  });

  it('keeps isSubscribed false on mount when no existing subscription', async () => {
    mockPushManager(null);

    const { result } = renderHook(() => usePushSubscription());

    await waitFor(() => {
      expect(result.current.isSubscribed).toBe(false);
    });
    expect(result.current.isSupported).toBe(true);
  });

  it('reports isSupported false when PushManager is unavailable', () => {
    // Remove PushManager from window
    const original = Object.getOwnPropertyDescriptor(window, 'PushManager');
    // @ts-expect-error — intentionally removing for test
    delete (window as Record<string, unknown>).PushManager;

    const { result } = renderHook(() => usePushSubscription());
    expect(result.current.isSupported).toBe(false);
    expect(result.current.isSubscribed).toBe(false);

    // Restore
    if (original) {
      Object.defineProperty(window, 'PushManager', original);
    }
  });
});
