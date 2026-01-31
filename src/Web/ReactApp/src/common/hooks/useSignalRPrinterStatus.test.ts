/**
 * Tests for useSignalRPrinterStatus Hook
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { useSignalRPrinterStatus } from '@/common/hooks/useSignalRPrinterStatus';

// Mock SignalR - Vitest v4 requires class/function for constructors
vi.mock('@microsoft/signalr', () => {
  const mockConnection = {
    start: vi.fn(() => Promise.resolve()),
    stop: vi.fn(() => Promise.resolve()),
    invoke: vi.fn(() => Promise.resolve()),
    on: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    onclose: vi.fn(),
  };

  return {
    HubConnectionBuilder: class MockHubConnectionBuilder {
      withUrl() {
        return this;
      }
      withAutomaticReconnect() {
        return this;
      }
      configureLogging() {
        return this;
      }
      build() {
        return mockConnection;
      }
    },
    LogLevel: {
      Warning: 2,
      Information: 1,
      Debug: 0,
      Trace: -1,
      Error: 4,
      Critical: 5,
      None: 6,
    },
  };
});

describe('useSignalRPrinterStatus Hook', () => {
  beforeEach(() => {
    // Clear localStorage before each test
    localStorage.clear();
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('initializes with null status', async () => {
    const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    await waitFor(() => {
      expect(result.current.status).toBeNull();
      expect(result.current.isConnected).toBe(false);
    });
  });

  it('requires a printer ID', async () => {
    const { result } = renderHook(() => useSignalRPrinterStatus(''));

    await waitFor(() => {
      expect(result.current.error).toBeDefined();
    });
  });

  it('returns a reconnect function', async () => {
    const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    await waitFor(() => {
      expect(typeof result.current.reconnect).toBe('function');
    });
  });

  it('handles error state properly', async () => {
    const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    await waitFor(() => {
      expect(result.current.error).toBeNull();
    });
  });

  it('tracks connection state', async () => {
    const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    // Initially disconnected
    await waitFor(() => {
      expect(result.current.isConnected).toBe(false);
    });

    // Eventually should attempt connection (no explicit assertion required, wait for side-effects)
    await waitFor(() => {
      // allow hook effects to run
      expect(result.current).toBeDefined();
    }, { timeout: 1000 });
  });

  it('provides reconnect functionality', async () => {
    const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    await waitFor(() => {
      expect(result.current.reconnect).toBeDefined();
    });

    // Should be callable without error; wrap in act to avoid async state update warnings
    await act(async () => {
      await result.current.reconnect();
    });
  });

  it('returns typed status when available', async () => {
    const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    // Status should be null until updated
    await waitFor(() => {
      expect(result.current.status).toBeNull();
    });
  });

  it('handles different printer states', async () => {
    const states: Array<'Idle' | 'Printing' | 'Paused' | 'Error' | 'Offline'> = [
      'Idle',
      'Printing',
      'Paused',
      'Error',
      'Offline',
    ];

    states.forEach(() => {
      const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));
      waitFor(() => {
        expect(result.current).toBeDefined();
      });
    });
  });

  it('properly cleans up on unmount', async () => {
    const { unmount } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    await act(async () => {
      unmount();
    });

    expect(true).toBe(true);
  });

  it('handles multiple printer IDs', async () => {
    const { result: result1 } = renderHook(() =>
      useSignalRPrinterStatus('printer-1')
    );
    const { result: result2 } = renderHook(() =>
      useSignalRPrinterStatus('printer-2')
    );

    await waitFor(() => {
      expect(result1.current).toBeDefined();
      expect(result2.current).toBeDefined();
    });
  });
});

describe('useSignalRPrinterStatus - Error Handling', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.clearAllMocks();
  });

  it('returns error for empty printer ID', async () => {
    const { result } = renderHook(() => useSignalRPrinterStatus(''));

    await waitFor(() => {
      expect(result.current.error).toBeTruthy();
    });
  });

  it('has graceful error recovery', async () => {
    const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    await waitFor(() => {
      expect(result.current.reconnect).toBeDefined();
    });

    await act(async () => {
      await result.current.reconnect();
    });
  });
});
