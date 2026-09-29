import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { components } from '../release-policy.mjs';
import { buildManifest } from '../release-manifest.mjs';
import { formatSums, hostUpdateCliArchiveName, hostUpdateCliRuntimes, hostUpdateCliSbomName, hostUpdateCliSumsBundleName,
  hostUpdateCliSumsName } from '../host-update-cli-package.mjs';
import { assembleOfflineBundle, offlineBundleName, offlineBundleSignatureName, releaseSigningIdentity }
  from '../offline-update-bundle.mjs';
import { networkDenialMechanism, validatePublishedBundleVerification, verificationKind }
  from '../recovery-matrix/evidence.mjs';
import { buildVerificationRecord, parseArguments, publishedBundleAssetNames, readNetworkAttempts,
  selectPublishedInsiderRelease, targetIdentity, verifyPublishedBundle } from '../recovery-matrix/published-bundle.mjs';

const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const insider = { version: '1.5.0-insider.3', tag: 'v1.5.0-insider.3', channel: 'insider', sourceBranch: 'development',
  sourceCommit: 'b'.repeat(40), buildId: '42' };
const imageDetails = Object.fromEntries(Object.entries(components).map(([name, component], index) => [name, {
  indexDigest: `sha256:${String(index).repeat(64)}`,
  platforms: component.platforms,
  platformDigests: Object.fromEntries(component.platforms.map(platform => [platform, `sha256:${'d'.repeat(64)}`])),
}]));

// Simulated keyless signature bound to exact bytes and a workflow identity, as in the bundle tests.
const sign = (bytes, identity = releaseSigningIdentity('insider')) =>
  `${JSON.stringify({ sha256: sha256(bytes), identity })}\n`;

function fakeCosign({ requireTrustedRoot = true } = {}) {
  const calls = [];
  const run = (name, args) => {
    calls.push([name, ...args]);
    assert.equal(name, 'cosign');
    assert.equal(args[0], 'verify-blob');
    if (requireTrustedRoot) assert.ok(args.includes('--trusted-root'), 'published verification must stay offline');
    const bundle = JSON.parse(readFileSync(args[args.indexOf('--bundle') + 1], 'utf8'));
    if (bundle.sha256 !== sha256(readFileSync(args.at(-1)))) throw new Error('signature does not match the blob');
    if (bundle.identity !== args[args.indexOf('--certificate-identity') + 1]) throw new Error('identity mismatch');
    return '';
  };
  return { run, calls };
}

function publishedFixture() {
  const root = mkdtempSync(join(tmpdir(), 'published-bundle-'));
  const assets = join(root, 'assets');
  mkdirSync(assets);
  const manifest = Buffer.from(buildManifest(insider, imageDetails));
  writeFileSync(join(assets, 'update-manifest.json'), manifest);
  writeFileSync(join(assets, 'update-manifest.sigstore.json'), sign(manifest));
  const archives = hostUpdateCliRuntimes.flatMap(rid => {
    const name = hostUpdateCliArchiveName(insider.version, rid);
    const bytes = Buffer.from(`archive ${rid}`);
    writeFileSync(join(assets, name), bytes);
    const sbomName = hostUpdateCliSbomName(insider.version, rid);
    const sbom = Buffer.from(`${JSON.stringify({ spdxVersion: 'SPDX-2.3', SPDXID: 'SPDXRef-DOCUMENT', name: `cli-${rid}`,
      packages: [{ SPDXID: 'SPDXRef-Package-cli', name: 'printfarmer-host-update-cli' }] })}\n`);
    writeFileSync(join(assets, sbomName), sbom);
    return [{ name, sha256: sha256(bytes) }, { name: sbomName, sha256: sha256(sbom) }];
  });
  const sums = Buffer.from(formatSums(archives));
  writeFileSync(join(assets, hostUpdateCliSumsName(insider.version)), sums);
  writeFileSync(join(assets, hostUpdateCliSumsBundleName(insider.version)), sign(sums));
  const bundle = join(root, offlineBundleName(insider.version));
  assembleOfflineBundle({ releaseAssets: assets, channel: 'insider', output: bundle,
    run: fakeCosign({ requireTrustedRoot: false }).run });
  const signature = join(root, offlineBundleSignatureName(insider.version));
  writeFileSync(signature, sign(readFileSync(bundle)));
  const trustedRoot = join(root, 'trusted_root.json');
  writeFileSync(trustedRoot, '{}\n');
  return { root, bundle, signature, trustedRoot, staging: join(root, 'staging'),
    cleanup: () => rmSync(root, { force: true, recursive: true }) };
}

const asset = (name, sha = 'a'.repeat(64)) => ({ name, size: 10, digest: `sha256:${sha}` });
const release = (tag, { draft = false, assets } = {}) => {
  const version = tag.slice(1);
  const names = /-insider\./.test(tag) ? publishedBundleAssetNames(version) : undefined;
  return { tag_name: tag, draft, assets: assets ?? (names ? [asset(names.bundle), asset(names.signature, 'b'.repeat(64))] : []) };
};

test('selects the newest published insider release and its signed bundle assets', () => {
  const selected = selectPublishedInsiderRelease([
    release('v1.5.0-insider.2'), release('v1.5.0-insider.10'), release('v1.5.0'), release('v1.6.0-insider.1', { draft: true }),
    release('v1.5.0-beta.4'),
  ]);
  assert.equal(selected.tag, 'v1.5.0-insider.10');
  assert.equal(selected.version, '1.5.0-insider.10');
  assert.deepEqual(selected.bundle, { name: 'printfarmer-offline-bundle-v1.5.0-insider.10.tar', sha256: 'a'.repeat(64), size: 10 });
  assert.equal(selected.signature.name, 'printfarmer-offline-bundle-v1.5.0-insider.10.tar.sigstore.json');
  assert.equal(selectPublishedInsiderRelease([release('v1.5.0-insider.2'), release('v1.5.0-insider.10')],
    { tag: 'v1.5.0-insider.2' }).tag, 'v1.5.0-insider.2');
});

test('never falls back to an older release when the newest insider lacks its signed bundle', () => {
  const releases = [release('v1.5.0-insider.2'), release('v1.5.0-insider.3', { assets: [] })];
  assert.throws(() => selectPublishedInsiderRelease(releases), /published_bundle_missing: v1\.5\.0-insider\.3/);
  const unsigned = release('v1.5.0-insider.4');
  unsigned.assets = unsigned.assets.slice(0, 1);
  assert.throws(() => selectPublishedInsiderRelease([unsigned]), /does not publish .*\.tar\.sigstore\.json/);
  assert.throws(() => selectPublishedInsiderRelease([release('v1.5.0')]), /no published insider release/);
  assert.throws(() => selectPublishedInsiderRelease([release('v1.5.0-insider.2')], { tag: 'v1.5.0' }), /insider release tag/);
  assert.throws(() => selectPublishedInsiderRelease([release('v1.5.0-insider.2')], { tag: 'v1.5.0-insider.9' }),
    /published_bundle_missing/);
  const undigested = release('v1.5.0-insider.5');
  delete undigested.assets[0].digest;
  assert.throws(() => selectPublishedInsiderRelease([undigested]), /published_bundle_digest_missing/);
});

test('refuses any fallback, build or reset flag', () => {
  for (const flag of ['--allow-download', '--build', '--reset', '--force', '--fixture-root']) {
    assert.throws(() => parseArguments(['select', '--releases', 'r.json', flag, 'x']), /Unsupported option/);
  }
  assert.throws(() => parseArguments(['build']), /Usage/);
  assert.throws(() => parseArguments(['verify', '--bundle', 'b']), /Missing --signature/);
  assert.deepEqual(parseArguments(['select', '--releases', 'r.json', '--tag', 'v1.5.0-insider.2']).options,
    { releases: 'r.json', tag: 'v1.5.0-insider.2' });
});

test('verifies the published archive signature before the offline verifier, read-only', () => {
  const context = publishedFixture();
  try {
    const before = sha256(readFileSync(context.bundle));
    const { run, calls } = fakeCosign();
    const result = verifyPublishedBundle({ ...context, version: insider.version, run });
    assert.equal(result.signatureVerified, true);
    assert.equal(result.release.tag, insider.tag);
    assert.equal(calls[0].at(-1), context.bundle, 'the published archive itself is authenticated first');
    assert.equal(calls[0][calls[0].indexOf('--certificate-identity') + 1], releaseSigningIdentity('insider'));
    assert.equal(sha256(readFileSync(context.bundle)), before);
    assert.deepEqual(targetIdentity(result.release), { tag: insider.tag, version: insider.version, channel: 'insider',
      sourceCommit: insider.sourceCommit, buildId: insider.buildId, sequence: result.release.sequence });
  } finally {
    context.cleanup();
  }
});

test('a forged or foreign-signed published archive fails before anything is staged', () => {
  const context = publishedFixture();
  try {
    writeFileSync(context.signature, sign(readFileSync(context.bundle), releaseSigningIdentity('stable')));
    assert.throws(() => verifyPublishedBundle({ ...context, version: insider.version, run: fakeCosign().run }),
      /Published bundle signature verification failed/);
    assert.equal(existsSync(context.staging), false);
    assert.throws(() => verifyPublishedBundle({ ...context, version: '1.5.0', run: fakeCosign().run }), /insider/);
  } finally {
    context.cleanup();
  }
});

const verification = { signatureVerified: true, release: { ...insider, sequence: 7 } };
const base = {
  runId: 'published-bundle-1', startedAt: '2026-09-28T10:00:00.000Z', finishedAt: '2026-09-28T10:05:00.000Z',
  harnessCommit: 'c'.repeat(40),
  host: { distribution: 'ubuntu', distributionVersion: '24.04', arch: 'x64', kernel: '6.8.0' },
  tools: { cli: 'not-installed', docker: '28.0.0', compose: '2.30.0', cosign: 'v3.0.6', node: '24.0.0', shell: 'bash' },
  verification, bundleSha256: 'd'.repeat(64), bundleSha256After: 'd'.repeat(64), hostBefore: 'x', hostAfter: 'x',
  attempts: [{ at: '2026-09-28T10:01:00Z', destination: 'canary.printfarmer.invalid', protocol: 'dns',
    query: 'canary.printfarmer.invalid' }],
};

test('emits a valid read-only published-bundle verification record', () => {
  const record = buildVerificationRecord(base);
  assert.equal(record.kind, verificationKind);
  assert.equal(record.verdict, 'pass');
  assert.equal(record.identities.signingRoot, 'published-insider');
  assert.equal(record.identities.target.sourceBranch, undefined);
  assert.equal(record.networkDenial.mechanism, networkDenialMechanism);
  assert.deepEqual(record.networkDenial.attempts, [], 'the canary proof is not an egress attempt');
  assert.deepEqual(record.verification, { signatureVerified: true, imported: false, activated: false, hostModified: false });
  assert.deepEqual(validatePublishedBundleVerification(record), []);
});

test('fails on egress, a modified bundle, a changed host or an unverified signature', () => {
  const egress = buildVerificationRecord({ ...base,
    attempts: [...base.attempts, { at: '2026-09-28T10:02:00Z', destination: 'rekor.sigstore.dev', protocol: 'dns' }] });
  assert.equal(egress.verdict, 'fail');
  assert.equal(egress.networkDenial.attempts.length, 1);
  assert.throws(() => buildVerificationRecord({ ...base, bundleSha256After: 'e'.repeat(64) }), /hostModified/);
  assert.throws(() => buildVerificationRecord({ ...base, hostAfter: 'y' }), /hostModified/);
  assert.equal(buildVerificationRecord({ ...base, verification: { ...verification, signatureVerified: false } }).verdict,
    'fail');
  assert.throws(() => buildVerificationRecord({ ...base,
    verification: { signatureVerified: true, release: { ...insider, channel: 'stable', sequence: 7 } } }), /insider/);
});

test('reads egress-sink NDJSON attempts', () => {
  const root = mkdtempSync(join(tmpdir(), 'published-attempts-'));
  try {
    const path = join(root, 'attempts.ndjson');
    writeFileSync(path, `${JSON.stringify({ at: 't', destination: 'd', protocol: 'dns', query: 'd', extra: 1 })}\n\n`);
    assert.deepEqual(readNetworkAttempts(path), [{ at: 't', destination: 'd', protocol: 'dns', query: 'd' }]);
  } finally {
    rmSync(root, { force: true, recursive: true });
  }
});
