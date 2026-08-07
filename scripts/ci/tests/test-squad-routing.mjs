import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const {
  classifySquadLabels,
  hasWord,
  isCanonicalMemberLabel,
  isRosterExcluded,
  memberLabel,
  routeIssue,
  slugify,
} = require('../squad-routing.cjs');
const ralph = require('../../../.squad/templates/ralph-triage.js');

// The full live roster from .squad/team.md, in file order. Roster order
// matters: under the old member-outer loop, Ripley (Frontend) preceding
// Lambert (Backend) is what made frontend win nearly every race.
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
  { name: '🔍 Bishop', role: 'Code Reviewer (Claude Opus 5)' },
  { name: '🔍 Hicks', role: 'Code Reviewer (GPT-5.6 Sol)' },
  { name: '🔍 Vasquez', role: 'Code Reviewer (Gemini 3.1 Pro Preview)' },
  { name: '⚛️ Drake', role: 'Frontend Dev' },
  { name: '📋 Scribe', role: 'Session Logger' },
  { name: '🔄 Ralph', role: 'Work Monitor' },
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

  // Multi-word and punctuated forms the keyword table actually relies on.
  assert.equal(hasWord('built on ASP.NET Core 10', 'asp.net'), true);
  assert.equal(hasWord('a front end concern', 'front end'), true);
  assert.equal(hasWord('uses Entity Framework Core', 'entity framework'), true);
  assert.equal(hasWord('see GitHub Actions logs', 'github actions'), true);

  // `cd src && dotnet build` in repro steps must not read as DevOps.
  assert.equal(hasWord('run cd src && dotnet build', 'ci/cd'), false);

  // A keyword containing a backslash must not be silently corrupted into `\s+`.
  assert.equal(hasWord('a\\ b', 'a\\ b'), true);
  assert.equal(hasWord('assb', 'a\\ b'), false);
});

test('boilerplate from the issue template cannot carry the test domain', () => {
  // Every issue in this repo ships "## Proposed fix" / "## How to verify" /
  // "add a regression test", so testing boilerplate must not outscore a real
  // domain signal. This is the regression Bishop and Hicks both flagged.
  const boilerplate = [
    '## Proposed fix', 'Rewrite the query.',
    '## How to verify', '1. Apply the fix and re-measure.',
    '2. Add a regression test; run the tests. Effort: S/M plus tests.',
  ].join('\n');

  const backend = routeIssue(
    { title: 'perf: /api/spools endpoint is slow', body: boilerplate },
    members,
    lead,
  );
  assert.equal(backend.domain, 'backend', 'backend must beat test boilerplate');

  const frontend = routeIssue(
    { title: 'perf: Files page fetches 2000 records', body: boilerplate },
    members,
    lead,
  );
  assert.equal(frontend.domain, 'frontend', 'frontend must beat test boilerplate');
});

test('ordinary bug reports route to the owning domain, not the Tester', () => {
  const ui = routeIssue(
    {
      title: 'bug: printer screen is blank',
      body: 'The React view renders nothing. Please fix.',
    },
    members,
    lead,
  );
  assert.equal(ui.domain, 'frontend');
  assert.equal(ui.member.name, '⚛️ Ripley');

  const api = routeIssue(
    {
      title: 'fix: saving a printer fails',
      body: 'The endpoint returns 500.',
    },
    members,
    lead,
  );
  assert.equal(api.domain, 'backend');
  assert.equal(api.member.name, '🔧 Lambert');
});

test('a QA-titled issue routes to the Tester', () => {
  const result = routeIssue(
    {
      title: 'QA: qualify operator-first redesign for iOS beta',
      body: 'Run the acceptance pass before shipping.',
    },
    members,
    lead,
  );
  assert.equal(result.domain, 'test');
  assert.equal(result.member.name, '🧪 Kane');
});

test('a keyword concept scores once, not once per surface form', () => {
  // `test`, `tests` and `testing` are one concept; matching all three must not
  // score more than matching one.
  const one = routeIssue({ title: '', body: 'add a test' }, members, lead);
  const many = routeIssue(
    { title: '', body: 'add a test; the tests fail; testing is broken' },
    members,
    lead,
  );
  const scoreOf = (r) => (r.scores.find((s) => s.id === 'test') || {}).score;
  assert.equal(scoreOf(one), scoreOf(many));
});

test('Ralph routes identically to the triage workflow', () => {
  // .squad/templates/ralph-triage.js is a second live router, executed by
  // squad-heartbeat.yml. It carried the same substring bug. If `squad upgrade`
  // ever overwrites it and reintroduces member-first substring matching, this
  // assertion fails.
  const cases = [
    { title: issue1236Title, body: issue1236Body },
    { title: 'fix: PrinterCard component CSS overflow', body: 'Tailwind layout' },
    { title: 'ci: docker build fails in the deployment pipeline', body: '' },
    { title: 'test: raise coverage for SliceJobQueue', body: 'flaky xunit' },
    { title: 'feat: add /api/spools endpoint', body: '' },
  ];
  for (const issue of cases) {
    const expected = routeIssue(issue, members, lead);
    const actual = ralph.findRoleKeywordMatch(issue, members);
    assert.ok(actual, `Ralph returned no match for "${issue.title}"`);
    assert.equal(
      actual.agent.name,
      expected.member.name,
      `Ralph disagreed with squad-triage on "${issue.title}"`,
    );
  }
});

test('Ralph declines ambiguous issues instead of guessing', () => {
  assert.equal(
    ralph.findRoleKeywordMatch({ title: 'Improve things', body: '' }, members),
    null,
  );
});

test('malformed issues do not throw', () => {
  for (const issue of [
    {},
    { title: 'only a title' },
    { title: undefined, body: 'only a body' },
    { title: 'x', body: null },
    { title: '', body: '' },
  ]) {
    assert.doesNotThrow(() => routeIssue(issue, members, lead));
    assert.ok(routeIssue(issue, members, lead).member);
  }
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

test('the roster exclusion fires despite the emoji prefix', () => {
  // `.squad/team.md` rows are "| 📋 Scribe | Session Logger | … |", so the
  // literal `cells[0] !== 'Scribe'` this replaces never fired: squad:scribe
  // was synced as a label and Scribe was an eligible triage target.
  assert.equal(isRosterExcluded('📋 Scribe', ['scribe']), true);
  assert.equal(isRosterExcluded('Scribe', ['scribe']), true);
  assert.equal(isRosterExcluded('🔄 Ralph', ['scribe', 'ralph']), true);
  assert.equal(isRosterExcluded('🔧 Lambert', ['scribe', 'ralph']), false);
  assert.equal(isRosterExcluded('⚛️ Ripley', ['scribe']), false);
});

test('Ralph parses the real roster without Scribe or Ralph', () => {
  const teamMd = readFileSync(
    path.join(here, '..', '..', '..', '.squad', 'team.md'),
    'utf8',
  );
  const roster = ralph.parseRoster(teamMd);

  assert.ok(roster.length > 0, 'roster must parse');
  const labels = roster.map((m) => m.label);
  assert.equal(labels.includes('squad:scribe'), false, 'Scribe must be excluded');
  assert.equal(labels.includes('squad:ralph'), false, 'Ralph must be excluded');
  assert.equal(labels.includes('squad:lambert'), true);
  assert.equal(labels.includes('squad:dallas'), true);

  // Every generated label must round-trip: the label a member is given
  // resolves back to that same member, which is what squad-issue-assign.yml
  // does when a `squad:*` label is applied.
  for (const member of roster) {
    assert.match(member.label, /^squad:[a-z0-9-]+$/, `${member.label} not canonical`);
    const suffix = member.label.slice('squad:'.length);
    const resolved = roster.find((m) => slugify(m.name) === suffix);
    assert.equal(resolved.name, member.name, `${member.label} did not round-trip`);
  }
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

test('the label audit classifies by roster membership, not spelling', () => {
  const rosterLabels = members.map((m) => memberLabel(m.name));
  rosterLabels.push('squad:copilot');

  const { duplicates, retired } = classifySquadLabels(
    [
      'squad',                 // base triage label — not a member label
      'squad:dallas',          // canonical, on roster
      'squad:copilot',         // load-bearing, never synced from the roster
      'squad:🏗️ dallas',       // duplicate of a current member
      'squad:⚛️ ripley',       // duplicate of a current member
      'squad:kaylee',          // retired, canonical spelling
      'squad:📱 frost',        // retired, emoji spelling
      'squad:🔨 anvil',        // retired, emoji spelling
      'bug',                   // unrelated
    ],
    rosterLabels,
  );

  assert.deepEqual(
    duplicates,
    [
      { name: 'squad:⚛️ ripley', canonical: 'squad:ripley' },
      { name: 'squad:🏗️ dallas', canonical: 'squad:dallas' },
    ],
  );

  // A former member's label is retired in EITHER spelling. There is no current
  // owner to migrate onto, and deleting it would erase closed-issue history.
  assert.deepEqual(retired, ['squad:kaylee', 'squad:📱 frost', 'squad:🔨 anvil']);

  // Never flag the labels the workflow itself manages.
  const flagged = [...duplicates.map((d) => d.name), ...retired];
  assert.equal(flagged.includes('squad'), false);
  assert.equal(flagged.includes('squad:copilot'), false);
  assert.equal(flagged.includes('squad:dallas'), false);
  assert.equal(flagged.includes('bug'), false);
});
