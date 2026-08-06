import '@testing-library/jest-dom';
import React from 'react';
import { renderHook } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import {
  FailureDetectionPollingProvider,
  useFailureDetectionPollingEnabled,
} from '../useFailureDetectionPolling';

/**
 * Coverage for the fleet-level "is any printer Obico/failure-detection
 * enabled" gate (#1146 item 3). `useFailureDetectionAlert.test.tsx` already
 * proves the SignalR alert store is hoisted to one subscription/timer per
 * grid; this file covers the separate, previously-untested gate that
 * `CompactPrinterCard`/`DetailedPrinterCard` read to decide whether their
 * shared `usePrinterFailureDetectionStatus` poll should run at all, instead
 * of each card computing that from just its own `printer.obicoEnabled`.
 */
describe('useFailureDetectionPolling (#1146 item 3)', () => {
  it('defaults to false with no provider in the tree, matching the previous "no explicit opt-in" behavior', () => {
    const { result } = renderHook(() => useFailureDetectionPollingEnabled());

    expect(result.current).toBe(false);
  });

  it('returns true when a provider supplies true', () => {
    const { result } = renderHook(() => useFailureDetectionPollingEnabled(), {
      wrapper: ({ children }) => (
        <FailureDetectionPollingProvider value={true}>{children}</FailureDetectionPollingProvider>
      ),
    });

    expect(result.current).toBe(true);
  });

  it('returns false when a provider explicitly supplies false', () => {
    const { result } = renderHook(() => useFailureDetectionPollingEnabled(), {
      wrapper: ({ children }) => (
        <FailureDetectionPollingProvider value={false}>{children}</FailureDetectionPollingProvider>
      ),
    });

    expect(result.current).toBe(false);
  });

  it('shares the identical fleet-wide value across multiple consumers under the same provider', () => {
    function Consumers() {
      const a = useFailureDetectionPollingEnabled();
      const b = useFailureDetectionPollingEnabled();
      return { a, b };
    }

    const { result } = renderHook(() => Consumers(), {
      wrapper: ({ children }) => (
        <FailureDetectionPollingProvider value={true}>{children}</FailureDetectionPollingProvider>
      ),
    });

    // Two independent hook calls (standing in for two different mounted
    // cards) must read the exact same fleet-wide decision, not each derive
    // their own — the whole point of hoisting this above per-card state.
    expect(result.current.a).toBe(true);
    expect(result.current.b).toBe(true);
  });

  it('a nested provider value overrides an outer one for its subtree (standard context scoping)', () => {
    const { result } = renderHook(() => useFailureDetectionPollingEnabled(), {
      wrapper: ({ children }) => (
        <FailureDetectionPollingProvider value={true}>
          <FailureDetectionPollingProvider value={false}>{children}</FailureDetectionPollingProvider>
        </FailureDetectionPollingProvider>
      ),
    });

    expect(result.current).toBe(false);
  });
});
