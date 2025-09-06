import { describe, it, expect, vi, afterEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React from 'react';
import { renderHook, act, waitFor } from '@testing-library/react';
import { useCreateManufacturer, useCreateModel, useCancelJob, useDeleteJob, useQueuePrintJob, queryKeys } from '../../hooks/useApi';
import { apiClient } from '../../services/api';
import { ModelDto, JobQueueStatus, JobQueuePrintJob } from '../../types/api';

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
    client.setQueryData(queryKeys.models(manufacturerId), [] as ModelDto[]);

    const createSpy = vi.spyOn(apiClient, 'createModel').mockImplementation(async (model: Omit<ModelDto, 'id'>) => {
      await new Promise(r => setTimeout(r, 5));
      return { id: 'model-real', ...model } as ModelDto;
    });

    const { result } = renderHook(() => useCreateModel(), { wrapper });

    await act(async () => {
      result.current.mutate({ name: 'MK4', manufacturerId });
    });

    const tempModels = client.getQueryData<ModelDto[]>(queryKeys.models(manufacturerId));
    expect(tempModels?.some(m => m.id.startsWith('temp-') && m.name === 'MK4')).toBe(true);

    await waitFor(() => {
      const models = client.getQueryData<ModelDto[]>(queryKeys.models(manufacturerId));
      expect(models?.some(m => m.id === 'model-real')).toBe(true);
    });

    const finalModels = client.getQueryData<ModelDto[]>(queryKeys.models(manufacturerId))!;
    expect(finalModels.some(m => m.id.startsWith('temp-'))).toBe(false);
    expect(createSpy).toHaveBeenCalledOnce();
  });

  it('rolls back model list on error', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    const manufacturerId = 'mfg-err';
    client.setQueryData(queryKeys.models(manufacturerId), [] as ModelDto[]);

    vi.spyOn(apiClient, 'createModel').mockImplementation(async () => {
      await new Promise(r => setTimeout(r, 5));
      throw new Error('fail');
    });

    const { result } = renderHook(() => useCreateModel(), { wrapper });
    await act(async () => { result.current.mutate({ name: 'Bad', manufacturerId }); });

    const tempModels = client.getQueryData<ModelDto[]>(queryKeys.models(manufacturerId));
    expect(tempModels?.some(m => m.id.startsWith('temp-'))).toBe(true);

    await waitFor(() => {
      const finalModels = client.getQueryData<ModelDto[]>(queryKeys.models(manufacturerId));
      expect(finalModels?.some(m => m.id.startsWith('temp-'))).toBe(false);
    });
  });
});

describe('optimistic job cancel/delete', () => {
  const seedJobs = (client: QueryClient, jobs: JobQueuePrintJob[]) => {
    client.setQueryData(queryKeys.jobQueue(), jobs);
  };

  it('cancel job marks status and keeps after success', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    const job: JobQueuePrintJob = { id: 'job-x', printerId: 'p1', gcodeFileId: 'f', gcodeFileName: 'f.gcode', status: JobQueueStatus.Pending, priority: 0, queuedAt: new Date(), createdAt: new Date(), updatedAt: new Date() } as JobQueuePrintJob;
    seedJobs(client, [job]);
    vi.spyOn(apiClient, 'cancelJob').mockImplementation(async () => { await new Promise(r => setTimeout(r, 5)); });

    const { result } = renderHook(() => useCancelJob(), { wrapper });
    await act(async () => { result.current.mutate('job-x'); });

    const interim = client.getQueryData<JobQueuePrintJob[]>(queryKeys.jobQueue());
    expect(interim?.find(j => j.id === 'job-x')?.status).toBe(JobQueueStatus.Cancelled);
  });

  it('delete job removes from list', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    const job: JobQueuePrintJob = { id: 'job-y', printerId: 'p1', gcodeFileId: 'f', gcodeFileName: 'f.gcode', status: JobQueueStatus.Pending, priority: 0, queuedAt: new Date(), createdAt: new Date(), updatedAt: new Date() } as JobQueuePrintJob;
    seedJobs(client, [job]);

    vi.spyOn(apiClient, 'deleteJob').mockImplementation(async () => { await new Promise(r => setTimeout(r, 5)); });

    const { result } = renderHook(() => useDeleteJob(), { wrapper });
    await act(async () => { result.current.mutate('job-y'); });

    const interim = client.getQueryData<JobQueuePrintJob[]>(queryKeys.jobQueue());
    expect(interim?.some(j => j.id === 'job-y')).toBe(false);
  });

  it('queue job rollback on error', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    vi.spyOn(apiClient, 'queuePrintJob').mockImplementation(async () => { await new Promise(r => setTimeout(r,5)); throw new Error('queue fail'); });
    const { result } = renderHook(() => useQueuePrintJob(), { wrapper });
    await act(async () => { result.current.mutate({ printerId: 'p-err', gcodeFileId: 'f1' }); });
    const key = queryKeys.jobQueue('p-err');
    const temp = client.getQueryData<JobQueuePrintJob[]>(key);
    expect(temp?.some(j => j.id.startsWith('temp-'))).toBe(true);
    await waitFor(() => {
      const after = client.getQueryData<JobQueuePrintJob[]>(key);
      expect(after?.some(j => j.id.startsWith('temp-'))).toBe(false);
    });
  });

  it('cancel job rollback on error', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    const job: JobQueuePrintJob = { id: 'job-cancel-err', printerId: 'p1', gcodeFileId: 'f', gcodeFileName: 'f.gcode', status: JobQueueStatus.Pending, priority: 0, queuedAt: new Date(), createdAt: new Date(), updatedAt: new Date() } as JobQueuePrintJob;
    client.setQueryData(queryKeys.jobQueue(), [job]);
    vi.spyOn(apiClient, 'cancelJob').mockImplementation(async () => { await new Promise(r => setTimeout(r,5)); throw new Error('cancel fail'); });
    const { result } = renderHook(() => useCancelJob(), { wrapper });
    await act(async () => { result.current.mutate('job-cancel-err'); });
    const interim = client.getQueryData<JobQueuePrintJob[]>(queryKeys.jobQueue());
    expect(interim?.find(j => j.id === 'job-cancel-err')?.status).toBe(JobQueueStatus.Cancelled);
    await waitFor(() => {
      const after = client.getQueryData<JobQueuePrintJob[]>(queryKeys.jobQueue());
      expect(after?.find(j => j.id === 'job-cancel-err')?.status).toBe(JobQueueStatus.Pending);
    });
  });

  it('delete job rollback on error', async () => {
    const client = createClient();
    const wrapper = wrapperFactory(client);
    const job: JobQueuePrintJob = { id: 'job-del-err', printerId: 'p1', gcodeFileId: 'f', gcodeFileName: 'f.gcode', status: JobQueueStatus.Pending, priority: 0, queuedAt: new Date(), createdAt: new Date(), updatedAt: new Date() } as JobQueuePrintJob;
    client.setQueryData(queryKeys.jobQueue(), [job]);
    vi.spyOn(apiClient, 'deleteJob').mockImplementation(async () => { await new Promise(r => setTimeout(r,5)); throw new Error('delete fail'); });
    const { result } = renderHook(() => useDeleteJob(), { wrapper });
    await act(async () => { result.current.mutate('job-del-err'); });
    const interim = client.getQueryData<JobQueuePrintJob[]>(queryKeys.jobQueue());
    expect(interim?.some(j => j.id === 'job-del-err')).toBe(false);
    await waitFor(() => {
      const after = client.getQueryData<JobQueuePrintJob[]>(queryKeys.jobQueue());
      expect(after?.some(j => j.id === 'job-del-err')).toBe(true);
    });
  });
});
