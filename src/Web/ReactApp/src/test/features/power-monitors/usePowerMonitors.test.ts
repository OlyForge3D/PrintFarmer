import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React from 'react';
import type { PowerMonitor, PowerMonitorTestResult } from '../../../features/power-monitors/types';

const MONITOR: PowerMonitor = {
  id: '1',
  printerId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
  printerName: 'Voron 2.4',
  provider: 'Kasa',
  deviceAddress: '192.168.1.50',
  electricityRatePerKwh: 0.12,
  enabled: true,
};

const TEST_RESULT: PowerMonitorTestResult = {
  success: true,
  message: 'Connected',
  currentWatts: 95.3,
};

vi.mock('../../../services/api', () => ({
  apiClient: {
    get: vi.fn(),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
  },
}));

import { apiClient } from '../../../services/api';

import {
  usePowerMonitors,
  useCreatePowerMonitor,
  useUpdatePowerMonitor,
  useDeletePowerMonitor,
  useTestPowerMonitorConnection,
} from '../../../features/power-monitors/hooks/usePowerMonitors';

function wrapper({ children }: { children: React.ReactNode }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return React.createElement(QueryClientProvider, { client: qc }, children);
}

describe('usePowerMonitors', () => {
  beforeEach(() => vi.clearAllMocks());

  it('fetches from GET /admin/power-monitors', async () => {
    vi.mocked(apiClient.get).mockResolvedValueOnce({ data: [MONITOR] } as never);

    const { result } = renderHook(() => usePowerMonitors(), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(apiClient.get).toHaveBeenCalledWith('/admin/power-monitors');
    expect(result.current.data).toEqual([MONITOR]);
  });
});

describe('useCreatePowerMonitor', () => {
  beforeEach(() => vi.clearAllMocks());

  it('posts to POST /admin/power-monitors', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: MONITOR } as never);

    const { result } = renderHook(() => useCreatePowerMonitor(), { wrapper });

    result.current.mutate({
      printerId: MONITOR.printerId,
      provider: 'Kasa',
      deviceAddress: '192.168.1.50',
      electricityRatePerKwh: 0.12,
      enabled: true,
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(apiClient.post).toHaveBeenCalledWith('/admin/power-monitors', expect.objectContaining({
      deviceAddress: '192.168.1.50',
    }));
  });
});

describe('useUpdatePowerMonitor', () => {
  beforeEach(() => vi.clearAllMocks());

  it('puts to PUT /admin/power-monitors/{id}', async () => {
    vi.mocked(apiClient.put).mockResolvedValueOnce({ data: MONITOR } as never);

    const { result } = renderHook(() => useUpdatePowerMonitor(), { wrapper });

    result.current.mutate({
      id: '1',
      dto: {
        printerId: MONITOR.printerId,
        provider: 'Kasa',
        deviceAddress: '192.168.1.50',
        enabled: true,
      },
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(apiClient.put).toHaveBeenCalledWith('/admin/power-monitors/1', expect.objectContaining({
      deviceAddress: '192.168.1.50',
    }));
  });
});

describe('useDeletePowerMonitor', () => {
  beforeEach(() => vi.clearAllMocks());

  it('deletes DELETE /admin/power-monitors/{id}', async () => {
    vi.mocked(apiClient.delete).mockResolvedValueOnce({ data: undefined } as never);

    const { result } = renderHook(() => useDeletePowerMonitor(), { wrapper });

    result.current.mutate('1');

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(apiClient.delete).toHaveBeenCalledWith('/admin/power-monitors/1');
  });
});

describe('useTestPowerMonitorConnection', () => {
  beforeEach(() => vi.clearAllMocks());

  it('posts to POST /admin/power-monitors/test', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: TEST_RESULT } as never);

    const { result } = renderHook(() => useTestPowerMonitorConnection(), { wrapper });

    result.current.mutate({ provider: 'Kasa', deviceAddress: '192.168.1.50' });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(apiClient.post).toHaveBeenCalledWith('/admin/power-monitors/test', {
      provider: 'Kasa',
      deviceAddress: '192.168.1.50',
    });
    expect(result.current.data).toEqual(TEST_RESULT);
  });

  it('surfaces success=false without throwing', async () => {
    const failResult: PowerMonitorTestResult = { success: false, message: 'Device did not respond' };
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: failResult } as never);

    const { result } = renderHook(() => useTestPowerMonitorConnection(), { wrapper });

    result.current.mutate({ provider: 'Tasmota', deviceAddress: '10.0.0.5' });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.success).toBe(false);
    expect(result.current.data?.message).toBe('Device did not respond');
  });
});
