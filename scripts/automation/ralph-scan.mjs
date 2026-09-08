#!/usr/bin/env node

import { execFile } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { chmod, lstat, mkdir, open, readFile, realpath, rename, unlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);
export const schemaVersion = 1;
const maxConcurrency = 6;

function fail(message, code = 'SCAN_ERROR') {
  const error = new Error(message);
  error.code = code;
  throw error;
}

function required(value, name) {
  if (!value) fail(`${name} is required.`, 'ARGUMENT_ERROR');
  return value;
}

export function parseArgs(argv) {
  const args = {};
  for (let index = 0; index < argv.length; index += 2) {
    const key = argv[index];
    const value = argv[index + 1];
    if (!['--repo', '--workflow-id', '--state-root', '--sessions-file'].includes(key) || value === undefined) {
      fail('Usage: ralph-scan.mjs --repo OWNER/REPO --workflow-id ID --state-root ABSOLUTE_PATH [--sessions-file JSON_PATH]', 'ARGUMENT_ERROR');
    }
    if (args[key]) fail(`Duplicate ${key}.`, 'ARGUMENT_ERROR');
    args[key] = value;
  }
  const repo = required(args['--repo'], '--repo');
  const workflowId = required(args['--workflow-id'], '--workflow-id');
  const stateRoot = required(args['--state-root'], '--state-root');
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repo)) fail('--repo must be OWNER/REPO.', 'ARGUMENT_ERROR');
  for (const segment of repo.split('/')) safeSegment(segment);
  if (!/^[A-Za-z0-9][A-Za-z0-9_.-]*$/.test(workflowId)) fail('--workflow-id contains unsupported characters.', 'ARGUMENT_ERROR');
  if (!path.isAbsolute(stateRoot)) fail('--state-root must be an absolute path.', 'ARGUMENT_ERROR');
  if (args['--sessions-file'] && !path.isAbsolute(args['--sessions-file'])) {
    fail('--sessions-file must be an absolute path.', 'ARGUMENT_ERROR');
  }
  return { repo, workflowId, stateRoot, sessionsFile: args['--sessions-file'] };
}

function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, canonical(value[key])]));
  }
  return value;
}

export function digest(value) {
  return createHash('sha256').update(JSON.stringify(canonical(value)) ?? 'undefined').digest('hex');
}

function safeSegment(value) {
  const segment = value.replace(/[^A-Za-z0-9_.-]/g, '_');
  if (!segment || segment === '.' || segment === '..') fail('State namespace contains an unsafe path segment.', 'ARGUMENT_ERROR');
  return segment;
}

function stateDirectory({ stateRoot, repo, workflowId, host = 'github.com' }) {
  const root = path.resolve(stateRoot);
  const directory = path.resolve(
    root, safeSegment(host), ...repo.toLowerCase().split('/').map(safeSegment), safeSegment(workflowId),
  );
  const relative = path.relative(root, directory);
  if (!relative || relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) {
    fail('State namespace escapes --state-root.', 'ARGUMENT_ERROR');
  }
  return directory;
}

async function ensurePrivateDirectory(directory, root) {
  const relative = path.relative(root, directory);
  if (relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) {
    fail('State namespace escapes its physical root.', 'ARGUMENT_ERROR');
  }
  let current = root;
  for (const segment of relative ? relative.split(path.sep) : []) {
    current = path.join(current, segment);
    await mkdir(current, { mode: 0o700 }).catch((error) => {
      if (error.code !== 'EEXIST') throw error;
    });
    const metadata = await lstat(current);
    if (!metadata.isDirectory() || metadata.isSymbolicLink()) {
      fail(`State namespace component is not a real directory: ${current}`, 'STATE_PATH_UNSAFE');
    }
    await chmod(current, 0o700);
  }
}

export async function readPriorSnapshot(file) {
  try {
    const source = await readFile(file, 'utf8');
    const snapshot = JSON.parse(source);
    if (snapshot.schemaVersion !== schemaVersion || !snapshot.snapshot || typeof snapshot.snapshot !== 'object') {
      fail(`Prior state at ${file} has an incompatible schema.`, 'STATE_INCOMPATIBLE');
    }
    return { snapshot, baseline: 'existing' };
  } catch (error) {
    if (error?.code === 'ENOENT') return { snapshot: undefined, baseline: 'initial' };
    if (error?.code === 'STATE_INCOMPATIBLE') throw error;
    const diagnostic = `${file}.corrupt-${Date.now()}`;
    try {
      await rename(file, diagnostic);
    } catch {
      // Keep an unreadable original in place when it cannot be moved.
    }
    fail(`Prior state is corrupt; preserved for diagnosis at ${diagnostic}. Rebaseline with a complete scan.`, 'STATE_CORRUPT');
  }
}

export async function writeSnapshotAtomically(file, content) {
  const temporary = `${file}.${process.pid}.${randomUUID()}.tmp`;
  await writeFile(temporary, `${JSON.stringify(content)}\n`, { mode: 0o600 });
  await rename(temporary, file);
  await chmod(file, 0o600);
}

export async function acquireLock(file) {
  try {
    const handle = await open(file, 'wx', 0o600);
    await handle.writeFile(JSON.stringify({ pid: process.pid, startedAt: new Date().toISOString() }));
    return async () => {
      await handle.close();
      await unlink(file).catch(() => {});
    };
  } catch (error) {
    if (error?.code === 'EEXIST') fail(`A scan already holds ${file}; refusing to race its baseline.`, 'LOCKED');
    throw error;
  }
}

function validateArray(value, context) {
  if (!Array.isArray(value)) fail(`${context} returned a non-array page.`, 'SHAPE_ERROR');
  return value;
}

function validateObject(value, context) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) fail(`${context} returned a non-object.`, 'SHAPE_ERROR');
  if (Array.isArray(value.errors) && value.errors.length) fail(`${context} contained partial GraphQL errors.`, 'PARTIAL_ERROR');
  return value;
}

export function createGhTransport() {
  return {
    async get(endpoint, { paginate = false } = {}) {
      const args = ['api'];
      if (paginate) args.push('--paginate', '--slurp');
      args.push(endpoint);
      let output;
      try {
        ({ stdout: output } = await execFileAsync('gh', args, {
          encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, windowsHide: true,
        }));
      } catch (error) {
        const detail = error.stderr?.trim() || error.message;
        const unavailable = /\b(403|404)\b/.test(detail) && !/rate limit/i.test(detail);
        fail(`GitHub read failed for ${endpoint}: ${detail}`, unavailable ? 'GITHUB_DENIED' : 'GITHUB_ERROR');
      }
      try {
        return JSON.parse(output);
      } catch {
        fail(`GitHub returned invalid JSON for ${endpoint}.`, 'SHAPE_ERROR');
      }
    },
  };
}

async function pages(transport, endpoint) {
  const response = await transport.get(endpoint, { paginate: true });
  const pageList = validateArray(response, `${endpoint} pagination`);
  const results = [];
  for (const page of pageList) {
    if (page && typeof page === 'object' && Array.isArray(page.errors) && page.errors.length) {
      fail(`${endpoint} contained partial GraphQL errors.`, 'PARTIAL_ERROR');
    }
    validateArray(page, endpoint);
    results.push(...page);
  }
  return results;
}

async function objectPages(transport, endpoint, listProperty) {
  const response = await transport.get(endpoint, { paginate: true });
  const pageList = validateArray(response, `${endpoint} pagination`);
  const merged = [];
  for (const page of pageList) {
    validateObject(page, endpoint);
    merged.push(...validateArray(page[listProperty], `${endpoint}.${listProperty}`));
  }
  return merged;
}

async function mapLimit(items, callback) {
  const output = new Array(items.length);
  let next = 0;
  await Promise.all(Array.from({ length: Math.min(maxConcurrency, items.length) }, async () => {
    while (next < items.length) {
      const index = next++;
      output[index] = await callback(items[index], index);
    }
  }));
  return output;
}

function issueKey(issue) {
  return `issue:${issue.number}`;
}

function prKey(pr) {
  return `pr:${pr.number}`;
}

function labels(issue) {
  return validateArray(issue.labels ?? [], `issue #${issue.number} labels`)
    .map((label) => typeof label === 'string' ? label : label?.name)
    .filter(Boolean).sort();
}

function priority(issue) {
  const match = labels(issue).map((name) => /^priority:p?(\d+)$/i.exec(name)).find(Boolean);
  return match ? Number(match[1]) : Number.MAX_SAFE_INTEGER;
}

function compactIssue(issue, dependencies) {
  validateObject(issue, 'issue');
  if (!Number.isSafeInteger(issue.number) || issue.number < 1 || typeof issue.state !== 'string') {
    fail('Issue is missing number or state.', 'SHAPE_ERROR');
  }
  return {
    number: issue.number, state: issue.state, title: issue.title ?? '', bodyHash: digest(issue.body ?? ''),
    updatedAt: issue.updated_at ?? '', createdAt: issue.created_at ?? '', labels: labels(issue),
    isPullRequest: Boolean(issue.pull_request), dependencies,
  };
}

function compactPr(pr, reviews, comments, checks, status) {
  validateObject(pr, 'pull request');
  if (!Number.isSafeInteger(pr.number) || !pr.head?.sha) fail('Pull request is missing number or head SHA.', 'SHAPE_ERROR');
  return {
    number: pr.number, state: pr.state, draft: Boolean(pr.draft), title: pr.title ?? '',
    updatedAt: pr.updated_at ?? '', headSha: pr.head.sha, baseRef: pr.base?.ref ?? '',
    labels: labels(pr), reviews, comments, checks, status,
  };
}

function compactTimeline(items, context) {
  return validateArray(items, context).map((item) => {
    validateObject(item, context);
    return {
      id: item.id, nodeId: item.node_id, updatedAt: item.updated_at ?? item.submitted_at ?? '',
      state: item.state ?? '', bodyHash: digest(item.body ?? ''), commitId: item.commit_id ?? '',
    };
  }).sort((left, right) => String(left.id).localeCompare(String(right.id)));
}

function compactChecks(checks) {
  const list = validateArray(checks.check_runs ?? [], 'check-runs.check_runs');
  return list.map((check) => ({
    id: check.id, name: check.name ?? '', status: check.status ?? '', conclusion: check.conclusion ?? '',
    startedAt: check.started_at ?? '', completedAt: check.completed_at ?? '', detailsUrl: check.details_url ?? '',
  })).sort((left, right) => left.id - right.id);
}

function compactStatus(status) {
  validateObject(status, 'combined status');
  return validateArray(status.statuses ?? [], 'combined status.statuses').map((item) => ({
    id: item.id, context: item.context ?? '', state: item.state ?? '', updatedAt: item.updated_at ?? '',
    targetUrl: item.target_url ?? '', descriptionHash: digest(item.description ?? ''),
  })).sort((left, right) => left.id - right.id);
}

function issueReference(repository, number) {
  return `github.com/${repository.toLowerCase()}#${number}`;
}

function referenceFor(raw) {
  validateObject(raw, 'dependency');
  const repository = raw.repository_url?.split('/repos/')[1];
  if (!repository || !Number.isSafeInteger(raw.number)) return undefined;
  return issueReference(repository, raw.number);
}

function dependencyEdge(blocker, blocked) {
  const blockerReference = referenceFor(blocker);
  if (!blockerReference || !blocked) {
    return { blocked, unknown: true, reason: 'dependency-shape' };
  }
  return { blocker: blockerReference, blocked, state: blocker.state ?? 'unknown' };
}

function changeReasons(previous, current, fields) {
  if (!previous) return ['initial'];
  return fields.filter((field) => digest(previous[field]) !== digest(current[field]));
}

function topologicalOrder(repo, issues, edges) {
  const indexed = new Map(issues.map((issue) => [issueReference(repo, issue.number), issue]));
  const incoming = new Map(issues.map((issue) => [issueReference(repo, issue.number), 0]));
  const next = new Map(issues.map((issue) => [issueReference(repo, issue.number), []]));
  const blockedEdges = [];
  let unknown = false;
  for (const edge of edges) {
    if (edge.unknown || !indexed.has(edge.blocked) || !indexed.has(edge.blocker)) {
      unknown = true;
      blockedEdges.push(edge);
      continue;
    }
    if (edge.state !== 'open') {
      blockedEdges.push(edge);
      continue;
    }
    incoming.set(edge.blocked, incoming.get(edge.blocked) + 1);
    next.get(edge.blocker).push(edge.blocked);
  }
  const compare = (left, right) => priority(indexed.get(left)) - priority(indexed.get(right)) ||
    (indexed.get(left).createdAt || '').localeCompare(indexed.get(right).createdAt || '') ||
    indexed.get(left).number - indexed.get(right).number;
  const ready = [...incoming.keys()].filter((key) => incoming.get(key) === 0).sort(compare);
  const order = [];
  while (ready.length) {
    const key = ready.shift();
    order.push(indexed.get(key).number);
    for (const dependent of next.get(key).sort(compare)) {
      incoming.set(dependent, incoming.get(dependent) - 1);
      if (incoming.get(dependent) === 0) {
        ready.push(dependent);
        ready.sort(compare);
      }
    }
  }
  return { order, blockedEdges, cyclic: order.length !== issues.length, unknown };
}

async function readSessions(file) {
  if (!file) return { availability: 'unavailable', fingerprint: undefined, active: [] };
  const source = await readFile(file, 'utf8');
  const sessions = JSON.parse(source);
  if (!Array.isArray(sessions)) fail('--sessions-file must contain native list_sessions_and_chats JSON array.', 'SHAPE_ERROR');
  return {
    availability: 'provided',
    fingerprint: digest(sessions),
    active: sessions.filter((session) => session?.active || session?.status === 'active' || session?.status === 'dirty')
      .map((session) => ({ id: session.id, name: session.name ?? '', status: session.status ?? 'active' })),
  };
}

async function securityState(transport, repo) {
  try {
    const alerts = await pages(transport, `/repos/${repo}/code-scanning/alerts?state=open&per_page=100`);
    return { availability: 'available', alerts: alerts.map((alert) => ({
      number: alert.number, state: alert.state, updatedAt: alert.updated_at ?? '',
      rule: alert.rule?.id ?? '', severity: alert.rule?.security_severity_level ?? '',
    })).sort((left, right) => left.number - right.number) };
  } catch (error) {
    if (error.code !== 'GITHUB_DENIED') throw error;
    return { availability: 'unknown', reason: 'code-scanning-unavailable', alerts: [] };
  }
}

export async function collectSnapshot({ repo, workflowId, sessionsFile, transport = createGhTransport() }) {
  const issueListing = await pages(transport, `/repos/${repo}/issues?state=open&per_page=100`);
  const prListing = await pages(transport, `/repos/${repo}/pulls?state=open&per_page=100`);
  const issues = issueListing.filter((issue) => !issue.pull_request);
  const issueDetails = await mapLimit(issues, async (issue) => {
    const number = issue.number;
    const [blockedBy, blocking] = await Promise.all([
      pages(transport, `/repos/${repo}/issues/${number}/dependencies/blocked_by?per_page=100`),
      pages(transport, `/repos/${repo}/issues/${number}/dependencies/blocking?per_page=100`),
    ]);
    return compactIssue(issue, {
      blockedBy: blockedBy.map((entry) => dependencyEdge(entry, issueReference(repo, number))),
      blocking: blocking.map((entry) => dependencyEdge(
        { ...issue, repository_url: `https://api.github.com/repos/${repo}`, number },
        referenceFor(entry),
      )),
    });
  });
  const prs = await mapLimit(prListing, async (pr) => {
    const sha = pr.head?.sha;
    const [comments, reviews, checks, status] = await Promise.all([
      pages(transport, `/repos/${repo}/issues/${pr.number}/comments?per_page=100`),
      pages(transport, `/repos/${repo}/pulls/${pr.number}/reviews?per_page=100`),
      objectPages(transport, `/repos/${repo}/commits/${sha}/check-runs?per_page=100`, 'check_runs'),
      objectPages(transport, `/repos/${repo}/commits/${sha}/status?per_page=100`, 'statuses'),
    ]);
    return compactPr(pr, compactTimeline(reviews, 'reviews'), compactTimeline(comments, 'comments'),
      compactChecks({ check_runs: checks }), compactStatus({ statuses: status }));
  });
  const security = await securityState(transport, repo);
  const sessions = await readSessions(sessionsFile);
  return {
    repo, workflowId, collectedAt: new Date().toISOString(),
    issues: issueDetails.sort((left, right) => left.number - right.number),
    prs: prs.sort((left, right) => left.number - right.number), security, sessions,
  };
}

function writeArtifactPath(directory, kind, value) {
  return path.join(directory, `${kind}-${digest(value).slice(0, 16)}.json`);
}

export async function scan(options) {
  const configuredRoot = path.resolve(options.stateRoot);
  await mkdir(configuredRoot, { recursive: true, mode: 0o700 });
  const physicalRoot = await realpath(configuredRoot);
  const rootMetadata = await lstat(physicalRoot);
  if (!rootMetadata.isDirectory() || rootMetadata.isSymbolicLink()) {
    fail(`State root is not a real directory: ${configuredRoot}`, 'STATE_PATH_UNSAFE');
  }
  await chmod(physicalRoot, 0o700);
  const directory = stateDirectory({ ...options, stateRoot: physicalRoot });
  await ensurePrivateDirectory(directory, physicalRoot);
  const release = await acquireLock(path.join(directory, 'scan.lock'));
  try {
    const stateFile = path.join(directory, 'snapshot.json');
    const prior = await readPriorSnapshot(stateFile);
    const snapshot = await collectSnapshot(options);
    const issueMap = new Map((prior.snapshot?.snapshot.issues ?? []).map((item) => [issueKey(item), item]));
    const prMap = new Map((prior.snapshot?.snapshot.prs ?? []).map((item) => [prKey(item), item]));
    const changedIssues = snapshot.issues.map((item) => ({
      id: issueKey(item), reasons: changeReasons(issueMap.get(issueKey(item)), item,
        ['state', 'title', 'bodyHash', 'updatedAt', 'labels', 'dependencies']),
    })).filter((item) => item.reasons.length);
    const changedPrs = snapshot.prs.map((item) => ({
      id: prKey(item), reasons: changeReasons(prMap.get(prKey(item)), item,
        ['state', 'draft', 'title', 'updatedAt', 'headSha', 'baseRef', 'labels', 'reviews', 'comments', 'checks', 'status']),
    })).filter((item) => item.reasons.length);
    for (const priorPr of prior.snapshot?.snapshot.prs ?? []) {
      if (!snapshot.prs.some((item) => item.number === priorPr.number)) {
        changedPrs.push({ id: prKey(priorPr), reasons: ['removed-terminal-lookup-candidate'] });
      }
    }
    for (const priorIssue of prior.snapshot?.snapshot.issues ?? []) {
      if (!snapshot.issues.some((item) => item.number === priorIssue.number)) {
        changedIssues.push({ id: issueKey(priorIssue), reasons: ['removed-terminal-lookup-candidate'] });
      }
    }
    const edges = snapshot.issues.flatMap((issue) => issue.dependencies.blockedBy);
    const graph = topologicalOrder(snapshot.repo, snapshot.issues, edges);
    const artifactDirectory = path.join(directory, 'artifacts');
    await ensurePrivateDirectory(artifactDirectory, physicalRoot);
    const issueArtifact = writeArtifactPath(artifactDirectory, 'issues', snapshot.issues);
    const prArtifact = writeArtifactPath(artifactDirectory, 'prs', snapshot.prs);
    await Promise.all([
      writeSnapshotAtomically(issueArtifact, snapshot.issues),
      writeSnapshotAtomically(prArtifact, snapshot.prs),
    ]);
    const output = {
      schemaVersion, complete: true, baseline: prior.baseline,
      counts: { openIssues: snapshot.issues.length, openPrs: snapshot.prs.length, changedIssues: changedIssues.length, changedPrs: changedPrs.length },
      changedItems: {
        issues: changedIssues, prs: changedPrs,
        security: changeReasons(prior.snapshot?.snapshot.security, snapshot.security, ['availability', 'alerts']),
        sessions: changeReasons(prior.snapshot?.snapshot.sessions, snapshot.sessions, ['availability', 'fingerprint', 'active']),
      },
      issues: { inventoryArtifact: issueArtifact, readyUnresolved: graph.order, totalAccounted: snapshot.issues.length },
      dependencyOrder: graph.order, blockedEdges: graph.blockedEdges,
      graphFlags: { cyclic: graph.cyclic, unknown: graph.unknown },
      prs: { attention: snapshot.prs.map((pr) => ({ number: pr.number, draft: pr.draft, headSha: pr.headSha })), detailArtifact: prArtifact },
      sessions: { ...snapshot.sessions, requiresLiveEnumerationBeforeDispatchOrReap: snapshot.sessions.availability !== 'provided' },
      security: snapshot.security,
      artifacts: { stateDirectory: directory, issueInventory: issueArtifact, prDetails: prArtifact },
      api: { pagination: 'complete-rest-pages', boundedConcurrency: maxConcurrency },
    };
    await writeSnapshotAtomically(stateFile, { schemaVersion, snapshot });
    return output;
  } finally {
    await release();
  }
}

async function main() {
  const output = await scan(parseArgs(process.argv.slice(2)));
  process.stdout.write(`${JSON.stringify(output)}\n`);
}

const invokedPath = process.argv[1] ? pathToFileURL(process.argv[1]).href : undefined;
if (import.meta.url === invokedPath) {
  main().catch((error) => {
    process.stderr.write(`${error.code ?? 'SCAN_ERROR'}: ${error.message}\n`);
    process.exitCode = 1;
  });
}
