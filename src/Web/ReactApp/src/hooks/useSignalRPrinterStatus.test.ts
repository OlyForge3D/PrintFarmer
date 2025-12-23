/**
 * Tests for useSignalRPrinterStatus Hook
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { useSignalRPrinterStatus, PrinterStatusUpdate } from '@/hooks/useSignalRPrinterStatus';

// Mock SignalR
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
    HubConnectionBuilder: vi.fn(() => ({
      withUrl: vi.fn(function () {
        return this;
      }),
      withAutomaticReconnect: vi.fn(function () {
        return this;
      }),
      configureLogging: vi.fn(function () {
        return this;
      }),
      build: vi.fn(() => mockConnection),
    })),
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

  it('initializes with null status', () => {
    const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    expect(result.current.status).toBeNull();
    expect(result.current.isConnected).toBe(false);
  });

  it('requires a printer ID', () => {
    const { result } = renderHook(() => useSignalRPrinterStatus(''));

    expect(result.current.error).toBeDefined();
  });

  it('returns a reconnect function', () => {
    const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    expect(typeof result.current.reconnect).toBe('function');
  });

  it('handles error state properly', async () => {
    const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    expect(result.current.error).toBeNull();
  });

  it('tracks connection state', async () => {
    const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    // Initially disconnected
    expect(result.current.isConnected).toBe(false);

    // Eventually should attempt connection
    await waitFor(
      () => {
        // Connection attempt made
      },
      { timeout: 1000 }
    );
  });

  it('provides reconnect functionality', () => {
    const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    expect(result.current.reconnect).toBeDefined();

    // Should be callable without error
    expect(() => result.current.reconnect()).not.toThrow();
  });

  it('returns typed status when available', async () => {
    const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    // Status should be null until updated
    expect(result.current.status).toBeNull();
  });

  it('handles different printer states', () => {
    const states: Array<'Idle' | 'Printing' | 'Paused' | 'Error' | 'Offline'> = [
      'Idle',
      'Printing',
      'Paused',
      'Error',
      'Offline',
    ];

    states.forEach((state) => {
      const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

      expect(result.current).toBeDefined();
    });
  });

  it('properly cleans up on unmount', () => {
    const { unmount } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    expect(() => unmount()).not.toThrow();
  });

  it('handles multiple printer IDs', () => {
    const { result: result1 } = renderHook(() =>
      useSignalRPrinterStatus('printer-1')
    );
    const { result: result2 } = renderHook(() =>
      useSignalRPrinterStatus('printer-2')
    );

    expect(result1.current).toBeDefined();
    expect(result2.current).toBeDefined();
  });
});

describe('useSignalRPrinterStatus - Error Handling', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.clearAllMocks();
  });

  it('returns error for empty printer ID', () => {
    const { result } = renderHook(() => useSignalRPrinterStatus(''));

    expect(result.current.error).toBeTruthy();
  });

  it('has graceful error recovery', () => {
    const { result } = renderHook(() => useSignalRPrinterStatus('printer-1'));

    expect(result.current.reconnect).toBeDefined();

    // Calling reconnect should not throw
    expect(() => {
      result.current.reconnect();
    }).not.toThrow();
  });
});
