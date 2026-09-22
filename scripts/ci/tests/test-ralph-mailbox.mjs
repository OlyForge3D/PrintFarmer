import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, realpath, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  applyEvent, digest, initialState, initializeMailbox, inventoryDigest,
  publishEvent, readMailbox, researchDisposition, taskFromEvidence, validateRegistry, verifyControlRepository,
} from '../ralph-mailbox.mjs';
import { prepareEvent, runNativeRequest, validateTriageEvidence } from '../ralph-native-runtime.mjs';

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
  const blobs = new Map(), trees = new Map(), commits = new Map(), refs = new Map();
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
  return { api, metadata, calls, refs, commits, blobs, putRecord,
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
  const research = { issue: 1, labels: ['go:needs-research'] };
  assert.equal(researchDisposition(research).action, 'reserve-research');
  assert.equal(researchDisposition(research, { task, state: 'running' }).action, 'follow-existing-research');
  assert.equal(researchDisposition(research, { task, state: 'terminal' }).action, 'retain-research-gate');
  const findings = {
    summary: 'Verified findings', acceptanceCriteria: ['Concrete implementation acceptance'],
    remainingBlockers: [], exitCriteriaMet: true, approvalRequired: false, implementationPlanVerified: true,
    implementationIssue: 2, researchPrUrl: 'https://github.com/OlyForge3D/PrintFarmer/pull/3',
    researchPrMerged: true, researchHeadSha: 'a'.repeat(40),
  };
  const proposal = researchDisposition({ ...research, findings }, { task, state: 'terminal' });
  assert.equal(proposal.action, 'propose-implementation-readiness');
  assert.equal(proposal.closeIssue, false);
  assert.equal(proposal.mutationAuthorized, false);
  assert.equal(proposal.issue, 2);
  for (const change of [{ approvalRequired: true }, { remainingBlockers: ['decision needed'] }, { researchPrMerged: false }, { exitCriteriaMet: false }]) {
    assert.equal(researchDisposition({ ...research, findings: { ...findings, ...change } }, { task, state: 'terminal' }).action, 'retain-research-gate');
  }
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

test('resumed terminal native sessions block readiness rather than escaping quota', () => {
  const state = withRounds();
  state.assignments.old = { workerId: 'mini', state: 'terminal' };
  const id = 'dddddddd-1111-4222-8333-444444444444';
  const request = { ...event('ready', 'consumer'), evidence: {
    observedAt: new Date(now).toISOString(), source: 'native', complete: true,
    queueChecked: true, historyChecked: true, capabilitiesVerified: true,
    capabilities: ['general'], sessions: [{ id, ownershipVerified: true }],
  } };
  assert.throws(() => prepareEvent({ role: 'consumer', workerId: 'mini', control: baseControl }, request, { state }, {
    sessions: { old: { assignmentId: 'old', sessionId: id } },
  }, now), /resumed terminal/);
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
  assert.equal(prepareEvent(config, request, { state }, journal, now).createAllowed, true);
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
  for (const phrase of [
    'coordinator alone scans all issues/PRs', 'Consumers do not globally triage',
    'go:needs-research', 'go:no', 'research-PR/implementation-issue',
    'Never close', 'appropriate Squad owner', 'normal category capacity',
    'source-only', 'nativeCreateAllowed:true', 'lost response',
    'never serialized into the queue', 'before **any** triage-label',
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
  const start = { ...request, id: 'native-start', type: 'receipt',
    data: { ...binding(state, 1), status: 'starting', correlation: 'correlation-1' },
    evidence: { ...evidence(), observedAt, source: 'native/GitHub', holdsChecked: true, ownershipReconciled: true } };
  assert.equal((await runNativeRequest(config, start, dependencies)).nativeCreateAllowed, true);
  assert.equal((await runNativeRequest(config, start, dependencies)).nativeCreateAllowed, false);
  const journal = JSON.parse(await readFile(path.join(root, 'native-state/journal.json'), 'utf8'));
  assert.equal(journal.sessions['correlation-1'].startEventId, 'native-start');
  const workerSession = 'dddddddd-1111-4222-8333-444444444444';
  const running = { ...request, id: 'native-running', type: 'receipt',
    data: { ...binding(state, 1), status: 'running', correlation: 'correlation-1' },
    evidence: { observedAt, source: 'native readback', session: { id: workerSession, projectId, worktreePath: '/worktrees/task' },
      assignmentCorrelation: 'correlation-1', repository: 'OlyForge3D/PrintFarmer', nativeReadbackVerified: true, kickoffDeliveryVerified: true } };
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
    const startRequest = { ...w, type: 'receipt', id: `start-${++next}`,
      data: { ...taskBinding, status: 'starting', correlation: 'delivery-one' }, evidence: taskEvidence };
    assert.equal((await runNativeRequest(consumer, startRequest, dependencies)).nativeCreateAllowed, true);
    assert.equal((await runNativeRequest(consumer, startRequest, dependencies)).nativeCreateAllowed, false);
    const sessionId = 'cccccccc-1111-4222-8333-444444444444';
    const session = { id: sessionId, projectId: consumer.projectId, worktreePath: '/worktrees/task' };
    const delivered = fresh({ session, assignmentCorrelation: 'delivery-one',
      repository: 'OlyForge3D/PrintFarmer', nativeReadbackVerified: true, kickoffDeliveryVerified: true });
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
        queueChecked: true, historyChecked: true, artifactsVerified: true },
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

test('supplied workflow IDs cannot exempt work from readiness; observed bounded role sessions can', () => {
  const state = withRounds(), config = { role: 'consumer', workerId: 'mini', host: 'macos-mobile',
    projectId: 'bbbbbbbb-1111-4222-8333-444444444444', worktreeRoot: '/worktrees', control: baseControl };
  const session = { id: 'cccccccc-1111-4222-8333-444444444444', workflowId: 'aaaaaaaa-1111-4222-8333-444444444444' };
  config.automationWorkflowIds = [session.workflowId];
  const request = { ...event('ready', 'consumer'), evidence: {
    observedAt: new Date(now).toISOString(), source: 'actual session observation', complete: true,
    queueChecked: true, historyChecked: true, capabilitiesVerified: true, capabilities: ['general'], sessions: [session],
  } };
  const prepare = () => prepareEvent(config, request, { state }, { sessions: {} }, now);
  assert.throws(prepare, /Pre-existing/);
  session.nativeReadbackVerified = true;
  session.roleObservation = { role: 'coordinator', workerId: 'mini', projectId: config.projectId,
    worktreePath: '/worktrees/role', ownerConfiguredRoleVerified: true, noTaskExecutionVerified: true };
  assert.equal(prepare().event.data.unassignedSessions, 0);
  session.roleObservation.noTaskExecutionVerified = false;
  assert.throws(prepare, /Pre-existing/);
});
