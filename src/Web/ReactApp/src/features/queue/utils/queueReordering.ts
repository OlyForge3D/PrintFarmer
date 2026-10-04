import type { QueuedPrintJobWithFileMetaDto } from '@/types/api';

export type QueueMovePlacement = 'before' | 'after';

export interface QueueMoveNeighbors {
  previous?: QueuedPrintJobWithFileMetaDto;
  next?: QueuedPrintJobWithFileMetaDto;
}

export interface QueueReorderAvailability {
  canWrite: boolean;
  isLoading: boolean;
  activeTab: string;
  sortBy: string;
  statusFilter: string | null;
  modelFilter: string | null;
  materialFilter: string | null;
}

export function canReorderQueueView({
  canWrite,
  isLoading,
  activeTab,
  sortBy,
  statusFilter,
  modelFilter,
  materialFilter,
}: QueueReorderAvailability): boolean {
  return (
    canWrite &&
    !isLoading &&
    activeTab === 'print-queue' &&
    sortBy === 'priority' &&
    statusFilter === null &&
    modelFilter === null &&
    materialFilter === null
  );
}

export function getQueueJobScope(
  jobWrapper: QueuedPrintJobWithFileMetaDto
): string | null | undefined {
  const jobPrinterId = jobWrapper.job.assignedPrinterId ?? null;
  const printerId = jobWrapper.assignedPrinter?.id ?? null;

  if (jobPrinterId === '' || printerId === '') return undefined;
  if (jobPrinterId && printerId && jobPrinterId !== printerId) {
    return undefined;
  }

  return jobPrinterId ?? printerId;
}

export function getQueueMoveNeighbors(
  jobs: QueuedPrintJobWithFileMetaDto[]
): Map<string, QueueMoveNeighbors> {
  const queuedByScopeAndPriority = new Map<
    string | null,
    Map<
      QueuedPrintJobWithFileMetaDto['job']['priority'],
      QueuedPrintJobWithFileMetaDto[]
    >
  >();

  for (const job of jobs) {
    if (job.job.status !== 'Queued') continue;
    const scope = getQueueJobScope(job);
    if (scope === undefined) continue;
    const jobsByPriority = queuedByScopeAndPriority.get(scope) ?? new Map();
    const samePriorityJobs = jobsByPriority.get(job.job.priority) ?? [];
    samePriorityJobs.push(job);
    jobsByPriority.set(job.job.priority, samePriorityJobs);
    queuedByScopeAndPriority.set(scope, jobsByPriority);
  }

  const neighborsByJobId = new Map<string, QueueMoveNeighbors>();
  for (const jobsByPriority of queuedByScopeAndPriority.values()) {
    for (const samePriorityJobs of jobsByPriority.values()) {
      samePriorityJobs.forEach((job, index) => {
        neighborsByJobId.set(job.job.id, {
          previous: samePriorityJobs[index - 1],
          next: samePriorityJobs[index + 1],
        });
      });
    }
  }

  return neighborsByJobId;
}

export function canDropQueueJob(
  jobs: QueuedPrintJobWithFileMetaDto[],
  movedJobId: string,
  neighborJobId: string
): boolean {
  if (movedJobId === neighborJobId) return false;
  const moved = jobs.find((entry) => entry.job.id === movedJobId);
  const neighbor = jobs.find((entry) => entry.job.id === neighborJobId);
  if (!moved || !neighbor || moved.job.status !== 'Queued' || neighbor.job.status !== 'Queued') {
    return false;
  }

  const movedScope = getQueueJobScope(moved);
  const neighborScope = getQueueJobScope(neighbor);
  return (
    movedScope !== undefined &&
    movedScope === neighborScope &&
    moved.job.priority === neighbor.job.priority
  );
}

export function moveQueuedJobInList(
  jobs: QueuedPrintJobWithFileMetaDto[],
  movedJobId: string,
  neighborJobId: string,
  placement: QueueMovePlacement
): QueuedPrintJobWithFileMetaDto[] | undefined {
  if (!canDropQueueJob(jobs, movedJobId, neighborJobId)) return undefined;

  const moved = jobs.find((entry) => entry.job.id === movedJobId);
  const neighbor = jobs.find((entry) => entry.job.id === neighborJobId);
  if (!moved || !neighbor) return undefined;
  const scope = getQueueJobScope(moved);
  if (scope === undefined) return undefined;
  const scopedQueuedIndices = jobs.flatMap((entry, index) =>
    entry.job.status === 'Queued' &&
    getQueueJobScope(entry) === scope &&
    entry.job.priority === moved.job.priority
      ? [index]
      : []
  );
  const scopedQueuedJobs = scopedQueuedIndices.map((index) => jobs[index]);
  const movedIndex = scopedQueuedJobs.findIndex((entry) => entry.job.id === movedJobId);
  const neighborIndex = scopedQueuedJobs.findIndex((entry) => entry.job.id === neighborJobId);
  if (movedIndex < 0 || neighborIndex < 0) return undefined;
  const isAlreadyAdjacent =
    placement === 'before'
      ? movedIndex + 1 === neighborIndex
      : neighborIndex + 1 === movedIndex;
  if (isAlreadyAdjacent) return undefined;

  const reorderedScope = scopedQueuedJobs.filter((entry) => entry.job.id !== movedJobId);
  const targetIndex = reorderedScope.findIndex((entry) => entry.job.id === neighborJobId);
  const insertionIndex = placement === 'before' ? targetIndex : targetIndex + 1;
  reorderedScope.splice(insertionIndex, 0, moved);
  const reordered = [...jobs];
  scopedQueuedIndices.forEach((index, scopeIndex) => {
    reordered[index] = reorderedScope[scopeIndex];
  });
  return reordered;
}

export function getQueueJobOrdinal(
  jobs: QueuedPrintJobWithFileMetaDto[],
  jobId: string
): number | undefined {
  const job = jobs.find((entry) => entry.job.id === jobId);
  if (!job || job.job.status !== 'Queued') return undefined;
  const scope = getQueueJobScope(job);
  if (scope === undefined) return undefined;
  const scopedQueuedJobs = jobs.filter(
    (entry) => entry.job.status === 'Queued' && getQueueJobScope(entry) === scope
  );
  const index = scopedQueuedJobs.findIndex((entry) => entry.job.id === jobId);
  return index < 0 ? undefined : index + 1;
}
