import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, mkdirSync, readdirSync, readFileSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';
import test from 'node:test';
import { load } from 'js-yaml';
import { components, compareVersions, parseTag, validateVersion, verifyEnvironmentRestrictions } from '../release-policy.mjs';
import { githubClient, verifyOwnerDispatch } from '../release-dispatch.mjs';
import { buildMetadata } from '../release-metadata.mjs';
import { buildImages, publishRelease, releaseAssets, rejectExistingVersion, selectRelease } from '../publish-release.mjs';
import { formatSums, hostUpdateCliArchiveName, hostUpdateCliAssets, hostUpdateCliRuntimes, hostUpdateCliSbomName,
  hostUpdateCliSumsBundleName, hostUpdateCliSumsName, packageHostUpdateCli, parseSums, validateHostUpdateCliSbom,
  verifyHostUpdateCliSums } from '../host-update-cli-package.mjs';

const spdxFixture = name => `${JSON.stringify({ spdxVersion: 'SPDX-2.3', SPDXID: 'SPDXRef-DOCUMENT', name,
  packages: [{ SPDXID: 'SPDXRef-Package', name: 'Farm.HostUpdate.Cli', versionInfo: '1.0.0' }] })}\n`;
import { imageRepository, inspectTag, publishImageTags, rejectExistingImages, verifyImages } from '../release-set.mjs';
import { buildManifest, deriveSequence, validateManifest, validateManifestInput,
  SEQUENCE_MAJOR_MAX, SEQUENCE_MINOR_MAX, SEQUENCE_PATCH_MAX, SEQUENCE_PRERELEASE_MAX,
  SEQUENCE_STABLE_SUFFIX, MINIMUM_UPDATER_VERSION } from '../release-manifest.mjs';

const sha = 'a'.repeat(40);
const head = 'b'.repeat(40);
const workflowSha = 'e'.repeat(40);
const workflowTreeSha = 'f'.repeat(40);
const workflowBytes = Buffer.from('workflow fixture\n');
function gitBlobSha(bytes) {
  return createHash('sha1').update(Buffer.concat([Buffer.from(`blob ${bytes.length}\0`), bytes])).digest('hex');
}
const workflowBlobSha = gitBlobSha(workflowBytes);
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
    [`commits/${workflowSha}`]: { sha: workflowSha, commit: { tree: { sha: workflowTreeSha } } },
    [`git/trees/${workflowTreeSha}?recursive=1`]:
      { tree: [{ path: '.github/workflows/consolidated-release.yml', type: 'blob', sha: workflowBlobSha }] },
    [`contents/.github/workflows/consolidated-release.yml?ref=${workflowSha}`]:
      { type: 'file', path: '.github/workflows/consolidated-release.yml', sha: workflowBlobSha,
        encoding: 'base64', content: workflowBytes.toString('base64') },
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
  assert.equal(validateVersion('0.2.3-insider.1', 'insider', 'v0.2.3\n').sequence, '1');
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
  await verifyOwnerDispatch(env, ownerApi(), event, workflowBytes);
  for (const override of [
    { GITHUB_RUN_ATTEMPT: '2' }, { GITHUB_EVENT_NAME: 'push' }, { GITHUB_REF: 'refs/heads/main' },
    { GITHUB_ACTOR_ID: '1' }, { GITHUB_TRIGGERING_ACTOR: 'another' },
    { GITHUB_WORKFLOW_REF: env.GITHUB_WORKFLOW_REF.replace('consolidated-release', 'other') },
    { RELEASE_APPROVAL_MODE: '' }, { RELEASE_VERSION: '1.2.3-insider.3' },
    { GITHUB_WORKFLOW_SHA: 'not-a-sha' }, { GITHUB_SHA: 'not-a-sha' },
  ]) await assert.rejects(verifyOwnerDispatch({ ...env, ...override }, ownerApi(), event, workflowBytes));
  // The dispatched branch/head identity (GITHUB_SHA vs. the live run's head_sha) is
  // verified independently of the workflow definition's own SHA (GITHUB_WORKFLOW_SHA),
  // which legitimately differs from it on a normal dispatch (see env fixture above).
  await assert.rejects(verifyOwnerDispatch({ ...env, GITHUB_SHA: head }, ownerApi(), event, workflowBytes));
  await assert.rejects(verifyOwnerDispatch(env, ownerApi(), { ...event, sender: { ...owner, id: 1 } }, workflowBytes));
  await assert.rejects(verifyOwnerDispatch(env, ownerApi(), { ...event,
    inputs: { ...event.inputs, operation: 'abandon' } }, workflowBytes));
  await assert.rejects(verifyOwnerDispatch(env, ownerApi({
    'collaborators/jpapiez/permission': { user: owner, permission: 'write', role_name: 'write' },
  }), event, workflowBytes));
  await assert.rejects(verifyOwnerDispatch(env, ownerApi({
    [`contents/.github/workflows/consolidated-release.yml?ref=${workflowSha}`]:
      { type: 'file', path: '.github/workflows/consolidated-release.yml', sha: '1'.repeat(40),
        encoding: 'base64', content: workflowBytes.toString('base64') },
  }), event, workflowBytes));
  await assert.rejects(verifyOwnerDispatch(env, ownerApi({
    [`contents/.github/workflows/consolidated-release.yml?ref=${workflowSha}`]:
      { type: 'file', path: '.github/workflows/consolidated-release.yml', sha: workflowBlobSha,
        encoding: 'base64', content: Buffer.from('tampered\n').toString('base64') },
  }), event, workflowBytes));
  await assert.rejects(verifyOwnerDispatch(env, ownerApi({
    [`commits/${workflowSha}`]: { sha: head, commit: { tree: { sha: workflowTreeSha } } },
  }), event, workflowBytes));
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
  await verifyOwnerDispatch(stableEnv, stableApi, stableEvent, workflowBytes);
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
    GITHUB_WORKFLOW_REF: stableEnv.GITHUB_WORKFLOW_REF.replace('main', 'development') }, stableApi, stableEvent, workflowBytes));
  await assert.rejects(verifyOwnerDispatch({ ...stableEnv, RELEASE_CHANNEL: 'insider' }, stableApi, stableEvent, workflowBytes));
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
  mkdirSync(join(source, 'scripts'));
  for (const file of ['printfarmer-host-update.sh', 'common-utils.sh', 'printfarmer-host-update.ps1']) {
    writeFileSync(join(source, 'scripts', file), file);
  }
  mkdirSync(join(source, '.release-assets'));
  writeFileSync(join(source, '.release-assets', 'preserved.txt'), 'preserve this source content');
  const builds = [];
  const smokes = [];
  const cliPublishes = [];
  const cliArchives = [];
  const syftScans = [];
  const enrichments = [];
  const run = (name, args) => {
    if (name === 'git') return sha;
    if (name === 'dotnet' && args[0] === 'publish') {
      cliPublishes.push(args);
      const output = args[args.indexOf('--output') + 1];
      const rid = args[args.indexOf('--runtime') + 1];
      mkdirSync(output, { recursive: true });
      writeFileSync(join(output, rid.startsWith('win-') ? 'Farm.HostUpdate.Cli.exe' : 'Farm.HostUpdate.Cli'), rid);
      return '';
    }
    if (name === 'tar' && args[0] === '--version') return 'tar (GNU tar) 1.35\n';
    if (name === 'tar' && args[0] === '-czf') {
      const stage = args[args.indexOf('-C') + 1];
      cliArchives.push({ args, manifest: JSON.parse(readFileSync(join(stage, 'host-update-cli-package.json'), 'utf8')),
        wrapper: existsSync(join(stage, 'printfarmer-host-update.sh')) });
      writeFileSync(args[1], `archive ${args[1]}`);
      return '';
    }
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
    else if (name === 'syft') {
      syftScans.push(args);
      writeFileSync(args[2].slice('spdx-json='.length), spdxFixture(args[0]));
    } else if (name === 'node' && args[0] === 'scripts/compliance/enrich-sbom.mjs') enrichments.push(args);
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
  assert.deepEqual(cliPublishes.map(args => args[args.indexOf('--runtime') + 1]), ['linux-x64', 'linux-arm64', 'win-x64']);
  for (const args of cliPublishes) {
    assert.equal(args[1], 'src/tools/Farm.HostUpdate.Cli/Farm.HostUpdate.Cli.csproj');
    assert.ok(args.includes('--self-contained') && args[args.indexOf('--self-contained') + 1] === 'true');
    assert.ok(args.includes(`-p:Version=${release.version}`) && args.includes(`-p:SourceRevisionId=${sha}`));
  }
  assert.equal(cliArchives.length, 3);
  for (const { args, manifest, wrapper } of cliArchives) {
    assert.ok(args.includes('--owner=0') && args.includes('--group=0'), 'archive members must be root-owned');
    assert.equal(manifest.rolloutAuthorization, false);
    assert.equal(manifest.selfContained, true);
    assert.equal(manifest.sourceCommit, sha);
    assert.ok(wrapper);
  }
  const sums = readFileSync(join(assets, `printfarmer-host-update-cli-v${release.version}-SHA256SUMS`), 'utf8');
  assert.equal(sums.trim().split('\n').length, 6);
  assert.deepEqual(verifyHostUpdateCliSums(assets, release.version).size, 6);
  const cliScans = syftScans.filter(args => args[0].startsWith('dir:'));
  assert.deepEqual(cliScans.map(args => args[args.indexOf('--source-name') + 1]),
    hostUpdateCliRuntimes.map(rid => `printfarmer-host-update-cli-${rid}`));
  for (const rid of hostUpdateCliRuntimes) {
    const sbom = join(assets, hostUpdateCliSbomName(release.version, rid));
    assert.ok(cliScans.some(args => args[2] === `spdx-json=${sbom}`), `SBOM scanned for ${rid}`);
    assert.ok(!enrichments.some(args => args[args.indexOf('--sbom') + 1] === sbom),
      `CLI SBOM is a component inventory, not license-enriched, for ${rid}`);
  }
  assert.equal(syftScans.indexOf(cliScans[0]), 0, 'CLI SBOMs are produced before any image scan');
  const beforeCli = builds.length;
  assert.throws(() => buildImages(release, source, assets, (name, args) => {
    if (name === 'dotnet' && args[0] === 'publish' && args.includes('linux-arm64')) throw new Error('cli publish failed');
    return run(name, args);
  }, () => {}), /cli publish failed/);
  assert.equal(builds.length, beforeCli, 'a CLI package failure must stop before any image build');
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
  const cliSums = join(assets, hostUpdateCliSumsName(chosen.version));
  writeFileSync(cliSums, formatSums(hostUpdateCliRuntimes.flatMap(rid => {
    const name = hostUpdateCliArchiveName(chosen.version, rid);
    const sbom = hostUpdateCliSbomName(chosen.version, rid);
    writeFileSync(join(assets, name), `archive ${rid}`);
    writeFileSync(join(assets, sbom), spdxFixture(rid));
    return [{ name, sha256: createHash('sha256').update(`archive ${rid}`).digest('hex') },
      { name: sbom, sha256: createHash('sha256').update(spdxFixture(rid)).digest('hex') }];
  })));
  writeFileSync(join(assets, hostUpdateCliSumsBundleName(chosen.version)), JSON.stringify({
    sha256: createHash('sha256').update(readFileSync(cliSums)).digest('hex'),
    issuer: manifestIssuer,
    identity: manifestIdentityFor(chosen.channel),
  }));
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
  const cosignIndices = calls.flatMap((call, index) => call.command === 'cosign' ? [index] : []);
  const cosignIndex = cosignIndices.at(-1);
  const ghIndex = calls.findIndex(call => call.command === 'gh');
  assert.ok(cosignIndex !== -1 && ghIndex !== -1, 'verification and upload commands must run');
  assert.equal(ghIndex, cosignIndex + 1, 'cosign verify-blob must be immediately before gh release upload');
  const cosignArgs = calls[cosignIndex].args;
  assert.equal(cosignArgs[0], 'verify-blob');
  assert.equal(cosignArgs[1], '--bundle');
  assert.match(cosignArgs[2], /update-manifest\.sigstore\.json$/);
  assert.equal(cosignArgs[3], '--certificate-oidc-issuer');
  assert.equal(cosignArgs[4], manifestIssuer);
  assert.equal(cosignArgs[5], '--certificate-identity');
  assert.equal(cosignArgs[6], manifestIdentity);
  assert.match(cosignArgs[7], /update-manifest\.json$/);
  assert.deepEqual(readFileSync(cosignArgs[7]), signedManifestBytes);
  assert.ok(cosignIndices[0] < calls.findIndex(call => call.endpoint === 'git/refs'),
    'signature must be verified before permanent Git tag creation');
});

test('the host-update CLI checksum list is verified with the channel identity before tagging and before upload', async t => {
  for (const channel of ['stable', 'insider']) {
    const { chosen, assets, api, deps, calls, files } = publishFixture(t, channel);
    for (const name of hostUpdateCliAssets(chosen.version)) assert.ok(files.includes(name), `${name} must be uploaded`);
    await publishRelease(chosen, assets, api, deps);
    const cliChecks = calls.flatMap((call, index) =>
      call.command === 'cosign' && call.args[7].endsWith(hostUpdateCliSumsName(chosen.version)) ? [index] : []);
    assert.equal(cliChecks.length, 2);
    for (const index of cliChecks) {
      assert.match(calls[index].args[2], /SHA256SUMS\.sigstore\.json$/);
      assert.equal(calls[index].args[4], manifestIssuer);
      assert.equal(calls[index].args[6], manifestIdentityFor(channel));
    }
    assert.ok(cliChecks[0] < calls.findIndex(call => call.endpoint === 'git/refs'));
    assert.ok(cliChecks[1] > calls.findIndex(call => call.endpoint === 'releases'));
    assert.ok(cliChecks[1] < calls.findIndex(call => call.command === 'gh'));
  }
});

test('host-update CLI checksum list is canonical and names exactly the supported archives and SBOMs', t => {
  assert.deepEqual([...hostUpdateCliRuntimes], ['linux-x64', 'linux-arm64', 'win-x64']);
  assert.throws(() => hostUpdateCliArchiveName('1.2.3', 'osx-arm64'), /Unsupported host-update CLI runtime/);
  assert.deepEqual(hostUpdateCliAssets('1.2.3'), [
    'printfarmer-host-update-cli-v1.2.3-linux-x64.tar.gz',
    'printfarmer-host-update-cli-v1.2.3-linux-arm64.tar.gz',
    'printfarmer-host-update-cli-v1.2.3-win-x64.tar.gz',
    'printfarmer-host-update-cli-v1.2.3-linux-x64.spdx.json',
    'printfarmer-host-update-cli-v1.2.3-linux-arm64.spdx.json',
    'printfarmer-host-update-cli-v1.2.3-win-x64.spdx.json',
    'printfarmer-host-update-cli-v1.2.3-SHA256SUMS',
    'printfarmer-host-update-cli-v1.2.3-SHA256SUMS.sigstore.json',
  ]);
  const hash = 'a'.repeat(64);
  assert.equal(formatSums([{ name: 'b.tar.gz', sha256: hash }, { name: 'a.tar.gz', sha256: hash }]),
    `${hash}  a.tar.gz\n${hash}  b.tar.gz\n`);
  assert.throws(() => formatSums([{ name: 'a b', sha256: hash }]), /Invalid checksum entry name/);
  assert.throws(() => formatSums([{ name: 'a', sha256: 'A'.repeat(64) }]), /Invalid SHA-256/);
  for (const bad of ['', `${hash}  a`, `${hash} a\n`, `${hash}  ../a\n`, `${hash}  a\n${hash}  a\n`, `${hash}  a\r\n`]) {
    assert.throws(() => parseSums(bad), /malformed|Duplicate/, JSON.stringify(bad));
  }
  const assets = workspace(t);
  const version = '1.2.3';
  const write = entries => writeFileSync(join(assets, hostUpdateCliSumsName(version)), formatSums(entries));
  const entries = hostUpdateCliRuntimes.flatMap(rid => {
    const name = hostUpdateCliArchiveName(version, rid);
    const sbom = hostUpdateCliSbomName(version, rid);
    writeFileSync(join(assets, name), rid);
    writeFileSync(join(assets, sbom), spdxFixture(rid));
    return [{ name, sha256: createHash('sha256').update(rid).digest('hex') },
      { name: sbom, sha256: createHash('sha256').update(spdxFixture(rid)).digest('hex') }];
  });
  write(entries);
  assert.equal(verifyHostUpdateCliSums(assets, version).size, 6);
  write(entries.filter(entry => !entry.name.endsWith('.spdx.json')));
  assert.throws(() => verifyHostUpdateCliSums(assets, version), /exactly the supported archives and SBOMs/);
  write(entries.slice(1));
  assert.throws(() => verifyHostUpdateCliSums(assets, version), /exactly the supported archives/);
  write([...entries, { name: 'extra.tar.gz', sha256: hash }]);
  assert.throws(() => verifyHostUpdateCliSums(assets, version), /exactly the supported archives/);
  write(entries.map((entry, index) => index === 2 ? { ...entry, sha256: hash } : entry));
  assert.throws(() => verifyHostUpdateCliSums(assets, version), /hash mismatch/);
  const sbom = hostUpdateCliSbomName(version, 'win-x64');
  writeFileSync(join(assets, sbom), '{}');
  write(entries.map(entry => entry.name === sbom
    ? { ...entry, sha256: createHash('sha256').update('{}').digest('hex') } : entry));
  assert.throws(() => verifyHostUpdateCliSums(assets, version), /not an SPDX 2\.x document/);
  for (const bad of ['not json', '[]', '{"spdxVersion":"SPDX-2.3","SPDXID":"SPDXRef-DOCUMENT","packages":[]}',
    '{"spdxVersion":"CycloneDX","SPDXID":"SPDXRef-DOCUMENT","packages":[{}]}']) {
    assert.throws(() => validateHostUpdateCliSbom(bad, sbom), /SBOM/, bad);
  }
});

test('host-update CLI packaging refuses a missing launcher or source commit and cleans its stage', t => {
  const root = workspace(t);
  const scratch = join(root, 'scratch');
  mkdirSync(scratch);
  const run = name => (name === 'tar' ? 'bsdtar 3.7.2' : '');
  assert.throws(() => packageHostUpdateCli({ ...release, sourceCommit: undefined }, root, join(root, 'out'), { run, scratch }),
    /source commit/);
  assert.throws(() => packageHostUpdateCli(release, root, join(root, 'out'), { run, scratch, runtimes: ['linux-x64'] }),
    /launcher missing for linux-x64/);
  assert.deepEqual(readdirSync(scratch), []);
  assert.ok(!existsSync(join(root, 'out', hostUpdateCliSumsName(release.version))));
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
    ['missing CLI checksum bundle', (assets) => rmSync(join(assets, hostUpdateCliSumsBundleName(release.version)))],
    ['empty CLI checksum bundle', (assets) => writeFileSync(join(assets, hostUpdateCliSumsBundleName(release.version)), '')],
    ['tampered CLI archive', (assets) => writeFileSync(join(assets, hostUpdateCliArchiveName(release.version, 'linux-x64')), 'evil')],
    ['missing CLI SBOM', (assets) => rmSync(join(assets, hostUpdateCliSbomName(release.version, 'linux-arm64')))],
    ['tampered CLI SBOM', (assets) => writeFileSync(join(assets, hostUpdateCliSbomName(release.version, 'linux-x64')),
      spdxFixture('tampered'))],
    ['re-hashed CLI checksum list', (assets) => {
      const archive = hostUpdateCliArchiveName(release.version, 'win-x64');
      writeFileSync(join(assets, archive), 'evil');
      const path = join(assets, hostUpdateCliSumsName(release.version));
      const entries = parseSums(readFileSync(path, 'utf8'));
      entries.set(archive, createHash('sha256').update('evil').digest('hex'));
      writeFileSync(path, formatSums([...entries].map(([name, sha256]) => ({ name, sha256 }))));
    }],
    ['CLI checksum signed by another identity', (assets) => {
      const path = join(assets, hostUpdateCliSumsBundleName(release.version));
      writeFileSync(path, JSON.stringify({ ...JSON.parse(readFileSync(path, 'utf8')),
        identity: manifestIdentity.replace('@refs/heads/development', '@refs/heads/feature') }));
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
  assert.deepEqual(workflow.jobs.build.needs, ['select', 'checks']);
  assert.deepEqual(workflow.jobs.sign.needs, ['select', 'checks', 'build']);
  assert.deepEqual(workflow.jobs.publish.needs, ['select', 'checks', 'build', 'sign']);
  assert.equal(workflow.jobs.build.environment, '${{ needs.select.outputs.environment }}');
  assert.equal(workflow.jobs.sign.environment, '${{ needs.select.outputs.environment }}');
  assert.equal(workflow.jobs.publish.environment, '${{ needs.select.outputs.environment }}');
  assert.equal(workflow.jobs.build.permissions['id-token'], undefined);
  assert.equal(workflow.jobs.sign.permissions['id-token'], 'write');
  assert.equal(workflow.jobs.sign['timeout-minutes'], 15);
  assert.equal(workflow.jobs.publish.permissions['id-token'], undefined);
  assert.equal(workflow.jobs.select.permissions['id-token'], undefined);
  assert.equal(workflow.jobs.build.env.RELEASE_SELECTED_SOURCE, '${{ needs.select.outputs.source_sha }}');
  const buildSteps = workflow.jobs.build.steps;
  const signSteps = workflow.jobs.sign.steps;
  const publishSteps = workflow.jobs.publish.steps;
  const cosignInstaller = 'sigstore/cosign-installer@6f9f17788090df1f26f669e9d70d6ae9567deba6';
  for (const [jobName, steps] of [['sign', signSteps], ['publish', publishSteps]]) {
    const installer = steps.find(step => step.uses?.startsWith('sigstore/cosign-installer@'));
    assert.equal(installer?.uses, cosignInstaller,
      `${jobName} must use the supported immutable installer`);
    assert.equal(installer?.with?.['cosign-release'], 'v3.0.6',
      `${jobName} must retain the verified cosign release`);
  }
  assert.equal(buildSteps.find(step => step.name === 'Checkout pinned application source').with.ref,
    '${{ needs.select.outputs.source_sha }}');
  assert.equal(signSteps.find(step => step.name === 'Verify owner dispatch and environment policy').run,
    'node scripts/ci/publish-release.mjs verify');
  assert.equal(signSteps.find(step => step.uses?.startsWith('actions/setup-node@')).with['node-version'], 24);
  assert.equal(signSteps.some(step => step.name === 'Checkout pinned application source'), false);
  assert.equal(signSteps.some(step => step.run === 'node scripts/ci/publish-release.mjs build'), false);
  assert.match(signSteps.find(step => step.name === 'Sign exact immutable manifest').run,
    /cosign sign-blob --yes --bundle "\$BUNDLE" "\$MANIFEST"/);
  assert.match(signSteps.find(step => step.name === 'Sign exact immutable manifest').run,
    /--certificate-oidc-issuer https:\/\/token\.actions\.githubusercontent\.com/);
  assert.equal(signSteps.find(step => step.name === 'Sign exact immutable manifest').env.EXPECTED_IDENTITY,
    manifestIdentityTemplate);
  assert.equal(publishSteps.find(step => step.id === 'publisher').with['permission-workflows'], 'write');
  assert.equal(publishSteps.find(step => step.id === 'publisher').with.repositories, 'PrintFarmer');
  assert.equal(publishSteps.find(step => step.run === 'node scripts/ci/publish-release.mjs publish').env.GH_TOKEN,
    '${{ steps.publisher.outputs.token }}');
  assert.match(publishSteps.find(step => step.name === 'Bind signature to exact manifest bytes').run,
    /sha256sum --check signed-release\/update-manifest\.sha256/);
  const cliSign = signSteps.find(step => step.name === 'Sign exact host-update CLI checksum list');
  assert.ok(signSteps.indexOf(cliSign) > signSteps.findIndex(step => step.name === 'Verify owner dispatch and environment policy'));
  assert.equal(cliSign.env.SUMS, 'release-assets/printfarmer-host-update-cli-v${{ inputs.version }}-SHA256SUMS');
  assert.equal(cliSign.env.EXPECTED_IDENTITY, manifestIdentityTemplate);
  assert.match(cliSign.run, /sha256sum "\$SUMS" > signed-release\/host-update-cli-sums\.sha256/);
  assert.match(cliSign.run, /cosign sign-blob --yes --bundle "\$BUNDLE" "\$SUMS"/);
  assert.match(cliSign.run, /cosign verify-blob --bundle "\$BUNDLE" \\\n\s+--certificate-oidc-issuer https:\/\/token\.actions\.githubusercontent\.com \\\n\s+--certificate-identity "\$EXPECTED_IDENTITY" "\$SUMS"/);
  const bind = publishSteps.find(step => step.name === 'Bind signature to exact manifest bytes');
  assert.match(bind.run, /sha256sum --check signed-release\/host-update-cli-sums\.sha256\n\s*cp "signed-release\/\$CLI_SUMS_BUNDLE" "release-assets\/\$CLI_SUMS_BUNDLE"/);
  assert.equal(bind.env.CLI_SUMS_BUNDLE, 'printfarmer-host-update-cli-v${{ inputs.version }}-SHA256SUMS.sigstore.json');
  assert.equal(publishSteps.find(step => step.name === 'Verify owner dispatch immediately before credentials').run,
    'node scripts/ci/publish-release.mjs verify');
  assert.ok(publishSteps.some(step => step.uses?.startsWith('docker/setup-buildx-action@')));
  assert.ok(publishSteps.some(step => step.uses?.startsWith('docker/login-action@')));
  assert.equal(publishSteps.some(step => step.name === 'Checkout pinned application source'), false);
  assert.equal(publishSteps.some(step => step.uses?.includes('sigstore/cosign-installer')), true);
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
  for (const [jobName, job] of Object.entries(workflow.jobs)) {
    for (const step of job.steps ?? []) {
      if (step.uses) assert.match(step.uses, /@[a-f0-9]{40}(?:\s+#.*)?$/,
        `${jobName} uses an unpinned action: ${step.uses}`);
    }
  }
});
