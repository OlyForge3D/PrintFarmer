import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const {
  hasWord,
  isCanonicalMemberLabel,
  memberLabel,
  routeIssue,
  slugify,
} = require('../squad-routing.cjs');

// The live roster from .squad/team.md, in file order. Roster order matters:
// under the old member-outer loop, Ripley (Frontend) preceding Lambert
// (Backend) is what made frontend win nearly every race.
const members = [
  { name: '🏗️ Dallas', role: 'Lead' },
  { name: '⚛️ Ripley', role: 'Frontend Dev' },
  { name: '🔧 Lambert', role: 'Backend Dev' },
  { name: '📱 Hudson', role: 'iOS Developer' },
  { name: '🌐 Gorman', role: 'iOS Networking' },
  { name: '🧪 Kane', role: 'Tester' },
  { name: '📝 Ash', role: 'Documentation Specialist' },
  { name: '🔍 Brett', role: 'Researcher' },
  { name: '⚙️ Parker', role: 'DevOps & Deployment Engineer' },
  { name: '🎨 Newt', role: 'Designer (Industrial UI)' },
];
const lead = members[0];

const issue1236Body = readFileSync(
  path.join(here, 'fixtures', 'issue-1236-body.md'),
  'utf8',
);
const issue1236Title =
  'perf: GetQueueStatsAsync loads the entire SliceJobs table 4x per call on '
  + 'the slice-submission path';

test('hasWord matches standalone tokens, not substrings', () => {
  // The exact substring false positives that misrouted real issues.
  for (const word of [
    'build', 'builder', 'require', 'required', 'quick', 'suite',
    'guide', 'equivalent', 'distinguish', 'circuit',
  ]) {
    assert.equal(hasWord(word, 'ui'), false, `"${word}" must not match "ui"`);
  }
  for (const word of ['specific', 'efficiency', 'decision', 'precision']) {
    assert.equal(hasWord(word, 'ci'), false, `"${word}" must not match "ci"`);
  }
  assert.equal(hasWord('pagination is broken', 'page'), false);
  assert.equal(hasWord('rapid retries', 'api'), false);

  // Genuine standalone tokens still match, case-insensitively.
  assert.equal(hasWord('the UI is broken', 'ui'), true);
  assert.equal(hasWord('improve the ux', 'ux'), true);
  assert.equal(hasWord('CI is red', 'ci'), true);
  assert.equal(hasWord('page 2 of the wizard', 'page'), true);
  assert.equal(hasWord('the /api/foo endpoint', 'api'), true);
  assert.equal(hasWord('uses EF  Core mapping', 'ef core'), true);
  assert.equal(hasWord('written in C# today', 'c#'), true);
});

test('issue #1236 routes to Backend, not Frontend (the reported misroute)', () => {
  const result = routeIssue(
    { title: issue1236Title, body: issue1236Body },
    members,
    lead,
  );
  assert.equal(result.domain, 'backend');
  assert.equal(result.member.name, '🔧 Lambert');
  assert.equal(memberLabel(result.member.name), 'squad:lambert');

  // The old code matched only because `builder` and `requiredCapabilitiesJson`
  // contain the letters "ui". Frontend must now score nothing at all.
  const frontend = result.scores.find((s) => s.id === 'frontend');
  assert.equal(frontend, undefined, 'frontend must not score on #1236');
});

test('build/require noise alone never routes to Frontend', () => {
  const result = routeIssue(
    {
      title: 'chore: the builder requires a quick suite of guide updates',
      body: 'This is required to distinguish equivalent build outputs.',
    },
    members,
    lead,
  );
  assert.notEqual(result.domain, 'frontend');
  assert.equal(result.member.name, lead.name);
});

test('a genuine frontend issue still routes to Frontend', () => {
  const result = routeIssue(
    {
      title: 'fix: PrinterCard component CSS overflow on the printers page',
      body: 'The Tailwind layout breaks; the React component needs a new '
        + 'stylesheet rule. Requires a rebuild of the UI bundle.',
    },
    members,
    lead,
  );
  assert.equal(result.domain, 'frontend');
  assert.equal(result.member.name, '⚛️ Ripley');
});

test('a DevOps issue routes to DevOps', () => {
  const result = routeIssue(
    {
      title: 'ci: docker build fails in the deployment pipeline',
      body: 'The Dockerfile layer cache is invalidated on every run, so the '
        + 'CI pipeline times out. Affects deploy to staging infrastructure.',
    },
    members,
    lead,
  );
  assert.equal(result.domain, 'devops');
  assert.equal(result.member.name, '⚙️ Parker');
});

test('a testing issue routes to the Tester', () => {
  const result = routeIssue(
    {
      title: 'test: raise coverage for SliceJobQueue',
      body: 'Several xunit tests are flaky and coverage has regressed.',
    },
    members,
    lead,
  );
  assert.equal(result.domain, 'test');
  assert.equal(result.member.name, '🧪 Kane');
});

test('an ambiguous issue falls through to the Lead', () => {
  const result = routeIssue(
    { title: 'Improve things', body: 'It should be better.' },
    members,
    lead,
  );
  assert.equal(result.domain, null);
  assert.equal(result.member.name, lead.name);
  assert.match(result.reason, /Lead/);
});

test('an evenly balanced issue falls through to the Lead, not roster order', () => {
  const result = routeIssue(
    { title: 'css endpoint', body: '' },
    members,
    lead,
  );
  assert.equal(result.domain, null);
  assert.equal(result.member.name, lead.name);
  assert.match(result.reason, /Ambiguous/);
});

test('roster order does not decide the winner', () => {
  const issue = { title: 'fix: /api/printers endpoint returns 500', body: '' };
  const forward = routeIssue(issue, members, lead);
  const reversed = routeIssue(issue, [...members].reverse(), lead);
  assert.equal(forward.domain, 'backend');
  assert.equal(reversed.domain, 'backend');
  assert.equal(forward.member.name, reversed.member.name);
});

test('a title match outranks incidental body noise', () => {
  const result = routeIssue(
    {
      title: 'feat: add /api/spools endpoint',
      body: 'Design note: the page that consumes this can come later.',
    },
    members,
    lead,
  );
  assert.equal(result.domain, 'backend');
});

test('domains with nobody on the roster are never selected', () => {
  const frontendless = members.filter((m) => m.role !== 'Frontend Dev'
    && m.role !== 'Designer (Industrial UI)');
  const result = routeIssue(
    { title: 'fix: CSS component layout', body: '' },
    frontendless,
    lead,
  );
  assert.notEqual(result.domain, 'frontend');
});

test('slugify produces exactly one canonical label per member', () => {
  assert.equal(memberLabel('🏗️ Dallas'), 'squad:dallas');
  assert.equal(memberLabel('⚛️ Ripley'), 'squad:ripley');
  assert.equal(memberLabel('⚙️ Parker'), 'squad:parker');
  assert.equal(memberLabel('Dallas'), 'squad:dallas');

  // Emoji and plain spellings must collapse to the same canonical label.
  for (const member of members) {
    const label = memberLabel(member.name);
    assert.equal(memberLabel(label.slice('squad:'.length)), label);
    assert.equal(slugify(member.name), slugify(slugify(member.name)));
  }
});

test('isCanonicalMemberLabel rejects the emoji duplicates', () => {
  assert.equal(isCanonicalMemberLabel('squad:dallas'), true);
  assert.equal(isCanonicalMemberLabel('squad:copilot'), true);
  assert.equal(isCanonicalMemberLabel('squad:🏗️ dallas'), false);
  assert.equal(isCanonicalMemberLabel('squad:⚛️ ripley'), false);
  assert.equal(isCanonicalMemberLabel('squad:Dallas'), false);
  assert.equal(isCanonicalMemberLabel('squad'), false);
  assert.equal(isCanonicalMemberLabel('squad:'), false);
});
