import assert from 'node:assert/strict';
import test from 'node:test';
import { retainNativeLineage, resolveNativeLineage } from '../ralph-native-lineage.mjs';

const worker = 'aaaaaaaa-1111-4222-8333-444444444444';
const creator = 'bbbbbbbb-1111-4222-8333-444444444444';
const root = 'cccccccc-1111-4222-8333-444444444444';
const now = Date.parse('2026-09-23T00:00:00Z');
const observation = (id, creatorSessionId) => ({
  session: { id, ...(creatorSessionId ? { creatorSessionId } : {}) },
  nativeReadbackVerified: true, source: 'supported retained native readback',
  observedAt: '2026-09-21T00:00:00Z',
});

test('creator chains survive serialization and deletion without restoring liveness or exemptions', () => {
  const retained = retainNativeLineage({}, [
    observation(worker, creator), observation(creator, root), observation(root),
  ], now);
  const restarted = JSON.parse(JSON.stringify(retained));
  const resolved = resolveNativeLineage([{ id: worker, terminalVerified: true }], restarted, new Set([worker]), now);
  assert.deepEqual(resolved.get(creator), { id: creator, creatorSessionId: root });
  assert.deepEqual(resolved.get(root), { id: root });
  assert.equal(resolved.get(worker).creatorSessionId, creator);
  assert.equal(restarted[root].observedAt, '2026-09-21T00:00:00Z');
  for (const id of [creator, root]) {
    for (const key of ['terminalVerified', 'ownershipVerified', 'roleObservation', 'nativeReadbackVerified', 'path']) {
      assert.equal(resolved.get(id)[key], undefined);
    }
  }
});

test('cache never substitutes for a missing mapped worker or retains terminal flags', () => {
  const retained = retainNativeLineage({}, [
    { ...observation(worker, creator), session: { id: worker, creatorSessionId: creator, terminalVerified: true } },
    observation(creator),
  ], now);
  const resolved = resolveNativeLineage([{ id: root, creatorSessionId: worker }], retained, new Set([worker]), now);
  assert.equal(resolved.has(worker), false);
  assert.equal(retained[worker].terminalVerified, undefined);
  assert.equal(resolveNativeLineage([{ id: worker, terminalVerified: false }], retained, new Set([worker]), now)
    .get(worker).terminalVerified, false);
});

test('conflicting roots, changed parent edges and cycles are rejected without modifying prior evidence', () => {
  const retained = retainNativeLineage({}, [observation(worker, creator), observation(creator)], now);
  const original = JSON.stringify(retained);
  assert.throws(() => retainNativeLineage(retained, [observation(creator, root)], now), /Conflicting/);
  assert.throws(() => resolveNativeLineage([{ id: worker, creatorSessionId: root }], retained, new Set([worker]), now), /Conflicting/);
  assert.throws(() => resolveNativeLineage([{ id: worker, nativeReadbackVerified: true }], retained, new Set([worker]), now), /Conflicting/);
  assert.throws(() => retainNativeLineage({}, [observation(worker, creator), observation(creator, worker)], now), /Cyclic/);
  assert.equal(JSON.stringify(retained), original);
});

test('unproven, future, malformed, duplicate and incomplete ancestry cannot be invented', () => {
  for (const change of [{ nativeReadbackVerified: false }, { source: '' }, { observedAt: 'bad' },
    { observedAt: '2026-09-24T00:00:00Z' }, { session: { id: 'invalid' } }]) {
    assert.throws(() => retainNativeLineage({}, [{ ...observation(root), ...change }], now), /ancestry/);
  }
  assert.throws(() => resolveNativeLineage([{ id: worker }, { id: worker }], {}, new Set([worker]), now), /duplicate/);
  const result = resolveNativeLineage([{ id: worker, creatorSessionId: creator }], {}, new Set([worker]), now);
  assert.equal(result.has(creator), false);
});
