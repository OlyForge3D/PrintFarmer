import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import test from 'node:test';
import {
  ReleasePolicyError, loadReleaseTrustPolicy, releaseMetadataEvidence, sha256Bytes,
} from '../release-policy.mjs';

const root = resolve('.artifacts', `release-source-metadata-${process.pid}`);
const metadataPath = resolve(root, 'source-release-trust-policy.json');
const stagedMetadataPath = '.artifacts/release-authorization/source-release-metadata.json';
const sourceCommit = 'a'.repeat(40);

function metadataAndArtifacts() {
  const bytes = readFileSync('release-metadata/0.2.3.json', 'utf8');
  const metadata = JSON.parse(bytes);
  return {
    bytes,
    artifacts: Object.fromEntries(Object.values(metadata.schemas).map(schema => [
      schema.artifact, readFileSync(schema.artifact),
    ])),
  };
}

test('qualified source metadata binds exact canonical bytes, source commit, and named artifact digests', t => {
  const { bytes, artifacts } = metadataAndArtifacts();
  mkdirSync(resolve('.artifacts/release-authorization'), { recursive: true });
  writeFileSync(stagedMetadataPath, bytes);
  t.after(() => rmSync(stagedMetadataPath, { force: true }));

  const record = { sourceCommit, baseVersion: '0.2.3' };
  const evidence = releaseMetadataEvidence(record, bytes, artifacts);
  assert.equal(evidence.sourceCommit, sourceCommit);
  assert.equal(evidence.sha256, sha256Bytes(bytes));
  assert.throws(() => releaseMetadataEvidence(record, bytes, artifacts, 'b'.repeat(40)), ReleasePolicyError);
  const changedBytes = `${bytes} `;
  assert.throws(() => releaseMetadataEvidence(record, changedBytes, artifacts), ReleasePolicyError);
  const changedArtifacts = { ...artifacts };
  changedArtifacts['scripts/docker/configs/security-config.json'] = Buffer.from('altered');
  assert.throws(() => releaseMetadataEvidence(record, bytes, changedArtifacts), ReleasePolicyError);
});

test('trust policy rejects non-canonical timestamps, duplicate revocations, and revoked signers', t => {
  mkdirSync(root, { recursive: true });
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const policy = JSON.parse(readFileSync('release-trust-policy.json', 'utf8'));
  for (const mutate of [
    value => { value.signers[0].validFrom = '2026-01-01T00:00:00Z'; },
    value => { value.revokedReleaseIds = ['stable:0.2.3', 'stable:0.2.3']; },
    value => { value.revokedSignerIdentities = [value.signers[0].identity]; },
  ]) {
    const candidate = structuredClone(policy);
    mutate(candidate);
    writeFileSync(metadataPath, JSON.stringify(candidate));
    assert.throws(() => loadReleaseTrustPolicy(metadataPath), ReleasePolicyError);
  }
});
