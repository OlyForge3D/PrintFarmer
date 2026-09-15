import assert from 'node:assert/strict';
import { X509Certificate } from 'node:crypto';
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
  }, releaseId: 'insider:1.2.3-insider.1', createdTime: '2026-09-14T22:40:00.000Z',
  trustedTime: '2026-09-14T22:50:00.000Z', ...overrides,
});
function signed(subject = digest, predicateValue = predicate) {
  const verification = {
    optional: { Subject: signer, Issuer: 'https://token.actions.githubusercontent.com', certificate,
      Bundle: { Payload: { integratedTime: 1789426200 }, SignedEntryTimestamp: 'proof' } },
  };
  const statement = {
    subject: [{ digest: { sha256: subject.slice(7) } }], predicate: predicateValue,
  };
  return {
    signatureBytes: JSON.stringify([{ critical: { image: { 'docker-manifest-digest': subject } }, ...verification }]),
    attestationBytes: JSON.stringify([{ payload: Buffer.from(JSON.stringify(statement)).toString('base64'), ...verification }]),
    predicateBytes: JSON.stringify(predicateValue),
    signatureBundleBytes: JSON.stringify([{ SignedPayload: 'signed-payload', Cert: certificate,
      Bundle: { Payload: { integratedTime: 1789426200 }, SignedEntryTimestamp: 'proof' } }]),
    attestationBundleBytes: JSON.stringify([{ payload: Buffer.from(JSON.stringify(statement)).toString('base64'),
      payloadType: 'application/vnd.in-toto+json', signatures: [{ sig: 'dsse-signature' }] }]),
  };
}
test('stages validated index and platform crypto evidence', () => {
  const root = mkdtempSync(join('.artifacts', 'release-evidence-'));
  try {
    const completeSet = { images: { api: { digest, platforms: { 'linux/amd64': { digest } } } } };
    const evidencePath = join(root, 'release-crypto-evidence.json');
    const context = trust();
    const result = stageEvidence(evidencePath, completeSet, { api: { index: signed(), platforms: { 'linux/amd64': signed() } } }, context);
    assert.equal(result.set.services.api.platforms['linux/amd64'].subject, digest);
    assert.deepEqual(readEvidence(evidencePath, completeSet, context).set, result.set);
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

test('accepts only versioned Cosign signature download formats and rejects forged entries', () => {
  const legacy = signed();
  legacy.signatureBundleBytes = JSON.stringify([{ Base64Signature: 'signature', Payload: 'payload' }]);
  assert.doesNotThrow(() => normalizeEvidence({ subject: digest, ...legacy }));

  const invalid = signed();
  invalid.signatureBundleBytes = JSON.stringify([{ payload: 'invented-lowercase', certificate: certificate }]);
  assert.throws(() => normalizeEvidence({ subject: digest, ...invalid }), /signature download/);

  const extra = signed();
  extra.signatureBundleBytes = JSON.stringify([
    ...JSON.parse(extra.signatureBundleBytes), JSON.parse(extra.signatureBundleBytes)[0],
  ]);
  assert.throws(() => normalizeEvidence({ subject: digest, ...extra }), /signature download/);
});

test('accepts native Cosign v3 Sigstore v0.3 signature and DSSE bundles', () => {
  const evidence = signed();
  const statement = JSON.parse(Buffer.from(JSON.parse(evidence.attestationBundleBytes)[0].payload, 'base64').toString('utf8'));
  const nativeSignature = {
    mediaType: 'application/vnd.dev.sigstore.bundle.v0.3+json',
    verificationMaterial: {
      certificate: { rawBytes: new X509Certificate(certificate).raw.toString('base64') },
      tlogEntries: [{ integratedTime: 1789426200 }],
    },
    messageSignature: {
      messageDigest: { algorithm: 'SHA2_256', digest: Buffer.from(digest.slice(7), 'hex').toString('base64') },
      signature: 'native-signature',
    },
  };
  evidence.signatureBundleBytes = JSON.stringify([nativeSignature]);
  evidence.attestationBundleBytes = JSON.stringify([{
    mediaType: 'application/vnd.dev.sigstore.bundle.v0.3+json',
    verificationMaterial: nativeSignature.verificationMaterial,
    dsseEnvelope: {
      payload: Buffer.from(JSON.stringify(statement)).toString('base64'),
      payloadType: 'application/vnd.in-toto+json',
      signatures: [{ sig: 'native-dsse-signature' }],
    },
  }]);
  assert.doesNotThrow(() => normalizeEvidence({ subject: digest, trust: trust(), ...evidence }));

  const wrongIdentity = structuredClone(evidence);
  const identityVerification = JSON.parse(wrongIdentity.signatureBytes);
  identityVerification[0].optional.Subject = 'https://example.invalid/untrusted';
  wrongIdentity.signatureBytes = JSON.stringify(identityVerification);
  assert.throws(() => normalizeEvidence({ subject: digest, trust: trust(), ...wrongIdentity }),
    /untrusted|rotation window/);

  const wrongIssuer = structuredClone(evidence);
  const issuerVerification = JSON.parse(wrongIssuer.signatureBytes);
  issuerVerification[0].optional.Issuer = 'https://example.invalid/issuer';
  wrongIssuer.signatureBytes = JSON.stringify(issuerVerification);
  assert.throws(() => normalizeEvidence({ subject: digest, trust: trust(), ...wrongIssuer }),
    /untrusted|issuer/);

  assert.throws(() => normalizeEvidence({ subject: digest, trust: trust({
    policy: { ...trust().policy, revokedSignerIdentities: [signer] },
  }), ...evidence }), /untrusted|revoked/);

  assert.throws(() => normalizeEvidence({ subject: digest, trust: trust({
    policy: { ...trust().policy, signers: [{ ...trust().policy.signers[0],
      validUntil: '2026-09-01T00:00:00.000Z' }] },
  }), ...evidence }), /rotation window/);
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
      trust({ createdTime: '2026-09-14T22:51:00.000Z' })), /expired|revoked/);
    assert.throws(() => stageEvidence(path, completeSet, collected,
      trust({ policy: { ...trust().policy, revokedReleaseIds: [trust().releaseId] } })), /revoked/);
    assert.throws(() => stageEvidence(path, completeSet, collected,
      trust({ policy: { ...trust().policy, revokedSignerIdentities: [signer] } })), /untrusted|revoked/);
    const altered = structuredClone(collected);
    const alteredVerification = JSON.parse(altered.api.index.signatureBytes);
    alteredVerification[0].optional.certificate = 'not-a-certificate';
    altered.api.index.signatureBytes = JSON.stringify(alteredVerification);
    const alteredDownload = JSON.parse(altered.api.index.signatureBundleBytes);
    alteredDownload[0].Cert = 'not-a-certificate';
    altered.api.index.signatureBundleBytes = JSON.stringify(alteredDownload);
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
    const staged = stageEvidence(evidencePath, completeSet, collected, trust());
    staged.set.services.api.platforms['linux/amd64'].signature.bytes = signed(other).signatureBytes;
    writeFileSync(evidencePath, JSON.stringify(staged.set));
    assert.throws(() => readEvidence(evidencePath, completeSet, trust()), /bytes digest mismatch/);

    assert.throws(() => stageEvidence(evidencePath, completeSet, {
      api: { index: signed(), platforms: { 'linux/amd64': signed(digest) } },
    }), /subject mismatch/);

    const wrongPredicate = signed(platformDigest, { SPDXID: 'SPDXRef-OTHER' });
    wrongPredicate.predicateBytes = JSON.stringify(predicate);
    assert.throws(() => stageEvidence(evidencePath, completeSet, {
      api: { index: signed(), platforms: { 'linux/amd64': wrongPredicate } },
    }), /predicate mismatch/);

    stageEvidence(evidencePath, completeSet, collected, trust());
    const substituted = JSON.parse(readFileSync(evidencePath, 'utf8'));
    substituted.services.api.platforms['linux/amd64'].platform = 'linux/arm64';
    writeFileSync(evidencePath, JSON.stringify(substituted));
    assert.throws(() => readEvidence(evidencePath, completeSet, trust()), /subject\/platform mismatch/);

    stageEvidence(evidencePath, completeSet, collected, trust());
    const unknown = JSON.parse(readFileSync(evidencePath, 'utf8'));
    unknown.services.api.index.unverified = true;
    writeFileSync(evidencePath, JSON.stringify(unknown));
    assert.throws(() => readEvidence(evidencePath, completeSet, trust()), /Invalid evidence entry/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('rejects a raw DSSE bundle with substituted predicate payload', () => {
  const evidence = signed();
  evidence.attestationBundleBytes = JSON.stringify([{
    payload: Buffer.from(JSON.stringify({ subject: [{ digest: { sha256: digest.slice(7) } }],
      predicate: { SPDXID: 'SPDXRef-SUBSTITUTED' } })).toString('base64'),
    payloadType: 'application/vnd.in-toto+json', signatures: [{ sig: 'dsse-signature' }],
  }]);
  assert.throws(() => normalizeEvidence({ subject: digest, ...evidence }),
    /DSSE|predicate/);
});
