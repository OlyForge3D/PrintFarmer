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

    const createSpy = vi.spyOn(apiClient, 'createModel').mockImplementation(async (model: Omit<PrinterModelDto, 'id'>) => {
      await new Promise(r => setTimeout(r, 5));
      return { id: 'model-real', ...model } as PrinterModelDto;
    });

    const { result } = renderHook(() => useCreateModel(), { wrapper });

    await act(async () => {
      result.current.mutate({ name: 'MK4', manufacturerId });
    });

    const tempModels = client.getQueryData<PrinterModelDto[]>(queryKeys.models(manufacturerId));
    expect(tempModels?.some(m => m.id.startsWith('temp-') && m.name === 'MK4')).toBe(true);

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

    vi.spyOn(apiClient, 'createModel').mockImplementation(async () => {
      await new Promise(r => setTimeout(r, 5));
      throw new Error('fail');
    });

    const { result } = renderHook(() => useCreateModel(), { wrapper });
    await act(async () => { result.current.mutate({ name: 'Bad', manufacturerId }); });

    const tempModels = client.getQueryData<PrinterModelDto[]>(queryKeys.models(manufacturerId));
    expect(tempModels?.some(m => m.id.startsWith('temp-'))).toBe(true);

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
    await act(async () => { result.current.mutate('job-x'); });

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
    await act(async () => { result.current.mutate('job-y'); });

    const interim = client.getQueryData<QueuedPrintJobWithFileMetaDto[]>(queryKeys.jobQueue());
    expect(interim?.some(j => j.job.id === 'job-y')).toBe(false);
  });

  it('queue job rollback on error', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    vi.spyOn(apiClient, 'queuePrintJob').mockImplementation(async () => { await new Promise(r => setTimeout(r, 5)); throw new Error('queue fail'); });
    const { result } = renderHook(() => useQueuePrintJob(), { wrapper });
    await act(async () => { result.current.mutate({ printerId: 'p-err', gcodeFileId: 'f1' }); });
    const key = queryKeys.jobQueue('p-err');
    const temp = client.getQueryData<QueuedPrintJobWithFileMetaDto[]>(key);
    expect(temp?.some(j => j.job.id.startsWith('temp-'))).toBe(true);
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
    vi.spyOn(apiClient, 'cancelPrintQueueJob').mockImplementation(async () => { await new Promise(r => setTimeout(r, 5)); throw new Error('cancel fail'); });
    const { result } = renderHook(() => useCancelPrintQueueJob(), { wrapper });
    await act(async () => { result.current.mutate('job-cancel-err'); });
    const interim = client.getQueryData<QueuedPrintJobWithFileMetaDto[]>(queryKeys.jobQueue());
    expect(interim?.find(j => j.job.id === 'job-cancel-err')?.job.status).toBe('Cancelled');
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
    vi.spyOn(apiClient, 'deletePrintQueueJob').mockImplementation(async () => { await new Promise(r => setTimeout(r, 5)); throw new Error('delete fail'); });
    const { result } = renderHook(() => useDeletePrintQueueJob(), { wrapper });
    await act(async () => { result.current.mutate('job-del-err'); });
    const interim = client.getQueryData<QueuedPrintJobWithFileMetaDto[]>(queryKeys.jobQueue());
    expect(interim?.some(j => j.job.id === 'job-del-err')).toBe(false);
    await waitFor(() => {
      const after = client.getQueryData<QueuedPrintJobWithFileMetaDto[]>(queryKeys.jobQueue());
      expect(after?.some(j => j.job.id === 'job-del-err')).toBe(true);
    });
  });
});
