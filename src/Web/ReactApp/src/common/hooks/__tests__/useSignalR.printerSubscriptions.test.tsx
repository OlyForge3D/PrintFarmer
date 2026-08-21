import { renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { usePrinterStatusUpdates } from '@/common/hooks/useSignalR';
import { printerSignalRService } from '@/services/printer-signalr';

describe('usePrinterStatusUpdates printer subscriptions', () => {
  beforeEach(() => {
    vi.spyOn(printerSignalRService, 'connect').mockResolvedValue();
    vi.spyOn(printerSignalRService, 'getLastStatuses').mockReturnValue(new Map());
    vi.spyOn(printerSignalRService, 'onPrinterStatusUpdate').mockReturnValue(() => undefined);
    vi.spyOn(printerSignalRService, 'onConnectionStateChange').mockReturnValue(() => undefined);
    vi.spyOn(printerSignalRService, 'subscribeToPrinters').mockResolvedValue(new Set());
    vi.spyOn(printerSignalRService, 'isConnected', 'get').mockReturnValue(true);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('batches all requested printers into a single subscribeToPrinters call', async () => {
    const printerIds = ['printer-a', 'printer-b'];

    const { rerender } = renderHook(() =>
      usePrinterStatusUpdates(undefined, printerIds)
    );

    await waitFor(() => {
      expect(printerSignalRService.subscribeToPrinters).toHaveBeenCalledTimes(1);
    });
    expect(printerSignalRService.subscribeToPrinters).toHaveBeenCalledWith([
      'printer-a',
      'printer-b',
    ]);

    rerender();
    expect(printerSignalRService.subscribeToPrinters).toHaveBeenCalledTimes(1);
  });

  it('does not surface a batched invoke failure as an unhandled rejection', async () => {
    const unhandledRejections: unknown[] = [];
    const onUnhandledRejection = (event: PromiseRejectionEvent) => {
      unhandledRejections.push(event.reason);
    };
    window.addEventListener('unhandledrejection', onUnhandledRejection);

    vi.spyOn(printerSignalRService, 'subscribeToPrinters').mockRejectedValue(
      new Error('transient invoke failure')
    );
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => undefined);

    const printerIds = ['printer-a'];
    renderHook(() => usePrinterStatusUpdates(undefined, printerIds));

    await waitFor(() => {
      expect(printerSignalRService.subscribeToPrinters).toHaveBeenCalledTimes(1);
    });
    // Flush the rejected promise's microtask queue.
    await Promise.resolve();
    await Promise.resolve();

    expect(warnSpy).toHaveBeenCalled();
    expect(unhandledRejections).toHaveLength(0);

    window.removeEventListener('unhandledrejection', onUnhandledRejection);
  });
});
