import assert from 'node:assert/strict';
import test from 'node:test';
import { assessHostCapacity, classifyWork } from '../ralph-host-capacity.mjs';
import { planPrRecovery } from '../ralph-pr-recovery.mjs';

const now = Date.parse('2026-09-21T20:00:00Z');
const general = { scope: 'general', classificationComplete: true, files: ['src/feature.cs'] };
const mobile = { scope: 'mobile', files: ['mobile/App.swift'] };
const work = (id, candidate, host = 'macos-mobile', state = 'active') =>
  ({ ...candidate, jobId: `job-${id}`, executionHost: host, state });
const inventory = (items = []) => ({
  complete: true, historyChecked: true, queueChecked: true, reservationsChecked: true,
  remoteOwnershipChecked: true, observedAt: new Date(now).toISOString(),
  source: 'fixture correlated native/queue/ledger observations', work: items,
});
const check = (items, candidate, host = 'macos-mobile') =>
  assessHostCapacity({ host, inventory: inventory(items), candidate, now });

test('Mac permits one mobile plus four general but no mobile/general borrowing or sixth total', () => {
  const fourGeneral = Array.from({ length: 4 }, (_, i) => work(i, general));
  assert.equal(check(fourGeneral, mobile).allowed, true);
  assert.deepEqual(check(fourGeneral, mobile).projected, { total: 5, mobile: 1, general: 4 });
  assert.equal(check([work('mobile', mobile)], mobile).allowed, false);
  assert.equal(check(fourGeneral, general).allowed, false);
  assert.equal(check([work('mobile', mobile), ...fourGeneral], general).allowed, false);
  assert.equal(check([work('mobile', mobile), ...fourGeneral.slice(0, 3)], general).allowed, true);
});

test('Windows forbids mobile/mixed/unknown work and permits five but not six general workers', () => {
  const four = Array.from({ length: 4 }, (_, i) => work(i, general, 'windows-general'));
  assert.equal(check(four, general, 'windows-general').allowed, true);
  assert.equal(check([...four, work(4, general, 'windows-general')], general, 'windows-general').allowed, false);
  for (const candidate of [mobile, { scope: 'mixed' }, {}, { ...general, files: ['mobile/App.swift'] }]) {
    assert.equal(check([], candidate, 'windows-general').allowed, false);
  }
});

test('scope evidence cannot disguise mobile/mixed/unknown as general', () => {
  for (const candidate of [
    { ...general, classificationComplete: false }, { ...general, files: ['Views/App.swift'] },
    { ...general, labels: ['area:ios'] }, { ...general, acceptanceCriteria: ['Run Xcode tests'] },
    { ...general, scope: 'mixed' }, { ...general, scope: 'unknown' },
  ]) assert.equal(classifyWork(candidate), 'mobile');
  assert.equal(classifyWork(general), 'general');
});

test('queued/reserved/uncertain/unterminated work retains slots and real duplicate job/session observations are one slot', () => {
  for (const state of ['queued', 'reserved', 'uncertain', 'terminal']) {
    assert.equal(check([work('existing', mobile, 'macos-mobile', state)], mobile).allowed, false);
  }
  assert.equal(check([{ ...work('finished', mobile, 'macos-mobile', 'terminal'), terminalVerified: true }], mobile).allowed, true);
  const active = { ...work('active', mobile), sessionId: 'session-one' };
  assert.equal(check([active, { ...active, jobId: 'new-fence' }], general).counts.mobile, 1);
  assert.equal(check([active], { ...mobile, sessionId: 'session-one' }).projected.mobile, 1);
  assert.throws(() => check([active], { ...general, sessionId: 'session-one' }), /cannot change host\/category/);
  assert.throws(() => check([active, { ...active, executionHost: 'windows-general' }], general), /Conflicting execution-host/);
});

test('missing/stale/incomplete or malformed ownership inventory fails closed', () => {
  for (const override of [
    { complete: false }, { remoteOwnershipChecked: false }, { queueChecked: false },
    { observedAt: new Date(now - 60_001).toISOString() }, { observedAt: new Date(now + 1).toISOString() },
    { work: [{ scope: 'general' }] },
  ]) assert.throws(() => assessHostCapacity({ host: 'macos-mobile', inventory: { ...inventory(), ...override }, candidate: general, now }));
  assert.throws(() => check([], general, 'unknown'), /Unknown/);
});

test('PR recovery cannot bypass mobile cap or current cross-host general authority blocker', () => {
  const headSha = 'a'.repeat(40);
  const pulls = [1, 2, 3].map((number) => ({
    number, state: 'open', draft: true, sameRepository: true, headSha, labels: ['squad'],
    ...(number === 3 ? general : mobile), files: [number === 3 ? 'src/general.cs' : `mobile/${number}.swift`],
    filesComplete: true, verdict: { classification: 'CHANGES_REQUESTED', headSha },
  }));
  const ownership = Object.fromEntries(pulls.map(({ number }) => [number, {
    host: 'macos-mobile', state: 'inactive', admissionReconciled: true, noPendingDelivery: true,
    inventoryChecked: true, historyChecked: true, queueChecked: true,
    source: 'fixture native evidence', observedAt: new Date(now).toISOString(),
  }]));
  const result = planPrRecovery({ pulls, ownership, host: 'macos-mobile', scope: 'mixed', capacity: inventory(), now });
  assert.deepEqual(result.ready.map((pull) => pull.pr), [1]);
  assert.match(result.blocked.find((pull) => pull.pr === 2).reason, /capacity/);
  assert.match(result.blocked.find((pull) => pull.pr === 3).reason, /shared atomic Windows-authority/);
  const missing = planPrRecovery({ pulls: [pulls[0]], ownership, host: 'macos-mobile', scope: 'mixed', now });
  assert.equal(missing.ready.length, 0);
  assert.match(missing.blocked[0].reason, /inventory/);
});

test('held PR owners still occupy capacity while their work remains live', () => {
  const headSha = 'a'.repeat(40);
  const pulls = [1, 2].map((number) => ({
    number, state: 'open', sameRepository: true, headSha,
    labels: number === 1 ? ['squad', 'status:on-hold'] : ['squad'],
    ...mobile, files: [`mobile/${number}.swift`], filesComplete: true,
  }));
  const ownership = Object.fromEntries(pulls.map(({ number }) => [number, {
    host: 'macos-mobile', state: number === 1 ? 'live' : 'inactive',
    ...(number === 1 ? { sessionId: 'still-running' } : {}),
    admissionReconciled: true, noPendingDelivery: true, inventoryChecked: true,
    historyChecked: true, queueChecked: true, source: 'native evidence',
    observedAt: new Date(now).toISOString(),
  }]));
  const result = planPrRecovery({ pulls, ownership, host: 'macos-mobile', scope: 'mixed', capacity: inventory(), now });
  assert.deepEqual(result.ready, []);
  assert.match(result.blocked.find((entry) => entry.pr === 2).reason, /capacity/);
});
