#!/usr/bin/env node

import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

export const verdictContext = 'squad/pre-pr-verdict';
export const verdictWorkflowPath = '.github/workflows/squad-review-verdict.yml';

const trustedStatusCreator = 'github-actions[bot]';
const displayTitlePattern =
  /^Squad verdict (APPROVE|CHANGES_REQUESTED|REJECT) for PR #([1-9]\d*) @ ([0-9a-f]{40}) by ([A-Za-z0-9-]+)$/;

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
  return {
    verdict: match[1],
    prNumber: Number.parseInt(match[2], 10),
    reviewedHeadSha: match[3],
    actor: match[4],
  };
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
  const author = pull.user?.login;
  const currentHeadSha = pull.head?.sha?.toLowerCase();
  const statusSha = status.sha?.toLowerCase();
  if (!repository || !defaultBranch || !author || !currentHeadSha || !statusSha) {
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
    run.event !== 'workflow_dispatch' ||
    run.head_branch !== defaultBranch ||
    run.default_branch_contains_run !== true ||
    run.repository?.full_name !== repository ||
    run.status !== 'completed' ||
    run.conclusion !== 'success'
  ) {
    return result('INVALID', 'The target is not a successful trusted verdict workflow run.');
  }

  const title = parseDisplayTitle(run.display_title);
  if (
    !title ||
    title.prNumber !== pull.number ||
    title.reviewedHeadSha !== statusSha ||
    title.actor.toLowerCase() !== run.actor?.login?.toLowerCase()
  ) {
    return result('INVALID', 'The workflow run metadata does not match the status.');
  }
  if (title.actor.toLowerCase() === author.toLowerCase()) {
    return result('INVALID', 'The PR author recorded the verdict.');
  }

  const expectedState = title.verdict === 'APPROVE' ? 'success' : 'failure';
  const expectedDescription =
    `${title.verdict} @ ${statusSha.slice(0, 12)} by ${title.actor}`;
  if (
    status.state !== expectedState ||
    status.description !== expectedDescription ||
    !isStatusCreatedDuringRun(status, run)
  ) {
    return result('INVALID', 'The status does not match the trusted workflow verdict.');
  }

  if (statusSha !== currentHeadSha) {
    return result(
      'SUPERSEDED',
      `${title.verdict} applies to ${statusSha}, not current head ${currentHeadSha}.`,
      { verdict: title.verdict, reviewedHeadSha: statusSha, actor: title.actor },
    );
  }

  const classification = title.verdict === 'APPROVE'
    ? 'APPROVED'
    : 'CHANGES_REQUESTED';
  return result(classification, 'Verified SHA-pinned squad verdict.', {
    verdict: title.verdict,
    reviewedHeadSha: statusSha,
    actor: title.actor,
    workflowRunUrl: run.html_url,
  });
}

export function selectSquadVerdict({ pull, statuses, loadRun }) {
  const candidates = statuses
    .filter((status) => status.context === verdictContext)
    .map((status) => bindStatusToHead(status, pull.head.sha))
    .sort((left, right) => {
      const timestampOrder =
        Date.parse(right.created_at) - Date.parse(left.created_at);
      return timestampOrder || right.id - left.id;
    });

  for (const status of candidates) {
    const runId = parseRunTarget(status.target_url, pull.base.repo.full_name);
    if (!runId) {
      continue;
    }
    const run = loadRun(runId);
    return verifySquadVerdict({ pull, status, run });
  }
  return result('MISSING', 'No squad verdict status exists on the current head.');
}

function parseArgs(argv) {
  const args = {};
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === '--repo' || argument === '--pr') {
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
  if (!/^[^/]+\/[^/]+$/.test(args.repo ?? '')) {
    throw new Error('--repo must be OWNER/REPOSITORY.');
  }
  if (!/^[1-9]\d*$/.test(args.pr ?? '')) {
    throw new Error('--pr must be a positive integer.');
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
  const statuses = ghApi(
    `/repos/${args.repo}/commits/${pull.head.sha}/statuses?per_page=100`,
  );
  const verdict = selectSquadVerdict({
    pull,
    statuses,
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
