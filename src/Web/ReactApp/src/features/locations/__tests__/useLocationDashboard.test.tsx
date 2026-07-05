import '@testing-library/jest-dom';
import React from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import type { LocationSubtreePrinter, Printer } from '@/types/api';
import { apiClient } from '@/services/api';
import { computeStats, useLocationPrinters } from '../hooks/useLocationDashboard';

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinters: vi.fn(),
    getLocationSubtreePrinters: vi.fn(),
  },
}));

function wrapper({ children }: { children: React.ReactNode }) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });

  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

function makeSubtreePrinter(overrides: Partial<LocationSubtreePrinter>): LocationSubtreePrinter {
  return {
    printerId: 'printer-1',
    printerName: 'Printer 1',
    locationId: 'loc-1',
    locationName: 'Rack 1',
    isOnline: true,
    status: 'Idle',
    currentJobName: null,
    ...overrides,
  };
}

function makePrinter(overrides: Partial<Printer>): Printer {
  return {
    id: 'printer-1',
    name: 'Printer 1',
    backend: 0,
    backendUrl: 'http://printer.local',
    isOnline: true,
    isReachable: true,
    state: 'Idle',
    ...overrides,
  } as Printer;
}

describe('useLocationDashboard', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('computes stats from real subtree status and isOnline fields', () => {
    const stats = computeStats([
      makeSubtreePrinter({ printerId: 'printing', status: 'Printing', currentJobName: 'gearbox.gcode' }),
      makeSubtreePrinter({ printerId: 'idle', status: 'Idle' }),
      makeSubtreePrinter({ printerId: 'paused', status: 'Paused' }),
      makeSubtreePrinter({ printerId: 'offline', isOnline: false, status: 'Offline' }),
    ]);

    expect(stats).toMatchObject({
      totalPrinters: 4,
      online: 3,
      offline: 1,
      printing: 1,
      idle: 1,
      attention: 2,
      activeJobs: 1,
    });
  });

  it('loads all printers for All Locations so unassigned printers are included once', async () => {
    vi.mocked(apiClient.getPrinters).mockResolvedValue([
      makePrinter({
        id: 'assigned',
        name: 'Assigned Printer',
        location: { id: 'loc-1', name: 'Rack 1' },
        state: 'Printing',
        jobName: 'assigned.gcode',
      }),
      makePrinter({
        id: 'unassigned',
        name: 'Unassigned Printer',
        location: undefined,
        isOnline: false,
        state: 'Offline',
      }),
    ]);

    const { result } = renderHook(() => useLocationPrinters(null), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(apiClient.getPrinters).toHaveBeenCalledTimes(1);
    expect(apiClient.getLocationSubtreePrinters).not.toHaveBeenCalled();
    expect(result.current.data).toEqual([
      expect.objectContaining({
        printerId: 'assigned',
        printerName: 'Assigned Printer',
        locationId: 'loc-1',
        locationName: 'Rack 1',
        status: 'Printing',
        currentJobName: 'assigned.gcode',
      }),
      expect.objectContaining({
        printerId: 'unassigned',
        printerName: 'Unassigned Printer',
        locationId: null,
        locationName: null,
        status: 'Offline',
      }),
    ]);
    expect(computeStats(result.current.data ?? [])).toMatchObject({
      totalPrinters: 2,
      online: 1,
      offline: 1,
      printing: 1,
      attention: 1,
    });
  });
});
