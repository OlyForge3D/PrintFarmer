import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  classifyChangeScope,
  collectVerdicts,
  evaluateGate,
  fullGateFiles,
  fullGatePrefixes,
  hasAdminAccess,
  hasSquadScopeLabel,
  hasWriteAccess,
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
  });
  assert.equal(result.state, 'success');
});

test('workflow keeps its default-branch, SHA-binding and least-privilege controls', async () => {
  const workflow = (await readFile(
    path.join(repositoryRoot, '.github', 'workflows', 'squad-review-verdict.yml'),
    'utf8',
  )).replaceAll('\r\n', '\n');

  assert.match(workflow, /^\s+statuses: write$/m);
  assert.doesNotMatch(workflow, /pull-requests: write/);
  assert.doesNotMatch(workflow, /contents: write/);
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

  const labeller = await readFile(
    path.join(repositoryRoot, '.github/workflows/squad-pr-label.yml'), 'utf8',
  );
  assert.match(labeller, /pull-requests: write/);
  assert.match(labeller, /types: \[opened, reopened\]/);
  assert.match(labeller, /gate\.resolveAuthorMembers/);
  // Never checks out PR-controlled code to classify the PR.
  assert.match(labeller, /ref: \$\{\{ github\.event\.repository\.default_branch \}\}/);
});
