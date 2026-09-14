import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { normalizeEvidence, readEvidence, stageEvidence } from '../release-evidence.mjs';

const digest = `sha256:${'a'.repeat(64)}`;
const other = `sha256:${'b'.repeat(64)}`;
const platformDigest = `sha256:${'c'.repeat(64)}`;
const predicate = { SPDXID: 'SPDXRef-DOCUMENT' };
function signed(subject = digest, predicateValue = predicate) {
  return {
    signatureBytes: JSON.stringify([{ critical: { image: { 'docker-manifest-digest': subject } } }]),
    attestationBytes: JSON.stringify([{ payload: Buffer.from(JSON.stringify({
      subject: [{ digest: { sha256: subject.slice(7) } }], predicate: predicateValue,
    })).toString('base64') }]),
    predicateBytes: JSON.stringify(predicateValue),
    signatureBundleBytes: JSON.stringify([{ payload: 'signed-payload', optional: { Bundle: { Payload: { integratedTime: 1780000000 } } } }]),
    attestationBundleBytes: JSON.stringify([{ payload: 'dsse-payload', optional: { Bundle: { Payload: { integratedTime: 1780000000 } } } }]),
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
