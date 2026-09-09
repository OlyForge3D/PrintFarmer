#!/usr/bin/env node

import { execFile } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { promisify } from 'node:util';
import {
  collectPaginated,
  compareSnapshots,
  createRoundCache,
  isCacheCurrent,
  readRoundCache,
  writeRoundCache,
} from './ralph-round-cache.mjs';

const execFileAsync = promisify(execFile);

function fail(message, code) {
  const error = new Error(message);
  error.code = code;
  throw error;
}

function normalizeRepository(repository) {
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) {
    fail('--repo must be OWNER/REPOSITORY.');
  }
  return repository;
}

export function parseArgs(argv) {
  const args = {};
  for (let index = 0; index < argv.length; index += 2) {
    const name = argv[index];
    const value = argv[index + 1];
    if (!['--repo', '--workflow', '--policy-version', '--sessions-file'].includes(name) || value === undefined || args[name]) {
      fail('Usage: ralph-github-snapshot.mjs --repo OWNER/REPOSITORY --workflow ID --policy-version VERSION [--sessions-file ABSOLUTE_JSON]');
    }
    args[name] = value;
  }
  if (args['--sessions-file'] && !args['--sessions-file'].startsWith('/')) {
    fail('--sessions-file must be an absolute path.');
  }
  return {
    repository: normalizeRepository(args['--repo'] ?? ''),
    workflow: args['--workflow'] ?? fail('--workflow is required.'),
    policyVersion: args['--policy-version'] ?? fail('--policy-version is required.'),
    sessionsFile: args['--sessions-file'],
  };
}

function stable(value) {
  if (Array.isArray(value)) return value.map(stable);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, stable(value[key])]));
  }
  return value;
}

function requireArray(value, context) {
  if (!Array.isArray(value)) fail(`${context} returned incomplete data.`);
  return value;
}

function labelNames(labels = []) {
  return requireArray(labels, 'labels').map((label) => typeof label === 'string' ? label : label?.name)
    .filter(Boolean).sort();
}

function compactIssue(issue, blockedBy, blocking) {
  if (!Number.isSafeInteger(issue?.number) || typeof issue.state !== 'string') fail('Issue response is incomplete.');
  return {
    number: issue.number,
    state: issue.state,
    title: issue.title ?? '',
    body: issue.body ?? '',
    updatedAt: issue.updated_at ?? '',
    createdAt: issue.created_at ?? '',
    labels: labelNames(issue.labels),
    assignees: requireArray(issue.assignees ?? [], 'assignees').map((user) => user?.login).filter(Boolean).sort(),
    blockedBy: normalizeDependencies(blockedBy),
    blocking: normalizeDependencies(blocking),
  };
}

function normalizeDependencies(entries) {
  return requireArray(entries, 'dependencies').map((issue) => ({
    repository: issue.repository_url?.split('/repos/')[1]?.toLowerCase(),
    number: issue.number,
    state: issue.state ?? 'unknown',
  })).map((issue) => {
    if (!issue.repository || !Number.isSafeInteger(issue.number)) fail('Dependency response is incomplete.');
    return issue;
  }).sort((left, right) =>
    left.repository.localeCompare(right.repository) || left.number - right.number || left.state.localeCompare(right.state));
}

function hasEntryId(entry) {
  return entry && typeof entry === 'object' && !Array.isArray(entry) &&
    Number.isSafeInteger(entry.id) && entry.id > 0;
}

function isNonEmptyString(value) {
  return typeof value === 'string' && value.length > 0;
}

function isNullableString(value) {
  return value === null || isNonEmptyString(value);
}

function compactTimeline(entries, context) {
  const isReview = context === 'reviews';
  return requireArray(entries, context).map((entry) => {
    if (!hasEntryId(entry) || typeof entry.body !== 'string' ||
      (isReview
        ? !isNonEmptyString(entry.state) || !isNullableString(entry.commit_id) ||
          !(isNonEmptyString(entry.submitted_at) || (entry.state === 'PENDING' && entry.submitted_at == null))
        : !isNonEmptyString(entry.updated_at))) {
      fail(`${context} returned incomplete data.`);
    }
    return {
      id: entry.id,
      state: isReview ? entry.state : '',
      updatedAt: isReview ? entry.submitted_at ?? '' : entry.updated_at,
      body: entry.body,
      commitId: isReview ? entry.commit_id ?? '' : '',
    };
  }).sort((left, right) => String(left.id).localeCompare(String(right.id)));
}

function compactChecks(entries, context) {
  const isCheckRun = context === 'checks';
  return requireArray(entries, context).map((entry) => {
    if (!hasEntryId(entry) ||
      (isCheckRun
        ? !isNonEmptyString(entry.name) || !isNonEmptyString(entry.status) ||
          !isNullableString(entry.conclusion) || !isNullableString(entry.completed_at)
        : !isNonEmptyString(entry.context) || !isNonEmptyString(entry.state) || !isNonEmptyString(entry.updated_at))) {
      fail(`${context} returned incomplete data.`);
    }
    return {
      id: entry.id,
      name: isCheckRun ? entry.name : entry.context,
      status: isCheckRun ? entry.status : entry.state,
      conclusion: isCheckRun ? entry.conclusion ?? '' : '',
      updatedAt: isCheckRun ? entry.completed_at ?? '' : entry.updated_at,
    };
  }).sort((left, right) => left.id - right.id);
}

function compactAlerts(entries) {
  return requireArray(entries, 'CodeQL alerts').map((entry) => {
    if (!Number.isSafeInteger(entry?.number) || typeof entry.state !== 'string' ||
      typeof entry.updated_at !== 'string' || typeof entry.rule?.id !== 'string') {
      fail('CodeQL alerts returned incomplete data.');
    }
    return {
      number: entry.number,
      state: entry.state,
      updatedAt: entry.updated_at,
      rule: entry.rule.id,
      severity: entry.rule.security_severity_level ?? '',
    };
  }).sort((left, right) => left.number - right.number);
}

export function createGitHubReader() {
  const requestCounts = { requests: 0, pages: 0, responseBytes: 0 };
  return {
    metrics: requestCounts,
    async page(endpoint, { page, perPage }) {
      const separator = endpoint.includes('?') ? '&' : '?';
      const path = `${endpoint}${separator}page=${page}&per_page=${perPage}`;
      let stdout;
      try {
        ({ stdout } = await execFileAsync('gh', ['api', '--hostname', 'github.com', path], {
          encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, windowsHide: true,
        }));
      } catch (error) {
        const detail = error.stderr?.trim() || error.message;
        const denied = /\b(403|404)\b/.test(detail) && !/rate limit/i.test(detail);
        fail(`GitHub read failed for ${endpoint}: ${detail}`, denied ? 'GITHUB_DENIED' : 'GITHUB_ERROR');
      }
      requestCounts.requests += 1;
      requestCounts.responseBytes += Buffer.byteLength(stdout);
      let result;
      try {
        result = JSON.parse(stdout);
      } catch {
        fail(`GitHub returned invalid JSON for ${endpoint}.`);
      }
      const entries = endpoint.includes('/check-runs')
        ? result?.check_runs
        : result;
      requireArray(entries, endpoint);
      requestCounts.pages += 1;
      return entries;
    },
  };
}

async function paginated(reader, endpoint) {
  return collectPaginated((page) => reader.page(endpoint, page));
}

async function collectSessions(file) {
  if (!file) return { availability: 'unavailable' };
  const sessions = JSON.parse(await readFile(file, 'utf8'));
  if (!Array.isArray(sessions)) fail('--sessions-file must contain the native session array.');
  return { availability: 'provided', entries: stable(sessions) };
}

export async function collectComparisons(repository, reader, sessionsFile) {
  const [listing, pulls] = await Promise.all([
    paginated(reader, `/repos/${repository}/issues?state=open`),
    paginated(reader, `/repos/${repository}/pulls?state=open`),
  ]);
  const issues = requireArray(listing, 'issue listing').filter((issue) => !issue.pull_request);
  const issueComparisons = {};
  for (const issue of issues) {
    const [blockedBy, blocking] = await Promise.all([
      paginated(reader, `/repos/${repository}/issues/${issue.number}/dependencies/blocked_by`),
      paginated(reader, `/repos/${repository}/issues/${issue.number}/dependencies/blocking`),
    ]);
    issueComparisons[issue.number] = compactIssue(issue, blockedBy, blocking);
  }
  const prComparisons = {};
  for (const pull of pulls) {
    if (!Number.isSafeInteger(pull?.number) || !pull.head?.sha) fail('Pull request response is incomplete.');
    const sha = pull.head.sha;
    const [comments, reviews, checks, statuses] = await Promise.all([
      paginated(reader, `/repos/${repository}/issues/${pull.number}/comments`),
      paginated(reader, `/repos/${repository}/pulls/${pull.number}/reviews`),
      paginated(reader, `/repos/${repository}/commits/${sha}/check-runs`),
      paginated(reader, `/repos/${repository}/commits/${sha}/statuses`),
    ]);
    prComparisons[pull.number] = {
      state: pull.state, draft: Boolean(pull.draft), title: pull.title ?? '', updatedAt: pull.updated_at ?? '',
      headSha: sha, baseRef: pull.base?.ref ?? '', labels: labelNames(pull.labels),
      comments: compactTimeline(comments, 'comments'), reviews: compactTimeline(reviews, 'reviews'),
      checks: compactChecks(checks, 'checks'), statuses: compactChecks(statuses, 'statuses'),
    };
  }
  let codeql;
  try {
    codeql = compactAlerts(await paginated(reader, `/repos/${repository}/code-scanning/alerts?state=open`));
  } catch (error) {
    if (error.sourceCode !== 'GITHUB_DENIED') throw error;
    codeql = { availability: 'unknown', error: error.message };
  }
  const sessions = await collectSessions(sessionsFile);
  return stable({
    issues: issueComparisons,
    prs: prComparisons,
    base: { availability: 'not-collected-live-action-required' },
    sessions,
    claims: { availability: 'not-cached-live-action-required' },
    linkedPrs: { availability: 'not-cached-live-action-required' },
    codeql: Array.isArray(codeql) ? { availability: 'available', alerts: codeql } : codeql,
    holds: { availability: 'not-collected-live-action-required' },
  });
}

export async function runSnapshot({ scope, policyVersion, sessionsFile, reader = createGitHubReader(), cacheOptions } = {}) {
  const previous = await readRoundCache(scope, cacheOptions);
  const comparisons = await collectComparisons(scope.repository, reader, sessionsFile);
  const baselineCurrent = previous.cache && isCacheCurrent(previous.cache, policyVersion);
  const coverage = Object.fromEntries(
    Object.entries(comparisons).map(([name, value]) => [name, value.availability ?? 'available']),
  );
  const coverageComplete = Object.values(coverage).every((availability) => availability === 'available');
  const comparison = compareSnapshots(
    baselineCurrent ? previous.cache.comparisons : {},
    comparisons,
  );
  const conclusions = {
    observation: baselineCurrent && coverageComplete ? 'delta' : 'deep-scan-required',
    cacheReason: coverage.codeql !== 'available' ? 'codeql-unavailable' :
      !coverageComplete ? 'coverage-incomplete' :
      baselineCurrent ? undefined : previous.reason ?? 'policy-version-changed',
    changed: comparison.changed,
    coverage,
    metrics: reader.metrics,
  };
  const cache = createRoundCache({ scope, policyVersion, comparisons, conclusions });
  await writeRoundCache(scope, cache, cacheOptions);
  return { complete: true, baseline: baselineCurrent ? 'existing' : 'initial', conclusions };
}

async function main() {
  const { repository, workflow, policyVersion, sessionsFile } = parseArgs(process.argv.slice(2));
  const result = await runSnapshot({ scope: { repository, workflow }, policyVersion, sessionsFile });
  process.stdout.write(`${JSON.stringify(result)}\n`);
}

const invokedPath = process.argv[1] ? pathToFileURL(process.argv[1]).href : undefined;
if (import.meta.url === invokedPath) {
  main().catch((error) => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  });
}
