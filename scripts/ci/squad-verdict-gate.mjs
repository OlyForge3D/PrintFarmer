// Pure evaluation logic for the squad pre-PR review record.
//
// ⚠️ THIS IS NOT INDEPENDENT REVIEW AND PROVIDES NO SEPARATION OF DUTIES.
// Every squad agent runs under the repository owner's authority and posts
// through the owner's token, so a reviewer agent "approving" an author agent is
// the owner approving the owner's own work. The reviewer-is-not-the-author rule
// implemented below is a QUALITY HEURISTIC — a second agent with fresh context
// catches more than the author re-reading its own output — and is deliberately
// NOT presented as an independence or four-eyes control. The owner accepted
// self-attested agent review for single-maintainer operation (issue #1310).
//
// What the record genuinely provides: SHA binding (a record is valid only for
// the exact commit it names), presence (the gate fails when nothing reviewed the
// change at all), an audit trail, and legible failure reasons.
//
// The workflow (.github/workflows/squad-review-verdict.yml) collects live data
// from the GitHub API and delegates every decision to this module so the rules
// are unit-testable. Nothing here performs I/O.
//
// Canonical record comment format (see the workflow header for the full spec):
//
//   <!-- squad-verdict -->
//   Squad-Reviewer: bishop
//   Squad-Verdict: APPROVE
//   Squad-Head-SHA: 0123456789abcdef0123456789abcdef01234567

export const verdictContext = 'squad/pre-pr-verdict';

/**
 * Squad members that form the standard review panel. Three agents reviewing
 * instead of one is a quality measure, not three independent parties.
 */
export const reviewPanel = ['bishop', 'hicks', 'vasquez'];

/**
 * Comment `author_association` values accepted as a cheap PRE-FILTER only.
 *
 * ⚠️ This is NOT the authorisation check. Both repositories are public, so ANY
 * GitHub user can comment on a pull request without any permission at all, and
 * `author_association` varies with organisation configuration. The authoritative
 * check is `hasWriteAccess` below, applied to the live collaborator-permission
 * API result. Never accept a record on association alone.
 */
const trustedAssociations = new Set(['OWNER', 'MEMBER', 'COLLABORATOR']);

/**
 * Repository permission levels that may record a review.
 *
 * The authoritative author-authentication check. Ralph merges autonomously
 * using the OWNER's write access, so a forgeable record would effectively lend
 * the owner's privileges to whoever forged it — an unauthenticated path from a
 * stranger to `development`. Everything below write is rejected, including
 * `read` (which is what a non-collaborator returns on a public repository) and
 * `triage`.
 *
 * FAILS CLOSED by construction: an unresolved lookup, a rate-limited call, an
 * unexpected shape, `undefined`, or any unrecognised string all return false.
 *
 * No bot identity is allowlisted. Allowlisting one would re-create the
 * bot-hop laundering pattern rejected on issue #1310: a workflow dispatched by
 * the owner posting as `github-actions[bot]` adds no judgement, it only makes
 * the metadata imply someone else reviewed.
 */
export function hasWriteAccess(permission) {
  return permission === 'admin' || permission === 'maintain' ||
    permission === 'write' || permission === 'push';
}

/** Repository permission level required for the owner-override path. */
export function hasAdminAccess(permission) {
  return permission === 'admin';
}

/**
 * Sentinel that must appear in a verdict comment. Requiring it stops prose that
 * merely *illustrates* the format — a reviewer explaining the protocol, a doc
 * excerpt — from being read as a binding verdict.
 */
export const verdictMarker = '<!-- squad-verdict -->';

const fencedBlock = /^[ \t]*(?:```|~~~)[^\n]*\n[\s\S]*?^[ \t]*(?:```|~~~)[ \t]*$/gm;
// An opening fence with no closing fence is fenced through end of body, which
// is how GitHub renders it. Without this, an unterminated fence displays as
// code but parses as live text.
const unterminatedFence = /^[ \t]*(?:```|~~~)[^\n]*\n[\s\S]*$/m;
const quotedLine = /^[ \t]*>.*$/gm;
// Any HTML comment other than the marker. Fields hidden inside one render as
// nothing on GitHub, so counting them would break the audit-trail property:
// a human reading the thread could not see the evidence the gate used.
const htmlComment = /<!--[\s\S]*?(?:-->|$)/g;

const verdictAliases = new Map([
  ['APPROVE', 'APPROVE'],
  ['APPROVED', 'APPROVE'],
  ['REQUEST_CHANGES', 'REQUEST_CHANGES'],
  ['CHANGES_REQUESTED', 'REQUEST_CHANGES'],
  ['REJECT', 'REQUEST_CHANGES'],
]);

const reviewerLine = /^[ \t]*Squad-Reviewer:[ \t]*(.+?)[ \t]*$/gim;
const verdictLine = /^[ \t]*Squad-Verdict:[ \t]*([A-Za-z_]+)[ \t]*$/gim;
const headShaLine = /^[ \t]*Squad-Head-SHA:[ \t]*([0-9a-fA-F]{40})[ \t]*$/gim;

// Prose file extensions. Path prefixes alone are not enough: `docs/**` can hold
// binary or image assets, which the policy denylist excludes.
const proseExtensions = ['.md', '.markdown', '.rst', '.adoc', '.txt'];

// Trees that always take the full gate even when they look like prose. These
// hold agent instructions, review policy, and CI definitions: whether a given
// edit moves an agent's safety boundary cannot be judged from the path, so the
// conservative reading of the carve-out in .github/copilot-instructions.md is
// applied. `.github/**` is covered in full — it is process configuration, not
// product documentation, and it contains the merge-evidence rules that the
// unattended merger itself obeys.
//
// Exported so a test can assert the docs enumerate exactly this list. The two
// drifted apart once already, which is how an agent-instruction file could have
// taken the one-reviewer path without anyone noticing.
export const fullGatePrefixes = [
  '.github/',
  '.squad/',
  '.copilot/',
  '.claude/',
  '.cursor/',
];

// Root-level agent-instruction files, which are agent behaviour by content even
// though nothing in their path says so.
export const fullGateFiles = new Set([
  'agents.md',
  'claude.md',
  'gemini.md',
  'copilot.md',
  '.cursorrules',
]);

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

/**
 * Remove anything GitHub renders as code or as a quotation of someone else's
 * text. A verbatim quote-reply of a record, or an example inside a fence, must
 * not register as a fresh record.
 */
function stripQuotedAndFenced(body) {
  return body
    .replace(fencedBlock, '')
    .replace(unterminatedFence, '')
    .replace(quotedLine, '');
}

/**
 * Everything the gate is willing to read: visible text only, with hidden HTML
 * comments removed so the parsed evidence matches what a human sees.
 */
function sanitizeBody(body) {
  return stripQuotedAndFenced(body).replace(htmlComment, '');
}

function countMatches(pattern, body) {
  pattern.lastIndex = 0;
  const values = [];
  let match = pattern.exec(body);
  while (match) {
    values.push(match[1].trim());
    match = pattern.exec(body);
  }
  // More than one occurrence of a field is ambiguous regardless of whether the
  // values agree, so it is not evidence.
  return values.length === 1 ? values[0] : undefined;
}

function singleMatch(pattern, body) {
  return countMatches(pattern, sanitizeBody(body));
}

/**
 * Parse one PR comment into a review record, or undefined when the comment is
 * not a well-formed record from a trusted account.
 */
export function parseVerdictComment(comment) {
  const body = typeof comment?.body === 'string' ? comment.body : '';
  const visible = stripQuotedAndFenced(body);
  if (visible.split(verdictMarker).length !== 2) {
    return undefined;
  }
  // Drop the marker itself, then every remaining HTML comment, so fields cannot
  // be smuggled in text that renders as nothing.
  const clean = visible.split(verdictMarker).join('\n').replace(htmlComment, '');
  const reviewerRaw = countMatches(reviewerLine, clean);
  const verdictRaw = countMatches(verdictLine, clean);
  const headShaRaw = countMatches(headShaLine, clean);
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
    // Two independent conditions, both required. `author_association` is not a
    // permission level — GitHub reports MEMBER for any organisation member and
    // COLLABORATOR for read-only collaborators — so the caller must also
    // confirm write access through the collaborator permission API.
    trusted: trustedAssociations.has(comment.author_association ?? '') &&
      comment.squadWriteAccess === true,
    // Set by the caller when the commenting account is a repository
    // administrator naming its own GitHub login as the reviewer. That is the
    // human owner speaking rather than an agent, and it is decisive.
    isSelfDeclaredAdmin: comment.squadAdminOverride === true,
    recordedAt: comment.updated_at ?? comment.created_at ?? '',
    url: comment.html_url ?? '',
  };
}

/**
 * Split trusted verdicts into the reviewer's decision *on the current head* and
 * everything else.
 *
 * Current-head records and stale records are ranked in separate pools on
 * purpose. Taking a single newest-overall record per reviewer would let a
 * comment naming an old SHA erase that reviewer's live REQUEST_CHANGES, since
 * the stale record would win on timestamp and then be filtered out of the
 * current pool. A stale comment can never displace a current-head one.
 */
export function collectVerdicts(comments, headSha) {
  const head = String(headSha ?? '').toLowerCase();
  const current = new Map();
  const staleLatest = new Map();
  const unauthenticated = [];
  for (const comment of comments ?? []) {
    const record = parseVerdictComment(comment);
    if (!record) {
      continue;
    }
    if (!record.trusted) {
      // Kept and reported rather than silently dropped: "no review recorded" is
      // a misleading failure message when a record existed but its author could
      // not be authenticated.
      unauthenticated.push(record);
      continue;
    }
    const pool = record.headSha === head ? current : staleLatest;
    const previous = pool.get(record.reviewer);
    if (
      !previous ||
      Date.parse(record.recordedAt || 0) >= Date.parse(previous.recordedAt || 0)
    ) {
      pool.set(record.reviewer, record);
    }
  }
  const stale = [...staleLatest.values()]
    .filter((record) => !current.has(record.reviewer));
  return { current, stale, unauthenticated };
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
    if (!path.includes('/') && fullGateFiles.has(basename)) {
      return { docsOnly: false, reason: `${path} is a root agent-instruction file` };
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

  // 1. Owner override through GitHub's native review UI at the exact current
  //    head. Only each administrator's MOST RECENT decisive review at that head
  //    counts: taking any matching approval would let an earlier APPROVED
  //    survive after the same administrator later recorded CHANGES_REQUESTED on
  //    the same commit. COMMENTED reviews are not decisive — GitHub itself does
  //    not treat them as changing approval state — and DISMISSED clears.
  const latestAdminReview = new Map();
  for (const review of reviews ?? []) {
    if (
      review?.isAdmin !== true ||
      String(review.commitId ?? '').toLowerCase() !== head ||
      !['APPROVED', 'CHANGES_REQUESTED', 'DISMISSED'].includes(review.state)
    ) {
      continue;
    }
    const login = String(review.login ?? '').toLowerCase();
    const previous = latestAdminReview.get(login);
    const rank = (entry) => [
      Date.parse(entry.submittedAt ?? '') || 0,
      Number(entry.id) || 0,
    ];
    if (!previous) {
      latestAdminReview.set(login, review);
      continue;
    }
    const [newTime, newId] = rank(review);
    const [oldTime, oldId] = rank(previous);
    if (newTime > oldTime || (newTime === oldTime && newId >= oldId)) {
      latestAdminReview.set(login, review);
    }
  }

  // A standing administrator change request outranks another approval.
  const adminBlock = [...latestAdminReview.values()]
    .find((review) => review.state === 'CHANGES_REQUESTED');
  if (adminBlock) {
    return {
      state: 'failure',
      passed: false,
      override: 'github-review',
      description: truncate(
        `REQUEST_CHANGES @ ${shortSha(head)} by ${adminBlock.login}`,
      ),
      reason:
        `administrator ${adminBlock.login} requested changes on the current ` +
        'head through GitHub review',
      notes,
      requiredMembers: [],
      approvals: [],
      stale: [],
    };
  }

  const adminApproval = [...latestAdminReview.values()]
    .find((review) => review.state === 'APPROVED');
  if (adminApproval) {
    return {
      state: 'success',
      passed: true,
      override: 'github-review',
      description: truncate(
        `APPROVE (owner) @ ${shortSha(head)} by ${adminApproval.login}`,
      ),
      reason: `administrator ${adminApproval.login} approved the current head on GitHub`,
      notes,
      requiredMembers: [],
      approvals: [adminApproval.login],
      stale: [],
    };
  }

  const { current, stale, unauthenticated } = collectVerdicts(comments, head);
  if (unauthenticated.length > 0) {
    notes.push(
      `Rejected ${unauthenticated.length} record(s) whose author could not be ` +
      'authenticated with repository write access: ' +
      `${[...new Set(unauthenticated.map((r) => r.commenter || '(unknown)'))].join(', ')}. ` +
      'Both repositories are public, so anyone can comment; only verified ' +
      'write-access authors count.',
    );
  }

  // 2. Owner override via record comment: an administrator who names their own
  //    GitHub login as the reviewer is speaking as the owner rather than as an
  //    agent. This is the one path that is a real authorisation rather than a
  //    self-attested agent record, so it is labelled `(owner)`.
  for (const record of current.values()) {
    if (record.isSelfDeclaredAdmin) {
      const passed = record.verdict === 'APPROVE';
      return {
        state: passed ? 'success' : 'failure',
        passed,
        override: 'owner-comment',
        description: truncate(
          passed
            ? `APPROVE (owner) @ ${shortSha(head)} by ${record.commenter}`
            : `REQUEST_CHANGES @ ${shortSha(head)} by ${record.commenter}`,
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
      'PR author squad identity could not be resolved; the reviewer-is-not-the-' +
      'author quality heuristic is applied only against the roster.',
    );
  }
  notes.push(
    'Self-attested: every agent here runs under the owner\'s authority, so this ' +
    'record is not independent review and provides no separation of duties.',
  );

  // 3. Reviewer eligibility. Excluding the author agent is a quality heuristic
  //    (fresh context catches more than self-re-reading), not an independence
  //    guarantee — the author agent and the reviewer agent share one principal.
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

  // 4. Any current-head rejection blocks, regardless of approval count. This is
  //    the only path that emits a REQUEST_CHANGES status: it is a reviewer
  //    decision, unlike the BLOCKED states below, which mean evidence is
  //    absent rather than negative.
  const rejection = [...eligible.values()].find(
    (record) => record.verdict === 'REQUEST_CHANGES');
  if (rejection) {
    return {
      state: 'failure',
      passed: false,
      description: truncate(
        `REQUEST_CHANGES @ ${shortSha(head)} by ${rejection.reviewer}`,
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
      ? (unauthenticated.length > 0
        ? `no authenticated review for ${shortSha(head)} ` +
          `(${unauthenticated.length} unauthenticated)`
        : `no review recorded for ${shortSha(head)}`)
      : `have ${approvals.length}/${requiredCount}` +
        (missingPanel.length > 0 ? `, missing ${missingPanel.join('+')}` : '') +
        staleNote;
    return {
      state: 'failure',
      passed: false,
      description: truncate(`BLOCKED @ ${shortSha(head)}: ${detail}`),
      reason: stale.length > 0 && approvals.length === 0
        ? `every recorded review is stale: ${stale
          .map((r) => `${r.reviewer} reviewed ${r.headSha}, head is ${head}`)
          .join('; ')}`
        : detail,
      notes,
      requiredMembers,
      approvals,
      stale,
    };
  }

  // Deliberately NOT "APPROVE": this is a record that reviewer agents examined
  // this exact commit, self-attested under the owner's authority. Only the owner
  // override path emits an `APPROVE (owner)` status, because only that path is a
  // real authorisation by a distinct principal.
  return {
    state: 'success',
    passed: true,
    description: truncate(
      `REVIEWED (self-attested) @ ${shortSha(head)} by ${approvals.join('+')}`,
    ),
    reason:
      `${approvals.length} SHA-bound self-attested review record(s) on the ` +
      'current head',
    notes,
    requiredMembers,
    approvals,
    stale,
  };
}
