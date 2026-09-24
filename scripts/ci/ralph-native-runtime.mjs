import { constants } from 'node:fs';
import { createHash, randomBytes } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { lstat, mkdir, open, readFile, rename } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import {
  applyEvent, digest, admissionInventoryDigest, readMailbox, publishEvent, initializeMailbox,
  taskFromEvidence, researchDisposition, validateControl, verifyControlRepository, githubApi, localInventoryFreshness,
  taskFromPacket, taskPacketFromEvidence, hasHeldLabel, prestartProofSource, validateTerminalArtifact,
} from './ralph-mailbox.mjs';
import { runAutomationPreflight } from './ralph-automation.mjs';
import { acquireTransactionLock } from './ralph-native-lock.mjs';
import { retainNativeLineage, resolveNativeLineage } from './ralph-native-lineage.mjs';
import {
  defaultCleanupProbe, deletionLedger, pendingSummary, terminalIdentity, planWorkerCleanup, recordDeletionIntent, recordDeletionResult, requireCleanupRole,
} from './ralph-native-cleanup.mjs';
import {
  buildDispatchPlan, policyTextDigest, validateClassification, validateNativeCapabilities, validateStartup, validatePacketAck,
} from './ralph-native-dispatch.mjs';
import { loadSquadVerdict } from './verify-squad-verdict.mjs';

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const fail = (message) => { throw new Error(`Native Ralph blocked: ${message}`); };
const exec = promisify(execFile);

function windowsPowerShellModulePath() {
  const userProfile = process.env.USERPROFILE;
  const programFiles = process.env.ProgramFiles ?? 'C:\\Program Files';
  const systemRoot = process.env.SystemRoot ?? 'C:\\Windows';
  return [
    userProfile ? path.join(userProfile, 'Documents', 'WindowsPowerShell', 'Modules') : undefined,
    path.join(programFiles, 'WindowsPowerShell', 'Modules'),
    path.join(systemRoot, 'system32', 'WindowsPowerShell', 'v1.0', 'Modules'),
  ].filter(Boolean).join(';');
}

function windowsPowerShellExecutable() {
  const systemRoot = process.env.SystemRoot ?? 'C:\\Windows';
  return path.join(systemRoot, 'system32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
}

function windowsPowerShellEnv(extra = {}) {
  const env = { ...process.env };
  const extraKeys = new Set(Object.keys(extra).map((key) => key.toLowerCase()));
  for (const key of Object.keys(env)) {
    if (key.toLowerCase() === 'psmodulepath' || extraKeys.has(key.toLowerCase())) delete env[key];
  }
  return { ...env, PSModulePath: windowsPowerShellModulePath(), ...extra };
}

async function privatePath(target, file = false) {
  if (!path.isAbsolute(target) || path.normalize(target) !== target || target === path.parse(target).root) fail('Normalized private absolute path required.');
  let current = path.parse(target).root;
  for (const part of target.slice(current.length).split(path.sep)) {
    current = path.join(current, part);
    try {
      await lstat(path.join(current, '.git'));
      fail('Native private state must remain outside repository checkouts.');
    } catch (error) {
      if (!['ENOENT', 'ENOTDIR'].includes(error.code)) throw error;
    }
    let stat;
    try { stat = await lstat(current); } catch (error) { if (error.code === 'ENOENT') continue; throw error; }
    if (stat.isSymbolicLink() || (current === target && file ? !stat.isFile() || stat.nlink !== 1 : !stat.isDirectory())) fail('Unsafe private state path.');
    if (current === target && process.platform !== 'win32' &&
        (stat.uid !== process.getuid() || (stat.mode & (file ? 0o077 : 0o022)))) fail('Unsafe private state ownership/permissions.');
    if (current === target && process.platform === 'win32') {
      const script = `$ErrorActionPreference='Stop'; $s=[System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value; $a=Get-Acl -LiteralPath $env:RALPH_PRIVATE_CHECK_PATH; if($a.Owner -ne [System.Security.Principal.WindowsIdentity]::GetCurrent().Name){exit 1}; foreach($r in $a.Access){if($r.AccessControlType -eq 'Allow' -and $r.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value -notin @($s,'S-1-5-18','S-1-5-32-544')){exit 1}}`;
      await exec(windowsPowerShellExecutable(), ['-NoProfile', '-NonInteractive', '-Command', script], {
        env: windowsPowerShellEnv({ RALPH_PRIVATE_CHECK_PATH: target }), timeout: 30_000,
      }).catch(() => fail('Private Windows ACL is not verified; provision permissions manually.'));
    }
  }
}

async function writeJournal(file, journal) {
  await privatePath(file, true);
  const temporary = `${file}.pending`;
  await privatePath(temporary, true);
  const handle = await open(temporary, constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW, 0o600);
  try { await handle.writeFile(JSON.stringify(journal)); await handle.sync(); }
  finally { await handle.close(); }
  await rename(temporary, file);
  if (process.platform !== 'win32') {
    const directory = await open(path.dirname(file), 'r');
    try { await directory.sync(); } finally { await directory.close(); }
  }
}

function freshEvidence(evidence, now) {
  const observed = Date.parse(evidence?.observedAt);
  if (!Number.isFinite(observed) || observed > now || now - observed > 60_000 ||
      typeof evidence.source !== 'string' || !evidence.source.trim()) fail('Fresh observations from supported native/GitHub tools required.');
}

function withinWorktreeRoot(root, target) {
  if (!path.isAbsolute(root ?? '') || !path.isAbsolute(target ?? '') ||
      path.normalize(target) !== target) return false;
  const relative = path.relative(root, target);
  return Boolean(relative) && !relative.startsWith('..') && !path.isAbsolute(relative);
}

function ownedSessionInventory(config, evidence, journal, state, now) {
  if (evidence.ownershipScope !== 'ralph-owned-v1' || evidence.lineageChecked !== true) {
    fail('Complete Ralph-owned lineage reconciliation required, not project-wide session ownership.');
  }
  const known = new Map();
  for (const entry of Object.values(journal.sessions)) {
    const assignment = state.assignments[entry.assignmentId];
    if (!assignment || assignment.workerId !== config.workerId) fail('Recorded Ralph delivery intent has unresolved assignment ownership.');
    if (!uuidPattern.test(entry.sessionId ?? '')) fail('Recorded Ralph creation intent lacks correlated native delivery; reconcile the lost acknowledgement.');
    if (known.has(entry.sessionId)) fail('Duplicate Ralph native session mapping.');
    known.set(entry.sessionId, entry);
  }
  // A journal-verified retirement (not found or archived, #2956) of a settled
  // mapped worker retires that mapping without new live evidence; any
  // reappearance under a known ID fails closed.
  const { bySession: deletions, identifiers: deletionIds } = deletionLedger(journal, state);
  const deleted = new Set([...deletions.values()].filter((record) => record.status === 'deleted').map((record) => record.sessionId));
  if (deleted.size !== deletions.size) {
    fail('A pending Ralph worker deletion intent blocks readiness; resolve it with record-deletion-result (never retry delete_item) or stop and report it.');
  }
  for (const session of evidence.sessions) {
    if (deleted.has(deletionIds.get(session?.id))) {
      fail(`Deleted Ralph worker ${deletionIds.get(session.id)} reappeared in native inventory; omit only verified retirements (deleted or archived) and reconcile any reappearance.`);
    }
    if (deleted.has(deletionIds.get(session?.creatorSessionId))) fail('Descendant of a deleted Ralph worker blocks readiness.');
  }
  const sessions = resolveNativeLineage(evidence.sessions, journal.nativeLineage, new Set(known.keys()), now);
  const ownedIds = new Set(known.keys());
  const roles = new Set();
  for (const session of sessions.values()) {
    if (session.assignmentCorrelation !== undefined &&
        Object.hasOwn(journal.sessions, session.assignmentCorrelation)) {
      if (journal.sessions[session.assignmentCorrelation].sessionId !== session.id) {
        fail('Observed Ralph correlation conflicts with its immutable native mapping.');
      }
      ownedIds.add(session.id);
    }
    const role = session.roleObservation;
    if (role?.workerId === config.workerId && role.projectId === config.projectId) {
      if (session.nativeReadbackVerified !== true || role.ownerConfiguredRoleVerified !== true ||
          !['coordinator', 'consumer'].includes(role.role) ||
          (role.role === 'coordinator' && config.host !== 'macos-mobile') ||
          !withinWorktreeRoot(config.worktreeRoot, role.worktreePath)) {
        fail('Ralph role lineage requires actual owner-configured native readback.');
      }
      ownedIds.add(session.id);
      if (role.noTaskExecutionVerified === true) roles.add(session.id);
    }
  }
  for (const id of known.keys()) {
    if (!sessions.has(id) && !deleted.has(id)) fail('Every retained Ralph native mapping needs fresh session evidence, including terminal assignments.');
  }
  let changed = true;
  while (changed) {
    changed = false;
    for (const session of sessions.values()) {
      if (!ownedIds.has(session.id) && ownedIds.has(session.creatorSessionId)) {
        ownedIds.add(session.id);
        changed = true;
      }
    }
  }
  const ownedSessions = [...sessions.values()].filter((session) => ownedIds.has(session.id));
  const lineageIds = new Set();
  for (const session of ownedSessions) {
    const ancestry = new Set();
    let ancestor = session;
    while (ancestor) {
      if (ancestry.has(ancestor.id)) fail('Cyclic Ralph-owned native ancestry blocks readiness.');
      ancestry.add(ancestor.id);
      lineageIds.add(ancestor.id);
      if (ancestor.creatorSessionId === undefined) break;
      const parentId = ancestor.creatorSessionId;
      ancestor = sessions.get(parentId);
      if (!ancestor) fail(`Missing Ralph-owned native ancestor readback ${parentId} blocks readiness; record verified ancestry before retrying.`);
    }
    const mapping = known.get(session.id);
    if (session.retirementObservation !== undefined) {
      const observation = session.retirementObservation;
      freshEvidence(observation, now);
      const assignment = mapping && state.assignments[mapping.assignmentId];
      const receipt = assignment?.receipts?.at(-1);
      if (assignment?.state !== 'terminal' || session.terminalVerified !== true ||
          !['archived', 'deleted'].includes(observation.status) ||
          observation.liveChecked !== true || observation.cessationProven !== true ||
          observation.noPendingContinuation !== true || observation.noFutureDelivery !== true ||
          !Object.hasOwn(journal.sessions, session.assignmentCorrelation ?? '') ||
          journal.sessions[session.assignmentCorrelation] !== mapping ||
          !/^[0-9a-f]{64}$/.test(observation.terminalEvidenceDigest ?? '') ||
          observation.terminalEvidenceDigest !== mapping.lastEvidenceDigest ||
          receipt?.status !== 'terminal-reported' || receipt.correlation !== session.assignmentCorrelation ||
          observation.terminalEvidenceDigest !== receipt.evidenceDigest ||
          observation.terminalEvidenceDigest !== assignment.terminalCommitment) {
        fail('Retired Ralph session needs current verified cessation and retained correlated terminal evidence; absence or idle alone is insufficient.');
      }
    }
    if (session.terminalVerified === true) continue;
    if (mapping) {
      if (state.assignments[mapping.assignmentId].state === 'terminal') {
        fail('Resumed terminal Ralph native work blocks readiness; reconcile ownership first.');
      }
    } else if (!roles.has(session.id)) {
      fail('Unmapped Ralph-owned session or descendant blocks readiness; reconcile its assignment first.');
    }
  }
  journal.nativeLineage = retainNativeLineage(journal.nativeLineage,
    evidence.sessions.filter((session) => lineageIds.has(session.id) && session.nativeReadbackVerified === true)
      .map((session) => ({ session, nativeReadbackVerified: true, source: evidence.source, observedAt: evidence.observedAt })), now);
  return ownedSessions;
}

export async function readResearchArtifact(assignment, url, api = githubApi) {
  const match = /^https:\/\/github\.com\/OlyForge3D\/PrintFarmer\/issues\/([0-9]+)#issuecomment-([0-9]+)$/.exec(url ?? '');
  if (!assignment || !['research', 'analysis'].includes(assignment.task.purpose) ||
      match?.[1] !== String(assignment.task.issue)) fail('Research artifact must belong to the assigned issue.');
  const comment = await api(`repos/OlyForge3D/PrintFarmer/issues/comments/${match[2]}`);
  if (String(comment.id) !== match[2] || comment.html_url !== url ||
      comment.issue_url !== `https://api.github.com/repos/OlyForge3D/PrintFarmer/issues/${assignment.task.issue}` ||
      typeof comment.body !== 'string' || !comment.body.trim()) fail('GitHub returned an invalid research artifact readback.');
  return {
    kind: 'issue-comment', url,
    bodyDigest: createHash('sha256').update(comment.body, 'utf8').digest('hex'),
    bodyBytes: Buffer.byteLength(comment.body, 'utf8'),
  };
}

const repository = 'OlyForge3D/PrintFarmer';
const sha256Text = (text) => createHash('sha256').update(text, 'utf8').digest('hex');
const sortedDigest = (values) => digest([...(Array.isArray(values) ? values : [])].map(String).sort());
const packetTaskKeys = ['issue', 'pr', 'purpose', 'headSha', 'title', 'labels', 'acceptanceCriteria', 'files',
  'scope', 'classificationComplete', 'capabilities'];
const liveTaskKeys = ['issueState', 'githubAssignees', 'prState', 'prHeadRepository', 'prHeadRef', 'prHeadSha'];

// Runtime-owned GitHub readback of the public task subject. The consumer never
// re-authors task facts: it compares this readback with the published packet.
export async function readTaskSubject(task, api = githubApi) {
  const subject = task.issue ?? task.pr;
  if (!Number.isSafeInteger(subject) || subject < 1) fail('github-readback-invalid: exact issue or PR number required.');
  const issue = await api(`repos/${repository}/issues/${subject}`);
  const labels = Array.isArray(issue?.labels) ? issue.labels.map((label) => typeof label === 'string' ? label : label?.name) : undefined;
  const assignees = Array.isArray(issue?.assignees ?? []) ? (issue?.assignees ?? []).map((user) => user?.login) : undefined;
  if (issue?.number !== subject || typeof issue.title !== 'string' || !labels || labels.some((label) => typeof label !== 'string') ||
      !assignees || assignees.some((login) => typeof login !== 'string') || !['open', 'closed'].includes(issue.state) ||
      (issue.body != null && typeof issue.body !== 'string')) fail('github-readback-invalid: GitHub returned an invalid issue readback.');
  const live = { title: issue.title, labels, issueState: issue.state, githubAssignees: assignees, bodySha256: sha256Text(issue.body ?? '') };
  if (task.pr) {
    const pr = await api(`repos/${repository}/pulls/${task.pr}`);
    if (pr?.number !== task.pr || typeof pr.head?.sha !== 'string') fail('github-readback-invalid: GitHub returned an invalid PR readback.');
    Object.assign(live, { prState: pr.merged ? 'merged' : pr.state, prHeadRepository: pr.head.repo?.full_name,
      prHeadRef: pr.head.ref, prHeadSha: pr.head.sha });
  }
  return live;
}

function liveMatches(key, supplied, live) {
  if (key === 'githubAssignees') return sortedDigest(supplied) === sortedDigest(live);
  if (['issueState', 'prState'].includes(key)) return String(supplied).toLowerCase() === String(live).toLowerCase();
  return supplied === live;
}

// Coordinator reservation: its evidence must describe the live issue exactly.
export function verifyReservationReadback(evidence, live) {
  const drift = ['title', 'labels', ...liveTaskKeys].filter((key) => {
    if (key === 'title') return evidence.title !== live.title;
    if (key === 'labels') return sortedDigest(evidence.labels) !== sortedDigest(live.labels);
    return evidence[key] !== undefined && !liveMatches(key, evidence[key], live[key]);
  });
  if (drift.length) fail(`task-readback-mismatch: reserve evidence ${drift.join(', ')} differs from the live GitHub subject; re-read GitHub and retry.`);
}

// Rebuilds task evidence from the published packet plus a fresh runtime readback.
// Supplied task facts are optional and must equal the packet exactly.
export function composeTaskEvidence(assignment, live, supplied) {
  const packet = assignment?.taskPacket;
  if (!packet) {
    fail('native-evidence-missing: this assignment has no published task packet (legacy reservation). The consumer reports blocker native-evidence-missing with its prestart-proof; the coordinator withdraws and re-reserves. Never relay task facts.');
  }
  const task = taskFromPacket(packet);
  if (digest(task) !== assignment.taskDigest || digest(task) !== digest(assignment.task)) {
    fail('task-packet-tampered: published task packet does not reproduce the assignment task digest.');
  }
  for (const key of [...packetTaskKeys, 'filesComplete', 'repository']) {
    if (supplied?.[key] === undefined) continue;
    const expected = key === 'filesComplete' ? true : packet[key];
    const same = expected !== undefined &&
      (key === 'labels' ? sortedDigest(supplied.labels) === sortedDigest(expected) : digest(supplied[key]) === digest(expected));
    if (!same) fail(`task-packet-mismatch: supplied ${key} differs from the published task packet; omit task facts and let the runtime read the packet.`);
  }
  for (const key of liveTaskKeys) {
    if (supplied?.[key] !== undefined && !liveMatches(key, supplied[key], live[key])) {
      fail(`github-readback-mismatch: supplied ${key} differs from the runtime GitHub readback; omit it.`);
    }
  }
  if (hasHeldLabel(live.labels)) fail('held: a human hold or go:no label is now present; report-blocker reasonCode held without kickoff.');
  const changed = [
    live.title !== packet.title && 'title',
    sortedDigest(live.labels) !== sortedDigest(packet.labels) && 'labels',
    live.bodySha256 !== packet.sourceBodySha256 && 'body',
  ].filter(Boolean);
  if (changed.length) {
    fail(`task-changed: issue ${changed.join(', ')} changed after reservation; report-blocker reasonCode task-changed with prestart-proof so the coordinator withdraws and re-reserves.`);
  }
  const { version, sourceBodySha256, ...facts } = packet;
  return {
    ...supplied, ...facts, filesComplete: true,
    issueState: live.issueState, githubAssignees: live.githubAssignees,
    ...(packet.pr ? { prState: live.prState, prHeadRepository: live.prHeadRepository, prHeadRef: live.prHeadRef, prHeadSha: live.prHeadSha } : {}),
  };
}

// Bounded worker policy at an existing PR head, read by the runtime from GitHub
// so PR recovery never needs a consumer-computed policy digest.
export async function readPrWorkerPolicyDigest(headSha, member, api = githubApi) {
  if (!/^[0-9a-f]{40}$/.test(headSha ?? '') || !/^[a-z]+$/.test(member ?? '')) fail('github-readback-invalid: exact PR head and member required.');
  const read = async (file) => {
    const content = await api(`repos/${repository}/contents/${file}?ref=${headSha}`).catch(() => undefined);
    if (content?.type !== 'file' || content.encoding !== 'base64' || typeof content.content !== 'string') {
      fail(`github-readback-invalid: ${file} is unavailable at the PR head; the existing owner reconciles policy first.`);
    }
    return policyTextDigest(Buffer.from(content.content, 'base64').toString('utf8'));
  };
  const charterPath = member === 'copilot' ? '.github/copilot-instructions.md' : `.squad/agents/${member}/charter.md`;
  return digest({
    agentSha256: await read('.github/agents/ralph-worker.agent.md'),
    contractSha256: await read('.copilot/skills/ralph-loop/assigned-worker.md'),
    charterSha256: await read(charterPath),
  });
}

export async function readIssueCommentArtifact(assignment, url, api = githubApi) {
  const subject = assignment?.task.issue ?? assignment?.task.pr;
  const match = /^https:\/\/github\.com\/OlyForge3D\/PrintFarmer\/issues\/([0-9]+)#issuecomment-([0-9]+)$/.exec(url ?? '');
  if (!assignment || match?.[1] !== String(subject)) fail('Issue-comment artifact must belong to the assigned issue.');
  const comment = await api(`repos/${repository}/issues/comments/${match[2]}`);
  if (String(comment.id) !== match[2] || comment.html_url !== url ||
      comment.issue_url !== `https://api.github.com/repos/${repository}/issues/${subject}` ||
      typeof comment.body !== 'string' || !comment.body.trim()) fail('GitHub returned an invalid issue-comment artifact readback.');
  return { kind: 'issue-comment', url, bodyDigest: sha256Text(comment.body), bodyBytes: Buffer.byteLength(comment.body, 'utf8') };
}

export async function readPullRequestArtifact(assignment, url, api = githubApi) {
  const match = /^https:\/\/github\.com\/OlyForge3D\/PrintFarmer\/pull\/([1-9][0-9]*)$/.exec(url ?? '');
  if (!assignment || !match || !['implementation', 'recovery'].includes(assignment.task.purpose) ||
      (assignment.task.pr !== undefined && Number(match[1]) !== assignment.task.pr)) {
    fail('Pull-request artifact must be the assigned implementation/recovery PR.');
  }
  const pr = await api(`repos/${repository}/pulls/${match[1]}`);
  if (pr?.number !== Number(match[1]) || pr.html_url !== url || pr.base?.repo?.full_name !== repository ||
      pr.head?.repo?.full_name !== repository || !/^[0-9a-f]{40}$/.test(pr.head?.sha ?? '')) {
    fail('GitHub returned an invalid or cross-repository pull-request artifact readback.');
  }
  return { kind: 'pull-request', url, number: pr.number, headSha: pr.head.sha,
    state: pr.merged ? 'merged' : pr.state, body: typeof pr.body === 'string' ? pr.body : '', pull: pr };
}

function publishedArtifact(artifact) {
  return artifact.kind === 'pull-request'
    ? { kind: artifact.kind, url: artifact.url, number: artifact.number, headSha: artifact.headSha }
    : { kind: artifact.kind, url: artifact.url, bodyDigest: artifact.bodyDigest };
}

// Coordinator-side settlement readback of the consumer-published terminal artifact.
export async function verifyTerminalArtifact(assignment, evidence, api = githubApi) {
  const published = assignment.terminalArtifact;
  if (!published) fail('native-evidence-missing: packet-bound assignment has no published terminal artifact; the consumer must report its artifact in the terminal receipt.');
  if (published.kind === 'issue-comment') {
    const artifact = await readIssueCommentArtifact(assignment, published.url, api);
    if (artifact.bodyDigest !== published.bodyDigest) fail('artifact-changed: published issue-comment artifact bytes changed after the terminal receipt.');
    return { artifact: publishedArtifact(artifact) };
  }
  const pr = await readPullRequestArtifact(assignment, published.url, api);
  if (pr.state === 'open') fail('artifact-pending: implementation PR is still open; retain the assignment until it merges or closes.');
  if (pr.state !== 'merged') {
    if (typeof evidence.closureReason !== 'string' || !evidence.closureReason.trim()) {
      fail('artifact-closed: a closed unmerged implementation PR needs an explicit closureReason before settlement.');
    }
    return { artifact: publishedArtifact(pr), closedUnmerged: true };
  }
  if (pr.headSha !== published.headSha) fail('artifact-changed: merged PR head differs from the published terminal artifact head; reconcile a recovery assignment.');
  if (assignment.task.issue !== undefined && assignment.task.pr === undefined &&
      !new RegExp(`\\b(close[sd]?|fix(e[sd])?|resolve[sd]?) #${assignment.task.issue}\\b`, 'i').test(pr.body)) {
    fail(`artifact-unlinked: merged PR body must contain Closes #${assignment.task.issue}.`);
  }
  // Same provenance as verify-squad-verdict.mjs: GitHub Actions creator, trusted
  // default-branch workflow run, this PR number and this exact head.
  const verdict = await loadSquadVerdict({ api, pull: pr.pull, statusHeadSha: pr.headSha }).catch(() => undefined);
  if (!['REVIEWED', 'APPROVED'].includes(verdict?.classification) || verdict.reviewedHeadSha !== pr.headSha) {
    fail(`artifact-unreviewed: merged PR head lacks a verified REVIEWED or APPROVE (owner) squad/pre-pr-verdict at that exact head (${verdict?.classification ?? 'unreadable'}).`);
  }
  return { artifact: publishedArtifact(pr), verdict: {
    classification: verdict.classification, reviewedHeadSha: verdict.reviewedHeadSha, workflowRunUrl: verdict.workflowRunUrl,
    ...(verdict.carriedAcrossSync !== undefined ? { carriedAcrossSync: verdict.carriedAcrossSync } : {}),
  } };
}

// Issue workers start from the live development branch, which may have advanced
// past the reservation head. Accept only a GitHub-proven descendant on development.
export async function verifyDevelopmentAdvance(packet, initialHeadSha, api = githubApi) {
  if (packet.pr || !/^[0-9a-f]{40}$/.test(initialHeadSha ?? '') || initialHeadSha === packet.headSha) return undefined;
  const forward = await api(`repos/${repository}/compare/${packet.headSha}...${initialHeadSha}`);
  const onBranch = await api(`repos/${repository}/compare/${initialHeadSha}...${packet.sourceRef}`);
  if (forward?.status !== 'ahead' || !['identical', 'ahead'].includes(onBranch?.status)) {
    fail('Initial worker HEAD is not a GitHub-verified descendant of the reserved head on the source branch; reconcile on the same child before work.');
  }
  return initialHeadSha;
}

export function validateTriageEvidence(evidence) {
  const owners = 'dallas|ripley|drake|lambert|hudson|gorman|kane|ash|brett|parker|newt|copilot';
  const ownerPattern = new RegExp(`^squad:[^a-z]*(${owners})$`);
  if (evidence.issueState?.toLowerCase() !== 'open' || !Array.isArray(evidence.githubAssignees) ||
      evidence.githubAssignees.length !== 0 || !Array.isArray(evidence.labels)) fail('New work must be open and not personally assigned; never assign jpapiez.');
  const labels = evidence.labels.map((label) => String(label).toLowerCase());
  const ownerLabels = labels.filter((label) => label.startsWith('squad:'));
  const normalizedOwners = new Set(ownerLabels.map((label) => label.match(ownerPattern)?.[1]));
  const types = labels.filter((label) => label.startsWith('type:'));
  const priorities = labels.filter((label) => label.startsWith('priority:'));
  if (normalizedOwners.size !== 1 || normalizedOwners.has(undefined) ||
      types.length !== 1 || !['type:feature', 'type:bug', 'type:chore', 'type:docs', 'type:spike', 'type:epic'].includes(types[0]) ||
      priorities.length !== 1 || !/^priority:p[0-3]$/.test(priorities[0])) fail('One valid dispatch Squad owner, type and priority are required; reviewer/Ralph labels are not owners.');
  const purpose = taskFromEvidence(evidence).purpose;
  if ((types[0] === 'type:epic' || labels.includes('status:needs-analysis')) &&
      !['analysis', 'research'].includes(purpose)) fail('Epics and needs-analysis work permit bounded analysis/research, never direct implementation.');
  return { owner: `squad:${[...normalizedOwners][0]}`, purpose };
}

export function prepareEvent(config, request, snapshot, journal, now = Date.now(), invocationDigest, dispatchPlan, context = {}) {
  if (!['coordinator', 'consumer'].includes(config.role) ||
      !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(request.roundId ?? '') ||
      !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(request.id ?? '')) fail('Exact role, round and event IDs required.');
  const event = {
    id: request.id, type: request.type, authorityId: config.control.registry.authorityId,
    epoch: config.control.registry.epoch, role: config.role,
    ...(config.role === 'consumer' ? { workerId: config.workerId } : {}),
    roundId: request.roundId, observedAt: new Date(now).toISOString(), data: request.data ?? {},
  };
  const evidence = request.evidence;
  const map = journal.sessions;
  const assignment = snapshot.state.assignments[event.data.assignmentId];
  let createAllowed = false;
  const requireEvidence = () => freshEvidence(evidence, now);
  if (event.type === 'begin-round') {
    event.data = { invocationDigest };
  } else if (event.type === 'recover-coordinator-round' || event.type === 'reconcile-round') {
    requireEvidence();
    if (evidence.cessationProven !== true || evidence.liveChecked !== true ||
        evidence.queuedChecked !== true || evidence.historyChecked !== true ||
        evidence.priorInvocationDigest !== (event.type === 'recover-coordinator-round'
          ? snapshot.state.rounds.coordinator?.invocationDigest
          : snapshot.state.rounds[`consumer:${event.data.workerId}`]?.invocationDigest)) fail('Proven correlated cessation required; idle, age and absence alone are insufficient.');
    event.data = { ...event.data, cessationEvidenceDigest: digest(evidence) };
    if (event.type === 'recover-coordinator-round') event.data.invocationDigest = invocationDigest;
  } else if (event.type === 'reserve') {
    requireEvidence();
    validateTriageEvidence(evidence);
    validateClassification(evidence);
    for (const key of ['claimsReconciled', 'holdsChecked', 'dependenciesReady', 'epicChildrenReady', 'analysisReady', 'filesComplete', 'reviewGatesChecked']) {
      if (evidence[key] !== true) fail(`Reservation requires ${key}.`);
    }
    if (evidence.repository !== 'OlyForge3D/PrintFarmer') fail('Wrong target repository.');
    const offer = snapshot.state.availability?.[request.data.workerId];
    if (!offer) fail('Consumer must publish a finite capacity offer before new reservations.');
    if (config.role !== 'coordinator') fail('Only coordinator may assign or release work.');
    const task = taskFromEvidence(evidence);
    if (!context.taskPacket || digest(taskFromPacket(context.taskPacket)) !== digest(task)) {
      fail('native-evidence-missing: reservation requires the runtime-built task packet from a live GitHub readback.');
    }
    event.data = {
      assignmentId: request.data.assignmentId, workerId: request.data.workerId, generation: 1,
      task, eligibilityDigest: digest(evidence), policySha: config.approvedPolicy,
      offerId: offer.offerId, taskPacket: context.taskPacket,
    };
  } else if (event.type === 'publish') {
    requireEvidence();
    validateTriageEvidence(evidence);
    if (!assignment || evidence.holdsChecked !== true || evidence.ownershipReconciled !== true ||
        digest(taskFromEvidence(evidence)) !== assignment.taskDigest) fail('Recheck exact task, holds and ownership immediately before publication.');
    event.type = 'deliver';
  } else if (event.type === 'ready') {
    requireEvidence();
    validateNativeCapabilities(evidence);
    if (evidence.complete !== true || evidence.queueChecked !== true || evidence.historyChecked !== true ||
        evidence.capabilitiesVerified !== true || !Array.isArray(evidence.sessions)) fail('Complete native inventory, queue/history and local tooling checks required.');
    const sessions = ownedSessionInventory(config, evidence, journal, snapshot.state, now);
    for (const entry of Object.values(snapshot.state.assignments).filter((item) => item.workerId === config.workerId && !['reserved', 'published', 'terminal'].includes(item.state))) {
      const local = map[entry.correlation];
      if (!local?.sessionId || !sessions.some((session) => session.id === local.sessionId &&
          (entry.state === 'terminal-reported' ? session.terminalVerified === true : session.ownershipVerified === true))) fail('Every live/uncertain receipt needs fresh correlated native evidence.');
    }
    event.data = {
      inventoryDigest: digest({ ...evidence, sessions }), assignmentInventoryDigest: admissionInventoryDigest(snapshot.state, config.workerId),
      unassignedSessions: 0, capabilities: evidence.capabilities, policySha: config.approvedPolicy,
      previousCapacityDigest: digest(snapshot.state.availability?.[config.workerId] ?? {}),
      inventoryObservedAt: evidence.observedAt,
    };
    event.type = 'offer-capacity';
  } else if (event.type === 'unavailable') {
    requireEvidence();
    event.data = { ...event.data, evidenceDigest: digest(evidence) };
  } else if (event.type === 'receipt') {
    if (!assignment || assignment.workerId !== config.workerId) fail('Consumer does not own this assignment.');
    const correlation = event.data.correlation;
    if (!/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(correlation ?? '')) fail('Opaque correlation ID required.');
    const prior = map[correlation];
    if (event.data.status === 'starting') {
      requireEvidence();
      validateTriageEvidence(evidence);
      if (evidence.holdsChecked !== true || evidence.ownershipReconciled !== true ||
          digest(taskFromEvidence(evidence)) !== assignment.taskDigest) fail('Assignment changed or held; report blocker without kickoff.');
      if (prior || Object.values(map).some((entry) => entry.assignmentId === event.data.assignmentId)) {
        if (!prior || prior.assignmentId !== event.data.assignmentId || prior.startEventId !== request.id) fail('Existing delivery intent prevents another session kickoff.');
      } else {
        if (assignment.state !== 'published') fail('Only published assignment can start.');
        if (!dispatchPlan) fail('Validated bounded specialist dispatch plan required before starting.');
        map[correlation] = { assignmentId: event.data.assignmentId, startEventId: request.id, dispatchPlan };
        createAllowed = true;
      }
      event.type = 'accept';
      event.data = { ...event.data, evidenceDigest: digest(map[correlation]), policySha: config.approvedPolicy };
    } else {
      requireEvidence();
      if (!prior || prior.assignmentId !== event.data.assignmentId ||
          !uuidPattern.test(evidence.session?.id ?? '') || evidence.assignmentCorrelation !== correlation ||
          evidence.repository !== 'OlyForge3D/PrintFarmer' || evidence.nativeReadbackVerified !== true ||
          evidence.session.projectId !== config.projectId ||
          !withinWorktreeRoot(config.worktreeRoot, evidence.session.worktreePath) ||
          (prior.sessionId && prior.sessionId !== evidence.session.id)) fail('Actual native session readback must match the immutable local mapping.');
      if (!prior.sessionId && evidence.kickoffDeliveryVerified !== true) fail('Lost kickoff needs proven correlated native delivery, not a guessed session.');
      if (event.data.status === 'running' && prior.dispatchPlan && !prior.startupEvidenceDigest) {
        fail('Record exact startup ACK/configuration before running; a workspace is not a worker.');
      }
      if (event.data.status === 'running' && prior.dispatchPlan && !prior.runningEvidenceDigest) {
        const ack = evidence.continuationAck;
        const packet = prior.dispatchPlan.packet;
        if (!prior.continuationIntent || evidence.kickoffDeliveryVerified !== true ||
            ack?.substantiveWorkStarted !== true) {
          fail('Actual substantive continuation ACK must match the saved specialist binding.');
        }
        validatePacketAck(packet, ack);
        prior.runningEvidenceDigest = digest(ack);
      }
      if (Object.entries(map).some(([otherCorrelation, entry]) => otherCorrelation !== correlation && entry.sessionId === evidence.session.id)) fail('Native session is already mapped to another assignment.');
      if (event.data.status === 'terminal-reported' &&
          (evidence.session.terminalVerified !== true || evidence.queueChecked !== true ||
            evidence.historyChecked !== true || evidence.artifactsVerified !== true ||
            evidence.noPendingContinuation !== true || evidence.noFutureDelivery !== true)) fail('Terminal report requires cessation, final delivery ACK, no future delivery commitment, queue/history and artifact evidence.');
      if (event.data.status === 'terminal-reported' &&
          !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(evidence.finalDeliveryCorrelation ?? '')) {
        fail('finalDeliveryCorrelation must be a 1-64 character opaque ID; use artifact-readback.finalDeliveryCorrelation and obtain the same child ACK. Never rewrite an ACK.');
      }
      if (event.data.status === 'terminal-reported' && prior.dispatchPlan &&
          (!prior.startupEvidenceDigest || !prior.continuationIntent || !prior.runningEvidenceDigest)) {
        fail('Completed terminal work requires acknowledged startup and substantive continuation; uncertain pre-start work remains owned.');
      }
      if (event.data.status === 'terminal-reported' && prior.dispatchPlan) {
        validatePacketAck(prior.dispatchPlan.packet, evidence.finalAck);
        if (evidence.finalAck.noChildren !== true || evidence.finalAck.noPendingContinuation !== true ||
            evidence.finalAck.noFutureDelivery !== true ||
            evidence.finalAck.finalDeliveryCorrelation !== evidence.finalDeliveryCorrelation) {
          fail('Exact worker final ACK with no children, continuation or future delivery required.');
        }
      }
      let terminalArtifact;
      if (event.data.status === 'terminal-reported' && prior.dispatchPlan && assignment.taskPacket) {
        const artifact = evidence.artifact;
        const ack = evidence.finalAck;
        const research = ['research', 'analysis'].includes(assignment.task.purpose);
        if (evidence.artifactReadbackVerified !== true || !artifact || (research && artifact.kind !== 'issue-comment') ||
            ack?.artifactUrl !== artifact.url || ack.artifactReadbackVerified !== true ||
            (artifact.kind === 'pull-request' ? ack.artifactHeadSha !== artifact.headSha : ack.artifactBodyDigest !== artifact.bodyDigest)) {
          fail(research
            ? 'Research terminal receipt needs read-back issue findings and the same specialist final ACK, not chat-only completion.'
            : 'Terminal receipt needs a runtime artifact-readback (issue comment or implementation PR) echoed by the same specialist final ACK; the artifact is published for coordinator settlement.');
        }
        terminalArtifact = validateTerminalArtifact(publishedArtifact(artifact), assignment.task);
      }
      if (event.data.status === 'terminal-reported' && prior.dispatchPlan &&
          ['research', 'analysis'].includes(assignment.task.purpose)) {
        const ack = evidence.finalAck;
        const packet = prior.dispatchPlan.packet;
        const artifact = evidence.artifact;
        if (evidence.artifactReadbackVerified !== true || artifact?.kind !== 'issue-comment' ||
            !new RegExp(`^https://github\\.com/OlyForge3D/PrintFarmer/issues/${assignment.task.issue}#issuecomment-[0-9]+$`).test(artifact.url ?? '') ||
            !/^[0-9a-f]{64}$/.test(artifact.bodyDigest ?? '') ||
            !ack ||
            ack.artifactUrl !== artifact.url || ack.artifactBodyDigest !== artifact.bodyDigest ||
            ack.artifactReadbackVerified !== true || ack.finalDeliveryCorrelation !== evidence.finalDeliveryCorrelation ||
            ack.noChildren !== true || ack.noPendingContinuation !== true || ack.noFutureDelivery !== true) {
          fail('Research terminal receipt needs read-back issue findings and the same specialist final ACK, not chat-only completion.');
        }
        validatePacketAck(packet, ack);
      }
      // The terminal commitment also covers the runtime-known identifier set, so a
      // later deletion cannot be narrowed by editing mutable mapping aliases (#2954).
      const committed = event.data.status === 'terminal-reported'
        ? JSON.parse(JSON.stringify({ ...evidence, runtimeIdentity: terminalIdentity(prior, evidence.session.id) }))
        : evidence;
      prior.sessionId = evidence.session.id;
      prior.lastEvidenceDigest = digest(committed);
      if (event.data.status === 'terminal-reported') prior.terminalEvidence = committed;
      const { artifact: ignoredArtifact, ...receiptData } = event.data;
      event.data = { ...receiptData, evidenceDigest: digest(committed), ...(terminalArtifact ? { artifact: terminalArtifact } : {}) };
      if (event.data.status === 'terminal-reported') event.type = 'terminal-receipt';
    }
  } else if (event.type === 'report-blocker') {
    requireEvidence();
    const { prestartProof: ignoredProof, ...blocker } = event.data;
    event.data = { ...blocker, evidenceDigest: digest(evidence) };
    if (evidence.source === prestartProofSource) {
      // Publish the runtime-generated never-started proof itself so the coordinator
      // can withdraw from the mailbox alone; no session relay is part of the protocol.
      const retained = journal.prestartProofs?.[event.data.assignmentId];
      if (!retained || digest(retained) !== digest(evidence)) fail('Prestart proof must be the exact proof retained in this consumer journal.');
      event.data.prestartProof = retained;
    }
  } else if (event.type === 'withdraw') {
    requireEvidence();
    if (evidence.claimsReconciled !== true || evidence.noNativeDeliveryVerified !== true) fail('Reconcile never-delivered reservation before withdrawal; no active-worker abandonment.');
    const proof = evidence.prestartProof ?? assignment?.blocker?.prestartProof;
    if (!assignment || !['reserved', 'published'].includes(assignment.state) || assignment.receipts.length ||
        proof?.source !== 'native-runtime-prestart-proof-v1' ||
        proof.assignmentId !== assignment.assignmentId || proof.generation !== assignment.generation ||
        proof.taskDigest !== assignment.taskDigest || proof.workerId !== assignment.workerId ||
        assignment.blocker?.evidenceDigest !== digest(proof)) {
      fail('Exact consumer no-delivery proof committed by its blocker is required; coordinator absence is not proof.');
    }
    event.data = { ...event.data, reconciliationDigest: digest({ ...evidence, prestartProof: proof }) };
  } else if (event.type === 'release') {
    requireEvidence();
    if (!assignment || evidence.consumerReceiptDigest !== assignment.receipts.at(-1)?.evidenceDigest ||
        evidence.taskDigest !== assignment.taskDigest || evidence.ownershipReconciled !== true ||
        evidence.artifactsVerified !== true || evidence.noPendingContinuation !== true) fail('Coordinator must reconcile the actual consumer terminal receipt and task artifacts.');
    if (assignment.taskPacket && !context.settlement) fail('native-evidence-missing: packet-bound settlement requires the runtime GitHub readback of the published terminal artifact.');
    event.data = { ...event.data, terminalEvidenceDigest: digest(context.settlement ? { ...evidence, settlement: context.settlement } : evidence) };
    if (assignment.taskPacket) {
      // Bind settlement to the exact receipt and artifact verified above; a racing
      // replacement terminal receipt makes the reducer reject this settlement.
      event.data.terminalReceiptDigest = assignment.terminalCommitment;
      event.data.terminalArtifactDigest = digest(assignment.terminalArtifact);
    }
    event.type = 'settle';
  } else if (event.type !== 'end-round') fail('Unknown public runtime transition; internal mailbox events are not requests.');
  return { event, createAllowed };
}

export async function runNativeRequest(config, request, {
  api, cwd = process.cwd(), now = Date.now(), preflight = runAutomationPreflight, cleanupProbe = defaultCleanupProbe,
} = {}) {
  validateControl(config.control);
  if (config.verified !== true || config.migrationAttested !== true ||
      config.executionTrust !== 'local-owner-v1' ||
      config.approvedPolicy !== request.approvedPolicy ||
      (config.role === 'coordinator' && config.host !== 'macos-mobile') ||
      config.control.registry.workers.find((worker) => worker.workerId === config.workerId)?.host !== config.host) fail('Attested role, approved policy and reconciled migration are required.');
  const checked = await preflight({
    host: config.host, workflow: config.workflowId, hostConfig: request.hostConfigPath,
    approvedPolicy: config.approvedPolicy, cwd,
  });
  if (!path.isAbsolute(checked.localContext?.worktreePath ?? '') ||
      !path.isAbsolute(checked.localContext?.gitDirectory ?? '')) fail('Verified local worktree context required.');
  if (request.native !== undefined) fail('Retired native.actual is not execution proof. Use the owner-configured role contract.');
  await verifyControlRepository(config.control, api);
  const root = config.stateDirectory;
  if (!path.isAbsolute(root ?? '') || path.basename(root) !== 'native-state') fail('Approved private native-state path required; retain it across package renewals.');
  await privatePath(path.dirname(root));
  await privatePath(root);
  await mkdir(root, { recursive: true, mode: 0o700 });
  const lockPath = path.join(root, 'journal.lock');
  await privatePath(lockPath, true);
  const releaseLock = await acquireTransactionLock(lockPath, {
    role: config.role, workerId: config.workerId, requestType: request.type,
    requestId: request.id, roundId: request.roundId, localContext: checked.localContext,
  });
  try {
    if (request.type === 'initialize') {
      if (config.role !== 'coordinator' || request.explicitInitializationApproval !== true) fail('Separate explicit owner approval for queue initialization required.');
      freshEvidence(request.evidence, now);
      if (request.evidence.legacyAuthoritiesReconciled !== true || request.evidence.cessationOrFencedHandoffProven !== true) fail('Legacy authority migration evidence required.');
      const intentPath = path.join(root, 'initialization.json');
      await privatePath(intentPath, true);
      try {
        await lstat(intentPath);
        fail('Initialization intent already exists; inspect actual mailbox and reconcile, never blindly retry.');
      } catch (error) { if (error.code !== 'ENOENT') throw error; }
      const intent = { control: config.control, evidenceDigest: digest(request.evidence), localContext: checked.localContext };
      await writeJournal(intentPath, intent);
      const result = await initializeMailbox(config.control, intent.evidenceDigest, api);
      await writeJournal(intentPath, { ...intent, result });
      return result;
    }
    const journalPath = path.join(root, 'journal.json');
    await privatePath(journalPath, true);
    let journal;
    try { journal = JSON.parse(await readFile(journalPath, 'utf8')); }
    catch (error) {
      if (error.code !== 'ENOENT') throw error;
      journal = { version: 1, controlDigest: digest(config.control), sessions: {}, events: {}, requestDigests: {}, observedEvents: {} };
    }
    if (journal.version !== 1 || journal.controlDigest !== digest(config.control)) fail('Private journal belongs to another immutable control configuration.');
    journal.roundOwners ??= {};
    const snapshot = await readMailbox(config.control, api, journal.snapshot);
    for (const [id, value] of Object.entries(journal.observedEvents)) {
      if (snapshot.state.events[id] !== value) fail('Mailbox rewound or previously observed history changed.');
    }
    journal.observedEvents = snapshot.state.events;
    journal.snapshot = snapshot;
    if (request.type === 'inspect') {
      await writeJournal(journalPath, journal);
      let deletions;
      try {
        const records = [...deletionLedger(journal, snapshot.state).bySession.values()];
        deletions = { pending: records.filter((record) => record.status === 'pending').map(pendingSummary),
          deleted: records.filter((record) => record.status === 'deleted')
            .map((record) => ({ ...pendingSummary(record), outcome: record.confirmation.outcome ?? 'deleted' })) };
      } catch (error) { deletions = { error: error.message }; }
      return { ...snapshot, retainedLineage: retainNativeLineage(journal.nativeLineage, [], now), deletions, dispatchAuthorized: false };
    }
    if (request.type === 'abandon-acquisition') {
      const acquisition = journal.events[request.data?.acquisitionId];
      const owner = journal.roundOwners[request.roundId];
      if (!owner || owner.publicationProtocol !== 'record-before-ref-v1' ||
          !acquisition || acquisition.roundId !== request.roundId ||
          !['begin-round', 'recover-coordinator-round'].includes(acquisition.type) ||
          acquisition.data.invocationDigest !== owner.invocationDigest) fail('Exact recorded acquisition with durable pre-publication protocol required.');
      if (snapshot.state.events[acquisition.id] ||
          Object.values(snapshot.state.rounds).some((round) => round.invocationDigest === owner.invocationDigest)) fail('Acquisition was published; reconcile cessation, never abandon a published gate.');
      if (owner.publication && snapshot.head === owner.publication.baseSha) fail('Publication may still land on its recorded base; retain the acquisition until outcome is proven.');
      // readMailbox proved descent from the fsynced publication base. A delayed
      // single-parent candidate cannot fast-forward over its sibling's advance.
      owner.abandoned = true;
      await writeJournal(journalPath, journal);
      return { ...snapshot, acquisitionAbandoned: true, dispatchAuthorized: false, nativeCreateAllowed: false };
    }
    const requestDigest = digest({
      type: request.type, roundId: request.roundId, data: request.data ?? {}, evidence: request.evidence,
    });
    const saved = journal.events[request.id];
    journal.requestDigests ??= {};
    if (saved && journal.requestDigests[request.id]) {
      if (journal.requestDigests[request.id] !== requestDigest) fail('Local event ID changed content.');
      if (snapshot.state.events[request.id] === digest(saved)) {
        await writeJournal(journalPath, journal);
        return { ...snapshot, replayed: true, nativeCreateAllowed: false };
      }
    }
    const round = snapshot.state.rounds[config.role === 'coordinator' ? 'coordinator' : `consumer:${config.workerId}`];
    const acquiring = ['begin-round', 'recover-coordinator-round'].includes(request.type);
    let roundToken;
    let owner = journal.roundOwners[request.roundId];
    if (acquiring) {
      if (owner || saved || request.roundToken !== undefined) fail('Round acquisition already attempted; lost acquisition response requires reconciliation, not another token.');
      roundToken = randomBytes(32).toString('hex');
      owner = { localContext: checked.localContext, tokenDigest: digest(roundToken), publicationProtocol: 'record-before-ref-v1' };
      owner.invocationDigest = digest(owner);
      journal.roundOwners[request.roundId] = owner;
    } else if (!owner || !/^[0-9a-f]{64}$/.test(request.roundToken ?? '') ||
        owner.tokenDigest !== digest(request.roundToken) ||
        digest(owner.localContext) !== digest(checked.localContext) ||
        round?.invocationDigest !== owner.invocationDigest) fail('Locally acquired round token and matching worktree must own the persistent role gate.');
    if (request.type === 'research-plan') {
      if (config.role !== 'coordinator') fail('Only coordinator performs research triage.');
      freshEvidence(request.evidence, now);
      const existing = Object.values(snapshot.state.assignments).filter((entry) =>
        entry.task.issue === request.evidence.issue && entry.task.purpose === 'research');
      const assignment = existing.find((entry) => entry.state !== 'terminal') ?? existing.at(-1);
      const disposition = researchDisposition(request.evidence, assignment);
      if (assignment?.taskPacket && assignment.state === 'terminal' && assignment.disposition !== 'withdrawn-before-delivery' &&
          request.evidence.findings?.issueCommentUrl !== assignment.terminalArtifact?.url) {
        return { ...disposition, action: 'retain-research-gate', closeIssue: false, mutationAuthorized: false,
          removeLabels: [], addLabels: [], reason: 'Findings must cite the consumer-published terminal artifact for this assignment.' };
      }
      return disposition;
    }
    const assignment = snapshot.state.assignments[request.data?.assignmentId];
    const local = journal.sessions[request.data?.correlation];
    const requireBinding = () => {
      if (config.role !== 'consumer' || !assignment || assignment.workerId !== config.workerId ||
          request.data.generation !== assignment.generation || request.data.taskDigest !== assignment.taskDigest) {
        fail('Exact locally owned assignment binding required.');
      }
      freshEvidence(request.evidence, now);
    };
    if (request.type === 'record-lineage') {
      if (config.role !== 'consumer') fail('Only the consumer records native ancestry.');
      freshEvidence(request.evidence, now);
      if (!Array.isArray(request.evidence.observations) || !request.evidence.observations.length) {
        fail('Verified native ancestry observations required.');
      }
      journal.nativeLineage = retainNativeLineage(journal.nativeLineage, request.evidence.observations, now);
      await writeJournal(journalPath, journal);
      return { dispatchAuthorized: false, nativeCreateAllowed: false, retainedLineage: journal.nativeLineage };
    }
    if (['cleanup-plan', 'record-deletion-intent', 'record-deletion-result'].includes(request.type)) {
      requireCleanupRole(config);
      freshEvidence(request.evidence, now);
      const context = {
        config, evidence: request.evidence, journal, state: snapshot.state, now, api: api ?? githubApi,
        probe: cleanupProbe, readArtifact: (entry, url) => readResearchArtifact(entry, url, api),
      };
      if (request.type === 'cleanup-plan') return planWorkerCleanup({ ...context, roundId: request.roundId });
      const result = request.type === 'record-deletion-intent'
        ? await recordDeletionIntent({ request, ...context })
        : await recordDeletionResult({ request, ...context });
      await writeJournal(journalPath, journal);
      return result;
    }
    if (request.type === 'artifact-readback') {
      requireBinding();
      const artifact = !assignment.taskPacket
        ? await readResearchArtifact(assignment, request.data.artifactUrl, api)
        : /\/pull\/[0-9]+$/.test(request.data.artifactUrl ?? '')
          ? publishedArtifact(await readPullRequestArtifact(assignment, request.data.artifactUrl, api))
          : await readIssueCommentArtifact(assignment, request.data.artifactUrl, api);
      const finalDeliveryCorrelation = `final-${digest({
        assignmentId: assignment.assignmentId, generation: assignment.generation,
        taskDigest: assignment.taskDigest, artifact,
      }).slice(0, 58)}`;
      return { dispatchAuthorized: false, nativeCreateAllowed: false,
        artifactReadbackVerified: true, artifact, finalDeliveryCorrelation };
    }
    if (request.type === 'prestart-proof') {
      requireBinding();
      if (!['reserved', 'published'].includes(assignment.state) || assignment.receipts.length ||
          Object.values(journal.sessions).some((entry) => entry.assignmentId === assignment.assignmentId) ||
          request.evidence.authoritativeJournalRetained !== true ||
          request.evidence.protocolOnlyDeliveryAttested !== true) {
        fail('Continuous published/reserved history and intact consumer journal with no delivery intent required.');
      }
      journal.prestartProofs ??= {};
      const retained = journal.prestartProofs[assignment.assignmentId];
      // A pre-#2958 blocker committed only the digest; mint a fresh proof so it can be published.
      if (retained && retained.generation === assignment.generation && retained.taskDigest === assignment.taskDigest &&
          assignment.blocker?.evidenceDigest === digest(retained) &&
          digest(assignment.blocker.prestartProof ?? null) === digest(retained)) {
        await writeJournal(journalPath, journal);
        return { dispatchAuthorized: false, nativeCreateAllowed: false, alreadyReported: true, proof: retained };
      }
      const proof = {
        source: 'native-runtime-prestart-proof-v1', observedAt: new Date(now).toISOString(),
        assignmentId: assignment.assignmentId, generation: assignment.generation,
        taskDigest: assignment.taskDigest, workerId: config.workerId,
        mailboxHead: snapshot.head, consumerJournalDigest: digest(journal.sessions),
      };
      journal.prestartProofs[assignment.assignmentId] = proof;
      await writeJournal(journalPath, journal);
      return { dispatchAuthorized: false, nativeCreateAllowed: false, alreadyReported: false, proof };
    }
    if (['record-creation', 'startup-check'].includes(request.type)) {
      requireBinding();
      const evidence = request.evidence;
      if (!local?.dispatchPlan || local.assignmentId !== assignment.assignmentId ||
          !['starting', 'uncertain'].includes(assignment.state) ||
          evidence.dispatchPlanDigest !== local.dispatchPlan.planDigest) fail('Exact native creation/readback and saved dispatch plan required.');
      if (request.type === 'record-creation') {
        if (!uuidPattern.test(evidence.creationHandle ?? '') ||
            !['succeeded', 'partial'].includes(evidence.creationOutcome) ||
            (local.creationHandle && local.creationHandle !== evidence.creationHandle) ||
            (evidence.session && local.worktreePath && local.worktreePath !== evidence.session.worktreePath) ||
            (evidence.session && local.sessionId && local.sessionId !== evidence.session.id &&
              evidence.resolvedCreationHandle !== local.creationHandle) ||
            (evidence.creationOutcome === 'succeeded' &&
              (evidence.createRequestDigest !== digest(local.dispatchPlan.nativeArguments) || evidence.kickoffAccepted !== true)) ||
            (local.creationOutcome === 'partial' && evidence.creationOutcome !== 'partial')) {
          fail('Retain original creation handle/outcome; alias changes need readback of that handle, never replacement.');
        }
        local.creationHandle = evidence.creationHandle;
        local.creationOutcome = evidence.creationOutcome;
        if (!evidence.session) {
          await writeJournal(journalPath, journal);
          return { nativeCreateAllowed: false, creationHandle: local.creationHandle, reconciliationRequired: true };
        }
      }
      if (!uuidPattern.test(evidence.session?.id ?? '') ||
          evidence.nativeReadbackVerified !== true || evidence.repository !== 'OlyForge3D/PrintFarmer' ||
          evidence.session.projectId !== config.projectId ||
          !withinWorktreeRoot(config.worktreeRoot, evidence.session.worktreePath) ||
          Object.entries(journal.sessions).some(([correlation, entry]) =>
            correlation !== request.data.correlation && entry.sessionId === evidence.session.id)) {
        fail('Fresh same-project isolated native readback required; never share a session between assignments.');
      }
      if (request.type === 'record-creation') {
        local.sessionAliases = [...new Set([...(local.sessionAliases ?? []), local.sessionId, evidence.session.id].filter(Boolean))];
        local.sessionId = evidence.session.id;
        local.worktreePath = evidence.session.worktreePath;
        local.creationEvidenceDigest = digest(evidence);
        await writeJournal(journalPath, journal);
        return { nativeCreateAllowed: false, sessionId: local.sessionId, creationHandle: local.creationHandle };
      }
      if (!local.creationHandle || local.sessionId !== evidence.session.id ||
          local.worktreePath !== evidence.session.worktreePath ||
          (local.creationOutcome !== 'succeeded' && evidence.configuration?.source === 'successful-native-create')) {
        fail('Partial startup requires actual configuration readback or explicit owner attestation on the same child.');
      }
      const verifiedHeadAdvance = await verifyDevelopmentAdvance(local.dispatchPlan.packet, evidence.startupAck?.initialHeadSha, api ?? githubApi);
      validateStartup(local.dispatchPlan, evidence, { verifiedHeadAdvance });
      if (!local.continuationIntent && assignment.taskPacket) {
        // Re-read the subject before the first substantive continuation: an edit or
        // hold after `starting` keeps the child startup-only.
        try {
          const current = composeTaskEvidence(assignment, await readTaskSubject(assignment.task, api ?? githubApi), {});
          validateTriageEvidence(current);
          if (assignment.task.pr && (String(current.prState).toLowerCase() !== 'open' ||
              current.prHeadRepository !== repository || current.prHeadSha !== assignment.task.headSha)) {
            fail('task-changed: assigned PR is no longer open at the reserved same-repository head.');
          }
        } catch (error) {
          const code = /blocked: (held|github-readback-invalid|task-packet-tampered):/.exec(error.message)?.[1] ??
            (error.message.startsWith('Native Ralph blocked:') ? 'task-changed' : 'github-readback-invalid');
          fail(`${code}: no substantive continuation; keep the child startup-only. ${code === 'github-readback-invalid'
            ? 'Retry startup-check after GitHub is readable.'
            : `Report-blocker ${code === 'held' ? 'held' : 'task-changed'} so the coordinator reconciles recovery.`} (${error.message})`);
        }
      }
      const continuationAllowed = !local.continuationIntent;
      local.continuationIntent ??= { requestId: request.id, evidenceDigest: digest(evidence) };
      local.startupEvidenceDigest = digest(evidence);
      await writeJournal(journalPath, journal);
      return {
        nativeCreateAllowed: false, continuationAllowed, sessionId: local.sessionId,
        ...(continuationAllowed ? { continuation: local.dispatchPlan.continuation } : {}),
      };
    }
    const context = {};
    let evidenceRequest = request;
    if (request.type === 'reserve' && config.role === 'coordinator') {
      freshEvidence(request.evidence, now);
      validateTriageEvidence(request.evidence);
      validateClassification(request.evidence);
      const live = await readTaskSubject({ issue: request.evidence.issue, pr: request.evidence.pr }, api ?? githubApi);
      verifyReservationReadback(request.evidence, live);
      context.taskPacket = taskPacketFromEvidence(request.evidence, live.bodySha256);
    }
    const packetStart = (request.type === 'publish' && config.role === 'coordinator') ||
      (config.role === 'consumer' && (request.type === 'dispatch-plan' || (request.type === 'receipt' && request.data?.status === 'starting')));
    if (packetStart) {
      if (config.role === 'consumer') requireBinding();
      else if (!assignment || request.data?.generation !== assignment.generation || request.data?.taskDigest !== assignment.taskDigest) {
        fail('Recheck exact task, holds and ownership immediately before publication.');
      }
      freshEvidence(request.evidence, now);
      const legacyStartReplay = !assignment.taskPacket && request.type === 'receipt' &&
        journal.sessions[request.data?.correlation]?.startEventId === request.id;
      if (!legacyStartReplay) {
        // Packetless assignments already delivered before #2958 may only replay their saved start.
        if (!assignment.taskPacket) composeTaskEvidence(assignment);
        const live = await readTaskSubject(assignment.task, api ?? githubApi);
        evidenceRequest = { ...request, evidence: composeTaskEvidence(assignment, live, request.evidence) };
        if (config.role === 'consumer' && assignment.task.pr && live.prHeadSha === assignment.task.headSha) {
          const { owner: prOwner } = validateTriageEvidence(evidenceRequest.evidence);
          const derived = await readPrWorkerPolicyDigest(assignment.task.headSha, prOwner.slice('squad:'.length), api ?? githubApi);
          if (request.evidence.prWorkerPolicyDigest !== undefined && request.evidence.prWorkerPolicyDigest !== derived) {
            fail('github-readback-mismatch: supplied prWorkerPolicyDigest differs from the runtime PR-head policy readback; omit it.');
          }
          evidenceRequest.evidence.prWorkerPolicyDigest = derived;
        }
      }
    }
    let dispatchPlan;
    if (request.type === 'dispatch-plan' ||
        (request.type === 'receipt' && request.data?.status === 'starting')) {
      requireBinding();
      const { owner: member } = validateTriageEvidence(evidenceRequest.evidence);
      if (assignment.policySha !== config.approvedPolicy ||
          !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(request.data.correlation ?? '')) {
        fail('Matching policy and opaque correlation required before planning kickoff.');
      }
      dispatchPlan = await buildDispatchPlan({
        config, evidence: evidenceRequest.evidence, assignment, correlation: request.data.correlation, owner: member, cwd,
      });
      if (request.type === 'dispatch-plan') return {
        dispatchAuthorized: false, nativeCreateAllowed: false, dispatchPlan,
        inventoryFreshness: localInventoryFreshness(snapshot.state, config.workerId, now),
      };
    }
    if (request.type === 'release' && config.role === 'coordinator' && assignment?.taskPacket) {
      freshEvidence(request.evidence, now);
      context.settlement = await verifyTerminalArtifact(assignment, request.evidence, api ?? githubApi);
    }
    const prepared = prepareEvent(config, evidenceRequest, snapshot, journal, now, owner.invocationDigest, dispatchPlan, context);
    if (request.type === 'receipt' && request.data?.status === 'terminal-reported' && local?.dispatchPlan &&
        (assignment?.taskPacket || ['research', 'analysis'].includes(assignment?.task.purpose))) {
      const claimed = request.evidence?.artifact;
      if (claimed?.kind === 'pull-request') {
        const artifact = await readPullRequestArtifact(assignment, claimed.url, api ?? githubApi);
        if (artifact.number !== claimed.number || artifact.headSha !== claimed.headSha) {
          fail('Pull-request artifact head moved or was misreported; obtain the same worker ACK for the exact PR head.');
        }
        local.terminalArtifact = publishedArtifact(artifact);
      } else {
        const artifact = assignment.taskPacket
          ? await readIssueCommentArtifact(assignment, claimed?.url, api ?? githubApi)
          : await readResearchArtifact(assignment, claimed?.url, api);
        if (artifact.bodyDigest !== claimed.bodyDigest) {
          fail('Research artifact bytes changed or were hashed incorrectly; obtain the same worker ACK for the exact API body.');
        }
        local.terminalArtifact = { url: artifact.url, bodyDigest: artifact.bodyDigest };
      }
    }
    if (saved) {
      if (digest({ ...prepared.event, observedAt: saved.observedAt }) !== digest(saved)) fail('Local event ID changed content.');
      prepared.event = saved;
      prepared.createAllowed = false;
    }
    journal.events[request.id] = prepared.event;
    journal.requestDigests[request.id] = requestDigest;
    applyEvent(snapshot.state, prepared.event, { now });
    await writeJournal(journalPath, journal);
    const result = await publishEvent(config.control, prepared.event, api, snapshot, acquiring ? async ({ base, candidateSha }) => {
      owner.publication = { baseSha: base.head, candidateSha };
      journal.snapshot = base;
      journal.observedEvents = base.state.events;
      await writeJournal(journalPath, journal);
    } : undefined);
    journal.observedEvents = result.state.events;
    journal.snapshot = { head: result.head, state: result.state };
    await writeJournal(journalPath, journal);
    return {
      ...result, nativeCreateAllowed: prepared.createAllowed && !result.replayed,
      ...(prepared.createAllowed && !result.replayed ? { dispatchPlan } : {}),
      ...(roundToken && !result.replayed ? { roundToken } : {}),
      message: 'A lost create/ack response NEVER authorizes another native creation. Keep local correlation and reconcile.',
    };
  } finally {
    await releaseLock();
  }
}

async function main() {
  const [flag, hostConfigPath] = process.argv.slice(2);
  if (flag !== '--host-config' || !hostConfigPath || process.argv.length !== 4) fail('Usage: node scripts/ci/ralph-native-runtime.mjs --host-config ABS_JSON < request.json');
  await privatePath(hostConfigPath, true);
  const config = JSON.parse(await readFile(hostConfigPath, 'utf8'));
  let input = '';
  for await (const chunk of process.stdin) {
    input += chunk;
    if (Buffer.byteLength(input) > 4 * 1024 * 1024) fail('Request too large; never truncate evidence.');
  }
  const request = { ...JSON.parse(input), hostConfigPath };
  process.stdout.write(`${JSON.stringify(await runNativeRequest(config, request))}\n`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main().catch((error) => {
    process.stderr.write(`Native Ralph blocked: ${error.message}\n`);
    process.exitCode = 1;
  });
}
