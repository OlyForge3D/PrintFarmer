import type {
  BulkDeleteItemResult,
  BulkDeleteItemStatus,
  DeleteGcodeFilesResponse,
  DeleteModel3dFilesResponse,
} from '@/types/api';

export type LocalLibrarySource = 'model' | 'gcode';

export interface LocalLibraryDeleteClient {
  deleteModel3dFiles(ids: string[]): Promise<DeleteModel3dFilesResponse>;
  deleteGcodeFiles(fileIds: string[]): Promise<DeleteGcodeFilesResponse>;
}

export interface LocalLibraryDeleteOutcome {
  status: BulkDeleteItemStatus;
  reason?: string;
}

export interface LocalLibraryDeleteSummary {
  /** Outcome per distinct prefixed ID (`model:<id>` / `gcode:<id>`). */
  outcomes: Map<string, LocalLibraryDeleteOutcome>;
  /** Prefixed IDs whose record no longer exists: Deleted, DeletedCleanupIncomplete or NotFound. */
  removedIds: Set<string>;
  /** Prefixed IDs that may still exist and stay selected for retry: Blocked, Failed or Unknown. */
  retainedIds: Set<string>;
  /** Counts per distinct requested item (case-variant aliases count once). */
  counts: Record<BulkDeleteItemStatus, number>;
  /** Distinct items blocked because active queue jobs use them. */
  activeJobs: number;
}

type Parse = (prefixedId: string) => { source: LocalLibrarySource; actualId: string } | null;

const REMOVED: ReadonlySet<BulkDeleteItemStatus> = new Set(['Deleted', 'DeletedCleanupIncomplete', 'NotFound']);

/**
 * A rejected bulk request. A 4xx means the server refused the whole request before deleting
 * anything; anything else (5xx, network, timeout) leaves the outcome unknown. Singles are never
 * replayed: an unknown outcome is reported, and the caller's refresh shows the real state.
 */
function rejectionOutcome(error: unknown): LocalLibraryDeleteOutcome {
  const statusCode = (error as { statusCode?: unknown } | null)?.statusCode;
  return typeof statusCode === 'number' && statusCode >= 400 && statusCode < 500
    ? { status: 'Failed', reason: 'RequestRejected' }
    : { status: 'Unknown', reason: 'TransportOutcomeUnknown' };
}

/**
 * Deletes local-library files with at most one bulk request per source (models send `{ ids }`,
 * G-code sends `{ fileIds }`). Sources are attempted independently, so one failing does not
 * hide the other's progress.
 */
export async function deleteLocalLibraryFiles(
  prefixedIds: readonly string[],
  client: LocalLibraryDeleteClient,
  parse: Parse,
): Promise<LocalLibraryDeleteSummary> {
  const outcomes = new Map<string, LocalLibraryDeleteOutcome>();
  const distinct: LocalLibraryDeleteOutcome[] = [];
  // Keyed by lower-cased GUID so case variants collapse into one requested ID.
  const groups: Record<LocalLibrarySource, Map<string, { actualId: string; aliases: string[] }>> = {
    model: new Map(),
    gcode: new Map(),
  };

  for (const prefixedId of prefixedIds) {
    if (outcomes.has(prefixedId)) continue;
    const parsed = parse(prefixedId);
    if (!parsed) {
      const invalid: LocalLibraryDeleteOutcome = { status: 'Failed', reason: 'InvalidId' };
      outcomes.set(prefixedId, invalid);
      distinct.push(invalid);
      continue;
    }
    const key = parsed.actualId.toLowerCase();
    const entry = groups[parsed.source].get(key);
    if (entry) {
      if (!entry.aliases.includes(prefixedId)) entry.aliases.push(prefixedId);
    } else {
      groups[parsed.source].set(key, { actualId: parsed.actualId, aliases: [prefixedId] });
    }
  }

  const send = (source: LocalLibrarySource, ids: string[]): Promise<{ results?: BulkDeleteItemResult[] }> =>
    source === 'model' ? client.deleteModel3dFiles(ids) : client.deleteGcodeFiles(ids);

  const sources = (Object.keys(groups) as LocalLibrarySource[]).filter((source) => groups[source].size > 0);
  const settled = await Promise.allSettled(
    sources.map((source) => send(source, [...groups[source].values()].map((entry) => entry.actualId))),
  );

  sources.forEach((source, index) => {
    const group = groups[source];
    const result = settled[index];
    if (result.status === 'rejected') {
      const outcome = rejectionOutcome(result.reason);
      for (const entry of group.values()) {
        distinct.push(outcome);
        for (const alias of entry.aliases) outcomes.set(alias, outcome);
      }
      return;
    }
    const byId = new Map((result.value?.results ?? []).map((item) => [item.id.toLowerCase(), item]));
    for (const [key, entry] of group) {
      const item = byId.get(key);
      // A server without per-ID results (or one omitting an ID) cannot prove what happened.
      const outcome: LocalLibraryDeleteOutcome = item
        ? { status: item.status, ...(item.reason ? { reason: item.reason } : {}) }
        : { status: 'Unknown', reason: 'MissingResult' };
      distinct.push(outcome);
      for (const alias of entry.aliases) outcomes.set(alias, outcome);
    }
  });

  const counts: Record<BulkDeleteItemStatus, number> = {
    Deleted: 0, DeletedCleanupIncomplete: 0, NotFound: 0, Blocked: 0, Failed: 0, Unknown: 0,
  };
  const removedIds = new Set<string>();
  const retainedIds = new Set<string>();
  for (const outcome of distinct) counts[outcome.status]++;
  for (const [prefixedId, outcome] of outcomes) {
    (REMOVED.has(outcome.status) ? removedIds : retainedIds).add(prefixedId);
  }
  const activeJobs = distinct.filter((o) => o.status === 'Blocked' && o.reason === 'ActiveJobs').length;

  return { outcomes, removedIds, retainedIds, counts, activeJobs };
}

const plural = (count: number, noun = 'file') => `${count} ${noun}${count === 1 ? '' : 's'}`;

export interface LocalLibraryDeleteToast {
  level: 'success' | 'warning' | 'error';
  message: string;
}

export function describeLocalLibraryDelete(summary: LocalLibraryDeleteSummary): LocalLibraryDeleteToast {
  const { counts, activeJobs } = summary;
  const deleted = counts.Deleted + counts.DeletedCleanupIncomplete;
  const otherBlocked = counts.Blocked - activeJobs;

  const parts: string[] = [];
  if (deleted > 0) parts.push(`Deleted ${plural(deleted)}`);
  if (counts.DeletedCleanupIncomplete > 0) parts.push(`storage cleanup incomplete for ${counts.DeletedCleanupIncomplete}`);
  if (counts.NotFound > 0) parts.push(`${counts.NotFound} already removed`);
  if (activeJobs > 0) parts.push(`${activeJobs} in use by active jobs`);
  if (otherBlocked > 0) parts.push(`${otherBlocked} blocked`);
  if (counts.Failed > 0) parts.push(`${counts.Failed} failed`);
  if (counts.Unknown > 0) parts.push(`${counts.Unknown} with an unknown outcome — check the refreshed list`);

  if (summary.retainedIds.size === 0) {
    return {
      level: counts.DeletedCleanupIncomplete > 0 ? 'warning' : 'success',
      message: parts.join(', '),
    };
  }
  if (summary.removedIds.size === 0) {
    return { level: 'error', message: `Failed to delete selected files: ${parts.join(', ')}` };
  }
  return { level: 'warning', message: parts.join(', ') };
}
