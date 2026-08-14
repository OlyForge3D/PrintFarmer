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
    vi.spyOn(printerSignalRService, 'subscribeToPrinter').mockResolvedValue();
    vi.spyOn(printerSignalRService, 'isConnected', 'get').mockReturnValue(true);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('joins each requested printer group so cached and live status updates are delivered', async () => {
    const printerIds = ['printer-a', 'printer-b'];

    const { rerender } = renderHook(() =>
      usePrinterStatusUpdates(undefined, printerIds)
    );

    await waitFor(() => {
      expect(printerSignalRService.subscribeToPrinter).toHaveBeenCalledTimes(2);
    });
    expect(printerSignalRService.subscribeToPrinter).toHaveBeenCalledWith('printer-a');
    expect(printerSignalRService.subscribeToPrinter).toHaveBeenCalledWith('printer-b');

    rerender();
    expect(printerSignalRService.subscribeToPrinter).toHaveBeenCalledTimes(2);
  });
});
