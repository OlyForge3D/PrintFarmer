import { createHash } from 'node:crypto';
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
  try { return JSON.parse(bytes); } catch { throw new Error(`Malformed ${label}`); }
}

function canonicalJson(value) {
  if (Array.isArray(value)) return value.map(canonicalJson);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonicalJson(value[key])]));
  }
  return value;
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

function validateVerification(bytes, expectedDigest, type, expectedPredicate) {
  requireThat(typeof bytes === 'string' && bytes.length > 0, `Missing ${type} bytes`);
  requireThat(digestPattern.test(expectedDigest), `Invalid ${type} subject`);
  const entries = subjects(parseJson(bytes, type), type);
  requireThat(entries.every(entry => entry.subject === expectedDigest), `${type} subject mismatch`);
  if (expectedPredicate !== undefined) {
    const parsedPredicate = parseJson(expectedPredicate, 'SPDX predicate');
    requireThat(entries.some(entry =>
      JSON.stringify(canonicalJson(entry.predicate)) === JSON.stringify(canonicalJson(parsedPredicate))),
      'SPDX predicate mismatch');
  }
}

function evidenceObject(subject, signatureBytes, attestationBytes, predicateBytes, platform) {
  validateVerification(signatureBytes, subject, 'signature');
  validateVerification(attestationBytes, subject, 'attestation', predicateBytes);
  return {
    subject, ...(platform === undefined ? {} : { platform }),
    signature: { sha256: sha256(signatureBytes), bytes: signatureBytes },
    sbom: { sha256: sha256(attestationBytes), predicateSha256: sha256(predicateBytes), predicate: predicateBytes,
      bytes: attestationBytes },
  };
}

export function normalizeEvidence({ subject, signatureBytes, attestationBytes, predicateBytes, platform }) {
  requireThat(platform === undefined || /^linux\/(?:amd64|arm64)$/.test(platform), 'Invalid evidence platform');
  return evidenceObject(subject, signatureBytes, attestationBytes, predicateBytes, platform);
}

export function validateEvidenceSet(set, completeSet) {
  requireThat(set?.schema === 1 && typeof set.services === 'object', 'Invalid release evidence set');
  for (const [service, image] of Object.entries(completeSet.images)) {
    const entry = set.services?.[service];
    requireThat(entry && Object.keys(entry).sort().join() === 'index,platforms', 'Incomplete release evidence');
    validateStored(entry.index, image.digest);
    requireThat(Object.keys(entry.platforms).sort().join() === Object.keys(image.platforms).sort().join(),
      'Evidence platform mapping mismatch');
    for (const [platform, value] of Object.entries(image.platforms)) {
      validateStored(entry.platforms[platform], value.digest, platform);
    }
  }
  requireThat(Object.keys(set.services).sort().join() === Object.keys(completeSet.images).sort().join(),
    'Unexpected release evidence service');
  return set;
}

function validateStored(value, digest, platform) {
  const expectedKeys = platform === undefined
    ? ['subject', 'signature', 'sbom']
    : ['platform', 'subject', 'signature', 'sbom'];
  requireThat(value && typeof value === 'object' &&
    Object.keys(value).sort().join() === expectedKeys.sort().join(), 'Invalid evidence entry');
  requireThat(value?.subject === digest && value?.platform === platform, 'Evidence subject/platform mismatch');
  requireThat(value.signature && typeof value.signature === 'object' &&
    Object.keys(value.signature).sort().join() === 'bytes,sha256' &&
    typeof value.signature.bytes === 'string' && digestPattern.test(`sha256:${value.signature.sha256}`),
  'Invalid signature bundle evidence');
  requireThat(value.sbom && typeof value.sbom === 'object' &&
    Object.keys(value.sbom).sort().join() === 'bytes,predicate,predicateSha256,sha256' &&
    typeof value.sbom.bytes === 'string' && typeof value.sbom.predicate === 'string' &&
    digestPattern.test(`sha256:${value.sbom.sha256}`) &&
    digestPattern.test(`sha256:${value.sbom.predicateSha256}`),
  'Invalid SPDX evidence');
  requireThat(value.signature?.sha256 === sha256(value.signature.bytes) &&
    value.sbom?.sha256 === sha256(value.sbom.bytes) &&
    value.sbom?.predicateSha256 === sha256(value.sbom.predicate), 'Evidence bytes digest mismatch');
  validateVerification(value.signature.bytes, digest, 'signature');
  validateVerification(value.sbom.bytes, digest, 'attestation', value.sbom.predicate);
}

export function stageEvidence(evidencePath, completeSet, collected) {
  const set = { schema: 1, services: {} };
  for (const [service, image] of Object.entries(completeSet.images)) {
    const value = collected[service];
    requireThat(value, `Missing evidence: ${service}`);
    set.services[service] = {
      index: normalizeEvidence({ subject: image.digest, ...value.index }),
      platforms: Object.fromEntries(Object.entries(image.platforms).map(([platform, item]) => [
        platform, normalizeEvidence({ subject: item.digest, platform, ...value.platforms?.[platform] }),
      ])),
    };
  }
  validateEvidenceSet(set, completeSet);
  mkdirSync(dirname(evidencePath), { recursive: true });
  const serialized = JSON.stringify(set);
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

export function stageEvidenceFromFiles(evidencePath, completeSet, root) {
  const evidence = (service, scope) => ({
    signatureBytes: readEvidenceFile(join(root, service, scope, 'signature.json')),
    attestationBytes: readEvidenceFile(join(root, service, scope, 'attestation.json')),
    predicateBytes: readEvidenceFile(join(root, service, scope, 'predicate.json')),
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
  return stageEvidence(evidencePath, completeSet, collected);
}

export function readEvidence(evidencePath, completeSet) {
  const bytes = readEvidenceFile(evidencePath);
  const set = validateEvidenceSet(parseJson(bytes, 'release evidence'), completeSet);
  return { set, sha256: sha256(bytes), bytes };
}
