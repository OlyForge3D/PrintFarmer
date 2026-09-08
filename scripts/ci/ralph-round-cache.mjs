import { createHash, randomUUID } from 'node:crypto';
import { mkdir, open, readFile, rename, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

export const cacheSchemaVersion = 1;

export class RalphCacheError extends Error {
  constructor(message, code = 'RALPH_CACHE_ERROR') {
    super(message);
    this.code = code;
  }
}

export function resolveRalphCacheDirectory({ env = process.env, platform = process.platform, home = os.homedir() } = {}) {
  if (env.RALPH_CACHE_DIR) return path.resolve(env.RALPH_CACHE_DIR);
  if (platform === 'win32') return path.join(env.LOCALAPPDATA || path.join(home, 'AppData', 'Local'), 'PrintFarmer', 'ralph-cache');
  if (platform === 'darwin') return path.join(home, 'Library', 'Caches', 'PrintFarmer', 'ralph-cache');
  return path.join(env.XDG_CACHE_HOME || path.join(home, '.cache'), 'printfarmer', 'ralph-cache');
}

export function cacheFileForScope(scope, options = {}) {
  if (!/^[\w.-]+\/[\w.-]+$/.test(scope.repository) || !/^[\w.-]+$/.test(scope.workflow)) {
    throw new RalphCacheError('Cache scope requires repository owner/name and workflow identifiers.', 'INVALID_SCOPE');
  }
  const key = createHash('sha256').update(`${scope.repository}\0${scope.workflow}`).digest('hex');
  return path.join(resolveRalphCacheDirectory(options), `${key}.json`);
}

export async function collectPaginated(fetchPage, { perPage = 100, maxPages = 1000 } = {}) {
  const entries = [];
  for (let page = 1; page <= maxPages; page += 1) {
    let result;
    try {
      result = await fetchPage({ page, perPage });
    } catch (error) {
      throw new RalphCacheError(`Snapshot API failed on page ${page}: ${error.message}`, 'API_FAILURE');
    }
    if (!Array.isArray(result)) {
      throw new RalphCacheError(`Snapshot API returned incomplete data on page ${page}.`, 'INCOMPLETE_DATA');
    }
    entries.push(...result);
    if (result.length < perPage) return entries;
  }
  throw new RalphCacheError(`Snapshot pagination exceeded ${maxPages} pages.`, 'INCOMPLETE_DATA');
}

function stable(value) {
  if (value === undefined) return '"__undefined__"';
  if (Array.isArray(value)) return `[${value.map(stable).join(',')}]`;
  if (value && typeof value === 'object') {
    return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${stable(value[key])}`).join(',')}}`;
  }
  return JSON.stringify(value);
}

export function fingerprint(value) {
  return createHash('sha256').update(stable(value)).digest('hex');
}

export function compareSnapshots(previous = {}, current = {}) {
  const changed = [];
  const keys = new Set([...Object.keys(previous), ...Object.keys(current)]);
  for (const key of [...keys].sort()) {
    if (fingerprint(previous[key]) !== fingerprint(current[key])) changed.push(key);
  }
  return { changed, unchanged: changed.length === 0 };
}

export function createRoundCache({ scope, comparisons, conclusions = {}, policyVersion }) {
  if (!policyVersion) throw new RalphCacheError('policyVersion is required.', 'INVALID_CACHE');
  return {
    schemaVersion: cacheSchemaVersion,
    scope: { repository: scope.repository, workflow: scope.workflow },
    policyVersion,
    savedAt: new Date().toISOString(),
    comparisons,
    conclusions,
  };
}

export function isCacheCurrent(cache, policyVersion) {
  return cache?.schemaVersion === cacheSchemaVersion && cache.policyVersion === policyVersion;
}

export async function readRoundCache(scope, options = {}) {
  const file = cacheFileForScope(scope, options);
  try {
    const parsed = JSON.parse(await readFile(file, 'utf8'));
    if (
      parsed.schemaVersion !== cacheSchemaVersion ||
      parsed.scope?.repository !== scope.repository ||
      parsed.scope?.workflow !== scope.workflow ||
      !parsed.comparisons || !parsed.conclusions
    ) {
      return { cache: undefined, reason: 'invalid-schema' };
    }
    return { cache: parsed, reason: undefined };
  } catch (error) {
    if (error.code === 'ENOENT') return { cache: undefined, reason: 'missing' };
    return { cache: undefined, reason: 'corrupt' };
  }
}

async function acquireLock(lockFile, { retries = 40, retryMs = 10, staleLockMs = 5 * 60 * 1000 } = {}) {
  for (let attempt = 0; attempt < retries; attempt += 1) {
    try {
      const handle = await open(lockFile, 'wx');
      await handle.writeFile(JSON.stringify({
        pid: process.pid,
        createdAt: new Date().toISOString(),
        expiresAt: new Date(Date.now() + staleLockMs).toISOString(),
      }));
      return handle;
    } catch (error) {
      if (error.code !== 'EEXIST') throw error;
      try {
        const metadata = JSON.parse(await readFile(lockFile, 'utf8'));
        const expiresAt = Date.parse(metadata.expiresAt);
        if (!Number.isNaN(expiresAt) && expiresAt < Date.now()) {
          await rm(lockFile, { force: true });
          continue;
        }
      } catch {
        // A malformed or unreadable lock is not demonstrably stale.
      }
      await new Promise((resolve) => setTimeout(resolve, retryMs));
    }
  }
  throw new RalphCacheError('Another Ralph round still owns the cache lock.', 'LOCK_TIMEOUT');
}

export async function writeRoundCache(scope, cache, options = {}) {
  const file = cacheFileForScope(scope, options);
  await mkdir(path.dirname(file), { recursive: true });
  const lockFile = `${file}.lock`;
  const lock = await acquireLock(lockFile, options);
  const temp = `${file}.${process.pid}.${randomUUID()}.tmp`;
  try {
    await writeFile(temp, `${JSON.stringify(cache)}\n`, { encoding: 'utf8', flag: 'wx' });
    await rename(temp, file);
  } finally {
    await rm(temp, { force: true });
    await lock.close();
    await rm(lockFile, { force: true });
  }
}

function priorityNumber(labels = []) {
  const match = labels.map((label) => typeof label === 'string' ? label : label.name)
    .find((label) => /^priority:p[0-3]$/i.test(label || ''));
  return match ? Number(match.slice(-1)) : 4;
}

export function orderReadyIssues(completeIssues, readyIssues, edges) {
  const byNumber = new Map(completeIssues.map((issue) => [issue.number, issue]));
  const outward = new Map();
  for (const { blocker, blocked } of edges) {
    if (!outward.has(blocker)) outward.set(blocker, new Set());
    outward.get(blocker).add(blocked);
  }
  const descendants = (number, visited = new Set()) => {
    if (visited.has(number)) throw new RalphCacheError(`Dependency cycle includes #${number}.`, 'DEPENDENCY_CYCLE');
    const next = new Set(visited).add(number);
    const result = new Set();
    for (const child of outward.get(number) || []) {
      if (byNumber.has(child)) {
        result.add(child);
        for (const nested of descendants(child, next)) result.add(nested);
      }
    }
    return result;
  };
  return readyIssues.map((issue) => {
    const unblocks = descendants(issue.number);
    const inheritedPriority = Math.min(
      priorityNumber(issue.labels),
      ...[...unblocks].map((number) => priorityNumber(byNumber.get(number).labels)),
    );
    return { ...issue, effectivePriority: inheritedPriority, unblockValue: unblocks.size };
  }).sort((left, right) =>
    left.effectivePriority - right.effectivePriority ||
    right.unblockValue - left.unblockValue ||
    new Date(left.createdAt) - new Date(right.createdAt) ||
    left.number - right.number);
}

export function compactRoundOutput({ changed = [], deferred = [], blocked = [], ready = [] }) {
  const list = (items) => items.length ? items.join(',') : '—';
  return `changed:${list(changed)} ready:${list(ready)} blocked:${list(blocked)} macOS:${list(deferred)}`;
}

export function assessCleanupCandidate(candidate = {}, { now = Date.now(), settlingMs = 60 * 60 * 1000 } = {}) {
  const reasons = [];
  if (candidate.session?.active !== false) reasons.push('session activity is active or unknown');
  if (!candidate.worktree?.inspected) reasons.push('worktree state is unknown');
  else {
    if (candidate.worktree.dirty) reasons.push('worktree has tracked changes');
    if (candidate.worktree.untracked) reasons.push('worktree has untracked files');
  }
  if (candidate.finalReport?.workingTreeClean !== true) reasons.push('clean-worktree attestation is absent');
  if (candidate.finalReport?.allCommitsPushed !== true) reasons.push('pushed-commits attestation is absent');

  const settledAt = new Date(candidate.settledAt).getTime();
  if (!candidate.settledAt || Number.isNaN(settledAt)) reasons.push('settling period is unknown');
  else if (now - settledAt < settlingMs) reasons.push('settling period has not elapsed');

  if (candidate.pr) {
    if (candidate.pr.state === 'MERGED') {
      if (candidate.pr.mergeCommitVerifiedOnDevelopment !== true) reasons.push('merge commit is not verified on origin/development');
      if (candidate.pr.headPreservedAfterMerge !== true) reasons.push('current HEAD preservation after merge is unknown');
    } else if (candidate.pr.state === 'CLOSED') {
      if (candidate.finalReport?.closedWithoutMerge !== true) reasons.push('CLOSED WITHOUT MERGE report is absent');
      if (!candidate.finalReport?.closureReason) reasons.push('closed-without-merge reason is absent');
      if (candidate.pr.linkedIssueDispositionVerified !== true) reasons.push('linked issue disposition is unverified');
    } else reasons.push('PR is not terminal');
    if (candidate.pr.commitsAfterMergeKnown !== true) reasons.push('post-merge commit state is unknown');
    else if ((candidate.pr.commitsAfterMerge || []).length > 0) reasons.push('commits were added after PR merge');
  } else if (candidate.noPrDeliverable?.completed !== true || candidate.noPrDeliverable?.verified !== true) {
    reasons.push('no-PR deliverable is incomplete or unverified');
  }

  return { candidate: reasons.length === 0, reasons };
}
