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
  const token = word.trim();
  // `escapeRegExp` never escapes whitespace, so match plain runs of it. An
  // optional `\\?` here would mangle any keyword containing a backslash.
  const pattern = escapeRegExp(token).replace(/\s+/g, '\\s+');
  // `#` and `.` are non-word characters, so a `\b` on that side would never
  // match tokens like `c#` or `.net`. Fall back to a lookaround in that case.
  const left = /^\w/.test(token) ? '\\b' : '(?<![\\w#])';
  const right = /\w$/.test(token) ? '\\b' : '(?![\\w#])';
  return new RegExp(`${left}${pattern}${right}`, 'i').test(text);
}

// Keywords are grouped into *concepts*. A concept scores at most once no
// matter how many of its surface forms appear, so `test`/`tests`/`testing`
// cannot triple-count and a long body cannot win by repetition.
//
// Weight 2 = a term that only appears when the domain is genuinely in play.
// Weight 1 = suggestive but common across domains, so it can break a tie but
// cannot carry a domain on its own.
//
// `titlePriority` is reserved for explicit ownership intent and applies whenever
// its concept matches in a title. `prefixPriority` applies only when the title
// starts with one of the concept's `priorityPrefixes`. Priority is compared
// before score. Conventional ownership prefixes are the strongest signal,
// followed by explicit documentation artifacts, then iOS implementation terms.
//
// Deliberately absent: `bug` and `fix`. They describe the *type* of an issue,
// not its domain — every domain gets bug reports — and treating them as
// testing signals routed ordinary frontend and backend bugs to the Tester.
const DOMAINS = [
  {
    id: 'frontend',
    reason: 'Issue relates to frontend/UI work',
    role: /front[\s-]?end|\bui\b|\bux\b|designer/i,
    keywords: [
      { id: 'ui', weight: 2, forms: ['ui'] },
      { id: 'ux', weight: 2, forms: ['ux'] },
      { id: 'frontend', weight: 2, forms: ['frontend', 'front-end', 'front end'] },
      { id: 'css', weight: 2, forms: ['css', 'stylesheet', 'tailwind'] },
      { id: 'react', weight: 2, forms: ['react', 'jsx', 'tsx'] },
      { id: 'component', weight: 2, forms: ['component', 'components'] },
      { id: 'page', weight: 1, forms: ['page', 'pages'] },
      { id: 'layout', weight: 1, forms: ['layout'] },
      { id: 'button', weight: 1, forms: ['button', 'buttons'] },
      { id: 'modal', weight: 1, forms: ['modal', 'dialog'] },
      { id: 'design', weight: 1, forms: ['design'] },
      // The React app is the only npm surface in this .NET repo.
      { id: 'npm', weight: 1, forms: ['npm', 'package.json', 'node_modules', 'vite'] },
    ],
  },
  {
    id: 'backend',
    reason: 'Issue relates to backend/API work',
    role: /back[\s-]?end|\bapi\b|server/i,
    keywords: [
      { id: 'backend', weight: 2, forms: ['backend', 'back-end', 'back end'] },
      { id: 'api', weight: 2, forms: ['api'] },
      { id: 'endpoint', weight: 2, forms: ['endpoint', 'endpoints'] },
      {
        id: 'database',
        weight: 2,
        forms: ['database', 'sql', 'dbcontext', 'ef core', 'entity framework'],
      },
      { id: 'migration', weight: 2, forms: ['migration', 'migrations'] },
      { id: 'signalr', weight: 2, forms: ['signalr'] },
      { id: 'controller', weight: 2, forms: ['controller', 'controllers'] },
      { id: 'query', weight: 1, forms: ['query', 'queries'] },
      { id: 'server', weight: 1, forms: ['server'] },
      { id: 'auth', weight: 1, forms: ['auth', 'authentication'] },
      { id: 'dotnet', weight: 1, forms: ['c#', '.net', 'asp.net', 'dotnet'] },
      { id: 'repository', weight: 1, forms: ['repository'] },
    ],
  },
  {
    id: 'test',
    reason: 'Issue relates to testing/quality work',
    role: /test|\bqa\b|quality/i,
    keywords: [
      // Every issue in this repo carries a "How to verify" / "add a regression
      // test" section, so these words in a *body* are boilerplate, not a
      // statement of ownership. They only signal the testing domain when the
      // author put them in the title.
      //
      // Priority is deliberately prefix-only. A plain title mention such as
      // "Deployment tests mutate Docker config" belongs to DevOps (#1250).
      {
        id: 'test',
        weight: 2,
        titleOnly: true,
        prefixPriority: 4,
        priorityPrefixes: ['test:', 'tests:', 'testing:'],
        forms: ['test', 'tests', 'testing'],
      },
      {
        id: 'regression',
        weight: 1,
        titleOnly: true,
        forms: ['regression'],
      },
      {
        id: 'assertion',
        weight: 1,
        titleOnly: true,
        forms: ['assert', 'assertion'],
      },
      {
        id: 'qa',
        weight: 2,
        prefixPriority: 4,
        priorityPrefixes: ['qa:'],
        forms: ['qa'],
      },
      { id: 'coverage', weight: 2, forms: ['coverage'] },
      { id: 'flaky', weight: 2, forms: ['flaky'] },
      {
        id: 'harness',
        weight: 2,
        forms: ['xunit', 'vitest', 'playwright'],
      },
    ],
  },
  {
    id: 'devops',
    reason: 'Issue relates to DevOps/infrastructure work',
    role: /devops|infra|\bops\b|deploy|platform/i,
    keywords: [
      { id: 'docker', weight: 2, forms: ['docker', 'dockerfile', 'compose'] },
      { id: 'k8s', weight: 2, forms: ['kubernetes', 'k8s'] },
      { id: 'nginx', weight: 2, forms: ['nginx'] },
      { id: 'pipeline', weight: 2, forms: ['pipeline'] },
      { id: 'ci', weight: 2, forms: ['ci', 'ci/cd', 'github actions'] },
      { id: 'deploy', weight: 2, forms: ['deploy', 'deployment'] },
      { id: 'infra', weight: 2, forms: ['infrastructure', 'infra'] },
      { id: 'workflow', weight: 2, forms: ['workflow'] },
      { id: 'container', weight: 1, forms: ['container'] },
      { id: 'runner', weight: 1, forms: ['runner'] },
      { id: 'release', weight: 1, forms: ['release'] },
    ],
  },
  {
    id: 'ios',
    reason: 'Issue relates to iOS/mobile app work',
    role: /\bios\b|swift|mobile/i,
    defaultRole: /\bios\b.*\bdeveloper\b/i,
    keywords: [
      { id: 'native-app', weight: 2, forms: ['ios app', 'mobile app'] },
      { id: 'swiftui', weight: 2, titlePriority: 1, forms: ['swiftui'] },
      { id: 'swift', weight: 2, forms: ['swift'] },
      { id: 'xcode', weight: 2, forms: ['xcode', 'xcodebuild'] },
      {
        id: 'ios-networking',
        weight: 3,
        titlePriority: 1,
        forms: [
          'ios networking',
          'ios api client',
          'ios rest client',
          'ios http client',
          'ios signalr client',
          'ios json decoding',
          'ios codable',
          'urlsession',
        ],
      },
      {
        id: 'apple-ecosystem',
        weight: 1,
        forms: ['iphone', 'ipad', 'apns', 'testflight'],
      },
    ],
    ownerRules: [
      {
        role: /\bios networking\b/i,
        titleForms: [
          'ios networking',
          'urlsession',
          'ios api client',
          'ios rest client',
          'ios http client',
          'ios signalr client',
          'ios json decoding',
          'ios codable',
        ],
      },
    ],
  },
  {
    id: 'docs',
    reason: 'Issue relates to documentation work',
    role: /documentation|technical writer|\bdocs\b/i,
    keywords: [
      {
        id: 'docs-intent',
        weight: 5,
        titleOnly: true,
        titlePriority: 4,
        titlePrefixes: ['docs:', 'doc:', 'documentation:'],
        forms: [],
      },
      {
        id: 'readme',
        weight: 5,
        titleOnly: true,
        titlePriority: 3,
        forms: ['readme'],
      },
      {
        id: 'changelog',
        weight: 3,
        titleOnly: true,
        titlePriority: 3,
        forms: ['changelog'],
      },
      {
        id: 'api-docs',
        weight: 3,
        titleOnly: true,
        titlePriority: 3,
        forms: ['api docs', 'api documentation', 'api reference'],
      },
      {
        id: 'guide',
        weight: 3,
        titleOnly: true,
        titlePriority: 3,
        forms: [
          'user guide',
          'developer guide',
          'deployment guide',
          'configuration guide',
          'installation guide',
          'tutorial',
          'tutorials',
        ],
      },
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
 * Each *concept* contributes at most once regardless of how many of its surface
 * forms occur or how often, so neither `test`/`tests`/`testing` nor a long body
 * can win by repetition. Concepts marked `titleOnly` are ignored in the body.
 */
function scoreDomain(domain, title, body) {
  let score = 0;
  let priority = 0;
  const matched = [];
  const normalizedTitle = (title || '').trimStart().toLowerCase();
  for (const concept of domain.keywords) {
    const inTitle = (concept.titlePrefixes || [])
      .some((prefix) => normalizedTitle.startsWith(prefix))
      || concept.forms.some((form) => hasWord(title, form));
    const inBody = !inTitle
      && !concept.titleOnly
      && concept.forms.some((form) => hasWord(body, form));
    if (!inTitle && !inBody) {
      continue;
    }
    score += concept.weight * (inTitle ? TITLE_WEIGHT : BODY_WEIGHT);
    if (inTitle) {
      priority = Math.max(priority, concept.titlePriority || 0);
      if (
        (concept.priorityPrefixes || [])
          .some((prefix) => normalizedTitle.startsWith(prefix))
      ) {
        priority = Math.max(priority, concept.prefixPriority || 0);
      }
    }
    matched.push(concept.id);
  }
  return {
    id: domain.id,
    score,
    priority,
    matched,
    reason: domain.reason,
  };
}

function findDomainMember(domain, members, title) {
  for (const rule of domain.ownerRules || []) {
    if (rule.titleForms.some((form) => hasWord(title, form))) {
      const specialist = members.find((member) => rule.role.test(member.role || ''));
      if (specialist) {
        return specialist;
      }
    }
  }
  return members.find((member) => domain.defaultRole?.test(member.role || ''))
    || members.find((member) => domain.role.test(member.role || ''));
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
    .sort((a, b) => b.priority - a.priority || b.score - a.score);

  const best = scores[0];
  const runnerUp = scores[1];

  // A tie is genuine ambiguity, not a reason to pick whoever sorted first.
  if (
    !best
    || (
      runnerUp
      && runnerUp.priority === best.priority
      && runnerUp.score === best.score
    )
  ) {
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
  const member = findDomainMember(domain, members, title);
  return { member, reason: best.reason, domain: best.id, scores };
}

/**
 * Split the repository's `squad:*` labels into duplicates and retired labels.
 *
 * Classification is by *roster membership of the canonical form*, not by
 * spelling. `squad:🏗️ dallas` is a duplicate because Dallas is on the roster
 * and already owns `squad:dallas`. `squad:📱 frost` is retired, not a
 * duplicate — Frost is gone, so there is no canonical label to migrate onto
 * and deleting it would erase the assignment history of every closed issue
 * that carries it.
 *
 * @param {string[]} existingNames every label name in the repository
 * @param {string[]} rosterLabels canonical labels for the current roster
 * @returns {{duplicates: Array<{name: string, canonical: string}>, retired: string[]}}
 */
function classifySquadLabels(existingNames, rosterLabels) {
  const roster = new Set(rosterLabels);
  const duplicates = [];
  const retired = [];

  for (const name of [...existingNames].sort()) {
    if (!name.startsWith('squad:') || roster.has(name)) {
      continue;
    }
    const canonical = memberLabel(name.slice('squad:'.length));
    if (canonical && roster.has(canonical)) {
      duplicates.push({ name, canonical });
    } else {
      retired.push(name);
    }
  }

  return { duplicates, retired };
}

module.exports = {
  DOMAINS,
  classifySquadLabels,
  hasWord,
  isCanonicalMemberLabel,
  memberLabel,
  routeIssue,
  scoreDomain,
  slugify,
};
