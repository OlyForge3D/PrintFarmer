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
} from './ralph-mailbox.mjs';
import { runAutomationPreflight } from './ralph-automation.mjs';
import { acquireTransactionLock } from './ralph-native-lock.mjs';
import { retainNativeLineage, resolveNativeLineage } from './ralph-native-lineage.mjs';
import {
  buildDispatchPlan, validateClassification, validateNativeCapabilities, validateStartup, validatePacketAck,
} from './ralph-native-dispatch.mjs';

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
    if (!sessions.has(id)) fail('Every retained Ralph native mapping needs fresh session evidence, including terminal assignments.');
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

export function prepareEvent(config, request, snapshot, journal, now = Date.now(), invocationDigest, dispatchPlan) {
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
    event.data = {
      assignmentId: request.data.assignmentId, workerId: request.data.workerId, generation: 1,
      task: taskFromEvidence(evidence), eligibilityDigest: digest(evidence), policySha: config.approvedPolicy,
      offerId: offer.offerId,
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
      prior.sessionId = evidence.session.id;
      prior.lastEvidenceDigest = digest(evidence);
      event.data = { ...event.data, evidenceDigest: digest(evidence) };
      if (event.data.status === 'terminal-reported') event.type = 'terminal-receipt';
    }
  } else if (event.type === 'report-blocker') {
    requireEvidence();
    event.data = { ...event.data, evidenceDigest: digest(evidence) };
  } else if (event.type === 'withdraw') {
    requireEvidence();
    if (evidence.claimsReconciled !== true || evidence.noNativeDeliveryVerified !== true) fail('Reconcile never-delivered reservation before withdrawal; no active-worker abandonment.');
    const proof = evidence.prestartProof;
    if (!assignment || !['reserved', 'published'].includes(assignment.state) || assignment.receipts.length ||
        proof?.source !== 'native-runtime-prestart-proof-v1' ||
        proof.assignmentId !== assignment.assignmentId || proof.generation !== assignment.generation ||
        proof.taskDigest !== assignment.taskDigest || proof.workerId !== assignment.workerId ||
        assignment.blocker?.evidenceDigest !== digest(proof)) {
      fail('Exact consumer no-delivery proof committed by its blocker is required; coordinator absence is not proof.');
    }
    event.data = { ...event.data, reconciliationDigest: digest(evidence) };
  } else if (event.type === 'release') {
    requireEvidence();
    if (!assignment || evidence.consumerReceiptDigest !== assignment.receipts.at(-1)?.evidenceDigest ||
        evidence.taskDigest !== assignment.taskDigest || evidence.ownershipReconciled !== true ||
        evidence.artifactsVerified !== true || evidence.noPendingContinuation !== true) fail('Coordinator must reconcile the actual consumer terminal receipt and task artifacts.');
    event.data = { ...event.data, terminalEvidenceDigest: digest(evidence) };
    event.type = 'settle';
  } else if (event.type !== 'end-round') fail('Unknown public runtime transition; internal mailbox events are not requests.');
  return { event, createAllowed };
}

export async function runNativeRequest(config, request, {
  api, cwd = process.cwd(), now = Date.now(), preflight = runAutomationPreflight,
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
      return { ...snapshot, retainedLineage: retainNativeLineage(journal.nativeLineage, [], now), dispatchAuthorized: false };
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
      return researchDisposition(request.evidence, assignment);
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
    if (request.type === 'artifact-readback') {
      requireBinding();
      const artifact = await readResearchArtifact(assignment, request.data.artifactUrl, api);
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
      if (retained && retained.generation === assignment.generation && retained.taskDigest === assignment.taskDigest &&
          assignment.blocker?.evidenceDigest === digest(retained)) {
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
      validateStartup(local.dispatchPlan, evidence);
      const continuationAllowed = !local.continuationIntent;
      local.continuationIntent ??= { requestId: request.id, evidenceDigest: digest(evidence) };
      local.startupEvidenceDigest = digest(evidence);
      await writeJournal(journalPath, journal);
      return {
        nativeCreateAllowed: false, continuationAllowed, sessionId: local.sessionId,
        ...(continuationAllowed ? { continuation: local.dispatchPlan.continuation } : {}),
      };
    }
    let dispatchPlan;
    if (request.type === 'dispatch-plan' ||
        (request.type === 'receipt' && request.data?.status === 'starting')) {
      requireBinding();
      const { owner: member } = validateTriageEvidence(request.evidence);
      if (assignment.policySha !== config.approvedPolicy ||
          !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(request.data.correlation ?? '')) {
        fail('Matching policy and opaque correlation required before planning kickoff.');
      }
      dispatchPlan = await buildDispatchPlan({
        config, evidence: request.evidence, assignment, correlation: request.data.correlation, owner: member, cwd,
      });
      if (request.type === 'dispatch-plan') return {
        dispatchAuthorized: false, nativeCreateAllowed: false, dispatchPlan,
        inventoryFreshness: localInventoryFreshness(snapshot.state, config.workerId, now),
      };
    }
    const prepared = prepareEvent(config, request, snapshot, journal, now, owner.invocationDigest, dispatchPlan);
    if (request.type === 'receipt' && request.data?.status === 'terminal-reported' &&
        local?.dispatchPlan && ['research', 'analysis'].includes(assignment?.task.purpose)) {
      const artifact = await readResearchArtifact(assignment, request.evidence?.artifact?.url, api);
      if (artifact.bodyDigest !== request.evidence.artifact.bodyDigest) {
        fail('Research artifact bytes changed or were hashed incorrectly; obtain the same worker ACK for the exact API body.');
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
