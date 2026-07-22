import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { NfcTagUnknownEvent } from '@/features/nfc/types';

/* ── Hoisted mock state ── */

const mockState = vi.hoisted(() => {
  let tagUnknownHandler: ((event: NfcTagUnknownEvent) => void) | null = null;
  let connectionHandler: ((connected: boolean) => void) | null = null;
  let connected = false;

  return {
    get tagUnknownHandler() { return tagUnknownHandler; },
    set tagUnknownHandler(v) { tagUnknownHandler = v; },
    get connectionHandler() { return connectionHandler; },
    set connectionHandler(v) { connectionHandler = v; },
    get connected() { return connected; },
    set connected(v: boolean) { connected = v; },

    ensureConnected: vi.fn(async () => { connected = true; }),
    isConnected: vi.fn(() => connected),

    onTagUnknown: vi.fn((cb: (event: NfcTagUnknownEvent) => void) => {
      tagUnknownHandler = cb;
      return () => { tagUnknownHandler = null; };
    }),
    onTagRead: vi.fn(() => () => {}),
    onConnectionChanged: vi.fn((cb: (connected: boolean) => void) => {
      connectionHandler = cb;
      return () => { connectionHandler = null; };
    }),
  };
});

vi.mock('@/services/nfcHubService', () => ({
  nfcHubService: {
    ensureConnected: mockState.ensureConnected,
    isConnected: mockState.isConnected,
    onTagUnknown: mockState.onTagUnknown,
    onTagRead: mockState.onTagRead,
    onConnectionChanged: mockState.onConnectionChanged,
  },
}));

function makeTagEvent(overrides: Partial<NfcTagUnknownEvent> = {}): NfcTagUnknownEvent {
  return {
    tagUid: 'AABBCCDD',
    readAt: new Date().toISOString(),
    ...overrides,
  };
}

describe('useNfcPairingSession', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockState.tagUnknownHandler = null;
    mockState.connectionHandler = null;
    mockState.connected = false;
  });

  it('starts with modal closed and no tag event', async () => {
    // Lazy import to pick up fresh mock
    const { useNfcPairingSession } = await import('@/features/nfc/hooks/useNfcPairingSession');
    const { result } = renderHook(() => useNfcPairingSession());

    expect(result.current.isOpen).toBe(false);
    expect(result.current.tagEvent).toBeNull();
    expect(result.current.isUnavailable).toBe(false);
  });

  it('calls ensureConnected on mount', async () => {
    const { useNfcPairingSession } = await import('@/features/nfc/hooks/useNfcPairingSession');
    renderHook(() => useNfcPairingSession());
    expect(mockState.ensureConnected).toHaveBeenCalledOnce();
  });

  it('opens modal and captures tag when nfctagunknown fires', async () => {
    const { useNfcPairingSession } = await import('@/features/nfc/hooks/useNfcPairingSession');
    const { result } = renderHook(() => useNfcPairingSession());

    const tagEvent = makeTagEvent({ tagUid: 'DEADBEEF', printerId: 'printer-1' });

    act(() => {
      mockState.tagUnknownHandler?.(tagEvent);
    });

    expect(result.current.isOpen).toBe(true);
    expect(result.current.tagEvent).toEqual(tagEvent);
    expect(result.current.isUnavailable).toBe(false);
  });

  it('replaces tag event when a second nfctagunknown fires', async () => {
    const { useNfcPairingSession } = await import('@/features/nfc/hooks/useNfcPairingSession');
    const { result } = renderHook(() => useNfcPairingSession());

    act(() => { mockState.tagUnknownHandler?.(makeTagEvent({ tagUid: 'FIRST' })); });
    act(() => { mockState.tagUnknownHandler?.(makeTagEvent({ tagUid: 'SECOND' })); });

    expect(result.current.tagEvent?.tagUid).toBe('SECOND');
  });

  it('startScanning opens modal without a tag event', async () => {
    const { useNfcPairingSession } = await import('@/features/nfc/hooks/useNfcPairingSession');
    const { result } = renderHook(() => useNfcPairingSession());

    act(() => { result.current.startScanning(); });

    expect(result.current.isOpen).toBe(true);
    expect(result.current.tagEvent).toBeNull();
  });

  it('close resets isOpen, tagEvent, and isUnavailable', async () => {
    const { useNfcPairingSession } = await import('@/features/nfc/hooks/useNfcPairingSession');
    const { result } = renderHook(() => useNfcPairingSession());

    act(() => { mockState.tagUnknownHandler?.(makeTagEvent()); });
    expect(result.current.isOpen).toBe(true);

    act(() => { result.current.close(); });

    expect(result.current.isOpen).toBe(false);
    expect(result.current.tagEvent).toBeNull();
    expect(result.current.isUnavailable).toBe(false);
  });

  it('sets isUnavailable when hub drops while modal is open', async () => {
    const { useNfcPairingSession } = await import('@/features/nfc/hooks/useNfcPairingSession');
    const { result } = renderHook(() => useNfcPairingSession());

    // Open the modal first
    act(() => { result.current.startScanning(); });
    expect(result.current.isOpen).toBe(true);

    // Hub drops
    act(() => { mockState.connectionHandler?.(false); });

    expect(result.current.isUnavailable).toBe(true);
    expect(result.current.isConnected).toBe(false);
  });

  it('does NOT set isUnavailable when hub drops while modal is closed', async () => {
    const { useNfcPairingSession } = await import('@/features/nfc/hooks/useNfcPairingSession');
    const { result } = renderHook(() => useNfcPairingSession());

    // Modal is closed (default)
    expect(result.current.isOpen).toBe(false);

    act(() => { mockState.connectionHandler?.(false); });

    expect(result.current.isUnavailable).toBe(false);
    expect(result.current.isConnected).toBe(false);
  });

  it('clears isUnavailable and re-opens when a new tag fires after reconnect', async () => {
    const { useNfcPairingSession } = await import('@/features/nfc/hooks/useNfcPairingSession');
    const { result } = renderHook(() => useNfcPairingSession());

    // Open → drop → tag arrives again
    act(() => { result.current.startScanning(); });
    act(() => { mockState.connectionHandler?.(false); });
    expect(result.current.isUnavailable).toBe(true);

    act(() => { mockState.tagUnknownHandler?.(makeTagEvent({ tagUid: 'RECONNECTED' })); });

    expect(result.current.isUnavailable).toBe(false);
    expect(result.current.tagEvent?.tagUid).toBe('RECONNECTED');
  });

  it('unsubscribes from hub events on unmount', async () => {
    const { useNfcPairingSession } = await import('@/features/nfc/hooks/useNfcPairingSession');
    const { unmount } = renderHook(() => useNfcPairingSession());

    unmount();

    // After unmount, firing an event should have no active handler
    expect(mockState.tagUnknownHandler).toBeNull();
    expect(mockState.connectionHandler).toBeNull();
  });
});
