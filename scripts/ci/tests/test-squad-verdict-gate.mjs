import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  classifyChangeScope,
  collectVerdicts,
  evaluateGate,
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
  assert.equal(result.description, `APPROVE @ ${headSha.slice(0, 12)} by bishop+hicks+vasquez`);
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
  assert.match(result.reason, /every verdict is stale/);
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

test('an administrator GitHub approval at a stale head does not satisfy the gate', () => {
  const result = gate({
    reviews: [{ state: 'APPROVED', commitId: staleSha, login: 'jpapiez', isAdmin: true }],
  });
  assert.equal(result.state, 'failure');
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
  assert.match(workflow, /squadWriteAccess: await canWrite\(login\)/);
  assert.match(workflow, /types: \[created, edited, deleted\]/);
  assert.match(
    workflow,
    /run-name: "Squad verdict gate for PR #\$\{\{ github\.event\.pull_request\.number/,
  );
});
