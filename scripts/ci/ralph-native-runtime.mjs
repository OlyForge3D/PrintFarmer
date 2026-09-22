import { constants } from 'node:fs';
import { randomBytes } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { lstat, mkdir, open, readFile, rename } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import {
  applyEvent, digest, admissionInventoryDigest, readMailbox, publishEvent, initializeMailbox,
  taskFromEvidence, researchDisposition, validateControl, verifyControlRepository,
} from './ralph-mailbox.mjs';
import { runAutomationPreflight } from './ralph-automation.mjs';

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const fail = (message) => { throw new Error(`Native Ralph blocked: ${message}`); };
const exec = promisify(execFile);

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
      await exec('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
        env: { ...process.env, RALPH_PRIVATE_CHECK_PATH: target }, timeout: 30_000,
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

export function prepareEvent(config, request, snapshot, journal, now = Date.now(), invocationDigest) {
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
    if (evidence.complete !== true || evidence.queueChecked !== true || evidence.historyChecked !== true ||
        evidence.capabilitiesVerified !== true || !Array.isArray(evidence.sessions)) fail('Complete native inventory, queue/history and local tooling checks required.');
    const known = new Map(Object.values(map).filter((entry) => entry.sessionId).map((entry) => [entry.sessionId, entry]));
    for (const session of evidence.sessions) {
      if (!uuidPattern.test(session.id ?? '')) fail('Malformed local native session identity.');
      if (session.terminalVerified === true) continue;
      const role = session.roleObservation;
      if (role && session.nativeReadbackVerified === true &&
          role.projectId === config.projectId &&
          ['coordinator', 'consumer'].includes(role.role) &&
          (role.role !== 'coordinator' || config.host === 'macos-mobile') &&
          role.workerId === config.workerId && role.ownerConfiguredRoleVerified === true &&
          role.noTaskExecutionVerified === true &&
          withinWorktreeRoot(config.worktreeRoot, role.worktreePath)) continue;
      const mapping = known.get(session.id);
      const owned = mapping && snapshot.state.assignments[mapping.assignmentId];
      if (!owned || owned.workerId !== config.workerId || owned.state === 'terminal') fail('Pre-existing/unassigned or resumed terminal native work blocks readiness; reconcile ownership first.');
    }
    for (const entry of Object.values(snapshot.state.assignments).filter((item) => item.workerId === config.workerId && !['reserved', 'published', 'terminal'].includes(item.state))) {
      const local = map[entry.correlation];
      if (!local?.sessionId || !evidence.sessions.some((session) => session.id === local.sessionId &&
          (entry.state === 'terminal-reported' ? session.terminalVerified === true : session.ownershipVerified === true))) fail('Every live/uncertain receipt needs fresh correlated native evidence.');
    }
    event.data = {
      inventoryDigest: digest(evidence), assignmentInventoryDigest: admissionInventoryDigest(snapshot.state, config.workerId),
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
        map[correlation] = { assignmentId: event.data.assignmentId, startEventId: request.id };
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
      if (Object.entries(map).some(([otherCorrelation, entry]) => otherCorrelation !== correlation && entry.sessionId === evidence.session.id)) fail('Native session is already mapped to another assignment.');
      if (event.data.status === 'terminal-reported' &&
          (evidence.session.terminalVerified !== true || evidence.queueChecked !== true ||
            evidence.historyChecked !== true || evidence.artifactsVerified !== true ||
            evidence.noPendingContinuation !== true || evidence.noFutureDelivery !== true ||
            !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(evidence.finalDeliveryCorrelation ?? ''))) fail('Terminal report requires cessation, final delivery ACK, no future delivery commitment, queue/history and artifact evidence.');
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
  const lock = await open(lockPath, 'wx', 0o600).catch(() => fail('Private transaction lock exists; reconcile interrupted write, never steal by age.'));
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
      return { ...snapshot, dispatchAuthorized: false };
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
    const prepared = prepareEvent(config, request, snapshot, journal, now, owner.invocationDigest);
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
      ...(roundToken && !result.replayed ? { roundToken } : {}),
      message: 'A lost create/ack response NEVER authorizes another native creation. Keep local correlation and reconcile.',
    };
  } finally {
    await lock.close();
    const { unlink } = await import('node:fs/promises');
    await unlink(lockPath);
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
