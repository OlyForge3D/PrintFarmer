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

  it('ignores a failure event for a printer with no mounted listener (no timer scheduled, no state resurrected later)', () => {
    vi.useFakeTimers();
    const setTimeoutSpy = vi.spyOn(globalThis, 'setTimeout');
    // Only printer-a is watched; printer-unobserved has no mounted card.
    renderHook(() => useFailureDetectionAlert('printer-a'));

    emit(makeEvent({ printerId: 'printer-unobserved' }));

    // No timer was scheduled for the unobserved event.
    expect(setTimeoutSpy).not.toHaveBeenCalled();

    // A card mounting afterward for that same printer must not see a stale
    // alert resurrected from the ignored event.
    const late = renderHook(() => useFailureDetectionAlert('printer-unobserved'));
    expect(late.result.current.event).toBeNull();
    expect(late.result.current.recentEvents).toHaveLength(0);

    setTimeoutSpy.mockRestore();
  });

  it("clears a printer's expiry timer and alert entry immediately once its last listener unmounts, instead of waiting out the 60s expiry", () => {
    vi.useFakeTimers();
    const clearTimeoutSpy = vi.spyOn(globalThis, 'clearTimeout');
    const card = renderHook(() => useFailureDetectionAlert('printer-a'));
    emit(makeEvent({ printerId: 'printer-a' }));
    expect(card.result.current.event).not.toBeNull();
    clearTimeoutSpy.mockClear();

    card.unmount();

    // The pending 60s expiry timer was torn down early, not left to fire on
    // its own after nothing is listening anymore.
    expect(clearTimeoutSpy).toHaveBeenCalledTimes(1);

    // A brand-new mount for the same printer must not resurrect the stale
    // alert that existed before the last listener left.
    const remount = renderHook(() => useFailureDetectionAlert('printer-a'));
    expect(remount.result.current.event).toBeNull();
    expect(remount.result.current.recentEvents).toHaveLength(0);

    clearTimeoutSpy.mockRestore();
  });

  it('defensively clears every remaining timer and alert entry once the final subscriber across the whole store unmounts', () => {
    vi.useFakeTimers();
    const clearTimeoutSpy = vi.spyOn(globalThis, 'clearTimeout');
    const cardA = renderHook(() => useFailureDetectionAlert('printer-a'));
    const cardB = renderHook(() => useFailureDetectionAlert('printer-b'));
    emit(makeEvent({ printerId: 'printer-a' }));
    emit(makeEvent({ printerId: 'printer-b' }));
    clearTimeoutSpy.mockClear();

    cardA.unmount();
    // printer-b is still mounted/listened, so only printer-a's timer clears.
    expect(clearTimeoutSpy).toHaveBeenCalledTimes(1);

    cardB.unmount();
    // The final subscriber anywhere just left — the defensive full-store
    // clear runs (a no-op for printer-b's already-cleared-by-itself timer,
    // but proves nothing was left dangling).
    expect(hoisted.listeners).toHaveLength(0);

    const freshA = renderHook(() => useFailureDetectionAlert('printer-a'));
    const freshB = renderHook(() => useFailureDetectionAlert('printer-b'));
    expect(freshA.result.current.event).toBeNull();
    expect(freshB.result.current.event).toBeNull();

    clearTimeoutSpy.mockRestore();
  });
});