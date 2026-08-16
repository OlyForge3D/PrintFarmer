import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import yaml from 'js-yaml';
import {
  canAutoScope,
  classifyChangeScope,
  collectVerdicts,
  diffFingerprint,
  evaluateGate,
  fullGateFiles,
  fullGatePrefixes,
  hasAdminAccess,
  hasSquadScopeLabel,
  hasWriteAccess,
  isCarriedAcrossSync,
  normalizeMember,
  parseVerdictComment,
  resolveAuthorMembers,
  rosterFromLabels,
} from '../squad-verdict-gate.mjs';

const repositoryRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '..', '..', '..',
);
const headSha = 'a'.repeat(40);
const staleSha = 'b'.repeat(40);
const roster = rosterFromLabels([
  'squad:🔍 bishop', 'squad:🔍 hicks', 'squad:🔍 vasquez',
  'squad:⚙️ parker', 'squad:🏗️ dallas', 'priority:p1',
]);

function comment(reviewer, verdict, sha = headSha, overrides = {}) {
  return {
    id: Math.floor(Math.random() * 1e6),
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
    ...overrides,
  };
}

function gate(overrides = {}) {
  return evaluateGate({
    headSha,
    changedPaths: ['src/api/Program.cs'],
    comments: [],
    reviews: [],
    roster,
    authorMembers: new Set(['parker']),
    authorSource: 'squad: label on linked issue',
    // Scope defaults to in-scope here so each test exercises the review logic;
    // the out-of-scope path has its own dedicated tests below.
    squadLabeled: true,
    ...overrides,
  });
}

test('normalizes emoji-decorated squad identities', () => {
  assert.equal(normalizeMember('squad:🔍 Bishop'), 'bishop');
  assert.equal(normalizeMember('Bishop'), 'bishop');
  assert.equal(normalizeMember('🔍'), undefined);
  assert.ok(roster.has('vasquez'));
  assert.ok(!roster.has('p1'));
});

test('parses a canonical verdict comment', () => {
  const record = parseVerdictComment(comment('Bishop', 'APPROVE'));
  assert.equal(record.reviewer, 'bishop');
  assert.equal(record.verdict, 'APPROVE');
  assert.equal(record.headSha, headSha);
  assert.equal(record.trusted, true);
});

test('normalizes REJECT and CHANGES_REQUESTED to REQUEST_CHANGES', () => {
  for (const alias of ['REJECT', 'CHANGES_REQUESTED', 'request_changes']) {
    assert.equal(parseVerdictComment(comment('hicks', alias)).verdict, 'REQUEST_CHANGES');
  }
});

test('ignores a comment carrying two different verdicts', () => {
  const ambiguous = comment('bishop', 'APPROVE');
  ambiguous.body += '\nSquad-Verdict: REQUEST_CHANGES';
  assert.equal(parseVerdictComment(ambiguous), undefined);
});

test('requires the squad-verdict marker', () => {
  const unmarked = comment('bishop', 'APPROVE');
  unmarked.body = unmarked.body.replace('<!-- squad-verdict -->', 'Looks good:');
  assert.equal(parseVerdictComment(unmarked), undefined);
});

test('a fenced example of the format is not a binding verdict', () => {
  const illustration = comment('bishop', 'APPROVE');
  illustration.body = [
    'Record verdicts like this:',
    '```text',
    '<!-- squad-verdict -->',
    'Squad-Reviewer: bishop',
    'Squad-Verdict: APPROVE',
    `Squad-Head-SHA: ${headSha}`,
    '```',
  ].join('\n');
  assert.equal(parseVerdictComment(illustration), undefined);
});

test('a verbatim quote-reply of a verdict is not a fresh verdict', () => {
  const quoted = comment('bishop', 'APPROVE');
  quoted.body = quoted.body.split('\n').map((line) => `> ${line}`).join('\n') +
    '\n\nAgreed.';
  assert.equal(parseVerdictComment(quoted), undefined);
});

test('a repeated field is ambiguous even when the values agree', () => {
  const doubled = comment('bishop', 'APPROVE');
  doubled.body += '\nSquad-Verdict: APPROVE';
  assert.equal(parseVerdictComment(doubled), undefined);
});

test('fields hidden in an HTML comment are not a record', () => {
  // Such a comment renders as two innocuous sentences on GitHub. Counting it
  // would break the audit-trail property: a human reading the thread could not
  // see the evidence the gate used.
  const hidden = comment('bishop', 'APPROVE');
  hidden.body = [
    'Thanks, looks fine to me!',
    '<!-- squad-verdict -->',
    '<!--',
    'Squad-Reviewer: bishop',
    'Squad-Verdict: APPROVE',
    `Squad-Head-SHA: ${headSha}`,
    '-->',
    'Nothing to see here.',
  ].join('\n');
  assert.equal(parseVerdictComment(hidden), undefined);
});

test('an unterminated fence hides its contents, matching how GitHub renders it', () => {
  const unterminated = comment('bishop', 'APPROVE');
  unterminated.body = ['Here is the format:', '```text', unterminated.body].join('\n');
  assert.equal(parseVerdictComment(unterminated), undefined);
});

test('drops verdicts from accounts without repository write access', () => {
  // author_association alone is not a permission level: GitHub reports MEMBER
  // for any organisation member and COLLABORATOR for read-only collaborators.
  const readOnlyMember = comment('bishop', 'APPROVE', headSha, {
    author_association: 'MEMBER', user: { login: 'org-member' },
    squadWriteAccess: false,
  });
  assert.equal(parseVerdictComment(readOnlyMember).trusted, false);
  assert.equal(collectVerdicts([readOnlyMember], headSha).current.size, 0);
});

test('only write or better may record a review, and lookups fail closed', () => {
  // Both repositories are public: any GitHub user can comment on a PR with no
  // permission at all, and a non-collaborator resolves to `read`.
  for (const permission of ['admin', 'maintain', 'write', 'push']) {
    assert.equal(hasWriteAccess(permission), true, permission);
  }
  for (const permission of [
    'read', 'triage', 'none', '', 'ADMIN', 'Write', 'unresolved',
    undefined, null, 0, {}, [], NaN, true,
  ]) {
    assert.equal(hasWriteAccess(permission), false, String(permission));
  }
  // The owner override needs admin specifically, not merely write.
  assert.equal(hasAdminAccess('admin'), true);
  for (const permission of ['maintain', 'write', 'push', 'read', 'unresolved', undefined]) {
    assert.equal(hasAdminAccess(permission), false, String(permission));
  }
});

test('an outsider on a public repo cannot forge a review record', () => {
  // The attack this closes: a stranger opens a PR, posts a canonical APPROVE
  // comment at the current head, and Ralph merges it unattended using the
  // owner's write access.
  const outsiders = ['bishop', 'hicks', 'vasquez'].map((member) =>
    comment(member, 'APPROVE', headSha, {
      user: { login: 'drive-by-stranger' },
      author_association: 'NONE',
      // Models the live permission lookup returning `read`, which is what a
      // non-collaborator resolves to on a public repository.
      squadWriteAccess: hasWriteAccess('read'),
    }));
  const result = gate({ comments: outsiders });
  assert.equal(result.state, 'failure');
  assert.match(result.description, /^BLOCKED @ /);
  assert.match(result.description, /no authenticated review/);
  assert.ok(result.notes.some((note) => note.includes('could not be authenticated')));
});

test('an unresolvable permission lookup fails closed rather than open', () => {
  const unresolved = comment('bishop', 'APPROVE', headSha, {
    user: { login: 'rate-limited-user' },
    // Models the workflow's catch path: the lookup threw or returned an
    // unexpected shape, so no write access could be established.
    squadWriteAccess: hasWriteAccess('unresolved'),
  });
  const { current, unauthenticated } = collectVerdicts([unresolved], headSha);
  assert.equal(current.size, 0);
  assert.equal(unauthenticated.length, 1);
  assert.equal(gate({
    changedPaths: ['docs/ARCHITECTURE.md'], comments: [unresolved],
  }).state, 'failure');
});

test('identity comes from the account, never from the comment text', () => {
  // A comment merely *claiming* to be the owner is not the owner. The override
  // flag is set by the workflow from the API-supplied login plus an admin
  // permission lookup, so text alone can never trigger it.
  const impostor = comment('jpapiez', 'APPROVE', headSha, {
    user: { login: 'not-jpapiez' },
    author_association: 'NONE',
    squadWriteAccess: hasWriteAccess('read'),
  });
  const parsed = parseVerdictComment(impostor);
  assert.equal(parsed.commenter, 'not-jpapiez');
  assert.equal(parsed.trusted, false);
  assert.equal(parsed.isSelfDeclaredAdmin, false);
  assert.equal(gate({ comments: [impostor] }).state, 'failure');
});

test('drops verdicts from untrusted commenters', () => {
  const outsider = comment('bishop', 'APPROVE', headSha, {
    author_association: 'NONE', user: { login: 'drive-by' },
    squadWriteAccess: false,
  });
  assert.equal(collectVerdicts([outsider], headSha).current.size, 0);
});

test('keeps only the newest verdict per reviewer', () => {
  const older = comment('bishop', 'APPROVE', headSha, {
    updated_at: '2026-08-08T01:00:00Z',
  });
  const newer = comment('bishop', 'REQUEST_CHANGES', headSha, {
    updated_at: '2026-08-08T02:00:00Z',
  });
  const { current } = collectVerdicts([older, newer], headSha);
  assert.equal(current.get('bishop').verdict, 'REQUEST_CHANGES');
});

test('a stale verdict cannot erase a live rejection from the same reviewer', () => {
  const rejection = comment('bishop', 'REQUEST_CHANGES', headSha, {
    updated_at: '2026-08-08T01:00:00Z',
  });
  const staleApproval = comment('bishop', 'APPROVE', staleSha, {
    updated_at: '2026-08-08T03:00:00Z',
  });
  const { current, stale } = collectVerdicts([rejection, staleApproval], headSha);
  assert.equal(current.get('bishop').verdict, 'REQUEST_CHANGES');
  assert.equal(stale.length, 0);

  const result = gate({
    changedPaths: ['docs/ARCHITECTURE.md'],
    comments: [rejection, staleApproval, comment('hicks', 'APPROVE')],
  });
  assert.equal(result.state, 'failure');
  assert.match(result.description, /^REQUEST_CHANGES @ .* by bishop$/);
});

test('accepts a full panel approval at the current head', () => {
  const result = gate({
    comments: [
      comment('bishop', 'APPROVE'),
      comment('hicks', 'APPROVE'),
      comment('vasquez', 'APPROVE'),
    ],
  });
  assert.equal(result.state, 'success');
  assert.equal(
    result.description,
    `REVIEWED (self-attested) @ ${headSha.slice(0, 12)} by bishop+hicks+vasquez`,
  );
  assert.ok(
    result.notes.some((note) => note.includes('not independent review')),
    'the record must state that it is not independent review',
  );
});

test('rejects verdicts pinned to a stale SHA', () => {
  const result = gate({
    comments: [
      comment('bishop', 'APPROVE', staleSha),
      comment('hicks', 'APPROVE', staleSha),
      comment('vasquez', 'APPROVE', staleSha),
    ],
  });
  assert.equal(result.state, 'failure');
  assert.equal(result.stale.length, 3);
  assert.match(result.reason, /every recorded review is stale/);
  assert.match(result.description, /^BLOCKED @ /);
});

// --- Sync carry-forward exemption (issue #1633, "Option A") ----------------
//
// A record at an old head SHA stays valid at the new head when (1) the old
// SHA is a strict ancestor of the new head, (2) the PR's own diff against the
// base branch is byte-for-byte unchanged between the old SHA and the new
// head, AND (3) every commit introduced since review that isn't already
// reachable from base is itself content-empty against its own first parent.
// `isCarriedAcrossSync` is the pure predicate; `carriedShas` is how a caller
// (the workflow, having already computed the compares and per-commit
// lookups) tells `collectVerdicts`/`evaluateGate` which old SHAs satisfy it.
//
// Condition 2 is deliberately a diff-equality check, not "every new commit is
// an ancestor of base": a plain `git merge development` always creates a
// fresh merge commit that is itself NOT an ancestor of base (base has no idea
// it exists), so a naive commit-membership check would reject the very sync
// this feature exists to allow.
//
// Condition 3 exists because (1)+(2) alone permit a revert-then-readd trick:
// an author pushes a commit that changes the PR's contribution and a later
// commit that reverts it, landing back on the same final diff while still
// having authored real changes in the range. A clean merge sync's merge
// commit is content-empty against its own first parent (merging cleanly
// changes nothing beyond what each side already had); a merge commit that
// resolved a conflict by adding logic, or any add/revert pair, is not.

function file(overrides = {}) {
  return {
    status: 'modified',
    filename: 'src/Foo.cs',
    sha: 'a'.repeat(40),
    patch: '@@ -1 +1 @@\n-old\n+new',
    ...overrides,
  };
}

test('a pure base-sync merge (identical PR diff, clean merge commit) is carried forward', () => {
  // Both compares recover exactly the PR's own contribution: three-dot
  // compare pivots on the merge base, so `compare(base...oldSha)` still
  // finds the PR's original diff and `compare(base...newHead)` finds the
  // same diff again now that the sync merge has folded base in. Nothing
  // about the PR's own changes moved, only unrelated base history did — and
  // the workflow has already proven the sync merge commit's own diff matches
  // `compare(parent1...parent2)`, i.e. it introduced nothing beyond merging
  // its two parents.
  const reviewedDiffFiles = [file({ filename: 'src/Foo.cs' }), file({ filename: 'src/Bar.cs', sha: 'b'.repeat(40) })];
  const currentDiffFiles = [file({ filename: 'src/Bar.cs', sha: 'b'.repeat(40) }), file({ filename: 'src/Foo.cs' })];
  assert.equal(
    isCarriedAcrossSync({
      recordAncestryStatus: 'ahead',
      reviewedDiffFiles,
      currentDiffFiles,
      nonBaseCommitsIntroduceNoExtraContent: true,
    }),
    true,
  );
});

test('a new author commit that changes the PR diff is not carried forward', () => {
  const reviewedDiffFiles = [file({ filename: 'src/Foo.cs', sha: 'a'.repeat(40) })];
  // Same file, but its resulting content (and patch) changed since review —
  // this is exactly what a new author-authored commit looks like, whether it
  // stands alone or was folded into the sync merge commit as a "conflict
  // resolution".
  const currentDiffFiles = [file({ filename: 'src/Foo.cs', sha: 'c'.repeat(40), patch: '@@ -1 +1 @@\n-old\n+malicious' })];
  assert.equal(
    isCarriedAcrossSync({
      recordAncestryStatus: 'ahead',
      reviewedDiffFiles,
      currentDiffFiles,
      nonBaseCommitsIntroduceNoExtraContent: true,
    }),
    false,
  );
});

test('a new author commit that adds a file to the PR diff is not carried forward', () => {
  const reviewedDiffFiles = [file({ filename: 'src/Foo.cs' })];
  const currentDiffFiles = [file({ filename: 'src/Foo.cs' }), file({ filename: 'src/NewFile.cs', sha: 'd'.repeat(40) })];
  assert.equal(
    isCarriedAcrossSync({
      recordAncestryStatus: 'ahead',
      reviewedDiffFiles,
      currentDiffFiles,
      nonBaseCommitsIntroduceNoExtraContent: true,
    }),
    false,
  );
});

test('a revert-then-readd of the same final diff is NOT carried forward (regression for Bishop\'s second finding)', () => {
  // An author pushes a commit changing the PR, then a later commit reverting
  // it, landing back on the exact same final diff — diff-equality (condition
  // 2) alone would wrongly approve this. Neither of those commits is a
  // two-parent merge commit whose own diff matches `compare(p1...p2)`, so the
  // workflow can never prove they introduce nothing beyond a clean merge; it
  // reports nonBaseCommitsIntroduceNoExtraContent: false, and that alone must
  // block carry forward regardless of how the final diffs compare.
  const reviewedDiffFiles = [file({ filename: 'src/Foo.cs', sha: 'a'.repeat(40) })];
  const currentDiffFiles = [file({ filename: 'src/Foo.cs', sha: 'a'.repeat(40) })]; // identical to reviewed
  assert.equal(
    isCarriedAcrossSync({
      recordAncestryStatus: 'ahead',
      reviewedDiffFiles,
      currentDiffFiles,
      nonBaseCommitsIntroduceNoExtraContent: false,
    }),
    false,
  );
});

test('isCarriedAcrossSync fails closed when nonBaseCommitsIntroduceNoExtraContent is omitted', () => {
  // The caller must positively prove every non-base commit is a clean merge
  // (or that there are none); omitting the flag must never default to
  // "assume clean".
  const reviewedDiffFiles = [file()];
  assert.equal(
    isCarriedAcrossSync({
      recordAncestryStatus: 'ahead',
      reviewedDiffFiles,
      currentDiffFiles: reviewedDiffFiles,
    }),
    false,
  );
});

test('isCarriedAcrossSync fails closed when the record SHA is not a strict ancestor', () => {
  // A rebase or force-push rewrites history: GitHub's compare status is
  // 'diverged' or 'behind' rather than 'ahead', so ancestry condition (1)
  // fails regardless of whether the diffs happen to match.
  const reviewedDiffFiles = [file()];
  for (const status of ['diverged', 'behind', 'identical', undefined]) {
    assert.equal(
      isCarriedAcrossSync({
        recordAncestryStatus: status,
        reviewedDiffFiles,
        currentDiffFiles: reviewedDiffFiles,
        nonBaseCommitsIntroduceNoExtraContent: true,
      }),
      false,
      String(status),
    );
  }
});

test('isCarriedAcrossSync fails closed on an empty reviewed diff', () => {
  // The caller must always supply the PR's actual recorded diff; an empty
  // list never means "safe by default".
  assert.equal(
    isCarriedAcrossSync({
      recordAncestryStatus: 'ahead',
      reviewedDiffFiles: [],
      currentDiffFiles: [],
      nonBaseCommitsIntroduceNoExtraContent: true,
    }),
    false,
  );
});

test('isCarriedAcrossSync fails closed when either diff may be truncated', () => {
  // GitHub's compare endpoint silently caps `files` with no in-band
  // truncation signal, so a diff at or beyond that cap can never be proven
  // unchanged — equality would be unprovable, not merely unproven.
  const reviewedDiffFiles = [file()];
  assert.equal(
    isCarriedAcrossSync({
      recordAncestryStatus: 'ahead',
      reviewedDiffFiles,
      currentDiffFiles: reviewedDiffFiles,
      filesMayBeTruncated: true,
      nonBaseCommitsIntroduceNoExtraContent: true,
    }),
    false,
  );
});

test('diffFingerprint is order-independent and detects content changes (regression for the merge-cleanliness check)', () => {
  // The workflow reuses this exact function to compare a merge commit's own
  // diff against `compare(parent1...parent2).files`, proving the merge
  // introduced nothing beyond its two parents (see the module docstring for
  // why the naive "commit's own diff against first parent is empty" check is
  // wrong: that diff is naturally non-empty for a real sync merge).
  const a = file({ filename: 'src/Foo.cs' });
  const b = file({ filename: 'src/Bar.cs', sha: 'b'.repeat(40) });
  assert.equal(diffFingerprint([a, b]), diffFingerprint([b, a]), 'order must not matter');
  assert.notEqual(
    diffFingerprint([a]),
    diffFingerprint([file({ filename: 'src/Foo.cs', patch: '@@ -1 +1 @@\n-old\n+different' })]),
    'a changed patch must change the fingerprint',
  );
});

test('a pure base-sync merge carries the record forward with the carried-status wording', () => {
  const reviewedSha = staleSha;
  const result = gate({
    comments: [
      comment('bishop', 'APPROVE', reviewedSha),
      comment('hicks', 'APPROVE', reviewedSha),
      comment('vasquez', 'APPROVE', reviewedSha),
    ],
    carriedShas: new Set([reviewedSha]),
  });
  assert.equal(result.state, 'success');
  assert.equal(result.stale.length, 0);
  assert.equal(
    result.description,
    `REVIEWED (self-attested, carried across sync) @ ${headSha.slice(0, 12)} by bishop+hicks+vasquez`,
  );
  assert.match(result.reason, /3 carried forward across a pure base sync/);
  assert.deepEqual(result.carried.sort(), ['bishop', 'hicks', 'vasquez']);
  assert.ok(
    result.notes.some((note) => note.startsWith('Carried across sync:')),
    'the audit trail must record that records were carried, not freshly earned',
  );
});

test('a mix of fresh and carried approvals is still reported and still carries', () => {
  const result = gate({
    comments: [
      comment('bishop', 'APPROVE', staleSha),
      comment('hicks', 'APPROVE'), // already at the current head
      comment('vasquez', 'APPROVE'),
    ],
    carriedShas: new Set([staleSha]),
  });
  assert.equal(result.state, 'success');
  assert.match(result.description, /^REVIEWED \(self-attested, carried across sync\) @/);
  assert.deepEqual(result.carried, ['bishop']);
});

test('any author commit in the sync range still supersedes the record normally', () => {
  // The workflow's diff-equality check failed (the PR's own diff changed
  // since review), so `carriedShas` was never populated. The record must
  // supersede exactly as it does today — regression coverage for the
  // review-then-push-more threat model.
  const result = gate({
    comments: [
      comment('bishop', 'APPROVE', staleSha),
      comment('hicks', 'APPROVE', staleSha),
      comment('vasquez', 'APPROVE', staleSha),
    ],
    carriedShas: new Set(), // nothing proven carry-forward eligible
  });
  assert.equal(result.state, 'failure');
  assert.equal(result.stale.length, 3);
  assert.match(result.description, /^BLOCKED @ /);
});

test('carriedShas is keyed on the reviewed SHA, not the current head', () => {
  // Carrying record at SHA X forward to head Y must not accidentally validate
  // an unrelated record pinned to some other stale SHA Z.
  const otherStaleSha = 'c'.repeat(40);
  const { current, stale } = collectVerdicts(
    [
      comment('bishop', 'APPROVE', staleSha),
      comment('hicks', 'APPROVE', otherStaleSha),
    ],
    headSha,
    { carriedShas: new Set([staleSha]) },
  );
  assert.equal(current.get('bishop').carriedAcrossSync, true);
  assert.equal(current.has('hicks'), false);
  assert.equal(stale.length, 1);
  assert.equal(stale[0].reviewer, 'hicks');
});

test('a single approval never satisfies a code change', () => {
  const result = gate({ comments: [comment('bishop', 'APPROVE')] });
  assert.equal(result.state, 'failure');
  assert.match(result.description, /have 1\/3, missing hicks\+vasquez/);
});

test('a single approval satisfies a documentation-only change', () => {
  const result = gate({
    changedPaths: ['docs/ARCHITECTURE.md', 'README.md'],
    comments: [comment('dallas', 'APPROVE')],
  });
  assert.equal(result.state, 'success');
  assert.equal(result.approvals.join(), 'dallas');
});

test('reviewer may not be the squad member who authored the PR', () => {
  const result = gate({
    changedPaths: ['docs/ARCHITECTURE.md'],
    comments: [comment('parker', 'APPROVE')],
  });
  assert.equal(result.state, 'failure');
  assert.match(result.description, /reviewer parker is the PR author/);
});

test('any current-head rejection blocks even with enough approvals', () => {
  const result = gate({
    comments: [
      comment('bishop', 'APPROVE'),
      comment('hicks', 'APPROVE'),
      comment('vasquez', 'REQUEST_CHANGES'),
    ],
  });
  assert.equal(result.state, 'failure');
  assert.equal(result.description, `REQUEST_CHANGES @ ${headSha.slice(0, 12)} by vasquez`);
});

test('only a reviewer decision emits REQUEST_CHANGES; absent evidence is BLOCKED', () => {
  // verify-squad-verdict.mjs distinguishes these: REQUEST_CHANGES routes back
  // to the author, BLOCKED means no usable evidence exists yet.
  const noVerdict = gate();
  const insufficient = gate({ comments: [comment('bishop', 'APPROVE')] });
  const authorReview = gate({
    changedPaths: ['docs/ARCHITECTURE.md'],
    comments: [comment('parker', 'APPROVE')],
  });
  const staleOnly = gate({ comments: [comment('bishop', 'APPROVE', staleSha)] });
  for (const result of [noVerdict, insufficient, authorReview, staleOnly]) {
    assert.match(result.description, /^BLOCKED @ /);
    assert.doesNotMatch(result.description, /^REQUEST_CHANGES/);
  }
});

test('an unknown reviewer identity is ignored, not counted', () => {
  const result = gate({
    changedPaths: ['docs/ARCHITECTURE.md'],
    comments: [comment('nostromo', 'APPROVE')],
  });
  assert.equal(result.state, 'failure');
  assert.ok(result.notes.some((note) => note.includes('not a known squad identity')));
});

test('an administrator GitHub approval at the current head satisfies the gate', () => {
  const result = gate({
    reviews: [{ state: 'APPROVED', commitId: headSha, login: 'jpapiez', isAdmin: true }],
  });
  assert.equal(result.state, 'success');
  assert.equal(result.override, 'github-review');
});

test('the owner override needs no comments, files or roster — the fork path', () => {
  // Fork PRs are evaluated with reviews only, so this call shape must work.
  // The fork call site declares scope explicitly, matching the workflow.
  const result = evaluateGate({
    headSha,
    squadLabeled: true,
    reviews: [{ state: 'APPROVED', commitId: headSha, login: 'jpapiez', isAdmin: true }],
  });
  assert.equal(result.state, 'success');
  assert.equal(result.override, 'github-review');
  assert.match(result.description, /^APPROVE \(owner\) @ /);

  // ...and a non-admin approval on that same path must not pass.
  const outsider = evaluateGate({
    headSha,
    squadLabeled: true,
    reviews: [{ state: 'APPROVED', commitId: headSha, login: 'stranger', isAdmin: false }],
  });
  assert.equal(outsider.state, 'failure');
  assert.notEqual(outsider.override, 'github-review');
});

test('an administrator GitHub approval at a stale head does not satisfy the gate', () => {
  const result = gate({
    reviews: [{ state: 'APPROVED', commitId: staleSha, login: 'jpapiez', isAdmin: true }],
  });
  assert.equal(result.state, 'failure');
});

test('a later administrator change request outranks their earlier approval', () => {
  // Same admin, same SHA, approval first. Taking any matching approval would let
  // the superseded one keep satisfying the gate.
  const result = gate({
    reviews: [
      {
        id: 1, state: 'APPROVED', commitId: headSha, login: 'jpapiez', isAdmin: true,
        submittedAt: '2026-08-08T01:00:00Z',
      },
      {
        id: 2, state: 'CHANGES_REQUESTED', commitId: headSha, login: 'jpapiez',
        isAdmin: true, submittedAt: '2026-08-08T02:00:00Z',
      },
    ],
  });
  assert.equal(result.state, 'failure');
  assert.equal(result.override, 'github-review');
  assert.match(result.description, /^REQUEST_CHANGES @ .* by jpapiez$/);
});

test('a later administrator approval clears their earlier change request', () => {
  const result = gate({
    reviews: [
      {
        id: 1, state: 'CHANGES_REQUESTED', commitId: headSha, login: 'jpapiez',
        isAdmin: true, submittedAt: '2026-08-08T01:00:00Z',
      },
      {
        id: 2, state: 'APPROVED', commitId: headSha, login: 'jpapiez', isAdmin: true,
        submittedAt: '2026-08-08T02:00:00Z',
      },
    ],
  });
  assert.equal(result.state, 'success');
  assert.match(result.description, /^APPROVE \(owner\) @ /);
});

test('review recency falls back to id when timestamps tie', () => {
  const result = gate({
    reviews: [
      {
        id: 7, state: 'APPROVED', commitId: headSha, login: 'jpapiez', isAdmin: true,
        submittedAt: '2026-08-08T01:00:00Z',
      },
      {
        id: 8, state: 'CHANGES_REQUESTED', commitId: headSha, login: 'jpapiez',
        isAdmin: true, submittedAt: '2026-08-08T01:00:00Z',
      },
    ],
  });
  assert.equal(result.state, 'failure');
});

test('a COMMENTED review is not decisive and cannot clear a change request', () => {
  // GitHub does not treat COMMENTED as changing approval state; neither do we.
  const result = gate({
    reviews: [
      {
        id: 1, state: 'CHANGES_REQUESTED', commitId: headSha, login: 'jpapiez',
        isAdmin: true, submittedAt: '2026-08-08T01:00:00Z',
      },
      {
        id: 2, state: 'COMMENTED', commitId: headSha, login: 'jpapiez', isAdmin: true,
        submittedAt: '2026-08-08T03:00:00Z',
      },
    ],
  });
  assert.equal(result.state, 'failure');
  assert.match(result.description, /^REQUEST_CHANGES @ /);
});

test('a change request from a non-administrator does not block the gate', () => {
  const result = gate({
    changedPaths: ['docs/ARCHITECTURE.md'],
    comments: [comment('dallas', 'APPROVE')],
    reviews: [{
      id: 1, state: 'CHANGES_REQUESTED', commitId: headSha, login: 'stranger',
      isAdmin: false, submittedAt: '2026-08-08T02:00:00Z',
    }],
  });
  assert.equal(result.state, 'success');
});

test('the owner overrides the panel by naming their own login as reviewer', () => {
  const owner = comment('jpapiez', 'APPROVE', headSha, {
    squadAdminOverride: true,
  });
  const result = gate({ comments: [owner] });
  assert.equal(result.state, 'success');
  assert.equal(result.override, 'owner-comment');
});

test('the owner can also block through the same override path', () => {
  const owner = comment('jpapiez', 'REQUEST_CHANGES', headSha, {
    squadAdminOverride: true,
  });
  const result = gate({ comments: [owner] });
  assert.equal(result.state, 'failure');
  assert.equal(result.override, 'owner-comment');
  assert.match(result.description, /^REQUEST_CHANGES @ /);
});

test('unresolved head SHA fails closed', () => {
  assert.equal(gate({ headSha: 'not-a-sha' }).state, 'error');
});

test('documentation-only classification honours the policy carve-outs', () => {
  assert.equal(classifyChangeScope(['docs/API.md']).docsOnly, true);
  assert.equal(classifyChangeScope(['README.md']).docsOnly, true);
  assert.equal(classifyChangeScope([]).docsOnly, false);
  for (const carveOut of [
    '.github/workflows/ci.yml',
    '.github/copilot-instructions.md',
    '.github/ralph-reference.md',
    '.github/instructions/a11y.instructions.md',
    '.github/chatmodes/debug.chatmode.md',
    '.squad/templates/ralph-reference.md',
    '.github/agents/squad.agent.md',
    '.github/skills/testing/SKILL.md',
    '.copilot/skills/reflect/SKILL.md',
    'AGENTS.md',
    'CLAUDE.md',
    'SECURITY.md',
    'LICENSE',
    'docs/api-contract.md',
    'src/Web/ReactApp/package.json',
    'src/api/Program.cs',
    'docs/screenshot.png',
  ]) {
    assert.equal(
      classifyChangeScope([carveOut]).docsOnly, false,
      `${carveOut} must take the full gate`,
    );
  }
  assert.equal(classifyChangeScope(['docs/API.md', 'src/api/Program.cs']).docsOnly, false);
});

test('the documented full-gate escalation list matches the code exactly', async () => {
  // These drifted apart once: the code force-escalated paths the docs never
  // mentioned, so a reader could not tell which changes take the full gate.
  const instructions = await readFile(
    path.join(repositoryRoot, '.github', 'copilot-instructions.md'), 'utf8',
  );
  const section = instructions.slice(
    instructions.indexOf('**How the gate automates this.**'),
  ).slice(0, 2000);

  for (const prefix of fullGatePrefixes) {
    assert.ok(
      section.includes(`\`${prefix}**\``),
      `${prefix}** must be documented as always taking the full gate`,
    );
  }
  for (const file of fullGateFiles) {
    // Documented in their conventional casing.
    assert.match(
      section, new RegExp(file.replace('.', '\\.'), 'i'),
      `${file} must be documented as always taking the full gate`,
    );
  }
  // ...and nothing may be documented that the code does not actually escalate.
  const documentedPrefixes = [...section.matchAll(/`(\.[a-z]+\/)\*\*`/g)]
    .map((match) => match[1]);
  for (const documented of documentedPrefixes) {
    assert.ok(
      fullGatePrefixes.includes(documented),
      `${documented}** is documented but not escalated by the code`,
    );
  }
});

test('every exported full-gate path is actually escalated', () => {
  for (const prefix of fullGatePrefixes) {
    assert.equal(classifyChangeScope([`${prefix}notes.md`]).docsOnly, false, prefix);
  }
  for (const file of fullGateFiles) {
    assert.equal(classifyChangeScope([file]).docsOnly, false, file);
    assert.equal(classifyChangeScope([file.toUpperCase()]).docsOnly, false, file);
  }
});

test('PR authorship prefers an explicit declaration over inference', () => {
  const resolved = resolveAuthorMembers({
    prBody: 'Squad-Author: Vasquez\n\nCloses #1',
    branchName: 'dev/jpapiez/parker-issue-1',
    linkedIssueLabels: ['squad:⚙️ parker'],
    roster,
  });
  assert.deepEqual([...resolved.members], ['vasquez']);
  assert.match(resolved.source, /Squad-Author/);
});

test('PR authorship falls back to the linked issue label, then the branch name', () => {
  const fromIssue = resolveAuthorMembers({
    linkedIssueLabels: ['squad:⚙️ parker', 'priority:p1'],
    branchName: 'dev/jpapiez/dallas-thing',
    roster,
  });
  assert.deepEqual([...fromIssue.members], ['parker']);

  const fromBranch = resolveAuthorMembers({
    branchName: 'dev/jpapiez/dallas-thing',
    roster,
  });
  assert.deepEqual([...fromBranch.members], ['dallas']);

  const unresolved = resolveAuthorMembers({ branchName: 'dev/jpapiez/x', roster });
  assert.equal(unresolved.members.size, 0);
  assert.equal(unresolved.source, 'unresolved');
});

test('a panel member who authored the PR is substitutable, not a deadlock', () => {
  const result = evaluateGate({
    headSha,
    changedPaths: ['src/api/Program.cs'],
    comments: [
      comment('hicks', 'APPROVE'),
      comment('vasquez', 'APPROVE'),
      comment('dallas', 'APPROVE'),
    ],
    reviews: [],
    roster,
    authorMembers: new Set(['bishop']),
    authorSource: 'PR body Squad-Author',
    squadLabeled: true,
  });
  // Assert the substitution actually happened rather than only that the gate
  // went green: without squadLabeled this returns success as out-of-scope, so a
  // bare state check would pass while exercising none of this logic.
  assert.equal(result.scope, undefined);
  assert.equal(result.passed, true);
  assert.ok(
    result.description.startsWith('REVIEWED'),
    `expected a REVIEWED record, got: ${result.description}`,
  );
  assert.ok(
    result.description.includes('dallas'),
    `expected dallas to substitute for the authoring reviewer: ${result.description}`,
  );
});

test('auto-scoping refuses forks and unrostered self-declared authors', () => {
  const inRoster = { authorMembers: new Set(['bishop']), roster, isFork: false };
  assert.equal(canAutoScope(inRoster), true);

  // A fork PR controls both its body and its branch name, which are exactly the
  // inputs resolveAuthorMembers reads. If forks could auto-scope, an outsider
  // could place their own PR into the gate's scope.
  assert.equal(canAutoScope({ ...inRoster, isFork: true }), false);

  // resolveAuthorMembers does NOT validate a declared Squad-Author against the
  // roster, so canAutoScope must, or one line of PR body text self-scopes.
  const declared = resolveAuthorMembers({
    prBody: 'Squad-Author: attacker',
    branchName: 'feature/x',
    roster,
  });
  assert.deepEqual([...declared.members], ['attacker']);
  assert.equal(
    canAutoScope({ authorMembers: declared.members, roster, isFork: false }),
    false,
  );

  // Every member must be rostered, not merely one of them: a `some` check would
  // let an attacker ride along by naming a real member beside themselves.
  assert.equal(
    canAutoScope({ authorMembers: new Set(['bishop', 'attacker']), roster, isFork: false }),
    false,
  );
  assert.equal(
    canAutoScope({ authorMembers: new Set(['bishop', 'hicks']), roster, isFork: false }),
    true,
  );

  assert.equal(canAutoScope({ authorMembers: new Set(), roster, isFork: false }), false);
  assert.equal(canAutoScope({}), false);
});

test('workflow keeps its default-branch, SHA-binding and least-privilege controls', async () => {
  const workflow = (await readFile(
    path.join(repositoryRoot, '.github', 'workflows', 'squad-review-verdict.yml'),
    'utf8',
  )).replaceAll('\r\n', '\n');

  assert.match(workflow, /^\s+statuses: write$/m);
  // pull-requests: write is intentional — this workflow applies the scope label
  // itself. It is the ONLY write scope beyond statuses, and it does not let a PR
  // influence the judgement: the gate logic is always read from the default
  // branch. contents: write would, so it stays out.
  assert.match(workflow, /^\s+pull-requests: write$/m);
  assert.doesNotMatch(workflow, /contents: write/);
  assert.doesNotMatch(workflow, /(?:actions|checks|packages|id-token): write/);
  // A pull_request (head-ref) trigger would let a PR rewrite its own gate.
  assert.doesNotMatch(workflow, /^\s{2}pull_request:$/m);
  assert.match(workflow, /^\s{2}pull_request_target:$/m);
  assert.match(workflow, /ref: \$\{\{ github\.event\.repository\.default_branch \}\}/);
  assert.match(workflow, /persist-credentials: false/);
  assert.match(workflow, /squad-verdict-gate\.mjs/);
  assert.match(workflow, /getCollaboratorPermissionLevel/);
  assert.match(workflow, /gate\.hasWriteAccess\(await permissionOf\(login\)\)/);
  assert.match(workflow, /gate\.hasAdminAccess\(await permissionOf\(login\)\)/);
  assert.match(workflow, /squadWriteAccess: await canWrite\(login\)/);
  // Fork PRs must never accept an agent record, but an administrator's native
  // approval is still evaluated in code rather than deferred to prose.
  assert.match(workflow, /fork PR needs a repository administrator/);
  assert.match(workflow, /if \(forkResult\.override === 'github-review'\)/);
  assert.match(workflow, /types: \[created, edited, deleted\]/);
  assert.match(
    workflow,
    /run-name: "Squad review record for PR #\$\{\{ github\.event\.pull_request\.number/,
  );
  // The gate must not present itself as independent review or four-eyes.
  assert.match(workflow, /THIS IS NOT INDEPENDENT REVIEW/);
  assert.match(workflow, /NO SEPARATION OF DUTIES/);
  assert.match(workflow, /QUALITY\s*#?\s*HEURISTIC/i);
  // ...including in the job summary, which is the surface a human scans first.
  assert.match(workflow, /addHeading\('Squad review record \(self-attested\)'\)/);
  assert.match(workflow, /Self-attested review records/);
  assert.doesNotMatch(workflow, /'Approvals'/);
  assert.doesNotMatch(workflow, /Stale verdicts/);
  assert.doesNotMatch(workflow, /Squad pre-PR verdict gate/);
  assert.doesNotMatch(workflow, /\? 'PASS'/);
});

test('the gate is scoped to squad-labelled pull requests', () => {
  // Scope marker recognition.
  assert.equal(hasSquadScopeLabel([{ name: 'squad' }]), true);
  assert.equal(hasSquadScopeLabel(['squad']), true);
  assert.equal(hasSquadScopeLabel([{ name: ' Squad ' }]), true, 'trimmed, case-insensitive');
  assert.equal(hasSquadScopeLabel([]), false);
  assert.equal(hasSquadScopeLabel([{ name: 'squadron' }]), false, 'no prefix matching');
  // A member-assignment label names who is responsible, not that the PR is in
  // scope; counting it would drag routine triage back into the gate.
  assert.equal(hasSquadScopeLabel([{ name: 'squad:bishop' }]), false);
  assert.equal(hasSquadScopeLabel([null, undefined, { }]), false, 'malformed entries');

  // An unlabelled PR is out of scope and says so, rather than emitting a
  // BLOCKED that nobody can clear without staging a fake agent review.
  const out = gate({ squadLabeled: false });
  assert.equal(out.state, 'success');
  assert.equal(out.scope, 'out-of-scope');
  assert.match(out.description, /^NOT_APPLICABLE @ [0-9a-f]{12}: not a squad PR \(no 'squad' label\)$/);
  assert.equal(out.approvals.length, 0);

  // Scope is evaluated before everything else: a full panel of records on an
  // unlabelled PR still reports out of scope rather than REVIEWED, so an
  // out-of-scope PR can never accumulate merge evidence.
  const withRecords = gate({
    squadLabeled: false,
    comments: [comment('bishop', 'APPROVE'), comment('hicks', 'APPROVE'), comment('vasquez', 'APPROVE')],
  });
  assert.equal(withRecords.scope, 'out-of-scope');
  assert.doesNotMatch(withRecords.description, /REVIEWED/);

  // Callers that forget the flag must fail safe to out-of-scope, never to an
  // empty-panel evaluation that could look like a pass.
  const omitted = evaluateGate({ headSha, changedPaths: ['src/api/Program.cs'], roster });
  assert.equal(omitted.scope, 'out-of-scope');

  // Labelled PRs still take the full gate.
  const inScope = gate({ squadLabeled: true });
  assert.equal(inScope.scope, undefined);
  assert.match(inScope.description, /^BLOCKED @ [0-9a-f]{12}: no review recorded/);
});

test('the sync carry-forward workflow wiring stays intact', async () => {
  // Regression coverage for the merge-commit bug this rewrite fixes: the
  // workflow must compute the ancestry check AND the diff-equality check
  // against the base ref — not the old commit-SET-membership shape passed
  // directly into the gate — and must thread the resulting `carriedShas`
  // into `gate.evaluateGate`. A silent revert back to the commit-list design
  // would reintroduce a feature that never actually fires for a real
  // `git merge` sync. It must also still verify every non-base commit
  // introduces nothing beyond a clean merge of its own two parents
  // (regression coverage for the revert-then-readd gap, AND for the
  // follow-up bug where "content-empty against first parent" was proven
  // wrong against this repo's own history — see the module docstring).
  const workflow = await readFile(
    path.join(repositoryRoot, '.github/workflows/squad-review-verdict.yml'), 'utf8',
  );

  // Ancestry compare (condition 1) still targets old-sha...head.
  assert.match(workflow, /basehead: `\$\{oldSha\}\.\.\.\$\{headSha\}`/);
  // Diff-equality compares (condition 2) target the base ref on BOTH sides —
  // not old-sha...head or base...head alone, which is the exact shape that
  // let a sync merge commit sit in both "new" and "ahead of base" sets.
  assert.match(workflow, /basehead: `\$\{baseRef\}\.\.\.\$\{oldSha\}`/);
  assert.match(workflow, /basehead: `\$\{baseRef\}\.\.\.\$\{headSha\}`/);

  // The gate call receives file lists, not raw commit-SHA sets, plus the
  // ancestry status, a files-truncation guard, and the clean-merge proof for
  // non-base commits (condition 3).
  assert.match(workflow, /reviewedDiffFiles:\s*reviewedFiles/);
  assert.match(workflow, /currentDiffFiles:\s*currentFiles/);
  assert.match(workflow, /recordAncestryStatus:\s*ancestryCompare\.status/);
  assert.match(workflow, /filesMayBeTruncated:/);
  assert.match(workflow, /compareFilesCap/);
  assert.match(workflow, /nonBaseCommitsIntroduceNoExtraContent/);

  // Condition 3's own per-commit check: for each non-base commit, the
  // workflow must fetch its parents, require EXACTLY two (reject ordinary
  // single-parent commits and octopus merges outright), and compare the
  // commit's own diff against `compare(parent1...parent2)` — NOT check for
  // an empty own-diff, which is a real, verified-wrong assumption for a
  // clean merge (its own diff against first parent naturally includes
  // everything pulled in from the base side).
  assert.match(workflow, /getCommit\(/);
  assert.match(workflow, /parentShas\.length !== 2/);
  assert.match(workflow, /basehead: `\$\{parentShas\[0\]\}\.\.\.\$\{parentShas\[1\]\}`/);
  assert.match(workflow, /gate\.diffFingerprint\(singleCommitFiles\)/);
  assert.match(workflow, /gate\.diffFingerprint\(parentsCompareFiles\)/);
  assert.doesNotMatch(workflow, /singleCommit\.files\s*\?\?\s*\[\]\)\.length\s*>\s*0/);
  // This per-commit equality check has its own truncation exposure. For
  // `singleCommit`, the workflow checks BOTH the length-vs-cap heuristic
  // AND a stronger signal: `stats.total` (additions + deletions summed
  // across the WHOLE commit, independent of the 300-file cap on `files` —
  // verified empirically against a real commit with >300 changed files)
  // must equal the sum computed from the returned `files`. The length
  // check alone is kept because a `stats.total` match is not sufficient by
  // itself — a truncated page whose missing files happen to be pure
  // renames (0 additions, 0 deletions) would still sum-match. Either
  // signal firing disqualifies. `compare(parent1...parent2)` has no
  // equivalent total, so `parentsCompare.files` uses only the
  // length-vs-cap heuristic used for the top-level diffs.
  assert.match(workflow, /singleCommitFiles\.length >= compareFilesCap/);
  assert.match(workflow, /singleCommit\.stats\?\.total/);
  assert.match(workflow, /singleCommitFilesSum !== singleCommit\.stats\.total/);
  assert.match(workflow, /singleCommitFilesTruncated/);
  assert.match(workflow, /parentsCompareFiles\.length >= compareFilesCap/);

  // The bulk commit lists that feed condition 3 must be checked for
  // truncation too — otherwise Vasquez's pagination concern reopens the
  // instant commit lists come back into the picture for condition 3.
  assert.match(workflow, /commitsIncomplete/);
  assert.match(workflow, /total_commits/);

  // The old shape (raw commit-SHA sets passed directly as the gate's own
  // condition-2 inputs) must be gone entirely — its presence would mean the
  // original buggy design crept back in alongside the new one.
  assert.doesNotMatch(workflow, /newCommitShas/);

  // Eligibility still fails closed and still threads into evaluateGate.
  assert.match(workflow, /carriedShas\.add\(oldSha\)/);
  assert.match(workflow, /carriedShas,\s*\n/);
  assert.match(workflow, /Treating the record as superseded\./);
});

test('the scoping workflow wiring stays intact', async () => {
  const workflow = await readFile(
    path.join(repositoryRoot, '.github/workflows/squad-review-verdict.yml'), 'utf8',
  );
  // Scope is checked before the fork branch, and both real call sites declare
  // their scope explicitly rather than relying on the default.
  assert.match(workflow, /gate\.hasSquadScopeLabel\(pull\.labels \?\? \[\]\)/);
  assert.match(workflow, /squadLabeled: false/);
  assert.match(workflow, /squadLabeled: true/);
  // The label changes scope, so the status must re-evaluate when it moves.
  assert.match(workflow, /- labeled/);
  assert.match(workflow, /- unlabeled/);

  // Labelling lives in THIS workflow, guarded by canAutoScope, and needs write
  // access to do it. A separate labelling workflow is not a valid refactor: the
  // default GITHUB_TOKEN does not start new workflow runs, so its `labeled`
  // event would never re-trigger the evaluation that depends on it, and the two
  // workflows would race on `opened`.
  assert.match(workflow, /gate\.canAutoScope\(/);
  assert.match(workflow, /issues\.addLabels/);
  assert.match(workflow, /pull-requests: write/);
  assert.match(workflow, /isFork/);

  // Labelling must not migrate back out into a dedicated workflow: the default
  // GITHUB_TOKEN does not start new workflow runs, so a separate labeller's
  // `labeled` event would never re-trigger the evaluation that depends on it,
  // and the two would race on `opened`.
  //
  // The primary guard keys on CAPABILITY, not on call sites. Scoped precisely:
  // a workflow cannot write a label using GITHUB_TOKEN by ANY mechanism —
  // issues.addLabels, issues.update, GraphQL addLabelsToLabelable, `gh pr edit
  // --add-label`, actions/labeler, raw REST — without granting GITHUB_TOKEN
  // `issues: write` or `pull-requests: write` (or `write-all`). GitHub enforces
  // that at the token level; scanning for call sites cannot, because the
  // transports are open-ended and the label value can be indirected through a
  // variable. The answerable question is "which workflows COULD label with the
  // default token", which the permissions block decides; "which workflows DO
  // label" is not decidable by regex.
  //
  // KNOWN, ACCEPTED RESIDUAL: the `permissions:` block constrains only
  // GITHUB_TOKEN. A workflow holding just `contents: write` that runs, say,
  // `gh pr edit --add-label squad` authenticated with a `secrets.*` PAT or a
  // GitHub App token bypasses this guard entirely, because that token's scopes
  // are not visible in the workflow file. Detecting it is out of scope here and
  // ruled non-blocking; it lives within the documented residual below. Do not
  // read the capability claim as absolute — it is a GITHUB_TOKEN claim only.
  //
  // RESIDUAL, stated plainly: this is defence-in-depth against a future refactor
  // reintroducing a standalone labeller. It is NOT the enforced safety property.
  // That is gate.canAutoScope, which refuses forks and unrostered authors and is
  // mutation-tested above. A workflow on the allowlist is trusted not to apply
  // the bare scope label; the secondary guard checks the obvious ways it might,
  // but cannot prove absence.
  const workflowDir = path.join(repositoryRoot, '.github/workflows');
  const names = (await readdir(workflowDir)).filter((n) => /\.ya?ml$/i.test(n));
  const bodies = new Map();
  for (const name of names) {
    bodies.set(name, await readFile(path.join(workflowDir, name), 'utf8'));
  }

  // GITHUB_TOKEN can be granted label-write capability in several YAML shapes,
  // all of which GitHub Actions accepts and all of which a regex over the raw
  // text kept missing — three rounds running, each defeat a piece of valid YAML:
  //
  //   permissions: { issues: write }       flow mapping (grant not on its own line)
  //   permissions:                         block mapping with a quoted inner
  //     'issues': "write"                   key and value
  //   "permissions": {"issues": "write"}   quoted OUTER key, defeating both the
  //                                         capability regex and the /^permissions:/
  //                                         "inherits default" filter at once
  //
  // Block vs flow, quoted vs unquoted, outer-quoted vs bare — these are surface
  // syntax that all parse to the SAME structure. So DO NOT match text here: parse
  // the workflow with a real YAML parser and inspect the resulting object. Every
  // regex attempt was correct-but-incomplete, and an incomplete capability check
  // is worse than none because a miss silently admits a label writer. Parsing
  // makes the whole "valid YAML the regex didn't anticipate" class disappear —
  // if you are tempted to "simplify" this back to a regex, re-read this note.
  const parseWorkflow = (name) => {
    try {
      return yaml.load(bodies.get(name)) ?? {};
    } catch (error) {
      // Fail loudly and closed. An unparseable workflow must never be silently
      // treated as non-capable; it must break this test instead.
      assert.fail(`workflow ${name} is not parseable YAML: ${error.message}`);
    }
  };
  const docs = new Map(names.map((n) => [n, parseWorkflow(n)]));

  // Every permissions mapping declared in a file: the top-level one plus each
  // job's own `jobs.<id>.permissions`. A grant at either level is a capability.
  const permissionBlocks = (doc) => {
    const blocks = [];
    if (doc && typeof doc === 'object' && 'permissions' in doc) {
      blocks.push(doc.permissions);
    }
    const jobs = doc && typeof doc === 'object' ? doc.jobs : undefined;
    if (jobs && typeof jobs === 'object') {
      for (const job of Object.values(jobs)) {
        if (job && typeof job === 'object' && 'permissions' in job) {
          blocks.push(job.permissions);
        }
      }
    }
    return blocks;
  };

  // A single permissions value grants label write if it is the `write-all`
  // scalar shorthand, or a mapping granting `issues: write` or
  // `pull-requests: write`. `permissions: {}` parses to an empty object and
  // grants nothing (a reviewer probe pins this); `read-all`, `read`, `none`,
  // and an absent block likewise grant nothing.
  const blockGrantsLabelWrite = (permissions) => {
    if (permissions === 'write-all') return true;
    if (typeof permissions !== 'object' || permissions === null) return false;
    return permissions.issues === 'write' ||
      permissions['pull-requests'] === 'write';
  };

  const grantsLabelWrite = (name) =>
    permissionBlocks(docs.get(name)).some(blockGrantsLabelWrite);

  // Workflows permitted to hold label-write capability. Adding an entry is a
  // deliberate act: any of these could apply the bare scope label and silently
  // place a PR in scope, which is what the review gate exists to prevent.
  const permittedLabelWriters = new Set([
    'close-linked-issues.yml',
    'squad-blocked-label-sync.yml',
    'squad-heartbeat.yml',
    'squad-issue-assign.yml',
    'squad-label-enforce.yml',
    'squad-review-verdict.yml',
    'squad-triage.yml',
    'sync-squad-labels.yml',
  ]);

  const capable = names.filter((n) => grantsLabelWrite(n));
  assert.deepEqual(
    capable.filter((n) => !permittedLabelWriters.has(n)), [],
    'a workflow not on the allowlist grants itself issues/pull-requests write and could ' +
    'therefore apply the bare squad scope label; if legitimate add it above, but first ' +
    'confirm it cannot place a PR in scope',
  );
  // Fail closed the other way too: a stale entry reserves a name and silently
  // widens the set a reintroduced labeller could occupy.
  assert.deepEqual(
    [...permittedLabelWriters].filter((n) => !capable.includes(n)), [],
    'the allowlist names a workflow that no longer holds label-write capability; prune it',
  );

  // A workflow omitting `permissions` inherits the repository default. Pinning
  // the exact set of blockless workflows catches a workflow GAINING or LOSING an
  // explicit `permissions:` block — an added block might grant label write, and a
  // removed one drops the file back onto the (unknown) repository default.
  //
  // "Has no explicit permissions block" is decided from the PARSED object, not
  // from text: the earlier `/^\s*permissions:/m` filter shared the quoted-outer-
  // key blind spot (`"permissions": {...}`), so a file could grant label write
  // yet still be counted as inheriting the default. `permissionBlocks(...)`
  // reflects the real structure at both the top level and every job.
  //
  // What this does NOT detect: the value of the repository-wide
  // `default_workflow_permissions` API setting. This test reads workflow files
  // only; it cannot see that setting, so flipping it from restricted to
  // permissive would make every blockless workflow label-capable WITHOUT
  // changing any file here and WITHOUT failing this test. That flip is guarded
  // elsewhere (org/repo settings), not by this assertion.
  const inheritsDefault = names
    .filter((n) => permissionBlocks(docs.get(n)).length === 0)
    .sort();
  assert.deepEqual(
    inheritsDefault,
    ['bootstrap-ubuntu-ci.yml', 'ci-lint.yml', 'compose-validate.yml',
     'enforce-path-casing.yml', 'prusa-preseed-build.yml', 'slicer-assets-ci.yml'],
    'a workflow gained or lost an explicit permissions block; one without a block ' +
    'inherits the repository default and is label-capable if that default is permissive',
  );

  // Secondary guard: a permitted writer must not start applying the BARE scope
  // label. Those seven apply `squad:*`, never `squad`. Covers the literal forms
  // plus assignment to a variable, which is how an indirected CLI call reads it.
  //
  // The last pattern (any `key: squad` line) is intentionally broad and WILL
  // also flag an innocuous line such as `name: squad` in a permitted writer.
  // That is left as-is on purpose: it fails toward MORE review, matching the
  // over-match doctrine above, and — critically — it is the only form that still
  // catches a label indirected through a variable like `SCOPE_LABEL: squad`,
  // which a reviewer specifically required. Narrowing it to exclude `name:`
  // would trade a harmless, self-announcing false positive (a failing test with
  // the offending file named) for the risk of a silent gap, a bad trade here.
  const bareScopeLabel = [
    /squadScopeLabel|canAutoScope/,
    /labels:\s*\[[^\]]*(['"])squad\1/,
    /--add-label[=\s]+['"]?squad['"]?(?![\w:-])/,
    /^\s*[A-Za-z_][\w-]*:\s*(['"])?squad\1?\s*$/m,
  ];
  const strays = capable.filter(
    (n) => n !== 'squad-review-verdict.yml' &&
           bareScopeLabel.some((re) => re.test(bodies.get(n))),
  );
  assert.deepEqual(
    strays, [],
    `squad scope labelling must stay in squad-review-verdict.yml; found: ${strays.join(', ')}`,
  );
});
