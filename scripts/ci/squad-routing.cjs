'use strict';

/**
 * Shared squad triage routing logic.
 *
 * Loaded by `.github/workflows/squad-triage.yml`, `squad-issue-assign.yml`, and
 * `sync-squad-labels.yml` via `require()` inside `actions/github-script`, and
 * exercised directly by `scripts/ci/tests/test-squad-routing.mjs`.
 *
 * Two invariants this module exists to enforce:
 *
 * 1. Keyword routing matches whole words, never substrings. A raw
 *    `text.includes('ui')` matches the `ui` inside build, builder, require,
 *    required, quick, suite, guide, equivalent — which in a .NET/C# repository
 *    means nearly every issue body looks like frontend work.
 * 2. `squad:*` labels have exactly one canonical spelling per member, derived
 *    from `slugify(name)`. Roster names carry emoji prefixes (`🏗️ Dallas`), so
 *    every producer and consumer of a label must go through `slugify`.
 */

/** Canonical `squad:{slug}` label suffix for a roster name. */
function slugify(text) {
  return String(text)
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '');
}

/** Canonical label for a roster name, e.g. `🏗️ Dallas` -> `squad:dallas`. */
function memberLabel(name) {
  return `squad:${slugify(name)}`;
}

/** True when `label` is a `squad:*` member label in its canonical spelling. */
function isCanonicalMemberLabel(label) {
  if (!label.startsWith('squad:')) {
    return false;
  }
  const suffix = label.slice('squad:'.length);
  return suffix.length > 0 && slugify(suffix) === suffix;
}

function escapeRegExp(text) {
  return String(text).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Whole-word containment test.
 *
 * `\b` is used at both ends, so `ui` matches `UI` and `the ui broke` but not
 * `builder`; `ci` matches `ci fails` but not `specific` or `efficiency`.
 * Multi-word phrases such as `ef core` are matched with flexible whitespace.
 */
function hasWord(text, word) {
  if (!text || !word) {
    return false;
  }
  const pattern = escapeRegExp(word.trim()).replace(/\\?\s+/g, '\\s+');
  // `#` and `.` are non-word characters, so a trailing \b would never match
  // tokens like `c#` or `.net`. Fall back to a lookaround in that case.
  const left = /^[\w]/.test(word.trim()) ? '\\b' : '(?<![\\w#])';
  const right = /[\w]$/.test(word.trim()) ? '\\b' : '(?![\\w#])';
  return new RegExp(`${left}${pattern}${right}`, 'i').test(text);
}

// Weight 2 = a term that only appears when the domain is genuinely in play.
// Weight 1 = a term that is suggestive but common across domains (`bug`, `fix`,
// `design`), so it can break a tie but cannot carry a domain on its own.
const DOMAINS = [
  {
    id: 'frontend',
    reason: 'Issue relates to frontend/UI work',
    role: /front[\s-]?end|\bui\b|\bux\b|designer/i,
    keywords: [
      ['ui', 2], ['ux', 2], ['frontend', 2], ['front-end', 2],
      ['css', 2], ['tailwind', 2], ['react', 2], ['jsx', 2], ['tsx', 2],
      ['component', 2], ['components', 2], ['stylesheet', 2],
      ['button', 1], ['buttons', 1], ['page', 1], ['pages', 1],
      ['layout', 1], ['design', 1], ['modal', 1], ['dialog', 1],
    ],
  },
  {
    id: 'backend',
    reason: 'Issue relates to backend/API work',
    role: /back[\s-]?end|\bapi\b|server/i,
    keywords: [
      ['backend', 2], ['back-end', 2], ['api', 2], ['endpoint', 2],
      ['endpoints', 2], ['database', 2], ['sql', 2], ['ef core', 2],
      ['entity framework', 2], ['dbcontext', 2], ['migration', 2],
      ['migrations', 2], ['signalr', 2], ['controller', 2],
      ['controllers', 2], ['repository', 1], ['query', 1], ['queries', 1],
      ['server', 1], ['auth', 1], ['authentication', 1], ['c#', 1],
      ['.net', 1], ['dotnet', 1],
    ],
  },
  {
    id: 'test',
    reason: 'Issue relates to testing/quality work',
    role: /test|\bqa\b|quality/i,
    keywords: [
      ['test', 2], ['tests', 2], ['testing', 2], ['coverage', 2],
      ['flaky', 2], ['xunit', 2], ['vitest', 2], ['playwright', 2],
      ['regression', 1], ['bug', 1], ['fix', 1], ['assertion', 1],
    ],
  },
  {
    id: 'devops',
    reason: 'Issue relates to DevOps/infrastructure work',
    role: /devops|infra|\bops\b|deploy|platform/i,
    keywords: [
      ['docker', 2], ['dockerfile', 2], ['kubernetes', 2], ['nginx', 2],
      ['pipeline', 2], ['infrastructure', 2], ['deploy', 2],
      ['deployment', 2], ['ci', 2], ['cd', 2], ['workflow', 2],
      ['github actions', 2], ['compose', 1], ['container', 1],
      ['runner', 1], ['release', 1],
    ],
  },
];

// A term in the title is a deliberate statement of what the issue is about; the
// same term buried in a body is incidental. Weighting the title higher stops a
// body that merely mentions `build` from outranking a title that says
// `endpoint`.
const TITLE_WEIGHT = 3;
const BODY_WEIGHT = 1;

/**
 * Score one domain against an issue.
 *
 * Each keyword contributes at most once regardless of how many times it occurs,
 * so a long body cannot win by repetition alone.
 */
function scoreDomain(domain, title, body) {
  let score = 0;
  const matched = [];
  for (const [word, weight] of domain.keywords) {
    const inTitle = hasWord(title, word);
    const inBody = !inTitle && hasWord(body, word);
    if (!inTitle && !inBody) {
      continue;
    }
    score += weight * (inTitle ? TITLE_WEIGHT : BODY_WEIGHT);
    matched.push(word);
  }
  return { id: domain.id, score, matched, reason: domain.reason };
}

/**
 * Route an issue to a squad member.
 *
 * Scoring is domain-outer / member-inner: every domain that has a member on the
 * roster is scored independently and the highest scorer wins. The previous
 * member-outer loop let roster ordering silently decide the assignment, and
 * because frontend was tested first it won nearly every race.
 *
 * @param {{title?: string, body?: string}} issue
 * @param {Array<{name: string, role: string}>} members roster order preserved
 * @param {{name: string, role: string}} lead fallback owner
 * @returns {{member: object, reason: string, domain: string|null, scores: object[]}}
 */
function routeIssue(issue, members, lead) {
  const title = issue.title || '';
  const body = issue.body || '';

  const scores = DOMAINS
    // Only score domains somebody on the roster can actually own.
    .filter((domain) => members.some((m) => domain.role.test(m.role || '')))
    .map((domain) => scoreDomain(domain, title, body))
    .filter((entry) => entry.score > 0)
    .sort((a, b) => b.score - a.score);

  const best = scores[0];
  const runnerUp = scores[1];

  // A tie is genuine ambiguity, not a reason to pick whoever sorted first.
  if (!best || (runnerUp && runnerUp.score === best.score)) {
    return {
      member: lead,
      reason: best
        ? 'Ambiguous domain signals — assigned to Lead for further analysis'
        : 'No specific domain match — assigned to Lead for further analysis',
      domain: null,
      scores,
    };
  }

  const domain = DOMAINS.find((d) => d.id === best.id);
  const member = members.find((m) => domain.role.test(m.role || ''));
  return { member, reason: best.reason, domain: best.id, scores };
}

module.exports = {
  DOMAINS,
  hasWord,
  isCanonicalMemberLabel,
  memberLabel,
  routeIssue,
  scoreDomain,
  slugify,
};
