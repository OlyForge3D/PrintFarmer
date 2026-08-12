import { describe, it, expect, vi, afterEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React from 'react';
import { renderHook } from '@testing-library/react';
import { waitFor } from '@testing-library/dom';
import { act } from '@testing-library/react';
import { useCreatePrinter, useQueuePrintJob, useUpdatePrinter, queryKeys } from '@/common/hooks/useApi';
import { apiClient } from '@/services/api';
import { PrinterBackend, Printer, QueuedPrintJobWithFileMetaDto } from '@/types/api';

// Utility to create a fresh QueryClient per test
function createTestClient() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false } }
  });
}

function wrapperFactory(client: QueryClient) {
  return ({ children }: { children: React.ReactNode }) => React.createElement(QueryClientProvider, { client }, children);
}

/**
 * #1028: every test here spies on the same `apiClient` method, and `vi.spyOn`
 * returns the *existing* mock when the property is already spied — so without a
 * restore the call history accumulates across tests. `toHaveBeenCalledTimes(1)`
 * then depends on this file's execution order, which is why the suite went red
 * under `--sequence.shuffle` (3/8 runs) while passing in definition order.
 */
afterEach(() => {
  vi.restoreAllMocks();
});

describe('optimistic printer create', () => {
  it('adds temp printer immediately and replaces after success', async () => {
    const client = createTestClient();
    const wrapper = wrapperFactory(client);



    const createSpy = vi.spyOn(apiClient, 'createPrinter').mockImplementation(async () => {
      // simulate network delay
      await new Promise(r => setTimeout(r, 5));
      // Return a fully-typed Printer object
      return {
        id: 'real-123',
        name: 'My Printer',
        serverUrl: 'http://p.local',
        notes: undefined,
        isOnline: false,
        isReachable: false,
        backend: PrinterBackend.Moonraker,
        originalServerUrl: 'http://p.local',
        state: 'Unknown',
        // Add any other required fields for Printer type here
      } as Printer;
    });

    const { result } = renderHook(() => useCreatePrinter(), { wrapper });

    await act(async () => {
      result.current.mutate({
        name: 'My Printer',
        serverUrl: 'http://p.local',
        backend: PrinterBackend.Moonraker
      });
    });

    // Temp printer should exist quickly
  const tempList = client.getQueryData<Printer[]>(queryKeys.printers);
    expect(tempList).toBeTruthy();
    const temp = tempList!.find(p => p.id.startsWith('temp-'));
    expect(temp).toBeTruthy();

    // Wait for replacement
    await waitFor(() => {
  const list = client.getQueryData<Printer[]>(queryKeys.printers);
      expect(list?.some(p => p.id === 'real-123')).toBe(true);
    });

  const finalList = client.getQueryData<Printer[]>(queryKeys.printers)!;
    expect(finalList.some(p => p.id.startsWith('temp-'))).toBe(false);
    expect(createSpy).toHaveBeenCalledTimes(1);
  });

  it('rolls back on error', async () => {
    const client = createTestClient();
    const wrapper = wrapperFactory(client);

    vi.spyOn(apiClient, 'createPrinter').mockImplementation(async () => {
      await new Promise(r => setTimeout(r, 5));
      throw new Error('boom');
    });

    const { result } = renderHook(() => useCreatePrinter(), { wrapper });

    await act(async () => {
      result.current.mutate({
        name: 'Err Printer',
        serverUrl: 'http://err.local',
        backend: PrinterBackend.Moonraker
      });
    });

    // Temp should appear first
  const tempList = client.getQueryData<Printer[]>(queryKeys.printers);
    expect(tempList?.some(p => p.id.startsWith('temp-'))).toBe(true);

    // Should rollback (temp removed) eventually
    await waitFor(() => {
      const postList = client.getQueryData<Printer[]>(queryKeys.printers);
      // success if list gone or no temp items remain
      const hasTemp = postList?.some(p => p.id.startsWith('temp-')) ?? false;
      expect(hasTemp).toBe(false);
    });
  });
});

describe('optimistic printer update', () => {
  it('preserves client URL fields omitted from the mutation response', async () => {
    const client = createTestClient();
    const wrapper = wrapperFactory(client);
    const existing = {
      id: 'printer-1',
      name: 'Original name',
      frontendUrl: 'http://printer.local',
      backendUrl: 'http://printer.local:7125',
      backend: PrinterBackend.Moonraker,
    } as Printer;
    const updated = {
      id: 'printer-1',
      name: 'Updated name',
      backend: PrinterBackend.Moonraker,
    } as Printer;

    client.setQueryData(queryKeys.printers, [existing]);
    client.setQueryData(queryKeys.printer(existing.id), existing);
    vi.spyOn(apiClient, 'updatePrinter').mockResolvedValue(updated);

    const { result } = renderHook(() => useUpdatePrinter(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync({
        id: existing.id,
        printer: { name: updated.name, backend: PrinterBackend.Moonraker },
        reviewedRowVersion: 'reviewed-version',
      });
    });

    const listPrinter = client.getQueryData<Printer[]>(queryKeys.printers)?.[0];
    const detailPrinter = client.getQueryData<Printer>(queryKeys.printer(existing.id));
    expect(listPrinter).toMatchObject({
      name: 'Updated name',
      frontendUrl: existing.frontendUrl,
      backendUrl: existing.backendUrl,
    });
    expect(detailPrinter).toMatchObject({
      name: 'Updated name',
      frontendUrl: existing.frontendUrl,
      backendUrl: existing.backendUrl,
    });
  });
});

describe('optimistic queue print job', () => {
  it('inserts temp job then replaces with real job', async () => {
    const client = createTestClient();
    const wrapper = wrapperFactory(client);

    const printerId = 'printer-1';
    const realJob = {
      id: 'job-123',
      printerId,
      gcodeFileId: 'file-9',
      gcodeFileName: 'cube.gcode',
      status: 0, // Pending
      priority: 0,
      queuedAt: new Date(),
      createdAt: new Date(),
      updatedAt: new Date()
    };

    const queueSpy = vi.spyOn(apiClient, 'queuePrintJob').mockImplementation(async () => {
      await new Promise(r => setTimeout(r, 5));
      return realJob;
    });

    const { result } = renderHook(() => useQueuePrintJob(), { wrapper });

    await act(async () => {
      result.current.mutate({ printerId, gcodeFileId: 'file-9', priority: 0 });
    });

    const printerQueueKey = queryKeys.jobQueue(printerId);

    // Temp job exists - now uses QueuedPrintJobWithFileMetaDto structure
    const tempQueue = client.getQueryData<QueuedPrintJobWithFileMetaDto[]>(printerQueueKey);
    expect(tempQueue?.some(j => j.job.id.startsWith('temp-'))).toBe(true);

    // After success, the hook invalidates queries which will refetch.
    // For tests, we just verify the temp was added and the mutation succeeds
    await waitFor(() => {
      expect(queueSpy).toHaveBeenCalledTimes(1);
    });
  });
});
