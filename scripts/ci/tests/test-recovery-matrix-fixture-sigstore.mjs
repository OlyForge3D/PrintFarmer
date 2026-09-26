import assert from 'node:assert/strict';
import { Buffer } from 'node:buffer';
import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import crypto from 'node:crypto';

import {
  createFixtureSigstoreRoot,
  fixtureReleaseIdentity,
} from '../recovery-matrix/fixture-sigstore.mjs';

const blob = Buffer.from('PrintFarmer recovery matrix fixture blob\n');
const fixedNow = new Date('2026-09-26T18:41:50.000Z');
const issuer = 'https://token.actions.githubusercontent.com';

test('release identities match the release workflow channels', () => {
  assert.equal(
    fixtureReleaseIdentity('stable'),
    'https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/main',
  );
  assert.equal(
    fixtureReleaseIdentity('insider'),
    'https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/development',
  );
  assert.throws(() => fixtureReleaseIdentity('beta'), /stable or insider/);
});

test('trusted root and bundle expose fixture-only Sigstore material', () => {
  const root = createFixtureSigstoreRoot({ now: fixedNow });
  const bundle = root.signBlob(blob, { identity: fixtureReleaseIdentity('insider') });
  const expectedFingerprint = crypto
    .createHash('sha256')
    .update(canonicalJson(root.trustedRoot))
    .digest('hex');

  assert.equal(root.trustedRoot.mediaType, 'application/vnd.dev.sigstore.trustedroot+json;version=0.1');
  assert.equal(root.fingerprint, expectedFingerprint);
  assert.match(root.fingerprint, /^[0-9a-f]{64}$/);
  assert.equal(root.trustedRoot.certificateAuthorities[0].subject.organization, 'printfarmer-recovery-matrix-fixture');
  assert.equal(root.trustedRoot.tlogs[0].baseUrl, 'https://fixture.invalid/rekor');
  assert.equal(root.trustedRoot.ctlogs[0].baseUrl, 'https://fixture.invalid/ct');
  assert.equal(bundle.mediaType, 'application/vnd.dev.sigstore.bundle.v0.3+json');
  assert.equal(bundle.messageSignature.messageDigest.algorithm, 'SHA2_256');
  assert.equal(bundle.verificationMaterial.tlogEntries[0].kindVersion.kind, 'hashedrekord');
  assert.equal(bundle.verificationMaterial.tlogEntries[0].kindVersion.version, '0.0.1');
  assert.equal(bundle.verificationMaterial.tlogEntries[0].inclusionProof.treeSize, '1');
  assert.equal(bundle.verificationMaterial.tlogEntries[0].inclusionProof.logIndex, '0');
  assert.equal(bundle.verificationMaterial.tlogEntries[0].inclusionProof.hashes.length, 0);
  assert.match(bundle.verificationMaterial.tlogEntries[0].inclusionProof.checkpoint.envelope, /^fixture\.invalid\n1\n/);
  assert.ok(bundle.verificationMaterial.tlogEntries[0].inclusionPromise.signedEntryTimestamp);
  assert.ok(bundle.verificationMaterial.certificate.rawBytes);

  root.dispose();
  assert.throws(
    () => root.signBlob(blob, { identity: fixtureReleaseIdentity('insider') }),
    /disposed/,
  );
});

test('independent fixture roots produce distinct trust material', () => {
  const first = createFixtureSigstoreRoot({ now: fixedNow });
  const second = createFixtureSigstoreRoot({ now: fixedNow });
  assert.notEqual(
    first.trustedRoot.tlogs[0].logId.keyId,
    second.trustedRoot.tlogs[0].logId.keyId,
  );
  assert.notEqual(
    first.trustedRoot.certificateAuthorities[0].certChain.certificates[0].rawBytes,
    second.trustedRoot.certificateAuthorities[0].certChain.certificates[0].rawBytes,
  );
  first.dispose();
  second.dispose();
});

test('real cosign verifies fixture bundles offline when PF_COSIGN is set', { skip: !process.env.PF_COSIGN }, () => {
  const cosign = process.env.PF_COSIGN;
  const scratch = path.join(
    os.homedir(),
    '.cache',
    'pf-fixture-sigstore-tests',
    `${process.pid}-${Date.now()}-${Math.random().toString(16).slice(2)}`,
  );
  fs.mkdirSync(scratch, { recursive: true });
  try {
    const first = createFixtureSigstoreRoot({ now: new Date() });
    const second = createFixtureSigstoreRoot({ now: new Date() });
    first.writeTrustedRoot(path.join(scratch, 'trusted-root.json'));
    writeJson(path.join(scratch, 'public-root.json'), second.trustedRoot);
    fs.writeFileSync(path.join(scratch, 'blob.bin'), blob);
    fs.writeFileSync(path.join(scratch, 'modified.bin'), Buffer.concat([blob, Buffer.from('changed\n')]));

    const insiderBundle = first.signBlob(blob, { identity: fixtureReleaseIdentity('insider') });
    const stableBundle = first.signBlob(blob, { identity: fixtureReleaseIdentity('stable') });
    const otherRootBundle = second.signBlob(blob, { identity: fixtureReleaseIdentity('insider') });
    writeJson(path.join(scratch, 'bundle-insider.json'), insiderBundle);
    writeJson(path.join(scratch, 'bundle-stable.json'), stableBundle);
    writeJson(path.join(scratch, 'bundle-other-root.json'), otherRootBundle);

    const verifyInsider = exactVerifyArgs({
      trustedRoot: path.join(scratch, 'trusted-root.json'),
      bundle: path.join(scratch, 'bundle-insider.json'),
      identity: fixtureReleaseIdentity('insider'),
      blobPath: path.join(scratch, 'blob.bin'),
    });
    assertCosignSucceeds(cosign, verifyInsider);
    assertCosignFails(cosign, exactVerifyArgs({
      trustedRoot: path.join(scratch, 'trusted-root.json'),
      bundle: path.join(scratch, 'bundle-insider.json'),
      identity: fixtureReleaseIdentity('insider'),
      blobPath: path.join(scratch, 'modified.bin'),
    }));
    assertCosignFails(cosign, exactVerifyArgs({
      trustedRoot: path.join(scratch, 'trusted-root.json'),
      bundle: path.join(scratch, 'bundle-insider.json'),
      identity: fixtureReleaseIdentity('stable'),
      blobPath: path.join(scratch, 'blob.bin'),
    }));
    assertCosignFails(cosign, exactVerifyArgs({
      trustedRoot: path.join(scratch, 'trusted-root.json'),
      bundle: path.join(scratch, 'bundle-other-root.json'),
      identity: fixtureReleaseIdentity('insider'),
      blobPath: path.join(scratch, 'blob.bin'),
    }));
    assertCosignFails(cosign, [
      'verify-blob',
      '--bundle', path.join(scratch, 'bundle-insider.json'),
      '--certificate-oidc-issuer', issuer,
      '--certificate-identity', fixtureReleaseIdentity('insider'),
      path.join(scratch, 'blob.bin'),
    ]);
    assertCosignSucceeds(cosign, exactVerifyArgs({
      trustedRoot: path.join(scratch, 'trusted-root.json'),
      bundle: path.join(scratch, 'bundle-stable.json'),
      identity: fixtureReleaseIdentity('stable'),
      blobPath: path.join(scratch, 'blob.bin'),
    }));

    first.dispose();
    second.dispose();
  } finally {
    fs.rmSync(scratch, { recursive: true, force: true });
  }
});

function exactVerifyArgs({ trustedRoot, bundle, identity, blobPath }) {
  return [
    'verify-blob',
    '--trusted-root', trustedRoot,
    '--bundle', bundle,
    '--certificate-oidc-issuer', issuer,
    '--certificate-identity', identity,
    blobPath,
  ];
}

function assertCosignSucceeds(cosign, args) {
  const result = runOffline(cosign, args);
  assert.equal(result.status, 0, result.stderr || result.stdout);
}

function assertCosignFails(cosign, args) {
  const result = runOffline(cosign, args);
  assert.notEqual(result.status, 0, 'cosign unexpectedly succeeded');
}

function runOffline(cosign, args) {
  const env = {
    ...process.env,
    HTTPS_PROXY: 'http://127.0.0.1:9',
    HTTP_PROXY: 'http://127.0.0.1:9',
    ALL_PROXY: 'http://127.0.0.1:9',
    NO_PROXY: '',
  };
  if (process.platform === 'linux' && canUnshareNetwork()) {
    return spawnSync('unshare', ['-rn', cosign, ...args], {
      encoding: 'utf8',
      env,
      timeout: 20_000,
    });
  }
  return spawnSync(cosign, args, {
    encoding: 'utf8',
    env,
    timeout: 20_000,
  });
}

function canUnshareNetwork() {
  try {
    execFileSync('unshare', ['-rn', 'true'], { stdio: 'ignore', timeout: 5_000 });
    return true;
  } catch {
    return false;
  }
}

function writeJson(file, value) {
  fs.writeFileSync(file, `${JSON.stringify(value, undefined, 2)}\n`);
}

function canonicalJson(value) {
  if (value === undefined) return undefined;
  if (value === null || typeof value !== 'object') return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map((item) => canonicalJson(item)).join(',')}]`;
  return `{${Object.keys(value).sort().map((key) =>
    `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(',')}}`;
}
