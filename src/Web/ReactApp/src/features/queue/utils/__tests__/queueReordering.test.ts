import { describe, expect, it } from 'vitest';
import {
  canReorderQueueView,
  canDropQueueJob,
  getQueueJobOrdinal,
  getQueueMoveNeighbors,
  moveQueuedJobInList,
} from '@/features/queue/utils/queueReordering';
import type { QueuedPrintJobWithFileMetaDto } from '@/types/api';
import { PrintJobPriority } from '@/types/api';

function createJob(
  id: string,
  scope: string | null,
  overrides: Partial<QueuedPrintJobWithFileMetaDto['job']> = {},
): QueuedPrintJobWithFileMetaDto {
  return {
    job: {
      id,
      rowVersion: `etag-${id}`,
      name: id,
      assignedPrinterId: scope ?? undefined,
      status: 'Queued',
      priority: overrides.priority ?? PrintJobPriority.Normal,
      queuePosition: 1,
      copies: 1,
      completedCopies: 0,
      remainingCopies: 1,
      createdAtUtc: '2026-01-01T00:00:00Z',
      updatedAtUtc: '2026-01-01T00:00:00Z',
      queuedAtUtc: '2026-01-01T00:00:00Z',
      ...overrides,
    },
    gcodeFile: {
      id: `file-${id}`,
      name: `${id}.gcode`,
      fileName: `${id}.gcode`,
      fileSizeBytes: 1,
      createdAtUtc: '2026-01-01T00:00:00Z',
    },
    assignedPrinter: scope
      ? { id: scope, name: scope, modelName: 'model', status: 'online', isOnline: true }
      : undefined,
  };
}

describe('queue reorder helpers', () => {
  it('enables reorder only for authorized, unfiltered active priority queues', () => {
    const base = {
      canWrite: true,
      isLoading: false,
      activeTab: 'print-queue',
      sortBy: 'priority',
      statusFilter: null,
      modelFilter: null,
      materialFilter: null,
    };

    expect(canReorderQueueView(base)).toBe(true);
    expect(canReorderQueueView({ ...base, canWrite: false })).toBe(false);
    expect(canReorderQueueView({ ...base, isLoading: true })).toBe(false);
    expect(canReorderQueueView({ ...base, activeTab: 'history' })).toBe(false);
    expect(canReorderQueueView({ ...base, sortBy: 'deadline' })).toBe(false);
    expect(canReorderQueueView({ ...base, statusFilter: 'Queued' })).toBe(false);
    expect(canReorderQueueView({ ...base, modelFilter: 'printer-model' })).toBe(false);
    expect(canReorderQueueView({ ...base, materialFilter: 'PLA' })).toBe(false);
  });

  it('finds queued neighbors within each printer and Any printer priority group, skipping pinned rows', () => {
    const anyOne = createJob('any-1', null);
    const printing = createJob('printing', 'printer-1', { status: 'Printing' });
    const assigned = createJob('assigned', 'printer-1', { status: 'Assigned' });
    const printerOne = createJob('printer-1-a', 'printer-1');
    const anyTwo = createJob('any-2', null);
    const printerOneOtherPriority = createJob('printer-1-high', 'printer-1', {
      priority: PrintJobPriority.Urgent,
    });
    const printerOneNext = createJob('printer-1-b', 'printer-1');
    const neighbors = getQueueMoveNeighbors([
      anyOne,
      printing,
      assigned,
      printerOne,
      anyTwo,
      printerOneOtherPriority,
      printerOneNext,
    ]);

    expect(neighbors.get('printer-1-a')).toEqual({ next: printerOneNext });
    expect(neighbors.get('printer-1-b')).toEqual({ previous: printerOne });
    expect(neighbors.get('printer-1-high')).toEqual({});
    expect(neighbors.get('any-1')).toEqual({ next: anyTwo });
    expect(neighbors.get('any-2')).toEqual({ previous: anyOne });
    expect(neighbors.has('printing')).toBe(false);
    expect(neighbors.has('assigned')).toBe(false);
  });

  it('moves only queued slots in the same scope and priority without changing priority', () => {
    const anyPrinter = createJob('any-1', null);
    const first = createJob('first', 'printer-1', { priority: PrintJobPriority.Urgent });
    const pinned = createJob('printing', 'printer-1', { status: 'Printing' });
    const differentPriority = createJob('different-priority', 'printer-1');
    const second = createJob('second', 'printer-1', { priority: PrintJobPriority.Urgent });
    const otherPrinter = createJob('other-printer', 'printer-2');
    const jobs = [anyPrinter, first, pinned, differentPriority, second, otherPrinter];
    const reordered = moveQueuedJobInList(jobs, 'second', 'first', 'before');

    expect(reordered?.map(({ job }) => job.id)).toEqual([
      'any-1',
      'second',
      'printing',
      'different-priority',
      'first',
      'other-printer',
    ]);
    expect(reordered?.[1].job.priority).toBe(first.job.priority);
    expect(reordered?.[2]).toBe(pinned);
    expect(reordered?.[3]).toBe(differentPriority);
    expect(reordered?.[5]).toBe(otherPrinter);
    expect(getQueueJobOrdinal(reordered ?? [], 'second')).toBe(1);
  });

  it('rejects cross-scope, self, pinned, inconsistent, and already-adjacent moves', () => {
    const first = createJob('first', 'printer-1', { priority: PrintJobPriority.Urgent });
    const second = createJob('second', 'printer-1', { priority: PrintJobPriority.Low });
    const any = createJob('any', null);
    const printing = createJob('printing', 'printer-1', { status: 'Printing' });
    const inconsistent = createJob('inconsistent', 'printer-1');
    inconsistent.assignedPrinter = {
      id: 'printer-2',
      name: 'Printer 2',
      modelName: 'model',
      status: 'online',
      isOnline: true,
    };
    const jobs = [first, second, any, printing, inconsistent];

    expect(canDropQueueJob(jobs, 'first', 'any')).toBe(false);
    expect(canDropQueueJob(jobs, 'first', 'printing')).toBe(false);
    expect(canDropQueueJob(jobs, 'first', 'first')).toBe(false);
    expect(canDropQueueJob(jobs, 'inconsistent', 'first')).toBe(false);
    expect(canDropQueueJob(jobs, 'first', 'second')).toBe(false);
    expect(moveQueuedJobInList(jobs, 'first', 'second', 'before')).toBeUndefined();
  });
});
