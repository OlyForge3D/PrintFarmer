// Pure evaluation logic for the squad pre-PR review gate.
//
// The workflow (.github/workflows/squad-review-verdict.yml) collects live data
// from the GitHub API and delegates every decision to this module so the rules
// are unit-testable. Nothing here performs I/O.
//
// Canonical verdict comment format (see the workflow header for the full spec):
//
//   <!-- squad-verdict -->
//   Squad-Reviewer: bishop
//   Squad-Verdict: APPROVE
//   Squad-Head-SHA: 0123456789abcdef0123456789abcdef01234567

export const verdictContext = 'squad/pre-pr-verdict';

/** Squad members that form the standard three-way adversarial review panel. */
export const reviewPanel = ['bishop', 'hicks', 'vasquez'];

/** Comment authors we accept a verdict from at all. */
const trustedAssociations = new Set(['OWNER', 'MEMBER', 'COLLABORATOR']);

const verdictAliases = new Map([
  ['APPROVE', 'APPROVE'],
  ['APPROVED', 'APPROVE'],
  ['REQUEST_CHANGES', 'REQUEST_CHANGES'],
  ['CHANGES_REQUESTED', 'REQUEST_CHANGES'],
  ['REJECT', 'REQUEST_CHANGES'],
]);

const reviewerLine = /^[ \t>]*Squad-Reviewer:[ \t]*(.+?)[ \t]*$/gim;
const verdictLine = /^[ \t>]*Squad-Verdict:[ \t]*([A-Za-z_]+)[ \t]*$/gim;
const headShaLine = /^[ \t>]*Squad-Head-SHA:[ \t]*([0-9a-fA-F]{40})[ \t]*$/gim;

// Prose file extensions. Path prefixes alone are not enough: `docs/**` can hold
// binary or image assets, which the policy denylist excludes.
const proseExtensions = ['.md', '.markdown', '.rst', '.adoc', '.txt'];

// Paths that always take the full gate even when they look like prose. The
// agent-instruction trees govern real agent behaviour (merge safety,
// destructive-operation permissions), which is the safety-boundary carve-out in
// .github/copilot-instructions.md. Path matching cannot judge whether a given
// edit crosses that boundary, so the conservative reading is applied: these
// trees never qualify for the one-reviewer exemption.
const fullGatePrefixes = [
  '.github/workflows/',
  '.squad/',
  '.github/agents/',
  '.github/skills/',
  '.copilot/skills/',
];

// Dependency manifests and lockfiles, matched by basename anywhere in the tree.
const manifestBasenames = new Set([
  'package.json',
  'package-lock.json',
  'npm-shrinkwrap.json',
  'yarn.lock',
  'pnpm-lock.yaml',
  'directory.packages.props',
  'directory.build.props',
  'directory.build.targets',
  'packages.lock.json',
  'paket.lock',
  'gemfile.lock',
  'podfile.lock',
  'package.resolved',
  'requirements.txt',
  'poetry.lock',
  'pipfile.lock',
  'go.mod',
  'go.sum',
  'cargo.toml',
  'cargo.lock',
  'nuget.config',
]);

// Prose whose contents carry real consequences: security policy, threat models,
// licensing terms, published API contracts.
const sensitiveProse =
  /(^|\/)(security|threat[-_ ]?model|licen[cs]e|notice|copying|code[-_ ]?of[-_ ]?conduct|api[-_ ]?contract)(\.[a-z0-9]+)?$/i;

/**
 * Reduce a squad identity to its canonical lowercase token.
 * "squad:🔍 Bishop" and "Bishop" both normalize to "bishop".
 */
export function normalizeMember(raw) {
  if (typeof raw !== 'string') {
    return undefined;
  }
  const stripped = raw
    .replace(/^squad:/i, '')
    .replace(/[^A-Za-z0-9 _.-]+/gu, ' ')
    .trim()
    .toLowerCase();
  const token = stripped.split(/[\s_.]+/).filter(Boolean).pop();
  return /^[a-z][a-z0-9-]{1,31}$/.test(token ?? '') ? token : undefined;
}

/** Build the roster of valid squad identities from repository label names. */
export function rosterFromLabels(labelNames) {
  const roster = new Set();
  for (const name of labelNames ?? []) {
    if (typeof name === 'string' && /^squad:/i.test(name)) {
      const member = normalizeMember(name);
      if (member) {
        roster.add(member);
      }
    }
  }
  return roster;
}

function singleMatch(pattern, body) {
  pattern.lastIndex = 0;
  const values = new Set();
  let match = pattern.exec(body);
  while (match) {
    values.add(match[1].trim());
    match = pattern.exec(body);
  }
  // Ambiguity (a comment quoting another verdict) is not evidence.
  return values.size === 1 ? [...values][0] : undefined;
}

/**
 * Parse one PR comment into a verdict record, or undefined when the comment is
 * not a well-formed verdict from a trusted account.
 */
export function parseVerdictComment(comment) {
  const body = typeof comment?.body === 'string' ? comment.body : '';
  const reviewerRaw = singleMatch(reviewerLine, body);
  const verdictRaw = singleMatch(verdictLine, body);
  const headShaRaw = singleMatch(headShaLine, body);
  if (!reviewerRaw || !verdictRaw || !headShaRaw) {
    return undefined;
  }
  const verdict = verdictAliases.get(verdictRaw.toUpperCase());
  const reviewer = normalizeMember(reviewerRaw);
  if (!verdict || !reviewer) {
    return undefined;
  }
  return {
    reviewer,
    verdict,
    headSha: headShaRaw.toLowerCase(),
    commenter: comment.user?.login ?? '',
    association: comment.author_association ?? '',
    trusted: trustedAssociations.has(comment.author_association ?? ''),
    // Set by the caller when the commenting account is a repository
    // administrator naming its own GitHub login as the reviewer. That is the
    // human owner speaking rather than an agent, and it is decisive.
    isSelfDeclaredAdmin: comment.squadAdminOverride === true,
    recordedAt: comment.updated_at ?? comment.created_at ?? '',
    url: comment.html_url ?? '',
  };
}

/**
 * Collect the most recent trusted verdict per reviewer identity.
 * Untrusted commenters are dropped: anyone can comment on a public PR.
 */
export function collectVerdicts(comments) {
  const latest = new Map();
  for (const comment of comments ?? []) {
    const record = parseVerdictComment(comment);
    if (!record || !record.trusted) {
      continue;
    }
    const previous = latest.get(record.reviewer);
    if (
      !previous ||
      Date.parse(record.recordedAt || 0) >= Date.parse(previous.recordedAt || 0)
    ) {
      latest.set(record.reviewer, record);
    }
  }
  return latest;
}

function isProse(path) {
  const lower = path.toLowerCase();
  return proseExtensions.some((extension) => lower.endsWith(extension));
}

/**
 * Decide whether the changed paths qualify for the one-reviewer
 * documentation-only exemption defined in .github/copilot-instructions.md.
 * Fails toward the full gate whenever classification is not obvious.
 */
export function classifyChangeScope(paths) {
  const files = (paths ?? []).filter((path) => typeof path === 'string' && path);
  if (files.length === 0) {
    return { docsOnly: false, reason: 'no changed files reported' };
  }
  for (const path of files) {
    const basename = path.split('/').pop().toLowerCase();
    if (fullGatePrefixes.some((prefix) => path.startsWith(prefix))) {
      return { docsOnly: false, reason: `${path} governs agent or CI behaviour` };
    }
    if (manifestBasenames.has(basename)) {
      return { docsOnly: false, reason: `${path} is a dependency manifest` };
    }
    if (sensitiveProse.test(path)) {
      return { docsOnly: false, reason: `${path} is security/licensing/contract prose` };
    }
    if (!isProse(path)) {
      return { docsOnly: false, reason: `${path} is not documentation` };
    }
  }
  return { docsOnly: true, reason: 'every changed path is documentation' };
}

/**
 * Resolve which squad member authored the PR.
 *
 * GitHub-account authorship is useless here because every agent acts through
 * the same owner token, so authorship is resolved at the squad-identity level:
 *   1. an explicit `Squad-Author: <member>` line in the PR body,
 *   2. otherwise the `squad:{member}` labels on the issues the PR closes,
 *   3. otherwise a known member token in the head branch name.
 */
export function resolveAuthorMembers({
  prBody = '',
  branchName = '',
  linkedIssueLabels = [],
  roster = new Set(),
} = {}) {
  const declared = singleMatch(
    /^[ \t>]*Squad-Author:[ \t]*(.+?)[ \t]*$/gim,
    prBody,
  );
  const declaredMember = normalizeMember(declared);
  if (declaredMember) {
    return { members: new Set([declaredMember]), source: 'PR body Squad-Author' };
  }

  const fromIssues = rosterFromLabels(linkedIssueLabels);
  if (fromIssues.size > 0) {
    return { members: fromIssues, source: 'squad: label on linked issue' };
  }

  const branchTokens = branchName.toLowerCase().split(/[^a-z0-9]+/).filter(Boolean);
  const fromBranch = new Set(branchTokens.filter((token) => roster.has(token)));
  if (fromBranch.size > 0) {
    return { members: fromBranch, source: 'head branch name' };
  }

  return { members: new Set(), source: 'unresolved' };
}

function shortSha(sha) {
  return sha.slice(0, 12);
}

function truncate(text, limit = 140) {
  return text.length <= limit ? text : `${text.slice(0, limit - 1)}…`;
}

/**
 * Evaluate the gate.
 *
 * Returns the commit-status state plus a precise, single-line reason. The
 * caller posts this as `squad/pre-pr-verdict` on `headSha`.
 */
export function evaluateGate({
  headSha,
  changedPaths = [],
  comments = [],
  reviews = [],
  roster = new Set(),
  authorMembers = new Set(),
  authorSource = 'unresolved',
} = {}) {
  const head = String(headSha ?? '').toLowerCase();
  const notes = [];
  if (!/^[0-9a-f]{40}$/.test(head)) {
    return {
      state: 'error',
      passed: false,
      description: 'Cannot evaluate: PR head SHA is unavailable.',
      reason: 'missing head sha',
      notes,
      requiredMembers: [],
      approvals: [],
      stale: [],
    };
  }

  // 1. Human owner override — a repository administrator approving through
  //    GitHub's native review UI at the exact current head always satisfies the
  //    gate. The owner is never locked out of the repository.
  const adminApproval = reviews.find((review) =>
    review?.state === 'APPROVED' &&
    review.isAdmin === true &&
    String(review.commitId ?? '').toLowerCase() === head);
  if (adminApproval) {
    return {
      state: 'success',
      passed: true,
      override: 'github-review',
      description: truncate(
        `APPROVE @ ${shortSha(head)} by ${adminApproval.login}`,
      ),
      reason: `administrator ${adminApproval.login} approved the current head on GitHub`,
      notes,
      requiredMembers: [],
      approvals: [adminApproval.login],
      stale: [],
    };
  }

  const verdicts = collectVerdicts(comments);
  const stale = [];
  const current = new Map();
  for (const [member, record] of verdicts) {
    if (record.headSha === head) {
      current.set(member, record);
    } else {
      stale.push(record);
    }
  }

  // 2. Human owner override via verdict comment: an administrator who names
  //    their own GitHub login as the reviewer is speaking as the owner, not as
  //    an agent, and their verdict is decisive.
  for (const record of current.values()) {
    if (record.isSelfDeclaredAdmin) {
      const passed = record.verdict === 'APPROVE';
      return {
        state: passed ? 'success' : 'failure',
        passed,
        override: 'owner-comment',
        description: truncate(
          passed
            ? `APPROVE @ ${shortSha(head)} by ${record.commenter}`
            : `BLOCKED @ ${shortSha(head)}: owner ${record.commenter} requested changes`,
        ),
        reason: `repository administrator ${record.commenter} recorded ${record.verdict}`,
        notes,
        requiredMembers: [],
        approvals: passed ? [record.commenter] : [],
        stale,
      };
    }
  }

  const scope = classifyChangeScope(changedPaths);
  notes.push(
    scope.docsOnly
      ? `Documentation-only change (${scope.reason}): one reviewer required.`
      : `Full gate (${scope.reason}): the ${reviewPanel.join('/')} panel is required.`,
  );
  if (authorMembers.size > 0) {
    notes.push(
      `PR authored by ${[...authorMembers].join(', ')} (source: ${authorSource}).`,
    );
  } else {
    notes.push(
      'PR author squad identity could not be resolved; reviewer-is-author is ' +
      'enforced only against the roster.',
    );
  }

  // 3. Reviewer eligibility.
  const eligible = new Map();
  for (const [member, record] of current) {
    if (!roster.has(member)) {
      notes.push(`Ignored ${member}: not a known squad identity.`);
      continue;
    }
    if (authorMembers.has(member)) {
      return {
        state: 'failure',
        passed: false,
        description: truncate(
          `BLOCKED @ ${shortSha(head)}: reviewer ${member} is the PR author`,
        ),
        reason:
          `reviewer ${member} is the squad member who authored this PR ` +
          `(source: ${authorSource})`,
        notes,
        requiredMembers: [],
        approvals: [],
        stale,
      };
    }
    eligible.set(member, record);
  }

  // 4. Any current-head rejection blocks, regardless of approval count.
  const rejection = [...eligible.values()].find(
    (record) => record.verdict === 'REQUEST_CHANGES');
  if (rejection) {
    return {
      state: 'failure',
      passed: false,
      description: truncate(
        `BLOCKED @ ${shortSha(head)}: ${rejection.reviewer} requested changes`,
      ),
      reason: `${rejection.reviewer} recorded REQUEST_CHANGES on the current head`,
      notes,
      requiredMembers: [],
      approvals: [],
      stale,
    };
  }

  const approvals = [...eligible.values()]
    .filter((record) => record.verdict === 'APPROVE')
    .map((record) => record.reviewer)
    .sort();

  // 5. Reviewer count and panel membership.
  const requiredCount = scope.docsOnly ? 1 : 3;
  const requiredMembers = scope.docsOnly
    ? []
    : reviewPanel.filter((member) => !authorMembers.has(member));
  if (!scope.docsOnly && requiredMembers.length < reviewPanel.length) {
    notes.push(
      `Panel members ${reviewPanel.filter((m) => authorMembers.has(m)).join(', ')} ` +
      'authored this PR; substitutes from the roster may stand in.',
    );
  }

  const missingPanel = requiredMembers.filter((member) => !approvals.includes(member));
  if (missingPanel.length > 0 || approvals.length < requiredCount) {
    const staleNote = stale.length > 0
      ? ` (stale at ${stale.map((r) => `${r.reviewer}@${shortSha(r.headSha)}`).join(', ')})`
      : '';
    const detail = approvals.length === 0 && stale.length === 0
      ? `no verdict found for ${shortSha(head)}`
      : `have ${approvals.length}/${requiredCount}` +
        (missingPanel.length > 0 ? `, missing ${missingPanel.join('+')}` : '') +
        staleNote;
    return {
      state: 'failure',
      passed: false,
      description: truncate(`BLOCKED @ ${shortSha(head)}: ${detail}`),
      reason: stale.length > 0 && approvals.length === 0
        ? `every verdict is stale: ${stale
          .map((r) => `${r.reviewer} reviewed ${r.headSha}, head is ${head}`)
          .join('; ')}`
        : detail,
      notes,
      requiredMembers,
      approvals,
      stale,
    };
  }

  return {
    state: 'success',
    passed: true,
    description: truncate(`APPROVE @ ${shortSha(head)} by ${approvals.join('+')}`),
    reason: `${approvals.length} SHA-bound approval(s) on the current head`,
    notes,
    requiredMembers,
    approvals,
    stale,
  };
}
