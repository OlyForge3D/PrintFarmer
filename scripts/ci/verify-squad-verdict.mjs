#!/usr/bin/env node

import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

export const verdictContext = 'squad/pre-pr-verdict';
export const verdictWorkflowPath = '.github/workflows/squad-review-verdict.yml';

const trustedStatusCreator = 'github-actions[bot]';
const displayTitlePattern = /^Squad verdict gate for PR #([1-9]\d*)$/;

// The gate runs only from events whose workflow definition comes from the
// default branch. A pull_request (head-ref) trigger would let a PR rewrite the
// logic that judges it.
const trustedEvents = new Set([
  'pull_request_target',
  'issue_comment',
  'pull_request_review',
  'workflow_dispatch',
]);

function result(classification, reason, evidence = {}) {
  return { classification, reason, ...evidence };
}

export function bindStatusToHead(status, headSha) {
  if (status.sha && status.sha.toLowerCase() !== headSha.toLowerCase()) {
    throw new Error('Commit status SHA does not match the requested head.');
  }
  return { ...status, sha: headSha };
}

function parseRunTarget(targetUrl, repository) {
  try {
    const url = new URL(targetUrl);
    const escapedRepository = repository.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const match = url.pathname.match(
      new RegExp(`^/${escapedRepository}/actions/runs/([1-9]\\d*)/?$`, 'i'),
    );
    if (url.protocol !== 'https:' || url.hostname !== 'github.com' || !match) {
      return undefined;
    }
    return Number.parseInt(match[1], 10);
  } catch {
    return undefined;
  }
}

function parseDisplayTitle(displayTitle) {
  const match = displayTitlePattern.exec(displayTitle ?? '');
  if (!match) {
    return undefined;
  }
  return { prNumber: Number.parseInt(match[1], 10) };
}

// The gate encodes its outcome in the status state and description:
//   success  `APPROVE @ <sha12> by <reviewers>`        -> a real approval
//   failure  `REQUEST_CHANGES @ <sha12> by <reviewer>` -> a real rejection
//   failure  `BLOCKED @ <sha12>: <reason>`             -> no usable evidence
// The third form covers "no verdict yet", "have 1/3", stale-only evidence and
// reviewer-is-author. Those are absent evidence, not a reviewer decision, and
// must not be reported as CHANGES_REQUESTED — that would suppress the
// administrator fallback path.
function parseStatusDescription(status, statusSha) {
  const description = status.description ?? '';
  const shortSha = statusSha.slice(0, 12);
  if (status.state === 'success') {
    const match = new RegExp(`^APPROVE @ ${shortSha} by (\\S.*)$`).exec(description);
    return match ? { verdict: 'APPROVE', reviewers: match[1] } : undefined;
  }
  if (status.state === 'failure') {
    const rejected =
      new RegExp(`^REQUEST_CHANGES @ ${shortSha} by (\\S.*)$`).exec(description);
    if (rejected) {
      return { verdict: 'REQUEST_CHANGES', reviewers: rejected[1] };
    }
    const blocked = new RegExp(`^BLOCKED @ ${shortSha}: (\\S.*)$`).exec(description);
    if (blocked) {
      return { verdict: 'BLOCKED', reviewers: '', detail: blocked[1] };
    }
  }
  return undefined;
}

function isStatusCreatedDuringRun(status, run) {
  const createdAt = Date.parse(status.created_at);
  const startedAt = Date.parse(run.run_started_at ?? run.created_at);
  const completedAt = Date.parse(run.updated_at);
  if ([createdAt, startedAt, completedAt].some(Number.isNaN)) {
    return false;
  }
  const toleranceMs = 5_000;
  return createdAt >= startedAt - toleranceMs &&
    createdAt <= completedAt + toleranceMs;
}

export function verifySquadVerdict({ pull, status, run }) {
  if (!status) {
    return result('MISSING', 'No squad verdict status exists on the current head.');
  }
  if (status.context !== verdictContext) {
    return result('INVALID', `Unexpected status context: ${status.context}.`);
  }

  const repository = pull.base?.repo?.full_name;
  const defaultBranch = pull.base?.repo?.default_branch;
  const baseRef = pull.base?.ref;
  const currentHeadSha = pull.head?.sha?.toLowerCase();
  const statusSha = status.sha?.toLowerCase();
  if (!repository || !defaultBranch || !currentHeadSha || !statusSha) {
    return result('INVALID', 'PR or status metadata is incomplete.');
  }
  if (status.creator?.login !== trustedStatusCreator) {
    return result('INVALID', 'The verdict status was not created by GitHub Actions.');
  }

  const runId = parseRunTarget(status.target_url, repository);
  if (!runId || run?.id !== runId || run.html_url !== status.target_url) {
    return result('INVALID', 'The status does not target its verified workflow run.');
  }
  if (
    run.path !== verdictWorkflowPath ||
    !trustedEvents.has(run.event) ||
    run.run_attempt !== 1 ||
    run.triggering_actor?.login?.toLowerCase() !== run.actor?.login?.toLowerCase() ||
    // The gate only ever runs from a protected ref: issue_comment and
    // pull_request_review runs sit on the default branch, pull_request_target
    // runs sit on the PR's base ref. A run on the PR head branch would mean the
    // PR supplied the logic that judged it.
    (run.head_branch !== defaultBranch && run.head_branch !== baseRef) ||
    run.default_branch_contains_run !== true ||
    run.repository?.full_name !== repository ||
    run.status !== 'completed' ||
    run.conclusion !== 'success'
  ) {
    return result('INVALID', 'The target is not a successful trusted verdict workflow run.');
  }

  const title = parseDisplayTitle(run.display_title);
  if (!title || title.prNumber !== pull.number) {
    return result('INVALID', 'The workflow run metadata does not match the status.');
  }

  const outcome = parseStatusDescription(status, statusSha);
  if (!outcome || !isStatusCreatedDuringRun(status, run)) {
    return result('INVALID', 'The status does not match the trusted workflow verdict.');
  }

  // A gate block is the absence of usable evidence, not a reviewer decision.
  if (outcome.verdict === 'BLOCKED') {
    return result(
      'MISSING',
      `The gate blocked ${statusSha} without a reviewer verdict: ${outcome.detail}`,
      { reviewedHeadSha: statusSha },
    );
  }

  if (statusSha !== currentHeadSha) {
    return result(
      'SUPERSEDED',
      `${outcome.verdict} applies to ${statusSha}, not current head ${currentHeadSha}.`,
      { verdict: outcome.verdict, reviewedHeadSha: statusSha, actor: outcome.reviewers },
    );
  }

  const classification = outcome.verdict === 'APPROVE'
    ? 'APPROVED'
    : 'CHANGES_REQUESTED';
  return result(classification, 'Verified SHA-pinned squad verdict.', {
    verdict: outcome.verdict,
    reviewedHeadSha: statusSha,
    actor: outcome.reviewers,
    workflowRunUrl: run.html_url,
  });
}

export function selectSquadVerdict({
  pull,
  statuses,
  statusHeadSha = pull.head.sha,
  loadRun,
}) {
  const candidates = statuses
    .filter((status) => status.context === verdictContext)
    .map((status) => bindStatusToHead(status, statusHeadSha))
    .sort((left, right) => {
      const timestampOrder =
        Date.parse(right.created_at) - Date.parse(left.created_at);
      return timestampOrder || right.id - left.id;
    });

  for (const status of candidates) {
    const runId = parseRunTarget(status.target_url, pull.base.repo.full_name);
    if (!runId) {
      return result('INVALID', 'The newest verdict status has no trusted run target.');
    }
    const run = loadRun(runId);
    // Deliberately fail closed on the newest candidate only: an older approval
    // must never be resurrected by a newer status being unusable.
    return verifySquadVerdict({ pull, status, run });
  }
  return result('MISSING', 'No squad verdict status exists on the current head.');
}

function parseArgs(argv) {
  const args = {};
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (
      argument === '--repo' ||
      argument === '--pr' ||
      argument === '--expected-head'
    ) {
      args[argument.slice(2)] = argv[index + 1];
      index += 1;
      continue;
    }
    if (argument === '--json') {
      args.json = true;
      continue;
    }
    throw new Error(`Unknown argument: ${argument}`);
  }
  if (!/^[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+$/.test(args.repo ?? '')) {
    throw new Error('--repo must be OWNER/REPOSITORY.');
  }
  if (!/^[1-9]\d*$/.test(args.pr ?? '')) {
    throw new Error('--pr must be a positive integer.');
  }
  if (
    args['expected-head'] !== undefined &&
    !/^[0-9a-f]{40}$/.test(args['expected-head'])
  ) {
    throw new Error('--expected-head must be a lowercase 40-character SHA.');
  }
  return { ...args, pr: Number.parseInt(args.pr, 10) };
}

function ghApi(path) {
  const output = execFileSync('gh', ['api', path], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'inherit'],
  });
  return JSON.parse(output);
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const pull = ghApi(`/repos/${args.repo}/pulls/${args.pr}`);
  const statusHeadSha = args['expected-head'] ?? pull.head.sha;
  const statuses = ghApi(
    `/repos/${args.repo}/commits/${statusHeadSha}/statuses?per_page=100`,
  );
  const verdict = selectSquadVerdict({
    pull,
    statuses,
    statusHeadSha,
    loadRun: (runId) => {
      const run = ghApi(`/repos/${args.repo}/actions/runs/${runId}`);
      const comparison = ghApi(
        `/repos/${args.repo}/compare/${run.head_sha}...` +
        encodeURIComponent(pull.base.repo.default_branch),
      );
      return {
        ...run,
        default_branch_contains_run:
          comparison.status === 'ahead' || comparison.status === 'identical',
      };
    },
  });

  if (args.json) {
    process.stdout.write(`${JSON.stringify(verdict)}\n`);
  } else {
    process.stdout.write(`${verdict.classification}: ${verdict.reason}\n`);
  }

  if (verdict.classification === 'APPROVED') {
    return;
  }
  // Exit 2 is a current rejection; exit 3 means no usable squad evidence.
  // Execution failures use exit 1 through the catch handler below.
  process.exitCode = verdict.classification === 'CHANGES_REQUESTED' ? 2 : 3;
}

const invokedPath = process.argv[1]
  ? pathToFileURL(process.argv[1]).href
  : undefined;
if (import.meta.url === invokedPath) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
