import { describe, it, expect, vi } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React from 'react';
import { renderHook, act, waitFor } from '@testing-library/react';
import { useCreatePrinter, useQueuePrintJob, queryKeys } from '../../hooks/useApi';
import { apiClient } from '../../services/api';
import { PrinterBackend, JobQueueStatus, Printer, JobQueuePrintJob } from '../../types/api';

// Utility to create a fresh QueryClient per test
function createTestClient() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false } }
  });
}

function wrapperFactory(client: QueryClient) {
  return ({ children }: { children: React.ReactNode }) => React.createElement(QueryClientProvider, { client }, children);
}

describe('optimistic printer create', () => {
  it('adds temp printer immediately and replaces after success', async () => {
    const client = createTestClient();
    const wrapper = wrapperFactory(client);

  const finalPrinter: Partial<Printer> & { id: string } = {
      id: 'real-123',
      name: 'My Printer',
      serverUrl: 'http://p.local',
      notes: undefined,
      isOnline: false,
      isReachable: false,
      backend: PrinterBackend.Moonraker,
      originalServerUrl: 'http://p.local',
      state: 'Unknown'
  }; // minimal shape used in test

    const createSpy = vi.spyOn(apiClient, 'createPrinter').mockImplementation(async () => {
      // simulate network delay
      await new Promise(r => setTimeout(r, 5));
      return finalPrinter;
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

    // Wait a bit for rejection processing
    await new Promise(r => setTimeout(r, 15));

    // Should rollback (empty list or undefined)
  const postList = client.getQueryData<Printer[]>(queryKeys.printers);
    if (postList) {
      expect(postList.every(p => !p.id.startsWith('temp-'))).toBe(true);
    }
  });
});

describe('optimistic queue print job', () => {
  it('inserts temp job then replaces with real job', async () => {
    const client = createTestClient();
    const wrapper = wrapperFactory(client);

    const printerId = 'printer-1';
  const realJob: Partial<JobQueuePrintJob> & { id: string; printerId: string; gcodeFileId: string; gcodeFileName: string; status: JobQueueStatus; priority: number; queuedAt: Date; createdAt: Date; updatedAt: Date } = {
      id: 'job-123',
      printerId,
      gcodeFileId: 'file-9',
      gcodeFileName: 'cube.gcode',
      status: JobQueueStatus.Pending,
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

    // Temp job exists
  const tempQueue = client.getQueryData<JobQueuePrintJob[]>(printerQueueKey);
    expect(tempQueue?.some(j => j.id.startsWith('temp-'))).toBe(true);

    // Wait for replacement
    await waitFor(() => {
  const list = client.getQueryData<JobQueuePrintJob[]>(printerQueueKey);
      expect(list?.some(j => j.id === 'job-123')).toBe(true);
    });

  const finalQueue = client.getQueryData<JobQueuePrintJob[]>(printerQueueKey)!;
    expect(finalQueue.some(j => j.id.startsWith('temp-'))).toBe(false);
    expect(queueSpy).toHaveBeenCalledTimes(1);
  });
});
