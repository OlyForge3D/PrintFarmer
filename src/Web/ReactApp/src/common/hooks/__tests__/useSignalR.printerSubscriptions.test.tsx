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
    vi.spyOn(printerSignalRService, 'subscribeToPrinters').mockResolvedValue();
    vi.spyOn(printerSignalRService, 'isConnected', 'get').mockReturnValue(true);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('subscribes to all requested printers in a single batched call so cached and live status updates are delivered', async () => {
    const printerIds = ['printer-a', 'printer-b'];

    const { rerender } = renderHook(() =>
      usePrinterStatusUpdates(undefined, printerIds)
    );

    await waitFor(() => {
      expect(printerSignalRService.subscribeToPrinters).toHaveBeenCalledTimes(1);
    });
    expect(printerSignalRService.subscribeToPrinters).toHaveBeenCalledWith(printerIds);

    rerender();
    expect(printerSignalRService.subscribeToPrinters).toHaveBeenCalledTimes(1);
  });
});
