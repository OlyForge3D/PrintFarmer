import assert from 'node:assert/strict';
import test from 'node:test';
import { planPrRecovery } from '../ralph-pr-recovery.mjs';

const headSha = 'a'.repeat(40);
const now = Date.parse('2026-09-21T15:00:00Z');
const pull = (number, overrides = {}) => ({
  number, state: 'open', draft: true, sameRepository: true, scope: 'mobile', headSha, labels: ['squad'],
  files: [`src/feature-${number}.ts`], filesComplete: true,
  verdict: { classification: 'CHANGES_REQUESTED', headSha, reason: 'R1: fix contract regression' },
  ...overrides,
});
const inactive = (overrides = {}) => ({
  state: 'inactive', host: 'macos-mobile', admissionReconciled: true, observedAt: new Date(now).toISOString(),
  source: 'fixture: live inventory, session history and delivery queue',
  inventoryChecked: true, historyChecked: true, queueChecked: true, noPendingDelivery: true,
  ...overrides,
});
const plan = (pulls, ownership = Object.fromEntries(pulls.map((pr) => [pr.number, inactive()]))) =>
  planPrRecovery({
    pulls, ownership, now, host: 'macos-mobile', scope: 'mixed',
    capacity: {
      complete: true, historyChecked: true, queueChecked: true, reservationsChecked: true,
      remoteOwnershipChecked: true, observedAt: new Date(now).toISOString(), source: 'fixture complete inventory', work: [],
    },
  });

test('rejected draft with inactive owner remains actionable under a blocked assigned parent', () => {
  const result = plan([pull(2897, { parent: { labels: ['status:blocked'], assignees: ['jpapiez'] } })]);
  assert.equal(result.ready[0].pr, 2897);
  assert.equal(result.ready[0].headSha, headSha);
  assert.equal(result.ready[0].kind, 'revision');
  assert.match(result.ready[0].findings.reason, /R1/);
});

test('live owner is retained without duplicate selection even after PR head movement', () => {
  const result = plan([pull(2897)], { 2897: inactive({ state: 'live', sessionId: 'actual-session', headSha: 'b'.repeat(40) }) });
  assert.deepEqual(result.ready, []);
  assert.equal(result.inFlight[0].sessionId, 'actual-session');
});

test('idle, unknown, expired or incomplete observations are not proof of death', () => {
  for (const owner of [
    {}, inactive({ state: 'idle' }), inactive({ noPendingDelivery: false }),
    inactive({ historyChecked: false }), inactive({ queueChecked: false }),
    inactive({ observedAt: new Date(now - 60_001).toISOString() }),
  ]) {
    const result = plan([pull(1)], { 1: owner });
    assert.deepEqual(result.ready, []);
    assert.equal(result.blocked[0].reason, 'ownership not proven inactive');
  }
});

test('stale rejection becomes review recovery, never current revision feedback', () => {
  const result = plan([pull(1, { verdict: { classification: 'CHANGES_REQUESTED', headSha: 'b'.repeat(40) } })]);
  assert.equal(result.ready[0].kind, 'review');
  assert.equal(result.ready[0].findings, undefined);
});

test('verified owner approval takes precedence over raw aggregate rejection', () => {
  const result = plan([pull(1, {
    reviewDecision: 'CHANGES_REQUESTED',
    verdict: { classification: 'APPROVED', headSha, dissent: 1 },
  })]);
  assert.equal(result.ready[0].kind, 'integration');
});

test('revision and CI work sort before review and approved draft integration', () => {
  const result = plan([
    pull(1, { verdict: { classification: 'REVIEWED', headSha } }),
    pull(2, { verdict: { classification: 'MISSING', headSha } }),
    pull(3, { verdict: { classification: 'APPROVED', headSha }, failedChecks: ['typecheck'] }),
    pull(4),
  ]);
  assert.deepEqual(result.ready.map((item) => item.kind), ['revision']);
  assert.equal(result.blocked.filter((item) => /capacity/.test(item.reason)).length, 3);
});

test('shared baseline recovery serializes while independent files stay parallel', () => {
  const baseline = 'src/Web/ReactApp/test-typecheck-baseline.json';
  const result = plan([pull(1, { files: [baseline] }), pull(2, { files: [baseline] }), pull(3)]);
  assert.deepEqual(result.ready.map((item) => item.pr), [1]);
  assert.deepEqual(result.blocked[0].conflicts, [1]);
  assert.match(result.blocked[1].reason, /capacity/);
});

test('live and uncertain owners reserve conflicting files; unknown coverage fails closed', () => {
  for (const owner of [inactive({ state: 'live', sessionId: 'live-session' }), {}]) {
    const result = plan([pull(1, { filesComplete: false }), pull(2)], { 1: owner, 2: inactive() });
    assert.deepEqual(result.ready, []);
    assert.ok(result.blocked.some((item) => item.pr === 2 && item.conflicts.includes(1)));
  }
});

test('holds, forks and unlabelled PRs never enter unattended recovery', () => {
  const result = plan([
    pull(1, { labels: ['squad', 'status:on-hold'] }),
    pull(2, { sameRepository: false }),
    pull(3, { labels: ['squad:parker'] }),
  ]);
  assert.deepEqual(result.ready, []);
  assert.equal(result.blocked.length, 2);
});

test('malformed or duplicate observations fail explicitly', () => {
  assert.throws(() => plan([pull(1, { headSha: 'abc' })]), /full head SHA/);
  assert.throws(() => plan([pull(1), pull(1)]), /Duplicate/);
});

test('local inventory never proves another host inactive', () => {
  const result = plan([pull(1)], { 1: inactive({ host: 'windows-general' }) });
  assert.deepEqual(result.ready, []);
  assert.equal(result.blocked[0].reason, 'ownership not proven inactive');
});

test('cross-host overlap is retained, while independent local recovery may proceed', () => {
  const result = plan([
    pull(1, { scope: 'general', classificationComplete: true, files: ['shared.json'] }),
    pull(2, { files: ['shared.json'] }),
    pull(3),
  ]);
  assert.match(result.blocked.find((entry) => entry.pr === 1).reason, /shared atomic Windows-authority/);
  assert.deepEqual(result.ready.map((entry) => entry.pr), [3]);
  assert.deepEqual(result.blocked.find((entry) => entry.pr === 2).conflicts, [1]);
});
