import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';
import test from 'node:test';
import { load } from 'js-yaml';
import { components, compareVersions, parseTag, validateVersion, verifyEnvironmentRestrictions } from '../release-policy.mjs';
import { githubClient, verifyOwnerDispatch } from '../release-dispatch.mjs';
import { buildMetadata } from '../release-metadata.mjs';
import { buildImages, publishRelease, releaseAssets, rejectExistingVersion, selectRelease } from '../publish-release.mjs';
import { imageRepository, inspectTag, publishImageTags, rejectExistingImages, verifyImages } from '../release-set.mjs';
import { buildManifest, deriveSequence, validateManifest, validateManifestInput,
  SEQUENCE_MAJOR_MAX, SEQUENCE_MINOR_MAX, SEQUENCE_PATCH_MAX, SEQUENCE_PRERELEASE_MAX,
  SEQUENCE_STABLE_SUFFIX, MINIMUM_UPDATER_VERSION } from '../release-manifest.mjs';

const sha = 'a'.repeat(40);
const head = 'b'.repeat(40);
const workflowSha = 'e'.repeat(40);
const digest = `sha256:${'c'.repeat(64)}`;
const platformDigest = `sha256:${'d'.repeat(64)}`;
const release = { version: '1.2.3-insider.2', tag: 'v1.2.3-insider.2', channel: 'insider',
  sourceBranch: 'development', sourceCommit: sha, buildId: '42' };
const digests = Object.fromEntries(Object.keys(components).map(name => [name, digest]));
const imageDetails = Object.fromEntries(Object.entries(components).map(([name, component]) => [name, {
  indexDigest: digest,
  platforms: component.platforms,
  platformDigests: Object.fromEntries(component.platforms.map(platform => [platform, platformDigest])),
}]));
const owner = { login: 'jpapiez', id: 5460061, type: 'User' };
const repo = { id: 123, full_name: 'OlyForge3D/PrintFarmer' };
const env = {
  GITHUB_REPOSITORY: repo.full_name, GITHUB_EVENT_NAME: 'workflow_dispatch',
  GITHUB_REF: 'refs/heads/development', GITHUB_RUN_ATTEMPT: '1', GITHUB_RUN_ID: '42',
  GITHUB_WORKFLOW_REF: `${repo.full_name}/.github/workflows/consolidated-release.yml@refs/heads/development`,
  GITHUB_WORKFLOW_SHA: workflowSha, GITHUB_SHA: sha, GITHUB_ACTOR: owner.login, GITHUB_ACTOR_ID: String(owner.id),
  GITHUB_TRIGGERING_ACTOR: owner.login, RELEASE_APPROVAL_MODE: 'single-maintainer',
  RELEASE_CHANNEL: release.channel, RELEASE_VERSION: release.version, RELEASE_SOURCE_SHA: '',
};
const event = { sender: owner, repository: repo, ref: 'development',
  inputs: { channel: release.channel, version: release.version, source_sha: '' } };
const environment = { name: 'release-insider', can_admins_bypass: false,
  deployment_branch_policy: { custom_branch_policies: true, protected_branches: false },
  protection_rules: [{ type: 'branch_policy' }] };
const policies = { total_count: 1, branch_policies: [{ name: 'development', type: 'branch' }] };
const versionFixturePath = 'scripts/ci/fixtures/release-version-sequence.golden.json';
const versionSchemaPath = 'scripts/ci/fixtures/release-version-sequence.schema.json';
const versionFixture = JSON.parse(readFileSync(versionFixturePath, 'utf8'));
const manifestFixturePath = 'scripts/ci/fixtures/update-manifest.golden.json';
const manifestSchemaPath = 'scripts/ci/fixtures/update-manifest.schema.json';
const manifestFixtureBytes = readFileSync(manifestFixturePath);
const manifestFixture = JSON.parse(manifestFixtureBytes);
const manifestIssuer = 'https://token.actions.githubusercontent.com';
function manifestIdentityFor(channel) {
  return `https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/${channel === 'stable' ? 'main' : 'development'}`;
}
const manifestIdentity = manifestIdentityFor(release.channel);
const manifestIdentityTemplate =
  'https://github.com/${{ github.repository }}/.github/workflows/consolidated-release.yml@refs/heads/${{ inputs.channel == \'stable\' && \'main\' || \'development\' }}';

function ownerApi(overrides = {}) {
  const values = {
    'actions/runs/42': { id: 42, run_attempt: 1, status: 'in_progress',
      event: 'workflow_dispatch', path: '.github/workflows/consolidated-release.yml', workflow_id: 9,
      head_branch: 'development', head_sha: sha, actor: owner, triggering_actor: owner,
      repository: repo, head_repository: repo },
    'actions/workflows/consolidated-release.yml': { id: 9, path: '.github/workflows/consolidated-release.yml', state: 'active' },
    'collaborators/jpapiez/permission': { user: owner, permission: 'admin', role_name: 'admin' },
    'environments/release-insider': environment,
    'environments/release-insider/deployment-branch-policies': policies,
    ...overrides,
  };
  return async endpoint => {
    assert.ok(Object.hasOwn(values, endpoint), `Unexpected owner API endpoint ${endpoint}`);
    return structuredClone(values[endpoint]);
  };
}

function workspace(t) {
  const root = mkdtempSync(join(tmpdir(), 'printfarmer-release-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  return root;
}

test('explicit version matches channel and VERSION without allocating any state', () => {
  assert.equal(validateVersion('1.2.3', 'stable', 'v1.2.3\n').baseVersion, '1.2.3');
  assert.equal(validateVersion(release.version, 'insider', 'v1.2.3\r\n').sequence, '2');
  for (const [version, channel, base] of [
    ['0.2.3', 'insider', 'v0.2.3'], ['1.2.3-insider.2', 'stable', 'v1.2.3'],
    ['1.2.3-beta.2', 'insider', 'v1.2.3'], ['1.2.3-rc.2', 'insider', 'v1.2.3'],
    ['1.2.4', 'stable', 'v1.2.3'], ['1.2.3-insider.0', 'insider', 'v1.2.3'],
    ['1.2.3-insider.01', 'insider', 'v1.2.3'], ['01.2.3', 'stable', 'v01.2.3'],
    ['1.2.3\n', 'stable', 'v1.2.3'], ['1.2.3', 'stable', 'v1.2.3\n\n'],
    ['1.2.3', 'unknown', 'v1.2.3'], ['101.2.3', 'stable', 'v101.2.3'],
  ]) assert.throws(() => validateVersion(version, channel, base));
  assert.equal(compareVersions('1.2.3-insider.10', '1.2.3-insider.2'), 1);
  assert.equal(compareVersions('1.2.3', '1.2.3-insider.99'), 1);
});

test('owner manual access verifies live identity, original inputs and existing environment policy', async () => {
  await verifyOwnerDispatch(env, ownerApi(), event);
  for (const override of [
    { GITHUB_RUN_ATTEMPT: '2' }, { GITHUB_EVENT_NAME: 'push' }, { GITHUB_REF: 'refs/heads/main' },
    { GITHUB_ACTOR_ID: '1' }, { GITHUB_TRIGGERING_ACTOR: 'another' },
    { GITHUB_WORKFLOW_REF: env.GITHUB_WORKFLOW_REF.replace('consolidated-release', 'other') },
    { RELEASE_APPROVAL_MODE: '' }, { RELEASE_VERSION: '1.2.3-insider.3' },
    { GITHUB_WORKFLOW_SHA: 'not-a-sha' }, { GITHUB_SHA: 'not-a-sha' },
  ]) await assert.rejects(verifyOwnerDispatch({ ...env, ...override }, ownerApi(), event));
  // The dispatched branch/head identity (GITHUB_SHA vs. the live run's head_sha) is
  // verified independently of the workflow definition's own SHA (GITHUB_WORKFLOW_SHA),
  // which legitimately differs from it on a normal dispatch (see env fixture above).
  await assert.rejects(verifyOwnerDispatch({ ...env, GITHUB_SHA: head }, ownerApi(), event));
  await assert.rejects(verifyOwnerDispatch(env, ownerApi(), { ...event, sender: { ...owner, id: 1 } }));
  await assert.rejects(verifyOwnerDispatch(env, ownerApi(), { ...event,
    inputs: { ...event.inputs, operation: 'abandon' } }));
  await assert.rejects(verifyOwnerDispatch(env, ownerApi({
    'collaborators/jpapiez/permission': { user: owner, permission: 'write', role_name: 'write' },
  }), event));
  for (const changed of [
    { ...environment, can_admins_bypass: true },
    { ...environment, protection_rules: [] },
    { ...environment, name: 'release-stable' },
  ]) assert.throws(() => verifyEnvironmentRestrictions(changed, policies, 'insider'));
  assert.throws(() => verifyEnvironmentRestrictions(environment, {
    total_count: 1, branch_policies: [{ name: '*', type: 'branch' }],
  }, 'insider'));
});

test('stable owner dispatch runs from main and reaches the channel-aware signing identity', async () => {
  const stableChannel = 'stable';
  const stableVersion = '1.2.3';
  const stableEnv = { ...env, GITHUB_REF: 'refs/heads/main',
    GITHUB_WORKFLOW_REF: `${repo.full_name}/.github/workflows/consolidated-release.yml@refs/heads/main`,
    RELEASE_CHANNEL: stableChannel, RELEASE_VERSION: stableVersion };
  const stableEvent = { sender: owner, repository: repo, ref: 'main',
    inputs: { channel: stableChannel, version: stableVersion, source_sha: '' } };
  const stableEnvironment = { name: 'release-stable', can_admins_bypass: false,
    deployment_branch_policy: { custom_branch_policies: true, protected_branches: false },
    protection_rules: [{ type: 'branch_policy' }] };
  const stablePolicies = { total_count: 1, branch_policies: [{ name: 'main', type: 'branch' }] };
  const stableApi = ownerApi({
    'actions/runs/42': { id: 42, run_attempt: 1, status: 'in_progress',
      event: 'workflow_dispatch', path: '.github/workflows/consolidated-release.yml', workflow_id: 9,
      head_branch: 'main', head_sha: sha, actor: owner, triggering_actor: owner,
      repository: repo, head_repository: repo },
    'environments/release-stable': stableEnvironment,
    'environments/release-stable/deployment-branch-policies': stablePolicies,
  });
  await verifyOwnerDispatch(stableEnv, stableApi, stableEvent);
  assert.doesNotThrow(() => verifyEnvironmentRestrictions(stableEnvironment, stablePolicies, stableChannel));
  // Integration assertion: the exact dispatch just verified for `stable` ran from
  // `main`; the channel's Cosign signing identity (see manifestSignatureIdentity
  // in publish-release.mjs) must reference that same branch, and never
  // `development`, so dispatch/source-branch policy and signing identity can
  // never contradict one another for a given channel.
  const identity = manifestIdentityFor(stableChannel);
  assert.match(identity, /@refs\/heads\/main$/);
  assert.equal(identity.includes('refs/heads/development'), false);
  assert.equal(stableEnv.GITHUB_REF, `refs/heads/${identity.split('@refs/heads/')[1]}`);
  // Each channel's branch policy and signing identity move together: a stable
  // dispatch from `development`, and an insider dispatch from `main`, both reject.
  await assert.rejects(verifyOwnerDispatch({ ...stableEnv, GITHUB_REF: 'refs/heads/development',
    GITHUB_WORKFLOW_REF: stableEnv.GITHUB_WORKFLOW_REF.replace('main', 'development') }, stableApi, stableEvent));
  await assert.rejects(verifyOwnerDispatch({ ...stableEnv, RELEASE_CHANNEL: 'insider' }, stableApi, stableEvent));
  assert.throws(() => verifyEnvironmentRestrictions(stableEnvironment, policies, stableChannel));
  assert.throws(() => verifyEnvironmentRestrictions(environment, stablePolicies, 'insider'));
});

test('GitHub absence is only 404; authorization, rate-limit and transport failures block', async () => {
  for (const status of [401, 403, 429, 500]) {
    const api = githubClient('test-token', async () => ({ status, ok: false }));
    await assert.rejects(api('git/ref/tags/v1.2.3', { allowMissing: true }), new RegExp(String(status)));
  }
  assert.equal(await githubClient('test-token', async () => ({ status: 404, ok: false }))
    ('git/ref/tags/v1.2.3', { allowMissing: true }), undefined);
  await assert.rejects(githubClient('test-token', async () => { throw new Error('offline'); })('releases'), /offline/);
});

test('selection pins correct canonical source and ignores missing historical ledger artifacts', async () => {
  for (const channel of ['stable', 'insider']) {
    const paths = [];
    const selectedEnv = { ...env, RELEASE_CHANNEL: channel,
      RELEASE_VERSION: channel === 'stable' ? '1.2.3' : release.version };
    const api = async endpoint => {
      paths.push(endpoint);
      if (endpoint.startsWith('git/ref/heads/')) return { object: { sha } };
      if (endpoint.startsWith('contents/VERSION')) return { encoding: 'base64', content: Buffer.from('v1.2.3').toString('base64') };
      if (endpoint.startsWith('git/ref/tags/') || endpoint.startsWith('releases/tags/')) return undefined;
      throw new Error(`Unexpected endpoint ${endpoint}`);
    };
    const selected = await selectRelease(selectedEnv, api);
    assert.equal(selected.sourceCommit, sha);
    assert.ok(paths.includes(`git/ref/heads/${channel === 'stable' ? 'main' : 'development'}`));
    assert.ok(paths.every(path => !/ledger|artifact|reservation/.test(path)));
  }
});

test('selected ancestor stays pinned after branch movement; unrelated or malformed sources reject', async () => {
  const api = async endpoint => {
    if (endpoint === 'git/ref/heads/development') return { object: { sha: head } };
    if (endpoint === `compare/${sha}...${head}`) return { status: 'ahead' };
    if (endpoint.startsWith('contents/VERSION')) return { encoding: 'base64', content: Buffer.from('v1.2.3').toString('base64') };
    return undefined;
  };
  assert.equal((await selectRelease({ ...env, RELEASE_SELECTED_SOURCE: sha }, api)).sourceCommit, sha);
  for (const status of ['behind', 'diverged', undefined]) {
    await assert.rejects(selectRelease({ ...env, RELEASE_SOURCE_SHA: sha },
      async endpoint => endpoint.startsWith('compare/') ? { status } : api(endpoint)), /ancestor/);
  }
  await assert.rejects(selectRelease({ ...env, RELEASE_SOURCE_SHA: 'development' }, api), /full lowercase/);
  await assert.rejects(selectRelease({ ...env, RELEASE_SOURCE_SHA: head, RELEASE_SELECTED_SOURCE: sha }, api),
    /differs from the requested source/);
});

test('permanent tag, existing release and even matching immutable image tags reject reuse', async () => {
  for (const existsAt of ['git/ref/tags/', 'releases/tags/']) {
    await assert.rejects(rejectExistingVersion(async endpoint => endpoint.startsWith(existsAt) ? { id: 1 } : undefined,
      'v1.2.3-insider.1'), /already exists/);
  }
  assert.throws(() => rejectExistingImages(release.version, () => ({ digest, version: release.version })), /already exists/);
});

test('registry lookup fails closed on auth/network/invalid data rather than treating it as an absent version', () => {
  const fake = (status, stderr = '', stdout = '') => () => ({ status, stderr, stdout });
  assert.equal(inspectTag('image:v1', fake(1, 'manifest unknown')), undefined);
  for (const error of ['unauthorized: not found', 'connection refused', 'no such host', 'timeout', 'permission denied']) {
    assert.throws(() => inspectTag('image:v1', fake(1, error)), /Cannot inspect/);
  }
  assert.throws(() => inspectTag('image:v1', fake(0, '', '{}')), /Invalid registry digest/);
});

function imageInspection(name, version = release.version) {
  return {
    manifests: components[name].platforms.flatMap(platform => {
      const [os, architecture] = platform.split('/');
      return [
        { digest: platformDigest, platform: { os, architecture } },
        { digest, annotations: { 'vnd.docker.reference.type': 'attestation-manifest',
          'vnd.docker.reference.digest': platformDigest } },
      ];
    }),
    config: { Labels: { 'org.opencontainers.image.version': version, 'org.opencontainers.image.revision': sha,
      'org.printfarmer.release-channel': version.includes('-') ? 'insider' : 'stable' } },
  };
}

function inspectionCommand(_name, args) {
  const reference = args[3];
  const name = Object.keys(components).find(key => reference.startsWith(`${imageRepository(key)}@`));
  assert.ok(name, reference);
  return JSON.stringify(imageInspection(name));
}

test('complete image verification enforces exact platform set, provenance and source labels', () => {
  verifyImages(release.version, sha, digests, inspectionCommand);
  assert.throws(() => verifyImages(release.version, sha, { api: digest }, inspectionCommand), /Incomplete/);
  for (const mutation of [
    image => { image.manifests.pop(); image.manifests.pop(); },
    image => { image.manifests = image.manifests.filter(item => !item.annotations); },
    image => { image.manifests.push(image.manifests[0]); },
    image => { image.config.Labels['org.opencontainers.image.revision'] = head; },
    image => { image.config.Labels['org.opencontainers.image.version'] = '0.2.2'; },
  ]) assert.throws(() => verifyImages(release.version, sha, digests, (name, args) => {
    const value = JSON.parse(inspectionCommand(name, args));
    mutation(value);
    return JSON.stringify(value);
  }));
});

test('managed update manifest is canonical, complete, sequence-bound and child-digest pinned', () => {
  const first = buildManifest(release, imageDetails);
  const second = buildManifest(release, imageDetails);
  assert.equal(first, second);
  const manifest = JSON.parse(first);
  assert.equal(manifest.schema, 1);
  assert.equal(manifest.managedUpdateEligible, true);
  assert.equal(manifest.sequence, deriveSequence(release.version));
  assert.equal(manifest.minimumUpdaterVersion, MINIMUM_UPDATER_VERSION);
  assert.deepEqual(manifest.services.map(service => service.id), Object.keys(components));
  assert.deepEqual(manifest.platforms, ['linux-amd64', 'linux-arm64']);
  for (const service of manifest.services) {
    assert.match(service.image, new RegExp(`^ghcr\\.io/olyforge3d/printfarmer-${service.id}@sha256:`));
    assert.deepEqual(service.platforms, components[service.id].platforms.map(platform => platform.replaceAll('/', '-')));
  }
  assert.deepEqual(manifest.services.find(service => service.id === 'orcaslicer-worker').platforms, ['linux-amd64']);
  assert.deepEqual(Object.keys(manifest.platformDigests), [
    'api/linux-amd64', 'api/linux-arm64',
    'frontend/linux-amd64', 'frontend/linux-arm64',
    'slicer-host/linux-amd64', 'slicer-host/linux-arm64',
    'printer-discovery/linux-amd64', 'printer-discovery/linux-arm64',
    'orcaslicer-worker/linux-amd64',
    'monolith/linux-amd64', 'monolith/linux-arm64',
  ]);
  validateManifestInput({ ...release, sequence: deriveSequence(release.version) }, imageDetails);
  validateManifest(first, { ...release, sequence: deriveSequence(release.version) }, digests);
  for (const mutation of [
    () => validateManifestInput(release, { ...imageDetails, api: undefined }),
    () => validateManifestInput(release, { ...imageDetails, api: { ...imageDetails.api, indexDigest: 'latest' } }),
    () => validateManifestInput(release, { ...imageDetails, api: { ...imageDetails.api,
      platformDigests: { ...imageDetails.api.platformDigests, 'linux/amd64': 'sha256:bad' } } }),
    () => validateManifestInput(release, { ...imageDetails, api: { ...imageDetails.api,
      platforms: ['linux/amd64', 'linux/amd64'] } }),
  ]) assert.throws(mutation);
  for (const mutation of [
    value => value.replace('ghcr.io/olyforge3d/printfarmer-api@', 'docker.io/example/api@'),
    value => value.replace('ghcr.io/olyforge3d/printfarmer-api@sha256:', 'ghcr.io/olyforge3d/printfarmer-api:'),
    value => value.replace('"id":"frontend"', '"id":"api"'),
    value => value.replace(`"orcaslicer-worker/linux-amd64":"sha256:${'d'.repeat(64)}"`, '"orcaslicer-worker/linux-amd64":"bad"'),
    value => value.replace('"platforms":["linux-amd64","linux-arm64"],"platformDigests"',
      '"platforms":["api-linux-amd64"],"platformDigests"'),
    value => value.replace('"platforms":["linux-amd64"]', '"platforms":["linux-amd64","linux-arm64"]'),
    value => value.replace(',"minimumUpdaterVersion":"0.0.0"', ''),
  ]) assert.throws(() => validateManifest(mutation(first)));
  assert.throws(() => validateManifest(first, { ...release, version: '1.2.3-insider.3',
    tag: 'v1.2.3-insider.3', sequence: deriveSequence('1.2.3-insider.3') }, digests));
  assert.throws(() => validateManifest(first, { ...release, sequence: deriveSequence(release.version) },
    { ...digests, api: platformDigest }));
  assert.throws(() => validateManifest(first, release, digests, {
    ...imageDetails,
    api: { ...imageDetails.api, platformDigests: { ...imageDetails.api.platformDigests,
      'linux/amd64': `sha256:${'e'.repeat(64)}` } },
  }));
});

test('shared update manifest fixture is the exact generated cross-language contract', () => {
  const schema = JSON.parse(readFileSync(manifestSchemaPath, 'utf8'));
  const fixtureRelease = {
    tag: manifestFixture.tag,
    version: manifestFixture.version,
    channel: manifestFixture.channel,
    sourceBranch: manifestFixture.sourceBranch,
    sourceCommit: manifestFixture.sourceCommit,
    buildId: manifestFixture.buildId,
    sequence: manifestFixture.sequence,
  };
  const fixtureImageDetails = Object.fromEntries(manifestFixture.services.map(service => {
    const policy = components[service.id];
    return [service.id, {
      indexDigest: service.image.split('@')[1],
      platforms: [...policy.platforms],
      platformDigests: Object.fromEntries(policy.platforms.map(platform => [
        platform,
        manifestFixture.platformDigests[`${service.id}/${platform.replaceAll('/', '-')}`],
      ])),
    }];
  }));
  const fixtureDigests = Object.fromEntries(manifestFixture.services.map(service =>
    [service.id, service.image.split('@')[1]]));
  const schemaServiceDefinitions = schema.properties.services.prefixItems.map(item =>
    schema.$defs[item.$ref.slice('#/$defs/'.length)]);

  assert.deepEqual(manifestFixture.services.map(service => service.id), Object.keys(components));
  assert.deepEqual(schemaServiceDefinitions.map(definition => definition.properties.id.const), Object.keys(components));
  assert.deepEqual(manifestFixture.platforms, schema.properties.platforms.const);
  assert.deepEqual(Object.keys(manifestFixture.platformDigests), schema.properties.platformDigests.required);
  assert.ok(schema.required.includes('minimumUpdaterVersion'));
  assert.equal(manifestFixture.minimumUpdaterVersion, MINIMUM_UPDATER_VERSION);
  assert.equal(manifestFixture.sequence, 10020000300042);
  assert.equal(schema.properties.sequence.maximum, Number.MAX_SAFE_INTEGER);
  assert.ok(Number.isSafeInteger(manifestFixture.sequence));
  assert.ok(BigInt(manifestFixture.sequence) <= 9223372036854775807n);
  assert.equal(deriveSequence(manifestFixture.version), manifestFixture.sequence);
  assert.deepEqual(manifestFixture.services.find(service => service.id === 'orcaslicer-worker').platforms,
    ['linux-amd64']);
  assert.equal(typeof manifestFixture.buildId, 'string');
  for (const [index, service] of manifestFixture.services.entries()) {
    const definition = schemaServiceDefinitions[index];
    const expectedPlatforms = definition.properties.platforms?.const ??
      schema.$defs.dualPlatformService.properties.platforms.const;
    assert.match(service.image, new RegExp(definition.properties.image.pattern));
    assert.deepEqual(service.platforms, expectedPlatforms);
  }

  const generated = Buffer.from(buildManifest(fixtureRelease, fixtureImageDetails));
  assert.equal(Buffer.compare(generated, manifestFixtureBytes), 0);
  validateManifest(manifestFixtureBytes, fixtureRelease, fixtureDigests, fixtureImageDetails);
});

test('language-neutral golden contract defines parsing, sequences, ordering, bounds and collisions', () => {
  const schema = JSON.parse(readFileSync(versionSchemaPath, 'utf8'));
  assert.equal(versionFixture.$schema, './release-version-sequence.schema.json');
  assert.equal(versionFixture.schemaVersion, 1);
  assert.equal(schema.properties.schemaVersion.const, versionFixture.schemaVersion);
  assert.equal(versionFixture.contract.manifestSequenceJsonType, 'integer');
  assert.equal(versionFixture.contract.implementationType, 'signed 64-bit integer');
  assert.deepEqual(versionFixture.contract.limits, {
    major: SEQUENCE_MAJOR_MAX,
    minor: SEQUENCE_MINOR_MAX,
    patch: SEQUENCE_PATCH_MAX,
    insiderSequence: SEQUENCE_PRERELEASE_MAX,
    stableSuffix: SEQUENCE_STABLE_SUFFIX,
  });

  for (const golden of versionFixture.validCases) {
    const parsed = parseTag(`v${golden.version}`);
    assert.deepEqual({
      major: Number(parsed.major),
      minor: Number(parsed.minor),
      patch: Number(parsed.patch),
      kind: parsed.stage ?? 'stable',
      suffix: parsed.stage ? Number(parsed.sequence) : SEQUENCE_STABLE_SUFFIX,
    }, golden.parsed, golden.name);
    const sequence = deriveSequence(golden.version);
    assert.equal(String(sequence), golden.expectedSequence, golden.name);
    assert.ok(Number.isSafeInteger(sequence), `${golden.name} produced an unsafe JSON integer`);
  }

  for (const golden of versionFixture.invalidCases) {
    assert.throws(() => deriveSequence(golden.version), new RegExp(golden.errorContains), golden.name);
  }
  for (const golden of versionFixture.ordering) {
    assert.ok(deriveSequence(golden.lower) < deriveSequence(golden.higher), golden.name);
  }
  for (const golden of versionFixture.distinctGroups) {
    const sequences = golden.versions.map(deriveSequence);
    assert.equal(new Set(sequences).size, sequences.length, golden.name);
  }
  const maximum = BigInt(versionFixture.validCases.at(-1).expectedSequence);
  assert.ok(maximum <= 9223372036854775807n && maximum <= BigInt(Number.MAX_SAFE_INTEGER));
});

test('actual build loop passes the six targets/platforms and source metadata, stops on partial failure', t => {
  const root = workspace(t);
  const source = join(root, 'source');
  const assets = join(root, 'assets');
  mkdirSync(join(source, 'src'), { recursive: true });
  writeFileSync(join(source, 'VERSION'), 'v1.2.3');
  for (const file of ['LICENSE', 'THIRD-PARTY-NOTICES.md']) writeFileSync(join(source, file), file);
  mkdirSync(join(source, '.release-assets'));
  writeFileSync(join(source, '.release-assets', 'preserved.txt'), 'preserve this source content');
  const builds = [];
  const smokes = [];
  const run = (name, args) => {
    if (name === 'git') return sha;
    if (name === 'node' && args[0] === 'scripts/compliance/create-source-bundle.mjs') {
      const outputDirectory = args[args.indexOf('--output') + 1];
      assert.ok(!relative(source, outputDirectory).startsWith('..'));
      assert.ok(outputDirectory.includes('.release-assets-'));
      mkdirSync(outputDirectory, { recursive: true });
      writeFileSync(join(outputDirectory, `PrintFarmer-${release.tag}-source.tar.gz`), 'archive');
      writeFileSync(join(outputDirectory, `PrintFarmer-${release.tag}-source.json`), 'manifest');
    }
    if (name === 'docker' && args[1] === 'build') {
      builds.push(args);
      writeFileSync(args[args.indexOf('--metadata-file') + 1], JSON.stringify({ 'containerimage.digest': digest }));
    } else if (name === 'docker' && args[1] === 'imagetools') return inspectionCommand(name, args);
    else if (name === 'docker' && args[0] === 'run') smokes.push(args);
    else if (name === 'syft') writeFileSync(args[2].slice('spdx-json='.length), '{}');
    return '';
  };
  assert.deepEqual(buildImages(release, source, assets, run, () => {}), digests);
  for (const file of [`PrintFarmer-${release.tag}-source.tar.gz`, `PrintFarmer-${release.tag}-source.json`]) {
    assert.ok(existsSync(join(assets, file)));
  }
  assert.ok(existsSync(join(source, '.release-assets', 'preserved.txt')));
  assert.equal(builds.length, 6);
  assert.equal(smokes.length, 5);
  for (const [index, [, component]] of Object.entries(components).entries()) {
    const args = builds[index];
    assert.equal(args[args.indexOf('--target') + 1], component.target);
    assert.equal(args[args.indexOf('--platform') + 1], component.platforms.join(','));
    assert.ok(args.includes(`GIT_SHA=${sha}`) && args.includes(`VITE_GIT_SHA=${sha}`));
    assert.ok(args.includes(`BUILD_VERSION=${release.version}`));
    assert.ok(args.includes('--sbom=true') && args.includes('--provenance=mode=max'));
    assert.ok(!args.includes('--tag'));
  }
  const metadata = JSON.parse(readFileSync(join(assets, 'container-images.json')));
  assert.equal(metadata.managedUpdateEligible, false);
  assert.equal(Object.keys(metadata.images).length, 6);
  assert.ok(!existsSync(join(assets, 'release-manifest.json')));
  const before = builds.length;
  assert.throws(() => buildImages(release, source, assets, (name, args) => {
    if (name === 'docker' && args.includes('frontend-runtime')) throw new Error('frontend build failed');
    return run(name, args);
  }, () => {}), /frontend build failed/);
  assert.equal(builds.length - before, 1);
});

function publishFixture(t, channel = 'insider') {
  const assets = workspace(t);
  const chosen = channel === 'stable' ? { ...release, channel, version: '1.2.3', tag: 'v1.2.3', sourceBranch: 'main' } : release;
  const files = releaseAssets(chosen);
  for (const file of files) writeFileSync(join(assets, file), 'asset');
  writeFileSync(join(assets, 'update-manifest.json'), buildManifest(
    { ...chosen, sequence: deriveSequence(chosen.version) }, imageDetails));
  const signedManifestBytes = readFileSync(join(assets, 'update-manifest.json'));
  writeFileSync(join(assets, 'update-manifest.sigstore.json'), JSON.stringify({
    sha256: createHash('sha256').update(signedManifestBytes).digest('hex'),
    issuer: manifestIssuer,
    identity: manifestIdentityFor(chosen.channel),
  }));
  writeFileSync(join(assets, 'digests.json'), JSON.stringify(digests));
  const calls = [];
  const api = async (endpoint, options = {}) => {
    calls.push({ endpoint, ...options });
    if (endpoint === 'releases/generate-notes') return { body: '* A real change' };
    if (endpoint.startsWith('git/ref/tags/') || endpoint.startsWith('releases/tags/') || endpoint === 'releases/latest') return undefined;
    if (endpoint === 'git/refs') return {};
    if (endpoint === 'releases') return { id: 99 };
    if (endpoint === 'releases/99/assets?per_page=100') return files.map(name => {
      const bytes = readFileSync(join(assets, name));
      return { name, state: 'uploaded', size: bytes.length,
        digest: `sha256:${createHash('sha256').update(bytes).digest('hex')}` };
    });
    if (endpoint === 'releases/99') return { draft: false, tag_name: chosen.tag,
      prerelease: channel === 'insider', html_url: `https://github.com/${repo.full_name}/releases/tag/${chosen.tag}` };
    throw new Error(`Unexpected endpoint ${endpoint}`);
  };
  const deps = { run: (name, args) => {
    calls.push({ command: name, args });
    if (name !== 'cosign') return;
    const bundle = JSON.parse(readFileSync(args[2], 'utf8'));
    const manifestBytes = readFileSync(args[7]);
    assert.equal(bundle.sha256, createHash('sha256').update(manifestBytes).digest('hex'));
    assert.equal(bundle.issuer, args[4]);
    assert.equal(bundle.identity, args[6]);
  },
    verify: () => { calls.push({ verify: true }); return imageDetails; }, rejectImages: () => calls.push({ rejectImages: true }),
    tagImages: () => calls.push({ tagImages: true }) };
  return { assets, chosen, files, calls, api, deps, signedManifestBytes };
}

test('publication creates tag once, uploads a complete draft, tags images, publishes notes last', async t => {
  for (const channel of ['stable', 'insider']) {
    const fixture = publishFixture(t, channel);
    const { chosen, assets, api, deps, calls } = fixture;
    assert.match(await publishRelease(chosen, assets, api, deps), /releases\/tag/);
    const draft = calls.find(call => call.endpoint === 'releases');
    assert.equal(draft.body.draft, true);
    assert.equal(draft.body.prerelease, channel === 'insider');
    assert.match(draft.body.body, /Manual installation only/);
    assert.equal(calls.at(-2).tagImages, true);
    assert.deepEqual(calls.at(-1), { endpoint: 'releases/99', method: 'PATCH',
      body: { draft: false, make_latest: String(channel === 'stable') } });
    assert.equal(calls.filter(call => call.endpoint === 'git/refs').length, 1);
    assert.ok(calls.every(call => !/ledger|authorization|reservation/.test(call.endpoint ?? '')));
    assert.ok(calls.every(call => call.method !== 'DELETE'));
    assert.ok(!calls.find(call => call.command)?.args.includes('--clobber'));
  }
});

test('the exact manifest bytes and its signature bundle are re-verified immediately before upload', async t => {
  const { chosen, assets, api, deps, calls, signedManifestBytes } = publishFixture(t);
  await publishRelease(chosen, assets, api, deps);
  const commands = calls.filter(call => call.command);
  const cosignIndex = commands.findIndex(call => call.command === 'cosign');
  const ghIndex = commands.findIndex(call => call.command === 'gh');
  assert.ok(cosignIndex !== -1 && ghIndex !== -1, 'verification and upload commands must run');
  assert.equal(ghIndex, cosignIndex + 1, 'cosign verify-blob must be immediately before gh release upload');
  const cosignArgs = commands[cosignIndex].args;
  assert.equal(cosignArgs[0], 'verify-blob');
  assert.equal(cosignArgs[1], '--bundle');
  assert.match(cosignArgs[2], /update-manifest\.sigstore\.json$/);
  assert.equal(cosignArgs[3], '--certificate-oidc-issuer');
  assert.equal(cosignArgs[4], manifestIssuer);
  assert.equal(cosignArgs[5], '--certificate-identity');
  assert.equal(cosignArgs[6], manifestIdentity);
  assert.match(cosignArgs[7], /update-manifest\.json$/);
  assert.deepEqual(readFileSync(cosignArgs[7]), signedManifestBytes);
});

test('missing, tampered or wrong-identity signing evidence blocks publication before upload', async t => {
  const corruptions = [
    ['missing manifest', (assets) => rmSync(join(assets, 'update-manifest.json'))],
    ['missing bundle', (assets) => rmSync(join(assets, 'update-manifest.sigstore.json'))],
    ['tampered manifest', (assets) => writeFileSync(join(assets, 'update-manifest.json'),
      `${readFileSync(join(assets, 'update-manifest.json'), 'utf8')} `)],
    ['tampered bundle', (assets) => {
      const path = join(assets, 'update-manifest.sigstore.json');
      writeFileSync(path, JSON.stringify({ ...JSON.parse(readFileSync(path, 'utf8')), sha256: '0'.repeat(64) }));
    }],
    ['wrong issuer', (assets) => {
      const path = join(assets, 'update-manifest.sigstore.json');
      writeFileSync(path, JSON.stringify({ ...JSON.parse(readFileSync(path, 'utf8')), issuer: 'https://example.invalid' }));
    }],
    ['wrong workflow identity', (assets) => {
      const path = join(assets, 'update-manifest.sigstore.json');
      writeFileSync(path, JSON.stringify({ ...JSON.parse(readFileSync(path, 'utf8')),
        identity: manifestIdentity.replace('/PrintFarmer/', '/OtherRepository/') }));
    }],
  ];
  for (const [name, corrupt] of corruptions) {
    const fixture = publishFixture(t);
    const { assets, chosen, calls, api, deps } = fixture;
    const draftingApi = async (endpoint, options) => {
      const result = await api(endpoint, options);
      if (endpoint === 'releases') corrupt(assets);
      return result;
    };
    await assert.rejects(publishRelease(chosen, assets, draftingApi, deps));
    assert.ok(!calls.some(call => call.command === 'gh'), `gh upload must not run for ${name}`);
  }
});

test('duplicate race, upload error, incomplete assets and partial image tagging never finalize a release', async t => {
  for (const fail of ['duplicate-race', 'upload', 'inventory', 'digest', 'images']) {
    const fixture = publishFixture(t);
    const { assets, chosen, calls, api, deps } = fixture;
    const failedApi = async (endpoint, options) => {
      if (fail === 'duplicate-race' && endpoint === 'git/refs') throw new Error('422 ref exists');
      if (fail === 'inventory' && endpoint.includes('/assets?')) return [];
      if (fail === 'digest' && endpoint.includes('/assets?')) {
        return (await api(endpoint, options)).map(asset => ({ ...asset, digest }));
      }
      return api(endpoint, options);
    };
    if (fail === 'upload') {
      const verify = deps.run;
      deps.run = (name, args) => name === 'gh' ? (() => { throw new Error('upload failed'); })() : verify(name, args);
    }
    if (fail === 'images') deps.tagImages = () => { throw new Error('second image failed'); };
    await assert.rejects(publishRelease(chosen, assets, failedApi, deps));
    assert.ok(!calls.some(call => call.method === 'PATCH'));
  }
});

test('an older stable release cannot replace GitHub latest', async t => {
  const { assets, chosen, calls, api, deps } = publishFixture(t, 'stable');
  await publishRelease(chosen, assets,
    (endpoint, options) => endpoint === 'releases/latest' ? { tag_name: 'v2.0.0' } : api(endpoint, options), deps);
  assert.equal(calls.at(-1).body.make_latest, 'false');
});

test('image aliases stay channel-correct, never regress, and version tags never overwrite', () => {
  for (const version of [release.version, '1.2.3']) {
    const registry = new Map();
    const created = [];
    const inspect = ref => registry.get(ref);
    if (version === '1.2.3') {
      for (const name of Object.keys(components)) registry.set(`${imageRepository(name)}:latest`,
        { digest: platformDigest, version: '2.0.0' });
    }
    const run = (_name, args) => {
      const ref = args[args.indexOf('--tag') + 1];
      created.push(ref);
      registry.set(ref, { digest, version });
    };
    publishImageTags(version, digests, inspect, run);
    assert.equal(created.length, version === release.version ? 6 : 24);
    assert.ok(created.every(tag => !tag.endsWith(':latest')));
    if (version === release.version) assert.ok(created.every(tag => tag.endsWith(`:${version}`)));
    assert.throws(() => publishImageTags(version, digests, inspect, run), /already exists/);
  }
  const registry = new Map(Object.keys(components).map(name =>
    [`${imageRepository(name)}:latest`, { digest, version: release.version }]));
  assert.throws(() => publishImageTags('1.2.3', digests, reference => registry.get(reference),
    (_name, args) => registry.set(args[args.indexOf('--tag') + 1], { digest, version: '1.2.3' })), /Cross-channel/);
});

test('build metadata reports version/source without fabrication of signed allocation or managed identity', () => {
  const metadata = buildMetadata(release);
  assert.match(metadata.props, /<Version>1.2.3-insider.2<\/Version>/);
  assert.equal(JSON.parse(metadata.frontend).canonicalVersion, release.version);
  assert.doesNotMatch(JSON.stringify(metadata), /allocation|stableSequence|releaseId|signature|managedEligible/);
});

test('actual workflow connects inputs, pinned source checks, environment, build and publish-last outputs', () => {
  const workflow = load(readFileSync('.github/workflows/consolidated-release.yml', 'utf8'));
  assert.deepEqual(Object.keys(workflow.on), ['workflow_dispatch']);
  assert.deepEqual(Object.keys(workflow.on.workflow_dispatch.inputs), ['channel', 'version', 'source_sha']);
  assert.deepEqual(workflow.concurrency, { group: 'server-release', 'cancel-in-progress': false });
  assert.equal(workflow.env.RELEASE_VERSION, '${{ inputs.version }}');
  assert.equal(workflow.jobs.select.outputs.source_sha, '${{ steps.select.outputs.source_sha }}');
  assert.equal(workflow.jobs.checks.with.source_sha, '${{ needs.select.outputs.source_sha }}');
  assert.equal(workflow.jobs.checks.with.release_qualification, true);
  assert.deepEqual(workflow.jobs.publish.needs, ['select', 'checks']);
  assert.equal(workflow.jobs.publish.environment, '${{ needs.select.outputs.environment }}');
  assert.equal(workflow.jobs.publish.permissions['id-token'], 'write');
  assert.equal(workflow.jobs.select.permissions['id-token'], undefined);
  assert.equal(workflow.jobs.publish.env.RELEASE_SELECTED_SOURCE, '${{ needs.select.outputs.source_sha }}');
  const steps = workflow.jobs.publish.steps;
  assert.equal(steps.find(step => step.name === 'Checkout pinned application source').with.ref,
    '${{ needs.select.outputs.source_sha }}');
  const build = steps.findIndex(step => step.run === 'node scripts/ci/publish-release.mjs build');
  const sign = steps.findIndex(step => step.name === 'Sign and verify exact update manifest');
  const reverify = steps.findIndex(step => step.name === 'Re-verify exact manifest before publication');
  const mint = steps.findIndex(step => step.id === 'publisher');
  const publish = steps.findIndex(step => step.run === 'node scripts/ci/publish-release.mjs publish');
  assert.ok(build < sign && sign < reverify && reverify < mint && mint < publish);
  assert.equal(steps[sign].env.MANIFEST, 'release-assets/update-manifest.json');
  assert.equal(steps[sign].env.BUNDLE, 'release-assets/update-manifest.sigstore.json');
  assert.equal(steps[sign].env.EXPECTED_IDENTITY, manifestIdentityTemplate);
  assert.match(steps[sign].run, /cosign sign-blob --yes --bundle "\$BUNDLE" "\$MANIFEST"/);
  assert.match(steps[sign].run, /--certificate-oidc-issuer https:\/\/token\.actions\.githubusercontent\.com/);
  assert.match(steps[sign].run, /--certificate-identity "\$EXPECTED_IDENTITY" "\$MANIFEST"/);
  assert.equal(steps[reverify].env.EXPECTED_IDENTITY, manifestIdentityTemplate);
  assert.match(steps[reverify].run, /--certificate-identity "\$EXPECTED_IDENTITY" release-assets\/update-manifest\.json/);
  assert.equal(steps[mint].with['permission-workflows'], 'write');
  assert.equal(steps[mint].with.repositories, 'PrintFarmer');
  assert.equal(steps[publish].env.GH_TOKEN, '${{ steps.publisher.outputs.token }}');
  assert.equal(workflow.jobs.publish.outputs.release_url, '${{ steps.publish.outputs.release_url }}');
  assert.equal(workflow.jobs.summary.if, 'always()');
  assert.match(workflow.jobs.summary.steps[0].run, /Partial images, tags, aliases or a draft may remain/);
  for (const retired of ['docker-publish.yml', 'qualify-canonical-release.yml', 'record-canonical-qualification.yml']) {
    assert.ok(!existsSync(`.github/workflows/${retired}`));
  }
  const active = ['.github/workflows/consolidated-release.yml', 'scripts/ci/publish-release.mjs',
    'scripts/ci/release-dispatch.mjs', 'scripts/ci/release-set.mjs'].map(file => readFileSync(file, 'utf8')).join('\n');
  assert.match(active, /cosign sign-blob --yes --bundle/);
  assert.match(active, /cosign verify-blob --bundle/);
  assert.match(active, /--certificate-oidc-issuer https:\/\/token\.actions\.githubusercontent\.com/);
  assert.match(active, /--certificate-identity "\$EXPECTED_IDENTITY"/);
  assert.match(active, /update-manifest\.sigstore\.json/);
  assert.match(active, /EXPECTED_IDENTITY: https:\/\/github\.com\/\$\{\{ github\.repository \}\}\/\.github\/workflows\/consolidated-release\.yml@refs\/heads\/\$\{\{ inputs\.channel == 'stable' && 'main' \|\| 'development' \}\}/);
  assert.doesNotMatch(active, /--certificate-oidc-issuer\s+\S+\s+\S+\*/);
  assert.doesNotMatch(active, /RELEASE_LEDGER|release-authorization|release-transaction|reservation_target/);
});
