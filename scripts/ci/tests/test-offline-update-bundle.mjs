import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { closeSync, existsSync, fstatSync, mkdirSync, mkdtempSync, openSync, readFileSync, readSync, readdirSync, rmSync,
  writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { isAbsolute, join, relative } from 'node:path';
import test from 'node:test';
import { components } from '../release-policy.mjs';
import { buildManifest, deriveSequence } from '../release-manifest.mjs';
import { canonicalImageIndex, imageArchiveLimits, imageTarHeader, infrastructureImagesDocument, infrastructureImagesName,
  infrastructureImagesSignatureName, infrastructureLockKind, infrastructureLockPath, mediaTypes, platformMatches,
  validateInfrastructureLock, verifyImageArchive, verifyInfrastructureLockAgainstRegistry,
  writeImageArchive } from '../offline-bundle-images.mjs';
import { formatSums, hostUpdateCliArchiveName, hostUpdateCliRuntimes, hostUpdateCliSbomName, hostUpdateCliSumsBundleName,
  hostUpdateCliSumsName } from '../host-update-cli-package.mjs';
import { admitOfflineReplay, assembleOfflineBundle, evaluateTrustPolicy, hostUpdateCliInvocation, hostUpdateCliRunnerName,
  importOfflineBundle, loadPriorImages, loadVerifiedImages, offlineBundleIndexName, offlineBundleLimits, offlineBundleName,
  offlineBundleVerificationName, offlineImportDecisionKind, offlineTrustPolicy, parseArguments, readOfflineBundleEntries,
  redactReason, releaseSigningIdentity, tarHeader, trustedRootApprovalKind, verifyOfflineBundle } from '../offline-update-bundle.mjs';
import { recoveryInstructionsDocument, recoveryInstructionsName, recoveryInstructionsSignatureName,
  validateRecoveryInstructions } from '../offline-recovery-instructions.mjs';
import { bindDeploymentSetToImages, deploymentSetDocument, deploymentSetName, deploymentSetSignatureName,
  deploymentTemplatePaths, deploymentTopologies, offlineToolsLockPath, readDeploymentTemplates, toolMember,
  validateDeploymentSet, validateOfflineToolsLock } from '../offline-deployment-set.mjs';

const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const imageDetails = Object.fromEntries(Object.entries(components).map(([name, component], index) => [name, {
  indexDigest: `sha256:${String(index).repeat(64)}`,
  platforms: component.platforms,
  platformDigests: Object.fromEntries(component.platforms.map(platform => [platform, `sha256:${'d'.repeat(64)}`])),
}]));
const releases = {
  stable: { version: '1.4.0', tag: 'v1.4.0', channel: 'stable', sourceBranch: 'main', sourceCommit: 'a'.repeat(40), buildId: '41' },
  insider: { version: '1.5.0-insider.3', tag: 'v1.5.0-insider.3', channel: 'insider', sourceBranch: 'development',
    sourceCommit: 'b'.repeat(40), buildId: '42' },
};

// Simulated keyless signature: the "bundle" binds the exact signed bytes to the workflow identity.
function sign(bytes, channel) {
  return `${JSON.stringify({ sha256: sha256(bytes), identity: releaseSigningIdentity(channel) })}\n`;
}

// Fake cosign that authenticates like the real one would: exact bytes + certificate identity.
function cosign({ requireOffline }) {
  const calls = [];
  const run = (name, args) => {
    calls.push([name, ...args]);
    assert.equal(name, 'cosign');
    assert.equal(args[0], 'verify-blob');
    if (requireOffline) {
      assert.ok(args.includes('--trusted-root') && args[args.indexOf('--trusted-root') + 1],
        'network-denied verification must pass the operator trusted root');
      assert.ok(!args.some(arg => /^--(offline|insecure|rekor-url|private-infrastructure)/.test(arg)),
        'no unsupported or weakening cosign flag');
    }
    const bundle = JSON.parse(readFileSync(args[args.indexOf('--bundle') + 1], 'utf8'));
    const file = readFileSync(args.at(-1));
    if (bundle.sha256 !== sha256(file)) throw new Error('signature does not match the blob');
    if (bundle.identity !== args[args.indexOf('--certificate-identity') + 1]) throw new Error('certificate identity mismatch');
    return '';
  };
  return { run, calls };
}

function spdx(rid) {
  return `${JSON.stringify({ spdxVersion: 'SPDX-2.3', SPDXID: 'SPDXRef-DOCUMENT', name: `cli-${rid}`,
    packages: [{ SPDXID: 'SPDXRef-Package-cli', name: 'printfarmer-host-update-cli' }] })}\n`;
}

function fixture(channel = 'stable') {
  const root = mkdtempSync(join(tmpdir(), 'offline-bundle-'));
  const release = releases[channel];
  const assets = join(root, 'assets');
  mkdirSync(assets);
  const manifest = Buffer.from(buildManifest(release, imageDetails));
  writeFileSync(join(assets, 'update-manifest.json'), manifest);
  writeFileSync(join(assets, 'update-manifest.sigstore.json'), sign(manifest, channel));
  const archives = hostUpdateCliRuntimes.map(rid => {
    const name = hostUpdateCliArchiveName(release.version, rid);
    const bytes = Buffer.from(`archive ${rid} ${'x'.repeat(700)}`);
    writeFileSync(join(assets, name), bytes);
    const sbomName = hostUpdateCliSbomName(release.version, rid);
    const sbom = Buffer.from(spdx(rid));
    writeFileSync(join(assets, sbomName), sbom);
    return [{ name, sha256: sha256(bytes) }, { name: sbomName, sha256: sha256(sbom) }];
  }).flat();
  const sums = Buffer.from(formatSums(archives));
  writeFileSync(join(assets, hostUpdateCliSumsName(release.version)), sums);
  writeFileSync(join(assets, hostUpdateCliSumsBundleName(release.version)), sign(sums, channel));
  const trustedRoot = join(root, 'trusted_root.json');
  writeFileSync(trustedRoot, `${JSON.stringify({ mediaType: 'application/vnd.dev.sigstore.trustedroot+json;version=0.1',
    certificateAuthorities: [{ uri: 'https://fulcio.sigstore.dev', validFor: { start: '2026-01-01T00:00:00Z' } }],
    tlogs: [{ baseUrl: 'https://rekor.sigstore.dev', publicKey: { validFor: { start: '2026-01-01T00:00:00Z' } } }] })}\n`);
  return { root, assets, release, trustedRoot, bundle: join(root, offlineBundleName(release.version)),
    staging: join(root, 'staging'), cleanup: () => rmSync(root, { force: true, recursive: true }) };
}

function assemble(context, overrides = {}) {
  // Verification needs the operator's expected backup reference; remember the one used to assemble.
  context.expectedBackup = overrides.priorReleaseAssets ? overrides.protectedBackup : undefined;
  return assembleOfflineBundle({ releaseAssets: context.assets, channel: context.release.channel,
    output: context.bundle, run: cosign({ requireOffline: false }).run, ...overrides });
}

function verify(context, overrides = {}) {
  return verifyOfflineBundle({ bundle: context.bundle, channel: context.release.channel,
    trustedRoot: context.trustedRoot, staging: context.staging, run: cosign({ requireOffline: true }).run,
    protectedBackup: context.expectedBackup, now: () => new Date('2026-09-25T20:00:00Z'), ...overrides });
}

// Load re-authenticates the staged signed bytes offline, so the runner must answer cosign too.
function load(context, docker, overrides = {}) {
  const signatures = cosign({ requireOffline: true });
  const run = (name, args, options) => (name === 'cosign' ? signatures.run(name, args) : docker(name, args, options));
  return loadVerifiedImages({ staging: context.staging, channel: context.release.channel,
    trustedRoot: context.trustedRoot, run, ...overrides });
}

function rejectsWithoutStaging(context, pattern, overrides) {
  assert.throws(() => verify(context, overrides), pattern);
  assert.equal(existsSync(context.staging), false, 'a failed import must leave no staging directory');
}

// Writes an arbitrary archive from raw header/data parts so malicious shapes can be forged.
function writeTar(path, members, { trailer = Buffer.alloc(1024) } = {}) {
  const parts = [];
  for (const { header, data = Buffer.alloc(0) } of members) {
    parts.push(header, data, Buffer.alloc((512 - (data.length % 512)) % 512));
  }
  parts.push(trailer);
  writeFileSync(path, Buffer.concat(parts));
}

function member(name, data, options = {}) {
  const bytes = Buffer.from(data);
  return { header: tarHeader({ name, size: bytes.length, ...options }), data: bytes };
}

for (const channel of ['stable', 'insider']) {
  test(`${channel} bundle round-trips original signed bytes through network-denied verification`, () => {
    const context = fixture(channel);
    try {
      const { index } = assemble(context);
      assert.equal(index.installable, false);
      assert.equal(index.rolloutAuthorization, false);
      assert.deepEqual(index.contents.cliRuntimes, hostUpdateCliRuntimes);
      const { run, calls } = cosign({ requireOffline: true });
      const record = verify(context, { run, version: context.release.version });
      assert.equal(record.decision, 'verified-not-installable');
      assert.equal(record.release.channel, channel);
      assert.equal(record.release.sourceBranch, context.release.sourceBranch);
      assert.equal(record.signatureIdentity, releaseSigningIdentity(channel));
      assert.equal(record.bundleSha256, sha256(readFileSync(context.bundle)));
      assert.equal(calls.length, 2);
      for (const call of calls) assert.ok(call.includes(context.trustedRoot) || call.some(arg => arg.endsWith('trusted_root.json')));
      const extracted = readdirSync(context.staging).sort();
      assert.deepEqual(extracted, [...readdirSync(context.assets), offlineBundleVerificationName].sort());
      for (const name of readdirSync(context.assets)) {
        assert.deepEqual(readFileSync(join(context.staging, name)), readFileSync(join(context.assets, name)),
          `imported ${name} must equal the original signed bytes`);
      }
    } finally {
      context.cleanup();
    }
  });
}

test('members stay quarantined until authentication succeeds and the record is written last', () => {
  const context = fixture();
  try {
    assemble(context);
    const { run: authenticate } = cosign({ requireOffline: true });
    const observed = [];
    const run = (name, args) => {
      observed.push(readdirSync(context.staging).sort());
      return authenticate(name, args);
    };
    verify(context, { run });
    assert.ok(observed.length > 0);
    for (const listing of observed) assert.deepEqual(listing, ['.unverified'], 'unauthenticated members must not be published');
    assert.equal(existsSync(join(context.staging, '.unverified')), false);
    assert.ok(existsSync(join(context.staging, offlineBundleVerificationName)));
  } finally {
    context.cleanup();
  }
});

test('a runtime subset carries only the selected archive but the full signed checksum list', () => {
  const context = fixture('insider');
  try {
    const { index } = assemble(context, { runtimes: ['linux-arm64'] });
    assert.deepEqual(index.contents.cliRuntimes, ['linux-arm64']);
    const record = verify(context);
    assert.deepEqual(record.cliRuntimes, ['linux-arm64']);
    assert.ok(existsSync(join(context.staging, hostUpdateCliSumsName(context.release.version))));
    assert.ok(existsSync(join(context.staging, hostUpdateCliSbomName(context.release.version, 'linux-arm64'))));
    assert.equal(existsSync(join(context.staging, hostUpdateCliSbomName(context.release.version, 'win-x64'))), false);
    assert.equal(existsSync(join(context.staging, hostUpdateCliArchiveName(context.release.version, 'win-x64'))), false);
  } finally {
    context.cleanup();
  }
});

test('the expected channel is mandatory and never defaults', () => {
  const context = fixture('stable');
  try {
    assert.throws(() => assemble(context, { channel: undefined }), /explicit expected channel/);
    assert.throws(() => assemble(context, { channel: 'insider' }), /does not match the expected insider channel/);
    assemble(context);
    rejectsWithoutStaging(context, /explicit expected channel/, { channel: undefined });
    rejectsWithoutStaging(context, /explicit expected channel/, { channel: 'beta' });
    rejectsWithoutStaging(context, /does not match the expected insider channel/, { channel: 'insider' });
    rejectsWithoutStaging(context, /does not match the expected 1\.4\.1/, { version: '1.4.1' });
  } finally {
    context.cleanup();
  }
});

test('verification requires an operator trusted root and a fresh staging directory', () => {
  const context = fixture('stable');
  try {
    assemble(context);
    assert.throws(() => verify(context, { trustedRoot: undefined }), /operator-supplied Sigstore trusted root/);
    mkdirSync(context.staging);
    assert.throws(() => verify(context), /must not already exist/);
    assert.deepEqual(readdirSync(context.staging), []);
  } finally {
    context.cleanup();
  }
});

test('a modified member is rejected and leaves no success-shaped import', () => {
  const context = fixture('stable');
  try {
    assemble(context);
    const bytes = readFileSync(context.bundle);
    const at = bytes.indexOf(Buffer.from('"buildId":"41"'));
    assert.ok(at > 0);
    bytes[at + 12] = '9'.charCodeAt(0);
    writeFileSync(context.bundle, bytes);
    rejectsWithoutStaging(context, /member was modified: update-manifest\.json/);
  } finally {
    context.cleanup();
  }
});

test('a self-consistent forgery fails offline signature verification', () => {
  const context = fixture('stable');
  try {
    const forged = readFileSync(join(context.assets, 'update-manifest.json'), 'utf8').replace('"buildId":"41"', '"buildId":"99"');
    writeFileSync(join(context.assets, 'update-manifest.json'), forged);
    assert.throws(() => assemble(context), /signature verification failed for update-manifest\.json/);
    assert.equal(existsSync(context.bundle), false);
    assert.equal(existsSync(`${context.bundle}.partial`), false);
    // An assembler that skips authentication still cannot make the importer accept the forgery.
    assemble(context, { run: () => '' });
    rejectsWithoutStaging(context, /signature verification failed for update-manifest\.json/);
  } finally {
    context.cleanup();
  }
});

test('a wrong-channel signature identity is rejected even with matching bytes', () => {
  const context = fixture('insider');
  try {
    const manifest = readFileSync(join(context.assets, 'update-manifest.json'));
    writeFileSync(join(context.assets, 'update-manifest.sigstore.json'), sign(manifest, 'stable'));
    assert.throws(() => assemble(context), /certificate identity mismatch/);
  } finally {
    context.cleanup();
  }
});

test('an archive outside the signed checksum list is rejected during assembly', () => {
  const context = fixture('stable');
  try {
    writeFileSync(join(context.assets, hostUpdateCliArchiveName(context.release.version, 'linux-x64')), 'substituted');
    assert.throws(() => assemble(context), /does not match the signed checksum list/);
    assert.equal(existsSync(context.bundle), false);
  } finally {
    context.cleanup();
  }
});

// Re-signs a CLI checksum list so tests can prove checks beyond the signature itself.
function resignSums(context, entries) {
  const sums = Buffer.from(formatSums(entries));
  writeFileSync(join(context.assets, hostUpdateCliSumsName(context.release.version)), sums);
  writeFileSync(join(context.assets, hostUpdateCliSumsBundleName(context.release.version)), sign(sums, context.release.channel));
}

function assetEntries(context, names) {
  return names.map(name => ({ name, sha256: sha256(readFileSync(join(context.assets, name))) }));
}

// Builds a bundle straight from the assets with a caller-chosen member list, bypassing assembly checks.
function forgeBundle(context, { drop = () => false, prepare = () => {} } = {}) {
  const { index } = assemble(context, { output: `${context.bundle}.template` });
  rmSync(`${context.bundle}.template`, { force: true });
  prepare();
  index.files = index.files.filter(file => !drop(file.name)).map(file => {
    const bytes = readFileSync(join(context.assets, file.name));
    return { ...file, size: bytes.length, sha256: sha256(bytes) };
  });
  const indexBytes = Buffer.from(`${JSON.stringify(index, undefined, 2)}\n`);
  writeTar(context.bundle, [
    { header: tarHeader({ name: offlineBundleIndexName, size: indexBytes.length }), data: indexBytes },
    ...index.files.map(file => member(file.name, readFileSync(join(context.assets, file.name)))),
  ]);
}

test('every CLI SBOM must be listed in the signed checksum list', () => {
  const context = fixture('stable');
  try {
    const { version } = context.release;
    resignSums(context, assetEntries(context, hostUpdateCliRuntimes.map(rid => hostUpdateCliArchiveName(version, rid))));
    assert.throws(() => assemble(context), /does not name exactly the supported archives and SBOMs/);
    assert.equal(existsSync(context.bundle), false);
  } finally {
    context.cleanup();
  }
});

test('a signed but structurally invalid CLI SBOM is rejected on both sides', () => {
  const context = fixture('stable');
  try {
    const { version } = context.release;
    const corrupt = () => {
      writeFileSync(join(context.assets, hostUpdateCliSbomName(version, 'linux-x64')), '{"spdxVersion":"SPDX-2.3","packages":[]}\n');
      resignSums(context, assetEntries(context, hostUpdateCliRuntimes.flatMap(rid =>
        [hostUpdateCliArchiveName(version, rid), hostUpdateCliSbomName(version, rid)])));
    };
    forgeBundle(context, { prepare: corrupt });
    rejectsWithoutStaging(context, /not an SPDX 2\.x document with packages/);
    rmSync(context.bundle);
    assert.throws(() => assemble(context), /not an SPDX 2\.x document with packages/);
    assert.equal(existsSync(context.bundle), false);
  } finally {
    context.cleanup();
  }
});

test('a carried CLI archive without its SBOM is rejected before import', () => {
  const context = fixture('stable');
  try {
    const sbom = hostUpdateCliSbomName(context.release.version, 'linux-x64');
    forgeBundle(context, { drop: name => name === sbom });
    rejectsWithoutStaging(context, /archive and SBOM together: linux-x64/);
  } finally {
    context.cleanup();
  }
});

// --- Prior recovery set and protected-backup reference (#3062) ----------------------------------
const priorReleases = {
  stable: { version: '1.3.2', tag: 'v1.3.2', channel: 'stable', sourceBranch: 'main', sourceCommit: 'c'.repeat(40), buildId: '37' },
  insider: { version: '1.5.0-insider.2', tag: 'v1.5.0-insider.2', channel: 'insider', sourceBranch: 'development',
    sourceCommit: 'e'.repeat(40), buildId: '40' },
};

// Writes a release's signed metadata (manifest + CLI checksum list and their bundles) into `dir`.
function writePriorRelease(dir, release, { signAs = release.channel, sumsNames } = {}) {
  mkdirSync(dir, { recursive: true });
  const manifest = Buffer.from(buildManifest(release, imageDetails));
  writeFileSync(join(dir, 'update-manifest.json'), manifest);
  writeFileSync(join(dir, 'update-manifest.sigstore.json'), sign(manifest, signAs));
  const names = sumsNames ?? hostUpdateCliRuntimes.flatMap(rid =>
    [hostUpdateCliArchiveName(release.version, rid), hostUpdateCliSbomName(release.version, rid)]);
  const sums = Buffer.from(formatSums(names.map(name => ({ name, sha256: sha256(Buffer.from(name)) }))));
  writeFileSync(join(dir, hostUpdateCliSumsName(release.version)), sums);
  writeFileSync(join(dir, hostUpdateCliSumsBundleName(release.version)), sign(sums, signAs));
  return dir;
}

function backupFor(release, overrides = {}) {
  return { id: 'pf-backup-2026-09-25T2100Z', sha256: 'f'.repeat(64), locationClass: 'host-local',
    releaseVersion: release.version, ...overrides };
}

function priorFixture(channel = 'stable', prior = priorReleases[channel], options) {
  const context = fixture(channel);
  context.prior = prior;
  context.priorAssets = writePriorRelease(join(context.root, 'prior-assets'), prior, options);
  context.withPrior = (overrides = {}) => ({ priorReleaseAssets: context.priorAssets,
    protectedBackup: backupFor(prior), ...overrides });
  return context;
}

const priorNames = version => ['update-manifest.json', 'update-manifest.sigstore.json', hostUpdateCliSumsName(version),
  hostUpdateCliSumsBundleName(version)];

for (const channel of ['stable', 'insider']) {
  test(`${channel} bundle packages an authenticated prior recovery set and backup reference`, () => {
    const context = priorFixture(channel);
    try {
      const { index } = assemble(context, context.withPrior());
      assert.equal(index.contents.priorRecoverySet, true);
      assert.equal(index.installable, false);
      assert.equal(index.priorRecoverySet.mode, 'packaged');
      assert.equal(index.priorRecoverySet.release.version, context.prior.version);
      const { run, calls } = cosign({ requireOffline: true });
      const record = verify(context, { run });
      assert.equal(calls.length, 4, 'target and prior signatures are both authenticated offline');
      assert.deepEqual(record.priorRecoverySet.release, index.priorRecoverySet.release);
      assert.deepEqual(record.priorRecoverySet.protectedBackup, backupFor(context.prior));
      assert.equal(record.priorRecoverySet.mode, 'packaged');
      for (const name of priorNames(context.prior.version)) {
        assert.deepEqual(readFileSync(join(context.staging, `prior-${name}`)), readFileSync(join(context.priorAssets, name)),
          `imported prior ${name} must equal the original signed bytes`);
      }
      assert.equal(existsSync(join(context.staging, '.unverified')), false);
    } finally {
      context.cleanup();
    }
  });
}

test('a bundle without a prior set records priorRecoverySet false and rejects a supplied local set', () => {
  const context = priorFixture('stable');
  try {
    const { index } = assemble(context);
    assert.equal(index.contents.priorRecoverySet, false);
    assert.equal(Object.hasOwn(index, 'priorRecoverySet'), false);
    rejectsWithoutStaging(context, /carries no prior recovery set/, { priorRecoverySet: context.priorAssets });
    assert.equal(verify(context).priorRecoverySet, false);
  } finally {
    context.cleanup();
  }
});

test('a local-reference prior set is bound by digest and authenticated from the operator copy', () => {
  const context = priorFixture('insider');
  try {
    const { index } = assemble(context, context.withPrior({ priorMode: 'local-reference' }));
    assert.equal(index.contents.priorRecoverySet, true);
    assert.equal(index.files.some(file => file.name.startsWith('prior-')), false, 'local reference carries no prior bytes');
    rejectsWithoutStaging(context, /supply it with --prior-recovery-set/);
    const local = join(context.root, 'local-prior');
    mkdirSync(local);
    for (const name of priorNames(context.prior.version)) writeFileSync(join(local, name), readFileSync(join(context.priorAssets, name)));
    rejectsWithoutStaging(context, /must be a regular file/, { priorRecoverySet: join(context.root, 'missing') });
    const manifestPath = join(local, 'update-manifest.json');
    const original = readFileSync(manifestPath);
    writeFileSync(manifestPath, Buffer.from(original.toString('utf8').replace('"buildId":"40"', '"buildId":"49"')));
    rejectsWithoutStaging(context, /does not match the bundle's bound digest: update-manifest\.json/, { priorRecoverySet: local });
    writeFileSync(manifestPath, original);
    const record = verify(context, { priorRecoverySet: local });
    assert.equal(record.priorRecoverySet.mode, 'local-reference');
    assert.equal(record.priorRecoverySet.release.version, context.prior.version);
    assert.deepEqual(readFileSync(join(context.staging, 'prior-update-manifest.json')), original);
  } finally {
    context.cleanup();
  }
});

test('a packaged prior set refuses a separately supplied local copy', () => {
  const context = priorFixture('stable');
  try {
    assemble(context, context.withPrior());
    rejectsWithoutStaging(context, /must not be supplied/, { priorRecoverySet: context.priorAssets });
  } finally {
    context.cleanup();
  }
});

test('verification binds the protected backup reference to the operator expectation, not the unsigned index', () => {
  const context = priorFixture('stable');
  try {
    assemble(context, context.withPrior());
    rejectsWithoutStaging(context, /supply the expected reference with --protected-backup/, { protectedBackup: undefined });
    for (const overrides of [{ id: 'pf-backup-attacker' }, { sha256: 'a'.repeat(64) }, { locationClass: 'external-storage' }]) {
      rejectsWithoutStaging(context, /does not match the expected reference/,
        { protectedBackup: backupFor(context.prior, overrides) });
    }
    rejectsWithoutStaging(context, /exactly id, sha256/,
      { protectedBackup: { ...backupFor(context.prior), path: '/var/backups' } });
  } finally {
    context.cleanup();
  }
});

test('a protected backup reference is refused for a bundle without a prior set', () => {
  const context = priorFixture('stable');
  try {
    assemble(context);
    rejectsWithoutStaging(context, /bundle carries no prior recovery set/, { protectedBackup: backupFor(context.prior) });
  } finally {
    context.cleanup();
  }
});

test('the prior set is incomplete without a protected backup reference, and vice versa', () => {
  const context = priorFixture('stable');
  try {
    assert.throws(() => assemble(context, { priorReleaseAssets: context.priorAssets }), /must be supplied together/);
    assert.throws(() => assemble(context, { protectedBackup: backupFor(context.prior) }), /must be supplied together/);
    assert.throws(() => assemble(context, context.withPrior({ priorMode: 'remote' })), /packaged or local-reference/);
    assert.equal(existsSync(context.bundle), false);
  } finally {
    context.cleanup();
  }
});

test('protected backup references carry only identity, checksum and location class', () => {
  const context = priorFixture('stable');
  try {
    const cases = [
      [{ ...backupFor(context.prior), password: 'hunter2' }, /exactly id, sha256, locationClass and releaseVersion/],
      [{ ...backupFor(context.prior), contents: 'base64...' }, /exactly id, sha256/],
      [backupFor(context.prior, { id: 'postgres://user:pass@db/backup' }), /id is invalid/],
      [backupFor(context.prior, { id: '../etc/backup' }), /id is invalid/],
      [backupFor(context.prior, { sha256: 'F'.repeat(64) }), /lowercase SHA-256/],
      [backupFor(context.prior, { locationClass: 's3://bucket' }), /location class is not supported/],
      [backupFor(context.prior, { releaseVersion: '1.4.0' }), /not taken for the prior release 1\.3\.2/],
      [[], /exactly id, sha256/],
    ];
    for (const [protectedBackup, pattern] of cases) {
      assert.throws(() => assemble(context, context.withPrior({ protectedBackup })), pattern);
      assert.equal(existsSync(context.bundle), false);
    }
  } finally {
    context.cleanup();
  }
});

test('wrong-channel, newer, same-version and wrong-identity prior sets fail closed', () => {
  const insiderPrior = priorFixture('insider', priorReleases.stable);
  const newer = priorFixture('stable', { ...priorReleases.stable, version: '1.4.1', tag: 'v1.4.1' });
  const same = priorFixture('stable', { ...releases.stable, sourceCommit: 'f'.repeat(40), buildId: '43' });
  const wrongSigner = priorFixture('stable', priorReleases.stable, { signAs: 'insider' });
  try {
    assert.throws(() => assemble(insiderPrior, insiderPrior.withPrior()),
      /Prior recovery set: .*does not match the expected insider channel/);
    assert.throws(() => assemble(newer, newer.withPrior()), /1\.4\.1 is not strictly older than the target 1\.4\.0/);
    assert.throws(() => assemble(same, same.withPrior()), /1\.4\.0 is not strictly older than the target 1\.4\.0/);
    assert.throws(() => assemble(wrongSigner, wrongSigner.withPrior()),
      /Prior recovery set signature verification failed for update-manifest\.json: certificate identity mismatch/);
  } finally {
    for (const context of [insiderPrior, newer, same, wrongSigner]) context.cleanup();
  }
});

test('a signed prior checksum list must name exactly the supported assets', () => {
  const context = priorFixture('stable', priorReleases.stable,
    { sumsNames: [hostUpdateCliArchiveName(priorReleases.stable.version, 'linux-x64')] });
  try {
    assert.throws(() => assemble(context, context.withPrior()),
      /Prior recovery set CLI checksum list does not name exactly the supported archives and SBOMs/);
  } finally {
    context.cleanup();
  }
});

test('tampered or forged prior sets are rejected by the network-denied verifier', () => {
  const context = priorFixture('stable');
  try {
    assemble(context, context.withPrior());
    const bytes = readFileSync(context.bundle);
    const target = bytes.indexOf(Buffer.from('"buildId":"37"'));
    assert.ok(target > 0);
    bytes[target + 12] = '8'.charCodeAt(0);
    writeFileSync(context.bundle, bytes);
    rejectsWithoutStaging(context, /member was modified: prior-update-manifest\.json/);
    rmSync(context.bundle);
    // A self-consistent forgery from an assembler that skips authentication still fails offline.
    const manifestPath = join(context.priorAssets, 'update-manifest.json');
    writeFileSync(manifestPath, readFileSync(manifestPath, 'utf8').replace('"buildId":"37"', '"buildId":"38"'));
    assemble(context, context.withPrior({ run: () => '' }));
    rejectsWithoutStaging(context, /Prior recovery set signature verification failed for update-manifest\.json/);
  } finally {
    context.cleanup();
  }
});

test('index claims about the prior set cannot be forged or left incomplete', () => {
  const context = priorFixture('stable');
  try {
    assemble(context, context.withPrior());
    const original = readFileSync(context.bundle);
    const indexSize = Number.parseInt(original.subarray(124, 135).toString('latin1'), 8);
    const index = JSON.parse(original.subarray(512, 512 + indexSize).toString('utf8'));
    const rest = original.subarray(512 + Math.ceil(indexSize / 512) * 512);
    const rewrite = mutate => {
      const copy = structuredClone(index);
      mutate(copy);
      const data = Buffer.from(`${JSON.stringify(copy, undefined, 2)}\n`);
      writeFileSync(context.bundle, Buffer.concat([tarHeader({ name: offlineBundleIndexName, size: data.length }), data,
        Buffer.alloc((512 - (data.length % 512)) % 512), rest]));
    };
    rewrite(copy => { delete copy.priorRecoverySet; });
    rejectsWithoutStaging(context, /prior recovery claim does not match|not part of this release/);
    rewrite(copy => { copy.contents.priorRecoverySet = false; });
    rejectsWithoutStaging(context, /prior recovery claim does not match/);
    rewrite(copy => { delete copy.priorRecoverySet.protectedBackup; });
    rejectsWithoutStaging(context, /prior recovery set fields are invalid/);
    rewrite(copy => { copy.priorRecoverySet.protectedBackup.connectionString = 'Server=db;Password=x'; });
    rejectsWithoutStaging(context, /exactly id, sha256, locationClass and releaseVersion/);
    rewrite(copy => { copy.priorRecoverySet.protectedBackup.releaseVersion = '1.4.0'; });
    rejectsWithoutStaging(context, /not taken for the prior release/);
    // Every signed member is untouched; only the unsigned index names another well-formed backup.
    for (const change of [{ id: 'pf-backup-attacker' }, { sha256: 'b'.repeat(64) }, { locationClass: 'external-storage' }]) {
      rewrite(copy => { Object.assign(copy.priorRecoverySet.protectedBackup, change); });
      rejectsWithoutStaging(context, /does not match the expected reference/);
    }
    rewrite(copy => { copy.priorRecoverySet.release.buildId = '99'; });
    rejectsWithoutStaging(context, /prior recovery identity does not equal the signed prior manifest identity/);
    rewrite(copy => { copy.priorRecoverySet.manifestDigest = `sha256:${'0'.repeat(64)}`; });
    rejectsWithoutStaging(context, /prior recovery manifest digest mismatch/);
    rewrite(copy => { copy.priorRecoverySet.mode = 'remote'; });
    rejectsWithoutStaging(context, /prior recovery mode is not supported/);
    rewrite(copy => { copy.priorRecoverySet.files.pop(); });
    rejectsWithoutStaging(context, /does not list exactly the prior signed metadata/);
    rewrite(copy => { copy.priorRecoverySet.files[0].sha256 = '0'.repeat(64); });
    rejectsWithoutStaging(context, /missing or mismatches its packaged prior recovery member/);
    rewrite(copy => { copy.priorRecoverySet.mode = 'local-reference'; });
    rejectsWithoutStaging(context, /supply it with --prior-recovery-set/);
    rejectsWithoutStaging(context, /not part of this release: prior-/, { priorRecoverySet: context.priorAssets });
  } finally {
    context.cleanup();
  }
});

test('a missing packaged prior member is rejected before import', () => {
  const context = priorFixture('stable');
  try {
    const { index } = assemble(context, context.withPrior({ output: `${context.bundle}.template` }));
    rmSync(`${context.bundle}.template`);
    const dropped = 'prior-update-manifest.sigstore.json';
    index.files = index.files.filter(file => file.name !== dropped);
    const data = Buffer.from(`${JSON.stringify(index, undefined, 2)}\n`);
    const source = name => name.startsWith('prior-') ? join(context.priorAssets, name.slice(6)) : join(context.assets, name);
    writeTar(context.bundle, [{ header: tarHeader({ name: offlineBundleIndexName, size: data.length }), data },
      ...index.files.map(file => member(file.name, readFileSync(source(file.name))))]);
    rejectsWithoutStaging(context, /missing or mismatches its packaged prior recovery member: update-manifest\.sigstore\.json/);
  } finally {
    context.cleanup();
  }
});

test('the command line accepts prior recovery options without adding a bypass', () => {
  assert.deepEqual(parseArguments(['assemble', '--release-assets', 'a', '--channel', 'stable', '--output', 'o',
    '--prior-release-assets', 'p', '--protected-backup', 'b.json', '--prior-mode', 'local-reference']).options,
  { 'release-assets': 'a', channel: 'stable', output: 'o', 'prior-release-assets': 'p', 'protected-backup': 'b.json',
    'prior-mode': 'local-reference' });
  assert.equal(parseArguments(['verify', '--bundle', 'b', '--prior-recovery-set', 'p']).options['prior-recovery-set'], 'p');
  assert.equal(parseArguments(['verify', '--bundle', 'b', '--protected-backup', 'b.json']).options['protected-backup'],
    'b.json');
  for (const argv of [['assemble', '--prior-recovery-set', 'p'],
    ['verify', '--skip-prior-verification', 'true']]) {
    assert.throws(() => parseArguments(argv), /usage/, argv.join(' '));
  }
});

test('a prior set in a bundle stays within the member bound', () => {
  const context = priorFixture('stable');
  try {
    const { index } = assemble(context, context.withPrior());
    assert.ok(index.files.length + 1 <= offlineBundleLimits.maxMembers);
  } finally {
    context.cleanup();
  }
});

test('assembly refuses to overwrite an existing bundle', () => {
  const context = fixture('stable');
  try {
    writeFileSync(context.bundle, 'existing');
    assert.throws(() => assemble(context), /output already exists/);
    assert.equal(readFileSync(context.bundle, 'utf8'), 'existing');
    assert.equal(existsSync(`${context.bundle}.partial`), false);
  } finally {
    context.cleanup();
  }
});

test('index claims cannot add authority, material or members', () => {
  const context = fixture('stable');
  try {
    assemble(context);
    const original = readFileSync(context.bundle);
    const indexSize = Number.parseInt(original.subarray(124, 135).toString('latin1'), 8);
    const index = JSON.parse(original.subarray(512, 512 + indexSize).toString('utf8'));
    const rest = original.subarray(512 + Math.ceil(indexSize / 512) * 512);
    const rewrite = mutate => {
      const copy = structuredClone(index);
      mutate(copy);
      const bytes = Buffer.from(`${JSON.stringify(copy, undefined, 2)}\n`);
      writeFileSync(context.bundle, Buffer.concat([tarHeader({ name: offlineBundleIndexName, size: bytes.length }), bytes,
        Buffer.alloc((512 - (bytes.length % 512)) % 512), rest]));
    };
    rewrite(copy => { copy.installable = true; });
    rejectsWithoutStaging(context, /claims installation or rollout authority/);
    rewrite(copy => { copy.rolloutAuthorization = true; });
    rejectsWithoutStaging(context, /claims installation or rollout authority/);
    rewrite(copy => { copy.contents.images = true; });
    rejectsWithoutStaging(context, /contents claim material/);
    rewrite(copy => { copy.skipVerification = true; });
    rejectsWithoutStaging(context, /index fields are invalid/);
    rewrite(copy => { copy.files.pop(); });
    rejectsWithoutStaging(context, /does not list exactly the bundle members/);
    rewrite(copy => { copy.files[0].role = 'cli-archive'; });
    rejectsWithoutStaging(context, /not part of this release/);
    rewrite(copy => { copy.release.buildId = '99'; });
    rejectsWithoutStaging(context, /index identity does not equal the signed manifest identity/);
    rewrite(copy => { copy.contents.cliRuntimes = ['linux-x64']; });
    rejectsWithoutStaging(context, /runtime list does not match its archives/);
  } finally {
    context.cleanup();
  }
});

test('missing required members and unexpected members are rejected', () => {
  const context = fixture('stable');
  try {
    const manifest = readFileSync(join(context.assets, 'update-manifest.json'));
    const index = files => Buffer.from(JSON.stringify({ schema: 1, kind: 'printfarmer-offline-bundle',
      release: {}, manifestDigest: '', contents: { cliRuntimes: [], deploymentSet: false, images: false, infrastructure: false,
        priorRecoverySet: false, recoveryInstructions: false }, installable: false, rolloutAuthorization: false, files }));
    const file = (name, role, bytes) => ({ name, role, size: bytes.length, sha256: sha256(bytes) });
    const indexWith = (release, files) => {
      const bytes = JSON.parse(index(files).toString());
      bytes.release = release;
      return Buffer.from(JSON.stringify(bytes));
    };
    const release = { version: '1.4.0' };
    writeTar(context.bundle, [member(offlineBundleIndexName, indexWith(release, [file('update-manifest.json', 'manifest', manifest)])),
      member('update-manifest.json', manifest)]);
    rejectsWithoutStaging(context, /missing required member: update-manifest\.sigstore\.json/);
    writeTar(context.bundle, [member(offlineBundleIndexName, indexWith(release, [file('install.sh', 'cli-archive', Buffer.from('x'))])),
      member('install.sh', 'x')]);
    rejectsWithoutStaging(context, /not part of this release: install\.sh/);
    writeTar(context.bundle, [member('update-manifest.json', manifest), member(offlineBundleIndexName, index([]))]);
    rejectsWithoutStaging(context, /index must be the first member/);
  } finally {
    context.cleanup();
  }
});

test('malicious archive shapes are rejected before anything is extracted', () => {
  const context = fixture('stable');
  const cases = [
    [[member('../escape', 'x')], /not allowed|nested or absolute/],
    [[member('/etc/passwd', 'x')], /nested or absolute/],
    [[member('nested/file', 'x')], /nested or absolute/],
    [[member('C:evil', 'x')], /nested or absolute/],
    [[member('..\\evil', 'x')], /nested or absolute/],
    [[member('.hidden', 'x')], /not allowed/],
    [[member('NUL.txt', 'x')], /not allowed/],
    [[member('trailing.', 'x')], /not allowed/],
    [[member('link', '', { type: '2', linkname: '/etc/shadow' })], /symbolic link/],
    [[member('hard', '', { type: '1', linkname: 'update-manifest.json' })], /hard link/],
    [[member('dir', '', { type: '5' })], /directory/],
    [[member('pax', 'path=../../x\n', { type: 'x' })], /extended header/],
    [[member('longname', 'x', { type: 'L' })], /long name/],
    [[member('fifo', '', { type: '6' })], /FIFO/],
    [[member('file', 'x', { prefix: 'abs' })], /path prefix/],
    [[member('file', 'x', { magic: 'ustar ', version: ' \0' })], /not a POSIX ustar entry/],
    [[member('file', 'x', { mode: 0o4755 })], /mode is not 0644/],
    [[member('a', 'x'), member('a', 'y')], /duplicate or conflicting member: a/],
    [[member('Update-Manifest.json', 'x'), member('update-manifest.json', 'y')], /duplicate or conflicting/],
  ];
  try {
    for (const [members, pattern] of cases) {
      writeTar(context.bundle, members);
      rejectsWithoutStaging(context, pattern);
    }
    const corrupt = member('file', 'x');
    corrupt.header[0] ^= 1;
    writeTar(context.bundle, [corrupt]);
    rejectsWithoutStaging(context, /checksum mismatch/);
    const nonZeroPadding = member('file', 'x');
    writeFileSync(context.bundle, Buffer.concat([nonZeroPadding.header, Buffer.from('x'), Buffer.alloc(510), Buffer.from('!'),
      Buffer.alloc(1024)]));
    rejectsWithoutStaging(context, /padding is not zero/);
    writeTar(context.bundle, [member('file', 'x')], { trailer: Buffer.concat([Buffer.alloc(1024), Buffer.from('tail'), Buffer.alloc(508)]) });
    rejectsWithoutStaging(context, /data after its end marker/);
    writeTar(context.bundle, [member('file', 'x')], { trailer: Buffer.concat([Buffer.alloc(512), member('b', 'y').header]) });
    rejectsWithoutStaging(context, /end marker is incomplete/);
    const truncated = member('file', 'x'.repeat(4096));
    writeFileSync(context.bundle, Buffer.concat([truncated.header, Buffer.alloc(1024)]));
    rejectsWithoutStaging(context, /truncated/);
    writeFileSync(context.bundle, Buffer.alloc(100));
    rejectsWithoutStaging(context, /not a complete tar archive/);
  } finally {
    context.cleanup();
  }
});

test('member count, member size and bundle size are bounded before extraction', () => {
  const context = fixture('stable');
  try {
    writeTar(context.bundle, Array.from({ length: 5 }, (_, index) => member(`m${index}`, 'x')));
    rejectsWithoutStaging(context, /too many members/, { limits: { ...offlineBundleLimits, maxMembers: 4 } });
    rmSync(context.bundle);
    assemble(context);
    rejectsWithoutStaging(context, /exceeds its size limit/, { limits: { ...offlineBundleLimits, maxArchiveBytes: 16 } });
    rejectsWithoutStaging(context, /exceeds the maximum bundle size/, { limits: { ...offlineBundleLimits, maxBundleBytes: 2048 } });
    // A header may declare a size far beyond the file; it is rejected without allocating it.
    writeFileSync(context.bundle, Buffer.concat([tarHeader({ name: 'bomb', size: 8 * 1024 ** 3 - 1 }), Buffer.alloc(1024)]));
    rejectsWithoutStaging(context, /truncated/);
  } finally {
    context.cleanup();
  }
});

test('linked release assets are refused during assembly', { skip: process.platform === 'win32' }, async () => {
  const { symlinkSync, renameSync } = await import('node:fs');
  const context = fixture('stable');
  try {
    const name = hostUpdateCliArchiveName(context.release.version, 'linux-x64');
    renameSync(join(context.assets, name), join(context.root, name));
    symlinkSync(join(context.root, name), join(context.assets, name));
    assert.throws(() => assemble(context), /must be a regular file \(not a link\)/);
  } finally {
    context.cleanup();
  }
});

test('the command line offers no verification bypass', () => {
  assert.deepEqual(parseArguments(['verify', '--bundle', 'b.tar', '--channel', 'stable', '--trusted-root', 'r.json',
    '--staging', 's']).options, { bundle: 'b.tar', channel: 'stable', 'trusted-root': 'r.json', staging: 's' });
  assert.deepEqual(parseArguments(['assemble', '--release-assets', 'a', '--channel', 'insider', '--output', 'o',
    '--runtime', 'linux-x64', '--runtime', 'win-x64']).options.runtime, ['linux-x64', 'win-x64']);
  for (const argv of [
    ['verify', '--skip-verification', 'true'],
    ['verify', '--force', 'true'],
    ['verify', '--reset-replay', 'true'],
    ['assemble', '--staging', 'x'],
    ['import', '--bundle', 'b'],
    ['verify', '--channel', 'stable', '--channel', 'insider'],
    ['verify', '--bundle'],
  ]) {
    assert.throws(() => parseArguments(argv), /usage|Duplicate option/, argv.join(' '));
  }
});

test('signature identity matches the release workflow identity for each channel', () => {
  assert.equal(releaseSigningIdentity('stable'),
    'https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/main');
  assert.equal(releaseSigningIdentity('insider'),
    'https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/development');
  assert.throws(() => releaseSigningIdentity(undefined), /stable or insider/);
});

// ---------------------------------------------------------------------------------------------
// Issue #3061: every release-selected application and infrastructure image
// ---------------------------------------------------------------------------------------------
const ociConfigType = 'application/vnd.oci.image.config.v1+json';
const ociLayerType = 'application/vnd.oci.image.layer.v1.tar';
const ociLayoutBytes = Buffer.from('{"imageLayoutVersion":"1.0.0"}');
const allImageMembers = [...Object.keys(components).map(id => `image-${id}.oci.tar`),
  'infrastructure-mssql.oci.tar', 'infrastructure-nginx.oci.tar', 'infrastructure-postgres.oci.tar'].sort();
const byName = (a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
const blobName = digest => `blobs/sha256/${digest.slice('sha256:'.length)}`;

function layoutWriter(layout) {
  mkdirSync(join(layout, 'blobs', 'sha256'), { recursive: true });
  return bytes => {
    const data = Buffer.from(bytes);
    writeFileSync(join(layout, 'blobs', 'sha256', sha256(data)), data);
    return { digest: `sha256:${sha256(data)}`, size: data.length };
  };
}

// One platform image: config (naming its os/architecture/variant), one layer and the manifest.
function platformManifest(writeBlob, label, platform, mediaType = mediaTypes.ociManifest) {
  const [os, architecture, variant] = platform.split('/');
  const shape = { os, architecture, ...(variant ? { variant } : {}) };
  const config = writeBlob(JSON.stringify({ ...shape, rootfs: { type: 'layers', diff_ids: [] } }));
  const layer = writeBlob(`layer ${label} ${platform} ${'z'.repeat(600)}`);
  const manifest = writeBlob(JSON.stringify({ schemaVersion: 2, mediaType,
    config: { mediaType: ociConfigType, ...config }, layers: [{ mediaType: ociLayerType, ...layer }] }));
  return { mediaType, ...manifest, platform: shape, config, layer };
}

// A multi-platform index as registries publish it: arm64 carries the `v8` variant, and `extra`
// adds platforms or attestation manifests the release does not select.
function indexImage(writeBlob, label, platforms, extra = []) {
  const children = platforms.map(platform => platformManifest(writeBlob, label,
    platform === 'linux/arm64' ? 'linux/arm64/v8' : platform));
  const extras = extra.map(platform => platformManifest(writeBlob, label, platform));
  const root = writeBlob(JSON.stringify({ schemaVersion: 2, mediaType: mediaTypes.ociIndex,
    manifests: [...children, ...extras].map(({ mediaType, digest, size, platform }) => ({ mediaType, digest, size, platform })) }));
  return { mediaType: mediaTypes.ociIndex, ...root, children, extras,
    platforms: Object.fromEntries(platforms.map((platform, index) => [platform, children[index].digest])) };
}

function imageFixture(channel = 'stable') {
  const context = fixture(channel);
  const layout = join(context.root, 'layout');
  const writeBlob = layoutWriter(layout);
  const application = Object.fromEntries(Object.entries(components).map(([id, component]) =>
    [id, indexImage(writeBlob, id, component.platforms, id === 'api' ? ['unknown/unknown'] : [])]));
  const manifest = Buffer.from(buildManifest(context.release, Object.fromEntries(Object.entries(application).map(([id, image]) =>
    [id, { indexDigest: image.digest, platforms: components[id].platforms, platformDigests: image.platforms }]))));
  writeFileSync(join(context.assets, 'update-manifest.json'), manifest);
  writeFileSync(join(context.assets, 'update-manifest.sigstore.json'), sign(manifest, channel));
  const mssql = platformManifest(writeBlob, 'mssql', 'linux/amd64', mediaTypes.dockerManifest);
  const nginx = indexImage(writeBlob, 'nginx', ['linux/amd64', 'linux/arm64'], ['linux/arm/v7', 'unknown/unknown']);
  const postgres = indexImage(writeBlob, 'postgres', ['linux/amd64', 'linux/arm64']);
  const lock = { schema: 1, kind: infrastructureLockKind, images: [
    { id: 'mssql', reference: 'mcr.microsoft.com/mssql/server:2022-latest', mediaType: mediaTypes.dockerManifest,
      digest: mssql.digest, platforms: { 'linux/amd64': mssql.digest } },
    { id: 'nginx', reference: 'docker.io/library/nginx:alpine', mediaType: mediaTypes.ociIndex, digest: nginx.digest,
      platforms: nginx.platforms },
    { id: 'postgres', reference: 'docker.io/library/postgres:16-alpine', mediaType: mediaTypes.ociIndex,
      digest: postgres.digest, platforms: postgres.platforms },
  ] };
  const identity = { ...context.release, sequence: deriveSequence(context.release.version) };
  const writeInfrastructure = (value = lock, boundTo = identity, signedBy = channel) => {
    const bytes = infrastructureImagesDocument(boundTo, value);
    writeFileSync(join(context.assets, infrastructureImagesName), bytes);
    writeFileSync(join(context.assets, infrastructureImagesSignatureName), sign(bytes, signedBy));
    return bytes;
  };
  writeInfrastructure();
  const expected = id => {
    const image = lock.images.find(entry => entry.id === id);
    return { id, reference: image.reference, mediaTypes: [image.mediaType], digest: image.digest, platforms: image.platforms };
  };
  return { ...context, layout, writeBlob, application, infrastructure: { mssql, nginx, postgres }, lock, identity,
    writeInfrastructure, expected };
}

function withImages(body, channel = 'stable') {
  const context = imageFixture(channel);
  try {
    assemble(context, { images: context.layout });
    return body(context);
  } finally {
    context.cleanup();
  }
}

function nestedEntries(data) {
  const entries = [];
  for (let offset = 0; offset + 512 <= data.length;) {
    const header = data.subarray(offset, offset + 512);
    if (header.every(byte => byte === 0)) break;
    const name = header.subarray(0, 100).toString('latin1').replace(/\0[\s\S]*$/, '');
    const size = Number.parseInt(header.subarray(124, 135).toString('latin1'), 8);
    entries.push({ name, data: Buffer.from(data.subarray(offset + 512, offset + 512 + size)) });
    offset += 512 + Math.ceil(size / 512) * 512;
  }
  return entries;
}

function nestedArchive(entries, { trailer = Buffer.alloc(1024) } = {}) {
  const parts = [];
  for (const { name, data, type } of entries) {
    parts.push(imageTarHeader({ name, size: data.length, type }), data, Buffer.alloc((512 - (data.length % 512)) % 512));
  }
  parts.push(trailer);
  return Buffer.concat(parts);
}

function verifyNested(directory, buffer, expected, limits) {
  const path = join(directory, `nested-${sha256(buffer).slice(0, 16)}.tar`);
  writeFileSync(path, buffer);
  const fd = openSync(path, 'r');
  try {
    return verifyImageArchive(fd, 0, buffer.length, expected, limits);
  } finally {
    closeSync(fd);
  }
}

function readBundle(path) {
  const bytes = readFileSync(path);
  const fd = openSync(path, 'r');
  let entries;
  try {
    entries = readOfflineBundleEntries(fd, bytes.length);
  } finally {
    closeSync(fd);
  }
  const [head, ...rest] = entries.map(({ name, size, offset }) => ({ name, data: Buffer.from(bytes.subarray(offset, offset + size)) }));
  return { index: JSON.parse(head.data.toString('utf8')), members: new Map(rest.map(entry => [entry.name, entry.data])) };
}

// Forges a self-consistent outer bundle (index sizes and hashes recomputed) around edited members,
// so every rejection below comes from signature, identity or nested-image verification.
function rewriteBundle(context, edit) {
  const { index, members } = readBundle(context.bundle);
  const roles = new Map(index.files.map(file => [file.name, file.role]));
  edit({ index, members, roles });
  const names = [...members.keys()].sort();
  index.files = names.map(name => ({ name, role: roles.get(name), size: members.get(name).length,
    sha256: sha256(members.get(name)) }));
  rmSync(context.bundle);
  writeTar(context.bundle, [member(offlineBundleIndexName, `${JSON.stringify(index, undefined, 2)}\n`),
    ...names.map(name => member(name, members.get(name)))]);
}

function editNested(members, name, edit) {
  const entries = nestedEntries(members.get(name));
  members.set(name, nestedArchive(edit(entries) ?? entries));
}

function resignedInfrastructure(members, bytes, channel) {
  members.set(infrastructureImagesName, bytes);
  members.set(infrastructureImagesSignatureName, Buffer.from(sign(bytes, channel)));
}

test('every release-selected image round-trips through network-denied verification and a load-only import', () => {
  const context = imageFixture('insider');
  try {
    const { index } = assemble(context, { images: context.layout });
    assert.equal(index.contents.images, true);
    assert.equal(index.contents.infrastructure, true);
    assert.equal(index.installable, false);
    assert.equal(index.rolloutAuthorization, false);
    assert.deepEqual(index.files.filter(file => file.role.endsWith('-image')).map(file => file.name), allImageMembers);
    const { run, calls } = cosign({ requireOffline: true });
    const record = verify(context, { run });
    assert.equal(calls.length, 3, 'manifest, CLI checksums and infrastructure list are each signature-verified');
    assert.ok(calls.some(call => call.at(-1).endsWith(infrastructureImagesName)));
    assert.deepEqual(readdirSync(context.staging).sort(),
      [...readdirSync(context.assets), ...allImageMembers, offlineBundleVerificationName].sort());
    assert.deepEqual(record.images.map(image => image.member).sort(), allImageMembers);
    for (const image of record.images) {
      assert.equal(image.sha256, sha256(readFileSync(join(context.staging, image.member))));
    }
    const api = record.images.find(image => image.id === 'api');
    assert.equal(api.kind, 'application');
    assert.equal(api.reference, `ghcr.io/olyforge3d/printfarmer-api:${context.release.version}`);
    assert.equal(api.digest, context.application.api.digest);
    assert.deepEqual(api.platforms, context.application.api.platforms);
    const worker = record.images.find(image => image.id === 'orcaslicer-worker');
    assert.deepEqual(Object.keys(worker.platforms), ['linux/amd64']);
    const mssql = record.images.find(image => image.id === 'mssql');
    assert.equal(mssql.kind, 'infrastructure');
    assert.equal(mssql.mediaType, mediaTypes.dockerManifest);
    assert.equal(mssql.digest, context.infrastructure.mssql.digest);

    // Only the selected platform closure is carried: no arm/v7 child, no attestation manifest.
    const nginx = context.infrastructure.nginx;
    const carried = nestedEntries(readFileSync(join(context.staging, 'infrastructure-nginx.oci.tar'))).map(entry => entry.name);
    assert.deepEqual(carried.slice(0, 2), ['oci-layout', 'index.json']);
    assert.deepEqual(carried.slice(2), [nginx, ...nginx.children, ...nginx.children.map(child => child.config),
      ...nginx.children.map(child => child.layer)].map(blob => blobName(blob.digest)).sort());
    const alias = JSON.parse(nestedEntries(readFileSync(join(context.staging, 'infrastructure-nginx.oci.tar')))[1].data);
    assert.equal(alias.manifests[0].annotations['io.containerd.image.name'], 'docker.io/library/nginx:alpine');

    const loads = [];
    const docker = (name, args, options) => {
      assert.equal(name, 'docker', 'load never invokes anything but the local engine');
      assert.deepEqual(args, ['load'], 'load never pulls, builds or tags');
      assert.equal(typeof options?.stdin, 'number', 'the verified descriptor itself is handed to docker load');
      const header = Buffer.alloc(512);
      readSync(options.stdin, header, 0, 512, 0);
      assert.equal(header.subarray(0, 10).toString('latin1'), 'oci-layout');
      loads.push(fstatSync(options.stdin).size);
      return '';
    };
    const { loaded } = load(context, docker);
    assert.equal(loads.length, allImageMembers.length);
    assert.deepEqual(loaded.map(image => image.member).sort(), allImageMembers);
    assert.deepEqual(loaded.map(image => image.reference).sort(), record.images.map(image => image.reference).sort());
  } finally {
    context.cleanup();
  }
});

test('image platform matching accepts only the canonical arm64 variant', () => {
  assert.equal(platformMatches({ os: 'linux', architecture: 'arm64', variant: 'v8' }, 'linux/arm64'), true);
  assert.equal(platformMatches({ os: 'linux', architecture: 'arm64' }, 'linux/arm64'), true);
  assert.equal(platformMatches({ os: 'linux', architecture: 'arm64', variant: 'v9' }, 'linux/arm64'), false);
  assert.equal(platformMatches({ os: 'linux', architecture: 'arm', variant: 'v7' }, 'linux/arm64'), false);
  assert.equal(platformMatches({ os: 'linux', architecture: 'amd64', variant: 'v8' }, 'linux/amd64'), false);
  assert.equal(platformMatches({ os: 'windows', architecture: 'amd64' }, 'linux/amd64'), false);
  assert.equal(platformMatches(undefined, 'linux/amd64'), false);
});

test('a tampered image layer is rejected before anything is imported', () => withImages(context => {
  rewriteBundle(context, ({ members }) => editNested(members, 'image-api.oci.tar', entries => {
    const layer = entries.find(entry => entry.name === blobName(context.application.api.children[0].layer.digest));
    layer.data[0] ^= 1;
  }));
  rejectsWithoutStaging(context, /image-api\.oci\.tar failed verification: Image blob was modified/);
}));

test('a moved or non-canonical image alias is rejected', () => {
  withImages(context => {
    rewriteBundle(context, ({ members }) => editNested(members, 'infrastructure-postgres.oci.tar', entries => {
      const index = JSON.parse(entries[1].data);
      for (const key of ['io.containerd.image.name', 'org.opencontainers.image.ref.name']) {
        index.manifests[0].annotations[key] = 'docker.io/library/postgres:latest';
      }
      entries[1].data = Buffer.from(JSON.stringify(index));
    }));
    rejectsWithoutStaging(context, /Image archive alias does not match the pinned reference: postgres/);
  });
  withImages(context => {
    rewriteBundle(context, ({ members }) => editNested(members, 'infrastructure-postgres.oci.tar', entries => {
      entries[1].data = Buffer.from(JSON.stringify(JSON.parse(entries[1].data), undefined, 1));
    }));
    rejectsWithoutStaging(context, /Image archive index is not canonical/);
  });
});

test('an image archive for a different root digest is rejected even under the right alias', () => withImages(context => {
  rewriteBundle(context, ({ members }) => {
    const entries = nestedEntries(members.get('image-api.oci.tar'));
    const root = JSON.parse(entries[1].data).manifests[0];
    entries[1].data = canonicalImageIndex({ reference: `ghcr.io/olyforge3d/printfarmer-frontend:${context.release.version}` }, root);
    members.set('image-frontend.oci.tar', nestedArchive(entries));
  });
  rejectsWithoutStaging(context, /image-frontend\.oci\.tar failed verification: Image archive root digest does not match/);
}));

test('a platform child that is not the pinned digest is rejected', () => withImages(context => {
  const lock = structuredClone(context.lock);
  lock.images[1].platforms['linux/arm64'] = context.infrastructure.nginx.extras[0].digest;
  rewriteBundle(context, ({ members }) => resignedInfrastructure(members,
    infrastructureImagesDocument(context.identity, lock), context.release.channel));
  rejectsWithoutStaging(context,
    /infrastructure-nginx\.oci\.tar failed verification: Image linux\/arm64 manifest digest does not match the pinned identity/);
}));

test('a platform manifest whose config names another platform is rejected on both sides', () => {
  const context = imageFixture();
  try {
    const wrong = platformManifest(context.writeBlob, 'wrong', 'linux/arm64/v8');
    const root = context.writeBlob(JSON.stringify({ schemaVersion: 2, mediaType: mediaTypes.ociIndex, manifests: [
      { mediaType: wrong.mediaType, digest: wrong.digest, size: wrong.size, platform: { os: 'linux', architecture: 'amd64' } }] }));
    const expected = { id: 'wrong', reference: 'docker.io/library/wrong:1', mediaTypes: [mediaTypes.ociIndex],
      digest: root.digest, platforms: { 'linux/amd64': wrong.digest } };
    const output = join(context.root, 'wrong.oci.tar');
    assert.throws(() => writeImageArchive({ layout: context.layout, expected, output }), /Image config is not linux\/amd64/);
    assert.equal(existsSync(output), false);
    const blobs = [root, wrong, wrong.config, wrong.layer].map(blob => ({ name: blobName(blob.digest),
      data: readFileSync(join(context.layout, 'blobs', 'sha256', blob.digest.slice(7))) })).sort(byName);
    const archive = nestedArchive([{ name: 'oci-layout', data: ociLayoutBytes },
      { name: 'index.json', data: canonicalImageIndex(expected, { mediaType: mediaTypes.ociIndex, ...root }) }, ...blobs]);
    assert.throws(() => verifyNested(context.root, archive, expected), /Image config is not linux\/amd64/);
  } finally {
    context.cleanup();
  }
});

test('unselected platforms and attestation blobs are rejected when smuggled into an archive', () => withImages(context => {
  rewriteBundle(context, ({ members }) => editNested(members, 'infrastructure-nginx.oci.tar', entries => {
    const attestation = context.infrastructure.nginx.extras[1];
    return [...entries.slice(0, 2), ...[...entries.slice(2), { name: blobName(attestation.digest),
      data: readFileSync(join(context.layout, 'blobs', 'sha256', attestation.digest.slice(7))) }].sort(byName)];
  }));
  rejectsWithoutStaging(context, /carries blobs outside the selected platforms: nginx/);
}));

test('a missing or extra image is rejected as not the release-selected set', () => {
  withImages(context => {
    rewriteBundle(context, ({ members }) => members.delete('image-orcaslicer-worker.oci.tar'));
    rejectsWithoutStaging(context, /does not carry exactly the release-selected image set/);
  });
  withImages(context => {
    rewriteBundle(context, ({ members, roles }) => {
      members.set('infrastructure-redis.oci.tar', members.get('infrastructure-nginx.oci.tar'));
      roles.set('infrastructure-redis.oci.tar', 'infrastructure-image');
    });
    rejectsWithoutStaging(context, /does not carry exactly the release-selected image set/);
  });
  withImages(context => {
    rewriteBundle(context, ({ members, roles }) => {
      members.set('image-unknown.oci.tar', members.get('image-api.oci.tar'));
      roles.set('image-unknown.oci.tar', 'application-image');
    });
    rejectsWithoutStaging(context, /member is not part of this release: image-unknown\.oci\.tar/);
  });
});

test('images are all or nothing and must match the contents flags', () => {
  withImages(context => {
    rewriteBundle(context, ({ index }) => { index.contents.infrastructure = false; });
    rejectsWithoutStaging(context, /contents claim material/);
  });
  withImages(context => {
    rewriteBundle(context, ({ index }) => { index.contents.images = false; index.contents.infrastructure = false; });
    rejectsWithoutStaging(context, /image members do not match its image contents flag/);
  });
  withImages(context => {
    rewriteBundle(context, ({ members }) => members.delete(infrastructureImagesSignatureName));
    rejectsWithoutStaging(context, /missing required member: infrastructure-images\.sigstore\.json/);
  });
  const context = fixture();
  try {
    assemble(context);
    rewriteBundle(context, ({ index }) => { index.contents.images = true; index.contents.infrastructure = true; });
    rejectsWithoutStaging(context, /image members do not match its image contents flag/);
  } finally {
    context.cleanup();
  }
});

test('the infrastructure image list must be signed and bound to this exact release', () => {
  withImages(context => {
    rewriteBundle(context, ({ members }) => members.set(infrastructureImagesName,
      Buffer.concat([members.get(infrastructureImagesName), Buffer.from(' ')])));
    rejectsWithoutStaging(context, /signature verification failed for infrastructure-images\.json/);
  });
  withImages(context => {
    const bytes = infrastructureImagesDocument(context.identity, context.lock);
    rewriteBundle(context, ({ members }) => resignedInfrastructure(members, bytes,
      context.release.channel === 'stable' ? 'insider' : 'stable'));
    rejectsWithoutStaging(context, /signature verification failed for infrastructure-images\.json/);
  });
  withImages(context => {
    rewriteBundle(context, ({ members }) => resignedInfrastructure(members,
      infrastructureImagesDocument({ ...context.identity, buildId: '99' }, context.lock), context.release.channel));
    rejectsWithoutStaging(context, /not bound to this release identity/);
  });
  const context = imageFixture();
  try {
    context.writeInfrastructure(context.lock, { ...context.identity, sourceCommit: 'c'.repeat(40) });
    assert.throws(() => assemble(context, { images: context.layout }), /not bound to this release identity/);
    assert.deepEqual(readdirSync(context.root).sort(), ['assets', 'layout', 'trusted_root.json']);
  } finally {
    context.cleanup();
  }
});

test('nested archive blob count and size are bounded before any blob is trusted', () => withImages(context => {
  rejectsWithoutStaging(context, /Image archive has too many members/, { imageLimits: { ...imageArchiveLimits, maxBlobs: 3 } });
  rejectsWithoutStaging(context, /Image archive exceeds its size limit/,
    { imageLimits: { ...imageArchiveLimits, maxArchiveBytes: 4096 } });
  rejectsWithoutStaging(context, /Offline bundle member exceeds its size limit: image-/,
    { limits: { ...offlineBundleLimits, maxImageArchiveBytes: 2048 } });
  rejectsWithoutStaging(context, /exceeds its size limit/, { imageLimits: { ...imageArchiveLimits, maxJsonBytes: 16 } });
}));

test('malformed nested archive shapes are rejected', () => {
  const context = imageFixture();
  try {
    const expected = context.expected('postgres');
    const output = join(context.root, 'postgres.oci.tar');
    writeImageArchive({ layout: context.layout, expected, output });
    const valid = nestedEntries(readFileSync(output));
    assert.equal(verifyNested(context.root, nestedArchive(valid), expected).digest, expected.digest);
    const cases = [
      [entries => { entries[3] = { ...entries[3], name: 'etc/passwd' }; }, /not part of an OCI layout/],
      [entries => { entries[3] = { ...entries[3], name: `../${entries[3].name}` }; }, /not part of an OCI layout/],
      [entries => { entries[3] = { ...entries[3], type: '2' }; }, /not a regular file/],
      [entries => { entries.splice(2, 0, { name: 'blobs/', data: Buffer.alloc(0), type: '5' }); }, /not a regular file/],
      [entries => { [entries[2], entries[3]] = [entries[3], entries[2]]; }, /canonical order/],
      [entries => { entries.splice(3, 0, entries[3]); }, /canonical order/],
      [entries => { [entries[0], entries[1]] = [entries[1], entries[0]]; }, /canonical order/],
      [entries => { entries[0] = { ...entries[0], data: Buffer.from('{"imageLayoutVersion":"2.0.0"}') }; }, /oci-layout is not supported/],
      [entries => {
        const index = JSON.parse(entries[1].data);
        index.manifests.push(index.manifests[0]);
        entries[1] = { ...entries[1], data: Buffer.from(JSON.stringify(index)) };
      }, /must name exactly one image/],
      [entries => { entries.splice(entries.findIndex(entry =>
        entry.name === blobName(context.infrastructure.postgres.children[1].layer.digest)), 1); }, /Image is missing a linux\/arm64 layer/],
    ];
    for (const [edit, pattern] of cases) {
      const entries = structuredClone(valid).map(entry => ({ ...entry, data: Buffer.from(entry.data) }));
      edit(entries);
      assert.throws(() => verifyNested(context.root, nestedArchive(entries), expected), pattern, String(pattern));
    }
    assert.throws(() => verifyNested(context.root, Buffer.concat([nestedArchive(valid), Buffer.alloc(512, 1)]), expected),
      /data after its end marker|not a complete tar archive/);
    assert.throws(() => verifyNested(context.root, nestedArchive(valid, { trailer: Buffer.alloc(512) }), expected),
      /end marker/);
  } finally {
    context.cleanup();
  }
});

test('assembly fails closed on missing or modified layout content and leaves no output', () => {
  for (const [edit, pattern] of [
    [context => rmSync(join(context.layout, 'blobs', 'sha256', context.application.monolith.children[1].layer.digest.slice(7))),
      /Image is missing a linux\/arm64 layer/],
    [context => rmSync(join(context.layout, 'blobs', 'sha256', context.infrastructure.mssql.digest.slice(7))),
      /Image layout is missing blob/],
    [context => {
      const path = join(context.layout, 'blobs', 'sha256', context.application.frontend.children[0].layer.digest.slice(7));
      const bytes = readFileSync(path);
      bytes[5] ^= 1;
      writeFileSync(path, bytes);
    }, /Image layout blob does not match its digest/],
    [context => {
      const path = join(context.layout, 'blobs', 'sha256', context.infrastructure.nginx.digest.slice(7));
      writeFileSync(path, Buffer.concat([readFileSync(path), Buffer.from(' ')]));
    }, /Image layout blob does not match its digest|size does not match/],
  ]) {
    const context = imageFixture();
    try {
      edit(context);
      assert.throws(() => assemble(context, { images: context.layout }), pattern, String(pattern));
      assert.deepEqual(readdirSync(context.root).sort(), ['assets', 'layout', 'trusted_root.json'],
        'no bundle, partial bundle or partial image stage survives a failed assembly');
    } finally {
      context.cleanup();
    }
  }
  const context = imageFixture();
  try {
    assert.throws(() => assemble(context, { images: context.layout, imageLimits: { ...imageArchiveLimits, maxBlobs: 3 } }),
      /Image has too many blobs/);
  } finally {
    context.cleanup();
  }
});

test('load only accepts images that are unchanged since verification', () => {
  withImages(context => {
    verify(context);
    const path = join(context.staging, 'image-slicer-host.oci.tar');
    const bytes = readFileSync(path);
    bytes[bytes.length - 2048] ^= 1;
    writeFileSync(path, bytes);
    const calls = [];
    assert.throws(() => load(context, (...call) => calls.push(call)),
      /changed after verification: image-slicer-host\.oci\.tar/);
    assert.ok(calls.every(([, args]) => args[0] === 'load'));
  });
  const context = fixture();
  try {
    assemble(context);
    const record = verify(context);
    assert.deepEqual(record.images, []);
    assert.throws(() => load(context, () => assert.fail('nothing to load')),
      /holds no verified image set/);
    assert.throws(() => load(context, () => assert.fail('never'), { staging: context.assets }),
      /verification record is unusable/);
  } finally {
    context.cleanup();
  }
});

test('load derives expectations from the re-authenticated signed release, never the mutable record', () => {
  const never = () => assert.fail('nothing may be loaded');
  withImages(context => {
    verify(context);
    // Replace an archive AND its record entry with a self-consistent different image.
    const recordPath = join(context.staging, offlineBundleVerificationName);
    const record = JSON.parse(readFileSync(recordPath, 'utf8'));
    const victim = record.images.find(image => image.id === 'postgres');
    const donor = record.images.find(image => image.id === 'nginx');
    const bytes = readFileSync(join(context.staging, donor.member));
    writeFileSync(join(context.staging, victim.member), bytes);
    Object.assign(victim, { reference: donor.reference, mediaType: donor.mediaType, digest: donor.digest,
      platforms: donor.platforms, size: bytes.length, sha256: sha256(bytes) });
    writeFileSync(recordPath, JSON.stringify(record));
    assert.throws(() => load(context, never), /infrastructure-postgres\.oci\.tar does not match the signed release/);
  });
  withImages(context => {
    verify(context);
    const recordPath = join(context.staging, offlineBundleVerificationName);
    const record = JSON.parse(readFileSync(recordPath, 'utf8'));
    record.images = record.images.filter(image => image.id !== 'mssql');
    writeFileSync(recordPath, JSON.stringify(record));
    assert.throws(() => load(context, never), /does not name exactly the signed release-selected image set/);
  });
  withImages(context => {
    verify(context);
    // A forged infrastructure list is rejected by the offline signature check.
    context.lock.images[1].digest = context.lock.images[2].digest;
    writeFileSync(join(context.staging, infrastructureImagesName),
      infrastructureImagesDocument(context.identity, context.lock));
    assert.throws(() => load(context, never), /signature verification failed for infrastructure-images\.json/);
  });
  withImages(context => {
    verify(context);
    assert.throws(() => load(context, never, { channel: 'insider' }), /does not match the expected insider channel/);
    assert.throws(() => load(context, never, { trustedRoot: undefined }), /requires an operator-supplied Sigstore trusted root/);
  });
});

test('the repository infrastructure lock is valid and is proven against the registry before signing', () => {
  const repositoryLock = validateInfrastructureLock(readFileSync(new URL(`../../../${infrastructureLockPath}`, import.meta.url)));
  assert.deepEqual(repositoryLock.images.map(image => image.id), ['mssql', 'nginx', 'postgres']);
  for (const image of repositoryLock.images) assert.ok(image.platforms['linux/amd64'], `${image.id} supports linux/amd64`);
  assert.deepEqual(Object.keys(repositoryLock.images.find(image => image.id === 'postgres').platforms), ['linux/amd64', 'linux/arm64']);

  const context = imageFixture();
  try {
    const blob = digest => readFileSync(join(context.layout, 'blobs', 'sha256', digest.slice(7)), 'utf8');
    const registry = (overrides = {}) => (name, args) => {
      assert.equal(name, 'docker');
      assert.deepEqual(args.slice(0, 3), ['buildx', 'imagetools', 'inspect']);
      const digest = args[3].slice(args[3].indexOf('@') + 1);
      if (args[4] === '--raw') return overrides.raw?.(digest) ?? blob(digest);
      assert.deepEqual(args.slice(4), ['--format', '{{json .Image}}']);
      return overrides.config?.(digest) ?? blob(context.infrastructure.mssql.config.digest);
    };
    verifyInfrastructureLockAgainstRegistry(context.lock, registry());
    assert.throws(() => verifyInfrastructureLockAgainstRegistry(context.lock,
      registry({ raw: digest => `${blob(digest)} ` })), /Registry content does not match the pinned digest/);
    const moved = structuredClone(context.lock);
    moved.images[2].platforms['linux/arm64'] = context.infrastructure.nginx.children[1].digest;
    assert.throws(() => verifyInfrastructureLockAgainstRegistry(moved, registry()),
      /does not select the pinned linux\/arm64 manifest/);
    assert.throws(() => verifyInfrastructureLockAgainstRegistry(context.lock,
      registry({ config: () => JSON.stringify({ os: 'linux', architecture: 'arm64' }) })), /platform does not match the lock/);
  } finally {
    context.cleanup();
  }

  const valid = JSON.parse(readFileSync(new URL(`../../../${infrastructureLockPath}`, import.meta.url), 'utf8'));
  for (const edit of [
    lock => { lock.images.reverse(); },
    lock => { lock.images[1].id = lock.images[0].id; },
    lock => { lock.images[1].reference = 'nginx:alpine'; },
    lock => { lock.images[1].platforms['linux/arm/v7'] = lock.images[1].platforms['linux/arm64']; },
    lock => { lock.images[0].platforms['linux/arm64'] = lock.images[1].digest; },
    lock => { lock.images[1].platforms['linux/arm64'] = lock.images[1].platforms['linux/amd64']; },
    lock => { lock.images[1].mirror = 'x'; },
    lock => { lock.kind = 'other'; },
  ]) {
    const lock = structuredClone(valid);
    edit(lock);
    assert.throws(() => validateInfrastructureLock(Buffer.from(JSON.stringify(lock))), /Infrastructure image lock/);
  }
});

test('the command line exposes image assembly and a load-only import without bypasses', () => {
  assert.deepEqual(parseArguments(['load', '--staging', 's', '--channel', 'stable', '--trusted-root', 't']).options,
    { staging: 's', channel: 'stable', 'trusted-root': 't' });
  assert.equal(parseArguments(['assemble', '--release-assets', 'a', '--channel', 'stable', '--output', 'o',
    '--images', 'layout']).options.images, 'layout');
  for (const argv of [
    ['load', '--bundle', 'b'],
    ['load', '--pull', 'true'],
    ['load', '--skip-verification', 'true'],
    ['verify', '--images', 'layout'],
    ['load', '--staging', 'a', '--staging', 'b'],
  ]) {
    assert.throws(() => parseArguments(argv), /usage|Duplicate option/, argv.join(' '));
  }
});

// ---------------------------------------------------------------------------------------------
// Issue #3063: release-bound recovery instructions and the host-local import decision path
// ---------------------------------------------------------------------------------------------
function withInstructions(context, { boundTo = context.identity, signedBy = context.release.channel } = {}) {
  const bytes = recoveryInstructionsDocument(boundTo);
  writeFileSync(join(context.assets, recoveryInstructionsName), bytes);
  writeFileSync(join(context.assets, recoveryInstructionsSignatureName), sign(bytes, signedBy));
  return bytes;
}

function completeFixture(channel = 'stable') {
  const context = imageFixture(channel);
  withInstructions(context);
  withDeploymentSet(context);
  assemble(context, { images: context.layout, tools: context.tools });
  context.records = join(context.root, 'records');
  mkdirSync(context.records);
  approveTrustedRoot(context);
  return context;
}

// ---------------------------------------------------------------------------------------------
// Issue #3081: the signed deployment set (real repository templates) and approved tool bytes
// ---------------------------------------------------------------------------------------------
const repositoryRoot = join(import.meta.dirname, '..', '..', '..');
const deploymentTemplates = readDeploymentTemplates(repositoryRoot);

function fakeToolLock(tools) {
  mkdirSync(tools, { recursive: true });
  const artifacts = [['linux/amd64', 'cosign-linux-amd64'], ['linux/arm64', 'cosign-linux-arm64'],
    ['windows/amd64', 'cosign-windows-amd64.exe']].map(([platform, name]) => {
    const bytes = Buffer.from(`fake ${name} ${'c'.repeat(900)}`);
    writeFileSync(join(tools, name), bytes);
    return { tool: 'cosign', version: '3.0.6', platform, name,
      url: `https://github.com/sigstore/cosign/releases/download/v3.0.6/${name}`, size: bytes.length, sha256: sha256(bytes) };
  });
  const lock = validateOfflineToolsLock(readFileSync(join(repositoryRoot, ...offlineToolsLockPath.split('/'))));
  return { artifacts, imageTools: lock.imageTools };
}

function withDeploymentSet(context, { boundTo = context.identity, signedBy = context.release.channel, lock } = {}) {
  context.tools = join(context.root, 'tools');
  context.toolLock = lock ?? fakeToolLock(context.tools);
  const bytes = deploymentSetDocument(boundTo, { templates: deploymentTemplates, lock: context.toolLock });
  writeFileSync(join(context.assets, deploymentSetName), bytes);
  writeFileSync(join(context.assets, deploymentSetSignatureName), sign(bytes, signedBy));
  return bytes;
}

function approveTrustedRoot(context, overrides = {}) {
  context.approval = join(context.root, 'trusted-root-approval.json');
  writeFileSync(context.approval, `${JSON.stringify({ schema: 1, kind: trustedRootApprovalKind,
    trustedRootSha256: sha256(readFileSync(context.trustedRoot)), approvedAt: '2026-09-01T00:00:00.000Z',
    approvedBy: 'ops.bob', ...overrides })}\n`);
  return context.approval;
}

// Fake host-update CLI `offline-admit`: a durable per-channel replay store keyed by manifest digest,
// with a strictly increasing sequence high-water mark, bound to a standing policy channel.
function replayStore({ policyChannel, highWater = {} } = {}) {
  const decisions = new Map();
  const calls = [];
  const admit = args => {
    calls.push(args);
    assert.deepEqual([args.length, args[0], args[1], args[3], args[5], args[7]],
      [8, 'offline-admit', '--staging', '--channel', '--trusted-root', '--json']);
    assert.ok(isAbsolute(args[2]), 'offline-admit receives the absolute verified staging directory');
    assert.ok(isAbsolute(args[6]), 'offline-admit re-verifies against the absolute trusted root');
    const inside = relative(args[2], args[6]);
    assert.ok(inside.startsWith('..') || isAbsolute(inside),
      'the trusted root offline-admit verifies against lies outside the mutable staging directory');
    const staging = args[2];
    const record = JSON.parse(readFileSync(join(staging, offlineBundleVerificationName), 'utf8'));
    const digest = `sha256:${sha256(readFileSync(join(staging, 'update-manifest.json')))}`;
    assert.equal(record.manifestDigest, digest, 'offline-admit reads the verified staging');
    const channel = args[4];
    const fail = (exitCode, result) => {
      const error = new Error(`host update CLI exited ${exitCode}`);
      error.status = exitCode;
      error.stdout = `${JSON.stringify({ exitCode, result })}\n`;
      throw error;
    };
    if ((policyChannel ?? channel) !== channel) fail(6, { code: 'channel_mismatch_policy', details: [] });
    const sequence = record.release.sequence;
    const report = (disposition, reused, correlationId) => ({ decision: ['Imported', 'Accepted'].includes(disposition)
      ? 'admitted' : 'refused', reason: disposition === 'Imported' ? null : `replay_${disposition.toLowerCase()}`,
    releaseId: `${channel}:${record.release.version}`, channel: channel.toUpperCase(), sequence, manifestDigest: digest,
    disposition, correlationId, reused });
    const existing = decisions.get(digest);
    if (existing) return `${JSON.stringify({ exitCode: 0, result: report(existing.disposition, true, existing.id) })}\n`;
    if ((highWater[channel] ?? 0) >= sequence) fail(6, report('Rejected', false, `r-${calls.length}`));
    highWater[channel] = sequence;
    const id = `c-${calls.length}`;
    decisions.set(digest, { disposition: 'Imported', id });
    return `${JSON.stringify({ exitCode: 0, result: report('Imported', false, id) })}\n`;
  };
  return { admit, calls, decisions, highWater };
}

function importRunner(store = replayStore()) {
  const signatures = cosign({ requireOffline: true });
  const loads = [];
  const run = (name, args, options) => {
    if (name === 'cosign') return signatures.run(name, args);
    if (name === hostUpdateCliRunnerName) return store.admit(args);
    assert.equal(name, 'docker');
    assert.deepEqual(args, ['load']);
    assert.equal(typeof options?.stdin, 'number');
    loads.push(fstatSync(options.stdin).size);
    return '';
  };
  return { run, loads, calls: signatures.calls, store };
}

function runImport(context, overrides = {}) {
  return importOfflineBundle({ bundle: context.bundle, channel: context.release.channel, version: context.release.version,
    trustedRoot: context.trustedRoot, trustedRootApproval: context.approval, staging: context.staging,
    records: context.records, operator: 'ops.alice', run: importRunner().run, now: () => new Date('2026-09-25T20:00:00Z'),
    ...overrides });
}

function decisionFiles(context) {
  return readdirSync(context.records).sort();
}

test('recovery instructions are derived only from the signed release identity', () => {
  const identity = { ...releases.stable, sequence: deriveSequence(releases.stable.version) };
  const bytes = recoveryInstructionsDocument(identity);
  assert.deepEqual(recoveryInstructionsDocument({ ...identity }), bytes, 'generation is deterministic');
  const document = validateRecoveryInstructions(bytes, identity);
  assert.equal(document.kind, 'printfarmer-offline-recovery-instructions');
  assert.equal(document.rolloutAuthorization, false);
  assert.deepEqual(document.operations.map(operation => operation.id), ['offline-bundle-import', 'host-update-status',
    'host-update-recover-preview', 'host-update-recover-confirm']);
  for (const operation of document.operations) {
    assert.equal(operation.bash[0], 'printfarmer-host-update.sh');
    assert.deepEqual(operation.powershell.slice(0, 3), ['pwsh', '-File', 'printfarmer-host-update.ps1']);
    assert.ok(!operation.bash.some(arg => /force|skip|reset|rollout/i.test(arg)), 'no bypass or rollout operation');
  }
  const confirm = document.operations.at(-1);
  assert.deepEqual(confirm.bash.slice(-4), ['--release', 'stable:1.4.0', '--confirm', 'stable:1.4.0']);
  for (const other of [{ ...identity, buildId: '99' }, { ...identity, sourceCommit: 'c'.repeat(40) }]) {
    assert.throws(() => validateRecoveryInstructions(bytes, other), /not the exact release-bound instructions/);
  }
  assert.throws(() => validateRecoveryInstructions(Buffer.from(`${bytes.toString('utf8')} `), identity),
    /not the exact release-bound instructions/);
  assert.throws(() => recoveryInstructionsDocument({ ...identity, extra: 1 }), /complete release identity/);
  assert.throws(() => recoveryInstructionsDocument({ version: '1.4.0' }), /complete release identity/);
});

test('signed recovery instructions round-trip and set contents.recoveryInstructions only when complete', () => {
  const context = imageFixture('insider');
  try {
    const bytes = withInstructions(context);
    const { index } = assemble(context, { images: context.layout });
    assert.equal(index.contents.recoveryInstructions, true);
    assert.equal(index.installable, false);
    assert.ok(index.files.some(file => file.name === recoveryInstructionsName && file.role === 'recovery-instructions'));
    const { run, calls } = cosign({ requireOffline: true });
    const record = verify(context, { run });
    assert.equal(calls.length, 4, 'manifest, CLI checksums, infrastructure list and recovery instructions are each verified');
    assert.ok(calls.some(call => call.at(-1).endsWith(recoveryInstructionsName)));
    assert.deepEqual(record.recoveryInstructions, { sha256: sha256(bytes), operations: ['offline-bundle-import',
      'host-update-status', 'host-update-recover-preview', 'host-update-recover-confirm'] });
    assert.deepEqual(readFileSync(join(context.staging, recoveryInstructionsName)), bytes);
  } finally {
    context.cleanup();
  }
  const plain = fixture('stable');
  try {
    assert.equal(assemble(plain).index.contents.recoveryInstructions, false);
    assert.equal(verify(plain).recoveryInstructions, false);
  } finally {
    plain.cleanup();
  }
});

test('recovery instructions and their signature are both-or-neither at assembly', () => {
  for (const drop of [recoveryInstructionsName, recoveryInstructionsSignatureName]) {
    const context = imageFixture('stable');
    try {
      withInstructions(context);
      rmSync(join(context.assets, drop));
      assert.throws(() => assemble(context, { images: context.layout }), /must be present together/);
      assert.equal(existsSync(context.bundle), false);
    } finally {
      context.cleanup();
    }
  }
  const context = imageFixture('stable');
  try {
    withInstructions(context, { boundTo: { ...context.identity, buildId: '99' } });
    assert.throws(() => assemble(context, { images: context.layout }), /not the exact release-bound instructions/);
  } finally {
    context.cleanup();
  }
});

test('forged, re-signed, wrong-identity or unflagged recovery instructions are rejected before import', () => {
  const context = imageFixture('stable');
  try {
    withInstructions(context);
    assemble(context, { images: context.layout });
    const original = readFileSync(context.bundle);
    const restore = () => writeFileSync(context.bundle, original);
    rewriteBundle(context, ({ members }) => {
      members.set(recoveryInstructionsName, Buffer.from(members.get(recoveryInstructionsName).toString('utf8')
        .replace('"rolloutAuthorization": false', '"rolloutAuthorization": true')));
    });
    rejectsWithoutStaging(context, /signature verification failed for offline-recovery-instructions\.json/);
    restore();
    rewriteBundle(context, ({ members }) => {
      const bytes = recoveryInstructionsDocument({ ...context.identity, buildId: '99' });
      members.set(recoveryInstructionsName, bytes);
      members.set(recoveryInstructionsSignatureName, Buffer.from(sign(bytes, 'stable')));
    });
    rejectsWithoutStaging(context, /not the exact release-bound instructions/);
    restore();
    rewriteBundle(context, ({ members }) => {
      members.set(recoveryInstructionsSignatureName, Buffer.from(sign(members.get(recoveryInstructionsName), 'insider')));
    });
    rejectsWithoutStaging(context, /signature verification failed for offline-recovery-instructions\.json/);
    restore();
    rewriteBundle(context, ({ index }) => { index.contents.recoveryInstructions = false; });
    rejectsWithoutStaging(context, /do not match its recovery instructions flag/);
    restore();
    rewriteBundle(context, ({ members }) => { members.delete(recoveryInstructionsSignatureName); });
    rejectsWithoutStaging(context, /do not match its recovery instructions flag/);
    restore();
    assert.ok(verify(context).recoveryInstructions);
  } finally {
    context.cleanup();
  }
  const plain = fixture('stable');
  try {
    assemble(plain);
    rewriteBundle(plain, ({ index }) => { index.contents.recoveryInstructions = true; });
    rejectsWithoutStaging(plain, /do not match its recovery instructions flag/);
  } finally {
    plain.cleanup();
  }
});

test('import verifies, loads only verified images and writes one durable redacted decision record', () => {
  const context = completeFixture('stable');
  try {
    const runner = importRunner();
    const { record, path } = runImport(context, { run: runner.run, newId: () => '00000000-0000-4000-8000-000000000001' });
    assert.equal(record.outcome, 'imported');
    assert.equal(record.reason, null);
    assert.equal(record.kind, offlineImportDecisionKind);
    assert.equal(record.operator, 'ops.alice');
    assert.equal(record.decidedAt, '2026-09-25T20:00:00.000Z');
    assert.equal(record.installable, true, 'a complete, replay-admitted import is installable');
    assert.equal(record.rolloutAuthorization, false, 'import never authorizes a rollout');
    assert.deepEqual(record.replay, { disposition: 'Imported', correlationId: 'c-1', reused: false,
      sequence: context.identity.sequence, admitted: true });
    assert.deepEqual(record.trust, { trustedRootSha256: sha256(readFileSync(context.trustedRoot)),
      approvedAt: '2026-09-01T00:00:00.000Z', approvedBy: 'ops.bob', approvalExpiresAt: '2026-11-30T00:00:00.000Z' });
    assert.equal(runner.store.calls.length, 1);
    assert.equal(JSON.parse(readFileSync(join(context.staging, offlineBundleVerificationName), 'utf8')).installable, false,
      'the verification record itself stays not-installable');
    assert.equal(record.bundleSha256, sha256(readFileSync(context.bundle)));
    assert.deepEqual(record.release, { ...context.identity, sequence: context.identity.sequence });
    assert.deepEqual(record.verifiedDigests.images.map(image => image.member).sort(), allImageMembers);
    assert.equal(record.verifiedDigests.recoveryInstructions,
      sha256(readFileSync(join(context.assets, recoveryInstructionsName))));
    assert.equal(record.verifiedDigests.deploymentSet, sha256(readFileSync(join(context.assets, deploymentSetName))));
    assert.deepEqual(record.loadedImages.map(image => image.member).sort(), allImageMembers);
    assert.equal(runner.loads.length, allImageMembers.length);
    assert.deepEqual(decisionFiles(context), ['2026-09-25T20-00-00-000Z-00000000-0000-4000-8000-000000000001.json']);
    assert.equal(path, join(context.records, decisionFiles(context)[0]));
    const text = readFileSync(path, 'utf8');
    assert.deepEqual(JSON.parse(text), record);
    assert.ok(!text.includes(context.root), 'the record names the bundle by digest, never by host path');
    if (process.platform !== 'win32') assert.equal(fstatSync(openSync(path, 'r')).mode & 0o777, 0o600);
    assert.ok(existsSync(join(context.staging, offlineBundleVerificationName)), 'a verified import keeps its staging');
  } finally {
    context.cleanup();
  }
});

test('import refuses a verified but incomplete bundle, removes its staging and records the refusal', () => {
  for (const [label, prepare, pattern] of [
    ['no recovery instructions', context => assemble(context, { images: context.layout }), /lacks signed recovery instructions/],
    ['no images', context => { withInstructions(context); assemble(context); },
      /lacks release-selected images and the infrastructure image list/],
    ['no deployment set', context => { withInstructions(context); assemble(context, { images: context.layout }); },
      /lacks the signed deployment set and its approved tools/],
    ['pre-#3081 index', context => {
      withInstructions(context);
      assemble(context, { images: context.layout });
      rewriteBundle(context, ({ index }) => { delete index.contents.deploymentSet; });
    }, /lacks the signed deployment set and its approved tools/],
  ]) {
    const context = imageFixture('stable');
    try {
      prepare(context);
      context.records = join(context.root, 'records');
      mkdirSync(context.records);
      approveTrustedRoot(context);
      const runner = importRunner();
      const { record } = runImport(context, { run: runner.run });
      assert.equal(record.outcome, 'refused', label);
      assert.match(record.reason, pattern);
      assert.equal(runner.loads.length, 0, 'nothing is loaded from an incomplete bundle');
      assert.equal(runner.store.calls.length, 0, 'an incomplete bundle is never offered to the replay store');
      assert.equal(record.installable, false);
      assert.deepEqual(record.loadedImages, []);
      assert.equal(existsSync(context.staging), false, 'a refused import leaves no staging directory');
      assert.equal(decisionFiles(context).length, 1);
      assert.equal(record.bundleSha256, sha256(readFileSync(context.bundle)));
    } finally {
      context.cleanup();
    }
  }
});

test('import refuses unverified bundles with a redacted reason and never loads', () => {
  const context = completeFixture('stable');
  try {
    rewriteBundle(context, ({ members }) => {
      const name = hostUpdateCliSumsName(context.release.version);
      members.set(name, Buffer.concat([members.get(name), Buffer.from(' ')]));
    });
    const runner = importRunner();
    const { record } = runImport(context, { run: runner.run });
    assert.equal(record.outcome, 'refused');
    assert.equal(record.release, null);
    assert.equal(record.verifiedDigests, null);
    assert.equal(record.bundleSha256, sha256(readFileSync(context.bundle)));
    assert.equal(runner.loads.length, 0);
    assert.equal(existsSync(context.staging), false);
    const missing = runImport(context, { bundle: join(context.root, 'missing.tar'), staging: join(context.root, 's2') });
    assert.equal(missing.record.outcome, 'refused');
    assert.equal(missing.record.bundleSha256, null);
    assert.match(missing.record.reason, /<bundle>/);
    for (const { record: refused } of [{ record }, missing]) {
      const text = readFileSync(join(context.records, decisionFiles(context).find(name =>
        name.includes(refused.decisionId))), 'utf8');
      assert.ok(!text.includes(context.root), 'no host path is written to a decision record');
      assert.ok(refused.reason.length <= 512);
    }
    assert.equal(decisionFiles(context).length, 2);
    assert.ok(!decisionFiles(context).some(name => name.endsWith('.partial')));
  } finally {
    context.cleanup();
  }
});

test('an in-progress record is durable before the first load and a partial load is recorded accurately', () => {
  const context = completeFixture('stable');
  try {
    const decisionId = '00000000-0000-4000-8000-000000000002';
    const target = join(context.records, `2026-09-25T20-00-00-000Z-${decisionId}.json`);
    const base = importRunner();
    let loads = 0;
    const observed = [];
    const run = (name, args, options) => {
      if (name !== 'docker') return base.run(name, args, options);
      observed.push(JSON.parse(readFileSync(target, 'utf8')).outcome);
      loads += 1;
      if (loads === 2) throw new Error(`docker load failed for ${context.staging}`);
      return base.run(name, args, options);
    };
    const { record } = runImport(context, { run, newId: () => decisionId });
    assert.deepEqual(observed, ['in-progress', 'in-progress'], 'durable evidence precedes every docker load');
    assert.equal(record.outcome, 'refused');
    assert.match(record.reason, /docker load failed for <staging>/);
    assert.equal(record.loadedImages.length, 1, 'the image loaded before the failure is recorded');
    assert.equal(typeof record.failedLoad, 'string');
    assert.ok(!record.loadedImages.some(image => image.member === record.failedLoad));
    assert.deepEqual(JSON.parse(readFileSync(target, 'utf8')), record);
    assert.deepEqual(decisionFiles(context), [`2026-09-25T20-00-00-000Z-${decisionId}.json`]);
    assert.equal(existsSync(context.staging), false, 'a refused import leaves no staging directory');
  } finally {
    context.cleanup();
  }
});

test('a finalization failure after loading leaves the durable in-progress record', () => {
  const context = completeFixture('stable');
  try {
    const decisionId = '00000000-0000-4000-8000-000000000003';
    const name = `2026-09-25T20-00-00-000Z-${decisionId}.json`;
    const base = importRunner();
    const run = (command, args, options) => {
      // Block the final record's partial file so finalization fails after images were loaded.
      if (command === 'docker') mkdirSync(join(context.records, `.${name}.1.partial`), { recursive: true });
      return base.run(command, args, options);
    };
    assert.throws(() => runImport(context, { run, newId: () => decisionId }), /EEXIST/);
    assert.ok(base.loads.length > 0);
    const record = JSON.parse(readFileSync(join(context.records, name), 'utf8'));
    assert.equal(record.outcome, 'in-progress');
    assert.equal(record.decisionId, decisionId);
    assert.deepEqual(record.verifiedDigests.images.map(image => image.member).sort(), allImageMembers);
    assert.equal(record.rolloutAuthorization, false);
  } finally {
    context.cleanup();
  }
});

test('import refuses to run without an operator, version or durable records directory', () => {
  const context = completeFixture('stable');
  try {
    for (const [overrides, pattern] of [
      [{ operator: undefined }, /operator identifier/],
      [{ operator: 'alice smith' }, /operator identifier/],
      [{ operator: 'x'.repeat(65) }, /operator identifier/],
      [{ version: undefined }, /explicit expected version/],
      [{ channel: undefined }, /explicit expected channel/],
      [{ records: 'relative/records' }, /absolute path/],
      [{ records: join(context.root, 'absent') }, /existing directory/],
    ]) {
      assert.throws(() => runImport(context, overrides), pattern);
    }
    assert.deepEqual(decisionFiles(context), []);
    assert.equal(existsSync(context.staging), false);
  } finally {
    context.cleanup();
  }
});

test('decision reasons replace known and unknown host paths and stay bounded', () => {
  const reason = redactReason('Offline bundle must be a regular file (not a link): /srv/x/b.tar; root C:\\keys\\r.json and '
    + '/home/op/secret\u0007', { bundle: '/srv/x/b.tar' });
  assert.equal(reason, 'Offline bundle must be a regular file (not a link): <bundle>; root <path> and <path>');
  assert.equal(redactReason('failed [C:\\tmp\\a.tar],/var/b <\\\\srv\\c>', {}), 'failed [<path>],<path> <<path>>');
  assert.equal(redactReason('x'.repeat(2000), {}).length, 512);
  assert.equal(redactReason('arm64 linux/arm64 stable:1.2.3 sha256:abc', {}), 'arm64 linux/arm64 stable:1.2.3 sha256:abc');
});

test('the import command line requires every binding option and offers no bypass', () => {
  const full = ['import', '--bundle', 'b', '--channel', 'stable', '--version', '1.4.0', '--trusted-root', 'r',
    '--trusted-root-approval', 'a', '--staging', 's', '--records', 'd', '--operator', 'ops', '--config', 'c',
    '--host-update-cli', 'h'];
  assert.equal(parseArguments(full).options.operator, 'ops');
  for (let index = 1; index < full.length; index += 2) {
    assert.throws(() => parseArguments([...full.slice(0, index), ...full.slice(index + 2)]), /usage/, full[index]);
  }
  for (const extra of [['--force', 'true'], ['--skip-verification', 'true'], ['--images', 'x'], ['--runtime', 'linux-x64']]) {
    assert.throws(() => parseArguments([...full, ...extra]), /usage/, extra[0]);
  }
});
// ---------------------------------------------------------------------------------------------
// Issue #3064: replay protection, channel continuity and the offline trust expiry policy
// ---------------------------------------------------------------------------------------------
test('re-importing the same release reuses its replay decision and stays installable', () => {
  const context = completeFixture('insider');
  try {
    const store = replayStore();
    const first = runImport(context, { run: importRunner(store).run, newId: () => '00000000-0000-4000-8000-000000000011' });
    const second = runImport(context, { run: importRunner(store).run, staging: join(context.root, 'staging-2'),
      newId: () => '00000000-0000-4000-8000-000000000012' });
    assert.equal(first.record.outcome, 'imported');
    assert.equal(second.record.outcome, 'imported');
    assert.equal(second.record.replay.reused, true);
    assert.equal(second.record.replay.correlationId, first.record.replay.correlationId);
    assert.equal(second.record.installable, true);
    assert.equal(store.highWater.insider, context.identity.sequence);
    assert.equal(decisionFiles(context).length, 2);
  } finally {
    context.cleanup();
  }
});

test('a replayed older or equal-sequence release is refused before any image is loaded', () => {
  for (const offset of [1, 0]) {
    const context = completeFixture('stable');
    try {
      // Sequence 42 is already durable; the bundle carries 41 (offset 1) or a different release at 42 (offset 0).
      const store = replayStore({ highWater: { stable: context.identity.sequence + offset } });
      const runner = importRunner(store);
      const { record } = runImport(context, { run: runner.run });
      assert.equal(record.outcome, 'refused');
      assert.match(record.reason, /Offline replay admission refused: replay_rejected/);
      assert.equal(record.replay.admitted, false);
      assert.equal(record.replay.disposition, 'Rejected');
      assert.equal(record.installable, false);
      assert.equal(record.rolloutAuthorization, false);
      assert.equal(runner.loads.length, 0, 'a replay is refused before docker load');
      assert.equal(existsSync(context.staging), false, 'a refused import leaves no staging directory');
      assert.equal(store.highWater.stable, context.identity.sequence + offset, 'the high-water mark never moves back');
    } finally {
      context.cleanup();
    }
  }
});

test('a cross-channel import is refused by the standing policy channel', () => {
  const context = completeFixture('stable');
  try {
    const runner = importRunner(replayStore({ policyChannel: 'insider' }));
    const { record } = runImport(context, { run: runner.run });
    assert.equal(record.outcome, 'refused');
    assert.match(record.reason, /channel_mismatch_policy/);
    assert.equal(record.replay, null);
    assert.equal(runner.loads.length, 0);
    assert.equal(record.installable, false);
  } finally {
    context.cleanup();
  }
});

test('an unreadable or mismatched replay admission fails closed', () => {
  for (const [label, answer, pattern] of [
    ['no output', () => { const error = new Error('spawn failed'); error.status = 3; throw error; },
      /returned no decision \(host update CLI exit 3\)/],
    ['state unreadable', () => { const error = new Error('exit 4'); error.status = 4;
      error.stdout = JSON.stringify({ exitCode: 4, result: { code: 'state_unreadable', details: [] } }); throw error; },
    /refused: state_unreadable/],
    ['wrong digest', () => JSON.stringify({ exitCode: 0, result: { decision: 'admitted', manifestDigest: `sha256:${'0'.repeat(64)}`,
      sequence: 1, channel: 'stable', disposition: 'Imported', correlationId: 'x', reused: false } }),
    /does not match the verified release/],
  ]) {
    const context = completeFixture('stable');
    try {
      const base = importRunner();
      const run = (name, args, options) => (name === hostUpdateCliRunnerName ? answer() : base.run(name, args, options));
      const { record } = runImport(context, { run });
      assert.equal(record.outcome, 'refused', label);
      assert.match(record.reason, pattern, label);
      assert.equal(base.loads.length, 0, label);
      assert.equal(record.installable, false, label);
      assert.equal(existsSync(context.staging), false, label);
    } finally {
      context.cleanup();
    }
  }
});

test('the trust expiry policy refuses stale, future, unbound or unusable trusted roots before verification', () => {
  const at = new Date('2026-09-25T20:00:00Z');
  for (const [label, prepare, pattern] of [
    ['expired approval', context => approveTrustedRoot(context, { approvedAt: '2026-06-26T00:00:00.000Z' }),
      /approval expired \(older than 90 days\)/],
    ['future approval', context => approveTrustedRoot(context, { approvedAt: '2026-09-25T20:11:00.000Z' }),
      /dated in the future/],
    ['non-canonical time', context => approveTrustedRoot(context, { approvedAt: '2026-09-01' }), /canonical UTC timestamp/],
    ['unbound root', context => approveTrustedRoot(context, { trustedRootSha256: '0'.repeat(64) }),
      /does not bind the supplied trusted root/],
    ['extra field', context => approveTrustedRoot(context, { force: true }), /approval record is malformed/],
    ['bad approver', context => approveTrustedRoot(context, { approvedBy: 'alice smith' }), /approval record is malformed/],
    ['missing approval', context => rmSync(context.approval), /approval record is unreadable/],
    ['expired CA', context => {
      const root = JSON.parse(readFileSync(context.trustedRoot, 'utf8'));
      root.certificateAuthorities[0].validFor.end = '2026-09-01T00:00:00Z';
      writeFileSync(context.trustedRoot, JSON.stringify(root));
      approveTrustedRoot(context);
    }, /no certificate authority valid now/],
    ['no tlog', context => {
      const root = JSON.parse(readFileSync(context.trustedRoot, 'utf8'));
      root.tlogs = [];
      writeFileSync(context.trustedRoot, JSON.stringify(root));
      approveTrustedRoot(context);
    }, /no transparency log key valid now/],
    ['not yet valid tlog', context => {
      const root = JSON.parse(readFileSync(context.trustedRoot, 'utf8'));
      root.tlogs[0].publicKey.validFor.start = '2026-12-01T00:00:00Z';
      writeFileSync(context.trustedRoot, JSON.stringify(root));
      approveTrustedRoot(context);
    }, /no transparency log key valid now/],
  ]) {
    const context = completeFixture('stable');
    try {
      prepare(context);
      const runner = importRunner();
      const { record } = runImport(context, { run: runner.run, now: () => at });
      assert.equal(record.outcome, 'refused', label);
      assert.match(record.reason, pattern, label);
      assert.equal(record.trust, null, label);
      assert.equal(record.verifiedDigests, null, `${label}: nothing is verified under an untrusted root`);
      assert.equal(runner.calls.length, 0, `${label}: cosign never runs`);
      assert.equal(runner.store.calls.length, 0, label);
      assert.equal(runner.loads.length, 0, label);
      assert.equal(existsSync(context.staging), false, label);
      assert.ok(!readFileSync(join(context.records, decisionFiles(context)[0]), 'utf8').includes(context.root), label);
    } finally {
      context.cleanup();
    }
  }
});

test('the trust approval is honoured up to its 90-day expiry and not after', () => {
  const context = completeFixture('stable');
  try {
    assert.equal(offlineTrustPolicy.maxApprovalAgeDays, 90);
    const args = { trustedRoot: context.trustedRoot, trustedRootApproval: context.approval };
    assert.equal(evaluateTrustPolicy({ ...args, now: () => new Date('2026-11-29T23:59:59Z') }).approvalExpiresAt,
      '2026-11-30T00:00:00.000Z');
    assert.throws(() => evaluateTrustPolicy({ ...args, now: () => new Date('2026-11-30T00:00:00Z') }), /approval expired/);
    assert.ok(evaluateTrustPolicy({ ...args, now: () => new Date('2026-08-31T23:55:00Z') }), 'bounded clock skew');
    assert.throws(() => evaluateTrustPolicy({ ...args, trustedRootApproval: 'relative.json' }), /absolute trusted-root/);
    assert.equal(trustedRootApprovalKind, 'printfarmer-trusted-root-approval');
  } finally {
    context.cleanup();
  }
});

test('the host-update CLI is invoked with its config, directly or through the dotnet host', () => {
  const abs = process.platform === 'win32' ? 'C:\\pf' : '/pf';
  const cli = join(abs, 'cli', 'Farm.HostUpdate.Cli');
  const config = join(abs, 'host-update.json');
  assert.deepEqual(hostUpdateCliInvocation({ config, 'host-update-cli': cli }), { file: cli, prefix: ['--config', config] });
  assert.deepEqual(hostUpdateCliInvocation({ config, 'host-update-cli': `${cli}.dll` }),
    { file: 'dotnet', prefix: [`${cli}.dll`, '--config', config] });
  const dotnet = join(abs, 'dotnet');
  assert.equal(hostUpdateCliInvocation({ config, 'host-update-cli': `${cli}.dll`, dotnet }).file, dotnet);
  assert.throws(() => hostUpdateCliInvocation({ config, 'host-update-cli': cli, dotnet }), /must not be given/);
  assert.throws(() => hostUpdateCliInvocation({ config: 'c.json', 'host-update-cli': cli }), /--config/);
  assert.throws(() => hostUpdateCliInvocation({ config, 'host-update-cli': 'cli' }), /--host-update-cli/);
  assert.throws(() => hostUpdateCliInvocation({ config, 'host-update-cli': `${cli}.dll`, dotnet: 'dotnet' }), /--dotnet/);
});

test('offline-admit re-verifies against the absolute trusted root with the same Cosign executable', () => {
  const abs = process.platform === 'win32' ? 'C:\\pf' : '/pf';
  const trustedRoot = join(abs, 'trusted_root.json');
  const cosignPath = join(abs, 'bin', 'cosign');
  const verification = { manifestDigest: `sha256:${'a'.repeat(64)}`, release: { sequence: 7, version: '1.2.3' } };
  const seen = [];
  const run = (name, args) => {
    seen.push([name, args]);
    return JSON.stringify({ exitCode: 0, result: { decision: 'admitted', manifestDigest: verification.manifestDigest,
      sequence: 7, channel: 'Insider', releaseId: 'insider:1.2.3', disposition: 'Imported', correlationId: 'c', reused: false } });
  };
  admitOfflineReplay({ run, staging: join(abs, 's'), channel: 'insider', verification, trustedRoot, cosign: cosignPath });
  assert.deepEqual(seen, [[hostUpdateCliRunnerName, ['offline-admit', '--staging', join(abs, 's'), '--channel', 'insider',
    '--trusted-root', trustedRoot, '--cosign', cosignPath, '--json']]]);
  assert.throws(() => admitOfflineReplay({ run, staging: join(abs, 's'), channel: 'insider', verification,
    trustedRoot: 'trusted_root.json' }), /absolute Sigstore trusted root/);
  assert.throws(() => admitOfflineReplay({ run, staging: join(abs, 's'), channel: 'insider', verification, trustedRoot,
    cosign: 'cosign' }), /absolute Cosign path/);
});
// ---------------------------------------------------------------------------------------------
// Issue #3081: complete offline set — deployment templates, config schema and approved tools
// ---------------------------------------------------------------------------------------------
test('the repository tools lock is valid and pins the Cosign version the release workflow installs', () => {
  const lock = validateOfflineToolsLock(readFileSync(join(repositoryRoot, ...offlineToolsLockPath.split('/'))));
  const workflow = readFileSync(join(repositoryRoot, '.github', 'workflows', 'consolidated-release.yml'), 'utf8');
  const pinned = [...workflow.matchAll(/cosign-release: '(v[0-9.]+)'/g)].map(match => match[1]);
  assert.ok(pinned.length > 0);
  for (const artifact of lock.artifacts.filter(entry => entry.tool === 'cosign')) {
    assert.ok(pinned.every(version => version === `v${artifact.version}`), 'lock and workflow pin the same Cosign');
  }
  assert.deepEqual(lock.artifacts.map(artifact => artifact.platform), ['linux/amd64', 'linux/arm64', 'windows/amd64']);
  assert.deepEqual(lock.imageTools.map(entry => `${entry.tool}@${entry.image}`),
    ['pg_dump@postgres', 'pg_restore@postgres', 'sqlcmd@mssql']);
  const bytes = Buffer.from(JSON.stringify(lock));
  const edit = change => { const copy = JSON.parse(bytes.toString()); change(copy); return Buffer.from(JSON.stringify(copy)); };
  assert.throws(() => validateOfflineToolsLock(edit(copy => { copy.artifacts[0].url = 'https://example.com/cosign'; })),
    /not the pinned upstream release asset/);
  assert.throws(() => validateOfflineToolsLock(edit(copy => { copy.artifacts.reverse(); })), /invalid or unsorted/);
  assert.throws(() => validateOfflineToolsLock(edit(copy => { copy.imageTools.pop(); })), /exactly the tools|sqlcmd/);
  assert.throws(() => validateOfflineToolsLock(edit(copy => { copy.imageTools[2].image = 'postgres'; })), /sqlcmd/);
  assert.throws(() => validateOfflineToolsLock(edit(copy => { copy.extra = 1; })), /schema is not supported/);
});

test('the deployment set carries exactly the supported templates and a schema derived from them', () => {
  const identity = { ...releases.stable, sequence: deriveSequence(releases.stable.version) };
  const lock = fakeToolLock(mkdtempSync(join(tmpdir(), 'offline-tools-')));
  const bytes = deploymentSetDocument(identity, { templates: deploymentTemplates, lock });
  assert.deepEqual(deploymentSetDocument({ ...identity }, { templates: new Map(deploymentTemplates), lock }), bytes,
    'generation is deterministic');
  const document = validateDeploymentSet(bytes, identity);
  assert.equal(document.kind, 'printfarmer-offline-deployment-set');
  assert.equal(document.rolloutAuthorization, false);
  assert.deepEqual(document.templates.map(file => file.path), deploymentTemplatePaths);
  assert.ok(!document.templates.some(file => /monitoring|registry|emulator|pgadmin|spoolman|go2rtc|obico|telemetry/i
    .test(file.path)), 'optional add-ons are outside offline support');
  for (const file of document.templates) {
    assert.deepEqual(Buffer.from(file.content, 'base64'), deploymentTemplates.get(file.path));
  }
  assert.ok(document.configSchema.variables.includes('DB_PROVIDER') || document.configSchema.variables.length > 0);
  assert.deepEqual(document.configSchema.variables, [...document.configSchema.variables].sort());
  assert.deepEqual(Object.keys(document.topologies), Object.keys(deploymentTopologies));
  assert.deepEqual(document.hostPrerequisites, ['docker']);
  for (const other of [{ ...identity, buildId: '99' }, { ...identity, channel: 'insider' }]) {
    assert.throws(() => validateDeploymentSet(bytes, other), /not the exact release-bound deployment set/);
  }
  const text = bytes.toString('utf8');
  const forge = replace => Buffer.from(replace(JSON.parse(text)));
  const reencode = value => `${JSON.stringify(value, undefined, 2)}\n`;
  assert.throws(() => validateDeploymentSet(Buffer.from(`${text} `), identity), /not the exact release-bound/);
  assert.throws(() => validateDeploymentSet(forge(value => {
    value.templates[0].content = Buffer.from('tampered').toString('base64'); return reencode(value);
  }), identity), /not the exact release-bound/);
  assert.throws(() => validateDeploymentSet(forge(value => {
    value.configSchema.variables.push('ZZZ_EXTRA'); return reencode(value);
  }), identity), /not the exact release-bound/);
  assert.throws(() => validateDeploymentSet(forge(value => {
    value.templates.pop(); return reencode(value);
  }), identity), /exactly the supported deployment templates/);
  assert.throws(() => validateDeploymentSet(forge(value => {
    value.topologies['split-postgres'].images.push('grafana'); return reencode(value);
  }), identity), /not the exact release-bound/);
  const partial = new Map(deploymentTemplates);
  partial.delete(deploymentTemplatePaths[0]);
  assert.throws(() => deploymentSetDocument(identity, { templates: partial, lock }), /exactly the supported deployment templates/);
  const required = [...Object.keys(components), 'mssql', 'nginx', 'postgres'];
  assert.equal(bindDeploymentSetToImages(document, required, ['mssql', 'nginx', 'postgres']), document);
  assert.throws(() => bindDeploymentSetToImages(document, required.filter(id => id !== 'mssql'), ['nginx', 'postgres']),
    /names an image the release does not select: mssql/);
  assert.throws(() => bindDeploymentSetToImages(document, [...required, 'redis'], ['mssql', 'nginx', 'postgres', 'redis']),
    /do not cover exactly the release-selected image set/);
});

test('a complete set round-trips and every image, template and tool is bound to the signed release', () => {
  const context = completeFixture('insider');
  try {
    const { index } = readBundle(context.bundle);
    assert.equal(index.contents.deploymentSet, true);
    const toolNames = context.toolLock.artifacts.map(artifact => toolMember(artifact.name)).sort();
    assert.deepEqual(index.files.filter(file => file.role === 'deployment-tool').map(file => file.name).sort(), toolNames);
    const { run, calls } = cosign({ requireOffline: true });
    const record = verify(context, { run });
    assert.equal(calls.length, 5, 'manifest, CLI checksums, infrastructure, recovery instructions and deployment set');
    assert.ok(calls.some(call => call.at(-1).endsWith(deploymentSetName)));
    assert.deepEqual(record.deploymentSet, {
      sha256: sha256(readFileSync(join(context.assets, deploymentSetName))), configSchemaVersion: 1,
      topologies: Object.keys(deploymentTopologies),
      tools: context.toolLock.artifacts.map(artifact => ({ name: artifact.name, sha256: artifact.sha256 })) });
    for (const artifact of context.toolLock.artifacts) {
      assert.deepEqual(readFileSync(join(context.staging, toolMember(artifact.name))), readFileSync(join(context.tools, artifact.name)));
    }
  } finally {
    context.cleanup();
  }
});

test('the deployment set is all or nothing at assembly', () => {
  const cases = [
    ['tools without images', context => assemble(context, { tools: context.tools }), /require an image layout too/],
    ['signature missing', context => { rmSync(join(context.assets, deploymentSetSignatureName));
      assemble(context, { images: context.layout, tools: context.tools }); }, /must be present together/],
    ['tool missing', context => { rmSync(join(context.tools, 'cosign-linux-arm64'));
      assemble(context, { images: context.layout, tools: context.tools }); }, /Approved tool cosign-linux-arm64/],
    ['tool tampered', context => { writeFileSync(join(context.tools, 'cosign-linux-amd64'), 'evil');
      assemble(context, { images: context.layout, tools: context.tools }); }, /does not match its signed pin: cosign-linux-amd64/],
    ['unbound identity', context => { withDeploymentSet(context, { boundTo: { ...context.identity, buildId: '99' },
      lock: context.toolLock }); assemble(context, { images: context.layout, tools: context.tools }); },
    /not the exact release-bound deployment set/],
    ['wrong signer', context => { withDeploymentSet(context, { signedBy: 'insider', lock: context.toolLock });
      assemble(context, { images: context.layout, tools: context.tools }); },
    /signature verification failed for offline-deployment-set\.json/],
  ];
  for (const [label, act, pattern] of cases) {
    const context = imageFixture('stable');
    try {
      withDeploymentSet(context);
      assert.throws(() => act(context), pattern, label);
      assert.equal(existsSync(context.bundle), false, label);
    } finally {
      context.cleanup();
    }
  }
  const context = imageFixture('stable');
  try {
    assert.throws(() => assemble(context, { images: context.layout, tools: join(context.root, 'tools') }),
      /publishes no signed deployment set/);
    withDeploymentSet(context);
    const { index } = assemble(context, { images: context.layout });
    assert.equal(index.contents.deploymentSet, false, 'without approved tools the set is omitted, never partial');
    assert.ok(!index.files.some(file => file.role.startsWith('deployment')));
  } finally {
    context.cleanup();
  }
});

test('partial, extra, tampered, re-signed or unbound deployment members are rejected before import', () => {
  const context = completeFixture('stable');
  try {
    const original = readFileSync(context.bundle);
    const restore = () => writeFileSync(context.bundle, original);
    const forge = (edit, pattern) => { rewriteBundle(context, edit); rejectsWithoutStaging(context, pattern); restore(); };
    forge(({ members }) => { members.delete(toolMember('cosign-linux-arm64')); },
      /does not carry exactly the approved tools/);
    forge(({ members, roles }) => { members.set(toolMember('extra-tool'), Buffer.from('x')); roles.set(toolMember('extra-tool'),
      'deployment-tool'); }, /does not carry exactly the approved tools/);
    forge(({ members }) => { members.set(toolMember('cosign-linux-amd64'), Buffer.from('evil')); },
      /approved tool does not match its signed pin: cosign-linux-amd64/);
    forge(({ members }) => { members.set(deploymentSetName, Buffer.concat([members.get(deploymentSetName), Buffer.from(' ')])); },
      /signature verification failed for offline-deployment-set\.json/);
    forge(({ members }) => {
      const bytes = deploymentSetDocument({ ...context.identity, buildId: '99' }, { templates: deploymentTemplates,
        lock: context.toolLock });
      members.set(deploymentSetName, bytes);
      members.set(deploymentSetSignatureName, Buffer.from(sign(bytes, 'stable')));
    }, /not the exact release-bound deployment set/);
    forge(({ members }) => {
      const document = JSON.parse(members.get(deploymentSetName).toString('utf8'));
      document.tools[0].sha256 = sha256(Buffer.from('evil'));
      const bytes = Buffer.from(`${JSON.stringify(document, undefined, 2)}\n`);
      members.set(deploymentSetName, bytes);
      members.set(deploymentSetSignatureName, Buffer.from(sign(bytes, 'insider')));
    }, /signature verification failed for offline-deployment-set\.json/);
    forge(({ members }) => { members.delete(deploymentSetSignatureName); }, /missing required member: offline-deployment-set\.sigstore/);
    forge(({ index }) => { index.contents.deploymentSet = false; }, /do not match its deployment set flag/);
    forge(({ index, members }) => {
      for (const name of [...members.keys()]) if (/^image-|^infrastructure-/.test(name)) members.delete(name);
      index.contents.images = false; index.contents.infrastructure = false;
    }, /contents claim material this format does not carry/);
    assert.ok(verify(context).deploymentSet);
  } finally {
    context.cleanup();
  }
});

test('pre-#3081 bundles without a deploymentSet claim still verify as carrying no deployment set', () => {
  const legacy = imageFixture('stable');
  try {
    withInstructions(legacy);
    assemble(legacy, { images: legacy.layout });
    rewriteBundle(legacy, ({ index }) => { delete index.contents.deploymentSet; });
    const record = verify(legacy);
    assert.equal(record.deploymentSet, false);
  } finally {
    legacy.cleanup();
  }
  const context = completeFixture('stable');
  try {
    const original = readFileSync(context.bundle);
    rewriteBundle(context, ({ index }) => { delete index.contents.deploymentSet; });
    rejectsWithoutStaging(context, /do not match its deployment set flag/);
    writeFileSync(context.bundle, original);
    rewriteBundle(context, ({ index }) => { index.contents.unknownClaim = false; });
    rejectsWithoutStaging(context, /contents claim material this format does not carry/);
    writeFileSync(context.bundle, original);
    rewriteBundle(context, ({ index, members }) => {
      delete index.contents.deploymentSet;
      members.delete(toolMember('cosign-linux-arm64'));
    });
    rejectsWithoutStaging(context, /deployment set flag|approved tools/);
  } finally {
    context.cleanup();
  }
});

// ---------------------------------------------------------------------------------------------
// Issue #3094: prior recovery images, bound to the authenticated prior recovery set.
// ---------------------------------------------------------------------------------------------
const priorImageMembers = Object.keys(components).map(id => `prior-image-${id}.oci.tar`).sort();

function priorImageFixture(channel = 'stable') {
  const context = priorFixture(channel);
  const layout = join(context.root, 'prior-layout');
  const writeBlob = layoutWriter(layout);
  const application = Object.fromEntries(Object.entries(components).map(([id, component]) =>
    [id, indexImage(writeBlob, `prior-${id}`, component.platforms)]));
  const manifest = Buffer.from(buildManifest(context.prior, Object.fromEntries(Object.entries(application).map(([id, image]) =>
    [id, { indexDigest: image.digest, platforms: components[id].platforms, platformDigests: image.platforms }]))));
  writeFileSync(join(context.priorAssets, 'update-manifest.json'), manifest);
  writeFileSync(join(context.priorAssets, 'update-manifest.sigstore.json'), sign(manifest, channel));
  return Object.assign(context, { priorLayout: layout, priorApplication: application });
}

function loadPrior(context, docker, overrides = {}) {
  const signatures = cosign({ requireOffline: true });
  const run = (name, args, options) => (name === 'cosign' ? signatures.run(name, args) : docker(name, args, options));
  return loadPriorImages({ staging: context.staging, channel: context.release.channel,
    trustedRoot: context.trustedRoot, run, ...overrides });
}

const recordingDocker = loads => (name, args, options) => {
  assert.equal(name, 'docker', 'prior load never invokes anything but the local engine');
  assert.deepEqual(args, ['load'], 'prior load never pulls, builds or tags');
  assert.equal(typeof options?.stdin, 'number', 'the verified descriptor itself is handed to docker load');
  loads.push(fstatSync(options.stdin).size);
  return '';
};

for (const [channel, mode] of [['stable', 'packaged'], ['insider', 'local-reference']]) {
  test(`${channel} ${mode} prior images are packaged, verified by digest and platform and loadable offline`, () => {
    const context = priorImageFixture(channel);
    try {
      const { index } = assemble(context, context.withPrior({ priorImages: context.priorLayout, priorMode: mode }));
      assert.equal(index.contents.priorImages, true);
      assert.equal(index.contents.images, false, 'prior images do not claim the target image set');
      assert.deepEqual(index.files.filter(file => file.role === 'prior-application-image').map(file => file.name),
        priorImageMembers);
      const record = verify(context, mode === 'local-reference' ? { priorRecoverySet: context.priorAssets } : {});
      assert.equal(record.installable, false);
      assert.deepEqual(record.priorImages.map(image => image.member).sort(), priorImageMembers);
      for (const image of record.priorImages) {
        assert.equal(image.reference.endsWith(`:${context.prior.version}`), true, 'prior images carry the prior version');
        assert.equal(image.digest, context.priorApplication[image.id].digest);
        assert.deepEqual(image.platforms, context.priorApplication[image.id].platforms);
        assert.equal(image.sha256, sha256(readFileSync(join(context.staging, image.member))));
      }
      const loads = [];
      const { loaded } = loadPrior(context, recordingDocker(loads));
      assert.equal(loads.length, priorImageMembers.length);
      assert.deepEqual(loaded.map(image => image.member).sort(), priorImageMembers);
    } finally {
      context.cleanup();
    }
  });
}

test('a bundle without prior images records priorImages false and prior load refuses it', () => {
  const context = priorImageFixture('stable');
  try {
    const { index } = assemble(context, context.withPrior());
    assert.equal(index.contents.priorImages, false);
    const record = verify(context);
    assert.equal(record.priorImages, false);
    assert.throws(() => loadPrior(context, () => assert.fail('nothing may be loaded')),
      /holds no verified prior recovery image set/);
    assert.deepEqual(loadPrior(context, () => assert.fail('nothing may be loaded'), { missing: 'skip' }),
      { loaded: [], skipped: 'no_packaged_prior_images' }, 'the recovery wrapper falls back to the engine cache');
    assert.throws(() => loadPrior(context, () => assert.fail('nothing may be loaded'), { missing: 'ignore' }),
      /--missing must be refuse or skip/);
  } finally {
    context.cleanup();
  }
});

test('skipping absent prior images never skips a claimed prior image set', () => {
  const context = priorImageFixture('stable');
  try {
    assemble(context, context.withPrior({ priorImages: context.priorLayout }));
    verify(context);
    const recordPath = join(context.staging, 'offline-bundle-verification.json');
    const record = JSON.parse(readFileSync(recordPath, 'utf8'));
    writeFileSync(recordPath, JSON.stringify({ ...record, priorImages: [] }));
    assert.throws(() => loadPrior(context, () => assert.fail('nothing may be loaded'), { missing: 'skip' }),
      /holds no verified prior recovery image set/);
    writeFileSync(recordPath, JSON.stringify(record));
    const loads = [];
    assert.equal(loadPrior(context, recordingDocker(loads), { missing: 'skip' }).loaded.length, priorImageMembers.length);
  } finally {
    context.cleanup();
  }
});

test('prior images require the verified prior set and fail closed at assembly', () => {
  const context = priorImageFixture('stable');
  try {
    assert.throws(() => assemble(context, { priorImages: context.priorLayout }),
      /bound to the verified prior recovery set/);
    // A prior manifest service whose image is not in the layout.
    rmSync(join(context.priorLayout, 'blobs', 'sha256', context.priorApplication.api.digest.slice('sha256:'.length)));
    assert.throws(() => assemble(context, context.withPrior({ priorImages: context.priorLayout })));
    assert.equal(existsSync(context.bundle), false, 'a failed assembly leaves no bundle');
    assert.equal(existsSync(`${context.bundle}.partial`), false);
  } finally {
    context.cleanup();
  }
});

test('a prior image for the wrong platform is refused at assembly', () => {
  const context = priorImageFixture('stable');
  try {
    // The signed prior manifest pins linux/amd64 for the worker, but the only child is arm64.
    const writeBlob = layoutWriter(context.priorLayout);
    const wrong = indexImage(writeBlob, 'prior-wrong', ['linux/arm64']);
    const application = { ...context.priorApplication,
      'orcaslicer-worker': { ...wrong, platforms: { 'linux/amd64': wrong.platforms['linux/arm64'] } } };
    const manifest = Buffer.from(buildManifest(context.prior, Object.fromEntries(Object.entries(application).map(([id, image]) =>
      [id, { indexDigest: image.digest, platforms: components[id].platforms, platformDigests: image.platforms }]))));
    writeFileSync(join(context.priorAssets, 'update-manifest.json'), manifest);
    writeFileSync(join(context.priorAssets, 'update-manifest.sigstore.json'), sign(manifest, 'stable'));
    assert.throws(() => assemble(context, context.withPrior({ priorImages: context.priorLayout })));
    assert.equal(existsSync(context.bundle), false);
  } finally {
    context.cleanup();
  }
});

test('missing, tampered, mixed, extra or unflagged prior images are rejected before import', () => {
  const cases = [
    [({ members, roles }) => { members.delete('prior-image-api.oci.tar'); roles.delete('prior-image-api.oci.tar'); },
      /does not carry exactly the signed prior recovery image set/],
    [({ members }) => editNested(members, 'prior-image-api.oci.tar', entries => entries.map(entry =>
      (entry.data.includes('layer prior-api') ? { ...entry, data: Buffer.from(entry.data.toString().replace('z', 'y')) } : entry))),
    /prior image prior-image-api\.oci\.tar failed verification/],
    [({ members }) => members.set('prior-image-api.oci.tar', members.get('prior-image-frontend.oci.tar')),
      /prior image prior-image-api\.oci\.tar failed verification/],
    [({ members, roles }) => { members.set('prior-image-api.oci.tar', members.get('prior-image-api.oci.tar'));
      members.set('prior-image-bogus.oci.tar', Buffer.from('x')); roles.set('prior-image-bogus.oci.tar', 'prior-application-image'); },
    /not part of this release/],
    [({ index }) => { index.contents.priorImages = false; }, /prior image members do not match its prior images flag/],
    [({ index }) => { index.contents.priorImages = 'yes'; }, /contents claim material/],
  ];
  for (const [edit, pattern] of cases) {
    const context = priorImageFixture('stable');
    try {
      assemble(context, context.withPrior({ priorImages: context.priorLayout }));
      rewriteBundle(context, edit);
      rejectsWithoutStaging(context, pattern);
    } finally {
      context.cleanup();
    }
  }
});

test('prior images cannot be claimed without a prior recovery set, and legacy indexes default to none', () => {
  const context = priorImageFixture('stable');
  try {
    assemble(context);
    rewriteBundle(context, ({ index }) => { index.contents.priorImages = true; });
    rejectsWithoutStaging(context, /contents claim material/);
  } finally {
    context.cleanup();
  }
  const legacy = priorImageFixture('stable');
  try {
    assemble(legacy, legacy.withPrior());
    rewriteBundle(legacy, ({ index }) => { delete index.contents.priorImages; });
    assert.equal(verify(legacy).priorImages, false, 'a pre-#3094 index carries no prior images');
  } finally {
    legacy.cleanup();
  }
});

test('prior load re-authenticates the staged manifests and loads only unchanged, exactly recorded images', () => {
  const tamper = [
    [context => writeFileSync(join(context.staging, 'prior-image-api.oci.tar'), Buffer.from('swapped')),
      /changed after verification/],
    [context => {
      const path = join(context.staging, offlineBundleVerificationName);
      const record = JSON.parse(readFileSync(path, 'utf8'));
      record.priorImages = record.priorImages.slice(1);
      writeFileSync(path, JSON.stringify(record));
    }, /does not name exactly the signed prior recovery image set/],
    [context => {
      const path = join(context.staging, 'prior-update-manifest.json');
      writeFileSync(path, Buffer.concat([readFileSync(path), Buffer.from(' ')]));
    }, /does not match the staged signed prior manifest|signature verification failed/],
    [context => writeFileSync(join(context.staging, 'prior-update-manifest.sigstore.json'), sign(Buffer.from('other'), 'stable')),
      /signature verification failed for prior-update-manifest\.json/],
    [context => {
      const path = join(context.staging, offlineBundleVerificationName);
      const record = JSON.parse(readFileSync(path, 'utf8'));
      record.priorRecoverySet.manifestDigest = `sha256:${'0'.repeat(64)}`;
      writeFileSync(path, JSON.stringify(record));
    }, /does not match the staged signed prior manifest/],
  ];
  for (const [edit, pattern] of tamper) {
    const context = priorImageFixture('stable');
    try {
      assemble(context, context.withPrior({ priorImages: context.priorLayout }));
      verify(context);
      edit(context);
      assert.throws(() => loadPrior(context, () => assert.fail('nothing may be loaded after a failed check')), pattern);
    } finally {
      context.cleanup();
    }
  }
});

test('the command line exposes prior images and prior load without bypasses', () => {
  assert.equal(parseArguments(['assemble', '--release-assets', 'a', '--channel', 'stable', '--output', 'o',
    '--prior-images', 'p']).options['prior-images'], 'p');
  assert.deepEqual(parseArguments(['load-prior', '--staging', 's', '--channel', 'stable', '--trusted-root', 't']).options,
    { staging: 's', channel: 'stable', 'trusted-root': 't' });
  assert.equal(parseArguments(['load-prior', '--staging', 's', '--missing', 'skip']).options.missing, 'skip');
  for (const argv of [['load-prior', '--staging', 's', '--skip-verify', 'x'], ['load-prior', '--staging', 's', '--force', 'x'],
    ['load-prior', '--prior-images', 'p'], ['verify', '--prior-images', 'p']]) {
    assert.throws(() => parseArguments(argv), /usage/, argv.join(' '));
  }
});