import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { normalizeEvidence, readEvidence, stageEvidence } from '../release-evidence.mjs';

const digest = `sha256:${'a'.repeat(64)}`;
const other = `sha256:${'b'.repeat(64)}`;
const platformDigest = `sha256:${'c'.repeat(64)}`;
const predicate = { SPDXID: 'SPDXRef-DOCUMENT' };
const certificate = `-----BEGIN CERTIFICATE-----
MIIDITCCAgmgAwIBAgIUJgCnA8pimf4TtHhn54DkRCgUikowDQYJKoZIhvcNAQEL
BQAwIDEeMBwGA1UEAwwVcmVsZWFzZS1ldmlkZW5jZS10ZXN0MB4XDTI2MDkxNDIy
NDc0NFoXDTI3MDkxNDIyNDc0NFowIDEeMBwGA1UEAwwVcmVsZWFzZS1ldmlkZW5j
ZS10ZXN0MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAtZ3XWRa4DU9i
CHWD3tUhj5Kf2dpXE3+luZikC1/FQti6ZrxeT+6nLihTCdroFpNoXQrT2O3neUFI
37OOiygQ6Cd8WSRJ+CLt6VIUuTu6ntWbLR3OHEv2wBCAVsL1HWoiUoAuKoOVZEHq
HwHU+z6BCw/1MhbGDdzgc6tyirAN1WB5NYB8HXjnpfY4DLIvjYr7bLhn2ep5r1QH
mtM4pN67lgpEYxc6JLMvu5V+Dg3GU6VEX34OGXdmBT3xEQ7T54XJtFgU05OtRHpm
ME8rqz7U2uGWlPwPKmLin/Hb+o05UskhCNM5x/l/HnD0ggBVPFnhCNaDO18QJs0m
7CbykcQh+wIDAQABo1MwUTAdBgNVHQ4EFgQUn1N4X6p6NnttpzKqzDvNu/lbyDww
HwYDVR0jBBgwFoAUn1N4X6p6NnttpzKqzDvNu/lbyDwwDwYDVR0TAQH/BAUwAwEB
/zANBgkqhkiG9w0BAQsFAAOCAQEAq2mAhPcSnYNlCNYPX/iCn0GSg32RwYUdi2Ap
2L0lmztFLny0pKiYcwKwIK35+pYSVOr28vRj16SBmZFPdAjUGKnyMVbOKGR6Zggj
v7m19BoUidStAqQ32L6VT1Qr9CpbqFERXUEzn6AfnP0QoPo6WctYia+sa+FjYlk9
rQlctU3q0xs/y0h/ZtBMtiCSqoWd/pSr21YrXPLUFQPPKpE1eYfh1Zoc4sWY5xMF
xhbqlANI3r0ql7hOlLRKsnswTHq4h1xJ/81+BTahCkwGIbvBnO2ddVdZfNeAkNS4
JvcWVV+jlxUIUkvyi8jjlUzq/l/os6B8crubKZ9z++J4rrlAoA==
-----END CERTIFICATE-----`;
const signer = 'https://github.com/OlyForge3D/PrintFarmer/.github/workflows/docker-publish.yml@refs/heads/development';
const trust = (overrides = {}) => ({
  policy: {
    issuer: 'https://token.actions.githubusercontent.com', certificateMaxAgeSeconds: 900,
    revocationEpoch: '2026-01-01T00:00:00.000Z', revokedReleaseIds: [], revokedSignerIdentities: [],
    signers: [{ identity: signer, validFrom: '2026-01-01T00:00:00.000Z', validUntil: '2027-01-01T00:00:00.000Z' }],
  }, releaseId: 'insider:1.2.3-insider.1', trustedTime: '2026-09-14T22:50:00.000Z', ...overrides,
});
function signed(subject = digest, predicateValue = predicate) {
  return {
    signatureBytes: JSON.stringify([{ critical: { image: { 'docker-manifest-digest': subject } } }]),
    attestationBytes: JSON.stringify([{ payload: Buffer.from(JSON.stringify({
      subject: [{ digest: { sha256: subject.slice(7) } }], predicate: predicateValue,
    })).toString('base64') }]),
    predicateBytes: JSON.stringify(predicateValue),
    signatureBundleBytes: JSON.stringify([{ payload: 'signed-payload', optional: { Subject: signer,
      Issuer: 'https://token.actions.githubusercontent.com', certificate,
      Bundle: { Payload: { integratedTime: 1789426200 }, SignedEntryTimestamp: 'proof' } } }]),
    attestationBundleBytes: JSON.stringify([{ payload: 'dsse-payload', optional: { Subject: signer,
      Issuer: 'https://token.actions.githubusercontent.com', certificate,
      Bundle: { Payload: { integratedTime: 1789426200 }, SignedEntryTimestamp: 'proof' } } }]),
  };
}
test('stages validated index and platform crypto evidence', () => {
  const root = mkdtempSync(join('.artifacts', 'release-evidence-'));
  try {
    const completeSet = { images: { api: { digest, platforms: { 'linux/amd64': { digest } } } } };
    const evidencePath = join(root, 'release-crypto-evidence.json');
    const result = stageEvidence(evidencePath, completeSet, { api: { index: signed(), platforms: { 'linux/amd64': signed() } } });
    assert.equal(result.set.services.api.platforms['linux/amd64'].subject, digest);
    assert.deepEqual(readEvidence(evidencePath, completeSet).set, result.set);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
test('accepts formatted key-reordered predicate bytes while retaining their raw digest', () => {
  const formattedPredicate = '{\n  "name": "PrintFarmer",\n  "SPDXID": "SPDXRef-DOCUMENT"\n}\n';
  const attestedPredicate = { SPDXID: 'SPDXRef-DOCUMENT', name: 'PrintFarmer' };
  const evidence = signed(digest, attestedPredicate);
  evidence.predicateBytes = formattedPredicate;
  const normalized = normalizeEvidence({ subject: digest, ...evidence });
  assert.notEqual(normalized.sbom.predicateSha256, normalized.sbom.sha256);
});
test('rejects swapped signature subject, predicate, and platform', () => {
  assert.throws(() => normalizeEvidence({ subject: digest, ...signed(other) }), /subject mismatch/);
  const swappedPredicate = signed(digest, { SPDXID: 'other' });
  swappedPredicate.predicateBytes = JSON.stringify(predicate);
  assert.throws(() => normalizeEvidence({ subject: digest, ...swappedPredicate }), /predicate mismatch/);
  assert.throws(() => normalizeEvidence({ subject: digest, platform: 'linux/s390x', ...signed() }), /platform/);
});

test('rejects stale, revoked, substituted, and out-of-window bundle trust before staging', () => {
  const completeSet = { images: { api: { digest, platforms: { 'linux/amd64': { digest } } } } };
  const collected = { api: { index: signed(), platforms: { 'linux/amd64': signed() } } };
  const root = mkdtempSync(join('.artifacts', 'release-evidence-'));
  try {
    const path = join(root, 'evidence.json');
    assert.doesNotThrow(() => stageEvidence(path, completeSet, collected, trust()));
    assert.throws(() => stageEvidence(path, completeSet, collected,
      trust({ trustedTime: '2026-09-14T23:10:01.000Z' })), /expired|revoked/);
    assert.throws(() => stageEvidence(path, completeSet, collected,
      trust({ policy: { ...trust().policy, revokedReleaseIds: [trust().releaseId] } })), /revoked/);
    assert.throws(() => stageEvidence(path, completeSet, collected,
      trust({ policy: { ...trust().policy, revokedSignerIdentities: [signer] } })), /untrusted|revoked/);
    const altered = structuredClone(collected);
    const alteredBundle = JSON.parse(altered.api.index.signatureBundleBytes);
    alteredBundle[0].optional.certificate = 'not-a-certificate';
    altered.api.index.signatureBundleBytes = JSON.stringify(alteredBundle);
    assert.throws(() => stageEvidence(path, completeSet, altered, trust()), /certificate/);
    assert.throws(() => stageEvidence(path, completeSet, collected, trust({
      policy: { ...trust().policy, signers: [{ ...trust().policy.signers[0], validUntil: '2026-09-01T00:00:00.000Z' }] },
    })), /rotation window/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('rejects staged signature bundle, subject, predicate, and platform substitutions', () => {
  const root = mkdtempSync(join('.artifacts', 'release-evidence-'));
  try {
    const evidencePath = join(root, 'release-crypto-evidence.json');
    const completeSet = {
      images: { api: { digest, platforms: { 'linux/amd64': { digest: platformDigest } } } },
    };
    const collected = {
      api: { index: signed(), platforms: { 'linux/amd64': signed(platformDigest) } },
    };
    const staged = stageEvidence(evidencePath, completeSet, collected);
    staged.set.services.api.platforms['linux/amd64'].signature.bytes = signed(other).signatureBytes;
    writeFileSync(evidencePath, JSON.stringify(staged.set));
    assert.throws(() => readEvidence(evidencePath, completeSet), /bytes digest mismatch/);

    assert.throws(() => stageEvidence(evidencePath, completeSet, {
      api: { index: signed(), platforms: { 'linux/amd64': signed(digest) } },
    }), /subject mismatch/);

    const wrongPredicate = signed(platformDigest, { SPDXID: 'SPDXRef-OTHER' });
    wrongPredicate.predicateBytes = JSON.stringify(predicate);
    assert.throws(() => stageEvidence(evidencePath, completeSet, {
      api: { index: signed(), platforms: { 'linux/amd64': wrongPredicate } },
    }), /predicate mismatch/);

    stageEvidence(evidencePath, completeSet, collected);
    const substituted = JSON.parse(readFileSync(evidencePath, 'utf8'));
    substituted.services.api.platforms['linux/amd64'].platform = 'linux/arm64';
    writeFileSync(evidencePath, JSON.stringify(substituted));
    assert.throws(() => readEvidence(evidencePath, completeSet), /subject\/platform mismatch/);

    stageEvidence(evidencePath, completeSet, collected);
    const unknown = JSON.parse(readFileSync(evidencePath, 'utf8'));
    unknown.services.api.index.unverified = true;
    writeFileSync(evidencePath, JSON.stringify(unknown));
    assert.throws(() => readEvidence(evidencePath, completeSet), /Invalid evidence entry/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
