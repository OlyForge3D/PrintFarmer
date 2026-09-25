import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { components } from '../release-policy.mjs';
import { buildManifest } from '../release-manifest.mjs';
import { formatSums, hostUpdateCliArchiveName, hostUpdateCliRuntimes, hostUpdateCliSumsBundleName,
  hostUpdateCliSumsName } from '../host-update-cli-package.mjs';
import { assembleOfflineBundle, offlineBundleIndexName, offlineBundleLimits, offlineBundleName,
  offlineBundleVerificationName, parseArguments, releaseSigningIdentity, tarHeader,
  verifyOfflineBundle } from '../offline-update-bundle.mjs';

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
    return { name, sha256: sha256(bytes) };
  });
  const sums = Buffer.from(formatSums(archives));
  writeFileSync(join(assets, hostUpdateCliSumsName(release.version)), sums);
  writeFileSync(join(assets, hostUpdateCliSumsBundleName(release.version)), sign(sums, channel));
  const trustedRoot = join(root, 'trusted_root.json');
  writeFileSync(trustedRoot, '{"mediaType":"application/vnd.dev.sigstore.trustedroot+json;version=0.1"}\n');
  return { root, assets, release, trustedRoot, bundle: join(root, offlineBundleName(release.version)),
    staging: join(root, 'staging'), cleanup: () => rmSync(root, { force: true, recursive: true }) };
}

function assemble(context, overrides = {}) {
  return assembleOfflineBundle({ releaseAssets: context.assets, channel: context.release.channel,
    output: context.bundle, run: cosign({ requireOffline: false }).run, ...overrides });
}

function verify(context, overrides = {}) {
  return verifyOfflineBundle({ bundle: context.bundle, channel: context.release.channel,
    trustedRoot: context.trustedRoot, staging: context.staging, run: cosign({ requireOffline: true }).run,
    now: () => new Date('2026-09-25T20:00:00Z'), ...overrides });
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
      release: {}, manifestDigest: '', contents: { cliRuntimes: [], images: false, infrastructure: false,
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
