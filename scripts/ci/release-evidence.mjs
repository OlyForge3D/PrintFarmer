import { createHash, X509Certificate } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

const digestPattern = /^sha256:[a-f0-9]{64}$/;

function requireThat(condition, message) {
  if (!condition) throw new Error(message);
}

function sha256(bytes) {
  return createHash('sha256').update(bytes).digest('hex');
}

function parseJson(bytes, label) {
  try { return JSON.parse(bytes); } catch {
    try {
      const entries = bytes.trim().split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
      requireThat(entries.length > 0, `Malformed ${label}`);
      return entries;
    } catch { throw new Error(`Malformed ${label}`); }
  }
}

function canonicalJson(value) {
  if (Array.isArray(value)) return value.map(canonicalJson);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonicalJson(value[key])]));
  }
  return value;
}

function canonicalEvidenceSet(set) {
  const services = canonicalJson(set.services);
  return Object.hasOwn(set, 'createdTime')
    ? JSON.stringify({ schema: set.schema, createdTime: set.createdTime,
      verificationTime: set.verificationTime, services })
    : JSON.stringify({ schema: set.schema, services });
}
function nativeBundle(entry) {
  return entry?.mediaType === 'application/vnd.dev.sigstore.bundle.v0.3+json' &&
    entry.verificationMaterial && typeof entry.verificationMaterial === 'object';
}

function nativeSignatureBundle(entry) {
  return nativeBundle(entry) && entry.messageSignature && typeof entry.messageSignature === 'object' &&
    entry.dsseEnvelope === undefined;
}

function nativeDsseBundle(entry) {
  return nativeBundle(entry) && entry.dsseEnvelope && typeof entry.dsseEnvelope === 'object' &&
    entry.messageSignature === undefined;
}

function nativeCertificate(entry) {
  const rawBytes = entry?.verificationMaterial?.certificate?.rawBytes;
  requireThat(typeof rawBytes === 'string' && rawBytes.length > 0, 'Cosign bundle certificate is malformed');
  try { return new X509Certificate(Buffer.from(rawBytes, 'base64')); } catch {
    throw new Error('Cosign bundle certificate is malformed');
  }
}

function nativeIntegratedTime(entry) {
  const entries = entry?.verificationMaterial?.tlogEntries;
  requireThat(Array.isArray(entries) && entries.length === 1 &&
    Number.isSafeInteger(Number(entries[0]?.integratedTime)), 'Cosign transparency time is expired or revoked');
  return Number(entries[0].integratedTime);
}

function validateNativeSignatureBundle(entry, subject, verification) {
  requireThat(nativeSignatureBundle(entry) && entry.messageSignature?.messageDigest?.algorithm === 'SHA2_256' &&
    entry.messageSignature?.messageDigest?.digest === Buffer.from(subject.slice(7), 'hex').toString('base64') &&
    typeof entry.messageSignature?.signature === 'string' && entry.messageSignature.signature.length > 0,
  'Malformed native Cosign signature bundle');
  nativeCertificate(entry);
  requireThat(entry.messageSignature.signature === verification?.optional?.Bundle?.Payload?.signature,
    'Native Cosign signature differs from verification output');
}

function subjects(result, label) {
  const entries = Array.isArray(result) ? result : [result];
  requireThat(entries.length > 0, `Missing ${label} evidence`);
  return entries.map(entry => {
    const statement = payload(entry);
    const digest = statement?.subject?.[0]?.digest?.sha256;
    return {
      subject: entry?.critical?.image?.['docker-manifest-digest'] ??
        (digest ? `sha256:${digest}` : undefined),
      predicate: statement?.predicate ?? entry?.predicate,
    };
  });
}

function payload(entry) {
  if (typeof entry?.payload !== 'string') return entry?.payload;
  try { return JSON.parse(Buffer.from(entry.payload, 'base64').toString('utf8')); } catch {
    throw new Error('Malformed attestation payload');
  }
}

function verificationEntries(bytes, expectedDigest, type, expectedPredicate) {
  requireThat(typeof bytes === 'string' && bytes.length > 0, `Missing ${type} bytes`);
  requireThat(digestPattern.test(expectedDigest), `Invalid ${type} subject`);
  const parsed = parseJson(bytes, type);
  const entries = subjects(parsed, type);
  requireThat(entries.every(entry => entry.subject === expectedDigest), `${type} subject mismatch`);
  if (expectedPredicate !== undefined) {
    const parsedPredicate = parseJson(expectedPredicate, 'SPDX predicate');
    requireThat(entries.every(entry =>
      JSON.stringify(canonicalJson(entry.predicate)) === JSON.stringify(canonicalJson(parsedPredicate))),
      'SPDX predicate mismatch');
  }
  return Array.isArray(parsed) ? parsed : [parsed];
}

function validateVerification(bytes, expectedDigest, type, expectedPredicate) {
  verificationEntries(bytes, expectedDigest, type, expectedPredicate);
}

function validateBundleTrust(bundle, trust, verification = []) {
  if (trust === undefined) return;
  const { policy, releaseId, createdTime, trustedTime } = trust;
  requireThat(policy && typeof releaseId === 'string' && typeof createdTime === 'string' &&
    typeof trustedTime === 'string',
    'Missing release evidence trust context');
  const createdAt = Date.parse(createdTime);
  const trustedAt = Date.parse(trustedTime);
  requireThat(Number.isFinite(createdAt) && Number.isFinite(trustedAt) && createdAt <= trustedAt &&
    !policy.revokedReleaseIds.includes(releaseId),
    'Invalid or revoked release evidence time');
    const entries = Array.isArray(bundle) ? bundle : [bundle];
    requireThat(entries.length > 0 && (verification.length === 0 || verification.length === entries.length),
      'Cosign verification trust entries are missing or mismatched');
    for (const [index, entry] of entries.entries()) {
      const optional = verification.length > 0 ? verification[index]?.optional : entry?.optional;
      requireThat(typeof optional?.Subject === 'string' && typeof optional.Issuer === 'string' &&
        typeof optional.certificate === 'string' && optional.Bundle?.Payload &&
        Number.isSafeInteger(optional.Bundle.Payload.integratedTime),
    'Cosign verifier lacks required identity, certificate, or transparency trust material');
    if (nativeBundle(entry)) {
      requireThat(optional.Issuer === policy.issuer,
      'Native Cosign bundle lacks authoritative verifier identity or issuer');
      const integratedTime = nativeIntegratedTime(entry);
      const integratedAt = integratedTime * 1000;
      const certificate = nativeCertificate(entry);
      const verifiedCertificate = optional?.certificate;
      requireThat(typeof verifiedCertificate === 'string' &&
        certificate.raw.toString('base64') === new X509Certificate(verifiedCertificate).raw.toString('base64'),
      'Native Cosign certificate differs from verification output');
      requireThat(entry.verificationMaterial.tlogEntries[0]?.canonicalizedBody ===
        optional.Bundle.Payload.canonicalizedBody,
      'Native Cosign transparency body differs from verification output');
      requireThat(createdAt <= integratedAt && integratedAt >= Date.parse(policy.revocationEpoch) &&
        integratedAt <= trustedAt && trustedAt - integratedAt <= policy.certificateMaxAgeSeconds * 1000 &&
        Date.parse(certificate.validFrom) <= integratedAt && integratedAt <= Date.parse(certificate.validTo) &&
        trustedAt <= Date.parse(certificate.validTo),
      'Cosign transparency time is expired or revoked');
    }
    const trustedOptional = optional;
    const signer = trustedOptional?.Subject;
    const integratedTime = nativeBundle(entry)
      ? nativeIntegratedTime(entry)
      : trustedOptional?.Bundle?.Payload?.integratedTime;
    const integratedAt = integratedTime * 1000;
    requireThat(typeof signer === 'string' && trustedOptional?.Issuer === policy.issuer &&
      !policy.revokedSignerIdentities.includes(signer), 'Cosign bundle signer is untrusted or revoked');
    requireThat(Number.isSafeInteger(integratedTime) && createdAt <= integratedAt &&
      integratedAt >= Date.parse(policy.revocationEpoch) &&
      integratedAt <= trustedAt && trustedAt - integratedAt <= policy.certificateMaxAgeSeconds * 1000,
    'Cosign transparency time is expired or revoked');
    let certificate;
    try { certificate = new X509Certificate(nativeBundle(entry) ? nativeCertificate(entry).raw : trustedOptional?.certificate); } catch {
      throw new Error('Cosign bundle certificate is malformed');
    }
    requireThat(Date.parse(certificate.validFrom) <= integratedAt &&
      integratedAt <= Date.parse(certificate.validTo) && trustedAt <= Date.parse(certificate.validTo),
    'Cosign bundle certificate is not valid');
    requireThat(policy.signers.some(window => window.identity === signer &&
      Date.parse(window.validFrom) <= integratedAt && integratedAt <= Date.parse(window.validUntil)),
    'Cosign signer is outside its rotation window');
  }
}

function validateDsseBundle(bundle, subject, predicateBytes, verification) {
  const entries = Array.isArray(bundle) ? bundle : [bundle];
  requireThat(entries.every(nativeBundle), 'Legacy Cosign DSSE attestation downloads are unsupported');
  requireThat(entries.length > 0 && entries.every(entry => {
    const envelope = entry?.dsseEnvelope ?? entry;
    return typeof envelope?.payload === 'string' &&
      envelope.payload.length > 0 && envelope.payloadType === 'application/vnd.in-toto+json' &&
      Array.isArray(envelope.signatures) && envelope.signatures.length > 0 &&
      envelope.signatures.every(signature => typeof signature?.sig === 'string' && signature.sig.length > 0);
  }),
  'Malformed Cosign DSSE attestation bundle');
  const predicate = parseJson(predicateBytes, 'SPDX predicate');
  for (let index = 0; index < entries.length; index++) {
    const rawEntry = entries[index];
    requireThat(!nativeBundle(rawEntry) || nativeDsseBundle(rawEntry),
      'Malformed native Cosign DSSE attestation bundle');
    const entry = rawEntry?.dsseEnvelope ?? rawEntry;
    let statement;
    try { statement = JSON.parse(Buffer.from(entry.payload, 'base64').toString('utf8')); } catch {
      throw new Error('Malformed Cosign DSSE payload');
    }
    requireThat(statement?.subject?.[0]?.digest?.sha256 === subject.slice(7) &&
      JSON.stringify(canonicalJson(statement.predicate)) === JSON.stringify(canonicalJson(predicate)),
    'Cosign DSSE subject or SPDX predicate mismatch');
    requireThat(typeof verification[index]?.payload === 'string' &&
      verification[index].payload === entry.payload,
    'Cosign DSSE download is not the verified attestation');
  }
}

function validateSignatureDownload(bundle, verification, subject) {
  const entries = Array.isArray(bundle) ? bundle : [bundle];
  requireThat(entries.length === verification.length && entries.length > 0 && entries.every((entry, index) => {
    const legacy = typeof entry?.Base64Signature === 'string' && entry.Base64Signature.length > 0 &&
      typeof entry?.Payload === 'string' && entry.Payload.length > 0 &&
      entry.SignedPayload === undefined && entry.Cert === undefined && entry.Bundle === undefined;
    const modern = typeof entry?.SignedPayload === 'string' && entry.SignedPayload.length > 0 &&
      typeof entry?.Cert === 'string' && entry.Cert.length > 0 && entry?.Bundle &&
      typeof entry.Bundle === 'object' && entry.Base64Signature === undefined;
    if (nativeBundle(entry)) {
      if (!nativeSignatureBundle(entry)) return false;
      validateNativeSignatureBundle(entry, subject, verification[index]);
      return true;
    }
    if (!legacy && !modern) return false;
    if (legacy) return false;
    const optional = verification[index]?.optional;
    return optional?.certificate === entry.Cert &&
      JSON.stringify(canonicalJson(optional.Bundle)) === JSON.stringify(canonicalJson(entry.Bundle));
  }),
  'Malformed Cosign signature download');
}

function evidenceObject(subject, signatureBytes, attestationBytes, predicateBytes, signatureBundleBytes,
  attestationBundleBytes, platform, trust) {
  const signatureVerification = verificationEntries(signatureBytes, subject, 'signature');
  const attestationVerification = verificationEntries(attestationBytes, subject, 'attestation', predicateBytes);
  const signatureBundle = parseJson(signatureBundleBytes, 'signature bundle');
  const attestationBundle = parseJson(attestationBundleBytes, 'attestation bundle');
  validateSignatureDownload(signatureBundle, signatureVerification, subject);
  validateDsseBundle(attestationBundle, subject, predicateBytes, attestationVerification);
  requireThat((Array.isArray(signatureBundle) ? signatureBundle.length : 1) === signatureVerification.length &&
    (Array.isArray(attestationBundle) ? attestationBundle.length : 1) === attestationVerification.length,
  'Cosign verification/download entry count mismatch');
  validateBundleTrust(signatureBundle, trust, signatureVerification);
  validateBundleTrust(attestationBundle, trust, attestationVerification);
  validateBundleTrust(signatureVerification, trust);
  validateBundleTrust(attestationVerification, trust);
  return {
    subject, ...(platform === undefined ? {} : { platform }),
    signature: { sha256: sha256(signatureBytes), bytes: signatureBytes,
      bundleSha256: sha256(signatureBundleBytes), bundle: signatureBundleBytes },
    sbom: { sha256: sha256(attestationBytes), predicateSha256: sha256(predicateBytes), predicate: predicateBytes,
      bytes: attestationBytes, bundleSha256: sha256(attestationBundleBytes), bundle: attestationBundleBytes },
  };
}

export function normalizeEvidence({ subject, signatureBytes, attestationBytes, predicateBytes,
  signatureBundleBytes, attestationBundleBytes, platform, trust }) {
  requireThat(platform === undefined || /^linux\/(?:amd64|arm64)$/.test(platform), 'Invalid evidence platform');
  return evidenceObject(subject, signatureBytes, attestationBytes, predicateBytes, signatureBundleBytes,
    attestationBundleBytes, platform, trust);
}

export function validateEvidenceSet(set, completeSet, trust) {
  const trustedShape = trust === undefined ||
    (Object.keys(set ?? {}).sort().join() === 'createdTime,schema,services,verificationTime' &&
      typeof set.createdTime === 'string' && typeof set.verificationTime === 'string' &&
      trust.createdTime === set.createdTime && trust.trustedTime === set.verificationTime);
  requireThat(set?.schema === 1 && typeof set.services === 'object' && trustedShape,
  'Invalid release evidence set');
  for (const [service, image] of Object.entries(completeSet.images)) {
    const entry = set.services?.[service];
    requireThat(entry && Object.keys(entry).sort().join() === 'index,platforms', 'Incomplete release evidence');
    validateStored(entry.index, image.digest, undefined, trust);
    requireThat(Object.keys(entry.platforms).sort().join() === Object.keys(image.platforms).sort().join(),
      'Evidence platform mapping mismatch');
    for (const [platform, value] of Object.entries(image.platforms)) {
      validateStored(entry.platforms[platform], value.digest, platform, trust);
    }
  }
  requireThat(Object.keys(set.services).sort().join() === Object.keys(completeSet.images).sort().join(),
    'Unexpected release evidence service');
  return set;
}

function validateStored(value, digest, platform, trust) {
  const expectedKeys = platform === undefined
    ? ['subject', 'signature', 'sbom']
    : ['platform', 'subject', 'signature', 'sbom'];
  requireThat(value && typeof value === 'object' &&
    Object.keys(value).sort().join() === expectedKeys.sort().join(), 'Invalid evidence entry');
  requireThat(value?.subject === digest && value?.platform === platform, 'Evidence subject/platform mismatch');
  requireThat(value.signature && typeof value.signature === 'object' &&
    Object.keys(value.signature).sort().join() === 'bundle,bundleSha256,bytes,sha256' &&
    typeof value.signature.bytes === 'string' && digestPattern.test(`sha256:${value.signature.sha256}`),
  'Invalid signature bundle evidence');
  requireThat(value.sbom && typeof value.sbom === 'object' &&
    Object.keys(value.sbom).sort().join() === 'bundle,bundleSha256,bytes,predicate,predicateSha256,sha256' &&
    typeof value.sbom.bytes === 'string' && typeof value.sbom.predicate === 'string' &&
    digestPattern.test(`sha256:${value.sbom.sha256}`) &&
    digestPattern.test(`sha256:${value.sbom.predicateSha256}`),
  'Invalid SPDX evidence');
  requireThat(value.signature?.sha256 === sha256(value.signature.bytes) &&
    value.sbom?.sha256 === sha256(value.sbom.bytes) &&
    value.sbom?.predicateSha256 === sha256(value.sbom.predicate) &&
    value.signature.bundleSha256 === sha256(value.signature.bundle) &&
    value.sbom.bundleSha256 === sha256(value.sbom.bundle), 'Evidence bytes digest mismatch');
  validateVerification(value.signature.bytes, digest, 'signature');
  validateVerification(value.sbom.bytes, digest, 'attestation', value.sbom.predicate);
  const signatureBundle = parseJson(value.signature.bundle, 'signature bundle');
  const signatureVerification = verificationEntries(value.signature.bytes, digest, 'signature');
  const attestationVerification = verificationEntries(value.sbom.bytes, digest, 'attestation', value.sbom.predicate);
  const attestationBundle = parseJson(value.sbom.bundle, 'attestation bundle');
  validateSignatureDownload(signatureBundle, signatureVerification, digest);
  validateDsseBundle(attestationBundle, digest, value.sbom.predicate, attestationVerification);
  requireThat((Array.isArray(signatureBundle) ? signatureBundle.length : 1) === signatureVerification.length &&
    (Array.isArray(attestationBundle) ? attestationBundle.length : 1) === attestationVerification.length,
  'Cosign verification/download entry count mismatch');
  validateBundleTrust(signatureBundle, trust, signatureVerification);
  validateBundleTrust(attestationBundle, trust, attestationVerification);
  validateBundleTrust(signatureVerification, trust);
  validateBundleTrust(attestationVerification, trust);
}

export function stageEvidence(evidencePath, completeSet, collected, trust) {
  const set = { schema: 1, ...(trust ? {
    createdTime: trust.createdTime, verificationTime: trust.trustedTime,
  } : {}), services: {} };
  for (const [service, image] of Object.entries(completeSet.images)) {
    const value = collected[service];
    requireThat(value, `Missing evidence: ${service}`);
    set.services[service] = {
      index: normalizeEvidence({ subject: image.digest, trust, ...value.index }),
      platforms: Object.fromEntries(Object.entries(image.platforms).map(([platform, item]) => [
        platform, normalizeEvidence({ subject: item.digest, platform, trust, ...value.platforms?.[platform] }),
      ])),
    };
  }
  validateEvidenceSet(set, completeSet, trust);
  mkdirSync(dirname(evidencePath), { recursive: true });
  const serialized = canonicalEvidenceSet(set);
  writeFileSync(evidencePath, serialized);
  return { set, sha256: sha256(serialized) };
}

function readEvidenceFile(path) {
  try {
    const bytes = readFileSync(path, 'utf8');
    requireThat(bytes.length > 0, `Missing evidence file: ${path}`);
    return bytes;
  } catch {
    throw new Error(`Missing evidence file: ${path}`);
  }
}

export function stageEvidenceFromFiles(evidencePath, completeSet, root, trust) {
  const evidence = (service, scope) => ({
    signatureBytes: readEvidenceFile(join(root, service, scope, 'signature.json')),
    attestationBytes: readEvidenceFile(join(root, service, scope, 'attestation.json')),
    predicateBytes: readEvidenceFile(join(root, service, scope, 'predicate.json')),
    signatureBundleBytes: readEvidenceFile(join(root, service, scope, 'signature.bundle.json')),
    attestationBundleBytes: readEvidenceFile(join(root, service, scope, 'attestation.bundle.json')),
  });
  const collected = Object.fromEntries(Object.entries(completeSet.images).map(([service, image]) => [
    service,
    {
      index: evidence(service, 'index'),
      platforms: Object.fromEntries(Object.keys(image.platforms).map(platform => [
        platform,
        evidence(service, join('platforms', platform.replace('/', '-'))),
      ])),
    },
  ]));
  return stageEvidence(evidencePath, completeSet, collected, trust);
}

export function readEvidence(evidencePath, completeSet, trust) {
  const bytes = readEvidenceFile(evidencePath);
  const set = validateEvidenceSet(parseJson(bytes, 'release evidence'), completeSet, trust);
  requireThat(bytes === canonicalEvidenceSet(set), 'Release evidence bytes are not canonical');
  return { set, sha256: sha256(bytes), bytes };
}
