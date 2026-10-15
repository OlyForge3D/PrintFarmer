import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/services/api';
import { useQueueReordering } from '@/features/queue/hooks/useQueueReordering';
import type { QueuedPrintJobWithFileMetaDto } from '@/types/api';
import { PrintJobPriority } from '@/types/api';

function createJob(
  id: string,
  overrides: Partial<QueuedPrintJobWithFileMetaDto['job']> = {},
): QueuedPrintJobWithFileMetaDto {
  return {
    job: {
      id,
      rowVersion: `etag-${id}`,
      name: id,
      assignedPrinterId: 'printer-1',
      status: 'Queued',
      priority: PrintJobPriority.Normal,
      queuePosition: id === 'job-a' ? 1 : 2,
      copies: 1,
      completedCopies: 0,
      remainingCopies: 1,
      createdAtUtc: '2026-01-01T00:00:00Z',
      updatedAtUtc: '2026-01-01T00:00:00Z',
      queuedAtUtc: '2026-01-01T00:00:00Z',
      ...overrides,
    },
    assignedPrinter: {
      id: 'printer-1',
      name: 'Printer One',
      modelName: 'model',
      status: 'online',
      isOnline: true,
    },
    gcodeFile: {
      id: `file-${id}`,
      name: `${id}.gcode`,
      fileName: `${id}.gcode`,
      fileSizeBytes: 1,
      createdAtUtc: '2026-01-01T00:00:00Z',
    },
  };
}

const queryKey = ['queue-jobs', null, null, null, 'priority'] as const;

function renderQueueHook(
  jobs: QueuedPrintJobWithFileMetaDto[],
  onRefresh = vi.fn().mockResolvedValue(undefined),
  onError = vi.fn(),
) {
  return renderHook(
    ({ currentJobs }) =>
      useQueueReordering({
        jobs: currentJobs,
        queryKey,
        enabled: true,
        onRefresh,
        onError,
      }),
    { initialProps: { currentJobs: jobs } },
  );
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('useQueueReordering', () => {
  it('sends the moved-row revision and neighbor ETag, then announces the refreshed position', async () => {
    const jobs = [createJob('job-a'), createJob('job-b')];
    const move = vi.spyOn(apiClient, 'moveQueuedJob').mockResolvedValue(
      createJob('job-b').job,
    );
    const onRefresh = vi.fn().mockResolvedValue(undefined);
    const onError = vi.fn();
    const { result } = renderQueueHook(jobs, onRefresh, onError);

    await act(async () => {
      await result.current.moveJob('job-b', 'job-a', 'before');
    });

    expect(move).toHaveBeenCalledWith(
      'job-b',
      { beforeJobId: 'job-a', beforeJobETag: 'etag-job-a' },
      'etag-job-b',
    );
    expect(onRefresh).toHaveBeenCalledOnce();
    expect(result.current.jobs.map(({ job }) => job.id)).toEqual(['job-a', 'job-b']);
    expect(result.current.announcement).toContain('position 1 in Printer One');
    expect(onError).toHaveBeenLastCalledWith(null);
  });

  it.each([
    [412, /Queue changed — refreshed/],
    [409, /Queue changed — refreshed/],
    [400, /invalid neighbor/],
  ])('refreshes and reports an HTTP %i move failure', async (status, expectedMessage) => {
    const jobs = [createJob('job-a'), createJob('job-b')];
    vi.spyOn(apiClient, 'moveQueuedJob').mockRejectedValue({
      response: { status },
      message: 'invalid neighbor',
    });
    const onRefresh = vi.fn().mockResolvedValue(undefined);
    const onError = vi.fn();
    const { result } = renderQueueHook(jobs, onRefresh, onError);

    await act(async () => {
      await result.current.moveJob('job-b', 'job-a', 'before');
    });

    expect(onRefresh).toHaveBeenCalledOnce();
    expect(result.current.jobs).toEqual(jobs);
    expect(result.current.isMoving).toBe(false);
    expect(onError).toHaveBeenLastCalledWith(expect.stringMatching(expectedMessage));
  });

  it('does not let an authoritative update arriving during a move get hidden by optimistic state', async () => {
    const jobs = [createJob('job-a'), createJob('job-b')];
    let resolveMove!: (value: QueuedPrintJobWithFileMetaDto['job']) => void;
    const move = vi.spyOn(apiClient, 'moveQueuedJob').mockReturnValue(
      new Promise((resolve) => {
        resolveMove = resolve;
      }),
    );
    const { result, rerender } = renderQueueHook(jobs);

    let operation!: Promise<void>;
    act(() => {
      operation = result.current.moveJob('job-b', 'job-a', 'before');
    });
    await waitFor(() => expect(result.current.isMoving).toBe(true));
    expect(result.current.jobs.map(({ job }) => job.id)).toEqual(['job-b', 'job-a']);

    const serverUpdate = [
      createJob('job-a', { status: 'Printing', rowVersion: 'etag-a-new' }),
      createJob('job-b', { rowVersion: 'etag-b-new' }),
    ];
    rerender({ currentJobs: serverUpdate });
    expect(result.current.jobs).toEqual(serverUpdate);

    await act(async () => {
      resolveMove(createJob('job-b', { rowVersion: 'etag-b-moved' }).job);
      await operation;
    });
    expect(move).toHaveBeenCalledOnce();
    expect(result.current.jobs).toEqual(serverUpdate);
  });
});
