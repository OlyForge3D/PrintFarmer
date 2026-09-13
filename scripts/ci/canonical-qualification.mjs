import { repository, releaseBuildChecks, releaseReviewStatus, requireThat, requireString,
  validateApprovalMode } from './release-policy.mjs';

export const qualificationWorkflow = '.github/workflows/qualify-canonical-release.yml';
export const evidenceWorkflow = '.github/workflows/record-canonical-qualification.yml';
const shaPattern = /^[a-f0-9]{40}$/;
const idPattern = /^[1-9][0-9]*$/;
const loginPattern = /^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,38})$/;
const maximumAge = 24 * 60 * 60 * 1000;
const runUrl = id => `https://github.com/${repository}/actions/runs/${id}`;

export function qualificationTitle(channel, validationRun, comment, nativePr, mode) {
  requireThat(['stable', 'insider'].includes(channel), 'Invalid qualification channel');
  for (const id of [validationRun, comment]) requireString(String(id), idPattern, 'qualification ID');
  requireString(String(nativePr), /^(0|[1-9][0-9]*)$/, 'native PR');
  validateApprovalMode(mode);
  requireThat(mode !== 'single-maintainer' || String(nativePr) === '0', 'Unexpected native evidence');
  requireThat(mode !== 'separation-of-duties' || String(nativePr) !== '0', 'Missing native evidence');
  return `Canonical qualification ${channel} CI ${validationRun} review ${comment} PR ${nativePr} mode ${mode}`;
}

export function parseQualificationTitle(title) {
  const match = /^Canonical qualification (stable|insider) CI ([1-9][0-9]*) review ([1-9][0-9]*) PR (0|[1-9][0-9]*) mode (single-maintainer|separation-of-duties)$/.exec(title ?? '');
  requireThat(match, 'Unbound qualification run title');
  const [, channel, validationRun, comment, nativePr, mode] = match;
  requireThat(title === qualificationTitle(channel, validationRun, comment, nativePr, mode),
    'Invalid qualification title');
  return { channel, validationRun, comment, nativePr, mode };
}

export function confirmationBody(sha, validationRun, mode) {
  requireString(sha, shaPattern, 'review SHA');
  requireString(String(validationRun), idPattern, 'validation run');
  validateApprovalMode(mode);
  return [
    'Canonical-Qualification: v1',
    `Source-SHA: ${sha}`,
    `CI-Run: ${validationRun}`,
    'CI-Attempt: 1',
    `Approval-Mode: ${mode}`,
    `Review: ${mode === 'single-maintainer' ? 'owner-confirmed-self-attested' : 'native-code-owner-non-self'}`,
  ].join('\n');
}

export function qualificationRequestUrl(endpoint, method = 'GET', allowStatus = false) {
  const reads = [
    /^$/,
    /^git\/ref\/heads\/(?:main|development)$/,
    /^actions\/runs\/[1-9][0-9]*(?:\/attempts\/1\/jobs\?per_page=100)?$/,
    /^actions\/workflows\/(?:ci|qualify-canonical-release|record-canonical-qualification)\.yml$/,
    /^actions\/workflows\/(?:ci|qualify-canonical-release)\.yml\/runs\?head_sha=[a-f0-9]{40}&per_page=100$/,
    /^actions\/workflows\/qualify-canonical-release\.yml\/runs\?created=%3E%3D(?:[0-9TZ.-]|%3A)+&per_page=100$/,
    /^commits\/[a-f0-9]{40}\/(?:comments|statuses)\?per_page=100$/,
    /^commits\/[a-f0-9]{40}\/check-runs\?per_page=100$/,
    /^rules\/branches\/(?:main|development)\?per_page=100$/,
    /^collaborators\/[a-zA-Z0-9][a-zA-Z0-9-]{0,38}\/permission$/,
    /^contents\/\.github\/CODEOWNERS\?ref=[a-f0-9]{40}$/,
    /^pulls\/[1-9][0-9]*(?:\/reviews\?per_page=100)?$/,
  ];
  requireThat(typeof endpoint === 'string' && !/[\r\n]/.test(endpoint) &&
    ((method === 'GET' && reads.some(pattern => pattern.test(endpoint))) ||
     (allowStatus && method === 'POST' && /^statuses\/[a-f0-9]{40}$/.test(endpoint))),
  'Qualification API route or method is not allowlisted');
  return `https://api.github.com/repos/${repository}${endpoint ? `/${endpoint}` : ''}`;
}

export function qualificationClient(token = process.env.GH_TOKEN, allowStatus = false) {
  requireThat(token, 'Missing automatic workflow token');
  return async (endpoint, method = 'GET', body) => {
    if (method === 'POST') {
      requireThat(body?.context === releaseReviewStatus && ['success', 'failure'].includes(body.state) &&
        Object.keys(body).sort().join() === ['context', 'description', 'state', 'target_url'].sort().join() &&
        typeof body.description === 'string' && body.description.length <= 140 &&
        new RegExp(`^https://github.com/${repository}/actions/runs/[1-9][0-9]*$`).test(body.target_url),
      'Unbounded qualification status');
    }
    const response = await fetch(qualificationRequestUrl(endpoint, method, allowStatus), {
      method, redirect: 'error',
      headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28', ...(body ? { 'Content-Type': 'application/json' } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    });
    requireThat(response.ok, `Qualification API read/write denied: HTTP ${response.status}`);
    return response.json();
  };
}

function list(response, field) {
  requireThat(Number.isSafeInteger(response?.total_count) && response.total_count >= 0 &&
    response.total_count < 100 && Array.isArray(response[field]) &&
    response[field].length === response.total_count, 'Missing or truncated qualification evidence');
  return response[field];
}

function array(response) {
  requireThat(Array.isArray(response) && response.length < 100, 'Missing or truncated review evidence');
  return response;
}

function time(value) {
  const parsed = Date.parse(value);
  requireThat(typeof value === 'string' && Number.isFinite(parsed), 'Invalid evidence time');
  return parsed;
}

async function head(api, branch) {
  requireThat(['main', 'development'].includes(branch), 'Untrusted canonical/default branch');
  const ref = await api(`git/ref/heads/${branch}`);
  requireThat(ref.ref === `refs/heads/${branch}` && ref.object?.type === 'commit', 'Invalid branch evidence');
  requireString(ref.object.sha, shaPattern, 'canonical HEAD');
  return ref.object.sha;
}

async function permission(api, login) {
  requireString(login, loginPattern, 'review account');
  const result = await api(`collaborators/${login}/permission`);
  requireThat(result.user?.login === login && ['admin', 'maintain', 'write'].includes(result.permission),
    'Insufficient live review permission');
  return result.permission;
}

async function workflowRun(api, id, path, branch, sha, complete = true) {
  requireString(String(id), idPattern, 'run ID');
  const run = await api(`actions/runs/${id}`);
  const definition = await api(`actions/workflows/${path.split('/').at(-1)}`);
  requireThat(String(run.id) === String(id) && run.path === path && run.workflow_id === definition.id &&
    definition.path === path && definition.state === 'active' &&
    run.repository?.full_name === repository && run.head_repository?.full_name === repository &&
    run.head_sha === sha && run.head_branch === branch && run.run_attempt === 1 &&
    run.html_url === runUrl(id) && (complete ? run.status === 'completed' && run.conclusion === 'success' :
      run.status === 'in_progress'),
  'Untrusted, failed, moved or rerun workflow evidence');
  return run;
}

async function requireJobs(api, run, names) {
  const jobs = list(await api(`actions/runs/${run.id}/attempts/1/jobs?per_page=100`), 'jobs');
  requireThat(jobs.length > 0 && jobs.every(job => job.run_id === run.id && job.run_attempt === 1 &&
    job.head_sha === run.head_sha && job.status === 'completed' &&
    job.conclusion === 'success'),
  'Failed, cancelled, missing or cross-attempt validation jobs');
  for (const name of names) {
    const matches = jobs.filter(job => job.name === name);
    requireThat(matches.length === 1 && matches[0].conclusion === 'success', 'Required validation job missing');
  }
  return jobs;
}

async function verifyRequiredChecks(api, branch, sha, ci, jobs) {
  const rules = await api(`rules/branches/${branch}?per_page=100`);
  requireThat(Array.isArray(rules) && rules.length > 0 && rules.length < 100 &&
    rules.every(rule => rule && typeof rule.type === 'string'),
  'Owner blocker: missing or malformed applied branch rules');
  const checkRules = rules.filter(rule => rule.type === 'required_status_checks');
  requireThat(checkRules.length > 0 && checkRules.every(rule =>
    rule.parameters?.strict_required_status_checks_policy === true &&
    Array.isArray(rule.parameters.required_status_checks) && rule.parameters.required_status_checks.length > 0),
  'Owner blocker: missing or malformed live required-check policy');
  const required = checkRules.flatMap(rule => rule.parameters.required_status_checks);
  requireThat(required.every(rule => rule && typeof rule.context === 'string' &&
    rule.context.length > 0 && rule.context.length <= 100 && !/[\r\n]/.test(rule.context)),
  'Owner blocker: malformed required check context');
  requireThat([...releaseBuildChecks, releaseReviewStatus].every(name => required.some(rule => rule.context === name)),
    'Owner blocker: live policy must require canonical review and all release build checks');
  const checks = list(await api(`commits/${sha}/check-runs?per_page=100`), 'check_runs');
  for (const rule of required) {
    requireThat(typeof rule.context === 'string' &&
      (rule.integration_id == null || (Number.isSafeInteger(rule.integration_id) && rule.integration_id > 0)),
    'Owner blocker: invalid required check integration');
    if (rule.context === releaseReviewStatus) {
      requireThat(rule.integration_id == null, 'Commit review status cannot satisfy an App-bound check');
      continue;
    }
    const job = jobs.find(entry => entry.name === rule.context);
    requireThat(job && checks.some(check => check.name === rule.context && check.head_sha === sha &&
      check.status === 'completed' && check.conclusion === 'success' && check.app?.slug === 'github-actions' &&
      check.check_suite?.id === ci.check_suite_id && Number.isSafeInteger(ci.check_suite_id) &&
      check.url === job.check_run_url &&
      (rule.integration_id == null || rule.integration_id === check.app?.id)),
    'Owner blocker: required qualification check did not execute successfully in the fresh canonical CI run');
  }
}

async function verifyNativeReview(api, binding, sha, ci, comment) {
  const commenter = comment.user.login;
  const pr = await api(`pulls/${binding.nativePr}`);
  requireThat(String(pr.number) === binding.nativePr && pr.head?.sha === sha &&
    pr.head.repo?.full_name === repository && pr.base?.repo?.full_name === repository &&
    pr.base.ref === (binding.channel === 'stable' ? 'main' : 'development') &&
    pr.user?.login !== commenter && pr.draft === false,
  'Native review must cover canonical SHA, not a squash predecessor or self-authored PR');
  const ownersFile = await api(`contents/.github/CODEOWNERS?ref=${sha}`);
  requireThat(ownersFile.encoding === 'base64', 'Missing canonical CODEOWNERS');
  const lines = Buffer.from(ownersFile.content, 'base64').toString('utf8')
    .split(/\r?\n/).map(line => line.trim()).filter(line => line && !line.startsWith('#'));
  // A final catch-all overrides all earlier patterns. More complex ownership needs reviewed support.
  const catchAll = /^\*\s+(@[a-zA-Z0-9][a-zA-Z0-9-]{0,38}(?:\s+@[a-zA-Z0-9][a-zA-Z0-9-]{0,38})*)$/.exec(lines.at(-1) ?? '');
  requireThat(catchAll && catchAll[1].split(/\s+/).includes(`@${commenter}`),
    'Fresh native reviewer must be a canonical catch-all code owner; teams/pattern-only policy needs explicit support');
  const reviews = array(await api(`pulls/${binding.nativePr}/reviews?per_page=100`));
  const decisive = reviews.filter(review => ['APPROVED', 'CHANGES_REQUESTED', 'DISMISSED'].includes(review.state));
  const latest = new Map();
  for (const review of decisive.sort((a, b) => a.id - b.id)) {
    requireThat(Number.isSafeInteger(review.id) && review.id > 0, 'Invalid native review identity');
    latest.set(review.user?.login, review);
  }
  requireThat(![...latest.values()].some(review => review.state === 'CHANGES_REQUESTED'),
    'Outstanding native change request');
  const review = latest.get(commenter);
  requireThat(review?.state === 'APPROVED' && review.commit_id === sha &&
    time(review.submitted_at) >= time(ci.updated_at) &&
    time(review.submitted_at) <= time(comment.created_at), 'Missing fresh exact-SHA native approval');
}

export async function verifyQualification(api, runId, mode, now = Date.now(), complete = true) {
  validateApprovalMode(mode);
  const repo = await api('');
  requireThat(repo.full_name === repository, 'Untrusted qualification repository');
  const trustedHead = await head(api, repo.default_branch);
  const raw = await api(`actions/runs/${runId}`);
  const binding = parseQualificationTitle(raw.display_title);
  requireThat(binding.mode === mode, 'Qualification approval mode mismatch');
  const run = await workflowRun(api, runId, qualificationWorkflow, repo.default_branch, trustedHead, complete);
  requireThat(run.event === 'workflow_dispatch', 'Qualification requires default-branch dispatch');
  await permission(api, run.actor?.login);
  requireThat(run.triggering_actor?.login === run.actor?.login, 'Qualification actor changed');
  const branch = binding.channel === 'stable' ? 'main' : 'development';
  const sha = await head(api, branch);
  const ci = await workflowRun(api, binding.validationRun, '.github/workflows/ci.yml', branch, sha);
  requireThat(ci.event === 'workflow_dispatch' && time(ci.created_at) <= time(ci.updated_at) &&
    time(ci.updated_at) <= time(run.created_at) && now >= time(run.created_at) &&
    now - time(ci.created_at) <= maximumAge, 'Validation is not fresh manual canonical CI');
  const runs = list(await api(`actions/workflows/ci.yml/runs?head_sha=${sha}&per_page=100`), 'workflow_runs');
  requireThat(runs.some(candidate => candidate.id === ci.id) &&
    runs.every(candidate => candidate.id <= ci.id), 'Newer CI run invalidates qualification');
  const jobs = await requireJobs(api, ci, [...releaseBuildChecks, 'Select affected tests', 'CI summary',
    'Dependency license & provenance validation', '.NET provider tests (DbHeavy)']);
  await verifyRequiredChecks(api, branch, sha, ci, jobs);
  const qualifications = list(await api(`actions/workflows/qualify-canonical-release.yml/runs?created=${encodeURIComponent(`>=${ci.created_at}`)}&per_page=100`), 'workflow_runs');
  requireThat(qualifications.some(candidate => candidate.id === run.id), 'Qualification run absent from audit history');
  for (const candidate of qualifications) {
    if (candidate.id === run.id || !candidate.display_title?.startsWith(`Canonical qualification ${binding.channel} `)) continue;
    const other = parseQualificationTitle(candidate.display_title);
    requireThat(other.validationRun !== binding.validationRun, 'Validation run already consumed; start fresh CI');
    requireThat(candidate.id < run.id, 'Qualification superseded by a newer channel run');
  }
  const comments = array(await api(`commits/${sha}/comments?per_page=100`));
  const comment = comments.find(entry => String(entry.id) === binding.comment);
  requireThat(comment?.commit_id === sha && comment.user?.type === 'User' &&
    comment.body === confirmationBody(sha, binding.validationRun, mode) &&
    comment.created_at === comment.updated_at && time(comment.created_at) >= time(ci.updated_at) &&
    time(comment.created_at) <= time(run.created_at), 'Missing, edited, stale or mismatched canonical review');
  const commenter = comment.user.login;
  const authority = await permission(api, commenter);
  if (mode === 'single-maintainer') {
    requireThat(commenter === 'jpapiez' && authority === 'admin', 'Fresh owner confirmation required');
  } else {
    requireThat(commenter !== ci.actor?.login && commenter !== run.actor?.login,
      'Native reviewer must not be the validation/qualification initiator');
    await verifyNativeReview(api, binding, sha, ci, comment);
  }
  if (complete) await requireJobs(api, run, ['Verify canonical qualification']);
  requireThat(await head(api, branch) === sha && await head(api, repo.default_branch) === trustedHead,
    'Canonical or trusted HEAD moved during qualification');
  return { schema: 1, sourceCommit: sha, channel: binding.channel, approvalMode: mode,
    validationRun: String(ci.id), qualificationRun: String(run.id), trustedHead,
    defaultBranch: repo.default_branch };
}

export function qualificationDescription(evidence) {
  return `QUALIFIED (${evidence.approvalMode === 'single-maintainer' ? 'self-attested' : 'native non-self'}) @ ${evidence.sourceCommit.slice(0, 12)}`;
}

export async function verifyCanonicalReleaseEvidence(api, sha, channel, mode, now = Date.now()) {
  requireString(sha, shaPattern, 'release qualification SHA');
  validateApprovalMode(mode);
  const statuses = array(await api(`commits/${sha}/statuses?per_page=100`));
  requireThat(statuses.every(entry => Number.isSafeInteger(entry.id) && entry.id > 0),
    'Malformed canonical status evidence ID');
  const status = statuses.filter(entry => entry.context === releaseReviewStatus).sort((a, b) => b.id - a.id)[0];
  requireThat(status?.state === 'success' && status.creator?.login === 'github-actions[bot]',
    'Missing genuine canonical qualification status');
  const match = new RegExp(`^https://github.com/${repository}/actions/runs/([1-9][0-9]*)$`).exec(status.target_url ?? '');
  requireThat(match, 'Missing qualification audit run');
  const raw = await api(`actions/runs/${match[1]}`);
  const title = /^Canonical evidence for ([1-9][0-9]*)$/.exec(raw.display_title ?? '');
  requireThat(title, 'Untrusted qualification evidence producer');
  const evidence = await verifyQualification(api, title[1], mode, now);
  const writer = await workflowRun(api, match[1], evidenceWorkflow, evidence.defaultBranch, evidence.trustedHead);
  requireThat(writer.event === 'workflow_run' && evidence.channel === channel && evidence.sourceCommit === sha &&
    status.description === qualificationDescription(evidence) &&
    time(status.created_at) >= time(writer.run_started_at) && time(status.created_at) <= time(writer.updated_at),
  'Forged, stale or cross-channel qualification evidence');
  await requireJobs(api, writer, ['Record canonical evidence']);
  return evidence;
}

export async function recordQualification(api, qualificationRun, env = process.env, now = Date.now()) {
  requireThat(env.GITHUB_REPOSITORY === repository && env.GITHUB_EVENT_NAME === 'workflow_run' &&
    env.GITHUB_RUN_ATTEMPT === '1', 'Untrusted qualification writer event or rerun');
  const repo = await api('');
  requireThat(repo.full_name === repository, 'Untrusted writer repository');
  const trustedHead = await head(api, repo.default_branch);
  requireThat(env.GITHUB_REF === `refs/heads/${repo.default_branch}` &&
    env.GITHUB_SHA === trustedHead && env.GITHUB_WORKFLOW_SHA === trustedHead &&
    env.GITHUB_WORKFLOW_REF === `${repository}/${evidenceWorkflow}@refs/heads/${repo.default_branch}`,
  'Qualification writer must execute the live trusted default-branch workflow');
  const writer = await workflowRun(api, env.GITHUB_RUN_ID, evidenceWorkflow,
    repo.default_branch, trustedHead, false);
  requireThat(writer.event === 'workflow_run' && writer.display_title === `Canonical evidence for ${qualificationRun}`,
    'Qualification writer audit mismatch');
  const raw = await api(`actions/runs/${qualificationRun}`);
  const binding = parseQualificationTitle(raw.display_title);
  requireThat(raw.path === qualificationWorkflow && raw.event === 'workflow_dispatch' &&
    raw.repository?.full_name === repository && raw.head_repository?.full_name === repository &&
    raw.head_branch === repo.default_branch && raw.head_sha === trustedHead,
  'Untrusted qualification trigger');
  const branch = binding.channel === 'stable' ? 'main' : 'development';
  const sha = await head(api, branch);
  const post = (state, description) => api(`statuses/${sha}`, 'POST', {
    context: releaseReviewStatus, state, target_url: runUrl(writer.id), description,
  });
  try {
    const evidence = await verifyQualification(api, qualificationRun, env.RELEASE_APPROVAL_MODE, now);
    requireThat(evidence.sourceCommit === sha && await head(api, branch) === sha &&
      await head(api, repo.default_branch) === trustedHead, 'HEAD moved before evidence write');
    // Consumers require the writer to finish successfully as well. Cancellation
    // after this POST cannot authorize release, even before failure reconciliation.
    await post('success', qualificationDescription(evidence));
    requireThat(await head(api, branch) === sha && await head(api, repo.default_branch) === trustedHead,
      'HEAD moved during evidence write');
    return evidence;
  } catch (error) {
    await post('failure', `BLOCKED canonical qualification @ ${sha.slice(0, 12)}`);
    throw error;
  }
}
