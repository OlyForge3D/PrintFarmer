import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  bindStatusToHead,
  selectSquadVerdict,
  verdictContext,
  verdictWorkflowPath,
  verifySquadVerdict,
} from '../verify-squad-verdict.mjs';

const repositoryRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '..', '..', '..',
);
const reviewedHeadSha = 'a'.repeat(40);
const movedHeadSha = 'b'.repeat(40);

function fixture(verdict = 'APPROVE') {
  const actor = 'trusted-maintainer';
  const state = verdict === 'APPROVE' ? 'success' : 'failure';
  const pull = {
    number: 1116,
    user: { login: 'pr-author' },
    head: { sha: reviewedHeadSha },
    base: {
      repo: {
        full_name: 'OlyForge3D/PrintFarmer',
        default_branch: 'development',
      },
    },
  };
  const status = {
    id: 42,
    context: verdictContext,
    state,
    sha: reviewedHeadSha,
    description: `${verdict} @ ${reviewedHeadSha.slice(0, 12)} by ${actor}`,
    target_url:
      'https://github.com/OlyForge3D/PrintFarmer/actions/runs/123456',
    creator: { login: 'github-actions[bot]' },
    created_at: '2026-08-07T03:00:10Z',
  };
  const run = {
    id: 123456,
    html_url: status.target_url,
    path: verdictWorkflowPath,
    event: 'workflow_dispatch',
    head_branch: 'development',
    head_sha: 'c'.repeat(40),
    default_branch_contains_run: true,
    repository: { full_name: 'OlyForge3D/PrintFarmer' },
    actor: { login: actor },
    display_title:
      `Squad verdict ${verdict} for PR #1116 @ ${reviewedHeadSha} by ${actor}`,
    status: 'completed',
    conclusion: 'success',
    run_started_at: '2026-08-07T03:00:00Z',
    updated_at: '2026-08-07T03:00:20Z',
  };
  return { pull, status, run };
}

test('accepts a trusted approval for the exact current head', () => {
  const evidence = fixture();
  const verdict = verifySquadVerdict(evidence);
  assert.equal(verdict.classification, 'APPROVED');
  assert.equal(verdict.reviewedHeadSha, reviewedHeadSha);
});

test('binds the list-statuses API shape to the requested head', () => {
  const evidence = fixture();
  const apiStatus = { ...evidence.status };
  delete apiStatus.sha;
  const status = bindStatusToHead(apiStatus, reviewedHeadSha);
  const verdict = verifySquadVerdict({ ...evidence, status });
  assert.equal(verdict.classification, 'APPROVED');
});

test('rejects a status whose explicit SHA disagrees with the requested head', () => {
  const evidence = fixture();
  assert.throws(
    () => bindStatusToHead(evidence.status, movedHeadSha),
    /does not match the requested head/,
  );
});

test('blocks a trusted changes-requested verdict for the exact current head', () => {
  const evidence = fixture('CHANGES_REQUESTED');
  const verdict = verifySquadVerdict(evidence);
  assert.equal(verdict.classification, 'CHANGES_REQUESTED');
  assert.equal(verdict.verdict, 'CHANGES_REQUESTED');
});

test('supersedes an approval when rebase or force-push moves the head', () => {
  const evidence = fixture();
  evidence.pull.head.sha = movedHeadSha;
  const verdict = verifySquadVerdict(evidence);
  assert.equal(verdict.classification, 'SUPERSEDED');
  assert.equal(verdict.verdict, 'APPROVE');
});

test('supersedes a rejection when rebase or force-push moves the head', () => {
  const evidence = fixture('REJECT');
  evidence.pull.head.sha = movedHeadSha;
  const verdict = verifySquadVerdict(evidence);
  assert.equal(verdict.classification, 'SUPERSEDED');
  assert.equal(verdict.verdict, 'REJECT');
});

test('rejects a status not created by GitHub Actions', () => {
  const evidence = fixture();
  evidence.status.creator.login = 'pr-author';
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('rejects a workflow run dispatched by the PR author', () => {
  const evidence = fixture();
  evidence.run.actor.login = 'pr-author';
  evidence.run.display_title =
    `Squad verdict APPROVE for PR #1116 @ ${reviewedHeadSha} by pr-author`;
  evidence.status.description =
    `APPROVE @ ${reviewedHeadSha.slice(0, 12)} by pr-author`;
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('rejects a lookalike status from an untrusted workflow', () => {
  const evidence = fixture();
  evidence.run.path = '.github/workflows/lookalike.yml';
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('author-authored lookalike comments cannot satisfy the verifier', () => {
  const evidence = fixture();
  const verdict = verifySquadVerdict({
    pull: evidence.pull,
    comments: [{
      user: { login: 'pr-author' },
      body: evidence.run.display_title,
    }],
  });
  assert.equal(verdict.classification, 'MISSING');
});

test('newest trusted-run evidence fails closed instead of reviving an older approval', () => {
  const older = fixture();
  older.status.id = 41;
  older.status.created_at = '2026-08-07T02:59:10Z';

  const newer = fixture('REJECT');
  newer.status.id = 43;
  newer.status.target_url =
    'https://github.com/OlyForge3D/PrintFarmer/actions/runs/123457';
  newer.run.id = 123457;
  newer.run.html_url = newer.status.target_url;
  newer.run.display_title =
    `Squad verdict REJECT for PR #1116 @ ${reviewedHeadSha.toUpperCase()} ` +
    'by trusted-maintainer';

  const verdict = selectSquadVerdict({
    pull: newer.pull,
    statuses: [older.status, newer.status],
    loadRun: (runId) => runId === newer.run.id ? newer.run : older.run,
  });
  assert.equal(verdict.classification, 'INVALID');
});

test('workflow keeps the independent recorder and exact-head controls', async () => {
  const workflow = (await readFile(
    path.join(repositoryRoot, '.github', 'workflows', 'squad-review-verdict.yml'),
    'utf8',
  )).replaceAll('\r\n', '\n');
  assert.ok(
    workflow.includes(
      'run-name: "Squad verdict ${{ inputs.verdict }} for PR ' +
      '#${{ inputs.pr_number }} @ ${{ inputs.reviewed_head_sha }} ' +
      'by ${{ github.actor }}"',
    ),
  );
  assert.match(workflow, /^\s+statuses: write$/m);
  assert.match(workflow, /\/\^\[1-9\]\\d\*\$\/\.test\(prNumberInput\)/);
  assert.match(workflow, /\/\^\[0-9a-f\]\{40\}\$\/\.test\(reviewedHeadSha\)/);
  assert.match(workflow, /pull\.user\.login\.toLowerCase\(\) === actor\.toLowerCase\(\)/);
  assert.match(workflow, /pull\.head\.sha\.toLowerCase\(\) !== reviewedHeadSha/);
  assert.match(workflow, /getCollaboratorPermissionLevel/);
  assert.match(workflow, /context: 'squad\/pre-pr-verdict'/);
  assert.doesNotMatch(workflow, /pull-requests: write/);
});
