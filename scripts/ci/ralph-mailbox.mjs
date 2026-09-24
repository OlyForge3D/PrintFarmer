import { createHash } from 'node:crypto';
import { execFile } from 'node:child_process';
import { classifyWork, hostLimits } from './ralph-host-capacity.mjs';

const idPattern = /^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$/;
const shaPattern = /^[0-9a-f]{40}$/;
const digestPattern = /^[0-9a-f]{64}$/;
const heldLabels = new Set(['go:no', 'status:on-hold', 'on-hold', 'status:blocked', 'blocked', 'status:wontfix', 'do-not-merge']);
const maxRecordBytes = 1024 * 1024;
const maxObservationAgeMs = 60_000;
const liveStates = new Set(['reserved', 'published', 'starting', 'running', 'review', 'recovery', 'uncertain', 'terminal-reported']);
const fail = (message) => { throw new Error(`Mailbox blocked: ${message}`); };
export const digest = (value) => createHash('sha256').update(JSON.stringify(value)).digest('hex');

function exact(value, keys) {
  if (!value || typeof value !== 'object' || Array.isArray(value) ||
      Object.keys(value).some((key) => !keys.includes(key))) fail('Unexpected fields; never send native IDs, paths, prompts or credentials to the queue.');
}
function identifier(value) {
  if (typeof value !== 'string' || !idPattern.test(value)) fail('Invalid opaque identifier.');
  return value;
}
function fresh(value, now, age = maxObservationAgeMs) {
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed) || parsed > now || now - parsed > age) fail('Fresh complete observations required.');
}

export function localInventoryFreshness(state, workerId, now) {
  const observedAt = state.readiness?.[workerId]?.observedAt;
  const parsed = Date.parse(observedAt);
  const ageMs = Number.isFinite(parsed) ? now - parsed : undefined;
  return {
    observedAt, ageMs, maxAgeMs: maxObservationAgeMs,
    refreshRequired: ageMs === undefined || ageMs < 0 || ageMs > maxObservationAgeMs,
  };
}

export function validateRegistry(registry) {
  exact(registry, ['version', 'authorityId', 'epoch', 'writers', 'workers']);
  if (registry.version !== 1 || !Number.isSafeInteger(registry.epoch) || registry.epoch < 1) fail('Invalid authority epoch.');
  identifier(registry.authorityId);
  if (!Array.isArray(registry.writers) || !registry.writers.length ||
      registry.writers.some((name) => typeof name !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9-]{0,38}$/.test(name)) ||
      new Set(registry.writers.map((name) => name.toLowerCase())).size !== registry.writers.length) fail('Approved GitHub writer logins required.');
  if (!Array.isArray(registry.workers) || registry.workers.length !== 2) fail('Exactly one Mac and one Windows consumer are supported.');
  const hosts = new Set();
  const ids = new Set();
  for (const worker of registry.workers) {
    exact(worker, ['workerId', 'host', 'capabilities']);
    identifier(worker.workerId);
    hostLimits(worker.host);
    if (hosts.has(worker.host) || ids.has(worker.workerId)) fail('Duplicate worker/host would multiply quota.');
    hosts.add(worker.host); ids.add(worker.workerId);
    if (!Array.isArray(worker.capabilities) || !worker.capabilities.length ||
        worker.capabilities.some((capability) => typeof capability !== 'string' || !idPattern.test(capability)) ||
        new Set(worker.capabilities).size !== worker.capabilities.length) fail('Explicit capability IDs required.');
  }
  return registry;
}

export function validateControl(config) {
  exact(config, ['repository', 'repositoryId', 'ref', 'genesisSha', 'registry', 'sharedWriterTrustAccepted']);
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(config.repository ?? '') ||
      config.repository.toLowerCase() === 'olyforge3d/printfarmer' ||
      !Number.isSafeInteger(config.repositoryId) || config.repositoryId < 1 ||
      !/^heads\/[A-Za-z0-9_-][A-Za-z0-9_/-]*$/.test(config.ref ?? '') ||
      config.ref.endsWith('/') || config.ref.includes('//') ||
      (config.genesisSha !== undefined && !shaPattern.test(config.genesisSha)) ||
      config.sharedWriterTrustAccepted !== true) fail('Explicit private control repository identity, ref and shared-writer trust required.');
  validateRegistry(config.registry);
  return config;
}

export function initialState(registry) {
  validateRegistry(registry);
  return {
    version: 1, registry: structuredClone(registry), sequence: 0, rounds: {},
    readiness: {}, assignments: {}, events: {},
  };
}

function workerFor(state, workerId) {
  const worker = state.registry.workers.find((entry) => entry.workerId === workerId);
  if (!worker) fail('Unknown worker.');
  return worker;
}
function roundKey(role, workerId) { return role === 'coordinator' ? 'coordinator' : `consumer:${workerId}`; }

export function inventoryDigest(state, workerId) {
  return digest(Object.entries(state.assignments).filter(([, entry]) => entry.workerId === workerId)
    .map(([id, entry]) => [id, entry.generation, entry.state, entry.correlation ?? '', entry.taskDigest]));
}

export function admissionInventoryDigest(state, workerId) {
  return digest(Object.entries(state.assignments).filter(([, entry]) => entry.workerId === workerId));
}

function validateTask(task) {
  exact(task, ['issue', 'pr', 'headSha', 'requirementsDigest', 'fileKeys', 'category', 'capabilities', 'purpose']);
  if ((!Number.isSafeInteger(task.issue) || task.issue < 1) &&
      (!Number.isSafeInteger(task.pr) || task.pr < 1)) fail('Issue or PR identity required.');
  if (task.issue !== undefined && (!Number.isSafeInteger(task.issue) || task.issue < 1)) fail('Invalid issue.');
  if (task.pr !== undefined && (!Number.isSafeInteger(task.pr) || task.pr < 1)) fail('Invalid PR.');
  if (!['research', 'analysis', 'implementation', 'recovery'].includes(task.purpose) ||
      !shaPattern.test(task.headSha ?? '') || !digestPattern.test(task.requirementsDigest ?? '') ||
      !['mobile', 'general'].includes(task.category) || !Array.isArray(task.fileKeys) || !task.fileKeys.length ||
      task.fileKeys.some((key) => !digestPattern.test(key)) || new Set(task.fileKeys).size !== task.fileKeys.length ||
      !Array.isArray(task.capabilities) || task.capabilities.some((key) => !idPattern.test(key))) fail('Exact task/head, complete file digests and requirements required.');
}

export const taskPacketVersion = 'ralph-task-packet-v1';
export const prestartProofSource = 'native-runtime-prestart-proof-v1';
const repositoryName = 'OlyForge3D/PrintFarmer';
const maxPacketBytes = 64 * 1024;
const packetKeys = ['version', 'repository', 'issue', 'pr', 'purpose', 'headSha', 'title', 'labels',
  'acceptanceCriteria', 'files', 'scope', 'classificationComplete', 'capabilities', 'sourceBodySha256'];
const text = (value, max) => typeof value === 'string' && value.length <= max && !/[\u0000]/.test(value);
// Coordinator-authored free text is not verified against public GitHub facts, so it
// must not carry local paths, native/session UUIDs or recognizable credentials.
const privateTextPattern = new RegExp([
  /(?:^|[^A-Za-z0-9._-])\/(?:Users|home|root|private|var\/folders|Volumes)\//.source,
  /(?:^|[\s"'(=:])~[\\/]/.source, /(?:^|[^A-Za-z0-9])[A-Za-z]:\\/.source, /(?:^|\s)\\\\[A-Za-z0-9]/.source,
  /\.printfarmer-ralph/.source, /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/.source,
  /\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9_-]{20,}|xox[abprs]-[A-Za-z0-9-]{10,})/.source,
  /-----BEGIN [A-Z ]*PRIVATE KEY-----/.source,
].join('|'), 'i');

export function hasHeldLabel(labels) {
  return Array.isArray(labels) && labels.some((label) => heldLabels.has(String(label).toLowerCase()));
}

// The exact normalized facts behind a reservation's digests. Only public issue
// facts and repository-relative paths are allowed: never local paths or native IDs.
export function validateTaskPacket(packet) {
  exact(packet, packetKeys);
  if (packet.version !== taskPacketVersion || packet.repository !== repositoryName ||
      !['research', 'analysis', 'implementation', 'recovery'].includes(packet.purpose) ||
      !shaPattern.test(packet.headSha ?? '') || !digestPattern.test(packet.sourceBodySha256 ?? '') ||
      (packet.issue !== undefined && (!Number.isSafeInteger(packet.issue) || packet.issue < 1)) ||
      (packet.pr !== undefined && (!Number.isSafeInteger(packet.pr) || packet.pr < 1)) ||
      (packet.issue === undefined && packet.pr === undefined) ||
      !text(packet.title, 1024) || !text(packet.scope, 64) ||
      typeof packet.classificationComplete !== 'boolean' ||
      !Array.isArray(packet.labels) || packet.labels.some((label) => !text(label, 256)) ||
      !Array.isArray(packet.acceptanceCriteria) || packet.acceptanceCriteria.some((item) => !text(item, 8192)) ||
      !Array.isArray(packet.capabilities) || packet.capabilities.some((key) => typeof key !== 'string' || !idPattern.test(key)) ||
      !Array.isArray(packet.files) || !packet.files.length ||
      packet.files.some((file) => !text(file, 1024) || /^(?:[A-Za-z]:|~|[\\/])/.test(file) ||
        /(?:^|[\\/])\.\.(?:[\\/]|$)/.test(file)) ||
      Buffer.byteLength(JSON.stringify(packet)) > maxPacketBytes) fail('Invalid task packet: exact public task facts and repository-relative files only.');
  if ([...packet.acceptanceCriteria, packet.scope, ...packet.files].some((value) => privateTextPattern.test(value))) {
    fail('Invalid task packet: acceptance criteria, scope and files must not contain local paths, native IDs or credentials.');
  }
  return packet;
}

export function taskPacketFromEvidence(evidence, sourceBodySha256) {
  const task = taskFromEvidence(evidence);
  const packet = {
    version: taskPacketVersion, repository: repositoryName,
    ...(evidence.issue ? { issue: evidence.issue } : {}), ...(evidence.pr ? { pr: evidence.pr } : {}),
    purpose: task.purpose, headSha: evidence.headSha, title: evidence.title,
    labels: [...evidence.labels], acceptanceCriteria: [...evidence.acceptanceCriteria], files: [...evidence.files],
    scope: evidence.scope, classificationComplete: evidence.classificationComplete,
    capabilities: [...evidence.capabilities], sourceBodySha256,
  };
  validateTaskPacket(packet);
  if (digest(taskFromPacket(packet)) !== digest(task)) fail('Task packet cannot reproduce its task identity.');
  return packet;
}

export function taskFromPacket(packet) {
  validateTaskPacket(packet);
  const { version, sourceBodySha256, repository, ...facts } = packet;
  return taskFromEvidence({ ...facts, filesComplete: true });
}

function validatePrestartProof(proof, assignment, workerId, evidenceDigest) {
  exact(proof, ['source', 'observedAt', 'assignmentId', 'generation', 'taskDigest', 'workerId', 'mailboxHead', 'consumerJournalDigest']);
  if (proof.source !== prestartProofSource || !Number.isFinite(Date.parse(proof.observedAt)) ||
      proof.assignmentId !== assignment.assignmentId || proof.generation !== assignment.generation ||
      proof.taskDigest !== assignment.taskDigest || proof.workerId !== workerId || assignment.workerId !== workerId ||
      !shaPattern.test(proof.mailboxHead ?? '') || !digestPattern.test(proof.consumerJournalDigest ?? '') ||
      digest(proof) !== evidenceDigest || !['reserved', 'published'].includes(assignment.state) ||
      assignment.receipts.length) fail('Published prestart proof must be the exact never-started proof committed by this blocker.');
}

export function validateTerminalArtifact(artifact, task) {
  const subject = task.issue ?? task.pr;
  if (artifact?.kind === 'issue-comment') {
    exact(artifact, ['kind', 'url', 'bodyDigest']);
    if (!new RegExp(`^https://github\\.com/${repositoryName}/issues/${subject}#issuecomment-[0-9]+$`).test(artifact.url ?? '') ||
        !digestPattern.test(artifact.bodyDigest ?? '')) fail('Issue-comment artifact must be a read-back comment on the assigned issue.');
  } else if (artifact?.kind === 'pull-request') {
    exact(artifact, ['kind', 'url', 'number', 'headSha']);
    if (!['implementation', 'recovery'].includes(task.purpose) || !Number.isSafeInteger(artifact.number) || artifact.number < 1 ||
        artifact.url !== `https://github.com/${repositoryName}/pull/${artifact.number}` ||
        (task.pr !== undefined && artifact.number !== task.pr) || !shaPattern.test(artifact.headSha ?? '')) {
      fail('Pull-request artifact must be the exact same-repository PR and head for implementation work.');
    }
  } else fail('Unknown terminal artifact kind.');
  return artifact;
}

export function applyEvent(previous, event, { now = Date.now(), replay = false } = {}) {
  exact(event, ['id', 'type', 'authorityId', 'epoch', 'role', 'workerId', 'roundId', 'observedAt', 'data']);
  identifier(event.id); identifier(event.roundId);
  if (!['coordinator', 'consumer'].includes(event.role) ||
      event.authorityId !== previous.registry.authorityId || event.epoch !== previous.registry.epoch) fail('Wrong authority or role.');
  if (!Number.isFinite(Date.parse(event.observedAt))) fail('Invalid observation time.');
  const eventDigest = digest(event);
  if (previous.events[event.id]) {
    if (previous.events[event.id] !== eventDigest) fail('Event ID reused for different content.');
    return previous;
  }
  if (!replay) fresh(event.observedAt, now);
  if (event.role === 'consumer') workerFor(previous, event.workerId);
  else if (event.workerId !== undefined) fail('Coordinator role cannot masquerade as a consumer.');
  const state = structuredClone(previous);
  const key = roundKey(event.role, event.workerId);
  const data = event.data;
  const coordinator = () => { if (event.role !== 'coordinator') fail('Only coordinator may assign or release work.'); };
  const consumer = () => { if (event.role !== 'consumer') fail('Only assigned consumer may report local lifecycle.'); };
  const getAssignment = () => {
    const assignment = state.assignments[data.assignmentId];
    if (!assignment || assignment.generation !== data.generation ||
        assignment.taskDigest !== data.taskDigest ||
        (event.role === 'consumer' && assignment.workerId !== event.workerId)) fail('Assignment binding mismatch.');
    return assignment;
  };
  if (event.type === 'recover-coordinator-round') {
    coordinator();
    exact(data, ['oldRoundId', 'cessationEvidenceDigest', 'invocationDigest']);
    if (!state.rounds.coordinator || state.rounds.coordinator.roundId !== data.oldRoundId ||
        data.oldRoundId === event.roundId || !digestPattern.test(data.cessationEvidenceDigest ?? '') ||
        !digestPattern.test(data.invocationDigest ?? '')) fail('Exact prior coordinator and proven cessation required; never steal a live round.');
    state.rounds.coordinator = { roundId: event.roundId, invocationDigest: data.invocationDigest };
  } else if (event.type === 'begin-round') {
    exact(data, ['invocationDigest']);
    if (!digestPattern.test(data.invocationDigest ?? '') || state.rounds[key]) fail('Role already has a round or current invocation digest is missing.');
    state.rounds[key] = { roundId: event.roundId, invocationDigest: data.invocationDigest };
  } else {
    if (state.rounds[key]?.roundId !== event.roundId) fail('Round is not the active role owner.');
    switch (event.type) {
      case 'end-round':
        exact(data, []);
        delete state.rounds[key];
        break;
      case 'reconcile-round': {
        coordinator();
        exact(data, ['workerId', 'oldRoundId', 'cessationEvidenceDigest']);
        const old = state.rounds[roundKey('consumer', data.workerId)];
        workerFor(state, data.workerId);
        if (!old || old.roundId !== data.oldRoundId || !digestPattern.test(data.cessationEvidenceDigest ?? '')) fail('Exact old consumer round and proven cessation evidence required.');
        delete state.rounds[roundKey('consumer', data.workerId)];
        delete state.readiness[data.workerId];
        if (state.availability) state.availability[data.workerId] = { offerId: event.id, revoked: true };
        break;
      }
      case 'offer-capacity':
      case 'ready': {
        consumer();
        exact(data, ['inventoryDigest', 'assignmentInventoryDigest', 'unassignedSessions', 'capabilities',
          ...(event.type === 'offer-capacity' ? ['policySha', 'previousCapacityDigest', 'inventoryObservedAt'] : [])]);
        const worker = workerFor(state, event.workerId);
        if (!digestPattern.test(data.inventoryDigest ?? '') || data.unassignedSessions !== 0 ||
            data.assignmentInventoryDigest !== (event.type === 'offer-capacity'
              ? admissionInventoryDigest(state, event.workerId) : inventoryDigest(state, event.workerId)) ||
            !Array.isArray(data.capabilities) || data.capabilities.some((capability) => !worker.capabilities.includes(capability))) fail('Complete reconciled local inventory and verified capabilities required.');
        state.readiness[event.workerId] = {
          ...data, observedAt: event.type === 'offer-capacity' ? data.inventoryObservedAt : event.observedAt,
        };
        if (event.type === 'offer-capacity') {
          fresh(data.inventoryObservedAt, Date.parse(event.observedAt));
          if (!shaPattern.test(data.policySha ?? '')) fail('Approved policy binding required for capacity offer.');
          if (data.previousCapacityDigest !== digest(state.availability?.[event.workerId] ?? {})) fail('Capacity changed after observation; reconcile before replacing its offer.');
          const owned = Object.values(state.assignments).filter((entry) => entry.workerId === event.workerId && liveStates.has(entry.state));
          const limits = hostLimits(worker.host);
          state.availability ??= {};
          state.availability[event.workerId] = {
            offerId: event.id, roundId: event.roundId, policySha: data.policySha,
            registryDigest: digest(state.registry), capabilities: data.capabilities,
            remaining: Object.fromEntries(['mobile', 'general'].map((category) => [
              category, Math.max(0, limits[category] - owned.filter((entry) => entry.task.category === category).length),
            ])),
          };
        }
        break;
      }
      case 'unavailable': {
        consumer();
        exact(data, ['reasonCode', 'evidenceDigest']);
        if (!['inventory-unreconciled', 'capability-unavailable', 'owner-paused'].includes(data.reasonCode) ||
            !digestPattern.test(data.evidenceDigest ?? '')) fail('Known unavailability reason and evidence required.');
        state.availability ??= {};
        state.availability[event.workerId] = { offerId: event.id, revoked: true };
        delete state.readiness[event.workerId];
        break;
      }
      case 'reserve': {
        coordinator();
        exact(data, ['assignmentId', 'workerId', 'task', 'generation', 'eligibilityDigest', 'policySha', 'offerId', 'taskPacket']);
        identifier(data.assignmentId);
        if (data.generation !== 1 || state.assignments[data.assignmentId] ||
            !digestPattern.test(data.eligibilityDigest ?? '') || !shaPattern.test(data.policySha ?? '')) fail('New exact reservation required; never reuse assignment IDs.');
        validateTask(data.task);
        // Optional and versioned: historical packetless reservations replay unchanged.
        if (data.taskPacket !== undefined && digest(taskFromPacket(data.taskPacket)) !== digest(data.task)) {
          fail('task-packet-tampered: published task packet does not reproduce the reserved task digest.');
        }
        const worker = workerFor(state, data.workerId);
        const offer = state.availability?.[data.workerId];
        if (data.offerId !== undefined) {
          if (!offer || offer.revoked || offer.offerId !== data.offerId || offer.policySha !== data.policySha ||
              offer.registryDigest !== digest(state.registry)) fail('Current worker capacity offer and approved policy binding required.');
          if (offer.remaining[data.task.category] < 1) fail('Finite category offer exhausted; no borrowing or automatic credit refund.');
          if (data.task.capabilities.some((capability) => !offer.capabilities.includes(capability))) fail('Task exceeds offered capabilities.');
        } else {
          // Preserve the exact reducer for previously committed V1/V2 events.
          const ready = state.readiness[data.workerId];
          fresh(ready?.observedAt, Date.parse(event.observedAt));
          if (ready.assignmentInventoryDigest !== inventoryDigest(state, data.workerId)) fail('Consumer must reconcile all current assignments before more admission.');
          if (data.task.capabilities.some((capability) => !ready.capabilities.includes(capability))) fail('Consumer lacks freshly verified required tooling.');
        }
        const active = Object.values(state.assignments).filter((entry) => liveStates.has(entry.state));
        if (active.some((entry) =>
          (data.task.issue && entry.task.issue === data.task.issue) ||
          (data.task.pr && entry.task.pr === data.task.pr) ||
          entry.task.fileKeys.some((file) => data.task.fileKeys.includes(file)))) fail('Task/file overlap is already reserved.');
        const owned = active.filter((entry) => entry.workerId === data.workerId);
        const limits = hostLimits(worker.host);
        if (owned.length >= limits.total || owned.filter((entry) => entry.task.category === data.task.category).length >= limits[data.task.category]) fail('Hard category quota reached; no borrowing.');
        state.assignments[data.assignmentId] = {
          ...data, taskDigest: digest(data.task), state: 'reserved', receipts: [],
        };
        if (data.offerId !== undefined) offer.remaining[data.task.category]--;
        break;
      }
      case 'deliver':
      case 'publish': {
        coordinator();
        exact(data, ['assignmentId', 'generation', 'taskDigest']);
        const assignment = getAssignment();
        if (assignment.state !== 'reserved') fail('Only a durable reservation can be published.');
        if (event.type === 'publish') fresh(state.readiness[assignment.workerId]?.observedAt, Date.parse(event.observedAt));
        assignment.state = 'published';
        break;
      }
      case 'accept':
      case 'terminal-receipt':
      case 'receipt': {
        consumer();
        exact(data, ['assignmentId', 'generation', 'taskDigest', 'status', 'correlation', 'evidenceDigest',
          ...(event.type === 'accept' ? ['policySha'] : []), ...(event.type === 'terminal-receipt' ? ['artifact'] : [])]);
        const assignment = getAssignment();
        identifier(data.correlation);
        if (data.artifact !== undefined) validateTerminalArtifact(data.artifact, assignment.task);
        if (!digestPattern.test(data.evidenceDigest ?? '')) fail('Local evidence digest required.');
        if (event.type === 'accept') {
          const offer = state.availability?.[event.workerId];
          const ready = state.readiness[event.workerId];
          if (data.status !== 'starting' || !offer || offer.revoked || offer.roundId !== event.roundId ||
              offer.policySha !== data.policySha || assignment.policySha !== data.policySha ||
              offer.registryDigest !== digest(state.registry)) fail('Current-round local admission and matching policy required.');
          const inventory = localInventoryFreshness(state, event.workerId, Date.parse(event.observedAt));
          if (inventory.refreshRequired) {
            fail(`Local kickoff inventory expired or invalid (ready observedAt=${inventory.observedAt ?? 'missing'}, ageMs=${inventory.ageMs ?? 'unknown'}, maxAgeMs=${inventory.maxAgeMs}). Re-read native inventory and publish ready immediately before starting; changing receipt evidence.observedAt does not refresh ready.`);
          }
          if (ready.assignmentInventoryDigest !== admissionInventoryDigest(state, event.workerId) ||
              assignment.task.capabilities.some((capability) => !offer.capabilities.includes(capability))) fail('Fresh exact local inventory and required capabilities must admit kickoff.');
        }
        if (event.type === 'terminal-receipt' && data.status !== 'terminal-reported') fail('Durable terminal commitment required.');
        const transitions = {
          published: ['starting', 'uncertain'], starting: ['running', 'uncertain', 'terminal-reported'],
          running: ['running', 'review', 'recovery', 'uncertain', 'terminal-reported'],
          review: ['review', 'running', 'recovery', 'uncertain', 'terminal-reported'],
          recovery: ['recovery', 'running', 'review', 'uncertain', 'terminal-reported'],
          uncertain: ['uncertain', 'running', 'review', 'recovery', 'terminal-reported'],
          'terminal-reported': ['terminal-reported'],
        };
        if (!transitions[assignment.state]?.includes(data.status)) fail('Invalid receipt transition; no blind redispatch.');
        if (assignment.correlation && assignment.correlation !== data.correlation) fail('A replacement session requires a new reservation.');
        assignment.correlation = data.correlation;
        assignment.state = data.status;
        const { artifact, ...receipt } = data;
        assignment.receipts.push({ ...receipt, observedAt: event.observedAt });
        if (event.type === 'terminal-receipt') {
          assignment.terminalCommitment = data.evidenceDigest;
          if (artifact !== undefined) assignment.terminalArtifact = artifact;
          delete assignment.blocker;
        } else if (event.type === 'accept') delete assignment.blocker;
        break;
      }
      case 'report-blocker': {
        consumer();
        exact(data, ['assignmentId', 'generation', 'taskDigest', 'reasonCode', 'evidenceDigest', 'prestartProof']);
        const assignment = getAssignment();
        if (!liveStates.has(assignment.state) || !digestPattern.test(data.evidenceDigest ?? '') ||
            !['task-changed', 'held', 'capability-unavailable', 'native-evidence-missing', 'delivery-uncertain', 'scope-expanded', 'dependency-blocked'].includes(data.reasonCode)) fail('Known live assignment and nonsecret blocker code required.');
        if (data.prestartProof !== undefined) validatePrestartProof(data.prestartProof, assignment, event.workerId, data.evidenceDigest);
        assignment.blocker = { reasonCode: data.reasonCode, evidenceDigest: data.evidenceDigest, observedAt: event.observedAt,
          ...(data.prestartProof !== undefined ? { prestartProof: data.prestartProof } : {}) };
        if (state.availability) state.availability[event.workerId] = { offerId: event.id, revoked: true };
        break;
      }
      case 'withdraw': {
        coordinator();
        exact(data, ['assignmentId', 'generation', 'taskDigest', 'reconciliationDigest']);
        const assignment = getAssignment();
        if (!['reserved', 'published'].includes(assignment.state) || !digestPattern.test(data.reconciliationDigest ?? '')) fail('Only never-delivered work can be withdrawn; starting/uncertain work retains ownership.');
        assignment.state = 'terminal';
        assignment.disposition = 'withdrawn-before-delivery';
        assignment.terminalEvidenceDigest = data.reconciliationDigest;
        break;
      }
      case 'settle':
      case 'release': {
        coordinator();
        exact(data, ['assignmentId', 'generation', 'taskDigest', 'terminalEvidenceDigest', 'terminalReceiptDigest', 'terminalArtifactDigest']);
        const assignment = getAssignment();
        if (assignment.state !== 'terminal-reported' || !digestPattern.test(data.terminalEvidenceDigest ?? '')) fail('Correlated terminal report and independent native reconciliation required.');
        // Packet-bound settlement names the exact terminal receipt and artifact the
        // coordinator verified; a replacement receipt published meanwhile rejects it.
        if ((assignment.taskPacket || data.terminalReceiptDigest !== undefined || data.terminalArtifactDigest !== undefined) &&
            (data.terminalReceiptDigest !== assignment.terminalCommitment ||
              data.terminalArtifactDigest !== digest(assignment.terminalArtifact ?? null))) {
          fail('artifact-changed: the terminal receipt or artifact changed after coordinator verification; re-read and reconcile before settlement.');
        }
        if (event.type === 'settle') {
          if (assignment.blocker || assignment.terminalCommitment !== assignment.receipts.at(-1)?.evidenceDigest) fail('Unblocked durable terminal commitment required; old receipts need consumer reconciliation.');
        } else {
          fresh(state.readiness[assignment.workerId]?.observedAt, Date.parse(event.observedAt));
          if (state.readiness[assignment.workerId].assignmentInventoryDigest !== inventoryDigest(state, assignment.workerId)) fail('Consumer must reconcile latest terminal receipt before release.');
          fresh(assignment.receipts.at(-1)?.observedAt, Date.parse(event.observedAt));
        }
        assignment.state = 'terminal';
        assignment.terminalEvidenceDigest = data.terminalEvidenceDigest;
        break;
      }
      default: fail('Unknown transition.');
    }
  }
  state.sequence++;
  state.events[event.id] = eventDigest;
  return state;
}

export function taskFromEvidence(evidence) {
  if (evidence?.filesComplete !== true || !Array.isArray(evidence.files) || !evidence.files.length ||
      evidence.files.some((file) => typeof file !== 'string' || !file || file.startsWith('/') ||
        file.includes('\\') || file.split('/').some((part) => ['.', '..', ''].includes(part)))) fail('Complete normalized repository-relative file scope required.');
  if (typeof evidence.title !== 'string' || !Array.isArray(evidence.labels) ||
      evidence.labels.some((label) => typeof label !== 'string') ||
      !Array.isArray(evidence.acceptanceCriteria) || evidence.acceptanceCriteria.some((item) => typeof item !== 'string')) fail('Complete title/labels/acceptance facts required for task digest.');
  const labels = evidence.labels.map((label) => label.toLowerCase());
  if (labels.some((label) => heldLabels.has(label))) fail('Human hold or go:no forbids new research/implementation.');
  const purpose = evidence.purpose ?? (labels.includes('go:needs-research') ? 'research' : 'implementation');
  if (labels.includes('go:needs-research') && purpose !== 'research') fail('go:needs-research permits bounded research only, not implementation.');
  const task = {
    ...(evidence.issue ? { issue: evidence.issue } : {}),
    ...(evidence.pr ? { pr: evidence.pr } : {}),
    purpose, headSha: evidence.headSha, requirementsDigest: digest({
      title: evidence.title, labels: [...evidence.labels].sort(),
      acceptanceCriteria: evidence.acceptanceCriteria,
    }),
    fileKeys: [...new Set(evidence.files.map((file) => digest(file.toLowerCase())))].sort(),
    category: classifyWork(evidence), capabilities: evidence.capabilities,
  };
  validateTask(task);
  return task;
}

export function researchDisposition(evidence, assignment) {
  const result = { closeIssue: false, mutationAuthorized: false, removeLabels: [], addLabels: [] };
  if (!Array.isArray(evidence?.labels)) fail('Current labels required for research triage.');
  const labels = evidence.labels.map((label) => String(label).toLowerCase());
  if (labels.some((label) => heldLabels.has(label))) {
    return { ...result, action: 'blocked', reason: 'Respect go:no and explicit human holds.' };
  }
  if (evidence.issueState?.toLowerCase() !== 'open' || !Array.isArray(evidence.githubAssignees) ||
      evidence.githubAssignees.length) {
    return { ...result, action: 'blocked', reason: 'Fresh open, unassigned issue evidence required before research triage.' };
  }
  if (!labels.includes('go:needs-research')) return { ...result, action: 'normal-triage' };
  if (!assignment) return { ...result, action: 'reserve-research', reason: 'Bounded research needs normal owner/device selection and quota reservation.' };
  if (assignment.task?.purpose !== 'research' || assignment.task.issue !== evidence.issue) fail('Research result must belong to the exact research assignment.');
  if (assignment.disposition === 'withdrawn-before-delivery') return { ...result, action: 'reserve-research', reason: 'Withdrawn work did not complete research; use a fresh reconciled reservation.' };
  if (assignment.state !== 'terminal') return { ...result, action: 'follow-existing-research', reason: 'Retain existing research ownership; do not duplicate it.' };
  const findings = evidence.findings;
  if (!findings || typeof findings.summary !== 'string' || !findings.summary.trim() ||
      !Array.isArray(findings.acceptanceCriteria) || !findings.acceptanceCriteria.length ||
      findings.acceptanceCriteria.some((item) => typeof item !== 'string' || !item.trim()) ||
      !Array.isArray(findings.remainingResearchBlockers) || findings.remainingResearchBlockers.length ||
      findings.exitCriteriaMet !== true || findings.approvalRequired !== false ||
      findings.researchQuestionsAnswered !== true || findings.implementationPlanVerified !== true ||
      findings.issueEvidenceReadback !== true || findings.issueDescriptionUpdated !== true ||
      !new RegExp(`^https://github\\.com/OlyForge3D/PrintFarmer/issues/${evidence.issue}#issuecomment-[0-9]+$`).test(findings.issueCommentUrl ?? '') ||
      typeof findings.repositoryFilesChanged !== 'boolean' ||
      !/^squad:(dallas|ripley|drake|lambert|hudson|gorman|kane|ash|brett|parker|newt|copilot)$/.test(findings.implementationOwner ?? '') ||
      typeof findings.implementationReady !== 'boolean' || !Array.isArray(findings.implementationBlockers) ||
      findings.implementationBlockers.some((item) => typeof item !== 'string' || !item.trim()) ||
      (findings.implementationIssue !== undefined &&
        (!Number.isSafeInteger(findings.implementationIssue) || findings.implementationIssue < 1))) {
    return { ...result, action: 'retain-research-gate', reason: 'Durable issue findings, research exit criteria, owner or implementation plan are incomplete/awaiting approval.' };
  }
  if (findings.repositoryFilesChanged &&
      (!/^https:\/\/github\.com\/OlyForge3D\/PrintFarmer\/pull\/[1-9][0-9]*$/.test(findings.researchPrUrl ?? '') ||
        findings.researchPrMerged !== true || !shaPattern.test(findings.researchHeadSha ?? ''))) {
    return { ...result, action: 'retain-research-gate', reason: 'Research changed repository files: verify its reviewed, merged PR before readiness.' };
  }
  const sameIssue = findings.implementationIssue === undefined || findings.implementationIssue === evidence.issue;
  const ready = findings.implementationReady && findings.implementationBlockers.length === 0;
  const oldOwners = evidence.labels.filter((label) => label.toLowerCase().startsWith('squad:') &&
    label.toLowerCase() !== findings.implementationOwner);
  return {
    ...result, action: ready ? 'propose-implementation-readiness' : 'propose-research-complete', issue: evidence.issue,
    implementationIssue: findings.implementationIssue ?? evidence.issue,
    removeLabels: ['go:needs-research', ...(!ready && labels.includes('go:yes') ? ['go:yes'] : []),
      ...(sameIssue ? oldOwners : [])],
    addLabels: sameIssue ? [...(ready ? ['go:yes'] : []),
      ...(!labels.includes(findings.implementationOwner) ? [findings.implementationOwner] : [])] : [],
    reason: 'Coordinator must refresh and update the original issue, preserving its report. Research completion is not a bug fix; separately verify implementation prerequisites and any explicitly linked child.',
  };
}

export async function githubApi(endpoint, method = 'GET', body) {
  const args = ['api', '--hostname', 'github.com', '--method', method, endpoint];
  if (body) args.push('--input', '-');
  try {
    return await new Promise((resolve, reject) => {
      const child = execFile('gh', args, { encoding: 'utf8', maxBuffer: 8 * 1024 * 1024, timeout: 120_000 }, (error, stdout) => {
        if (error) reject(new Error('GitHub request failed; reconcile before retry. Raw output suppressed.'));
        else { try { resolve(JSON.parse(stdout)); } catch { reject(new Error('Invalid GitHub JSON response.')); } }
      });
      if (body) child.stdin.end(JSON.stringify(body));
      else child.stdin.end();
    });
  } catch { fail('GitHub request failed or was unverifiable; no success inferred.'); }
}

export async function verifyControlRepository(config, api = githubApi) {
  validateControl(config);
  const user = await api('user');
  if (!config.registry.writers.some((name) => name.toLowerCase() === user.login?.toLowerCase())) fail('Current GitHub principal is not an approved writer.');
  const repo = await api(`repos/${config.repository}`);
  if (repo.id !== config.repositoryId || repo.full_name !== config.repository || repo.private !== true ||
      repo.visibility !== 'private' || repo.archived !== false || repo.disabled !== false || repo.permissions?.push !== true) fail('Wrong/public/unavailable control repository or missing write permission.');
  const permission = await api(`repos/${config.repository}/collaborators/${user.login}/permission`);
  if (!['admin', 'maintain', 'write'].includes(permission.permission) || permission.user?.login?.toLowerCase() !== user.login.toLowerCase()) fail('Live approved-principal write permission could not be established.');
  return user.login;
}

async function readRecord(config, sha, api) {
  if (!shaPattern.test(sha ?? '')) fail('Invalid commit identity.');
  const base = `repos/${config.repository}/git`;
  const commit = await api(`${base}/commits/${sha}`);
  if (commit.sha !== sha || !shaPattern.test(commit.tree?.sha ?? '') || !Array.isArray(commit.parents)) fail('Malformed immutable commit.');
  const tree = await api(`${base}/trees/${commit.tree.sha}`);
  if (tree.sha !== commit.tree.sha || tree.truncated !== false || tree.tree?.length !== 1 ||
      tree.tree[0].path !== 'mailbox.json' || tree.tree[0].type !== 'blob' || tree.tree[0].mode !== '100644' ||
      !shaPattern.test(tree.tree[0].sha ?? '')) fail('Unexpected mailbox tree content.');
  const blob = await api(`${base}/blobs/${tree.tree[0].sha}`);
  if (blob.encoding !== 'base64' || blob.size > maxRecordBytes || typeof blob.content !== 'string') fail('Invalid mailbox blob.');
  const bytes = Buffer.from(blob.content.replace(/\n/g, ''), 'base64');
  const calculated = createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex');
  if (bytes.length !== blob.size || calculated !== tree.tree[0].sha || blob.sha !== calculated) fail('Mailbox content identity mismatch.');
  return { commit, record: JSON.parse(bytes.toString('utf8')) };
}

export async function readMailbox(config, api = githubApi, anchor) {
  await verifyControlRepository(config, api);
  if (!config.genesisSha) fail('Owner-approved genesis pin required; setup does not initialize a queue.');
  const ref = await api(`repos/${config.repository}/git/ref/${config.ref}`);
  if (ref.ref !== `refs/${config.ref}` || ref.object?.type !== 'commit') fail('Wrong mailbox reference.');
  const history = [];
  let current = ref.object.sha;
  let genesis;
  let state;
  if (anchor && (!shaPattern.test(anchor.head ?? '') ||
      digest(anchor.state?.registry) !== digest(config.registry))) fail('Invalid private history checkpoint.');
  for (let count = 0; count < 2000; count++) {
    if (anchor && current === anchor.head) {
      state = anchor.state;
      break;
    }
    const { commit, record } = await readRecord(config, current, api);
    if (current === config.genesisSha) {
      exact(record, ['version', 'registry', 'migrationEvidenceDigest']);
      if (record.version !== 1 || digest(record.registry) !== digest(config.registry) ||
          !digestPattern.test(record.migrationEvidenceDigest ?? '') || commit.parents.length !== 0) fail('Genesis/registry/migration pin mismatch.');
      genesis = record;
      break;
    }
    if (commit.parents.length !== 1) fail('Mailbox history must be a single-parent chain to pinned genesis.');
    exact(record, ['previousStateDigest', 'event']);
    history.push(record);
    current = commit.parents[0].sha;
  }
  if (!state && !genesis) fail('Incomplete history; reconcile/plan explicit checkpoint migration, never truncate.');
  if (anchor && !state) fail('Mailbox is not a descendant of the private checkpoint.');
  state ??= initialState(genesis.registry);
  for (const entry of history.reverse()) {
    if (entry.previousStateDigest !== digest(state)) fail('Modified or discontinuous mailbox history.');
    state = applyEvent(state, entry.event, { replay: true });
  }
  return { head: ref.object.sha, state };
}

function serializeRecord(record) {
  const content = JSON.stringify(record);
  if (Buffer.byteLength(content) > maxRecordBytes) fail('Mailbox record exceeds 1 MiB; reduce task scope before publication, never truncate.');
  return content;
}

async function createRecord(config, record, parents, api) {
  const base = `repos/${config.repository}/git`;
  const content = serializeRecord(record);
  const tree = await api(`${base}/trees`, 'POST', { tree: [{ path: 'mailbox.json', mode: '100644', type: 'blob', content }] });
  if (!shaPattern.test(tree.sha ?? '')) fail('GitHub did not return an immutable tree.');
  const commit = await api(`${base}/commits`, 'POST', { message: 'Ralph mailbox transition', tree: tree.sha, parents });
  if (!shaPattern.test(commit.sha ?? '') || JSON.stringify(commit.parents?.map((parent) => parent.sha)) !== JSON.stringify(parents)) fail('GitHub did not return the exact-parent commit.');
  return commit.sha;
}

export async function initializeMailbox(config, migrationEvidenceDigest, api = githubApi) {
  await verifyControlRepository(config, api);
  if (config.genesisSha || !digestPattern.test(migrationEvidenceDigest ?? '')) fail('Explicit new authority with proven migration digest required.');
  const record = { version: 1, registry: config.registry, migrationEvidenceDigest };
  const content = serializeRecord(record);
  const branches = await api(`repos/${config.repository}/branches?per_page=1`);
  if (!Array.isArray(branches)) fail('Cannot establish whether control repository is initialized.');
  if (branches.length === 0) {
    const repository = await api(`repos/${config.repository}`);
    if (config.ref !== `heads/${repository.default_branch}`) fail('Empty control repository must initialize its actual default branch; choose that explicit ref, never guess.');
    // No sha means create-only. A lost response must be inspected, never retried as an update.
    const created = await api(`repos/${config.repository}/contents/mailbox.json`, 'PUT', {
      message: 'Initialize Ralph private control ledger',
      branch: repository.default_branch, content: Buffer.from(content).toString('base64'),
    });
    const sha = created.commit?.sha;
    const checked = await readMailbox({ ...config, genesisSha: sha }, api);
    if (checked.head !== sha || checked.state.sequence !== 0) fail('Initialization raced or is uncertain; inspect before any further action.');
    return { genesisSha: sha, activationAuthorized: false };
  }
  const sha = await createRecord(config, record, [], api);
  // Create, never update: an existing ref must not be replaced, even after a lost response.
  await api(`repos/${config.repository}/git/refs`, 'POST', { ref: `refs/${config.ref}`, sha });
  const ref = await api(`repos/${config.repository}/git/ref/${config.ref}`);
  if (ref.object?.sha !== sha) fail('Genesis publication uncertain; do not retry initialization.');
  return { genesisSha: sha, activationAuthorized: false };
}

export async function publishEvent(config, event, api = githubApi, anchor, beforePublish) {
  const snapshot = await readMailbox(config, api, anchor);
  const state = applyEvent(snapshot.state, event);
  if (state === snapshot.state) return { ...snapshot, replayed: true };
  const sha = await createRecord(config, { previousStateDigest: digest(snapshot.state), event }, [snapshot.head], api);
  await verifyControlRepository(config, api);
  if (beforePublish) await beforePublish({ base: snapshot, candidateSha: sha });
  let uncertain = false;
  try {
    await api(`repos/${config.repository}/git/refs/${config.ref}`, 'PATCH', { sha, force: false });
  } catch { uncertain = true; }
  const actual = await readMailbox(config, api, snapshot);
  if (actual.state.events[event.id] !== digest(event)) {
    fail(uncertain ? 'Publication conflicted or acknowledgement lost; same event must be reconciled, never blindly redelivered.' : 'Publication did not persist.');
  }
  return { ...actual, replayed: false, acknowledgementRecovered: uncertain };
}
