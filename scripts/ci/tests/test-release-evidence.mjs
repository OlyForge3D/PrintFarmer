import assert from 'node:assert/strict';
import { X509Certificate } from 'node:crypto';
import test from 'node:test';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { normalizeEvidence, readEvidence, stageEvidence, stageEvidenceFromFiles } from '../release-evidence.mjs';

const digest = `sha256:${'a'.repeat(64)}`;
const other = `sha256:${'b'.repeat(64)}`;
const platformDigest = `sha256:${'c'.repeat(64)}`;
const predicate = { SPDXID: 'SPDXRef-DOCUMENT' };
const ndjson = (entries, separator = '\n') => `${entries.map(entry => JSON.stringify(entry)).join(separator)}${separator}`;
const parseNdjson = bytes => bytes.trimEnd().split(/\r?\n/).map(entry => JSON.parse(entry));
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
const alternateCertificate = `-----BEGIN CERTIFICATE-----
MIIDNTCCAh2gAwIBAgIUMM+ZvCqKeGYPK+FeVpJ9AuuMojQwDQYJKoZIhvcNAQEL
BQAwKjEoMCYGA1UEAwwfcmVsZWFzZS1ldmlkZW5jZS1hbHRlcm5hdGUtdGVzdDAe
Fw0yNjA5MTUwMzEwNDZaFw0zNjA5MTIwMzEwNDZaMCoxKDAmBgNVBAMMH3JlbGVh
c2UtZXZpZGVuY2UtYWx0ZXJuYXRlLXRlc3QwggEiMA0GCSqGSIb3DQEBAQUAA4IB
DwAwggEKAoIBAQC40lGW0Na1XTGVfd6wDosar7Mh30j/b2eqPb4AUivtvfoFvV6l
LNFsQIMIZWa2rOxElQdxQhLGYrZVgKOIBBmv4eNoTvvjm0GMzyX/HLa9gGqTs17D
Jz/kRdUqIK+VhhrMxhLrf+az7T+FLFLPODkbnJpbxnpFVCFQpk0sa4vBjDYdKURd
KgcvUURJhAKIQkrvRZE+81HJtDE4EPV7+5Xyt95DnShTUldNu+3YTz9IclicP5lV
vpLgu0ebOvt6S5IFi7DYd2mRSypkQich+ogrolE4b7A+tEbd+WE1dqqmVnhVin1p
clNDCMTXgOKcTa3rNBD5Mf1PHbTV7TkeLebfAgMBAAGjUzBRMB0GA1UdDgQWBBSr
FYLlhph9UqvZowKZPgy2URu+GDAfBgNVHSMEGDAWgBSrFYLlhph9UqvZowKZPgy2
URu+GDAPBgNVHRMBAf8EBTADAQH/MA0GCSqGSIb3DQEBCwUAA4IBAQAjZZGCNwD0
fKomjTmyBZYf7OrCdnP8cfAF6uzDQZTwPLD0V2lZXC40FCNLkz96+eJB5/FoFj3Z
GCgux5gN/EO+5PdQWzJtSxwv3+cq/H/p6ZMik4EX6+nqPwIp0vfH487YiUFsl4Zn
OvvgZEuaiNxcFiYfYnbDcWoSNK1jc0at9SFAUGWnIu/fGm5tGkGe/4wSRtf4AL85
2hh9ojS/ORD46cLKnKuUtNHXCMBaED6OKvFJtIQiwxYcqd+8Ipb0fN8CM266bQLF
qKWSr9Vpj2GdAQT0rHrNMuULq1Kh1+00O+rTGXgZP+YEYIoWtZvIl4aKZULtBYqj
N6H7jV+ARenD
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
      Bundle: { Payload: { integratedTime: 1789426200, canonicalizedBody: 'proof', signature: 'native-signature' },
        SignedEntryTimestamp: 'proof' } },
  };
  const statement = {
    subject: [{ digest: { sha256: subject.slice(7) } }], predicate: predicateValue,
  };
  return {
    signatureBytes: JSON.stringify([{ critical: { image: { 'docker-manifest-digest': subject } }, ...verification }]),
    attestationBytes: JSON.stringify([{ payload: Buffer.from(JSON.stringify(statement)).toString('base64'), ...verification }]),
    predicateBytes: JSON.stringify(predicateValue),
    signatureVerificationTime: '2026-09-14T22:50:00.000Z',
    attestationVerificationTime: '2026-09-14T22:50:00.000Z',
    signatureBundleBytes: JSON.stringify([{
      mediaType: 'application/vnd.dev.sigstore.bundle.v0.3+json',
      verificationMaterial: {
        certificate: { rawBytes: new X509Certificate(certificate).raw.toString('base64') },
        tlogEntries: [{ integratedTime: 1789426200, canonicalizedBody: 'proof' }],
      },
      messageSignature: {
        messageDigest: { algorithm: 'SHA2_256', digest: Buffer.from(subject.slice(7), 'hex').toString('base64') },
        signature: 'native-signature',
      },
    }]),
    attestationBundleBytes: JSON.stringify([{
      mediaType: 'application/vnd.dev.sigstore.bundle.v0.3+json',
      verificationMaterial: {
        certificate: { rawBytes: new X509Certificate(certificate).raw.toString('base64') },
        tlogEntries: [{ integratedTime: 1789426200, canonicalizedBody: 'proof' }],
      },
      dsseEnvelope: { payload: Buffer.from(JSON.stringify(statement)).toString('base64'),
        payloadType: 'application/vnd.in-toto+json', signatures: [{ sig: 'native-signature' }] },
    }]),
  };
}

function setIntegratedTime(evidence, kind, value) {
  const verification = JSON.parse(evidence[`${kind}Bytes`]);
  verification[0].optional.Bundle.Payload.integratedTime = value;
  evidence[`${kind}Bytes`] = JSON.stringify(verification);
  const bundleBytes = evidence[`${kind}BundleBytes`];
  const bundle = bundleBytes.trimStart().startsWith('[') ? JSON.parse(bundleBytes) : parseNdjson(bundleBytes);
  bundle[0].verificationMaterial.tlogEntries[0].integratedTime = value;
  evidence[`${kind}BundleBytes`] = bundleBytes.trimStart().startsWith('[')
    ? JSON.stringify(bundle)
    : ndjson(bundle);
}

function timedEvidence(signatureTime, attestationTime) {
  const evidence = signed();
  setIntegratedTime(evidence, 'signature', Math.floor(Date.parse(signatureTime) / 1000) - 600);
  setIntegratedTime(evidence, 'attestation', Math.floor(Date.parse(attestationTime) / 1000) - 600);
  return { ...evidence, signatureVerificationTime: signatureTime, attestationVerificationTime: attestationTime };
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

test('stages exact raw NDJSON partitions from captured evidence files', () => {
  const root = mkdtempSync(join('.artifacts', 'release-evidence-'));
  try {
    const completeSet = { images: { api: { digest, platforms: {} } } };
    const capture = signed();
    const signature = JSON.parse(capture.signatureBundleBytes);
    const attestation = JSON.parse(capture.attestationBundleBytes);
    const signatureRaw = ndjson(signature, '\r\n');
    const attestationRaw = ndjson(attestation);
    const evidenceRoot = join(root, 'crypto-evidence', 'api', 'index');
    mkdirSync(evidenceRoot, { recursive: true });
    for (const [name, bytes] of Object.entries({
      'signature.json': capture.signatureBytes,
      'attestation.json': capture.attestationBytes,
      'predicate.json': capture.predicateBytes,
      'signature.bundle.json': signatureRaw,
      'attestation.bundle.json': attestationRaw,
      'download.json': `${signatureRaw}${attestationRaw}`,
      'signature.verification-time': capture.signatureVerificationTime,
      'attestation.verification-time': capture.attestationVerificationTime,
    })) writeFileSync(join(evidenceRoot, name), bytes);

    const result = stageEvidenceFromFiles(join(root, 'release-crypto-evidence.json'), completeSet,
      join(root, 'crypto-evidence'));
    assert.equal(result.set.services.api.index.signature.bundle, signatureRaw);
    assert.equal(result.set.services.api.index.sbom.bundle, attestationRaw);
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
  assert.throws(() => normalizeEvidence({ subject: digest, ...legacy }), /signature download/);

  const invalid = signed();
  invalid.signatureBundleBytes = JSON.stringify([{ payload: 'invented-lowercase', certificate: certificate }]);
  assert.throws(() => normalizeEvidence({ subject: digest, ...invalid }), /signature download/);

  const extra = signed();
  extra.signatureBundleBytes = JSON.stringify([
    ...JSON.parse(extra.signatureBundleBytes), JSON.parse(extra.signatureBundleBytes)[0],
  ]);
  assert.throws(() => normalizeEvidence({ subject: digest, ...extra }), /signature download/);
});

test('rejects legacy download entries even when payload, signature, bundle, and verifier fields agree', () => {
  const evidence = signed();
  const signatureVerification = JSON.parse(evidence.signatureBytes)[0];
  evidence.signatureBytes = JSON.stringify([signatureVerification]);
  const signature = {
    Base64Signature: signatureVerification.optional.Bundle.Payload.signature,
    Payload: Buffer.from(JSON.stringify(signatureVerification)).toString('base64'),
    Cert: certificate,
    Bundle: signatureVerification.optional.Bundle,
  };
  const dsse = JSON.parse(evidence.attestationBundleBytes)[0].dsseEnvelope;
  evidence.signatureBundleBytes = JSON.stringify([signature]);
  evidence.attestationBundleBytes = JSON.stringify([dsse]);
  evidence.downloadBytes = JSON.stringify([signature, dsse]);
  assert.throws(() => normalizeEvidence({ subject: digest, trust: trust(), ...evidence }),
    /malformed entry|unknown or ambiguous|incomplete/);

  const payloadOptionalMutation = structuredClone(evidence);
  const mutatedPayload = JSON.parse(Buffer.from(signature.Payload, 'base64').toString('utf8'));
  mutatedPayload.optional.Subject = 'https://example.invalid/substituted';
  payloadOptionalMutation.signatureBundleBytes = JSON.stringify([{
    ...signature, Payload: Buffer.from(JSON.stringify(mutatedPayload)).toString('base64'),
  }]);
  assert.throws(() => normalizeEvidence({ subject: digest, trust: trust(), ...payloadOptionalMutation }),
    /malformed entry|unknown or ambiguous|incomplete/);
});

test('retains byte-faithful Cosign v3.0.6 raw capture composition metadata', () => {
  const fixtureRoot = join('scripts', 'ci', 'tests', 'fixtures', 'cosign-v3.0.6');
  const metadata = JSON.parse(readFileSync(join(fixtureRoot, 'metadata.json'), 'utf8'));
  const signature = readFileSync(join(fixtureRoot, 'legacy-signature.ndjson'));
  const attestation = readFileSync(join(fixtureRoot, 'native-attestation.ndjson'));
  const combined = readFileSync(join(fixtureRoot, 'combined.ndjson'));

  assert.equal(metadata.cliVersion, 'v3.0.6');
  assert.equal(metadata.order, 'legacy-signature.ndjson, native-attestation.ndjson');
  assert.equal(metadata.composition,
    'legacy-signature.ndjson bytes, then one LF delimiter if absent, then native-attestation.ndjson bytes; no JSON reserialization');
  assert.match(metadata.legacySupport, /^unsupported: retained only for parser and partition rejection coverage/);
  const delimiter = signature.at(-1) === 0x0a ? Buffer.alloc(0) : Buffer.from('\n');
  assert.deepEqual(combined, Buffer.concat([signature, delimiter, attestation]));

  const signatureEntries = signature.toString('utf8').trim().split(/\r?\n/).map(JSON.parse);
  const attestationEntries = attestation.toString('utf8').trim().split(/\r?\n/).map(JSON.parse);
  const combinedEntries = combined.toString('utf8').trim().split(/\r?\n/).map(JSON.parse);
  assert.ok(signatureEntries.every(entry => entry.Base64Signature && entry.Payload && entry.Bundle && !entry.dsseEnvelope));
  assert.ok(attestationEntries.every(entry => entry.dsseEnvelope && !entry.messageSignature));
  assert.deepEqual(combinedEntries, [...signatureEntries, ...attestationEntries]);
});

test('retains raw legacy NDJSON only as parser and partition rejection coverage', () => {
  const fixtureRoot = join('scripts', 'ci', 'tests', 'fixtures', 'cosign-v3.0.6');
  const legacyBytes = readFileSync(join(fixtureRoot, 'legacy-signature.ndjson'), 'utf8');
  const combinedBytes = readFileSync(join(fixtureRoot, 'combined.ndjson'), 'utf8');
  const legacyEntries = legacyBytes.trim().split(/\r?\n/).map(JSON.parse);
  const subject = `sha256:${JSON.parse(Buffer.from(legacyEntries[0].Payload, 'base64').toString('utf8'))
    .critical.image['docker-manifest-digest'].slice(7)}`;
  const signatureBytes = JSON.stringify(legacyEntries.map(entry => {
    const payload = JSON.parse(Buffer.from(entry.Payload, 'base64').toString('utf8'));
    return { ...payload, optional: { ...payload.optional, Subject: signer, Issuer: 'https://token.actions.githubusercontent.com',
      certificate, Bundle: entry.Bundle } };
  }));
  const evidence = signed(subject);
  evidence.signatureBytes = signatureBytes;
  evidence.signatureBundleBytes = legacyBytes;
  evidence.downloadBytes = combinedBytes;
  assert.throws(() => normalizeEvidence({ subject, trust: trust(), ...evidence }), /unknown or ambiguous/);
});

test('accepts native Cosign v3 Sigstore v0.3 signature and DSSE bundles', () => {
  const evidence = signed();
  const statement = JSON.parse(Buffer.from(
    JSON.parse(evidence.attestationBundleBytes)[0].dsseEnvelope.payload, 'base64').toString('utf8'));
  const nativeSignature = {
    mediaType: 'application/vnd.dev.sigstore.bundle.v0.3+json',
    verificationMaterial: {
      certificate: { rawBytes: new X509Certificate(certificate).raw.toString('base64') },
      tlogEntries: [{ integratedTime: 1789426200, canonicalizedBody: 'proof' }],
    },
    messageSignature: {
      messageDigest: { algorithm: 'SHA2_256', digest: Buffer.from(digest.slice(7), 'hex').toString('base64') },
      signature: 'native-signature',
    },
  };
  evidence.signatureBundleBytes = ndjson([nativeSignature]);
  evidence.attestationBundleBytes = ndjson([{
    mediaType: 'application/vnd.dev.sigstore.bundle.v0.3+json',
    verificationMaterial: nativeSignature.verificationMaterial,
    dsseEnvelope: {
      payload: Buffer.from(JSON.stringify(statement)).toString('base64'),
      payloadType: 'application/vnd.in-toto+json',
      signatures: [{ sig: 'native-signature' }],
    },
  }]);
  evidence.downloadBytes = `${evidence.signatureBundleBytes}${evidence.attestationBundleBytes}`;
  assert.doesNotThrow(() => normalizeEvidence({ subject: digest, trust: trust(), ...evidence }));
  for (const [label, mutate] of [
    ['signature', value => { value.messageSignature.signature = 'substituted'; }],
    ['transparency body', value => { value.verificationMaterial.tlogEntries[0].canonicalizedBody = 'substituted'; }],
  ]) {
    const substituted = structuredClone(evidence);
    mutate(parseNdjson(substituted.signatureBundleBytes)[0]);
    const bundle = parseNdjson(substituted.signatureBundleBytes);
    mutate(bundle[0]);
    substituted.signatureBundleBytes = ndjson(bundle);
    substituted.downloadBytes = `${substituted.signatureBundleBytes}${substituted.attestationBundleBytes}`;
    assert.throws(() => normalizeEvidence({ subject: digest, trust: trust(), ...substituted }),
      /differs from verification output/, label);
  }
  const substitutedDsseSignature = structuredClone(evidence);
  const dsseBundle = parseNdjson(substitutedDsseSignature.attestationBundleBytes);
  dsseBundle[0].dsseEnvelope.signatures[0].sig = 'substituted';
  substitutedDsseSignature.attestationBundleBytes = ndjson(dsseBundle);
  substitutedDsseSignature.downloadBytes =
    `${substitutedDsseSignature.signatureBundleBytes}${substitutedDsseSignature.attestationBundleBytes}`;
  assert.throws(() => normalizeEvidence({ subject: digest, trust: trust(), ...substitutedDsseSignature }),
    /DSSE download is not the verified attestation|DSSE differs from verification output/);
  const substitutedCertificate = structuredClone(evidence);
  const certificateBundle = parseNdjson(substitutedCertificate.signatureBundleBytes);
  certificateBundle[0].verificationMaterial.certificate.rawBytes =
    `A${new X509Certificate(certificate).raw.toString('base64').slice(1)}`;
  substitutedCertificate.signatureBundleBytes = ndjson(certificateBundle);
  substitutedCertificate.downloadBytes =
    `${substitutedCertificate.signatureBundleBytes}${substitutedCertificate.attestationBundleBytes}`;
  assert.throws(() => normalizeEvidence({ subject: digest, trust: trust(), ...substitutedCertificate }),
    /certificate differs from verification output|certificate is malformed/);
  const alternateCertificateBundle = structuredClone(evidence);
  const alternate = new X509Certificate(alternateCertificate);
  const alternateIntegratedTime = new Date(Date.parse(alternate.validFrom) + 60_000).toISOString();
  setIntegratedTime(alternateCertificateBundle, 'signature', alternateIntegratedTime);
  setIntegratedTime(alternateCertificateBundle, 'attestation', alternateIntegratedTime);
  alternateCertificateBundle.signatureVerificationTime = new Date(
    Date.parse(alternateIntegratedTime) + 60_000).toISOString();
  alternateCertificateBundle.attestationVerificationTime = alternateCertificateBundle.signatureVerificationTime;
  assert.ok(Date.parse(alternate.validFrom) <= Date.parse(alternateIntegratedTime) &&
    Date.parse(alternateIntegratedTime) <= Date.parse(alternate.validTo));
  const alternateCertificateEntry = parseNdjson(alternateCertificateBundle.signatureBundleBytes);
  alternateCertificateEntry[0].verificationMaterial.certificate.rawBytes = alternate.raw.toString('base64');
  alternateCertificateBundle.signatureBundleBytes = ndjson(alternateCertificateEntry);
  alternateCertificateBundle.downloadBytes =
    `${alternateCertificateBundle.signatureBundleBytes}${alternateCertificateBundle.attestationBundleBytes}`;
  assert.throws(() => normalizeEvidence({ subject: digest, trust: trust({
    createdTime: alternate.validFrom, trustedTime: alternateCertificateBundle.signatureVerificationTime,
  }), ...alternateCertificateBundle }),
    /certificate differs from verification output/);

  for (const [label, field] of [['signature', 'signatureBundleBytes'], ['attestation', 'attestationBundleBytes']]) {
    const storedSubstitution = structuredClone(evidence);
    const bundle = parseNdjson(storedSubstitution[field]);
    bundle[0].verificationMaterial.tlogEntries[0].canonicalizedBody = 'stored-substitution';
    storedSubstitution[field] = ndjson(bundle);
    assert.throws(() => normalizeEvidence({ subject: digest, trust: trust(), ...storedSubstitution }),
      new RegExp(`Stored ${label} bundle does not match partitioned download`));
  }

  for (const [label, mutate] of [
    ['whitespace', bytes => bytes.replace('{', '{ ')],
    ['key order', bytes => {
      const entry = parseNdjson(bytes)[0];
      return ndjson([Object.fromEntries(Object.entries(entry).reverse())]);
    }],
    ['line endings', bytes => bytes.replaceAll('\n', '\r\n')],
  ]) {
    const substituted = structuredClone(evidence);
    substituted.signatureBundleBytes = mutate(substituted.signatureBundleBytes);
    assert.throws(() => normalizeEvidence({ subject: digest, trust: trust(), ...substituted }),
      /Stored signature bundle does not match partitioned download/, label);
  }

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
    const staleByItemTime = structuredClone(collected);
    staleByItemTime.api.index.signatureVerificationTime = '2026-09-14T23:10:01.000Z';
    assert.throws(() => stageEvidence(path, completeSet, staleByItemTime, trust()), /expired|revoked/);
    const predating = structuredClone(collected);
    predating.api.index.signatureVerificationTime = '2026-09-14T22:39:59.000Z';
    assert.throws(() => stageEvidence(path, completeSet, predating, trust()), /expired|revoked/);
    assert.throws(() => stageEvidence(path, completeSet, collected,
      trust({ policy: { ...trust().policy, revokedReleaseIds: [trust().releaseId] } })), /revoked/);
    assert.throws(() => stageEvidence(path, completeSet, collected,
      trust({ policy: { ...trust().policy, revokedSignerIdentities: [signer] } })), /untrusted|revoked/);
    const altered = structuredClone(collected);
    const alteredDownload = JSON.parse(altered.api.index.signatureBundleBytes);
    alteredDownload[0].verificationMaterial.certificate.rawBytes = 'not-a-certificate';
    altered.api.index.signatureBundleBytes = JSON.stringify(alteredDownload);
    assert.throws(() => stageEvidence(path, completeSet, altered, trust()), /certificate/);
    assert.throws(() => stageEvidence(path, completeSet, collected, trust({
      policy: { ...trust().policy, signers: [{ ...trust().policy.signers[0], validUntil: '2026-09-01T00:00:00.000Z' }] },
    })), /rotation window/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('uses each signature and attestation verification time rather than final evidence completion time', () => {
  const completeSet = { images: { api: { digest, platforms: {} } } };
  const root = mkdtempSync(join('.artifacts', 'release-evidence-'));
  try {
    const evidencePath = join(root, 'evidence.json');
    const signatureVerificationTime = '2026-09-14T22:58:00.000Z';
    const attestationVerificationTime = '2026-09-14T23:28:01.000Z';
    const collected = { api: { index: timedEvidence(signatureVerificationTime, attestationVerificationTime), platforms: {} } };
    const result = stageEvidence(evidencePath, completeSet, collected, trust());
    assert.equal(result.set.services.api.index.signature.verificationTime, signatureVerificationTime);
    assert.equal(result.set.services.api.index.sbom.verificationTime, attestationVerificationTime);
    assert.equal(result.set.verificationTime, attestationVerificationTime);

    const stale = structuredClone(collected);
    stale.api.index.signatureVerificationTime = '2026-09-14T23:20:01.000Z';
    assert.throws(() => stageEvidence(evidencePath, completeSet, stale, trust()), /expired|revoked/);

    const malformed = structuredClone(collected);
    malformed.api.index.attestationVerificationTime = 'not-a-timestamp';
    assert.throws(() => stageEvidence(evidencePath, completeSet, malformed, trust()), /per-entry verification time/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('rejects legacy signature and attestation verification missing trusted optional material', () => {
  const legacySignature = signed();
  legacySignature.signatureBundleBytes = JSON.stringify([{ Base64Signature: 'signature', Payload: 'payload' }]);
  const signatureVerification = JSON.parse(legacySignature.signatureBytes);
  delete signatureVerification[0].optional;
  legacySignature.signatureBytes = JSON.stringify(signatureVerification);
  assert.throws(() => normalizeEvidence({ subject: digest, trust: trust(), ...legacySignature }),
    /Malformed Cosign signature download/);

  const legacyAttestation = signed();
  const attestationVerification = JSON.parse(legacyAttestation.attestationBytes);
  delete attestationVerification[0].optional;
  legacyAttestation.attestationBytes = JSON.stringify(attestationVerification);
  assert.throws(() => normalizeEvidence({ subject: digest, trust: trust(), ...legacyAttestation }),
    /DSSE download is not the verified attestation/);
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
