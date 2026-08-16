import assert from 'node:assert/strict';
import test from 'node:test';
import {
  evaluateGate,
  rosterFromLabels,
} from '../squad-verdict-gate.mjs';
import {
  bindStatusToHead,
  exitCodeFor,
  selectSquadVerdict,
  verdictContext,
  verdictWorkflowPath,
  verifySquadVerdict,
} from '../verify-squad-verdict.mjs';

const reviewedHeadSha = 'a'.repeat(40);
const movedHeadSha = 'b'.repeat(40);
const shortSha = reviewedHeadSha.slice(0, 12);

function fixture(verdict = 'APPROVE') {
  const actor = 'jpapiez';
  const state = verdict === 'APPROVE' ? 'success' : 'failure';
  const description = verdict === 'APPROVE'
    ? `REVIEWED (self-attested) @ ${shortSha} by bishop+hicks+vasquez`
    : `REQUEST_CHANGES @ ${shortSha} by vasquez`;
  const pull = {
    number: 1116,
    user: { login: 'pr-author' },
    head: { sha: reviewedHeadSha },
    base: {
      ref: 'development',
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
    description,
    target_url:
      'https://github.com/OlyForge3D/PrintFarmer/actions/runs/123456',
    creator: { login: 'github-actions[bot]' },
    created_at: '2026-08-07T03:00:10Z',
  };
  const run = {
    id: 123456,
    html_url: status.target_url,
    path: verdictWorkflowPath,
    event: 'issue_comment',
    run_attempt: 1,
    head_branch: 'development',
    head_sha: 'c'.repeat(40),
    default_branch_contains_run: true,
    repository: { full_name: 'OlyForge3D/PrintFarmer' },
    actor: { login: actor },
    triggering_actor: { login: actor },
    display_title: 'Squad review record for PR #1116',
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
  assert.equal(verdict.classification, 'REVIEWED');
  assert.equal(verdict.reviewedHeadSha, reviewedHeadSha);
  assert.equal(verdict.actor, 'bishop+hicks+vasquez');
});

test('accepts the agent-verdict events the gate actually runs on', () => {
  for (const event of [
    'pull_request_target', 'issue_comment', 'pull_request_review', 'workflow_dispatch',
  ]) {
    const evidence = fixture();
    evidence.run.event = event;
    // pull_request_target and pull_request_review runs report the reviewed
    // PR's own head branch here, never the default branch — this must not
    // affect the outcome (regression test for issue #1388).
    if (event === 'pull_request_target' || event === 'pull_request_review') {
      evidence.run.head_branch = 'dev/jpapiez/some-feature';
      evidence.run.default_branch_contains_run = false;
    }
    // pull_request_review additionally requires independent proof that the
    // workflow file content matches the default branch, since GitHub does
    // not guarantee its workflow definition is sourced from there.
    if (event === 'pull_request_review') {
      evidence.run.workflow_definition_matches_default_branch = true;
    }
    assert.equal(verifySquadVerdict(evidence).classification, 'REVIEWED', event);
  }
});

test('rejects a run triggered from the pull request head ref', () => {
  const evidence = fixture();
  evidence.run.event = 'pull_request';
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('accepts a verdict recorded by the PR author account', () => {
  // Every squad agent acts through the owner token, so GitHub-account-level
  // author checking is exactly what made the old gate unsatisfiable. Reviewer
  // separation is enforced at squad-identity level inside the gate itself.
  const evidence = fixture();
  evidence.pull.user.login = 'jpapiez';
  assert.equal(verifySquadVerdict(evidence).classification, 'REVIEWED');
});

test('binds the list-statuses API shape to the requested head', () => {
  const evidence = fixture();
  const apiStatus = { ...evidence.status };
  delete apiStatus.sha;
  const status = bindStatusToHead(apiStatus, reviewedHeadSha);
  const verdict = verifySquadVerdict({ ...evidence, status });
  assert.equal(verdict.classification, 'REVIEWED');
});

test('rejects a status whose explicit SHA disagrees with the requested head', () => {
  const evidence = fixture();
  assert.throws(
    () => bindStatusToHead(evidence.status, movedHeadSha),
    /does not match the requested head/,
  );
});

test('blocks a trusted changes-requested verdict for the exact current head', () => {
  const evidence = fixture('REQUEST_CHANGES');
  const verdict = verifySquadVerdict(evidence);
  assert.equal(verdict.classification, 'CHANGES_REQUESTED');
  assert.equal(verdict.verdict, 'REQUEST_CHANGES');
});

test('supersedes an approval when rebase or force-push moves the head', () => {
  const evidence = fixture();
  evidence.pull.head.sha = movedHeadSha;
  const verdict = verifySquadVerdict(evidence);
  assert.equal(verdict.classification, 'SUPERSEDED');
  assert.equal(verdict.verdict, 'REVIEWED');
});

test('supersedes a rejection when rebase or force-push moves the head', () => {
  const evidence = fixture('REQUEST_CHANGES');
  evidence.pull.head.sha = movedHeadSha;
  const verdict = verifySquadVerdict(evidence);
  assert.equal(verdict.classification, 'SUPERSEDED');
  assert.equal(verdict.verdict, 'REQUEST_CHANGES');
});

for (const [fixtureKind, expected] of [
  ['APPROVE', 'REVIEWED'], ['REQUEST_CHANGES', 'REQUEST_CHANGES'],
]) {
  test(`selector supersedes stale ${expected} evidence from an expected head`, () => {
    const evidence = fixture(fixtureKind);
    evidence.pull.head.sha = movedHeadSha;
    const verdict = selectSquadVerdict({
      pull: evidence.pull,
      statuses: [evidence.status],
      statusHeadSha: reviewedHeadSha,
      loadRun: () => evidence.run,
    });
    assert.equal(verdict.classification, 'SUPERSEDED');
    assert.equal(verdict.verdict, expected);
  });
}

test('an owner authorisation is classified apart from a self-attested record', () => {
  // REVIEWED must never be reported as though an independent party approved.
  const evidence = fixture();
  evidence.status.description = `APPROVE (owner) @ ${shortSha} by jpapiez`;
  const verdict = verifySquadVerdict(evidence);
  assert.equal(verdict.classification, 'APPROVED');
  assert.equal(verdict.actor, 'jpapiez');

  const selfAttested = verifySquadVerdict(fixture());
  assert.equal(selfAttested.classification, 'REVIEWED');
  assert.match(selfAttested.reason, /self-attested.*not independent review/);
});

test('rejects a status not created by GitHub Actions', () => {
  const evidence = fixture();
  evidence.status.creator.login = 'pr-author';
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('rejects a lookalike status from an untrusted workflow', () => {
  const evidence = fixture();
  evidence.run.path = '.github/workflows/lookalike.yml';
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('rejects a run whose workflow definition came off the default branch', () => {
  const evidence = fixture();
  evidence.run.head_branch = 'dev/jpapiez/rewrite-the-gate';
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

// Regression tests for issue #1388: GitHub reports the *reviewed PR's* own
// head branch (never the default branch or base ref) in run.head_branch for
// both of these event types. pull_request_target's workflow definition is
// still sourced from the default branch by platform guarantee, so event type
// alone is sufficient evidence for it. pull_request_review carries no such
// guarantee (its GITHUB_SHA/REF are the PR's own merge branch, identical to
// plain pull_request), so it additionally requires proof that the workflow
// file content at the reviewed commit matches the default branch's copy —
// see workflow_definition_matches_default_branch. run.pull_requests is
// deliberately NOT used as evidence: GitHub computes it dynamically from
// currently-open PRs on the matching branch, so it goes empty as soon as the
// PR merges or its branch is deleted — exactly the case this gate must still
// verify (Ralph checks squad evidence against historical, often now-merged,
// heads).
for (const event of ['pull_request_target', 'pull_request_review']) {
  const contentVerified = event === 'pull_request_review';

  test(`accepts a ${event} run whose head_branch is the PR's own branch`, () => {
    const evidence = fixture();
    evidence.run.event = event;
    evidence.run.head_branch = 'dev/jpapiez/codeql-sensitive-info-triage';
    evidence.run.default_branch_contains_run = false;
    if (contentVerified) {
      evidence.run.workflow_definition_matches_default_branch = true;
    }
    assert.equal(verifySquadVerdict(evidence).classification, 'REVIEWED');
  });

  test(`accepts a ${event} run even after its PR has merged (pull_requests empty)`, () => {
    const evidence = fixture();
    evidence.run.event = event;
    evidence.run.head_branch = 'dev/jpapiez/codeql-sensitive-info-triage';
    evidence.run.default_branch_contains_run = false;
    evidence.run.pull_requests = [];
    if (contentVerified) {
      evidence.run.workflow_definition_matches_default_branch = true;
    }
    assert.equal(verifySquadVerdict(evidence).classification, 'REVIEWED');
  });

  test(`rejects a ${event} run whose display title names a different PR`, () => {
    const evidence = fixture();
    evidence.run.event = event;
    evidence.run.head_branch = 'dev/jpapiez/codeql-sensitive-info-triage';
    evidence.run.display_title = 'Squad review record for PR #9999';
    if (contentVerified) {
      evidence.run.workflow_definition_matches_default_branch = true;
    }
    assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
  });
}

// Security regression tests: a pull_request_review run must NOT be trusted
// on event type alone, because GitHub does not guarantee this event's
// workflow definition is sourced from the default branch (unlike
// pull_request_target). A PR author who edits the gate workflow on their own
// branch and gets a review submitted on that PR must not have the resulting
// run accepted as trusted evidence.
test('rejects a pull_request_review run whose workflow file does not match the default branch', () => {
  const evidence = fixture();
  evidence.run.event = 'pull_request_review';
  evidence.run.head_branch = 'dev/jpapiez/codeql-sensitive-info-triage';
  evidence.run.workflow_definition_matches_default_branch = false;
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('rejects a pull_request_review run when the workflow content match is unproven', () => {
  const evidence = fixture();
  evidence.run.event = 'pull_request_review';
  evidence.run.head_branch = 'dev/jpapiez/codeql-sensitive-info-triage';
  // workflow_definition_matches_default_branch left undefined: simulates a
  // Contents API lookup failure, which must fail closed, not open.
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('rejects a success status whose description is not a recognised record', () => {
  const evidence = fixture();
  evidence.status.description = 'looks fine to me';
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('rejects a record description pinned to a different short SHA', () => {
  for (const description of [
    `REVIEWED (self-attested) @ ${movedHeadSha.slice(0, 12)} by bishop`,
    `APPROVE (owner) @ ${movedHeadSha.slice(0, 12)} by jpapiez`,
  ]) {
    const evidence = fixture();
    evidence.status.description = description;
    assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
  }
});

test('a bare APPROVE description is not accepted as an owner authorisation', () => {
  const evidence = fixture();
  evidence.status.description = `APPROVE @ ${shortSha} by bishop+hicks+vasquez`;
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('a gate block is missing evidence, not a reviewer rejection', () => {
  // Ralph routes CHANGES_REQUESTED back to the author but treats MISSING as
  // "no squad evidence", which permits the administrator fallback. Conflating
  // the two would suppress that fallback for PRs nobody has reviewed yet.
  //
  // This case is produced by the workflow rather than by evaluateGate, so it is
  // asserted literally. Every gate-produced form is covered by the round-trip
  // test below, which derives its strings from evaluateGate itself.
  const evidence = fixture('REQUEST_CHANGES');
  const detail = 'fork PR needs a repository administrator';
  evidence.status.description = `BLOCKED @ ${shortSha}: ${detail}`;
  const verdict = verifySquadVerdict(evidence);
  assert.equal(verdict.classification, 'MISSING');
  assert.equal(verdict.blockedReason, detail);
  assert.match(verdict.reason, /fork PR needs a repository administrator/);
});

test('escapes SHA characters before building a dynamic RegExp', () => {
  // The short SHA is interpolated into a dynamic RegExp. If it were not
  // escaped, characters such as '.' would act as wildcards, and a
  // description naming an unrelated 12-character prefix would incorrectly
  // satisfy the pattern.
  const craftedSha = `a${'.'.repeat(11)}${'b'.repeat(28)}`;
  const shortSha = craftedSha.slice(0, 12);
  const evidence = fixture();
  evidence.pull.head.sha = craftedSha;
  evidence.status.sha = craftedSha;

  const unrelatedPrefix = `a${'x'.repeat(11)}`;
  evidence.status.description =
    `REVIEWED (self-attested) @ ${unrelatedPrefix} by bishop+hicks+vasquez`;
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');

  evidence.status.description =
    `REVIEWED (self-attested) @ ${shortSha} by bishop+hicks+vasquez`;
  assert.equal(verifySquadVerdict(evidence).classification, 'REVIEWED');
});

test('every description evaluateGate can emit round-trips through the verifier', () => {
  // Derived from the gate, never hand-written: a hand-written fixture proves
  // only that the verifier parses the string someone imagined, not the string
  // the gate actually produces. Contract drift between the two would either
  // block every merge or silently downgrade a reason operators act on.
  const roster = rosterFromLabels([
    'squad:bishop', 'squad:hicks', 'squad:vasquez', 'squad:parker', 'squad:dallas',
  ]);
  const codePaths = ['src/api/Program.cs'];
  const docPaths = ['docs/ARCHITECTURE.md'];
  const record = (reviewer, verdict, sha = reviewedHeadSha, extra = {}) => ({
    body: [
      '<!-- squad-verdict -->',
      `Squad-Reviewer: ${reviewer}`,
      `Squad-Verdict: ${verdict}`,
      `Squad-Head-SHA: ${sha}`,
    ].join('\n'),
    user: { login: 'jpapiez' },
    author_association: 'OWNER',
    squadWriteAccess: true,
    created_at: '2026-08-08T01:00:00Z',
    updated_at: '2026-08-08T01:00:00Z',
    ...extra,
  });
  const base = {
    headSha: reviewedHeadSha,
    roster,
    authorMembers: new Set(['parker']),
    authorSource: 'squad: label on linked issue',
    squadLabeled: true,
  };
  const panel = ['bishop', 'hicks', 'vasquez'];

  const scenarios = [
    ['no record at all', { changedPaths: codePaths }, 'MISSING'],
    // Out of scope must NOT round-trip to REVIEWED/APPROVED: the status is
    // green, but green here means "no review was required", and treating that
    // as merge evidence would auto-merge every unlabelled PR.
    ['no squad label', { changedPaths: codePaths, squadLabeled: false }, 'NOT_APPLICABLE'],
    ['unauthenticated author', {
      changedPaths: codePaths,
      comments: panel.map((m) => record(m, 'APPROVE', reviewedHeadSha, {
        user: { login: 'stranger' }, author_association: 'NONE',
        squadWriteAccess: false,
      })),
    }, 'MISSING'],
    ['too few reviewers for a code change', {
      changedPaths: codePaths, comments: [record('bishop', 'APPROVE')],
    }, 'MISSING'],
    ['full gate, every record stale', {
      changedPaths: codePaths,
      comments: panel.map((m) => record(m, 'APPROVE', movedHeadSha)),
    }, 'MISSING'],
    ['docs-only, record stale', {
      changedPaths: docPaths, comments: [record('dallas', 'APPROVE', movedHeadSha)],
    }, 'MISSING'],
    ['reviewer is the PR author', {
      changedPaths: docPaths, comments: [record('parker', 'APPROVE')],
    }, 'MISSING'],
    ['reviewer requested changes', {
      changedPaths: codePaths, comments: [record('vasquez', 'REQUEST_CHANGES')],
    }, 'CHANGES_REQUESTED'],
    ['full panel recorded a review', {
      changedPaths: codePaths, comments: panel.map((m) => record(m, 'APPROVE')),
    }, 'REVIEWED'],
    ['docs-only, one record', {
      changedPaths: docPaths, comments: [record('dallas', 'APPROVE')],
    }, 'REVIEWED'],
    ['owner override by comment', {
      changedPaths: codePaths,
      comments: [record('jpapiez', 'APPROVE', reviewedHeadSha, {
        squadAdminOverride: true,
      })],
    }, 'APPROVED'],
    ['owner override by GitHub review', {
      changedPaths: codePaths,
      reviews: [{
        state: 'APPROVED', commitId: reviewedHeadSha, login: 'jpapiez', isAdmin: true,
      }],
    }, 'APPROVED'],
  ];

  for (const [name, input, expected] of scenarios) {
    const result = evaluateGate({ ...base, ...input });
    const evidence = fixture();
    evidence.status.state = result.state;
    evidence.status.description = result.description;
    const verdict = verifySquadVerdict(evidence);
    assert.equal(verdict.classification, expected, `${name}: ${result.description}`);
    if (result.description.startsWith('BLOCKED')) {
      assert.equal(
        verdict.blockedReason,
        result.description.slice(`BLOCKED @ ${shortSha}: `.length),
        `${name}: blockedReason must be preserved verbatim`,
      );
    }

    // The exit code is what the unattended merger branches on, so check it for
    // every description the gate can emit rather than only for hand-written
    // classifications.
    const merges = expected === 'REVIEWED' || expected === 'APPROVED';
    assert.equal(
      exitCodeFor(verdict.classification),
      merges ? 0 : { CHANGES_REQUESTED: 2, NOT_APPLICABLE: 4 }[expected] ?? 3,
      `${name}: wrong exit code for ${expected}`,
    );

    // reviewedHeadSha is the --match-head-commit argument. It must exist for
    // real evidence and must NOT exist otherwise — handing it out on a green
    // NOT_APPLICABLE would supply exactly the argument needed to merge code
    // nothing reviewed.
    if (merges) {
      assert.equal(verdict.reviewedHeadSha, reviewedHeadSha, `${name}: missing head SHA`);
    } else if (expected === 'NOT_APPLICABLE') {
      assert.equal(
        verdict.reviewedHeadSha, undefined,
        `${name}: out-of-scope results must not supply a mergeable head SHA`,
      );
    }
  }
});

test('exit codes never fail open', () => {
  assert.equal(exitCodeFor('REVIEWED'), 0);
  assert.equal(exitCodeFor('APPROVED'), 0);
  assert.equal(exitCodeFor('CHANGES_REQUESTED'), 2);
  assert.equal(exitCodeFor('MISSING'), 3);
  assert.equal(exitCodeFor('INVALID'), 3);
  assert.equal(exitCodeFor('SUPERSEDED'), 3);
  assert.equal(exitCodeFor('NOT_APPLICABLE'), 4);
  // A classification added later must not silently become "clear to merge".
  assert.equal(exitCodeFor('SOMETHING_NEW'), 3);
  assert.equal(exitCodeFor(undefined), 3);
});

test('author-authored lookalike comments cannot satisfy the verifier', () => {
  const evidence = fixture();
  const verdict = verifySquadVerdict({
    pull: evidence.pull,
    comments: [{
      user: { login: 'pr-author' },
      body: `REVIEWED (self-attested) @ ${shortSha} by bishop+hicks+vasquez`,
    }],
  });
  assert.equal(verdict.classification, 'MISSING');
});

test('newest trusted-run evidence fails closed instead of reviving an older approval', () => {
  const older = fixture();
  older.status.id = 41;
  older.status.created_at = '2026-08-07T02:59:10Z';

  const newer = fixture('REQUEST_CHANGES');
  newer.status.id = 43;
  newer.status.target_url =
    'https://github.com/OlyForge3D/PrintFarmer/actions/runs/123457';
  newer.status.description = 'BLOCKED: something else entirely';
  newer.run.id = 123457;
  newer.run.html_url = newer.status.target_url;

  const verdict = selectSquadVerdict({
    pull: newer.pull,
    statuses: [older.status, newer.status],
    loadRun: (runId) => runId === newer.run.id ? newer.run : older.run,
  });
  assert.equal(verdict.classification, 'INVALID');
});

test('rerunning an older approval cannot supersede a newer rejection', () => {
  const rejection = fixture('REQUEST_CHANGES');
  rejection.status.id = 44;
  rejection.status.created_at = '2026-08-07T03:01:10Z';
  rejection.status.target_url =
    'https://github.com/OlyForge3D/PrintFarmer/actions/runs/123458';
  rejection.run.id = 123458;
  rejection.run.html_url = rejection.status.target_url;

  const replayedApproval = fixture();
  replayedApproval.status.id = 45;
  replayedApproval.status.created_at = '2026-08-07T03:02:10Z';
  replayedApproval.run.run_attempt = 2;

  const verdict = selectSquadVerdict({
    pull: replayedApproval.pull,
    statuses: [rejection.status, replayedApproval.status],
    loadRun: (runId) =>
      runId === replayedApproval.run.id ? replayedApproval.run : rejection.run,
  });
  assert.equal(verdict.classification, 'INVALID');
});
