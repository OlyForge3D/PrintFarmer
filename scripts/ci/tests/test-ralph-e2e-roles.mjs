// End-to-end Ralph role test (#2958). The coordinator, the Mac consumer and the
// Windows consumer are separate runtime invocations: each loads its own host
// configuration from disk and owns a separate private journal. They share only
// the simulated private control repository and public GitHub fixtures. No test
// step copies a value produced by one role into another role's request.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  digest, publishEvent, readMailbox, taskFromEvidence, taskFromPacket, taskPacketFromEvidence, validateTaskPacket,
} from '../ralph-mailbox.mjs';
import { composeTaskEvidence, runNativeRequest, verifyDevelopmentAdvance, verifyTerminalArtifact } from '../ralph-native-runtime.mjs';
import {
  githubFixture, registerIssueComment, registerPolicyAtHead, registerPullRequest, registerTaskSubject, setCompare, setVerdictStatus,
} from './fixtures/ralph-github-fixture.mjs';

const now = Date.now();
const sha256 = (value) => createHash('sha256').update(value, 'utf8').digest('hex');
const registry = {
  version: 1, authorityId: 'primary', epoch: 1, writers: ['fixture-owner'],
  workers: [
    { workerId: 'mini', host: 'macos-mobile', capabilities: ['general', 'ios'] },
    { workerId: 'windows', host: 'windows-general', capabilities: ['general'] },
  ],
};
const nativeCapabilities = {
  createSession: true, openPrSession: true, agents: ['Ralph Worker'],
  models: { 'gpt-6-astra': ['medium', 'xhigh', 'max'], 'claude-opus-4.7': ['medium', 'xhigh'] },
};
// Facts only the coordinator authors. A consumer request must never carry them.
const coordinatorOnlyKeys = ['title', 'labels', 'acceptanceCriteria', 'files', 'filesComplete', 'scope',
  'classificationComplete', 'requirementsDigest', 'fileKeys', 'headSha', 'issueState', 'githubAssignees', 'taskPacket'];
let sessionCounter = 0;
const fresh = (extra = {}) => ({ observedAt: new Date(now).toISOString(), source: 'fixture: role-local native/GitHub readback', ...extra });

async function farm(t) {
  const root = await mkdtemp(path.join(await realpath(os.tmpdir()), 'ralph e2e roles '));
  t.after(() => rm(root, { recursive: true, force: true }));
  const github = githubFixture();
  const log = [];
  let sequence = 0;
  const roles = {};
  const define = (name, { role, workerId, host, capabilities, index }) => {
    const directory = path.join(root, name);
    roles[name] = {
      name, role, workerId, capabilities, directory,
      hostConfigPath: path.join(directory, 'host.json'),
      localContext: { worktreePath: `/role-${name}/checkout`, gitDirectory: `/role-${name}/git` },
      config: {
        control: { repository: 'fixture/private-control', repositoryId: 123, ref: 'heads/main', registry, sharedWriterTrustAccepted: true },
        role, workerId, host,
        workflowId: `aaaaaaaa-1111-4222-8333-44444444444${index}`,
        projectId: `bbbbbbbb-1111-4222-8333-44444444444${index}`,
        worktreeRoot: `/worktrees-${name}`, verified: true, migrationAttested: true,
        executionTrust: 'local-owner-v1', approvedPolicy: 'a'.repeat(40),
        stateDirectory: path.join(directory, 'native-state'),
      },
      sessions: [],
    };
  };
  define('coordinator', { role: 'coordinator', workerId: 'mini', host: 'macos-mobile', capabilities: ['general', 'ios'], index: 1 });
  define('mini', { role: 'consumer', workerId: 'mini', host: 'macos-mobile', capabilities: ['general', 'ios'], index: 2 });
  define('windows', { role: 'consumer', workerId: 'windows', host: 'windows-general', capabilities: ['general'], index: 3 });
  const writeHost = async (entry) => {
    await mkdir(entry.directory, { recursive: true, mode: 0o700 });
    await writeFile(entry.hostConfigPath, `${JSON.stringify(entry.config, null, 2)}\n`, { mode: 0o600 });
  };
  for (const entry of Object.values(roles)) await writeHost(entry);

  // One runtime invocation: load this role's host config from its own file,
  // exactly as `ralph-native-runtime.mjs --host-config` does, and nothing else.
  const invoke = async (name, request, { at = now } = {}) => {
    const entry = roles[name];
    const config = JSON.parse(await readFile(entry.hostConfigPath, 'utf8'));
    const full = { approvedPolicy: config.approvedPolicy, id: `${name}-${++sequence}`, ...request, hostConfigPath: entry.hostConfigPath };
    if (entry.role === 'consumer' && full.evidence && full.type !== 'ready') {
      for (const key of coordinatorOnlyKeys) {
        assert.equal(Object.hasOwn(full.evidence, key), false, `${name} ${full.type} must not supply coordinator-authored ${key}`);
      }
    }
    log.push({ role: name, request: full });
    return runNativeRequest(config, full, {
      api: github.api, now: at, preflight: async () => ({ localContext: entry.localContext }),
    });
  };
  const begin = async (name) => {
    const roundId = `${name}-round-${++sequence}`;
    const { roundToken } = await invoke(name, { type: 'begin-round', roundId });
    roles[name].round = { roundId, roundToken };
  };
  const act = (name, type, extra = {}, options) => invoke(name, { ...roles[name].round, type, ...extra }, options);
  const end = (name) => act(name, 'end-round');
  const inventory = (name) => fresh({
    ownershipScope: 'ralph-owned-v1', lineageChecked: true, nativeCapabilities,
    complete: true, queueChecked: true, historyChecked: true, capabilitiesVerified: true,
    capabilities: roles[name].capabilities, sessions: roles[name].sessions,
  });
  const ready = (name) => act(name, 'ready', { evidence: inventory(name) });
  const inspect = (name) => invoke(name, { type: 'inspect' });

  const initialized = await invoke('coordinator', {
    type: 'initialize', roundId: 'initialize', explicitInitializationApproval: true,
    evidence: fresh({ legacyAuthoritiesReconciled: true, cessationOrFencedHandoffProven: true }),
  });
  // Owner setup pins the genesis in every role's host config; this is setup, not a task relay.
  for (const entry of Object.values(roles)) {
    entry.config.control.genesisSha = initialized.genesisSha;
    await writeHost(entry);
  }
  return { root, github, roles, log, invoke, begin, act, end, ready, inspect, inventory };
}

// Coordinator-authored triage and classification. Only the coordinator holds it.
function coordinatorEvidence(issue, { research = false, owner = research ? 'squad:dallas' : 'squad:copilot', title } = {}) {
  return fresh({
    repository: 'OlyForge3D/PrintFarmer', issue, headSha: 'a'.repeat(40),
    title: title ?? `Task ${issue}`, issueState: 'open', githubAssignees: [],
    labels: [owner, ...(research ? ['go:needs-research'] : []), 'type:bug', 'priority:p1'],
    acceptanceCriteria: [`Coordinator-authored acceptance criterion for ${issue}`, 'Keep packet facts exact'],
    files: [`src/area-${issue}/Handler.cs`, `docs/area-${issue}.md`],
    filesComplete: true, scope: 'general', classificationComplete: true, capabilities: ['general'],
    nativeCapabilities, claimsReconciled: true, holdsChecked: true, dependenciesReady: true,
    epicChildrenReady: true, analysisReady: true, reviewGatesChecked: true, ownershipReconciled: true,
  });
}

// What a consumer can observe locally: nothing about the task beyond the binding.
const consumerStartEvidence = () => fresh({ holdsChecked: true, ownershipReconciled: true, nativeCapabilities });

async function reserveAndPublish(f, issue, workerId, options = {}) {
  const evidence = coordinatorEvidence(issue, options);
  const subject = registerTaskSubject(f.github, evidence, { body: options.body ?? `Public body for issue ${issue}.\n` });
  const assignmentId = options.assignmentId ?? `assignment-${issue}`;
  const reserved = await f.act('coordinator', 'reserve', { data: { assignmentId, workerId }, evidence });
  const entry = reserved.state.assignments[assignmentId];
  const binding = { assignmentId, generation: entry.generation, taskDigest: entry.taskDigest };
  await f.act('coordinator', 'publish', { data: binding, evidence });
  return { evidence, subject, assignmentId };
}

// The consumer discovers its work only from the mailbox it reads itself.
async function discover(f, name, issue) {
  const snapshot = await f.inspect(name);
  const found = Object.values(snapshot.state.assignments).filter((entry) =>
    entry.workerId === f.roles[name].workerId && entry.state === 'published' && entry.task.issue === issue);
  assert.equal(found.length, 1, `${name} sees exactly one published assignment for #${issue}`);
  const [assignment] = found;
  assert.ok(assignment.taskPacket, 'published assignment carries its task packet');
  return { assignment, binding: { assignmentId: assignment.assignmentId, generation: assignment.generation, taskDigest: assignment.taskDigest } };
}

async function startWorker(f, name, binding, { correlation, initialHeadSha } = {}) {
  const data = { ...binding, correlation };
  const preview = await f.act(name, 'dispatch-plan', { data, evidence: consumerStartEvidence() });
  assert.equal(preview.nativeCreateAllowed, false);
  await f.ready(name);
  const start = await f.act(name, 'receipt', { data: { ...data, status: 'starting' }, evidence: consumerStartEvidence() });
  assert.equal(start.nativeCreateAllowed, true);
  const plan = start.dispatchPlan;
  assert.deepEqual(plan, preview.dispatchPlan);
  const config = f.roles[name].config;
  const session = {
    id: `cccccccc-2222-4333-8444-${String(++sessionCounter).padStart(12, '0')}`,
    projectId: config.projectId, worktreePath: `${config.worktreeRoot}/${correlation}`, branch: `worker-${correlation}`,
  };
  const readback = fresh({ session, repository: 'OlyForge3D/PrintFarmer', nativeReadbackVerified: true, dispatchPlanDigest: plan.planDigest });
  await f.act(name, 'record-creation', { data, evidence: { ...readback,
    creationHandle: session.id, creationOutcome: 'succeeded', createRequestDigest: digest(plan.nativeArguments), kickoffAccepted: true } });
  const startupAck = (head) => ({ ...readback,
    configuration: { source: 'successful-native-create', model: plan.packet.model, reasoningEffort: plan.packet.reasoningEffort },
    startupAck: { ...plan.packet, substantiveWorkStarted: false, noChildren: true, actualModel: plan.packet.model,
      initialHeadSha: head, actualBranch: session.branch } });
  return { data, plan, session, startupAck, head: initialHeadSha ?? plan.packet.headSha };
}

async function runWorker(f, name, worker) {
  const allowed = await f.act(name, 'startup-check', { data: worker.data, evidence: worker.startupAck(worker.head) });
  assert.equal(allowed.continuationAllowed, true);
  const delivered = fresh({ session: worker.session, assignmentCorrelation: worker.data.correlation,
    repository: 'OlyForge3D/PrintFarmer', nativeReadbackVerified: true, kickoffDeliveryVerified: true,
    continuationAck: { ...worker.plan.packet, substantiveWorkStarted: true } });
  await f.act(name, 'receipt', { data: { ...worker.data, status: 'running' }, evidence: delivered });
  return delivered;
}

async function reportTerminal(f, name, worker, delivered, artifactUrl, { replacement = false } = {}) {
  const readback = await f.act(name, 'artifact-readback', { data: { ...worker.data, artifactUrl }, evidence: fresh() });
  const { artifact, finalDeliveryCorrelation } = readback;
  const echo = artifact.kind === 'pull-request' ? { artifactHeadSha: artifact.headSha } : { artifactBodyDigest: artifact.bodyDigest };
  const terminal = await f.act(name, 'receipt', { data: { ...worker.data, status: 'terminal-reported' }, evidence: {
    ...delivered, session: { ...worker.session, terminalVerified: true },
    queueChecked: true, historyChecked: true, artifactsVerified: true, artifactReadbackVerified: true, artifact,
    noPendingContinuation: true, noFutureDelivery: true, finalDeliveryCorrelation,
    finalAck: { ...worker.plan.packet, noChildren: true, noPendingContinuation: true, noFutureDelivery: true,
      finalDeliveryCorrelation, artifactUrl: artifact.url, artifactReadbackVerified: true, ...echo },
  } });
  if (replacement) return { terminal, artifact };
  f.roles[name].sessions.push({ id: worker.session.id, terminalVerified: true });
  await f.ready(name);
  return { terminal, artifact };
}

// The coordinator settles from what it reads in the mailbox and on GitHub.
async function settlementRequest(f, assignmentId) {
  const snapshot = await f.inspect('coordinator');
  const entry = snapshot.state.assignments[assignmentId];
  assert.equal(entry.state, 'terminal-reported');
  assert.ok(entry.terminalArtifact, 'consumer published its terminal artifact');
  return {
    data: { assignmentId, generation: entry.generation, taskDigest: entry.taskDigest },
    evidence: fresh({ consumerReceiptDigest: entry.receipts.at(-1).evidenceDigest, taskDigest: entry.taskDigest,
      ownershipReconciled: true, artifactsVerified: true, noPendingContinuation: true }),
  };
}

function assertNoRelay(f) {
  const consumerProofs = [];
  for (const { role, request } of f.log) {
    const text = JSON.stringify(request);
    if (role === 'coordinator') {
      assert.equal(text.includes('consumerJournalDigest'), false, 'coordinator never receives a consumer proof out of band');
      assert.equal(text.includes('native-runtime-prestart-proof-v1'), false, 'coordinator never supplies prestart-proof');
      for (const name of ['mini', 'windows']) {
        if (f.roles[name].round) assert.equal(text.includes(f.roles[name].round.roundToken), false);
      }
    } else {
      assert.equal(text.includes('Coordinator-authored acceptance criterion'), false, `${role} never resubmits coordinator text`);
      if (request.type === 'report-blocker') consumerProofs.push(request.evidence);
    }
  }
  return consumerProofs;
}

test('coordinator, Mac and Windows consumers settle research, implementation and stale-withdrawal work without owner relay', async (t) => {
  const f = await farm(t);
  await f.begin('coordinator');
  await f.begin('mini');
  await f.begin('windows');
  await f.ready('mini');
  await f.ready('windows');

  // 1. Research on the Mac consumer: findings are a read-back issue comment.
  const research = await reserveAndPublish(f, 401, 'mini', { research: true });
  const { assignment: researchAssignment, binding: researchBinding } = await discover(f, 'mini', 401);
  assert.equal(researchAssignment.task.purpose, 'research');
  assert.deepEqual(researchAssignment.taskPacket.acceptanceCriteria, research.evidence.acceptanceCriteria);
  const researchWorker = await startWorker(f, 'mini', researchBinding, { correlation: 'research-401' });
  assert.equal(researchWorker.plan.packet.model, 'gpt-6-astra');
  assert.equal(researchWorker.plan.packet.reasoningEffort, 'xhigh');
  const researchDelivered = await runWorker(f, 'mini', researchWorker);
  // Research legitimately updates the issue description after start.
  research.subject.body = 'Updated description with research findings.\n';
  const { url: findingsUrl } = registerIssueComment(f.github, 401, 5001, 'Root cause found; implementation plan attached.\n');
  await reportTerminal(f, 'mini', researchWorker, researchDelivered, findingsUrl);
  const researchSettlement = await settlementRequest(f, research.assignmentId);
  f.github.comments.get('repos/OlyForge3D/PrintFarmer/issues/comments/5001').body = 'Edited after the terminal receipt.\n';
  await assert.rejects(f.act('coordinator', 'release', researchSettlement), /artifact-changed/);
  f.github.comments.get('repos/OlyForge3D/PrintFarmer/issues/comments/5001').body = 'Root cause found; implementation plan attached.\n';
  const researchSettled = await f.act('coordinator', 'release', researchSettlement);
  assert.equal(researchSettled.state.assignments[research.assignmentId].state, 'terminal');
  const findings = (issueCommentUrl) => fresh({ ...research.evidence, findings: {
    summary: 'Root cause confirmed; implementation is outstanding.', acceptanceCriteria: ['Fix the handler'],
    remainingResearchBlockers: [], researchQuestionsAnswered: true, exitCriteriaMet: true, approvalRequired: false,
    implementationPlanVerified: true, issueCommentUrl, issueEvidenceReadback: true, issueDescriptionUpdated: true,
    repositoryFilesChanged: false, implementationOwner: 'squad:lambert', implementationReady: true, implementationBlockers: [],
  } });
  const unpublished = await f.act('coordinator', 'research-plan', {
    evidence: findings('https://github.com/OlyForge3D/PrintFarmer/issues/401#issuecomment-9999') });
  assert.equal(unpublished.action, 'retain-research-gate');
  assert.equal((await f.act('coordinator', 'research-plan', { evidence: findings(findingsUrl) })).action, 'propose-implementation-readiness');

  // 2. Implementation on the Mac consumer: PR opened, reviewed, merged, settled.
  const implementation = await reserveAndPublish(f, 402, 'mini');
  const { binding: implementationBinding } = await discover(f, 'mini', 402);
  const implementationWorker = await startWorker(f, 'mini', implementationBinding, { correlation: 'implementation-402' });
  const implementationDelivered = await runWorker(f, 'mini', implementationWorker);
  const prHead = 'b'.repeat(40);
  const pr = registerPullRequest(f.github, { number: 7402, issue: 402, headSha: prHead });
  const crossRepo = registerPullRequest(f.github, { number: 7403, issue: 402, headSha: prHead, headRepository: 'someone/fork' });
  await assert.rejects(f.act('mini', 'artifact-readback', { data: { ...implementationWorker.data, artifactUrl: crossRepo.html_url },
    evidence: fresh() }), /cross-repository/);
  await reportTerminal(f, 'mini', implementationWorker, implementationDelivered, pr.html_url);
  const implementationSettlement = await settlementRequest(f, implementation.assignmentId);
  await assert.rejects(f.act('coordinator', 'release', implementationSettlement), /artifact-pending/);
  Object.assign(pr, { state: 'closed', merged: true });
  await assert.rejects(f.act('coordinator', 'release', implementationSettlement), /artifact-unreviewed/);
  setVerdictStatus(f.github, prHead, `NOT_APPLICABLE @ ${prHead.slice(0, 12)}: not a squad PR (no 'squad' label)`);
  await assert.rejects(f.act('coordinator', 'release', implementationSettlement), /artifact-unreviewed/);
  setVerdictStatus(f.github, prHead, `REVIEWED (self-attested) @ ${'c'.repeat(12)} by bishop, hicks`);
  await assert.rejects(f.act('coordinator', 'release', implementationSettlement), /artifact-unreviewed/);
  setVerdictStatus(f.github, prHead);
  pr.body = 'Implements the assigned task.\n';
  await assert.rejects(f.act('coordinator', 'release', implementationSettlement), /artifact-unlinked/);
  pr.body = 'Implements the assigned task.\n\nCloses #402\n';
  const implementationSettled = await f.act('coordinator', 'release', implementationSettlement);
  assert.equal(implementationSettled.state.assignments[implementation.assignmentId].state, 'terminal');

  // 3. Stale reservation: the issue changes after reservation. The consumer blocks
  // with its runtime prestart-proof; the coordinator withdraws from the mailbox alone.
  const stale = await reserveAndPublish(f, 403, 'mini');
  const { binding: staleBinding } = await discover(f, 'mini', 403);
  stale.subject.body = 'The owner rewrote the requirements after reservation.\n';
  await assert.rejects(f.act('mini', 'dispatch-plan', { data: { ...staleBinding, correlation: 'stale-403' },
    evidence: consumerStartEvidence() }), /task-changed: issue body changed/);
  await assert.rejects(f.act('mini', 'receipt', { data: { ...staleBinding, correlation: 'stale-403', status: 'starting' },
    evidence: consumerStartEvidence() }), /task-changed/);
  const proofRequest = { data: staleBinding, evidence: fresh({ authoritativeJournalRetained: true, protocolOnlyDeliveryAttested: true }) };
  const { proof } = await f.act('mini', 'prestart-proof', proofRequest);
  const withdrawal = { evidence: fresh({ claimsReconciled: true, noNativeDeliveryVerified: true }) };
  const staleEntry = async () => (await f.inspect('coordinator')).state.assignments[stale.assignmentId];
  const staleData = async () => {
    const entry = await staleEntry();
    return { assignmentId: stale.assignmentId, generation: entry.generation, taskDigest: entry.taskDigest };
  };
  await assert.rejects(f.act('coordinator', 'withdraw', { ...withdrawal, data: await staleData() }), /committed by its blocker/);
  await f.act('mini', 'report-blocker', { data: { ...staleBinding, reasonCode: 'task-changed' }, evidence: proof });
  assert.equal((await staleEntry()).blocker.prestartProof.source, 'native-runtime-prestart-proof-v1');
  const withdrawn = await f.act('coordinator', 'withdraw', { ...withdrawal, data: await staleData() });
  assert.equal(withdrawn.state.assignments[stale.assignmentId].disposition, 'withdrawn-before-delivery');
  await f.ready('mini');
  // Re-reservation from a fresh readback publishes a packet bound to the new body.
  const rereserved = await reserveAndPublish(f, 403, 'mini', { assignmentId: 'assignment-403-r2', body: stale.subject.body });
  const { assignment: renewed, binding: renewedBinding } = await discover(f, 'mini', 403);
  assert.equal(renewed.assignmentId, rereserved.assignmentId);
  assert.equal(renewed.taskPacket.sourceBodySha256, sha256(stale.subject.body));
  const renewedPlan = await f.act('mini', 'dispatch-plan', { data: { ...renewedBinding, correlation: 'renewed-403' }, evidence: consumerStartEvidence() });
  assert.equal(renewedPlan.nativeCreateAllowed, false);
  assert.equal(renewedPlan.dispatchPlan.packet.issue, 403);

  // 4. Windows general assignment. The worker checkout starts from a development
  // HEAD that advanced after reservation; the runtime proves it on GitHub.
  const windows = await reserveAndPublish(f, 404, 'windows');
  const { assignment: windowsAssignment, binding: windowsBinding } = await discover(f, 'windows', 404);
  assert.equal(windowsAssignment.task.category, 'general');
  const advanced = 'd'.repeat(40);
  const windowsWorker = await startWorker(f, 'windows', windowsBinding, { correlation: 'windows-404', initialHeadSha: advanced });
  assert.equal(windowsWorker.plan.packet.model, 'claude-opus-4.7');
  assert.equal(windowsWorker.plan.packet.sourceRef, 'development');
  setCompare(f.github, 'a'.repeat(40), advanced, 'diverged');
  setCompare(f.github, advanced, 'development', 'identical');
  await assert.rejects(f.act('windows', 'startup-check', { data: windowsWorker.data, evidence: windowsWorker.startupAck(advanced) }),
    /not a GitHub-verified descendant/);
  setCompare(f.github, 'a'.repeat(40), advanced, 'ahead');
  const windowsDelivered = await runWorker(f, 'windows', windowsWorker);
  const windowsHead = 'e'.repeat(40);
  const windowsPr = registerPullRequest(f.github, { number: 7404, issue: 404, headSha: windowsHead });
  await reportTerminal(f, 'windows', windowsWorker, windowsDelivered, windowsPr.html_url);
  const windowsSettlement = await settlementRequest(f, windows.assignmentId);
  Object.assign(windowsPr, { state: 'closed', merged: true, head: { ...windowsPr.head, sha: 'f'.repeat(40) } });
  setVerdictStatus(f.github, 'f'.repeat(40));
  await assert.rejects(f.act('coordinator', 'release', windowsSettlement), /artifact-changed/);
  windowsPr.head.sha = windowsHead;
  setVerdictStatus(f.github, windowsHead, `APPROVE (owner) @ ${windowsHead.slice(0, 12)} by jpapiez; dissent=0; short=0`);
  const windowsSettled = await f.act('coordinator', 'release', windowsSettlement);
  assert.equal(windowsSettled.state.assignments[windows.assignmentId].state, 'terminal');

  for (const name of ['mini', 'windows', 'coordinator']) await f.end(name);

  // The shared mailbox is the only channel, and it carries no private or native data.
  const mailbox = await readMailbox(f.roles.coordinator.config.control, f.github.api);
  const text = JSON.stringify(mailbox);
  for (const forbidden of [f.root, '/worktrees-', '/role-', researchWorker.session.id, windowsWorker.session.id]) {
    assert.equal(text.includes(forbidden), false, `mailbox must not contain ${forbidden}`);
  }
  for (const entry of Object.values(mailbox.state.assignments)) assert.ok(entry.taskPacket, `${entry.assignmentId} is packet-bound`);
  // Every role kept its own private journal.
  const journals = await Promise.all(['coordinator', 'mini', 'windows'].map((name) =>
    readFile(path.join(f.roles[name].config.stateDirectory, 'journal.json'), 'utf8').then(JSON.parse)));
  assert.deepEqual(journals.map((journal) => Object.keys(journal.sessions).sort()),
    [[], ['implementation-402', 'research-401'], ['windows-404']]);
  const proofs = assertNoRelay(f);
  assert.deepEqual(proofs, [proof], 'the only prestart proof travelled consumer -> mailbox');
});

test('packet negatives fail closed: legacy packetless, tampered, mismatched, held, edited and misread subjects', async (t) => {
  const f = await farm(t);
  await f.begin('coordinator');
  await f.begin('mini');
  await f.ready('mini');

  // Coordinator reservation must describe the live GitHub subject.
  const drifted = coordinatorEvidence(501);
  registerTaskSubject(f.github, { ...drifted, title: 'Retitled on GitHub' });
  await assert.rejects(f.act('coordinator', 'reserve', { data: { assignmentId: 'drifted', workerId: 'mini' }, evidence: drifted }),
    /task-readback-mismatch: reserve evidence title/);
  await assert.rejects(f.act('coordinator', 'reserve', { data: { assignmentId: 'local-path', workerId: 'mini' },
    evidence: { ...coordinatorEvidence(501, { title: 'Retitled on GitHub' }), files: ['/Users/owner/private/file.cs'] } }), /Invalid task packet|repository-relative file scope/);

  // A packetless (pre-#2958) reservation keeps blocking, and self-heals through the
  // published prestart-proof rather than an owner relay.
  const legacyEvidence = coordinatorEvidence(502);
  registerTaskSubject(f.github, legacyEvidence);
  const snapshot = await f.inspect('coordinator');
  await publishEvent(f.roles.coordinator.config.control, {
    id: 'legacy-reserve', type: 'reserve', authorityId: 'primary', epoch: 1, role: 'coordinator',
    roundId: f.roles.coordinator.round.roundId, observedAt: new Date().toISOString(),
    data: { assignmentId: 'legacy-502', workerId: 'mini', generation: 1, task: taskFromEvidence(legacyEvidence),
      eligibilityDigest: digest('legacy'), policySha: 'a'.repeat(40), offerId: snapshot.state.availability.mini.offerId },
  }, f.github.api);
  const legacy = (await f.inspect('coordinator')).state.assignments['legacy-502'];
  const legacyBinding = { assignmentId: 'legacy-502', generation: legacy.generation, taskDigest: legacy.taskDigest };
  await assert.rejects(f.act('coordinator', 'publish', { data: legacyBinding, evidence: legacyEvidence }), /native-evidence-missing/);
  await assert.rejects(f.act('mini', 'dispatch-plan', { data: { ...legacyBinding, correlation: 'legacy' },
    evidence: consumerStartEvidence() }), /native-evidence-missing/);
  const { proof } = await f.act('mini', 'prestart-proof', { data: legacyBinding,
    evidence: fresh({ authoritativeJournalRetained: true, protocolOnlyDeliveryAttested: true }) });
  await f.act('mini', 'report-blocker', { data: { ...legacyBinding, reasonCode: 'native-evidence-missing' }, evidence: proof });
  const withdrawn = await f.act('coordinator', 'withdraw', { data: legacyBinding,
    evidence: fresh({ claimsReconciled: true, noNativeDeliveryVerified: true }) });
  assert.equal(withdrawn.state.assignments['legacy-502'].disposition, 'withdrawn-before-delivery');
  await f.ready('mini');

  // A forged packet that does not reproduce the reserved task digest is rejected by
  // the reducer, and a rewritten control-repository record cannot be replayed.
  const forgedEvidence = coordinatorEvidence(503);
  const packet = taskPacketFromEvidence(forgedEvidence, sha256('body'));
  const offerId = (await f.inspect('coordinator')).state.availability.mini.offerId;
  const forged = {
    id: 'forged-reserve', type: 'reserve', authorityId: 'primary', epoch: 1, role: 'coordinator',
    roundId: f.roles.coordinator.round.roundId, observedAt: new Date().toISOString(),
    data: { assignmentId: 'forged-503', workerId: 'mini', generation: 1, task: taskFromEvidence(forgedEvidence),
      eligibilityDigest: digest('forged'), policySha: 'a'.repeat(40), offerId,
      taskPacket: { ...packet, acceptanceCriteria: ['Do something the coordinator never approved'] } },
  };
  await assert.rejects(publishEvent(f.roles.coordinator.config.control, forged, f.github.api), /task-packet-tampered/);
  const head = f.github.refs.get('heads/main');
  const current = await readMailbox(f.roles.coordinator.config.control, f.github.api);
  const tampered = f.github.putRecord({ previousStateDigest: digest(current.state), event: forged }, [head]);
  f.github.refs.set('heads/main', tampered);
  await assert.rejects(readMailbox(f.roles.coordinator.config.control, f.github.api), /task-packet-tampered/);
  await assert.rejects(f.inspect('mini'), /task-packet-tampered/);
  f.github.refs.set('heads/main', head);

  // Published packet, then consumer-side negatives.
  const live = await reserveAndPublish(f, 504, 'mini');
  const { binding } = await discover(f, 'mini', 504);
  const data = { ...binding, correlation: 'negative-504' };
  const plan = (evidence) => f.act('mini', 'dispatch-plan', { data, evidence });
  // The guard in `invoke` forbids consumer task facts, so call the runtime helper directly.
  const assignment = (await f.inspect('mini')).state.assignments[binding.assignmentId];
  const liveSubject = { title: live.subject.title, labels: live.evidence.labels, issueState: 'open', githubAssignees: [], bodySha256: sha256(live.subject.body) };
  assert.throws(() => composeTaskEvidence(assignment, liveSubject, { acceptanceCriteria: ['Invented by the consumer'] }), /task-packet-mismatch: supplied acceptanceCriteria/);
  assert.throws(() => composeTaskEvidence(assignment, liveSubject, { files: ['src/other.cs'] }), /task-packet-mismatch: supplied files/);
  assert.throws(() => composeTaskEvidence(assignment, liveSubject, { githubAssignees: ['someone'] }), /github-readback-mismatch/);
  assert.equal(composeTaskEvidence(assignment, liveSubject, { labels: [...live.evidence.labels].reverse() }).title, live.subject.title);
  assert.throws(() => composeTaskEvidence({ ...assignment, taskDigest: digest('other') }, liveSubject, {}), /task-packet-tampered/);
  assert.throws(() => composeTaskEvidence({ ...assignment, taskPacket: undefined }, liveSubject, {}), /native-evidence-missing/);
  live.subject.labels.push({ name: 'status:blocked' });
  await assert.rejects(plan(consumerStartEvidence()), /^Error: held:|held: a human hold/);
  live.subject.labels.pop();
  live.subject.title = 'Retitled after reservation';
  await assert.rejects(plan(consumerStartEvidence()), /task-changed: issue title changed/);
  live.subject.title = live.evidence.title;
  live.subject.labels.push({ name: 'priority:p0' });
  await assert.rejects(plan(consumerStartEvidence()), /task-changed: issue labels changed/);
  live.subject.labels.pop();
  live.subject.state = 'closed';
  await assert.rejects(plan(consumerStartEvidence()), /must be open/);
  live.subject.state = 'open';
  live.subject.assignees = [{ login: 'jpapiez' }];
  await assert.rejects(plan(consumerStartEvidence()), /not personally assigned/);
  live.subject.assignees = [];
  f.github.comments.set('repos/OlyForge3D/PrintFarmer/issues/504', { ...f.github.comments.get('repos/OlyForge3D/PrintFarmer/issues/504'), number: 999 });
  await assert.rejects(plan(consumerStartEvidence()), /github-readback-invalid/);
  f.github.comments.set('repos/OlyForge3D/PrintFarmer/issues/504', live.subject);
  assert.equal((await plan(consumerStartEvidence())).dispatchPlan.packet.issue, 504);
  // Nothing about the task entered the consumer journal or the mailbox from the consumer.
  const journal = await readFile(path.join(f.roles.mini.config.stateDirectory, 'journal.json'), 'utf8');
  assert.equal(journal.includes('Invented by the consumer'), false);
  assertNoRelay(f);
});

test('task packets are exact, versioned and reject private or unbounded facts', () => {
  const evidence = coordinatorEvidence(601);
  const packet = taskPacketFromEvidence(evidence, sha256('body'));
  assert.equal(packet.version, 'ralph-task-packet-v1');
  assert.equal(digest(taskFromPacket(packet)), digest(taskFromEvidence(evidence)));
  assert.equal(Object.hasOwn(packet, 'nativeCapabilities'), false);
  for (const change of [
    { version: 'ralph-task-packet-v0' }, { repository: 'someone/fork' }, { sessionId: 'aaaaaaaa-1111-4222-8333-444444444444' },
    { files: ['C:\\Users\\owner\\file.cs'] }, { files: ['~/secret'] }, { files: ['/etc/passwd'] }, { files: ['src/../../escape'] },
    { files: [] }, { scope: 'x'.repeat(65) }, { sourceBodySha256: 'not-a-digest' },
    { acceptanceCriteria: ['x'.repeat(70 * 1024)] },
    { acceptanceCriteria: ['Inspect /Users/owner/private/native-state/journal.json'] },
    { acceptanceCriteria: ['Resume session cccccccc-2222-4333-8444-000000000001'] },
    { acceptanceCriteria: [`Use token ghp_${'a'.repeat(36)}`] }, { acceptanceCriteria: ['Read ~/.printfarmer-ralph/host.json'] },
    { acceptanceCriteria: ['Open C:\\Users\\owner\\notes.txt'] }, { scope: '/home/owner' },
  ]) {
    assert.throws(() => validateTaskPacket({ ...packet, ...change }), /Invalid task packet|Unexpected|exact/i, JSON.stringify(change).slice(0, 80));
  }
});

test('settlement and startup readbacks fail closed on GitHub gaps', async () => {
  const github = githubFixture();
  const packet = taskPacketFromEvidence(coordinatorEvidence(701), sha256('body'));
  const assignment = { task: taskFromPacket(packet), taskPacket: packet };
  await assert.rejects(verifyTerminalArtifact(assignment, {}, github.api), /native-evidence-missing/);
  const pr = registerPullRequest(github, { number: 7701, issue: 701, headSha: 'b'.repeat(40), state: 'closed' });
  assignment.terminalArtifact = { kind: 'pull-request', url: pr.html_url, number: 7701, headSha: 'b'.repeat(40) };
  await assert.rejects(verifyTerminalArtifact(assignment, {}, github.api), /artifact-closed/);
  const closed = await verifyTerminalArtifact(assignment, { closureReason: 'Superseded by #702' }, github.api);
  assert.equal(closed.closedUnmerged, true);
  pr.merged = true;
  await assert.rejects(verifyTerminalArtifact(assignment, {}, github.api), /artifact-unreviewed/);
  setVerdictStatus(github, 'b'.repeat(40), `REVIEWED (self-attested) @ ${'b'.repeat(12)} by bishop`, 'failure');
  await assert.rejects(verifyTerminalArtifact(assignment, {}, github.api), /artifact-unreviewed/);
  setVerdictStatus(github, 'b'.repeat(40), `REVIEWED (self-attested, carried across sync) @ ${'b'.repeat(12)} by bishop, hicks`);
  const carried = (await verifyTerminalArtifact(assignment, {}, github.api)).verdict;
  assert.equal(carried.classification, 'REVIEWED');
  assert.equal(carried.carriedAcrossSync, true);
  // Provenance is verified exactly as verify-squad-verdict.mjs does, not from the description.
  const reviewed = `REVIEWED (self-attested) @ ${'b'.repeat(12)} by bishop, hicks`;
  for (const [label, options] of [
    ['spoofed status creator', { creator: 'jpapiez' }], ['run for another PR', { pr: 9999 }],
    ['absent workflow run', { withoutRun: true }], ['untrusted pull_request event', { run: { event: 'pull_request' } }],
    ['failed run', { run: { conclusion: 'failure' } }], ['workflow from a feature branch', { run: { head_branch: 'feature' } }],
    ['re-run attempt', { run: { run_attempt: 2 } }],
  ]) {
    setVerdictStatus(github, 'b'.repeat(40), reviewed, 'success', options);
    await assert.rejects(verifyTerminalArtifact(assignment, {}, github.api), /artifact-unreviewed/, label);
  }
  setVerdictStatus(github, 'b'.repeat(40), `APPROVE (owner) @ ${'b'.repeat(12)} by jpapiez; dissent=1; short=1`);
  assert.equal((await verifyTerminalArtifact(assignment, {}, github.api)).verdict.classification, 'APPROVED');
  setVerdictStatus(github, 'b'.repeat(40), reviewed);
  assert.equal((await verifyTerminalArtifact(assignment, {}, github.api)).verdict.classification, 'REVIEWED');

  const reserved = 'a'.repeat(40), advanced = 'c'.repeat(40);
  const dispatch = { ...packet, sourceRef: 'development' };
  assert.equal(await verifyDevelopmentAdvance(dispatch, reserved, github.api), undefined);
  setCompare(github, reserved, advanced, 'ahead');
  setCompare(github, advanced, 'development', 'behind');
  await assert.rejects(verifyDevelopmentAdvance(dispatch, advanced, github.api), /not a GitHub-verified descendant/);
  setCompare(github, advanced, 'development', 'ahead');
  assert.equal(await verifyDevelopmentAdvance(dispatch, advanced, github.api), advanced);
  assert.equal(await verifyDevelopmentAdvance({ ...dispatch, pr: 5 }, advanced, github.api), undefined,
    'PR recovery never accepts a different head');
});

test('review findings: settlement races, startup rechecks, private text, legacy blockers and existing-PR dispatch fail closed', async (t) => {
  const f = await farm(t);
  await f.begin('coordinator');
  await f.begin('mini');
  await f.ready('mini');

  // Private free text never reaches the mailbox (R2958-04).
  const headBefore = f.github.refs.get('heads/main');
  const leaky = coordinatorEvidence(801);
  registerTaskSubject(f.github, leaky);
  for (const criterion of ['Inspect /Users/owner/private/native-state/journal.json', 'Resume cccccccc-2222-4333-8444-000000000009']) {
    await assert.rejects(f.act('coordinator', 'reserve', { data: { assignmentId: 'leaky-801', workerId: 'mini' },
      evidence: { ...leaky, acceptanceCriteria: [criterion] } }), /must not contain local paths, native IDs or credentials/);
  }
  assert.equal(f.github.refs.get('heads/main'), headBefore, 'a rejected reservation publishes nothing');

  // Optional identity facts that the packet does not carry are mismatches, not TypeErrors (R2958-07).
  const identity = await reserveAndPublish(f, 802, 'mini');
  const identityAssignment = (await f.inspect('mini')).state.assignments[identity.assignmentId];
  const identitySubject = { title: identity.subject.title, labels: identity.evidence.labels, issueState: 'open',
    githubAssignees: [], bodySha256: sha256(identity.subject.body) };
  assert.throws(() => composeTaskEvidence(identityAssignment, identitySubject, { pr: 5 }), /task-packet-mismatch: supplied pr/);

  // An edit or hold after `starting` keeps the child startup-only (R2958-03).
  const { binding: identityBinding } = await discover(f, 'mini', 802);
  const recheck = await startWorker(f, 'mini', identityBinding, { correlation: 'recheck-802' });
  identity.subject.body = 'Requirements rewritten after starting.\n';
  await assert.rejects(f.act('mini', 'startup-check', { data: recheck.data, evidence: recheck.startupAck(recheck.head) }),
    /task-changed: no substantive continuation.*Report-blocker task-changed/);
  identity.subject.body = 'Public body for issue 802.\n';
  identity.subject.labels.push({ name: 'go:no' });
  await assert.rejects(f.act('mini', 'startup-check', { data: recheck.data, evidence: recheck.startupAck(recheck.head) }),
    /held: no substantive continuation/);
  identity.subject.labels.pop();
  identity.subject.assignees = [{ login: 'jpapiez' }];
  await assert.rejects(f.act('mini', 'startup-check', { data: recheck.data, evidence: recheck.startupAck(recheck.head) }),
    /task-changed: no substantive continuation/);
  identity.subject.assignees = [];
  const recheckDelivered = await runWorker(f, 'mini', recheck);

  // A replacement terminal receipt that lands between coordinator verification and
  // settlement publication rejects the settlement (R2958-02).
  const verifiedHead = '1'.repeat(40);
  const verifiedPr = registerPullRequest(f.github, { number: 7802, issue: 802, headSha: verifiedHead });
  await reportTerminal(f, 'mini', recheck, recheckDelivered, verifiedPr.html_url);
  Object.assign(verifiedPr, { state: 'closed', merged: true });
  setVerdictStatus(f.github, verifiedHead);
  const settlement = await settlementRequest(f, identity.assignmentId);
  const unverifiedPr = registerPullRequest(f.github, { number: 7803, issue: 802, headSha: '2'.repeat(40) });
  const original = f.github.api;
  let armed = false, raced = false;
  f.github.api = async (endpoint, ...rest) => {
    if (endpoint.endsWith(`/commits/${verifiedHead}/statuses?per_page=100`)) armed = true;
    if (armed && !raced && endpoint === 'repos/fixture/private-control/git/ref/heads/main') {
      raced = true;
      f.github.api = original;
      await reportTerminal(f, 'mini', recheck, recheckDelivered, unverifiedPr.html_url, { replacement: true });
      f.github.api = async (...args) => original(...args);
    }
    return original(endpoint, ...rest);
  };
  await assert.rejects(f.act('coordinator', 'release', settlement), /artifact-changed: the terminal receipt or artifact changed/);
  f.github.api = original;
  assert.equal(raced, true, 'the replacement receipt raced the settlement');
  const racedEntry = (await f.inspect('coordinator')).state.assignments[identity.assignmentId];
  assert.equal(racedEntry.state, 'terminal-reported');
  assert.equal(racedEntry.terminalArtifact.number, 7803);
  // Fresh reconciliation of the new receipt sees the open PR and retains the assignment.
  await assert.rejects(f.act('coordinator', 'release', await settlementRequest(f, identity.assignmentId)), /artifact-pending/);

  // A pre-#2958 digest-only blocker self-heals after renewal: the consumer mints a
  // fresh proof that is published, and the coordinator withdraws from the mailbox (R2958-05).
  const legacyEvidence = coordinatorEvidence(803);
  registerTaskSubject(f.github, legacyEvidence);
  const offerId = (await f.inspect('coordinator')).state.availability.mini.offerId;
  await publishEvent(f.roles.coordinator.config.control, {
    id: 'legacy-reserve-803', type: 'reserve', authorityId: 'primary', epoch: 1, role: 'coordinator',
    roundId: f.roles.coordinator.round.roundId, observedAt: new Date(now).toISOString(),
    data: { assignmentId: 'legacy-803', workerId: 'mini', generation: 1, task: taskFromEvidence(legacyEvidence),
      eligibilityDigest: digest('legacy-803'), policySha: 'a'.repeat(40), offerId },
  }, f.github.api);
  const legacy = (await f.inspect('coordinator')).state.assignments['legacy-803'];
  const legacyBinding = { assignmentId: 'legacy-803', generation: legacy.generation, taskDigest: legacy.taskDigest };
  const proofEvidence = fresh({ authoritativeJournalRetained: true, protocolOnlyDeliveryAttested: true });
  const { proof: oldProof } = await f.act('mini', 'prestart-proof', { data: legacyBinding, evidence: proofEvidence });
  await publishEvent(f.roles.coordinator.config.control, {
    id: 'legacy-blocker-803', type: 'report-blocker', authorityId: 'primary', epoch: 1, role: 'consumer', workerId: 'mini',
    roundId: f.roles.mini.round.roundId, observedAt: new Date(now).toISOString(),
    data: { ...legacyBinding, reasonCode: 'native-evidence-missing', evidenceDigest: digest(oldProof) },
  }, f.github.api);
  assert.equal((await f.inspect('coordinator')).state.assignments['legacy-803'].blocker.prestartProof, undefined);
  await assert.rejects(f.act('coordinator', 'withdraw', { data: legacyBinding,
    evidence: fresh({ claimsReconciled: true, noNativeDeliveryVerified: true }) }), /committed by its blocker/);
  const renewed = await f.act('mini', 'prestart-proof', { data: legacyBinding, evidence: proofEvidence });
  assert.equal(renewed.alreadyReported, false, 'a digest-only historical blocker needs its proof published');
  assert.notEqual(digest(renewed.proof), digest(oldProof));
  await f.act('mini', 'report-blocker', { data: { ...legacyBinding, reasonCode: 'native-evidence-missing' }, evidence: renewed.proof });
  const again = await f.act('mini', 'prestart-proof', { data: legacyBinding, evidence: proofEvidence });
  assert.equal(again.alreadyReported, true, 'idempotent once the full proof is published');
  const withdrawn = await f.act('coordinator', 'withdraw', { data: legacyBinding,
    evidence: fresh({ claimsReconciled: true, noNativeDeliveryVerified: true }) });
  assert.equal(withdrawn.state.assignments['legacy-803'].disposition, 'withdrawn-before-delivery');
});

test('existing-PR recovery dispatches from a runtime PR-head policy readback, not consumer evidence (R2958-06)', async (t) => {
  const f = await farm(t);
  await f.begin('coordinator');
  await f.begin('mini');
  await f.ready('mini');
  const headSha = 'a'.repeat(40);
  const pr = registerPullRequest(f.github, { number: 7901, headSha, ref: 'fix/existing-7901' });
  const evidence = { ...coordinatorEvidence(901), pr: 7901, prState: 'open', prHeadRepository: 'OlyForge3D/PrintFarmer',
    prHeadRef: pr.head.ref, prHeadSha: headSha };
  registerTaskSubject(f.github, evidence);
  await f.act('coordinator', 'reserve', { data: { assignmentId: 'pr-7901', workerId: 'mini' }, evidence });
  const entry = (await f.inspect('coordinator')).state.assignments['pr-7901'];
  const binding = { assignmentId: 'pr-7901', generation: entry.generation, taskDigest: entry.taskDigest };
  await f.act('coordinator', 'publish', { data: binding, evidence });
  const data = { ...binding, correlation: 'pr-7901' };
  const plan = (extra = {}) => f.act('mini', 'dispatch-plan', { data, evidence: { ...consumerStartEvidence(), ...extra } });
  await assert.rejects(plan(), /github-readback-invalid: .* unavailable at the PR head/);
  await registerPolicyAtHead(f.github, headSha, 'copilot', { '.github/agents/ralph-worker.agent.md': 'name: Old Worker\n' });
  await assert.rejects(plan(), /New PR recovery requires/);
  await registerPolicyAtHead(f.github, headSha, 'copilot');
  await assert.rejects(plan({ prWorkerPolicyDigest: digest('consumer-invented') }), /github-readback-mismatch: supplied prWorkerPolicyDigest/);
  const accepted = await plan();
  assert.equal(accepted.dispatchPlan.nativeArguments.base_branch, 'fix/existing-7901');
  assert.equal(accepted.dispatchPlan.packet.pr, 7901);
  pr.head.sha = 'b'.repeat(40);
  await assert.rejects(plan(), /New PR recovery requires|github-readback/);
  assertNoRelay(f);
});
