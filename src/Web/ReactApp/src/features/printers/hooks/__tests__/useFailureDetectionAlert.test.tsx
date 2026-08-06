import '@testing-library/jest-dom';
import { renderHook, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { FailureDetectionEvent } from '@/types/api';

const hoisted = vi.hoisted(() => ({
  listeners: [] as Array<(event: FailureDetectionEvent) => void>,
  onFailureDetected: vi.fn(),
}));

vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    onFailureDetected: hoisted.onFailureDetected,
  },
}));

import {
  useFailureDetectionAlert,
  __resetFailureDetectionAlertStoreForTests,
} from '../useFailureDetectionAlert';

function emit(event: FailureDetectionEvent) {
  act(() => {
    hoisted.listeners.forEach((listener) => listener(event));
  });
}

function makeEvent(overrides: Partial<FailureDetectionEvent> = {}): FailureDetectionEvent {
  return {
    printerId: 'printer-1',
    printerName: 'Printer One',
    confidence: 0.9,
    detectedAt: '2026-01-01T00:00:00Z',
    autoPaused: false,
    ...overrides,
  };
}

describe('useFailureDetectionAlert hoisted store (#1146 item 3)', () => {
  beforeEach(() => {
    hoisted.listeners.length = 0;
    hoisted.onFailureDetected.mockReset();
    hoisted.onFailureDetected.mockImplementation((callback: (event: FailureDetectionEvent) => void) => {
      hoisted.listeners.push(callback);
      return () => {
        const index = hoisted.listeners.indexOf(callback);
        if (index >= 0) hoisted.listeners.splice(index, 1);
      };
    });
    __resetFailureDetectionAlertStoreForTests();
  });

  afterEach(() => {
    vi.useRealTimers();
    __resetFailureDetectionAlertStoreForTests();
  });

  it('registers exactly one SignalR handler no matter how many cards mount ("one handler per grid")', () => {
    const cardCount = 25;
    const hooks = Array.from({ length: cardCount }, (_, i) =>
      renderHook(() => useFailureDetectionAlert(`printer-${i}`))
    );

    expect(hoisted.onFailureDetected).toHaveBeenCalledTimes(1);

    hooks.forEach((hook) => hook.unmount());
  });

  it('unregisters the single SignalR handler only after the last card unmounts', () => {
    const first = renderHook(() => useFailureDetectionAlert('printer-1'));
    const second = renderHook(() => useFailureDetectionAlert('printer-2'));
    expect(hoisted.onFailureDetected).toHaveBeenCalledTimes(1);
    expect(hoisted.listeners).toHaveLength(1);

    first.unmount();
    expect(hoisted.listeners).toHaveLength(1); // still subscribed — second card is mounted

    second.unmount();
    expect(hoisted.listeners).toHaveLength(0); // last consumer gone — unsubscribed

    // A brand-new mount after full teardown re-subscribes exactly once more.
    renderHook(() => useFailureDetectionAlert('printer-3'));
    expect(hoisted.onFailureDetected).toHaveBeenCalledTimes(2);
  });

  it("routes an event to only the matching printer's hook instance", () => {
    const cardA = renderHook(() => useFailureDetectionAlert('printer-a'));
    const cardB = renderHook(() => useFailureDetectionAlert('printer-b'));

    emit(makeEvent({ printerId: 'printer-a' }));

    expect(cardA.result.current.event?.printerId).toBe('printer-a');
    expect(cardB.result.current.event).toBeNull();
  });

  it('dedupes recentEvents by incident key and caps them at 5, matching previous per-card semantics', () => {
    const card = renderHook(() => useFailureDetectionAlert('printer-a'));

    for (let i = 0; i < 6; i += 1) {
      emit(makeEvent({ printerId: 'printer-a', detectedAt: `2026-01-01T00:0${i}:00Z` }));
    }

    expect(card.result.current.recentEvents).toHaveLength(5);
    expect(card.result.current.recentEvents[0].detectedAt).toBe('2026-01-01T00:05:00Z');
  });

  it('clearEvent dismisses only the current alert, preserving recentEvents (dismissal semantics)', () => {
    const card = renderHook(() => useFailureDetectionAlert('printer-a'));
    emit(makeEvent({ printerId: 'printer-a' }));
    expect(card.result.current.event).not.toBeNull();

    act(() => card.result.current.clearEvent());

    expect(card.result.current.event).toBeNull();
    expect(card.result.current.recentEvents).toHaveLength(1);
  });

  it('expires the active alert after 60 seconds without any interaction (expiry semantics)', () => {
    vi.useFakeTimers();
    const card = renderHook(() => useFailureDetectionAlert('printer-a'));
    emit(makeEvent({ printerId: 'printer-a' }));
    expect(card.result.current.event).not.toBeNull();

    act(() => {
      vi.advanceTimersByTime(60_000);
    });

    expect(card.result.current.event).toBeNull();
    // recentEvents (the incident history) is untouched by expiry.
    expect(card.result.current.recentEvents).toHaveLength(1);
  });

  it('uses one expiry timer per active alert, not one per mounted card', () => {
    vi.useFakeTimers();
    const setTimeoutSpy = vi.spyOn(globalThis, 'setTimeout');

    // 10 idle cards mounted — no events emitted, so no timers should exist.
    const idleCards = Array.from({ length: 10 }, (_, i) =>
      renderHook(() => useFailureDetectionAlert(`printer-idle-${i}`))
    );
    expect(setTimeoutSpy).not.toHaveBeenCalled();

    // One event for one printer schedules exactly one expiry timer.
    emit(makeEvent({ printerId: 'printer-idle-0' }));
    expect(setTimeoutSpy).toHaveBeenCalledTimes(1);

    idleCards.forEach((hook) => hook.unmount());
    setTimeoutSpy.mockRestore();
  });

  it('a later event for the same printer resets the expiry window instead of stacking timers', () => {
    vi.useFakeTimers();
    const card = renderHook(() => useFailureDetectionAlert('printer-a'));

    emit(makeEvent({ printerId: 'printer-a', detectedAt: '2026-01-01T00:00:00Z' }));
    act(() => {
      vi.advanceTimersByTime(40_000);
    });
    // Second event arrives before the first would have expired.
    emit(makeEvent({ printerId: 'printer-a', detectedAt: '2026-01-01T00:00:40Z' }));
    act(() => {
      vi.advanceTimersByTime(40_000); // 80s total — first alert's original 60s window has passed
    });

    // Still visible: the second event reset the 60s countdown at t=40s, so it
    // expires at t=100s, not t=60s.
    expect(card.result.current.event?.detectedAt).toBe('2026-01-01T00:00:40Z');

    act(() => {
      vi.advanceTimersByTime(20_000); // t=100s
    });
    expect(card.result.current.event).toBeNull();
  });
});