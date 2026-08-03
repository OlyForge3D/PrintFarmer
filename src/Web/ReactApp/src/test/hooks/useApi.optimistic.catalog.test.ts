import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook } from '@testing-library/react';
import { waitFor } from '@testing-library/dom';
import { act } from '@testing-library/react';
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { queryKeys, useCancelPrintQueueJob, useCreateManufacturer, useCreateModel, useDeletePrintQueueJob, useQueuePrintJob } from '@/common/hooks/useApi';
import { apiClient } from '@/services/api';
import { PrinterModelDto, QueuedPrintJobWithFileMetaDto } from '@/types/api';

function createClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}
const wrapperFactory = (client: QueryClient) => ({ children }: { children: React.ReactNode }) => React.createElement(QueryClientProvider, { client }, children);

/** A promise the test controls, so a request can be held open deliberately. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('optimistic manufacturer/model creation', () => {
  it('manufacturer optimistic create then replace', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    const createSpy = vi.spyOn(apiClient, 'createManufacturer').mockImplementation(async (name: string) => {
      await new Promise(r => setTimeout(r, 5));
      return { id: 'm-real', name };
    });
    const { result } = renderHook(() => useCreateManufacturer(), { wrapper });

    await act(async () => {
      result.current.mutate('Acme');
    });

    const tempList = client.getQueryData<{ id: string; name: string }[]>(queryKeys.manufacturers);
    expect(tempList?.some(m => m.id.startsWith('temp-') && m.name === 'Acme')).toBe(true);

    await waitFor(() => {
      const list = client.getQueryData<{ id: string; name: string }[]>(queryKeys.manufacturers);
      expect(list?.some(m => m.id === 'm-real')).toBe(true);
    });

    const finalList = client.getQueryData<{ id: string; name: string }[]>(queryKeys.manufacturers)!;
    expect(finalList.some(m => m.id.startsWith('temp-'))).toBe(false);
    expect(createSpy).toHaveBeenCalledOnce();
  });

  it('model optimistic create then replace', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    const manufacturerId = 'mfg-1';
    client.setQueryData(queryKeys.models(manufacturerId), [] as PrinterModelDto[]);

    // The optimistic entry is asserted below while the request is still in
    // flight. A `setTimeout` would let the mutation settle inside `act()` under
    // load, so the window is held open explicitly instead.
    const gate = deferred<PrinterModelDto>();
    const createSpy = vi
      .spyOn(apiClient, 'createModel')
      .mockImplementation(() => gate.promise);

    const { result } = renderHook(() => useCreateModel(), { wrapper });

    await act(async () => {
      result.current.mutate({ name: 'MK4', manufacturerId });
    });

    const tempModels = client.getQueryData<PrinterModelDto[]>(queryKeys.models(manufacturerId));
    expect(tempModels?.some(m => m.id.startsWith('temp-') && m.name === 'MK4')).toBe(true);

    await act(async () => {
      gate.resolve({ id: 'model-real', name: 'MK4', manufacturerId } as PrinterModelDto);
    });

    await waitFor(() => {
      const models = client.getQueryData<PrinterModelDto[]>(queryKeys.models(manufacturerId));
      expect(models?.some(m => m.id === 'model-real')).toBe(true);
    });

    const finalModels = client.getQueryData<PrinterModelDto[]>(queryKeys.models(manufacturerId))!;
    expect(finalModels.some(m => m.id.startsWith('temp-'))).toBe(false);
    expect(createSpy).toHaveBeenCalledOnce();
  });

  it('rolls back model list on error', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    const manufacturerId = 'mfg-err';
    client.setQueryData(queryKeys.models(manufacturerId), [] as PrinterModelDto[]);

    // Same held-open window as above: the rollback must not be allowed to run
    // before the optimistic entry is observed.
    const gate = deferred<PrinterModelDto>();
    vi.spyOn(apiClient, 'createModel').mockImplementation(() => gate.promise);

    const { result } = renderHook(() => useCreateModel(), { wrapper });
    await act(async () => { result.current.mutate({ name: 'Bad', manufacturerId }); });

    const tempModels = client.getQueryData<PrinterModelDto[]>(queryKeys.models(manufacturerId));
    expect(tempModels?.some(m => m.id.startsWith('temp-'))).toBe(true);

    await act(async () => {
      gate.reject(new Error('fail'));
      await gate.promise.catch(() => {});
    });

    await waitFor(() => {
      const finalModels = client.getQueryData<PrinterModelDto[]>(queryKeys.models(manufacturerId));
      expect(finalModels?.some(m => m.id.startsWith('temp-'))).toBe(false);
    });
  });
});

describe('optimistic job cancel/delete', () => {
  const createMockJob = (id: string): QueuedPrintJobWithFileMetaDto => ({
    job: {
      id,
      rowVersion: 'reviewed-etag',
      name: 'Test Job',
      gcodeFileId: 'f',
      fileName: 'f.gcode',
      assignedPrinterId: 'p1',
      status: 'Queued',
      priority: 0,
      queuePosition: 1,
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      queuedAtUtc: new Date().toISOString(),
      copies: 1,
      completedCopies: 0,
      remainingCopies: 1,
    },
  });

  const seedJobs = (client: QueryClient, jobs: QueuedPrintJobWithFileMetaDto[]) => {
    client.setQueryData(queryKeys.jobQueue(), jobs);
  };

  it('cancel job marks status and keeps after success', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    const job = createMockJob('job-x');
    seedJobs(client, [job]);
    vi.spyOn(apiClient, 'cancelPrintQueueJob').mockImplementation(async () => { await new Promise(r => setTimeout(r, 5)); });

    const { result } = renderHook(() => useCancelPrintQueueJob(), { wrapper });
    await act(async () => {
      result.current.mutate({
        jobId: 'job-x',
        reviewedRowVersion: 'reviewed-etag',
      });
    });

    const interim = client.getQueryData<QueuedPrintJobWithFileMetaDto[]>(queryKeys.jobQueue());
    expect(interim?.find(j => j.job.id === 'job-x')?.job.status).toBe('Cancelled');
  });

  it('delete job removes from list', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    const job = createMockJob('job-y');
    seedJobs(client, [job]);

    vi.spyOn(apiClient, 'deletePrintQueueJob').mockImplementation(async () => { await new Promise(r => setTimeout(r, 5)); });

    const { result } = renderHook(() => useDeletePrintQueueJob(), { wrapper });
    await act(async () => {
      result.current.mutate({
        jobId: 'job-y',
        reviewedRowVersion: 'reviewed-etag',
      });
    });

    const interim = client.getQueryData<QueuedPrintJobWithFileMetaDto[]>(queryKeys.jobQueue());
    expect(interim?.some(j => j.job.id === 'job-y')).toBe(false);
  });

  it('queue job rollback on error', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    let rejectRequest!: (reason: Error) => void;
    const pendingRequest = new Promise<never>((_resolve, reject) => {
      rejectRequest = reject;
    });
    vi.spyOn(apiClient, 'queuePrintJob').mockReturnValue(pendingRequest);
    const { result } = renderHook(() => useQueuePrintJob(), { wrapper });
    await act(async () => { result.current.mutate({ printerId: 'p-err', gcodeFileId: 'f1' }); });
    const key = queryKeys.jobQueue('p-err');
    const temp = client.getQueryData<QueuedPrintJobWithFileMetaDto[]>(key);
    expect(temp?.some(j => j.job.id.startsWith('temp-'))).toBe(true);
    await act(async () => {
      rejectRequest(new Error('queue fail'));
    });
    await waitFor(() => {
      const after = client.getQueryData<QueuedPrintJobWithFileMetaDto[]>(key);
      expect(after?.some(j => j.job.id.startsWith('temp-'))).toBe(false);
    });
  });

  it('cancel job rollback on error', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    const job = createMockJob('job-cancel-err');
    client.setQueryData(queryKeys.jobQueue(), [job]);
    // #1028: this test asserts the transient optimistic state *before* the
    // rollback. A fixed `setTimeout(5)` gives it no guarantee — under load the
    // 5ms timer fires inside `act` and the rollback has already run, so the
    // interim read returns 'Queued'. Holding the request open until the test
    // releases it makes the window explicit instead of hoping for it.
    const request = deferred<void>();
    vi.spyOn(apiClient, 'cancelPrintQueueJob').mockImplementation(() => request.promise);
    const { result } = renderHook(() => useCancelPrintQueueJob(), { wrapper });
    await act(async () => {
      result.current.mutate({
        jobId: 'job-cancel-err',
        reviewedRowVersion: 'reviewed-etag',
      });
    });
    const interim = client.getQueryData<QueuedPrintJobWithFileMetaDto[]>(queryKeys.jobQueue());
    expect(interim?.find(j => j.job.id === 'job-cancel-err')?.job.status).toBe('Cancelled');
    await act(async () => {
      request.reject(new Error('cancel fail'));
      await request.promise.catch(() => {});
    });
    await waitFor(() => {
      const after = client.getQueryData<QueuedPrintJobWithFileMetaDto[]>(queryKeys.jobQueue());
      expect(after?.find(j => j.job.id === 'job-cancel-err')?.job.status).toBe('Queued');
    });
  });

  it('delete job rollback on error', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    const job = createMockJob('job-del-err');
    client.setQueryData(queryKeys.jobQueue(), [job]);
    // Same transient-state race as the cancel case above (#1028).
    const request = deferred<void>();
    vi.spyOn(apiClient, 'deletePrintQueueJob').mockImplementation(() => request.promise);
    const { result } = renderHook(() => useDeletePrintQueueJob(), { wrapper });
    await act(async () => {
      result.current.mutate({
        jobId: 'job-del-err',
        reviewedRowVersion: 'reviewed-etag',
      });
    });
    const interim = client.getQueryData<QueuedPrintJobWithFileMetaDto[]>(queryKeys.jobQueue());
    expect(interim?.some(j => j.job.id === 'job-del-err')).toBe(false);
    await act(async () => {
      request.reject(new Error('delete fail'));
      await request.promise.catch(() => {});
    });
    await waitFor(() => {
      const after = client.getQueryData<QueuedPrintJobWithFileMetaDto[]>(queryKeys.jobQueue());
      expect(after?.some(j => j.job.id === 'job-del-err')).toBe(true);
    });
  });
});
