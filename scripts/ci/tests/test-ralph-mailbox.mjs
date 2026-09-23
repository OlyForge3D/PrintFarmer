import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  applyEvent, digest, initialState, initializeMailbox, inventoryDigest, admissionInventoryDigest,
  publishEvent, readMailbox, researchDisposition, taskFromEvidence, validateRegistry, verifyControlRepository, localInventoryFreshness,
} from '../ralph-mailbox.mjs';
import { prepareEvent, runNativeRequest, validateTriageEvidence, readResearchArtifact } from '../ralph-native-runtime.mjs';
import { buildDispatchPlan, validateClassification, validateStartup, validatePacketAck, policyTextDigest } from '../ralph-native-dispatch.mjs';

const registry = {
  version: 1, authorityId: 'primary', epoch: 1, writers: ['fixture-owner'],
  workers: [
    { workerId: 'mini', host: 'macos-mobile', capabilities: ['general', 'ios'] },
    { workerId: 'windows', host: 'windows-general', capabilities: ['general'] },
  ],
};
const baseControl = {
  repository: 'fixture/private-control', repositoryId: 123, ref: 'heads/main',
  registry, sharedWriterTrustAccepted: true,
};
const now = Date.now();
test('local kickoff freshness identifies the saved inventory clock without extending its boundary', () => {
  for (const [ageMs, refreshRequired] of [[0, false], [60_000, false], [60_001, true], [-1, true]]) {
    const observedAt = new Date(now - ageMs).toISOString();
    assert.deepEqual(localInventoryFreshness({ readiness: { mini: { observedAt } } }, 'mini', now), {
      observedAt, ageMs, maxAgeMs: 60_000, refreshRequired,
    });
  }
  for (const observedAt of [undefined, 'invalid']) {
    assert.deepEqual(localInventoryFreshness({ readiness: { mini: { observedAt } } }, 'mini', now), {
      observedAt, ageMs: undefined, maxAgeMs: 60_000, refreshRequired: true,
    });
  }
  assert.equal(localInventoryFreshness({}, 'mini', now).refreshRequired, true);
});

const nativeCapabilities = {
  createSession: true, openPrSession: true, agents: ['Ralph Worker'],
  models: { 'gpt-6-astra': ['medium', 'xhigh', 'max'], 'claude-opus-4.7': ['medium', 'xhigh'] },
};
let next = 0;
function event(type, role = 'coordinator', data = {}, workerId = 'mini', roundId) {
  return {
    id: `event-${++next}`, type, authorityId: 'primary', epoch: 1, role,
    ...(role === 'consumer' ? { workerId } : {}),
    roundId: roundId ?? (role === 'coordinator' ? 'coordinator-round' : `${workerId}-round`),
    observedAt: new Date(now).toISOString(), data,
  };
}
const evidence = (issue = 1, mobile = false) => ({
  repository: 'OlyForge3D/PrintFarmer', issue, headSha: 'a'.repeat(40),
  title: mobile ? 'iOS change' : 'General change',
  issueState: 'open', githubAssignees: [],
  labels: ['squad:copilot', 'type:bug', 'priority:p1'],
  acceptanceCriteria: ['Implement assigned task'], files: [`${mobile ? 'mobile' : 'src'}/task-${issue}`],
  filesComplete: true, scope: mobile ? 'mobile' : 'general', classificationComplete: true,
  capabilities: [mobile ? 'ios' : 'general'],
  nativeCapabilities,
});

test('classification requires explicit inputs without weakening conservative mobile accounting', () => {
  const general = evidence();
  for (const missing of ['scope', 'classificationComplete']) {
    const incomplete = { ...general };
    delete incomplete[missing];
    assert.throws(() => validateClassification(incomplete), /Explicit scope/);
  }
  for (const change of [
    { scope: 'unknown', classificationComplete: false },
    { scope: 'mixed' }, { files: ['mobile/View.swift'] }, { classificationComplete: false },
  ]) {
    validateClassification({ ...general, ...change });
    assert.equal(taskFromEvidence({ ...general, ...change }).category, 'mobile');
  }
  assert.equal(taskFromEvidence(general).category, 'general');
});

test('policy-text hashes agree across Windows CRLF checkouts and Git LF blobs without trimming content', () => {
  assert.equal(policyTextDigest('name: Squad\r\nrole: worker\r\n'), policyTextDigest('name: Squad\nrole: worker\n'));
  assert.notEqual(policyTextDigest('name: Squad \n'), policyTextDigest('name: Squad\n'));
  assert.notEqual(policyTextDigest('name: Squad'), policyTextDigest('name: Squad\n'));
});

test('dispatch separates native Ralph Worker agent, Squad charter owner, category and explicit host model', async () => {
  const source = { ...evidence(), labels: ['squad:dallas', 'go:needs-research', 'type:bug', 'priority:p1'] };
  const task = taskFromEvidence(source);
  const assignment = { assignmentId: 'research-1', generation: 1, task, taskDigest: digest(task) };
  const config = { host: 'macos-mobile', projectId: 'project-fixture', approvedPolicy: 'a'.repeat(40) };
  const args = { config, assignment, correlation: 'correlation-1', owner: 'squad:dallas', evidence: source, cwd: process.cwd() };
  const plan = await buildDispatchPlan(args);
  assert.equal(plan.nativeTool, 'create_session');
  assert.equal(plan.nativeArguments.kickoff.agent, 'Ralph Worker');
  assert.equal(plan.nativeArguments.kickoff.model, 'gpt-6-astra');
  assert.equal(plan.nativeArguments.kickoff.reasoning_effort, 'xhigh');
  assert.equal(plan.packet.member, 'dallas');
  assert.equal(plan.packet.category, 'general');
  assert.equal(plan.packet.charterPath, '.squad/agents/dallas/charter.md');
  assert.match(plan.nativeArguments.kickoff.prompt, /^RALPH-ASSIGNED-WORKER-V1/);
  assert.match(plan.nativeArguments.kickoff.prompt, /startup-only ACK/);
  assert.match(plan.continuation, /no implementation, edits, commits, PRs/);
  assert.match(plan.continuation, /When your work is complete/);
  for (const change of [
    { agents: ['Squad'] }, { agents: ['Dallas'] }, { agents: [] },
    { models: { 'gpt-6-astra': ['medium'] } }, { createSession: false },
  ]) await assert.rejects(buildDispatchPlan({
    ...args, evidence: { ...source, nativeCapabilities: { ...nativeCapabilities, ...change } },
  }), /blocked/);
  const prEvidence = { ...source, pr: 12, prState: 'open', prHeadRepository: 'OlyForge3D/PrintFarmer',
    prHeadRef: 'fix/existing-pr', prHeadSha: task.headSha, prWorkerPolicyDigest: plan.packet.workerPolicyDigest };
  const prTask = taskFromEvidence(prEvidence);
  const prPlan = await buildDispatchPlan({ ...args, evidence: prEvidence,
    assignment: { ...assignment, task: prTask, taskDigest: digest(prTask) } });
  assert.equal(prPlan.nativeTool, 'create_session');
  assert.equal(prPlan.nativeArguments.workspace_type, 'worktree');
  assert.equal(prPlan.nativeArguments.base_branch, 'fix/existing-pr');
  assert.match(prPlan.continuation, /HEAD:refs\/heads\/fix\/existing-pr/);
  for (const change of [
    { prState: 'closed' }, { prHeadRepository: 'someone/fork' }, { prHeadSha: 'b'.repeat(40) },
    { prHeadRef: '--force' }, { prHeadRef: 'bad..ref' }, { prWorkerPolicyDigest: undefined },
  ]) await assert.rejects(buildDispatchPlan({ ...args, evidence: { ...prEvidence, ...change },
    assignment: { ...assignment, task: prTask, taskDigest: digest(prTask) } }), /New PR recovery/);
  const ack = {
    dispatchPlanDigest: plan.planDigest,
    session: { branch: 'fixture-worker' },
    startupAck: { ...plan.packet, substantiveWorkStarted: false, noChildren: true,
      initialHeadSha: plan.packet.headSha, actualBranch: 'fixture-worker' },
    configuration: { source: 'successful-native-create', model: 'gpt-6-astra', reasoningEffort: 'xhigh' },
  };
  validateStartup(plan, ack);
  const prAck = { ...ack, dispatchPlanDigest: prPlan.planDigest, currentPrHeadSha: prPlan.packet.headSha,
    startupAck: { ...ack.startupAck, ...prPlan.packet } };
  validateStartup(prPlan, prAck);
  assert.throws(() => validateStartup(prPlan, { ...prAck, currentPrHeadSha: 'b'.repeat(40) }), /Fresh PR head/);
  for (const key of Object.keys(plan.packet)) {
    assert.throws(() => validatePacketAck(plan.packet, { ...plan.packet, [key]: 'mismatch' }), /Packet ACK/);
  }
  assert.throws(() => validateStartup(plan, { ...ack,
    startupAck: { ...ack.startupAck, initialHeadSha: 'b'.repeat(40) } }), /Initial worker HEAD/);
  for (const branch of [undefined, '']) {
    assert.throws(() => validateStartup(plan, { ...ack, session: { branch } }),
      /requires evidence.session.branch.*same child/);
  }
  assert.throws(() => validateStartup(plan, { ...ack, session: { branch: 'different-worker' } }),
    /actualBranch differs from native session.branch/);
  for (const change of [
    { startupAck: { ...ack.startupAck, member: 'lambert' } },
    { startupAck: { ...ack.startupAck, actualReasoningEffort: 'medium' } },
    { configuration: { ...ack.configuration, reasoningEffort: 'medium' } },
    { configuration: { ...ack.configuration, source: 'failed-create-request' } },
  ]) assert.throws(() => validateStartup(plan, { ...ack, ...change }), /ACK|model\/effort/);
});

test('bounded dispatch uses only the PrintFarmer worker, not a Squad coordinator installation', async (t) => {
  const cwd = await mkdtemp(path.join(os.tmpdir(), 'ralph-worker-policy-'));
  t.after(() => rm(cwd, { recursive: true, force: true }));
  const agentPath = '.github/agents/ralph-worker.agent.md';
  for (const file of [
    '.squad/config.json', '.squad/agents/dallas/charter.md',
    '.copilot/skills/ralph-loop/hosts.json', '.copilot/skills/ralph-loop/macos-kickoff.md',
    '.copilot/skills/ralph-loop/assigned-worker.md', agentPath,
  ]) {
    await mkdir(path.dirname(path.join(cwd, file)), { recursive: true });
    await writeFile(path.join(cwd, file), await readFile(file));
  }
  const source = { ...evidence(), labels: ['squad:dallas', 'go:needs-research', 'type:bug', 'priority:p1'] };
  const task = taskFromEvidence(source);
  const args = {
    cwd, config: { host: 'macos-mobile', projectId: 'project-fixture', approvedPolicy: 'a'.repeat(40) },
    assignment: { assignmentId: 'research-worker', generation: 1, task, taskDigest: digest(task) },
    correlation: 'worker-correlation', owner: 'squad:dallas', evidence: source,
  };
  const plan = await buildDispatchPlan(args);
  assert.equal(plan.nativeArguments.kickoff.agent, 'Ralph Worker');
  const worker = await readFile(path.join(cwd, agentPath), 'utf8');
  assert.match(worker, /Do not invoke Squad, another custom agent, task agents or child sessions/);
  await writeFile(path.join(cwd, '.github/agents/squad.agent.md'), 'unrelated distribution update\n');
  assert.deepEqual(await buildDispatchPlan(args), plan);
  await writeFile(path.join(cwd, agentPath), `${worker}\nChanged worker policy.\n`);
  assert.notEqual((await buildDispatchPlan(args)).packet.workerPolicyDigest, plan.packet.workerPolicyDigest);
  await writeFile(path.join(cwd, agentPath), worker.replace('name: Ralph Worker', 'name: Squad'));
  await assert.rejects(buildDispatchPlan(args), /Registered Ralph Worker entrypoint/);
  await rm(path.join(cwd, agentPath));
  await assert.rejects(buildDispatchPlan(args), { code: 'ENOENT' });
});
function advance(state, value) { return applyEvent(state, value, { now }); }
function withRounds() {
  let state = initialState(registry);
  for (const role of ['coordinator', 'mini', 'windows']) {
    state = advance(state, event('begin-round', role === 'coordinator' ? role : 'consumer',
      { invocationDigest: digest(role) }, role));
  }
  return state;
}
function ready(state, worker = 'mini') {
  return advance(state, event('ready', 'consumer', {
    inventoryDigest: digest('native-inventory'), assignmentInventoryDigest: inventoryDigest(state, worker),
    unassignedSessions: 0, capabilities: registry.workers.find((item) => item.workerId === worker).capabilities,
  }, worker));
}
function reserve(state, issue, worker = 'mini', mobile = false) {
  return advance(state, event('reserve', 'coordinator', {
    assignmentId: `assignment-${issue}`, workerId: worker, generation: 1,
    task: taskFromEvidence(evidence(issue, mobile)), eligibilityDigest: digest('live-eligibility'), policySha: 'b'.repeat(40),
  }));
}
function binding(state, issue) {
  const entry = state.assignments[`assignment-${issue}`];
  return { assignmentId: `assignment-${issue}`, generation: entry.generation, taskDigest: entry.taskDigest };
}

export function githubFixture() {
  const blobs = new Map(), trees = new Map(), commits = new Map(), refs = new Map(), comments = new Map();
  const calls = [];
  let count = 0;
  const sha = (value) => createHash('sha1').update(value).digest('hex');
  const metadata = {
    id: 123, full_name: 'fixture/private-control', private: true, visibility: 'private',
    archived: false, disabled: false, permissions: { push: true }, default_branch: 'main',
  };
  const putRecord = (record, parents = []) => {
    const content = JSON.stringify(record);
    const blobSha = sha(`blob ${Buffer.byteLength(content)}\0${content}`);
    blobs.set(blobSha, { sha: blobSha, encoding: 'base64', size: Buffer.byteLength(content), content: Buffer.from(content).toString('base64') });
    const treeSha = sha(`tree-${++count}`);
    trees.set(treeSha, { sha: treeSha, truncated: false, tree: [{ path: 'mailbox.json', type: 'blob', mode: '100644', sha: blobSha }] });
    const commitSha = sha(`commit-${++count}`);
    commits.set(commitSha, { sha: commitSha, tree: { sha: treeSha }, parents: parents.map((parent) => ({ sha: parent })) });
    return commitSha;
  };
  let beforePatch;
  let losePatchResponse = false;
  const api = async (endpoint, method = 'GET', body) => {
    calls.push({ endpoint, method, body });
    if (comments.has(endpoint) && method === 'GET') return structuredClone(comments.get(endpoint));
    const suffix = endpoint.replace('repos/fixture/private-control', '');
    if (endpoint === 'user') return { login: 'fixture-owner' };
    if (suffix === '') return structuredClone(metadata);
    if (suffix === '/collaborators/fixture-owner/permission') return { permission: 'admin', user: { login: 'fixture-owner' } };
    if (suffix === '/branches?per_page=1') return refs.size ? [{ name: 'main' }] : [];
    if (suffix === '/contents/mailbox.json' && method === 'PUT') {
      assert.equal(body.sha, undefined);
      assert.equal(body.branch, 'main');
      if (refs.size) throw new Error('create-only conflict');
      const commitSha = putRecord(JSON.parse(Buffer.from(body.content, 'base64')));
      refs.set('heads/main', commitSha);
      return { commit: { sha: commitSha } };
    }
    if (suffix.startsWith('/git/ref/')) {
      const ref = suffix.slice('/git/ref/'.length);
      if (!refs.has(ref)) throw new Error('404 ref');
      return { ref: `refs/${ref}`, object: { sha: refs.get(ref), type: 'commit' } };
    }
    if (suffix === '/git/trees' && method === 'POST') {
      const record = JSON.parse(body.tree[0].content);
      const commitSha = putRecord(record);
      return trees.get(commits.get(commitSha).tree.sha);
    }
    if (suffix === '/git/commits' && method === 'POST') {
      const commitSha = sha(`commit-${++count}`);
      const commit = { sha: commitSha, tree: { sha: body.tree }, parents: body.parents.map((parent) => ({ sha: parent })) };
      commits.set(commitSha, commit);
      return commit;
    }
    if (suffix.startsWith('/git/commits/')) return commits.get(suffix.slice('/git/commits/'.length));
    if (suffix.startsWith('/git/trees/')) return trees.get(suffix.slice('/git/trees/'.length));
    if (suffix.startsWith('/git/blobs/')) return blobs.get(suffix.slice('/git/blobs/'.length));
    if (suffix === '/git/refs' && method === 'POST') {
      const ref = body.ref.slice('refs/'.length);
      if (refs.has(ref)) throw new Error('existing ref');
      refs.set(ref, body.sha);
      return {};
    }
    if (suffix.startsWith('/git/refs/') && method === 'PATCH') {
      assert.equal(body.force, false);
      if (beforePatch) { const hook = beforePatch; beforePatch = undefined; await hook(); }
      const ref = suffix.slice('/git/refs/'.length);
      const previous = refs.get(ref);
      const candidate = commits.get(body.sha);
      if (candidate.parents.length !== 1 || candidate.parents[0].sha !== previous) throw new Error('non-fast-forward');
      refs.set(ref, body.sha);
      if (losePatchResponse) { losePatchResponse = false; throw new Error('lost response after success'); }
      return {};
    }
    throw new Error(`Unexpected ${method} ${endpoint}`);
  };
  return { api, metadata, calls, refs, commits, blobs, comments, putRecord,
    race: (hook) => { beforePatch = hook; }, loseResponse: () => { losePatchResponse = true; } };
}

test('exact Mac1+4 and Windows0+5 quotas, no borrowing and global overlaps', () => {
  let state = withRounds();
  state = reserve(ready(state), 1, 'mini', true);
  assert.throws(() => reserve(ready(state), 2, 'mini', true), /quota/);
  for (let issue = 2; issue <= 5; issue++) state = reserve(ready(state), issue);
  assert.throws(() => reserve(ready(state), 6), /quota/);
  let generalOnly = withRounds();
  for (let issue = 1; issue <= 4; issue++) generalOnly = reserve(ready(generalOnly), issue);
  assert.throws(() => reserve(ready(generalOnly), 5), /quota/);
  assert.throws(() => advance(ready(state, 'windows'), event('reserve', 'coordinator', {
    assignmentId: 'conflicting-assignment', workerId: 'windows', generation: 1,
    task: taskFromEvidence(evidence(1)), eligibilityDigest: digest('eligibility'), policySha: 'b'.repeat(40),
  })), /overlap/);
  assert.throws(() => reserve(ready(state, 'windows'), 6, 'windows', true), /tooling|quota/);
  for (let issue = 6; issue <= 10; issue++) state = reserve(ready(state, 'windows'), issue, 'windows');
  assert.throws(() => reserve(ready(state, 'windows'), 11, 'windows'), /quota/);
  assert.equal(Object.keys(state.assignments).length, 10);
});

test('fresh readiness binds current assignment inventory; stale/missing evidence cannot admit', () => {
  let state = withRounds();
  assert.throws(() => reserve(state, 1), /Fresh/);
  state = reserve(ready(state), 1);
  assert.throws(() => reserve(state, 2), /reconcile/);
  state = ready(state);
  state.readiness.mini.observedAt = new Date(now - 60_001).toISOString();
  assert.throws(() => reserve(state, 2), /Fresh/);
});

test('research gate is actionable, quota-accounted, cannot authorize implementation or ignore holds', () => {
  const task = taskFromEvidence({ ...evidence(), labels: ['go:needs-research', 'squad:dallas'] });
  assert.equal(task.purpose, 'research');
  assert.equal(task.category, 'general');
  assert.throws(() => taskFromEvidence({ ...evidence(), purpose: 'implementation', labels: ['go:needs-research'] }), /research only/);
  for (const label of ['go:no', 'status:on-hold', 'status:blocked', 'blocked']) {
    assert.throws(() => taskFromEvidence({ ...evidence(), labels: ['go:needs-research', label] }), /hold/);
    assert.equal(researchDisposition({ labels: ['go:needs-research', label] }).action, 'blocked');
  }
  const research = { issue: 1, issueState: 'open', githubAssignees: [], labels: ['go:needs-research', 'squad:dallas'] };
  assert.equal(researchDisposition(research).action, 'reserve-research');
  assert.equal(researchDisposition(research, { task, state: 'running' }).action, 'follow-existing-research');
  assert.equal(researchDisposition(research, { task, state: 'terminal' }).action, 'retain-research-gate');
  const findings = {
    summary: 'Verified findings', acceptanceCriteria: ['Concrete implementation acceptance'],
    remainingResearchBlockers: [], exitCriteriaMet: true, approvalRequired: false, implementationPlanVerified: true,
    researchQuestionsAnswered: true, issueEvidenceReadback: true, issueDescriptionUpdated: true,
    issueCommentUrl: 'https://github.com/OlyForge3D/PrintFarmer/issues/1#issuecomment-123',
    repositoryFilesChanged: false, implementationOwner: 'squad:lambert', implementationReady: true,
    implementationBlockers: [],
  };
  const proposal = researchDisposition({ ...research, findings }, { task, state: 'terminal' });
  assert.equal(proposal.action, 'propose-implementation-readiness');
  assert.equal(proposal.closeIssue, false);
  assert.equal(proposal.mutationAuthorized, false);
  assert.equal(proposal.issue, 1);
  assert.equal(proposal.implementationIssue, 1);
  assert.deepEqual(proposal.removeLabels, ['go:needs-research', 'squad:dallas']);
  assert.deepEqual(proposal.addLabels, ['go:yes', 'squad:lambert']);
  for (const change of [
    { approvalRequired: true }, { remainingResearchBlockers: ['decision needed'] }, { exitCriteriaMet: false },
    { issueDescriptionUpdated: false }, { issueEvidenceReadback: false }, { researchQuestionsAnswered: false },
    { issueCommentUrl: 'https://github.com/OlyForge3D/PrintFarmer/issues/2#issuecomment-123' },
  ]) {
    assert.equal(researchDisposition({ ...research, findings: { ...findings, ...change } }, { task, state: 'terminal' }).action, 'retain-research-gate');
  }
  const blockedImplementation = researchDisposition({ ...research, findings: {
    ...findings, implementationReady: false, implementationBlockers: ['Waiting on a prerequisite'],
  } }, { task, state: 'terminal' });
  assert.equal(blockedImplementation.action, 'propose-research-complete');
  assert.ok(blockedImplementation.removeLabels.includes('go:needs-research'));
  assert.ok(!blockedImplementation.addLabels.includes('go:yes'));
  assert.equal(researchDisposition({ ...research, issueState: 'closed', findings }, { task, state: 'terminal' }).action, 'blocked');
  const repositoryResearch = { ...findings, repositoryFilesChanged: true };
  assert.equal(researchDisposition({ ...research, findings: repositoryResearch }, { task, state: 'terminal' }).action, 'retain-research-gate');
  assert.equal(researchDisposition({ ...research, findings: { ...repositoryResearch,
    researchPrUrl: 'https://github.com/OlyForge3D/PrintFarmer/pull/3',
    researchPrMerged: true, researchHeadSha: 'a'.repeat(40),
  } }, { task, state: 'terminal' }).action, 'propose-implementation-readiness');
  const child = researchDisposition({ ...research, findings: { ...findings, implementationIssue: 2 } }, { task, state: 'terminal' });
  assert.equal(child.issue, 1);
  assert.equal(child.implementationIssue, 2);
  assert.deepEqual(child.removeLabels, ['go:needs-research']);
  assert.deepEqual(child.addLabels, []);
  let state = withRounds();
  for (let issue = 1; issue <= 4; issue++) {
    state = ready(state);
    state = advance(state, event('reserve', 'coordinator', {
      assignmentId: `research-${issue}`, workerId: 'mini', generation: 1,
      task: taskFromEvidence({ ...evidence(issue), labels: ['go:needs-research'] }),
      eligibilityDigest: digest('evidence'), policySha: 'b'.repeat(40),
    }));
  }
  assert.throws(() => reserve(ready(state), 5), /quota/);
});

test('central triage mechanically requires valid type/priority/Squad owner, not a device or personal assignee', () => {
  const input = {
    ...evidence(), issueState: 'OPEN', githubAssignees: [],
    labels: ['squad:dallas', 'squad:🏗️ dallas', 'type:spike', 'priority:p1', 'go:needs-research'],
  };
  assert.deepEqual(validateTriageEvidence(input), { owner: 'squad:dallas', purpose: 'research' });
  for (const labels of [
    ['squad:mini', 'type:bug', 'priority:p1'],
    ['squad:ralph', 'type:bug', 'priority:p1'],
    ['squad:bishop', 'type:bug', 'priority:p1'],
    ['squad:dallas', 'squad:copilot', 'type:bug', 'priority:p1'],
    ['squad:dallas', 'type:bug'],
    ['squad:dallas', 'type:epic', 'priority:p1'],
    ['squad:dallas', 'type:bug', 'priority:p1', 'status:needs-analysis'],
  ]) assert.throws(() => validateTriageEvidence({ ...input, labels }));
  assert.throws(() => validateTriageEvidence({ ...input, githubAssignees: ['jpapiez'] }), /personally/);
  assert.throws(() => validateTriageEvidence({ ...input, issueState: 'closed' }), /open/);
});

test('pending blockers retain ownership and withdrawal cannot race an accepted kickoff', () => {
  let state = reserve(ready(withRounds()), 1);
  state = advance(state, event('publish', 'coordinator', binding(state, 1)));
  state = advance(state, event('report-blocker', 'consumer', {
    ...binding(state, 1), reasonCode: 'task-changed', evidenceDigest: digest('changed'),
  }));
  assert.equal(state.assignments['assignment-1'].state, 'published');
  const withdrawn = advance(state, event('withdraw', 'coordinator', {
    ...binding(state, 1), reconciliationDigest: digest('never-delivered'),
  }));
  assert.equal(withdrawn.assignments['assignment-1'].disposition, 'withdrawn-before-delivery');
  assert.throws(() => advance(withdrawn, event('receipt', 'consumer', {
    ...binding(state, 1), status: 'starting', correlation: 'opaque', evidenceDigest: digest('start'),
  })), /transition/);
  const starting = advance(state, event('receipt', 'consumer', {
    ...binding(state, 1), status: 'starting', correlation: 'opaque', evidenceDigest: digest('start'),
  }));
  assert.throws(() => advance(starting, event('withdraw', 'coordinator', {
    ...binding(state, 1), reconciliationDigest: digest('not-enough'),
  })), /never-delivered/);
});

test('crashed round recovery requires exact old identity and does not clear reservations', () => {
  const state = reserve(ready(withRounds()), 1);
  const request = event('recover-coordinator-round', 'coordinator', {
    oldRoundId: 'coordinator-round', cessationEvidenceDigest: digest('ceased'), invocationDigest: digest('new'),
  }, 'mini', 'new-coordinator-round');
  const recovered = advance(state, request);
  assert.equal(recovered.rounds.coordinator.roundId, 'new-coordinator-round');
  assert.deepEqual(recovered.assignments, state.assignments);
  assert.throws(() => advance(state, { ...request, data: { ...request.data, oldRoundId: 'unknown' } }), /prior coordinator/);
  assert.throws(() => prepareEvent({ role: 'coordinator', control: baseControl }, {
    ...request,
    evidence: { source: 'native', observedAt: new Date(now).toISOString(), cessationProven: false },
  }, { state }, { sessions: {} }, now), /cessation/);
});

const ownedScope = { ownershipScope: 'ralph-owned-v1', lineageChecked: true, nativeCapabilities };

test('resumed terminal native sessions block readiness rather than escaping quota', () => {
  const state = withRounds();
  state.assignments.old = { workerId: 'mini', state: 'terminal' };
  const id = 'dddddddd-1111-4222-8333-444444444444';
  const request = { ...event('ready', 'consumer'), evidence: {
    ...ownedScope,
    observedAt: new Date(now).toISOString(), source: 'native', complete: true,
    queueChecked: true, historyChecked: true, capabilitiesVerified: true,
    capabilities: ['general'], sessions: [{ id, ownershipVerified: true }],
  } };
  assert.throws(() => prepareEvent({ role: 'consumer', workerId: 'mini', control: baseControl }, request, { state }, {
    sessions: { old: { assignmentId: 'old', sessionId: id } },
  }, now), /Resumed terminal/);
});

test('round gates persist through uncertainty, wrong role/generation and changed replay fail', () => {
  let state = withRounds();
  assert.throws(() => advance(state, event('begin-round', 'coordinator', { invocationDigest: digest('other') }, 'mini', 'another-round')), /already/);
  state = reserve(ready(state), 1);
  const publish = event('publish', 'coordinator', binding(state, 1));
  state = advance(state, publish);
  assert.equal(advance(state, publish), state);
  assert.throws(() => advance(state, { ...publish, data: { ...publish.data, generation: 2 } }), /reused/);
  assert.throws(() => advance(state, event('release', 'consumer', { ...binding(state, 1), terminalEvidenceDigest: digest('x') })), /Only coordinator/);
  assert.throws(() => advance(state, event('receipt', 'consumer', { ...binding(state, 1), status: 'starting', correlation: 'c1', evidenceDigest: digest('x') }, 'windows')), /binding/);
  for (const status of ['starting', 'uncertain']) state = advance(state, event('receipt', 'consumer', { ...binding(state, 1), status, correlation: 'c1', evidenceDigest: digest(status) }));
  assert.throws(() => advance(state, event('receipt', 'consumer', { ...binding(state, 1), status: 'starting', correlation: 'c1', evidenceDigest: digest('x') })), /redispatch/);
  assert.throws(() => advance(state, event('release', 'coordinator', { ...binding(state, 1), terminalEvidenceDigest: digest('x') })), /terminal/);
  assert.equal(state.assignments['assignment-1'].state, 'uncertain');
});

test('only fresh reconciled terminal receipt releases a reservation', () => {
  let state = reserve(ready(withRounds()), 1);
  state = advance(state, event('publish', 'coordinator', binding(state, 1)));
  for (const status of ['starting', 'running', 'review', 'recovery', 'terminal-reported']) {
    state = advance(state, event('receipt', 'consumer', { ...binding(state, 1), status, correlation: 'opaque', evidenceDigest: digest(status) }));
  }
  assert.equal(state.assignments['assignment-1'].state, 'terminal-reported');
  state = ready(state);
  state = advance(state, event('release', 'coordinator', { ...binding(state, 1), terminalEvidenceDigest: digest('verified') }));
  assert.equal(state.assignments['assignment-1'].state, 'terminal');
  assert.throws(() => reserve(ready(state), 1), /reuse/);
});

test('terminal evidence can be refreshed next round without restarting or releasing stale work', () => {
  let state = reserve(ready(withRounds()), 1);
  state = advance(state, event('publish', 'coordinator', binding(state, 1)));
  for (const status of ['starting', 'terminal-reported']) {
    state = advance(state, event('receipt', 'consumer', {
      ...binding(state, 1), status, correlation: 'opaque', evidenceDigest: digest(status),
    }));
  }
  const later = now + 120_000;
  const atLater = (value) => ({ ...value, observedAt: new Date(later).toISOString() });
  const refreshReady = () => applyEvent(state, atLater(event('ready', 'consumer', {
    inventoryDigest: digest('fresh inventory'), assignmentInventoryDigest: inventoryDigest(state, 'mini'),
    unassignedSessions: 0, capabilities: ['general'],
  })), { now: later });
  state = refreshReady();
  const release = atLater(event('release', 'coordinator', {
    ...binding(state, 1), terminalEvidenceDigest: digest('fresh reconciliation'),
  }));
  assert.throws(() => applyEvent(state, release, { now: later }), /Fresh/);
  for (const status of ['starting', 'running']) {
    assert.throws(() => applyEvent(state, atLater(event('receipt', 'consumer', {
      ...binding(state, 1), status, correlation: 'opaque', evidenceDigest: digest('not terminal'),
    })), { now: later }), /transition/);
  }
  state = applyEvent(state, atLater(event('receipt', 'consumer', {
    ...binding(state, 1), status: 'terminal-reported', correlation: 'opaque', evidenceDigest: digest('fresh terminal evidence'),
  })), { now: later });
  state = refreshReady();
  state = applyEvent(state, release, { now: later });
  assert.equal(state.assignments['assignment-1'].state, 'terminal');
});

test('exact committed-event replay survives freshness expiry but stale new events do not', () => {
  const begin = event('begin-round', 'coordinator', { invocationDigest: digest('current') });
  const state = advance(initialState(registry), begin);
  assert.equal(applyEvent(state, begin, { now: now + 120_000 }), state);
  assert.throws(() => applyEvent(state, { ...begin, data: { invocationDigest: digest('changed') } },
    { now: now + 120_000 }), /reused/);
  assert.throws(() => applyEvent(state, { ...begin, id: 'new-stale-event' },
    { now: now + 120_000 }), /Fresh/);
});

test('classification derives conservative scope; registry cannot multiply capacity or contain paths', () => {
  assert.equal(taskFromEvidence({ ...evidence(), classificationComplete: false }).category, 'mobile');
  assert.equal(taskFromEvidence({ ...evidence(), files: ['mobile/Thing.swift'] }).category, 'mobile');
  assert.equal(taskFromEvidence({ ...evidence(), labels: ['iOS'] }).category, 'mobile');
  assert.throws(() => taskFromEvidence({ ...evidence(), files: [] }), /Complete/);
  assert.throws(() => taskFromEvidence({ ...evidence(), files: ['../private'] }), /Complete/);
  assert.throws(() => validateRegistry({ ...registry, workers: [...registry.workers, registry.workers[0]] }), /Exactly/);
  assert.throws(() => validateRegistry({ ...registry, privatePath: '/home/user' }), /Unexpected/);
});

test('private repo identity, actual principal and permission checks fail closed', async () => {
  for (const change of [{ private: false }, { visibility: 'public' }, { id: 456 }, { full_name: 'other/repo' }, { permissions: { push: false } }, { archived: true }]) {
    const f = githubFixture();
    Object.assign(f.metadata, change);
    await assert.rejects(verifyControlRepository(baseControl, f.api), /Wrong\/public/);
  }
  const f = githubFixture();
  await assert.rejects(verifyControlRepository({ ...baseControl, registry: { ...registry, writers: ['other-user'] } }, f.api), /principal/);
  await assert.rejects(verifyControlRepository(baseControl, async (endpoint) => endpoint.includes('permission') ? { permission: 'read' } : f.api(endpoint)), /permission/);
  assert.equal(f.calls.filter((call) => call.method !== 'GET').length, 0);
});

test('empty private repository initialization is create-only and pins actual root commit', async () => {
  const f = githubFixture();
  const result = await initializeMailbox(baseControl, digest('migration-evidence'), f.api);
  const config = { ...baseControl, genesisSha: result.genesisSha };
  assert.equal((await readMailbox(config, f.api)).state.sequence, 0);
  assert.equal(result.activationAuthorized, false);
  await assert.rejects(initializeMailbox(config, digest('x'), f.api), /Explicit new/);
  await assert.rejects(initializeMailbox(baseControl, digest('x'), f.api), /existing ref/);
  assert.equal(f.calls.find((call) => call.method === 'PUT').body.sha, undefined);
});

test('single-parent publication rejects sibling races, recovers lost ACK and replays idempotently', async () => {
  const f = githubFixture();
  const { genesisSha } = await initializeMailbox(baseControl, digest('migration'), f.api);
  const config = { ...baseControl, genesisSha };
  const first = event('begin-round', 'coordinator', { invocationDigest: digest('coordinator') });
  const other = event('begin-round', 'consumer', { invocationDigest: digest('consumer') });
  f.race(() => publishEvent(config, other, f.api));
  await assert.rejects(publishEvent(config, first, f.api), /conflicted/);
  assert.equal((await readMailbox(config, f.api)).state.sequence, 1);
  f.loseResponse();
  const result = await publishEvent(config, first, f.api);
  assert.equal(result.acknowledgementRecovered, true);
  const writes = f.calls.filter((call) => call.method !== 'GET').length;
  assert.equal((await publishEvent(config, first, f.api)).replayed, true);
  assert.equal(f.calls.filter((call) => call.method !== 'GET').length, writes);
  assert.equal(result.state.sequence, 2);
  f.refs.set('heads/main', genesisSha);
  await assert.rejects(readMailbox(config, f.api, result), /checkpoint/);
});

test('malformed queue content, privacy fields and forged history are rejected', async () => {
  const f = githubFixture();
  const { genesisSha } = await initializeMailbox(baseControl, digest('migration'), f.api);
  const config = { ...baseControl, genesisSha };
  await assert.rejects(publishEvent(config, { ...event('begin-round', 'coordinator', { invocationDigest: digest('x') }), sessionId: 'private' }, f.api), /Unexpected/);
  const forged = f.putRecord({ previousStateDigest: digest('not-state'), event: event('begin-round', 'coordinator', { invocationDigest: digest('x') }) }, [genesisSha]);
  f.refs.set('heads/main', forged);
  await assert.rejects(readMailbox(config, f.api), /discontinuous/);
});

test('oversized records fail before writing Git objects or advancing the mailbox', async () => {
  const f = githubFixture();
  const hugeRegistry = structuredClone(registry);
  hugeRegistry.workers[0].capabilities = Array.from({ length: 18_000 }, (_, index) =>
    `capability-${index}`.padEnd(64, 'x'));
  await assert.rejects(initializeMailbox({ ...baseControl, registry: hugeRegistry }, digest('migration'), f.api), /exceeds 1 MiB/);
  assert.equal(f.calls.some((call) => call.method !== 'GET'), false);

  const { genesisSha } = await initializeMailbox(baseControl, digest('migration'), f.api);
  const config = { ...baseControl, genesisSha };
  let snapshot;
  for (const [role, worker] of [['coordinator', 'mini'], ['consumer', 'mini']]) {
    snapshot = await publishEvent(config, event('begin-round', role, { invocationDigest: digest(role) }, worker), f.api);
  }
  await publishEvent(config, event('ready', 'consumer', {
    inventoryDigest: digest('inventory'), assignmentInventoryDigest: inventoryDigest(snapshot.state, 'mini'),
    unassignedSessions: 0, capabilities: ['general'],
  }), f.api);
  const previousHead = f.refs.get(config.ref);
  const writes = f.calls.filter((call) => call.method !== 'GET').length;
  await assert.rejects(publishEvent(config, event('reserve', 'coordinator', {
    assignmentId: 'large-task', workerId: 'mini', generation: 1,
    task: taskFromEvidence({ ...evidence(), files: Array.from({ length: 17_000 }, (_, index) => `src/file-${index}`) }),
    eligibilityDigest: digest('eligibility'), policySha: 'b'.repeat(40),
  }), f.api), /exceeds 1 MiB/);
  assert.equal(f.refs.get(config.ref), previousHead);
  assert.equal(f.calls.filter((call) => call.method !== 'GET').length, writes);
  assert.equal((await readMailbox(config, f.api)).head, previousHead);
});

test('publication and native kickoff recheck closed, personally assigned and blocked issues', () => {
  const reserved = reserve(ready(withRounds()), 1);
  const published = advance(reserved, event('publish', 'coordinator', binding(reserved, 1)));
  for (const [type, role, state] of [['publish', 'coordinator', reserved], ['receipt', 'consumer', published]]) {
    for (const change of [
      { issueState: 'closed' }, { githubAssignees: ['someone'] },
      { labels: [...evidence().labels, 'status:blocked'] },
    ]) {
      assert.throws(() => prepareEvent({ role, workerId: 'mini', control: baseControl }, {
        ...event(type, role, { ...binding(state, 1), ...(type === 'receipt' ? { status: 'starting', correlation: 'opaque' } : {}) }),
        evidence: { ...evidence(), observedAt: new Date(now).toISOString(), source: 'live GitHub',
          holdsChecked: true, ownershipReconciled: true, ...change },
      }, { state }, { sessions: {} }, now), /open|personally|hold/);
    }
  }
});

test('consumer delivery intent requires exact live task; session mapping cannot be replaced', () => {
  let state = reserve(ready(withRounds()), 1);
  state = advance(state, event('publish', 'coordinator', binding(state, 1)));
  const journal = { sessions: {} };
  const config = { role: 'consumer', workerId: 'mini', control: baseControl };
  const request = { ...event('receipt', 'consumer', { ...binding(state, 1), status: 'starting', correlation: 'opaque' }),
    evidence: { ...evidence(), observedAt: new Date(now).toISOString(), source: 'native/live', holdsChecked: true, ownershipReconciled: true } };
  assert.equal(prepareEvent(config, request, { state }, journal, now, undefined, { version: 1 }).createAllowed, true);
  assert.equal(prepareEvent(config, request, { state }, journal, now).createAllowed, false);
  assert.throws(() => prepareEvent(config, { ...request, evidence: { ...request.evidence, title: 'Changed task' } }, { state }, journal, now), /changed/);
  assert.throws(() => prepareEvent(config, { ...request, id: 'different-event' }, { state }, journal, now), /delivery intent/);
  assert.equal(JSON.stringify(state).includes('sessionId'), false);
});

test('native runtime blocks unverified migration and unaccepted trust without queue writes', async () => {
  const f = githubFixture();
  const config = { control: baseControl, role: 'consumer', workerId: 'mini', host: 'macos-mobile' };
  await assert.rejects(runNativeRequest(config, {}, { api: f.api }), /Attested/);
  await assert.rejects(runNativeRequest({
    ...config, verified: true, migrationAttested: true, approvedPolicy: 'a'.repeat(40),
  }, { approvedPolicy: 'a'.repeat(40) }, { api: f.api }), /Attested/);
  assert.equal(f.calls.length, 0);
});

test('native role contract separates central triage from local consumers and documents research exit', async () => {
  const contract = await readFile('.copilot/skills/ralph-loop/native-roles.md', 'utf8');
  assert.doesNotMatch(contract, /open_pr_session|via the PR-session tool/);
  assert.match(contract, /exactly `dispatchPlan.nativeTool` and `dispatchPlan.nativeArguments`/);
  for (const phrase of [
    'coordinator alone scans all issues/PRs', 'Consumers do not globally triage',
    'go:needs-research', 'go:no', 'research-PR/implementation-issue',
    'Never close', 'appropriate Squad owner', 'normal category capacity',
    'source-only', 'nativeCreateAllowed:true', 'lost response',
    'never serialized into the queue', 'before **any** triage-label',
    "create_session tool's kickoff model/effort catalog",
    'single-model list is not evidence',
    '`data.taskDigest`',
  ]) assert.ok(contract.includes(phrase), phrase);
});

test('runtime fsyncs local intent, validates actual session and never reauthorizes duplicate creation', async (t) => {
  const currentEvent = (...args) => ({ ...event(...args), observedAt: new Date().toISOString() });
  const f = githubFixture();
  const { genesisSha } = await initializeMailbox(baseControl, digest('migration'), f.api);
  const control = { ...baseControl, genesisSha };
  const root = await mkdtemp(path.join(await realpath(os.tmpdir()), 'ralph native '));
  t.after(() => rm(root, { recursive: true, force: true }));
  const workflowId = 'aaaaaaaa-1111-4222-8333-444444444444', projectId = 'bbbbbbbb-1111-4222-8333-444444444444';
  const observedAt = new Date().toISOString();
  const config = { control, role: 'consumer', workerId: 'mini', host: 'macos-mobile',
    workflowId, projectId, worktreeRoot: '/worktrees', executionTrust: 'local-owner-v1', verified: true, migrationAttested: true, approvedPolicy: 'a'.repeat(40),
    stateDirectory: path.join(root, 'native-state') };
  const dependencies = { api: f.api, preflight: async () => ({ localContext: { worktreePath: '/worktree', gitDirectory: '/git/worktrees/one' } }) };
  const request = {
    type: 'begin-round', id: 'runtime-begin', roundId: 'native-round', approvedPolicy: config.approvedPolicy,
    hostConfigPath: path.join(root, 'host.json'),
  };
  request.roundToken = (await runNativeRequest(config, request, dependencies)).roundToken;
  await runNativeRequest(config, { ...request, type: 'ready', id: 'runtime-ready', evidence: {
    ...ownedScope,
    observedAt, source: 'native inventory', complete: true, queueChecked: true,
    historyChecked: true, capabilitiesVerified: true, capabilities: ['general', 'ios'], sessions: [],
  } }, dependencies);
  await publishEvent(control, currentEvent('begin-round', 'coordinator', { invocationDigest: digest('coordinator') }), f.api);
  await publishEvent(control, currentEvent('reserve', 'coordinator', {
    assignmentId: 'assignment-1', workerId: 'mini', generation: 1, task: taskFromEvidence(evidence()),
    eligibilityDigest: digest('eligibility'), policySha: config.approvedPolicy,
  }), f.api);
  const state = (await readMailbox(control, f.api)).state;
  await publishEvent(control, currentEvent('publish', 'coordinator', binding(state, 1)), f.api);
  await runNativeRequest(config, { ...request, type: 'ready', id: 'runtime-admission-ready', evidence: {
    ...ownedScope,
    observedAt, source: 'native inventory after publication', complete: true, queueChecked: true,
    historyChecked: true, capabilitiesVerified: true, capabilities: ['general', 'ios'], sessions: [],
  } }, dependencies);
  const start = { ...request, id: 'native-start', type: 'receipt',
    data: { ...binding(state, 1), status: 'starting', correlation: 'correlation-1' },
    evidence: { ...evidence(), observedAt, source: 'native/GitHub', holdsChecked: true, ownershipReconciled: true } };
  const started = await runNativeRequest(config, start, dependencies);
  assert.equal(started.nativeCreateAllowed, true);
  assert.equal((await runNativeRequest(config, start, dependencies)).nativeCreateAllowed, false);
  const journal = JSON.parse(await readFile(path.join(root, 'native-state/journal.json'), 'utf8'));
  assert.equal(journal.sessions['correlation-1'].startEventId, 'native-start');
  const workerSession = 'dddddddd-1111-4222-8333-444444444444';
  const running = { ...request, id: 'native-running', type: 'receipt',
    data: { ...binding(state, 1), status: 'running', correlation: 'correlation-1' },
    evidence: { observedAt, source: 'native readback', session: { id: workerSession, projectId, worktreePath: '/worktrees/task' },
      assignmentCorrelation: 'correlation-1', repository: 'OlyForge3D/PrintFarmer', nativeReadbackVerified: true, kickoffDeliveryVerified: true } };
  running.evidence.continuationAck = await fixtureStartup(config, request, started.dispatchPlan,
    start.data, running.evidence.session, dependencies);
  await runNativeRequest(config, running, dependencies);
  assert.equal(JSON.stringify((await readMailbox(control, f.api)).state).includes(workerSession), false);
  await assert.rejects(runNativeRequest(config, { ...running, id: 'replacement',
    evidence: { ...running.evidence, session: { id: 'eeeeeeee-1111-4222-8333-444444444444' } } }, dependencies), /mapping/);
  const ended = { ...request, id: 'native-ended', type: 'end-round' };
  await runNativeRequest(config, ended, dependencies);
  const later = Date.now() + 120_000;
  const writes = f.calls.filter((call) => call.method !== 'GET').length;
  for (const original of [start, running, ended]) {
    const replay = { ...original };
    const result = await runNativeRequest(config, replay, { ...dependencies, now: later });
    assert.equal(result.replayed, true);
    assert.equal(result.nativeCreateAllowed, false);
    await assert.rejects(runNativeRequest(config, { ...replay, data: { ...replay.data, changed: true } },
      { ...dependencies, now: later }), /changed content/);
  }
  assert.equal(f.calls.filter((call) => call.method !== 'GET').length, writes);
  await mkdir(path.join(root, 'native-state/journal.lock'));
  await assert.rejects(runNativeRequest(config, { ...request, type: 'inspect' }, dependencies), /Unsafe|lock/);
});

async function fixtureStartup(config, round, plan, data, session, dependencies) {
  session = { ...session, branch: session.branch ?? 'fixture-worker' };
  const evidence = {
    observedAt: new Date(dependencies.now ?? Date.now()).toISOString(), source: 'fixture native successful create and ACK',
    session, repository: 'OlyForge3D/PrintFarmer', nativeReadbackVerified: true,
    dispatchPlanDigest: plan.planDigest,
  };
  const run = (type, extra) => runNativeRequest(config, {
    ...round, type, id: `startup-${++next}`, data, evidence: { ...evidence, ...extra },
  }, dependencies);
  await run('record-creation', {
    creationHandle: session.id, creationOutcome: 'succeeded',
    createRequestDigest: digest(plan.nativeArguments), kickoffAccepted: true,
  });
  const ack = {
    configuration: { source: 'successful-native-create', model: plan.packet.model, reasoningEffort: plan.packet.reasoningEffort },
    startupAck: { ...plan.packet, substantiveWorkStarted: false, noChildren: true, actualModel: plan.packet.model,
      initialHeadSha: plan.packet.headSha, actualBranch: session.branch },
  };
  const allowed = await run('startup-check', ack);
  assert.equal(allowed.continuationAllowed, true);
  assert.equal(allowed.continuation, plan.continuation);
  assert.equal((await run('startup-check', ack)).continuationAllowed, false);
  return { ...plan.packet, substantiveWorkStarted: true };
}

async function runtimeFixture(t) {
  const root = await mkdtemp(path.join(await realpath(os.tmpdir()), 'ralph role lifecycle '));
  t.after(() => rm(root, { recursive: true, force: true }));
  const github = githubFixture();
  const configFor = (role, workerId = 'mini') => ({
    control: structuredClone(baseControl), role, workerId,
    host: workerId === 'mini' ? 'macos-mobile' : 'windows-general',
    workflowId: 'aaaaaaaa-1111-4222-8333-444444444444',
    projectId: 'bbbbbbbb-1111-4222-8333-444444444444',
    worktreeRoot: '/worktrees', verified: true, migrationAttested: true,
    executionTrust: 'local-owner-v1', approvedPolicy: 'a'.repeat(40),
    stateDirectory: path.join(root, `${role}-${workerId}`, 'native-state'),
  });
  const localContext = { worktreePath: '/worktrees/one', gitDirectory: '/git/worktrees/one' };
  const dependencies = { api: github.api, preflight: async () => ({ localContext }), now };
  const request = (config, type, extra = {}) => ({
    type, id: `runtime-${++next}`, roundId: `${config.role}-${config.workerId}-${next}`,
    approvedPolicy: config.approvedPolicy, hostConfigPath: path.join(path.dirname(config.stateDirectory), 'host.json'),
    ...extra,
  });
  const fresh = (extra = {}) => ({ observedAt: new Date(now).toISOString(), source: 'fixture: retained tool observations and controlled delivery log', ...extra });
  const coordinator = configFor('coordinator');
  const initialize = request(coordinator, 'initialize', {
    explicitInitializationApproval: true,
    evidence: fresh({ legacyAuthoritiesReconciled: true, cessationOrFencedHandoffProven: true }),
  });
  return { github, configFor, coordinator, request, fresh, initialize, dependencies, localContext };
}

for (const lineageMode of ['record-lineage', 'automatic-ready']) {
test(`two research lifecycles retain ${lineageMode} ancestry, deliver findings and replenish capacity`, async (t) => {
  const f = await runtimeFixture(t);
  const { coordinator, dependencies, fresh } = f;
  coordinator.control.genesisSha = (await runNativeRequest(coordinator, f.initialize, dependencies)).genesisSha;
  const consumer = f.configFor('consumer');
  consumer.control.genesisSha = coordinator.control.genesisSha;
  const acquire = async (config) => {
    const request = f.request(config, 'begin-round');
    return { ...request, roundToken: (await runNativeRequest(config, request, dependencies)).roundToken };
  };
  const run = (config, round, type, extra = {}) => runNativeRequest(config, {
    ...round, type, id: `research-cycle-${++next}`, ...extra,
  }, dependencies);
  const sessions = [], nativeCalls = [];
  const creatorId = 'aaaaaaaa-2222-4333-8444-555555555555';
  if (lineageMode === 'automatic-ready') sessions.push(roleSession(consumer, creatorId));
  const inventory = () => fresh({ ...ownedScope, complete: true, queueChecked: true, historyChecked: true,
    capabilitiesVerified: true, capabilities: ['general', 'ios'], sessions });
  for (const issue of [100, 101]) {
    const c = await acquire(coordinator), w = await acquire(consumer);
    if (issue === 100 && lineageMode === 'record-lineage') {
      await run(consumer, w, 'record-lineage', { evidence: fresh({
        observations: [{ session: { id: creatorId }, nativeReadbackVerified: true,
          observedAt: new Date(now - 86_400_000).toISOString(), source: 'retained native creator readback' }],
      }) });
    } else if (issue === 101) {
      const inspected = await run(consumer, w, 'inspect');
      assert.equal(inspected.retainedLineage[creatorId].source,
        lineageMode === 'record-lineage' ? 'retained native creator readback' : inventory().source);
      assert.equal(inspected.retainedLineage[creatorId].observedAt,
        new Date(lineageMode === 'record-lineage' ? now - 86_400_000 : now).toISOString());
    }
    await run(consumer, w, 'ready', { evidence: inventory() });
    if (issue === 100 && lineageMode === 'automatic-ready') sessions.splice(0, 1);
    const taskEvidence = fresh({ ...evidence(issue), labels: ['squad:dallas', 'go:needs-research', 'type:bug', 'priority:p1'],
      claimsReconciled: true, holdsChecked: true, dependenciesReady: true, epicChildrenReady: true,
      analysisReady: true, reviewGatesChecked: true, ownershipReconciled: true });
    const reserved = await run(coordinator, c, 'reserve', {
      data: { assignmentId: `assignment-${issue}`, workerId: 'mini' }, evidence: taskEvidence,
    });
    const bound = binding(reserved.state, issue), correlation = `delivery-${issue}`;
    assert.equal(reserved.state.assignments[bound.assignmentId].task.category, 'general');
    assert.equal(reserved.state.availability.mini.remaining.mobile, 1);
    await run(coordinator, c, 'publish', { data: bound, evidence: taskEvidence });
    const data = { ...bound, correlation };
    const preview = await run(consumer, w, 'dispatch-plan', { data, evidence: taskEvidence });
    assert.equal(preview.nativeCreateAllowed, false);
    await run(consumer, w, 'ready', { evidence: inventory() });
    const start = await run(consumer, w, 'receipt', { data: { ...data, status: 'starting' }, evidence: taskEvidence });
    assert.equal(start.nativeCreateAllowed, true);
    const plan = start.dispatchPlan;
    assert.deepEqual(plan, preview.dispatchPlan);
    nativeCalls.push({ tool: plan.nativeTool, arguments: plan.nativeArguments });
    const session = { id: `cccccccc-1111-4222-8333-444444444${issue}`,
      projectId: consumer.projectId, worktreePath: `/worktrees/research-${issue}`, branch: `research-${issue}` };
    await run(consumer, w, 'record-creation', { data, evidence: fresh({
      session, repository: 'OlyForge3D/PrintFarmer', nativeReadbackVerified: true,
      creationHandle: session.id, creationOutcome: 'succeeded', dispatchPlanDigest: plan.planDigest,
      createRequestDigest: digest(plan.nativeArguments), kickoffAccepted: true,
    }) });
    const premature = fresh({ session: { ...session, terminalVerified: true },
      repository: 'OlyForge3D/PrintFarmer', nativeReadbackVerified: true, assignmentCorrelation: correlation,
      queueChecked: true, historyChecked: true, artifactsVerified: true,
      noPendingContinuation: true, noFutureDelivery: true, finalDeliveryCorrelation: `final-${issue}` });
    await assert.rejects(run(consumer, w, 'receipt', {
      data: { ...data, status: 'terminal-reported' }, evidence: premature,
    }), /acknowledged startup/);
    await run(consumer, w, 'receipt', { data: { ...data, status: 'uncertain' }, evidence: premature });
    await assert.rejects(run(consumer, w, 'receipt', {
      data: { ...data, status: 'terminal-reported' }, evidence: premature,
    }), /acknowledged startup/);
    const continuationAck = await fixtureStartup(consumer, w, plan, data, session, dependencies);
    const delivered = fresh({ session, assignmentCorrelation: correlation, repository: 'OlyForge3D/PrintFarmer',
      nativeReadbackVerified: true, kickoffDeliveryVerified: true, continuationAck });
    await assert.rejects(run(consumer, w, 'receipt', {
      data: { ...data, status: 'running' }, evidence: { ...delivered,
        continuationAck: { ...continuationAck, policySha: 'b'.repeat(40) } },
    }), /Packet ACK/);
    await run(consumer, w, 'receipt', { data: { ...data, status: 'running' }, evidence: delivered });
    const artifactUrl = `https://github.com/OlyForge3D/PrintFarmer/issues/${issue}#issuecomment-123`;
    const commentEndpoint = 'repos/OlyForge3D/PrintFarmer/issues/comments/123';
    const body = `Concrete findings for issue ${issue}.\n`;
    f.github.comments.set(commentEndpoint, { id: 123, html_url: artifactUrl,
      issue_url: `https://api.github.com/repos/OlyForge3D/PrintFarmer/issues/${issue}`, body });
    const readback = await run(consumer, w, 'artifact-readback', { data: { ...data, artifactUrl }, evidence: fresh() });
    assert.equal(readback.dispatchAuthorized, false);
    const artifact = readback.artifact;
    const finalDeliveryCorrelation = readback.finalDeliveryCorrelation;
    assert.match(finalDeliveryCorrelation, /^final-[0-9a-f]{58}$/);
    assert.equal((await run(consumer, w, 'artifact-readback', {
      data: { ...data, artifactUrl }, evidence: fresh(),
    })).finalDeliveryCorrelation, finalDeliveryCorrelation);
    assert.equal(artifact.bodyDigest, createHash('sha256').update(body).digest('hex'));
    const terminalEvidence = { ...delivered, session: { ...session, terminalVerified: true },
      queueChecked: true, historyChecked: true, artifactsVerified: true,
      noPendingContinuation: true, noFutureDelivery: true, finalDeliveryCorrelation,
      finalAck: { ...plan.packet, noChildren: true, noPendingContinuation: true,
        noFutureDelivery: true, finalDeliveryCorrelation } };
    await assert.rejects(run(consumer, w, 'receipt', {
      data: { ...data, status: 'terminal-reported' }, evidence: terminalEvidence,
    }), /read-back issue findings/);
    const completeEvidence = {
        ...terminalEvidence, artifactReadbackVerified: true, artifact,
        finalAck: { ...plan.packet, artifactUrl: artifact.url, artifactBodyDigest: artifact.bodyDigest,
          artifactReadbackVerified: true, finalDeliveryCorrelation,
          noChildren: true, noPendingContinuation: true, noFutureDelivery: true },
    };
    await assert.rejects(run(consumer, w, 'receipt', {
      data: { ...data, status: 'terminal-reported' },
      evidence: { ...completeEvidence, finalDeliveryCorrelation: `${finalDeliveryCorrelation}x`,
        finalAck: { ...completeEvidence.finalAck, finalDeliveryCorrelation: `${finalDeliveryCorrelation}x` } },
    }), /finalDeliveryCorrelation must be a 1-64 character opaque ID.*same child ACK/);
    for (const ack of [{ artifactBodyDigest: undefined }, { artifactReadbackVerified: false },
      { artifactBodyDigest: createHash('sha256').update(body + '\n').digest('hex') }]) {
      await assert.rejects(run(consumer, w, 'receipt', {
        data: { ...data, status: 'terminal-reported' },
        evidence: { ...completeEvidence, finalAck: { ...completeEvidence.finalAck, ...ack } },
      }), /read-back issue findings/);
    }
    f.github.comments.get(commentEndpoint).body = body + '\n';
    assert.notEqual((await run(consumer, w, 'artifact-readback', {
      data: { ...data, artifactUrl }, evidence: fresh(),
    })).finalDeliveryCorrelation, finalDeliveryCorrelation);
    await assert.rejects(run(consumer, w, 'receipt', {
      data: { ...data, status: 'terminal-reported' }, evidence: completeEvidence,
    }), /artifact bytes changed/);
    f.github.comments.get(commentEndpoint).body = body;
    const terminal = await run(consumer, w, 'receipt', {
      data: { ...data, status: 'terminal-reported' }, evidence: completeEvidence,
    });
    const entry = terminal.state.assignments[bound.assignmentId];
    const settled = await run(coordinator, c, 'release', {
      data: bound, evidence: fresh({ consumerReceiptDigest: entry.receipts.at(-1).evidenceDigest,
        taskDigest: bound.taskDigest, ownershipReconciled: true, artifactsVerified: true, noPendingContinuation: true }),
    });
    assert.equal(settled.state.assignments[bound.assignmentId].state, 'terminal');
    assert.equal(settled.state.availability.mini.remaining.general, 3);
    const disposition = await run(coordinator, c, 'research-plan', {
      evidence: fresh({ ...taskEvidence, findings: {
        summary: 'Root cause confirmed; implementation is still outstanding.',
        acceptanceCriteria: ['Mapper must emit six targets after implementation'],
        remainingResearchBlockers: [], researchQuestionsAnswered: true,
        exitCriteriaMet: true, approvalRequired: false, implementationPlanVerified: true,
        issueCommentUrl: artifact.url, issueEvidenceReadback: true, issueDescriptionUpdated: true,
        repositoryFilesChanged: false, implementationOwner: 'squad:lambert',
        implementationReady: true, implementationBlockers: [],
      } }),
    });
    assert.equal(disposition.action, 'propose-implementation-readiness');
    assert.equal(disposition.issue, issue);
    assert.deepEqual(disposition.removeLabels, ['go:needs-research', 'squad:dallas']);
    assert.deepEqual(disposition.addLabels, ['go:yes', 'squad:lambert']);
    assert.equal(disposition.closeIssue, false);
    assert.equal(disposition.mutationAuthorized, false);
    sessions.push({ ...session, creatorSessionId: creatorId, nativeReadbackVerified: true, terminalVerified: true });
    const renewed = await run(consumer, w, 'ready', { evidence: inventory() });
    assert.deepEqual(renewed.state.availability.mini.remaining, { mobile: 1, general: 4 });
    if (issue === 100) {
      const later = now + 16 * 60_000;
      const cleanupEvidence = (at, extra = {}) => ({ observedAt: new Date(at).toISOString(),
        source: 'fixture get_session, git status/rev-list and gh pr list readback',
        callingSessionId: creatorId, mainCheckoutPath: '/Volumes/data/src/pfarm1', candidates: [{
          sessionId: session.id,
          live: { found: true, name: 'research 100', worktreePath: session.worktreePath, branch: session.branch,
            busy: false, pendingInput: false, agentMerge: false, automation: false },
          worktree: { path: session.worktreePath, branch: session.branch, exists: true, gitPresent: true,
            porcelainEmpty: true, unpushedCommits: 0, commitsAheadOfDevelopment: 0, remoteBranchExists: false },
          prsChecked: true, prs: [], artifactUrl,
        }], ...extra });
      const cleanup = (config, round, type, extra) => runNativeRequest(config, {
        ...round, type, id: `cleanup-${++next}`, data: { sessionId: session.id }, ...extra,
      }, { ...dependencies, now: later });
      await assert.rejects(cleanup(coordinator, c, 'cleanup-plan', { evidence: cleanupEvidence(later) }), /coordinator never deletes/);
      const early = await run(consumer, w, 'cleanup-plan', { evidence: cleanupEvidence(now) });
      assert.deepEqual(early.eligible, []);
      assert.ok(early.retained[0].reasons.includes('settling period has not elapsed'));
      const plan = await cleanup(consumer, w, 'cleanup-plan', { evidence: cleanupEvidence(later) });
      assert.deepEqual(plan.eligible.map((item) => item.sessionId), [session.id]);
      assert.equal(plan.deleteAllowed, false);
      const intent = await cleanup(consumer, w, 'record-deletion-intent', { evidence: cleanupEvidence(later) });
      assert.equal(intent.deleteAllowed, true);
      assert.deepEqual({ tool: intent.nativeTool, arguments: intent.nativeArguments }, { tool: 'delete_item', arguments: { id: session.id } });
      await assert.rejects(cleanup(consumer, w, 'record-deletion-intent', { evidence: cleanupEvidence(later) }), /already pending/);
      const lookup = (notFound, absent) => ({ observedAt: new Date(later).toISOString(), source: 'fixture get_session and worktree stat',
        lookups: [{ id: session.id, notFound }], worktree: { path: session.worktreePath, absent }, deleteOutcome: 'unknown' });
      assert.equal((await cleanup(consumer, w, 'record-deletion-result', { evidence: lookup(true, false) })).pending, true);
      assert.equal((await cleanup(consumer, w, 'record-deletion-result', { evidence: lookup(true, true) })).confirmed, true);
      await assert.rejects(run(consumer, w, 'ready', { evidence: inventory() }), /reappeared/);
      sessions.pop();
      const afterDeletion = await run(consumer, w, 'ready', { evidence: inventory() });
      assert.deepEqual(afterDeletion.state.availability.mini.remaining, { mobile: 1, general: 4 });
    }
    await run(consumer, w, 'end-round');
    await run(coordinator, c, 'end-round');
  }
  assert.equal(nativeCalls.length, 2);
  assert.ok(nativeCalls.every(({ arguments: args }) =>
    args.kickoff.agent === 'Ralph Worker' && args.kickoff.model === 'gpt-6-astra' && args.kickoff.reasoning_effort === 'xhigh'));
  const mailbox = await readMailbox(coordinator.control, f.github.api);
  assert.deepEqual(mailbox.state.rounds, {});
  assert.equal(JSON.stringify(mailbox).includes('charterSha256'), false);
  assert.equal(JSON.stringify(mailbox).includes('/worktrees/'), false);
});
}

test('old-policy prestart proof comes from the consumer journal and is bound to its committed blocker', async (t) => {
  const f = await runtimeFixture(t);
  const { coordinator, dependencies, fresh } = f;
  coordinator.control.genesisSha = (await runNativeRequest(coordinator, f.initialize, dependencies)).genesisSha;
  const consumer = f.configFor('consumer');
  consumer.control.genesisSha = coordinator.control.genesisSha;
  const acquire = async (config) => {
    const request = f.request(config, 'begin-round');
    return { ...request, roundToken: (await runNativeRequest(config, request, dependencies)).roundToken };
  };
  const run = (config, round, type, extra = {}) => runNativeRequest(config, { ...round, type,
    id: `renewal-proof-${++next}`, ...extra }, dependencies);
  let c = await acquire(coordinator), w = await acquire(consumer);
  const inventory = fresh({ ...ownedScope, complete: true, queueChecked: true, historyChecked: true,
    capabilitiesVerified: true, capabilities: ['general', 'ios'], sessions: [] });
  await run(consumer, w, 'ready', { evidence: inventory });
  const taskEvidence = fresh({ ...evidence(), claimsReconciled: true, holdsChecked: true, dependenciesReady: true,
    epicChildrenReady: true, analysisReady: true, reviewGatesChecked: true, ownershipReconciled: true });
  const reserved = await run(coordinator, c, 'reserve', {
    data: { assignmentId: 'assignment-1', workerId: 'mini' }, evidence: taskEvidence,
  });
  const bound = binding(reserved.state, 1);
  await run(coordinator, c, 'publish', { data: bound, evidence: taskEvidence });
  await run(consumer, w, 'end-round'); await run(coordinator, c, 'end-round');
  coordinator.approvedPolicy = consumer.approvedPolicy = 'b'.repeat(40);
  c = await acquire(coordinator); w = await acquire(consumer);
  await assert.rejects(run(consumer, w, 'dispatch-plan', { data: { ...bound, correlation: 'old-task' },
    evidence: taskEvidence }), /Matching policy/);
  const proofRequest = { data: bound, evidence: fresh({ authoritativeJournalRetained: true, protocolOnlyDeliveryAttested: true }) };
  await assert.rejects(run(coordinator, c, 'prestart-proof', proofRequest), /locally owned/);
  const { proof } = await run(consumer, w, 'prestart-proof', proofRequest);
  const withdrawal = { data: bound, evidence: fresh({ claimsReconciled: true, noNativeDeliveryVerified: true, prestartProof: proof }) };
  await assert.rejects(run(coordinator, c, 'withdraw', withdrawal), /committed by its blocker/);
  await run(consumer, w, 'report-blocker', { data: { ...bound, reasonCode: 'task-changed' }, evidence: proof });
  const repeatProof = await run(consumer, w, 'prestart-proof', proofRequest);
  assert.equal(repeatProof.alreadyReported, true);
  assert.deepEqual(repeatProof.proof, proof);
  await assert.rejects(run(coordinator, c, 'withdraw', { ...withdrawal,
    evidence: { ...withdrawal.evidence, prestartProof: { ...proof, workerId: 'windows' } } }), /consumer no-delivery proof/);
  const withdrawn = await run(coordinator, c, 'withdraw', withdrawal);
  assert.equal(withdrawn.state.assignments['assignment-1'].disposition, 'withdrawn-before-delivery');
  const refreshed = await run(consumer, w, 'ready', { evidence: inventory });
  assert.deepEqual(refreshed.state.availability.mini.remaining, { mobile: 1, general: 4 });
  await run(consumer, w, 'end-round'); await run(coordinator, c, 'end-round');
});

test('partial native creation retains the handle before readback and never adopts requested effort as actual', async (t) => {
  const f = await runtimeFixture(t);
  const { coordinator, dependencies, fresh } = f;
  coordinator.control.genesisSha = (await runNativeRequest(coordinator, f.initialize, dependencies)).genesisSha;
  const consumer = f.configFor('consumer');
  consumer.control.genesisSha = coordinator.control.genesisSha;
  const acquire = async (config) => {
    const request = f.request(config, 'begin-round');
    return { ...request, roundToken: (await runNativeRequest(config, request, dependencies)).roundToken };
  };
  const c = await acquire(coordinator), w = await acquire(consumer);
  const run = (config, round, type, extra) => runNativeRequest(config, {
    ...round, type, id: `partial-${++next}`, ...extra,
  }, dependencies);
  const inventory = fresh({ ...ownedScope, complete: true, queueChecked: true, historyChecked: true,
    capabilitiesVerified: true, capabilities: ['general', 'ios'], sessions: [] });
  await run(consumer, w, 'ready', { evidence: inventory });
  const taskEvidence = fresh({ ...evidence(), labels: ['squad:dallas', 'type:bug', 'priority:p1'],
    claimsReconciled: true, holdsChecked: true, dependenciesReady: true, epicChildrenReady: true,
    analysisReady: true, reviewGatesChecked: true, ownershipReconciled: true });
  const incomplete = { ...taskEvidence };
  delete incomplete.classificationComplete;
  await assert.rejects(run(coordinator, c, 'reserve', {
    data: { assignmentId: 'assignment-1', workerId: 'mini' }, evidence: incomplete,
  }), /Explicit scope/);
  const reserved = await run(coordinator, c, 'reserve', {
    data: { assignmentId: 'assignment-1', workerId: 'mini' }, evidence: taskEvidence,
  });
  const bound = binding(reserved.state, 1);
  await run(coordinator, c, 'publish', { data: bound, evidence: taskEvidence });
  await run(consumer, w, 'ready', { evidence: inventory });
  const data = { ...bound, correlation: 'partial-child' };
  const started = await run(consumer, w, 'receipt', {
    data: { ...data, status: 'starting' }, evidence: taskEvidence,
  });
  const plan = started.dispatchPlan;
  const creation = fresh({
    dispatchPlanDigest: plan.planDigest, creationHandle: 'aaaaaaaa-1111-4222-8333-444444444444',
    creationOutcome: 'partial',
  });
  const retained = await run(consumer, w, 'record-creation', { data, evidence: creation });
  assert.equal(retained.reconciliationRequired, true);
  assert.equal(retained.nativeCreateAllowed, false);
  const journal = JSON.parse(await readFile(path.join(consumer.stateDirectory, 'journal.json'), 'utf8'));
  assert.equal(journal.sessions[data.correlation].creationHandle, creation.creationHandle);
  assert.equal(journal.sessions[data.correlation].sessionId, undefined);
  const session = { id: creation.creationHandle, projectId: consumer.projectId, worktreePath: '/worktrees/partial', branch: 'partial-worker' };
  const readback = { ...creation, session, repository: 'OlyForge3D/PrintFarmer', nativeReadbackVerified: true };
  await run(consumer, w, 'record-creation', { data, evidence: readback });
  const alias = { ...session, id: 'bbbbbbbb-1111-4222-8333-444444444444' };
  await assert.rejects(run(consumer, w, 'record-creation', {
    data, evidence: { ...readback, session: alias },
  }), /Retain original/);
  await run(consumer, w, 'record-creation', {
    data, evidence: { ...readback, session: alias, resolvedCreationHandle: creation.creationHandle },
  });
  const ack = {
    ...readback, session: alias,
    startupAck: { ...plan.packet, substantiveWorkStarted: false, noChildren: true, actualModel: 'gpt-6-astra',
      initialHeadSha: plan.packet.headSha, actualBranch: session.branch },
    configuration: { source: 'successful-native-create', model: 'gpt-6-astra', reasoningEffort: 'xhigh' },
  };
  await assert.rejects(run(consumer, w, 'startup-check', { data, evidence: ack }), /Partial startup/);
  await assert.rejects(run(consumer, w, 'startup-check', { data, evidence: {
    ...ack, configuration: { ...ack.configuration, source: 'native-readback', reasoningEffort: 'medium' },
  } }), /model\/effort/);
  const authorized = { ...ack, configuration: { ...ack.configuration, source: 'owner-attestation' } };
  assert.equal((await run(consumer, w, 'startup-check', { data, evidence: authorized })).continuationAllowed, true);
  assert.equal((await run(consumer, w, 'startup-check', { data, evidence: authorized })).continuationAllowed, false);
  await assert.rejects(run(consumer, w, 'receipt', {
    data: { ...data, status: 'starting' }, evidence: taskEvidence,
  }), /delivery intent/);
  await assert.rejects(run(consumer, w, 'prestart-proof', {
    data, evidence: fresh({ authoritativeJournalRetained: true, protocolOnlyDeliveryAttested: true }),
  }), /no delivery intent/);
});

test('manual initializer and repeated coordinator/consumer lifecycles need no current-automation metadata', async (t) => {
  for (const workerId of ['mini', 'windows']) {
    const f = await runtimeFixture(t);
    const { coordinator, dependencies, request, fresh } = f;
    await assert.rejects(runNativeRequest(coordinator, { ...f.initialize, explicitInitializationApproval: false }, dependencies), /explicit owner approval/);
    const initialized = await runNativeRequest(coordinator, f.initialize, dependencies);
    assert.equal(initialized.activationAuthorized, false);
    await assert.rejects(runNativeRequest(coordinator, f.initialize, dependencies), /intent already exists/);
    coordinator.control.genesisSha = initialized.genesisSha;
    const consumer = f.configFor('consumer', workerId);
    consumer.control.genesisSha = initialized.genesisSha;
    const acquire = async (config) => {
      const begin = request(config, 'begin-round');
      const result = await runNativeRequest(config, begin, dependencies);
      assert.match(result.roundToken, /^[0-9a-f]{64}$/);
      return { ...begin, roundToken: result.roundToken };
    };
    const c = await acquire(coordinator), w = await acquire(consumer);
    const run = (config, round, type, extra = {}) => runNativeRequest(config, {
      ...round, type, id: `lifecycle-${++next}`, ...extra,
    }, dependencies);
    const inventory = (sessions = []) => fresh({
      ...ownedScope,
      complete: true, queueChecked: true, historyChecked: true,
      capabilitiesVerified: true, capabilities: ['general'], sessions,
    });
    await run(consumer, w, 'ready', { evidence: inventory() });
    const taskEvidence = fresh({ ...evidence(), claimsReconciled: true, holdsChecked: true,
      dependenciesReady: true, epicChildrenReady: true, analysisReady: true, reviewGatesChecked: true,
      ownershipReconciled: true });
    await assert.rejects(run(consumer, w, 'reserve', {
      data: { assignmentId: 'unauthorized', workerId }, evidence: taskEvidence,
    }), /Only coordinator/);
    const reserved = await run(coordinator, c, 'reserve', {
      data: { assignmentId: 'assignment-1', workerId }, evidence: taskEvidence,
    });
    const taskBinding = binding(reserved.state, 1);
    await run(coordinator, c, 'publish', { data: taskBinding, evidence: taskEvidence });
    await run(consumer, w, 'ready', { evidence: inventory() });
    const startRequest = { ...w, type: 'receipt', id: `start-${++next}`,
      data: { ...taskBinding, status: 'starting', correlation: 'delivery-one' }, evidence: taskEvidence };
    const started = await runNativeRequest(consumer, startRequest, dependencies);
    assert.equal(started.nativeCreateAllowed, true);
    assert.equal((await runNativeRequest(consumer, startRequest, dependencies)).nativeCreateAllowed, false);
    const sessionId = 'cccccccc-1111-4222-8333-444444444444';
    const session = { id: sessionId, projectId: consumer.projectId, worktreePath: '/worktrees/task' };
    const delivered = fresh({ session, assignmentCorrelation: 'delivery-one',
      repository: 'OlyForge3D/PrintFarmer', nativeReadbackVerified: true, kickoffDeliveryVerified: true });
    delivered.continuationAck = await fixtureStartup(consumer, w, started.dispatchPlan, startRequest.data, session, dependencies);
    await run(consumer, w, 'receipt', {
      data: { ...taskBinding, status: 'running', correlation: 'delivery-one' }, evidence: delivered,
    });
    await assert.rejects(run(consumer, w, 'receipt', {
      data: { ...taskBinding, status: 'terminal-reported', correlation: 'delivery-one' }, evidence: delivered,
    }), /Terminal report/);
    await run(consumer, w, 'end-round');
    await run(coordinator, c, 'end-round');
    const c2 = await acquire(coordinator), w2 = await acquire(consumer);
    assert.notEqual(c.roundToken, c2.roundToken);
    assert.notEqual(w.roundToken, w2.roundToken);
    await assert.rejects(run(consumer, { ...w2, roundToken: w.roundToken }, 'ready', { evidence: inventory() }), /round token/);
    const terminal = await run(consumer, w2, 'receipt', {
      data: { ...taskBinding, status: 'terminal-reported', correlation: 'delivery-one' },
      evidence: { ...delivered, session: { ...session, terminalVerified: true },
        queueChecked: true, historyChecked: true, artifactsVerified: true,
        noPendingContinuation: true, noFutureDelivery: true, finalDeliveryCorrelation: 'delivery-one',
        finalAck: { ...started.dispatchPlan.packet, noChildren: true, noPendingContinuation: true,
          noFutureDelivery: true, finalDeliveryCorrelation: 'delivery-one' } },
    });
    await run(consumer, w2, 'ready', { evidence: inventory([{ id: sessionId, terminalVerified: true }]) });
    const receipt = terminal.state.assignments['assignment-1'].receipts.at(-1);
    const released = await run(coordinator, c2, 'release', {
      data: taskBinding, evidence: fresh({ consumerReceiptDigest: receipt.evidenceDigest,
        taskDigest: taskBinding.taskDigest, ownershipReconciled: true, artifactsVerified: true, noPendingContinuation: true }),
    });
    assert.equal(released.state.assignments['assignment-1'].state, 'terminal');
    await run(consumer, w2, 'end-round');
    await run(coordinator, c2, 'end-round');
    const journal = JSON.parse(await readFile(path.join(consumer.stateDirectory, 'journal.json'), 'utf8'));
    assert.equal(journal.sessions['delivery-one'].sessionId, sessionId);
    assert.equal(JSON.stringify(journal).includes(w.roundToken), false);
    assert.equal(JSON.stringify(released.state).includes('/worktrees'), false);
    assert.equal(JSON.stringify(released.state).includes(sessionId), false);
  }
});

test('same-worktree races, lost acquisition ACK, wrong tokens/context and recovery retain exclusion', async (t) => {
  const f = await runtimeFixture(t);
  const { coordinator: config, dependencies, request, fresh } = f;
  config.control.genesisSha = (await runNativeRequest(config, f.initialize, dependencies)).genesisSha;
  const first = request(config, 'begin-round');
  const competing = request(config, 'begin-round');
  const results = await Promise.allSettled([
    runNativeRequest(config, first, dependencies), runNativeRequest(config, competing, dependencies),
  ]);
  assert.equal(results.filter((result) => result.status === 'fulfilled').length, 1);
  const winnerIndex = results.findIndex((result) => result.status === 'fulfilled');
  const winner = [first, competing][winnerIndex], result = results[winnerIndex].value;
  assert.equal((await runNativeRequest(config, winner, dependencies)).roundToken, undefined);
  await assert.rejects(runNativeRequest(config, request(config, 'begin-round'), dependencies), /already has a round/);
  const end = { ...winner, type: 'end-round', id: `end-${++next}` };
  for (const token of [undefined, 'f'.repeat(64)]) {
    await assert.rejects(runNativeRequest(config, { ...end, roundToken: token }, dependencies), /round token/);
  }
  await assert.rejects(runNativeRequest(config, { ...end, roundToken: result.roundToken }, {
    ...dependencies, preflight: async () => ({ localContext: { ...f.localContext, worktreePath: '/worktrees/other' } }),
  }), /matching worktree/);
  const snapshot = await readMailbox(config.control, f.github.api);
  const recovery = request(config, 'recover-coordinator-round', {
    data: { oldRoundId: winner.roundId },
    evidence: fresh({ priorInvocationDigest: snapshot.state.rounds.coordinator.invocationDigest,
      cessationProven: false, liveChecked: true, queuedChecked: true, historyChecked: true }),
  });
  await assert.rejects(runNativeRequest(config, recovery, dependencies), /cessation/);
  recovery.evidence.cessationProven = true;
  const recovered = await runNativeRequest(config, recovery, dependencies);
  assert.notEqual(recovered.roundToken, result.roundToken);
  await assert.rejects(runNativeRequest(config, { ...end, roundToken: result.roundToken }, dependencies), /persistent role gate/);
  await runNativeRequest(config, { ...recovery, type: 'end-round', id: `recovered-end-${++next}`,
    roundToken: recovered.roundToken, data: {}, evidence: undefined }, dependencies);
});

function ownedInventoryFixture(sessions = []) {
  const state = withRounds(), config = { role: 'consumer', workerId: 'mini', host: 'macos-mobile',
    projectId: 'bbbbbbbb-1111-4222-8333-444444444444', worktreeRoot: '/worktrees', control: baseControl, approvedPolicy: 'a'.repeat(40) };
  const journal = { sessions: {} };
  const request = { ...event('ready', 'consumer'), evidence: {
    ...ownedScope, observedAt: new Date(now).toISOString(), source: 'actual session observation', complete: true,
    queueChecked: true, historyChecked: true, capabilitiesVerified: true, capabilities: ['general'], sessions,
  } };
  return { state, config, journal, request, prepare: () => prepareEvent(config, request, { state }, journal, now) };
}

function roleSession(config, id) {
  return { id, nativeReadbackVerified: true,
    roleObservation: { role: 'consumer', workerId: 'mini', projectId: config.projectId,
      worktreePath: '/worktrees/role', ownerConfiguredRoleVerified: true, noTaskExecutionVerified: true } };
}

test('unrelated busy, idle and other-automation sessions do not block or alter owned capacity evidence', () => {
  const session = { id: 'cccccccc-1111-4222-8333-444444444444', workflowId: 'aaaaaaaa-1111-4222-8333-444444444444' };
  const f = ownedInventoryFixture();
  const empty = f.prepare().event.data;
  f.config.automationWorkflowIds = [session.workflowId];
  f.request.evidence.sessions = [
    { ...session, status: 'busy', projectId: f.config.projectId },
    { id: 'dddddddd-1111-4222-8333-444444444444', status: 'idle', creatorSessionId: session.id },
    { id: 'eeeeeeee-1111-4222-8333-444444444444', roleObservation: {
      workerId: 'independent-acceptance', projectId: f.config.projectId } },
  ];
  assert.deepEqual(f.prepare().event.data, empty);
  const offered = advance(f.state, f.prepare().event);
  assert.equal(offered.availability.mini.remaining.general, 4);
  assert.equal(offered.availability.mini.remaining.mobile, 1);
});

test('explicit scoped completeness and lineage reconciliation are required', () => {
  for (const change of [{ ownershipScope: undefined }, { ownershipScope: 'all-project' }, { lineageChecked: false }]) {
    const f = ownedInventoryFixture();
    Object.assign(f.request.evidence, change);
    assert.throws(f.prepare, /Ralph-owned lineage reconciliation/);
  }
});

test('mapped workers across rounds cannot be omitted or dismissed with unrelated project work', () => {
  const id = 'dddddddd-1111-4222-8333-444444444444';
  const f = ownedInventoryFixture([{ id, ownershipVerified: true }]);
  f.state.assignments.earlier = { workerId: 'mini', state: 'running', correlation: 'previous-round' };
  f.journal.sessions['previous-round'] = { assignmentId: 'earlier', sessionId: id };
  assert.equal(f.prepare().event.data.unassignedSessions, 0);
  f.request.evidence.sessions = [];
  assert.throws(f.prepare, /Every retained Ralph native mapping/);
  f.request.evidence.sessions = [{ id, ownershipVerified: false }];
  assert.throws(f.prepare, /Every live\/uncertain receipt/);
  f.request.evidence.sessions = [{ id, ownershipVerified: true }];
  f.state.assignments.earlier.workerId = 'windows';
  assert.throws(f.prepare, /unresolved assignment ownership/);
});

test('terminal mappings require fresh readback and cannot conceal resumed workers', () => {
  const id = 'dddddddd-1111-4222-8333-444444444444';
  const f = ownedInventoryFixture();
  f.state.assignments.earlier = { workerId: 'mini', state: 'terminal', correlation: 'previous-round' };
  f.journal.sessions['previous-round'] = { assignmentId: 'earlier', sessionId: id };
  assert.throws(f.prepare, /Every retained Ralph native mapping/);
  f.request.evidence.sessions = [{ id, terminalVerified: true }];
  assert.equal(f.prepare().event.data.unassignedSessions, 0);
  f.request.evidence.sessions[0].terminalVerified = false;
  assert.throws(f.prepare, /Resumed terminal/);
});

test('owned ancestry requires complete acyclic readbacks without adopting unrelated ancestors', () => {
  const id = 'cccccccc-1111-4222-8333-444444444444';
  const parent = 'dddddddd-1111-4222-8333-444444444444';
  const root = 'eeeeeeee-1111-4222-8333-444444444444';
  const sibling = 'ffffffff-1111-4222-8333-444444444444';
  const f = ownedInventoryFixture();
  const session = { ...roleSession(f.config, id), creatorSessionId: parent };
  f.request.evidence.sessions = [session];
  assert.throws(f.prepare, /Missing Ralph-owned native ancestor/);
  f.request.evidence.sessions.push({ id: parent, creatorSessionId: root });
  assert.throws(f.prepare, /Missing Ralph-owned native ancestor/);
  f.request.evidence.sessions.push({ id: root }, { id: sibling, creatorSessionId: root, status: 'busy' });
  const complete = f.prepare().event.data;
  f.request.evidence.sessions.pop();
  assert.deepEqual(f.prepare().event.data, complete);
  f.request.evidence.sessions[2].creatorSessionId = parent;
  assert.throws(f.prepare, /Cyclic Ralph-owned native ancestry/);
  session.creatorSessionId = id;
  assert.throws(f.prepare, /Conflicting retained native ancestry/);
  f.request.evidence.sessions = [{ id: sibling, creatorSessionId: parent, status: 'busy' }];
  assert.equal(f.prepare().event.data.unassignedSessions, 0);
});

test('retired terminal workers require current cessation and retained correlated terminal evidence', () => {
  const id = 'dddddddd-1111-4222-8333-444444444444';
  const terminalEvidenceDigest = digest('retained final delivery ACK');
  const f = ownedInventoryFixture();
  f.state.assignments.earlier = { workerId: 'mini', state: 'terminal', correlation: 'previous-round',
    terminalCommitment: terminalEvidenceDigest,
    receipts: [{ status: 'terminal-reported', correlation: 'previous-round', evidenceDigest: terminalEvidenceDigest }] };
  f.journal.sessions['previous-round'] = { assignmentId: 'earlier', sessionId: id, lastEvidenceDigest: terminalEvidenceDigest };
  const observation = { observedAt: new Date(now).toISOString(), source: 'supported current cessation readback',
    status: 'archived', liveChecked: true, cessationProven: true,
    noPendingContinuation: true, noFutureDelivery: true, terminalEvidenceDigest };
  const session = { id, assignmentCorrelation: 'previous-round', terminalVerified: true, retirementObservation: observation };
  f.request.evidence.sessions = [session];
  for (const status of ['archived', 'deleted']) {
    observation.status = status;
    assert.equal(f.prepare().event.data.unassignedSessions, 0);
  }
  for (const change of [
    { status: 'idle' }, { liveChecked: false }, { cessationProven: false },
    { noPendingContinuation: false }, { noFutureDelivery: false },
    { terminalEvidenceDigest: undefined }, { terminalEvidenceDigest: digest('uncorrelated evidence') },
  ]) {
    session.retirementObservation = { ...observation, ...change };
    assert.throws(f.prepare, /Retired Ralph session needs/);
  }
  session.retirementObservation = { ...observation, observedAt: new Date(now - 60_001).toISOString() };
  assert.throws(f.prepare, /Fresh observations/);
  session.retirementObservation = observation;
  session.terminalVerified = false;
  assert.throws(f.prepare, /Retired Ralph session needs/);
  session.terminalVerified = true;
  f.journal.sessions['previous-round'].lastEvidenceDigest = digest('different retained evidence');
  assert.throws(f.prepare, /Retired Ralph session needs/);
  f.journal.sessions['previous-round'].lastEvidenceDigest = terminalEvidenceDigest;
  f.state.assignments.earlier.receipts[0].evidenceDigest = digest('different mailbox receipt');
  assert.throws(f.prepare, /Retired Ralph session needs/);
  f.state.assignments.earlier.receipts[0].evidenceDigest = terminalEvidenceDigest;
  f.state.assignments.earlier.state = 'running';
  assert.throws(f.prepare, /Retired Ralph session needs/);
  f.request.evidence.sessions = [];
  assert.throws(f.prepare, /Every retained Ralph native mapping/);
});

test('archived ancestry-only creator needs no live worktree or role exemption', () => {
  const f = ownedInventoryFixture();
  const id = 'cccccccc-1111-4222-8333-444444444444';
  const parent = 'dddddddd-1111-4222-8333-444444444444';
  f.state.assignments.earlier = { workerId: 'mini', state: 'terminal' };
  f.journal.sessions['previous-round'] = { assignmentId: 'earlier', sessionId: id };
  const worker = { id, creatorSessionId: parent, assignmentCorrelation: 'previous-round', terminalVerified: true };
  f.request.evidence.sessions = [
    worker,
    { id: parent, archived: true, path: '', nativeReadbackVerified: true },
  ];
  assert.equal(f.prepare().event.data.unassignedSessions, 0);
  worker.terminalVerified = false;
  assert.throws(f.prepare, /Resumed terminal/);
  worker.terminalVerified = true;
  f.request.evidence.sessions.pop();
  assert.equal(f.prepare().event.data.unassignedSessions, 0);
  f.request.evidence.sessions.push({ id: parent, archived: true, path: '', creatorSessionId: id });
  assert.throws(f.prepare, /Conflicting retained native ancestry/);
});

test('a journal-verified deletion retires a settled mapped worker without live evidence and fails closed on reappearance', () => {
  const id = 'dddddddd-1111-4222-8333-444444444444';
  const projectAlias = 'eeeeeeee-1111-4222-8333-444444444444';
  const terminalEvidenceDigest = digest('retained final delivery ACK');
  const f = ownedInventoryFixture();
  f.state.assignments.earlier = { assignmentId: 'earlier', workerId: 'mini', state: 'terminal', correlation: 'previous-round',
    terminalCommitment: terminalEvidenceDigest,
    receipts: [{ status: 'terminal-reported', correlation: 'previous-round', evidenceDigest: terminalEvidenceDigest,
      observedAt: new Date(now - 3_600_000).toISOString() }] };
  f.journal.sessions['previous-round'] = { assignmentId: 'earlier', sessionId: id, lastEvidenceDigest: terminalEvidenceDigest,
    worktreePath: '/worktrees/deleted' };
  const record = { status: 'deleted', sessionId: id, aliases: [projectAlias], correlation: 'previous-round',
    assignmentId: 'earlier', terminalEvidenceDigest, worktreePath: '/worktrees/deleted', inspections: [] };
  assert.throws(f.prepare, /Every retained Ralph native mapping/);
  f.journal.deletions = { [id]: { ...record, status: 'pending' } };
  assert.throws(f.prepare, /Every retained Ralph native mapping/);
  f.journal.deletions[id] = record;
  assert.equal(f.prepare().event.data.unassignedSessions, 0);
  for (const reappeared of [{ id, terminalVerified: true }, { id: projectAlias }, { id,
    retirementObservation: { status: 'deleted' } }]) {
    f.request.evidence.sessions = [reappeared];
    assert.throws(f.prepare, /Deleted Ralph worker .* reappeared/);
  }
  f.request.evidence.sessions = [{ id: 'ffffffff-1111-4222-8333-444444444444', creatorSessionId: id }];
  assert.throws(f.prepare, /Descendant of a deleted Ralph worker/);
  f.request.evidence.sessions = [];
  for (const change of [{ terminalEvidenceDigest: digest('forged') }, { sessionId: projectAlias }, { correlation: 'other' }]) {
    f.journal.deletions[id] = { ...record, ...change };
    assert.throws(f.prepare, /deletion record/);
  }
  f.journal.deletions[id] = record;
  f.state.assignments.earlier.state = 'terminal-reported';
  assert.throws(f.prepare, /deletion record/);
});

test('research artifact hashes parsed JSON body bytes including real newlines and Unicode', async () => {
  const assignment = { task: { issue: 123, purpose: 'research' } };
  const url = 'https://github.com/OlyForge3D/PrintFarmer/issues/123#issuecomment-456';
  const comment = { id: 456, html_url: url,
    issue_url: 'https://api.github.com/repos/OlyForge3D/PrintFarmer/issues/123', body: 'Findings: \u00e9\r\n\n' };
  const api = async (endpoint) => {
    assert.equal(endpoint, 'repos/OlyForge3D/PrintFarmer/issues/comments/456');
    return JSON.parse(JSON.stringify(comment));
  };
  const artifact = await readResearchArtifact(assignment, url, api);
  assert.equal(artifact.bodyBytes, Buffer.byteLength(comment.body, 'utf8'));
  assert.equal(artifact.bodyDigest, createHash('sha256').update(comment.body, 'utf8').digest('hex'));
  for (const body of [undefined, '', ' \n']) {
    await assert.rejects(readResearchArtifact(assignment, url, async () => ({ ...comment, body })), /invalid research artifact/);
  }
  await assert.rejects(readResearchArtifact(assignment, url.replace('/123#', '/124#'), api), /assigned issue/);
  await assert.rejects(readResearchArtifact(assignment, url, async () => ({ ...comment, id: 457 })), /invalid research artifact/);
  await assert.rejects(readResearchArtifact(assignment, url, async () => { throw new Error('GitHub unavailable'); }), /GitHub unavailable/);
});

test('lost creation responses remain owned even before mailbox acceptance is observed', () => {
  const f = ownedInventoryFixture();
  f.state.assignments.pending = { workerId: 'mini', state: 'published' };
  f.journal.sessions.pending = { assignmentId: 'pending', startEventId: 'persisted-create-intent' };
  assert.throws(f.prepare, /creation intent lacks correlated native delivery/);
});

test('owned descendants are transitive and cannot be hidden by idle roles or a terminal ancestor', () => {
  const parent = 'cccccccc-1111-4222-8333-444444444444';
  const child = 'dddddddd-1111-4222-8333-444444444444';
  const grandchild = 'eeeeeeee-1111-4222-8333-444444444444';
  const f = ownedInventoryFixture();
  f.request.evidence.sessions = [
    { id: grandchild, creatorSessionId: child },
    { id: child, creatorSessionId: parent, terminalVerified: true },
    roleSession(f.config, parent),
  ];
  assert.throws(f.prepare, /Unmapped Ralph-owned session or descendant/);
  f.request.evidence.sessions[0].terminalVerified = true;
  assert.equal(f.prepare().event.data.unassignedSessions, 0);
  f.request.evidence.sessions[0].terminalVerified = false;
  f.request.evidence.sessions[2] = { id: parent, terminalVerified: true };
  f.journal.sessions.old = { assignmentId: 'old', sessionId: parent };
  f.state.assignments.old = { workerId: 'mini', state: 'terminal' };
  assert.throws(f.prepare, /Unmapped Ralph-owned session or descendant/);
});

test('role observations exempt only bounded roles, never mapped tasks or conflicting correlations', () => {
  const id = 'cccccccc-1111-4222-8333-444444444444';
  const f = ownedInventoryFixture();
  const session = roleSession(f.config, id);
  f.request.evidence.sessions = [session];
  assert.equal(f.prepare().event.data.unassignedSessions, 0);
  session.roleObservation.noTaskExecutionVerified = false;
  assert.throws(f.prepare, /Unmapped Ralph-owned/);
  session.roleObservation.noTaskExecutionVerified = true;
  session.nativeReadbackVerified = false;
  assert.throws(f.prepare, /actual owner-configured native readback/);
  session.nativeReadbackVerified = true;
  f.state.assignments.old = { workerId: 'mini', state: 'terminal' };
  f.journal.sessions.old = { assignmentId: 'old', sessionId: id };
  assert.throws(f.prepare, /Resumed terminal/);
  session.id = 'dddddddd-1111-4222-8333-444444444444';
  session.assignmentCorrelation = 'old';
  assert.throws(f.prepare, /conflicts with its immutable native mapping/);
});

test('duplicate or malformed native ancestry cannot certify complete owned inventory', () => {
  const id = 'cccccccc-1111-4222-8333-444444444444';
  for (const sessions of [[{ id }, { id }], [{ id, creatorSessionId: 'not-a-native-id' }]]) {
    assert.throws(ownedInventoryFixture(sessions).prepare, /Malformed.*native/);
  }
});

test('cross-package acquisition race preserves sibling history and safely reconciles the impossible losing candidate', async (t) => {
  const f = await runtimeFixture(t);
  const { coordinator, dependencies, request } = f;
  coordinator.control.genesisSha = (await runNativeRequest(coordinator, f.initialize, dependencies)).genesisSha;
  const consumer = f.configFor('consumer');
  consumer.control.genesisSha = coordinator.control.genesisSha;
  const first = request(coordinator, 'begin-round'), sibling = request(consumer, 'begin-round');
  let siblingResult;
  f.github.race(async () => { siblingResult = await runNativeRequest(consumer, sibling, dependencies); });
  await assert.rejects(runNativeRequest(coordinator, first, dependencies), /conflicted/);
  assert.match(siblingResult.roundToken, /^[0-9a-f]{64}$/);
  await assert.rejects(runNativeRequest(coordinator, first, dependencies), /already attempted/);
  const reconcile = { ...first, type: 'abandon-acquisition', data: { acquisitionId: first.id } };
  const writes = f.github.calls.filter((call) => call.method !== 'GET').length;
  const result = await runNativeRequest(coordinator, reconcile, dependencies);
  assert.equal(result.acquisitionAbandoned, true);
  assert.equal(result.roundToken, undefined);
  assert.equal(result.dispatchAuthorized, false);
  assert.equal(f.github.calls.filter((call) => call.method !== 'GET').length, writes);
  const journal = JSON.parse(await readFile(path.join(coordinator.stateDirectory, 'journal.json'), 'utf8'));
  const candidate = journal.roundOwners[first.roundId].publication;
  assert.equal(candidate.baseSha, coordinator.control.genesisSha);
  assert.equal(journal.events[first.id].type, 'begin-round');
  await assert.rejects(f.github.api(`repos/${baseControl.repository}/git/refs/${baseControl.ref}`, 'PATCH', {
    sha: candidate.candidateSha, force: false,
  }), /non-fast-forward/);
  const nextRound = request(coordinator, 'begin-round');
  const acquired = await runNativeRequest(coordinator, nextRound, dependencies);
  assert.match(acquired.roundToken, /^[0-9a-f]{64}$/);
  assert.equal(acquired.state.events[first.id], undefined);
  assert.ok(acquired.state.events[sibling.id]);
  await assert.rejects(runNativeRequest(coordinator, first, dependencies), /already attempted/);
});

test('consumer losing to coordinator can abandon and complete a fresh round without waiting for a schedule', async (t) => {
  const f = await runtimeFixture(t);
  const { coordinator, dependencies, request } = f;
  coordinator.control.genesisSha = (await runNativeRequest(coordinator, f.initialize, dependencies)).genesisSha;
  const consumer = f.configFor('consumer');
  consumer.control.genesisSha = coordinator.control.genesisSha;
  const first = request(consumer, 'begin-round');
  const sibling = request(coordinator, 'begin-round');
  f.github.race(() => runNativeRequest(coordinator, sibling, dependencies));
  await assert.rejects(runNativeRequest(consumer, first, dependencies), /conflicted/);
  const abandoned = await runNativeRequest(consumer, {
    ...first, type: 'abandon-acquisition', data: { acquisitionId: first.id },
  }, dependencies);
  assert.equal(abandoned.acquisitionAbandoned, true);
  assert.equal(abandoned.roundToken, undefined);
  const next = request(consumer, 'begin-round');
  assert.notEqual(next.id, first.id);
  assert.notEqual(next.roundId, first.roundId);
  const acquired = await runNativeRequest(consumer, next, dependencies);
  assert.match(acquired.roundToken, /^[0-9a-f]{64}$/);
  const finished = await runNativeRequest(consumer, {
    ...request(consumer, 'end-round'), roundId: next.roundId, roundToken: acquired.roundToken,
  }, dependencies);
  assert.equal(finished.state.rounds['consumer:mini'], undefined);
  assert.ok(finished.state.rounds.coordinator);
  assert.equal(finished.state.events[first.id], undefined);
  assert.ok(finished.state.events[sibling.id]);
  const journal = JSON.parse(await readFile(path.join(consumer.stateDirectory, 'journal.json'), 'utf8'));
  assert.equal(journal.roundOwners[first.roundId].abandoned, true);
  assert.ok(journal.events[first.id]);
});

test('delayed ref write is not absent evidence; committed and later ended acquisition can never be abandoned', async (t) => {
  const f = await runtimeFixture(t);
  const { coordinator, dependencies, request } = f;
  coordinator.control.genesisSha = (await runNativeRequest(coordinator, f.initialize, dependencies)).genesisSha;
  let delayed;
  const api = async (endpoint, method, body) => {
    if (method === 'PATCH') {
      delayed = () => f.github.api(endpoint, method, body);
      throw new Error('network timeout while server is still processing');
    }
    return f.github.api(endpoint, method, body);
  };
  const begin = request(coordinator, 'begin-round');
  await assert.rejects(runNativeRequest(coordinator, begin, { ...dependencies, api }), /acknowledgement lost/);
  const abandon = { ...begin, type: 'abandon-acquisition', data: { acquisitionId: begin.id } };
  await assert.rejects(runNativeRequest(coordinator, abandon, dependencies), /may still land/);
  await delayed();
  await assert.rejects(runNativeRequest(coordinator, abandon, dependencies), /was published/);
  assert.equal((await runNativeRequest(coordinator, begin, dependencies)).roundToken, undefined);
  await publishEvent(coordinator.control, event('end-round', 'coordinator', {}, 'mini', begin.roundId), f.github.api);
  await assert.rejects(runNativeRequest(coordinator, abandon, dependencies), /was published/);
});

test('pre-ref failure can be reconciled from the durable protocol without deleting intent or accepting fabricated metadata', async (t) => {
  const f = await runtimeFixture(t);
  const { coordinator, dependencies, request } = f;
  coordinator.control.genesisSha = (await runNativeRequest(coordinator, f.initialize, dependencies)).genesisSha;
  const begin = request(coordinator, 'begin-round');
  const api = (endpoint, method, body) => {
    if (method === 'POST' && endpoint.endsWith('/git/trees')) throw new Error('tree creation unavailable');
    return f.github.api(endpoint, method, body);
  };
  const writes = f.github.calls.filter((call) => call.method === 'PATCH').length;
  await assert.rejects(runNativeRequest(coordinator, { ...begin, native: { actual: {} } }, dependencies), /Retired/);
  await assert.rejects(runNativeRequest(coordinator, begin, { ...dependencies, api }), /tree creation/);
  const abandon = { ...begin, type: 'abandon-acquisition', data: { acquisitionId: begin.id } };
  await assert.rejects(runNativeRequest(coordinator, { ...abandon, roundId: 'wrong-round' }, dependencies), /Exact recorded/);
  const reconciled = await runNativeRequest(coordinator, abandon, dependencies);
  assert.equal(reconciled.roundToken, undefined);
  assert.equal(reconciled.acquisitionAbandoned, true);
  assert.equal(f.github.calls.filter((call) => call.method === 'PATCH').length, writes);
  const journal = JSON.parse(await readFile(path.join(coordinator.stateDirectory, 'journal.json'), 'utf8'));
  assert.equal(journal.roundOwners[begin.roundId].abandoned, true);
  assert.equal(journal.roundOwners[begin.roundId].publication, undefined);
  assert.ok(journal.events[begin.id]);
  assert.match((await runNativeRequest(coordinator, request(coordinator, 'begin-round'), dependencies)).roundToken, /^[0-9a-f]{64}$/);
});

test('historical event replay keeps the exact pre-offer state digest', () => {
  const clock = Date.parse('2026-09-01T00:00:00Z');
  let state = initialState(registry), sequence = 0;
  const put = (type, role, data) => {
    state = applyEvent(state, {
      id: `legacy-${++sequence}`, type, authorityId: 'primary', epoch: 1, role,
      ...(role === 'consumer' ? { workerId: 'mini' } : {}), roundId: role,
      observedAt: new Date(clock).toISOString(), data,
    }, { replay: true });
  };
  put('begin-round', 'coordinator', { invocationDigest: digest('coordinator') });
  put('begin-round', 'consumer', { invocationDigest: digest('consumer') });
  const reconcile = () => put('ready', 'consumer', {
    inventoryDigest: digest('inventory'), assignmentInventoryDigest: inventoryDigest(state, 'mini'),
    unassignedSessions: 0, capabilities: ['general', 'ios'],
  });
  reconcile();
  const task = taskFromEvidence({
    issue: 1, headSha: 'a'.repeat(40), title: 'Legacy general task',
    labels: ['squad:copilot', 'type:bug', 'priority:p1'], acceptanceCriteria: ['Inspect only'],
    files: ['src/legacy'], filesComplete: true, scope: 'general', classificationComplete: true,
    capabilities: ['general'],
  });
  put('reserve', 'coordinator', {
    assignmentId: 'legacy-assignment', workerId: 'mini', generation: 1,
    task, eligibilityDigest: digest('eligible'), policySha: 'b'.repeat(40),
  });
  const bound = { assignmentId: 'legacy-assignment', generation: 1, taskDigest: digest(task) };
  put('publish', 'coordinator', bound);
  for (const status of ['starting', 'running', 'terminal-reported']) {
    put('receipt', 'consumer', { ...bound, status, correlation: 'legacy-delivery', evidenceDigest: digest(status) });
  }
  reconcile();
  put('release', 'coordinator', { ...bound, terminalEvidenceDigest: digest('terminal') });
  put('end-round', 'consumer', {});
  put('end-round', 'coordinator', {});
  assert.equal(sequence, 12);
  // Captured by running this history at 1039d637 before the additive protocol.
  assert.equal(digest(state), 'b4a1a28b8433e0d19d0ba48c7dbad43ebae65ae3570d14bb21b30ef62d419fc1');
});

function capacityOffer(state, workerId = 'mini') {
  return event('offer-capacity', 'consumer', {
    inventoryDigest: digest('reconciled local observations'),
    assignmentInventoryDigest: admissionInventoryDigest(state, workerId),
    unassignedSessions: 0, capabilities: registry.workers.find((entry) => entry.workerId === workerId).capabilities,
    policySha: 'b'.repeat(40), previousCapacityDigest: digest(state.availability?.[workerId] ?? {}),
    inventoryObservedAt: new Date(now).toISOString(),
  }, workerId);
}
function offeredReservation(state, issue, workerId = 'mini', mobile = false) {
  return event('reserve', 'coordinator', {
    assignmentId: `assignment-${issue}`, workerId, generation: 1,
    offerId: state.availability[workerId].offerId, policySha: 'b'.repeat(40),
    task: taskFromEvidence(evidence(issue, mobile)), eligibilityDigest: digest('fresh eligibility'),
  });
}

test('one finite offer admits a delayed four-general batch, never borrows, refunds or replays credits', () => {
  let state = withRounds(), clock = now;
  const put = (value) => {
    const observedAt = new Date(clock).toISOString();
    state = applyEvent(state, {
      ...value, observedAt,
      data: value.type === 'offer-capacity' ? { ...value.data, inventoryObservedAt: observedAt } : value.data,
    }, { now: clock });
  };
  const offer = capacityOffer(state);
  put(offer);
  clock += 3 * 60 * 60_000;
  for (let issue = 1; issue <= 4; issue++) {
    put(offeredReservation(state, issue));
    clock += 5 * 60_000;
    put(event('deliver', 'coordinator', binding(state, issue)));
  }
  assert.deepEqual(state.availability.mini.remaining, { mobile: 1, general: 0 });
  assert.throws(() => put(offeredReservation(state, 5)), /exhausted/);
  assert.equal(applyEvent(state, offer, { now: clock }), state);
  put(event('withdraw', 'coordinator', { ...binding(state, 1), reconciliationDigest: digest('never delivered') }));
  assert.equal(state.availability.mini.remaining.general, 0);
  assert.throws(() => put(offeredReservation(state, 5)), /exhausted/);
  const staleReservation = offeredReservation(state, 5);
  const refreshed = capacityOffer(state);
  put(refreshed);
  assert.deepEqual(state.availability.mini.remaining, { mobile: 1, general: 1 });
  assert.throws(() => put(staleReservation), /Current worker capacity offer/);
  put(offeredReservation(state, 5));
  assert.equal(state.availability.mini.remaining.general, 0);
  put(offeredReservation(state, 6, 'mini', true));
  assert.deepEqual(state.availability.mini.remaining, { mobile: 0, general: 0 });
  assert.throws(() => put(offeredReservation(state, 7, 'mini', true)), /exhausted/);
  put(capacityOffer(state, 'windows'));
  assert.throws(() => put(offeredReservation(state, 8, 'windows', true)), /exhausted/);
});

test('offers are bound to worker, registry, policy and exact refresh inventory; revocation retains assignments', () => {
  let state = withRounds();
  state = advance(state, capacityOffer(state));
  const reservation = offeredReservation(state, 1);
  assert.throws(() => advance(state, { ...reservation, data: { ...reservation.data, policySha: 'c'.repeat(40) } }), /policy binding/);
  assert.throws(() => advance(state, { ...reservation, data: { ...reservation.data, workerId: 'windows' } }), /capacity offer/);
  const changedRegistry = structuredClone(state);
  changedRegistry.registry.epoch++;
  assert.throws(() => advance(changedRegistry, { ...reservation, epoch: 2 }), /policy binding/);
  const staleRefresh = capacityOffer(state);
  state = advance(state, reservation);
  assert.throws(() => advance(state, staleRefresh), /Complete reconciled/);
  const beforeRevocation = capacityOffer(state);
  state = advance(state, event('unavailable', 'consumer', {
    reasonCode: 'inventory-unreconciled', evidenceDigest: digest('observed unknown work'),
  }));
  assert.equal(state.availability.mini.revoked, true);
  assert.throws(() => advance(state, beforeRevocation), /Capacity changed/);
  assert.equal(state.assignments['assignment-1'].state, 'reserved');
  assert.throws(() => advance(state, { ...reservation, id: 'new-reservation' }), /reuse/);
  const other = { ...reservation, id: 'another-reservation', data: { ...reservation.data, assignmentId: 'assignment-2' } };
  assert.throws(() => advance(state, other), /capacity offer/);
});

test('native asynchronous hourly rounds admit locally and settle days later without synchronized readiness', async (t) => {
  t.mock.timers.enable({ apis: ['Date'], now });
  const f = await runtimeFixture(t);
  const dependencies = { ...f.dependencies };
  delete dependencies.now;
  const { coordinator } = f;
  coordinator.control.genesisSha = (await runNativeRequest(coordinator, f.initialize, dependencies)).genesisSha;
  const consumer = f.configFor('consumer');
  consumer.control.genesisSha = coordinator.control.genesisSha;
  const fresh = (extra) => f.fresh({ ...extra, observedAt: new Date(Date.now()).toISOString() });
  const inventory = (sessions = []) => fresh({
    ...ownedScope,
    complete: true, queueChecked: true, historyChecked: true, capabilitiesVerified: true,
    capabilities: ['general'], sessions,
  });
  const taskEvidence = () => fresh({ ...evidence(), claimsReconciled: true, holdsChecked: true,
    dependenciesReady: true, epicChildrenReady: true, analysisReady: true, reviewGatesChecked: true,
    ownershipReconciled: true });
  const acquire = async (config) => {
    const request = f.request(config, 'begin-round');
    const result = await runNativeRequest(config, request, dependencies);
    return { ...request, roundToken: result.roundToken };
  };
  const run = (config, round, type, extra = {}) => runNativeRequest(config, {
    ...round, type, id: `delayed-${++next}`, ...extra,
  }, dependencies);
  const delay = (minutes) => t.mock.timers.tick(minutes * 60_000);
  let w = await acquire(consumer);
  await run(consumer, w, 'ready', { evidence: inventory() });
  await run(consumer, w, 'end-round');
  delay(67);
  let c = await acquire(coordinator);
  const reserved = await run(coordinator, c, 'reserve', {
    data: { assignmentId: 'assignment-1', workerId: 'mini' }, evidence: taskEvidence(),
  });
  const bound = binding(reserved.state, 1);
  delay(8);
  await run(coordinator, c, 'publish', { data: bound, evidence: taskEvidence() });
  await run(coordinator, c, 'end-round');
  delay(71);
  w = await acquire(consumer);
  const start = () => run(consumer, w, 'receipt', {
    data: { ...bound, status: 'starting', correlation: 'delayed-delivery' }, evidence: taskEvidence(),
  });
  await assert.rejects(start(), /Current-round local admission/);
  await run(consumer, w, 'ready', { evidence: inventory() });
  delay(4);
  const preview = await run(consumer, w, 'dispatch-plan', {
    data: { ...bound, correlation: 'delayed-delivery' }, evidence: taskEvidence(),
  });
  assert.equal(preview.nativeCreateAllowed, false);
  assert.deepEqual(preview.inventoryFreshness, {
    observedAt: new Date(Date.now() - 240_000).toISOString(),
    ageMs: 240_000, maxAgeMs: 60_000, refreshRequired: true,
  });
  await assert.rejects(start(), /Local kickoff inventory.*ageMs=240000.*publish ready.*changing receipt evidence.observedAt does not refresh ready/);
  await assert.rejects(start(), /Local kickoff inventory/);
  await run(consumer, w, 'ready', {
    evidence: inventory([{ id: 'cccccccc-1111-4222-8333-444444444444', ownershipVerified: true }]),
  });
  await run(consumer, w, 'unavailable', { data: { reasonCode: 'capability-unavailable' },
    evidence: fresh({ source: 'fixture: required local tooling unavailable; retained hold' }) });
  await assert.rejects(start(), /Current-round local admission/);
  // Tooling is restored; no TTL/assignment is reset.
  await run(consumer, w, 'ready', { evidence: inventory() });
  const authorized = await start();
  assert.equal(authorized.nativeCreateAllowed, true);
  delay(9);
  const rawReadback = {
    id: 'cccccccc-1111-4222-8333-444444444444', project_id: consumer.projectId,
    project_repo: 'OlyForge3D/PrintFarmer', path: '/worktrees/delayed-task',
    branch: 'test-only-general', activity: { status: 'idle' },
  };
  const session = { id: rawReadback.id, projectId: rawReadback.project_id, worktreePath: rawReadback.path };
  const continuationAck = await fixtureStartup(consumer, w, authorized.dispatchPlan,
    { ...bound, correlation: 'delayed-delivery' }, session, dependencies);
  const delivered = () => fresh({ session, repository: rawReadback.project_repo,
    nativeReadbackVerified: true, kickoffDeliveryVerified: true, assignmentCorrelation: 'delayed-delivery', continuationAck });
  await run(consumer, w, 'receipt', {
    data: { ...bound, status: 'running', correlation: 'delayed-delivery' }, evidence: delivered(),
  });
  await run(consumer, w, 'end-round');
  delay(3 * 60 + 11);
  w = await acquire(consumer);
  await run(consumer, w, 'receipt', {
    data: { ...bound, status: 'running', correlation: 'delayed-delivery' },
    evidence: { ...delivered(), source: 'fixture: same mapped child acknowledged recorded follow-up' },
  });
  const terminalRequest = {
    data: { ...bound, status: 'terminal-reported', correlation: 'delayed-delivery' },
    evidence: { ...delivered(), session: { ...session, terminalVerified: true },
      queueChecked: true, historyChecked: true, artifactsVerified: true },
  };
  await assert.rejects(run(consumer, w, 'receipt', terminalRequest), /final delivery ACK/);
  const terminal = await run(consumer, w, 'receipt', { ...terminalRequest, evidence: {
    ...terminalRequest.evidence, noPendingContinuation: true, noFutureDelivery: true,
    finalDeliveryCorrelation: 'follow-up-one',
    finalAck: { ...authorized.dispatchPlan.packet, noChildren: true, noPendingContinuation: true,
      noFutureDelivery: true, finalDeliveryCorrelation: 'follow-up-one' },
  } });
  const receiptDigest = terminal.state.assignments['assignment-1'].receipts.at(-1).evidenceDigest;
  await run(consumer, w, 'end-round');
  delay(2 * 24 * 60 + 17);
  c = await acquire(coordinator);
  const result = await run(coordinator, c, 'release', {
    data: bound, evidence: fresh({ consumerReceiptDigest: receiptDigest, taskDigest: bound.taskDigest,
      ownershipReconciled: true, artifactsVerified: true, noPendingContinuation: true }),
  });
  assert.equal(result.state.assignments['assignment-1'].state, 'terminal');
  assert.equal(result.state.availability.mini.remaining.general, 3);
  await run(coordinator, c, 'end-round');
  const replay = await readMailbox(coordinator.control, f.github.api);
  assert.deepEqual(replay.state, (await runNativeRequest(coordinator, {
    approvedPolicy: coordinator.approvedPolicy, type: 'inspect',
  }, dependencies)).state);
  assert.equal(JSON.stringify(replay.state).includes(rawReadback.id), false);
});

test('terminal commitments survive delay but observed resumption and uncertain delivery retain ownership', () => {
  let state = withRounds();
  state = advance(state, capacityOffer(state));
  state = advance(state, offeredReservation(state, 1));
  state = advance(state, event('deliver', 'coordinator', binding(state, 1)));
  state = advance(state, capacityOffer(state));
  state = advance(state, event('accept', 'consumer', {
    ...binding(state, 1), status: 'starting', correlation: 'one', evidenceDigest: digest('intent'), policySha: 'b'.repeat(40),
  }));
  const settle = () => advance(state, event('settle', 'coordinator', {
    ...binding(state, 1), terminalEvidenceDigest: digest('coordinator reconciliation'),
  }));
  assert.throws(settle, /terminal report/);
  state = advance(state, event('receipt', 'consumer', {
    ...binding(state, 1), status: 'uncertain', correlation: 'one', evidenceDigest: digest('lost acknowledgment'),
  }));
  assert.throws(settle, /terminal report/);
  const terminal = () => event('terminal-receipt', 'consumer', {
    ...binding(state, 1), status: 'terminal-reported', correlation: 'one', evidenceDigest: digest('final ACK/no future sends'),
  });
  state = advance(state, terminal());
  state = advance(state, event('unavailable', 'consumer', {
    reasonCode: 'owner-paused', evidenceDigest: digest('no further assignments offered'),
  }));
  assert.equal(state.readiness.mini, undefined);
  assert.equal(settle().assignments['assignment-1'].state, 'terminal');
  const beforeBlocker = capacityOffer(state);
  state = advance(state, event('report-blocker', 'consumer', {
    ...binding(state, 1), reasonCode: 'delivery-uncertain', evidenceDigest: digest('observed external resumption'),
  }));
  assert.throws(settle, /Unblocked durable/);
  assert.throws(() => advance(state, beforeBlocker), /Complete reconciled/);
  assert.equal(state.availability.mini.revoked, true);
  assert.throws(() => advance(state, event('receipt', 'consumer', {
    ...binding(state, 1), status: 'running', correlation: 'one', evidenceDigest: digest('resumed'),
  })), /transition/);
  state = advance(state, terminal());
  state = settle();
  assert.equal(state.assignments['assignment-1'].state, 'terminal');
});

test('local admission rechecks capability shrink, policy, receipt inventory and generation before creation', () => {
  let state = withRounds();
  state = advance(state, capacityOffer(state));
  state = advance(state, offeredReservation(state, 1));
  state = advance(state, event('deliver', 'coordinator', binding(state, 1)));
  const noTooling = capacityOffer(state);
  noTooling.data.capabilities = [];
  state = advance(state, noTooling);
  const acceptance = event('accept', 'consumer', {
    ...binding(state, 1), status: 'starting', correlation: 'one',
    evidenceDigest: digest('observed live admission'), policySha: 'b'.repeat(40),
  });
  assert.throws(() => advance(state, acceptance), /required capabilities/);
  state = advance(state, capacityOffer(state));
  assert.throws(() => advance(state, { ...acceptance, data: { ...acceptance.data, policySha: 'c'.repeat(40) } }), /matching policy/);
  assert.throws(() => advance(state, { ...acceptance, data: { ...acceptance.data, generation: 2 } }), /binding mismatch/);
  state = advance(state, event('report-blocker', 'consumer', {
    ...binding(state, 1), reasonCode: 'held', evidenceDigest: digest('hold observed'),
  }));
  assert.throws(() => advance(state, acceptance), /Current-round local admission/);
  const oldObservation = capacityOffer(state);
  oldObservation.data.inventoryObservedAt = new Date(now - 59_000).toISOString();
  state = advance(state, oldObservation);
  assert.throws(() => applyEvent(state, { ...acceptance, observedAt: new Date(now + 2_000).toISOString() },
    { now: now + 2_000 }), /Local kickoff inventory.*ageMs=61000/);
  state = advance(state, capacityOffer(state));
  assert.equal(advance(state, acceptance).assignments['assignment-1'].state, 'starting');
});

test('internal mailbox event names cannot bypass native evidence preparation', () => {
  for (const type of ['accept', 'offer-capacity', 'deliver', 'terminal-receipt', 'settle']) {
    assert.throws(() => prepareEvent({ role: 'consumer', workerId: 'mini', control: baseControl },
      event(type, 'consumer'), { state: withRounds() }, { sessions: {} }, now), /internal mailbox/);
  }
});
