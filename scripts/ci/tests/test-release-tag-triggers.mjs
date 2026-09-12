import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import test from 'node:test';
import {
  admit, advance, allocationKey, compareVersions, components, hash, identityLabels,
  parseTag, parseVersionFile, reserve, transact, validateCandidate, validateCompleteSet,
  validateLedger, verifyConsumer, verifyTag, verifyProtectionEvidence,
} from '../release-policy.mjs';
import { ensureSourceTag, gitLedger, readTag, verifyProtection } from '../release-github.mjs';
import { buildMetadata, emitBuildIdentity } from '../release-metadata.mjs';
import { runContext, runReleaseControl } from '../release-control.mjs';
import { inspectCompleteSet, publishImmutableTags } from '../release-set.mjs';

const sha = 'a'.repeat(40);
const newerSha = 'b'.repeat(40);
const anchor = 'c'.repeat(40);
const created = '2026-09-12T20:00:00.000Z';
const context = (overrides = {}) => ({
  repository: 'OlyForge3D/PrintFarmer', event: 'workflow_dispatch',
  ref: 'refs/heads/development', eventSha: sha,
  workflowIdentity: 'OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/development',
  workflowSha: sha, workflowBranch: 'development', buildId: '42', buildAttempt: '1',
  channel: 'insider', ...overrides,
});
const state = () => ({ schema: 1, anchor, counter: '0', reservations: {}, identities: {}, pointers: {}, qualifications: {} });
const admission = (overrides = {}) => admit(context(overrides), overrides.eventSha || sha, 'v1.2.3\n', '1.2.2');
const record = (ledger = state(), overrides = {}) => reserve(ledger, admission(overrides), created).record;
const completeSet = identity => ({
  schema: 1, identity, managedEligible: false,
  images: Object.fromEntries(Object.entries(components).map(([name, platforms], index) => [name, {
    digest: `sha256:${String(index + 1).repeat(64)}`,
    platforms: Object.fromEntries(platforms.map(platform => [platform, {
      digest: `sha256:${String(index + 1).repeat(64)}`, labels: identityLabels(identity),
    }])),
  }])),
});

function memoryStore(initial = state()) {
  let ledger = structuredClone(initial);
  let revision = 0;
  let writes = 0;
  return {
    async read() { return { revision, state: structuredClone(ledger) }; },
    async compareAndSet(expected, next) {
      if (revision !== expected) return false;
      ledger = structuredClone(next);
      revision++;
      writes++;
      return true;
    },
    get writes() { return writes; },
  };
}

test('strict canonical grammar accepts beta/RC and rejects every malformed numeric form', () => {
  assert.equal(runContext({ RELEASE_STAGE: 'none' }).stage, undefined);
  assert.equal(runContext({ RELEASE_STAGE: 'rc' }).stage, 'rc');
  for (const tag of ['v0.0.0', 'v1.2.3', 'v1.2.3-insider.1', 'v1.2.3-beta.9', 'v1.2.3-rc.10']) {
    assert.equal(parseTag(tag).channel, tag.includes('-') ? 'insider' : 'stable');
  }
  for (const tag of ['v01.2.3', 'v1.02.3', 'v1.2.03', 'v1.2.3-insider.0', 'v1.2.3-beta.01',
    'v1.2.3-rc.00', '1.2.3', 'v1.2.3 ', ' v1.2.3', 'v1.2.3\n', 'v1.2.3+build',
    'v1.2.3-insider.1.2', 'v1.2.3-dev.1', 'v1.2.3-alpha.1', 'ios/v1.2-beta.1', 'v1.2-beta.1']) {
    assert.throws(() => parseTag(tag), tag);
  }
  assert.equal(parseVersionFile('v1.2.3\r\n'), '1.2.3');
  for (const text of [' v1.2.3\n', 'v1.2.3 \n', 'v1.2.3\n\n', 'v1.2.3-insider.1\n']) {
    assert.throws(() => parseVersionFile(text));
  }
  assert.equal(compareVersions('1.2.3-insider.9', '1.2.3-insider.10'), -1);
  assert.equal(compareVersions('1.2.3-insider.9007199254740992', '1.2.3-insider.9007199254740993'), -1);
  assert.equal(compareVersions('1.2.3-beta.90', '1.2.3-insider.1'), -1);
  assert.equal(compareVersions('1.2.3-insider.90', '1.2.3-rc.1'), -1);
});

test('admission denies untrusted events, caller spoofing, source drift and swapped channels before writes', async () => {
  const store = memoryStore();
  for (const override of [
    { event: 'push' }, { event: 'pull_request' }, { event: 'pull_request_target' },
    { event: 'workflow_call' }, { event: 'repository_dispatch' },
    { repository: 'attacker/PrintFarmer' }, { workflowIdentity: 'forged-caller' },
    { workflowSha: newerSha }, { ref: 'refs/tags/v1.2.3' }, { ref: 'refs/heads/feature/test' },
    { ref: 'refs/heads/release' }, { ref: 'refs/heads/release/v1.2.3' }, { channel: 'stable' },
    { requestedTag: 'v1.2.4-insider.1' }, { buildAttempt: '01' }, { stage: 'dev' },
  ]) {
    await assert.rejects(transact(store, state => reserve(state, admission(override), created)));
  }
  assert.equal(store.writes, 0);
  assert.throws(() => admit(context(), newerSha, 'v1.2.3', '1.2.2'), /HEAD/);
  assert.throws(() => admit(context(), sha, 'v1.2.2', '1.2.2'), /exceed/);
  assert.equal(admission({ event: 'schedule' }).channel, 'insider');
  const stable = context({
    channel: 'stable', ref: 'refs/heads/main', workflowBranch: 'main',
    workflowIdentity: context().workflowIdentity.replace('/development', '/main'),
    requestedTag: 'v1.2.3',
  });
  assert.equal(admit(stable, sha, 'v1.2.3').channel, 'stable');
  assert.throws(() => admit({ ...stable, event: 'schedule' }, sha, 'v1.2.3'));
});

test('durable reservation returns same N for retries and larger N for attempts/base/workflow migration', async () => {
  const store = memoryStore();
  const first = await transact(store, state => reserve(state, admission(), created).record);
  const retry = await transact(store, state => reserve(state, admission(), 'later').record);
  assert.deepEqual(retry, first);
  const second = await transact(store, state => reserve(state, admission({ buildAttempt: '2' }), created).record);
  assert.equal(second.sequence, '2');
  const baseBump = { ...admission({ buildId: '43' }), baseVersion: '1.3.0' };
  const migrated = { ...baseBump, workflowIdentity: 'owner-approved-replacement' };
  const next = await transact(store, state => reserve(state, migrated, created).record);
  assert.equal(next.sequence, '3');
  assert.notEqual(allocationKey(baseBump), allocationKey(migrated));
  const { state: persisted } = await store.read();
  const resumedStore = memoryStore(persisted);
  assert.equal((await transact(resumedStore, state => reserve(state, migrated, created).record)).sequence, '3');
  assert.equal(persisted.reservations[first.allocationKey].record.sequence, '1');
});

test('concurrent allocators use a real CAS retry boundary and never recycle failed reservations', async () => {
  const store = memoryStore();
  const results = await Promise.all(Array.from({ length: 12 }, (_, index) => transact(store,
    state => reserve(state, admission({ buildId: String(index + 100) }), created).record)));
  assert.equal(new Set(results.map(item => item.sequence)).size, 12);
  assert.equal((await store.read()).state.counter, '12');
  const retry = await transact(store, state => reserve(state, admission({ buildId: '100' }), created).record);
  assert.equal(retry.sequence, results[0].sequence);
  const next = await transact(store, state => reserve(state, admission({ buildId: '999' }), created).record);
  assert.equal(next.sequence, '13');
});

test('continuity loss, reset and malformed persisted state fail closed', () => {
  assert.throws(() => validateLedger(undefined, anchor), /continuity/);
  const ledger = state();
  record(ledger);
  validateLedger(ledger, anchor);
  assert.throws(() => validateLedger(ledger, newerSha), /anchor/);
  ledger.counter = '0';
  assert.throws(() => validateLedger(ledger, anchor), /continuity/);
  ledger.counter = '01';
  assert.throws(() => validateLedger(ledger, anchor), /counter/);
});

test('beta/ordinary insider/RC share N and enforce nonregressing SemVer stage progression', () => {
  const ledger = state();
  assert.equal(record(ledger, { stage: 'beta' }).canonicalVersion, '1.2.3-beta.1');
  assert.equal(record(ledger, { buildId: '43' }).canonicalVersion, '1.2.3-insider.2');
  assert.equal(record(ledger, { buildId: '44', stage: 'rc' }).canonicalVersion, '1.2.3-rc.3');
  assert.throws(() => record(ledger, { buildId: '45' }), /Stage regression/);
  assert.throws(() => record(ledger, { stage: 'rc' }), /changed its admission/);
});

test('annotated and lightweight refs peel exactly; moved, deleted and recreated tags are rejected', async () => {
  const identity = record();
  const object = 'd'.repeat(40);
  for (const tag of [{ object, commit: newerSha }, { object: newerSha, commit: sha }, undefined]) {
    assert.throws(() => verifyTag(identity, object, tag));
  }
  const annotated = await readTag(async endpoint => endpoint.startsWith('git/ref/')
    ? { object: { sha: object, type: 'tag' } } : { object: { sha, type: 'commit' } }, identity.sourceTag);
  verifyTag(identity, object, annotated);
  const lightweight = await readTag(async () => ({ object: { sha, type: 'commit' } }), identity.sourceTag);
  verifyTag(identity, sha, lightweight);
  const missing = await readTag(async () => { throw Object.assign(new Error('missing'), { status: 404 }); }, identity.sourceTag);
  assert.equal(missing, undefined);
});

test('source tag is authorized in durable state before public ref creation and never recreated after deletion', async () => {
  const ledger = state();
  const identity = record(ledger);
  const store = memoryStore(ledger);
  const object = 'd'.repeat(40);
  let tag;
  const api = async (endpoint, method) => {
    if (endpoint.startsWith('git/ref/tags/')) {
      if (!tag) throw Object.assign(new Error('missing'), { status: 404 });
      return { object: { sha: object, type: 'tag' } };
    }
    if (endpoint === 'git/tags' && method === 'POST') return { sha: object };
    if (endpoint === `git/tags/${object}`) return { object: { sha, type: 'commit' } };
    if (endpoint === 'git/refs') {
      assert.equal((await store.read()).state.reservations[identity.allocationKey].tagObject, object);
      tag = true;
      return {};
    }
    throw new Error(endpoint);
  };
  await ensureSourceTag(api, store, identity, transact);
  await ensureSourceTag(api, store, identity, transact);
  tag = false;
  await assert.rejects(ensureSourceTag(api, store, identity, transact), /never recreate/);
});

test('record consumers reject direct tags, foreign run/attempt and changed signed identity', () => {
  const identity = record();
  verifyConsumer(identity, identity, context());
  for (const override of [{ event: 'push' }, { buildAttempt: '2' }, { repository: 'fork/repo' },
    { workflowIdentity: 'caller-forgery' }, { workflowSha: newerSha }]) {
    assert.throws(() => verifyConsumer(identity, identity, context(override)));
  }
  assert.throws(() => verifyConsumer({ ...identity, sourceCommit: newerSha }, identity, context()));
  for (const override of [{ repository: 'fork/repo' }, { releaseId: 'stable:1.2.3' },
    { canonicalVersion: '1.2.4-insider.1' }, { stage: 'rc' }, { allocationKey: 'forged' }]) {
    const malformed = { ...identity, ...override };
    assert.throws(() => verifyConsumer(malformed, malformed, context()), /Invalid canonical record/);
  }
});

test('complete-set CAS rejects mixed/missing platforms and stale-source high-N reruns', async () => {
  const ledger = state();
  const old = record(ledger);
  const current = record(ledger, { buildId: '43', eventSha: newerSha, workflowSha: newerSha });
  const set = completeSet(current);
  assert.throws(() => validateCompleteSet(current, { ...set, managedEligible: true }), /managed eligibility/);
  advance(ledger, current, set, newerSha, '');
  const previous = structuredClone(ledger.pointers);
  const highOld = record(ledger, { buildAttempt: '2' });
  assert.throws(() => advance(ledger, highOld, completeSet(highOld), newerSha, hash(set)), /Stale source/);
  assert.throws(() => advance(ledger, old, completeSet(old), sha, ''), /compare-and-set/);
  assert.deepEqual(ledger.pointers, previous);
  const missing = completeSet(current);
  delete missing.images['slicer-host'];
  assert.throws(() => validateCompleteSet(current, missing), /component/);
  const mixed = completeSet(current);
  mixed.images.frontend.platforms['linux/arm64'].labels['org.printfarmer.release-id'] = old.releaseId;
  assert.throws(() => validateCompleteSet(current, mixed), /Mixed/);
  const noArm = completeSet(current);
  delete noArm.images.api.platforms['linux/arm64'];
  assert.throws(() => validateCompleteSet(current, noArm), /platforms/);
  const differentBytes = completeSet(current);
  differentBytes.images.api.digest = `sha256:${'f'.repeat(64)}`;
  assert.throws(() => advance(ledger, current, differentBytes, newerSha, hash(set)), /different bytes/);
  assert.equal(advance(ledger, current, set, newerSha, hash(set)).setHash, hash(set));
});

test('two concurrent complete sets cannot both win the same expected pointer', async () => {
  const ledger = state();
  const a = record(ledger);
  const b = record(ledger, { buildId: '43' });
  const store = memoryStore(ledger);
  const outcomes = await Promise.allSettled([a, b].map(identity => transact(store,
    state => advance(state, identity, completeSet(identity), sha, ''))));
  assert.equal(outcomes.filter(result => result.status === 'fulfilled').length, 1);
  assert.equal(store.writes, 1);
});

test('emitted public identity excludes private and future fields without altering the authorization record', () => {
  const identity = { ...record(), protection: {
    rulesets: [{ id: 'private-ruleset-id' }],
    environment: { id: 'private-environment-id', reviewers: [{ id: 'private-reviewer-id' }] },
  }, futureAuthorization: { policy: 'private-future-value' },
  rulesetId: 'private-ruleset-id', environmentId: 'private-environment-id',
  reviewerId: 'private-reviewer-id', service: 'private-service', commit: 'private-commit',
  buildTime: 'private-time' };
  const original = JSON.stringify(identity);
  const root = resolve('.artifacts', `public-identity-${process.pid}`);
  try {
    emitBuildIdentity(identity, root);
    const emitted = readFileSync(resolve(root, 'src/Web/ReactApp/public/release-identity.json'), 'utf8');
    assert.deepEqual(JSON.parse(emitted), {
      service: 'frontend', commit: sha, buildTime: created,
      releaseId: identity.releaseId, channel: identity.channel,
      canonicalVersion: identity.canonicalVersion, baseVersion: identity.baseVersion,
      sourceBranch: identity.sourceBranch, sourceTag: identity.sourceTag,
      sourceCommit: identity.sourceCommit, authorizedBranchHead: identity.authorizedBranchHead,
      buildId: identity.buildId, buildAttempt: identity.buildAttempt,
      workflowIdentity: identity.workflowIdentity, identitySha256: hash(identity),
    });
    assert.doesNotMatch(emitted, /protection|ruleset|environment|reviewer|futureAuthorization|private-/);
    assert.equal(readFileSync(resolve(root, 'release-identity.json'), 'utf8'), original);
    assert.equal(JSON.stringify(identity), original);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('every platform uses one identity in assemblies/frontend/OCI, including large N', () => {
  const ledger = state();
  ledger.counter = '9007199254740992';
  const identity = record(ledger);
  const metadata = buildMetadata(identity);
  assert.match(metadata.props, /1\.2\.3-insider\.9007199254740993\+sha\.a{40}/);
  const frontend = JSON.parse(metadata.frontend);
  for (const key of ['releaseId', 'channel', 'canonicalVersion', 'sourceCommit', 'buildId', 'buildAttempt']) {
    assert.equal(frontend[key], identity[key]);
    assert.ok(metadata.props.includes(`Include="${key}" Value="${identity[key]}"`));
  }
  validateCompleteSet(identity, completeSet(identity));
  assert.ok(metadata.labels.includes(`org.opencontainers.image.version=${identity.canonicalVersion}`));
});

test('immutable image publication checks all conflicts before any tag writes and never moves aliases', () => {
  const identity = record();
  const set = completeSet(identity);
  const tags = new Map();
  const writes = [];
  const create = (tag, source) => { writes.push(tag); tags.set(tag, source.split('@')[1]); };
  publishImmutableTags(identity, set, tag => tags.get(tag), create);
  assert.equal(writes.length, 6);
  publishImmutableTags(identity, set, tag => tags.get(tag), create);
  assert.equal(writes.length, 6);
  assert.ok(writes.every(tag => tag.endsWith(`:${identity.canonicalVersion}`)));
  tags.set(writes[5], `sha256:${'f'.repeat(64)}`);
  assert.throws(() => publishImmutableTags(identity, set, tag => tags.get(tag), create), /conflict/);
  assert.equal(writes.length, 6);
});

test('registry inspection executes complete platform/provenance checks rather than accepting flags', () => {
  const identity = record();
  const digest = `sha256:${'d'.repeat(64)}`;
  const inspect = (_command, args) => args.includes('--raw') ? JSON.stringify({
    manifests: (args.some(arg => arg.includes('orcaslicer-worker')) ? ['amd64'] : ['amd64', 'arm64'])
      .flatMap(architecture => [
      { digest, platform: { os: 'linux', architecture } },
      { digest, annotations: { 'vnd.docker.reference.type': 'attestation-manifest',
        'vnd.docker.reference.digest': digest } },
    ]),
  }) : JSON.stringify({ config: { Labels: identityLabels(identity) } });
  const digests = Object.fromEntries(Object.keys(components).map(name => [name, digest]));
  assert.equal(Object.keys(inspectCompleteSet(identity, digests, inspect).images).length, 6);
  assert.throws(() => inspectCompleteSet(identity, digests,
    () => JSON.stringify({ manifests: [] })), /platform/);
});

test('stable promotion requires exact-main qualification and never reuses insider bytes', () => {
  const ledger = state();
  const insider = record(ledger);
  const set = completeSet(insider);
  advance(ledger, insider, set, sha, '');
  const stable = admit(context({ channel: 'stable', ref: 'refs/heads/main', workflowBranch: 'main',
    workflowIdentity: context().workflowIdentity.replace('/development', '/main') }), sha, 'v1.2.3');
  assert.throws(() => reserve(ledger, stable, created), /qualification/);
  ledger.qualifications[sha] = { sourceCommit: sha, reviewed: true, tests: 'passed',
    compatibility: 'passed', migrations: 'passed', recovery: 'passed', sourceTreeReviewed: true,
    promotionOrigin: { allocationKey: insider.allocationKey, releaseId: insider.releaseId,
      sourceCommit: sha, setHash: hash(set) } };
  const stableRecord = reserve(ledger, stable, created).record;
  assert.throws(() => validateCompleteSet(stableRecord, set), /identity/);
  advance(ledger, stableRecord, completeSet(stableRecord), sha, '');
  assert.equal(ledger.pointers.stable.canonicalVersion, '1.2.3');
  assert.equal(ledger.pointers.insider.canonicalVersion, insider.canonicalVersion);
});

test('candidate lifecycle rejects expiry, direct publication, deletion without merge-back and version regression', () => {
  const candidate = { branch: 'release/v1.2.3', target: '1.2.3', sourceCommit: sha, owner: 'maintainer',
    qualification: 'reviewed-commit', created, expires: '2026-09-14T20:00:00.000Z' };
  validateCandidate(candidate, created, 7);
  assert.throws(() => validateCandidate(candidate, '2026-09-15T00:00:00Z', 7), /Expired/);
  assert.throws(() => validateCandidate({ ...candidate, publish: true }, created, 7), /never publish/);
  assert.throws(() => validateCandidate({ ...candidate, action: 'delete' }, created, 7), /merge-back/);
  validateCandidate({ ...candidate, action: 'delete', abandonmentReason: 'superseded',
    mergeBack: { development: true, activeCandidates: true, versionDidNotRegress: true } }, created, 7);
});

test('missing live protections fail closed before a publisher operation', async () => {
  const calls = [];
  await assert.rejects(verifyProtection(async (endpoint, method = 'GET') => {
    calls.push({ endpoint, method }); return [];
  }, 'insider'), /Owner blocker/);
  assert.ok(calls.every(call => call.method === 'GET'));
});

test('GitHub ledger adapter uses non-force single-parent CAS and retries only real contention', async () => {
  const calls = [];
  const api = async (endpoint, method, body) => {
    calls.push({ endpoint, method, body });
    if (endpoint === `git/commits/${sha}`) return { tree: { sha: anchor } };
    if (endpoint === 'git/blobs' || endpoint === 'git/trees') return { sha: anchor };
    if (endpoint === 'git/commits') return { sha: newerSha };
    if (endpoint.startsWith('git/refs/')) return {};
    throw new Error(endpoint);
  };
  assert.equal(await gitLedger(api, anchor).compareAndSet(sha, state()), true);
  assert.deepEqual(calls.find(call => call.endpoint === 'git/commits').body.parents, [sha]);
  assert.deepEqual(calls.at(-1).body, { sha: newerSha, force: false });
});

test('workflow entry points have no direct tag/manual Docker bypass; iOS namespace is preserved', () => {
  const docker = readFileSync('.github/workflows/docker-publish.yml', 'utf8');
  assert.match(docker, /workflow_call:/);
  assert.doesNotMatch(docker, /^\s{2}(push|workflow_dispatch|schedule):/m);
  assert.match(docker, /uses: actions\/download-artifact@v8/);
  assert.match(docker, /cosign verify-blob/);
  assert.doesNotMatch(docker, /^\s+(?:packages|contents): write$/m);
  assert.match(docker, /password: \$\{\{ secrets\.RELEASE_REGISTRY_TOKEN \}\}/);
  assert.match(docker, /Publish and verify public corresponding-source assets\n\s+env:\n\s+GH_TOKEN: \$\{\{ steps\.publisher\.outputs\.token \}\}/);
  for (const step of docker.split(/^\s{6}- /m)) {
    if (!/uses: actions\/(?:upload|download)-artifact@/.test(step)) continue;
    const selector = step.match(/^\s{10}(?:name|pattern): (.+)$/m)?.[1];
    assert.ok(selector?.includes('github.run_attempt'), `Artifact crosses attempt boundary: ${selector}`);
  }
  assert.doesNotMatch(docker, /sort -V|promotion-tags\.txt|release-sha-|manual-\{\{sha\}\}/);
  assert.match(readFileSync('.github/workflows/consolidated-release.yml', 'utf8'),
    /uses: \.\/\.github\/workflows\/docker-publish\.yml/);
  const ios = readFileSync('.github/workflows/testflight-beta.yml', 'utf8');
  assert.match(ios, /TAG_NAME="ios\/v/);
  for (const tag of ['ios/v1.0-beta.106', 'ios/v1.0-rc.1', 'v1.0-beta.106']) {
    assert.throws(() => parseTag(tag));
  }
});

test('GitHub ledger reads pinned Git blobs and rejects ancestry, rollback, truncation and evidence loss', async () => {
  const previous = state();
  const identity = record(previous);
  previous.reservations[identity.allocationKey].tagObject = 'd'.repeat(40);
  advance(previous, identity, completeSet(identity), sha, '');
  const current = structuredClone(previous);
  record(current, { buildId: '43' });
  const fixture = (next = current, options = {}) => async endpoint => {
    if (endpoint === 'git/ref/heads/release-ledger') return { object: { sha: newerSha } };
    if (endpoint.startsWith('compare/')) return { status: options.ancestry || 'ahead' };
    if (endpoint === `git/commits/${newerSha}`) return { tree: { sha: 'head-tree' }, parents: [{ sha }] };
    if (endpoint === `git/commits/${sha}`) return { tree: { sha: 'previous-tree' }, parents: [{ sha: anchor }] };
    if (endpoint.startsWith('git/trees/')) return {
      truncated: options.truncated || false,
      tree: [{ path: 'state.json', type: 'blob', sha: endpoint.endsWith('head-tree') ? 'head-blob' : 'previous-blob' }],
    };
    if (endpoint.startsWith('git/blobs/')) return {
      encoding: 'base64',
      content: Buffer.from(JSON.stringify(endpoint.endsWith('head-blob') ? next : previous)).toString('base64'),
    };
    throw new Error(`Unexpected endpoint: ${endpoint}`);
  };
  assert.equal((await gitLedger(fixture(), anchor).read()).state.counter, '2');
  await assert.rejects(gitLedger(fixture(current, { ancestry: 'diverged' }), anchor).read(), /ancestry/);
  await assert.rejects(gitLedger(fixture(current, { truncated: true }), anchor).read(), /truncated/);
  const reset = state();
  await assert.rejects(gitLedger(fixture(reset), anchor).read(), /rollback/);
  const erased = structuredClone(current);
  delete erased.reservations[identity.allocationKey].tagObject;
  await assert.rejects(gitLedger(fixture(erased), anchor).read(), /immutable tagObject/);
  const replaced = structuredClone(current);
  replaced.reservations[identity.allocationKey].record.sourceCommit = newerSha;
  await assert.rejects(gitLedger(fixture(replaced), anchor).read(), /immutable reservation/);
  const lostPointer = structuredClone(current);
  delete lostPointer.pointers.insider;
  await assert.rejects(gitLedger(fixture(lostPointer), anchor).read(), /pointer rollback/);
  const lostStage = structuredClone(current);
  delete lostStage.stages;
  await assert.rejects(gitLedger(fixture(lostStage), anchor).read(), /stage rollback/);
  const changedAdmission = structuredClone(current);
  changedAdmission.reservations[identity.allocationKey].admission.buildId = '999';
  await assert.rejects(gitLedger(fixture(changedAdmission), anchor).read(), /immutable reservation/);
  const orphan = structuredClone(current);
  orphan.identities['1.2.4-insider.9'] = 'unknown';
  await assert.rejects(gitLedger(fixture(orphan), anchor).read(), /identity continuity/);
  // Owner-approved seed floors may exist before the first reservation.
  previous.reservations = {};
  previous.identities = {};
  const seedNext = structuredClone(previous);
  delete seedNext.pointers.insider;
  await assert.rejects(gitLedger(fixture(seedNext), anchor).read(), /pointer rollback/);
  seedNext.pointers = structuredClone(previous.pointers);
  delete seedNext.stages;
  await assert.rejects(gitLedger(fixture(seedNext), anchor).read(), /stage rollback/);
});

test('GitHub CAS distinguishes a competing head from a rejected protected write', async () => {
  const api = head => async (endpoint, method) => {
    if (endpoint === `git/commits/${sha}`) return { tree: { sha: anchor } };
    if (method === 'POST') return { sha: newerSha };
    if (method === 'PATCH') throw Object.assign(new Error('conflict'), { status: 422 });
    if (endpoint === 'git/ref/heads/release-ledger') return { object: { sha: head } };
    throw new Error(endpoint);
  };
  assert.equal(await gitLedger(api(newerSha), anchor).compareAndSet(sha, state()), false);
  await assert.rejects(gitLedger(api(sha), anchor).compareAndSet(sha, state()), /policy, not a CAS/);
});

function protectionFixture() {
  const names = ['release-canonical-tags', 'release-ledger-continuity', 'release-tag-creators', 'release-ledger-writer'];
  const rulesets = names.map((name, id) => ({
    id, name, enforcement: 'active',
    target: id % 2 === 0 ? 'tag' : 'branch',
    conditions: { ref_name: { include: [id % 2 === 0 ? 'refs/tags/v*' : 'refs/heads/release-ledger'], exclude: [] } },
    bypass_actors: id < 2 ? [] : [{ actor_type: 'Integration', actor_id: 123 }],
    rules: (id === 0 ? ['update', 'deletion'] : id === 1 ? ['non_fast_forward', 'deletion']
      : id === 2 ? ['creation'] : ['update']).map(type => ({ type })),
  }));
  const environment = { name: 'release-insider', deployment_branch_policy: { custom_branch_policies: true },
    protection_rules: [{ type: 'required_reviewers', prevent_self_review: true, reviewers: [{ id: 7 }] }] };
  const api = async (endpoint, method = 'GET') => {
    assert.equal(method, 'GET');
    if (endpoint === 'rules/branches/development') return [
      { type: 'deletion' }, { type: 'non_fast_forward' },
      { type: 'pull_request', parameters: { require_code_owner_review: true, required_approving_review_count: 1 } },
      { type: 'required_status_checks', parameters: { required_status_checks: [{ context: 'CI' }] } },
    ];
    if (endpoint === 'environments/release-insider') return environment;
    if (endpoint.endsWith('/deployment-branch-policies')) return { branch_policies: [{ name: 'development', type: 'branch' }] };
    if (endpoint === 'rulesets?per_page=100') return rulesets;
    if (endpoint.startsWith('rulesets/')) return rulesets[Number(endpoint.split('/')[1])];
    throw new Error(endpoint);
  };
  return { api, environment, rulesets };
}

test('live protection adapter accepts only scoped reviewer-gated environments and exclusive publisher rules', async () => {
  const { api, environment, rulesets } = protectionFixture();
  const evidence = await verifyProtection(api, 'insider', '123');
  assert.equal(evidence.rulesets.length, 4);
  verifyProtectionEvidence(evidence, 'insider', '123');
  assert.equal(evidence.environment.protection_rules[0].prevent_self_review, true);
  assert.throws(() => verifyProtectionEvidence(evidence, 'stable', '123'), /mismatched/);
  assert.throws(() => verifyProtectionEvidence(evidence, 'insider', '999'), /mismatched/);
  await assert.rejects(verifyProtection(api, 'insider', '999'), /approved publisher app/);
  environment.protection_rules[0].prevent_self_review = false;
  await assert.rejects(verifyProtection(api, 'insider', '123'), /non-self reviewer/);
  environment.protection_rules[0].prevent_self_review = true;
  rulesets[0].bypass_actors.push({ actor_type: 'RepositoryRole', actor_id: 5 });
  await assert.rejects(verifyProtection(api, 'insider', '123'), /continuity bypass/);
  rulesets[0].bypass_actors = [];
  const staleListing = async endpoint => endpoint === 'rulesets?per_page=100'
    ? rulesets.map(rule => ({ ...rule, enforcement: 'active' })) : api(endpoint);
  rulesets[0].enforcement = 'disabled';
  await assert.rejects(verifyProtection(staleListing, 'insider', '123'), /active release-canonical-tags/);
});

function authorizationFixture(initial = state()) {
  const { api: policies } = protectionFixture();
  const objects = new Map();
  let serial = 100;
  const put = value => {
    const id = (++serial).toString(16).padStart(40, '0');
    objects.set(id, value);
    return id;
  };
  const blob = put({ encoding: 'base64', content: Buffer.from(JSON.stringify(initial)).toString('base64') });
  const tree = put({ truncated: false, tree: [{ path: 'state.json', type: 'blob', sha: blob }] });
  let head = put({ tree: { sha: tree }, parents: [{ sha: anchor }] });
  let tag;
  const calls = [];
  const env = {
    GH_TOKEN: 'github-fixture', RELEASE_PUBLISHER_TOKEN: 'publisher-fixture',
    RELEASE_PUBLISHER_APP_ID: '123', RELEASE_LEDGER_ANCHOR: anchor,
    GITHUB_REPOSITORY: context().repository, GITHUB_EVENT_NAME: context().event,
    GITHUB_REF: context().ref, GITHUB_SHA: sha,
    GITHUB_WORKFLOW_REF: context().workflowIdentity, GITHUB_WORKFLOW_SHA: sha,
    GITHUB_RUN_ID: '42', GITHUB_RUN_ATTEMPT: '1', RELEASE_CHANNEL: 'insider',
  };
  return {
    env, calls,
    deleteTag() { tag = undefined; },
    async fetch(url, options) {
      const endpoint = url.split('/repos/OlyForge3D/PrintFarmer/')[1];
      assert.ok(endpoint, `Unexpected API host/path: ${url}`);
      const method = options.method;
      const publisher = options.headers.Authorization === `Bearer ${env.RELEASE_PUBLISHER_TOKEN}`;
      const admin = /^(rules\/|rulesets|environments\/)/.test(endpoint);
      calls.push({ endpoint, method, publisher, admin });
      const response = (body, status = 200) => new Response(JSON.stringify(body), { status });
      if ((admin || method !== 'GET') && !publisher) return response({}, 403);
      if (admin) return response(await policies(endpoint));
      if (endpoint === 'git/ref/heads/development') return response({ object: { sha } });
      if (endpoint === 'git/ref/heads/release-ledger') return response({ object: { sha: head } });
      if (endpoint.startsWith('compare/')) return response({ status: 'ahead' });
      if (endpoint.startsWith('contents/VERSION?')) {
        return response({ encoding: 'base64', content: Buffer.from('v1.2.3\n').toString('base64') });
      }
      if (endpoint.startsWith(`commits/${sha}/check-runs`)) {
        return response({ total_count: 3, check_runs: ['CI tooling tests', '.NET build', 'Frontend build & tests']
          .map((name, id) => ({ name, id, conclusion: 'success', app: { slug: 'github-actions' } })) });
      }
      if (endpoint.startsWith('git/ref/tags/')) {
        return tag ? response({ object: { sha: tag, type: 'tag' } }) : response({}, 404);
      }
      if (method === 'GET') {
        const object = objects.get(endpoint.split('/').at(-1));
        assert.ok(object, `Missing fixture object: ${endpoint}`);
        return response(object);
      }
      const body = JSON.parse(options.body);
      if (endpoint === 'git/blobs') {
        return response({ sha: put({ encoding: 'base64', content: Buffer.from(body.content).toString('base64') }) });
      }
      if (endpoint === 'git/trees') return response({ sha: put({ truncated: false, tree: body.tree }) });
      if (endpoint === 'git/commits') {
        return response({ sha: put({ tree: { sha: body.tree }, parents: body.parents.map(sha => ({ sha })) }) });
      }
      if (endpoint === 'git/refs/heads/release-ledger') {
        assert.equal(body.force, false);
        assert.equal(objects.get(body.sha).parents[0].sha, head);
        head = body.sha;
        return response({});
      }
      if (endpoint === 'git/tags') return response({ sha: put({ object: { sha: body.object, type: body.type } }) });
      if (endpoint === 'git/refs') { tag = body.sha; return response({}); }
      throw new Error(`Unexpected request: ${method} ${endpoint}`);
    },
  };
}

test('actual control flow keeps github.token read-only and requires App verification before any writes', async () => {
  const fixture = authorizationFixture();
  const originalFetch = globalThis.fetch;
  const cwd = process.cwd();
  const root = resolve('.artifacts', `authorization-${process.pid}`);
  mkdirSync(root, { recursive: true });
  globalThis.fetch = fixture.fetch;
  process.chdir(root);
  try {
    await runReleaseControl('admit', { ...fixture.env, RELEASE_PUBLISHER_TOKEN: undefined });
    assert.ok(fixture.calls.length > 0);
    assert.ok(fixture.calls.every(call => !call.admin && !call.publisher && call.method === 'GET'));
    fixture.calls.length = 0;
    for (const operation of ['authorize', 'advance']) {
      for (const token of [undefined, fixture.env.GH_TOKEN]) {
        await assert.rejects(runReleaseControl(operation, { ...fixture.env, RELEASE_PUBLISHER_TOKEN: token }),
          /Protected publisher App token required/);
      }
    }
    assert.equal(fixture.calls.length, 0, 'Missing App credential must fail before API calls');
    const denied = { ...fixture.env, RELEASE_PUBLISHER_TOKEN: 'not-authorized-fixture' };
    await assert.rejects(runReleaseControl('authorize', denied), /HTTP 403/);
    assert.ok(fixture.calls.every(call => call.method === 'GET'), '403 must not allocate or tag');
    fixture.calls.length = 0;
    await runReleaseControl('authorize', fixture.env);
    const identity = JSON.parse(readFileSync('release-identity.json', 'utf8'));
    verifyProtectionEvidence(identity.protection, 'insider', '123');
    const firstWrite = fixture.calls.findIndex(call => call.method !== 'GET');
    const adminCalls = fixture.calls.filter(call => call.admin);
    assert.ok(adminCalls.length >= 8 && adminCalls.every(call => call.publisher));
    assert.ok(fixture.calls.slice(firstWrite).every(call => !call.admin), 'Protection must precede allocation');
    const signedBytes = readFileSync('release-identity.json', 'utf8');
    await runReleaseControl('authorize', fixture.env);
    assert.equal(readFileSync('release-identity.json', 'utf8'), signedBytes, 'Retry retains original evidence bytes');
    fixture.calls.length = 0;
    const consumer = { ...fixture.env, RELEASE_PUBLISHER_TOKEN: undefined, RELEASE_IDENTITY: signedBytes };
    await runReleaseControl('consume', consumer);
    assert.ok(fixture.calls.every(call => !call.admin && !call.publisher && call.method === 'GET'));
    assert.match(readFileSync('src/ReleaseIdentity.props', 'utf8'), /1\.2\.3-insider\.1/);
    assert.equal(readFileSync('release-identity.json', 'utf8'), signedBytes,
      'Public projection must preserve the signed private record bytes');
    const publicIdentity = readFileSync('src/Web/ReactApp/public/release-identity.json', 'utf8');
    assert.doesNotMatch(publicIdentity, /protection|rulesets|environment|reviewers|publisherAppId/);
    assert.equal(JSON.parse(publicIdentity).identitySha256, hash(identity));
    fixture.calls.length = 0;
    writeFileSync('release-set.json', JSON.stringify(completeSet(identity)));
    await runReleaseControl('advance', { ...fixture.env, RELEASE_IDENTITY: signedBytes });
    assert.ok(fixture.calls.some(call => call.method === 'PATCH'));
    assert.ok(fixture.calls.every(call => call.publisher && !call.admin),
      'Pointer writer needs the App, not Administration permission');
    fixture.calls.length = 0;
    const changed = structuredClone(identity);
    changed.protection.publisherAppId = '999';
    await assert.rejects(runReleaseControl('consume', { ...consumer, RELEASE_IDENTITY: JSON.stringify(changed) }),
      /record was changed/);
    await assert.rejects(runReleaseControl('consume', { ...consumer, GITHUB_RUN_ATTEMPT: '2' }),
      /Unauthorized consumer/);
    await assert.rejects(runReleaseControl('consume', { ...consumer, RELEASE_PUBLISHER_APP_ID: '999' }),
      /mismatched publisher/);
    fixture.deleteTag();
    await assert.rejects(runReleaseControl('consume', consumer), /tag missing/);
    assert.ok(fixture.calls.every(call => call.method === 'GET'));
  } finally {
    process.chdir(cwd);
    globalThis.fetch = originalFetch;
    rmSync(root, { recursive: true, force: true });
  }
});

test('missing, malformed and weakened signed protection evidence always fails closed', async () => {
  const evidence = await verifyProtection(protectionFixture().api, 'insider', '123');
  for (const mutate of [
    item => { item.schema = 0; }, item => { item.repository = 'fork/repo'; },
    item => { item.verifiedAt = 'invalid'; }, item => { item.branchRules = []; },
    item => { item.environment.protection_rules = []; }, item => { item.rulesets.pop(); },
    item => { item.rulesets[0].enforcement = 'disabled'; },
    item => { item.rulesets[3].bypass_actors[0].actor_id = 456; },
  ]) {
    const invalid = structuredClone(evidence);
    mutate(invalid);
    assert.throws(() => verifyProtectionEvidence(invalid, 'insider', '123'));
  }
  assert.throws(() => verifyProtectionEvidence(undefined, 'insider', '123'), /Missing/);
  const ledger = state();
  const identity = record(ledger);
  const fixture = authorizationFixture(ledger);
  const originalFetch = globalThis.fetch;
  globalThis.fetch = fixture.fetch;
  try {
    await assert.rejects(runReleaseControl('consume', {
      ...fixture.env, RELEASE_IDENTITY: JSON.stringify(identity), RELEASE_PUBLISHER_TOKEN: undefined,
    }), /Missing or mismatched publisher protection evidence/);
    assert.ok(fixture.calls.every(call => !call.admin && call.method === 'GET'));
    assert.ok(!fixture.calls.some(call => call.endpoint.startsWith('git/ref/tags/')));
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test('workflow credential wiring and signature gate reject failure or changed bytes before consumers', () => {
  const authority = readFileSync('.github/workflows/consolidated-release.yml', 'utf8');
  const docker = readFileSync('.github/workflows/docker-publish.yml', 'utf8');
  const authorize = authority.split('\n  authorize:')[1].split('\n  publish:')[0];
  assert.match(authorize, /environment: release-/);
  assert.ok(authorize.indexOf('uses: actions/create-github-app-token@v2') <
    authorize.indexOf('node scripts/ci/release-control.mjs authorize'));
  assert.match(authorize, /permission-administration: read/);
  assert.match(authorize, /GH_TOKEN: \$\{\{ github\.token \}\}\n\s+RELEASE_PUBLISHER_TOKEN: \$\{\{ steps\.publisher\.outputs\.token \}\}/);
  assert.doesNotMatch(authority.split('\n  admit:')[1].split('\n  authorize:')[0], /permission-administration|RELEASE_PUBLISHER_TOKEN/);
  const jobs = Object.fromEntries([...docker.matchAll(/^  ([\w-]+):\n([\s\S]*?)(?=^  [\w-]+:\n|(?![\s\S]))/gm)]
    .map(match => [match[1], match[2]]));
  const gated = name => name === 'admission' || (jobs[name]?.match(/^    needs: (.+)$/m)?.[1]
    .replace(/[[\]]/g, '').split(',').map(item => item.trim()).some(gated) ?? false);
  for (const [name, job] of Object.entries(jobs)) {
    if (/release-control\.mjs (consume|advance)|RELEASE_REGISTRY_TOKEN/.test(job)) {
      assert.ok(gated(name), `${name} can bypass signature admission`);
    }
  }
  const script = docker.split('      - name: Verify signed branch-at-authorization evidence')[1]
    .split('\n  #')[0].split('        run: |\n')[1].split('\n')
    .map(line => line.replace(/^          /, '')).join('\n')
    .replace('${{ fromJSON(inputs.identity).workflowIdentity }}', context().workflowIdentity)
    .replace('node scripts/ci/release-control.mjs consume', 'printf "CONSUMED"');
  const root = resolve('.artifacts', `signature-gate-${process.pid}`);
  mkdirSync(root, { recursive: true });
  try {
    writeFileSync(resolve(root, 'release-identity.json'), '{"signed":true}');
    const shell = process.platform === 'win32' ? 'C:\\Program Files\\Git\\bin\\bash.exe' : 'bash';
    for (const [exit, bytes, expected] of [
      ['1', '{"signed":true}', false],
      ['0', '{"signed":false}', false],
      ['0', '{"signed":true}', true],
    ]) {
      const result = spawnSync(shell, ['-c', `cosign() { return "$VERIFY_EXIT"; }\n${script}`], {
        cwd: root, encoding: 'utf8', env: { ...process.env, VERIFY_EXIT: exit, RELEASE_IDENTITY: bytes },
      });
      assert.ifError(result.error);
      assert.equal(result.status === 0, expected, result.stderr);
      assert.equal(result.stdout.includes('CONSUMED'), expected);
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('candidate CLI validates real ancestry and fails expiry, bare branches and publication', () => {
  const root = resolve('.artifacts', `candidate-cli-${process.pid}`);
  mkdirSync(resolve(root, '.github'), { recursive: true });
  try {
    const git = args => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
    git(['init', '--quiet']);
    git(['-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '--quiet', '--allow-empty', '-m', 'fixture']);
    const plan = { branch: 'release/v1.2.3', target: '1.2.3', sourceCommit: git(['rev-parse', 'HEAD']).trim(),
      owner: 'maintainer', qualification: 'reviewed',
      created: new Date().toISOString(), expires: new Date(Date.now() + 86400000).toISOString() };
    const run = (branch, overrides = {}) => {
      writeFileSync(resolve(root, '.github', 'release-candidate.json'), JSON.stringify({ ...plan, ...overrides }));
      return spawnSync(process.execPath, [resolve('scripts/ci/validate-release-candidate.mjs')], {
        cwd: root, encoding: 'utf8',
        env: { ...process.env, CANDIDATE_BRANCH: branch, RELEASE_CANDIDATE_MAX_DAYS: '7' },
      });
    };
    assert.equal(run(plan.branch).status, 0);
    assert.equal(run('main').status, 0);
    assert.equal(run('release').status, 1);
    assert.equal(run(plan.branch, { publish: true }).status, 1);
    assert.equal(run(plan.branch, { sourceCommit: sha }).status, 1);
    assert.equal(run(plan.branch, { expires: '2000-01-01T00:00:00Z' }).status, 1);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
