import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { readReleaseIdentity } from '@/common/utils/releaseIdentity';
import { commit, identity } from '@/test/features/system/serviceInventoryFixture';

const buildIdentity = { ...identity, stableSequence: '0' };

describe('shared release identity consumer', () => {
  it('leaves native/legacy asset association unknown', () => { expect(readReleaseIdentity(undefined, commit)).toBeNull(); });
  it('copies the build authority record and retains full binding without derivation', () => {
    expect(readReleaseIdentity(JSON.stringify(buildIdentity), commit)).toEqual(buildIdentity);
  });
  it('rejects cached or mismatched source SHA and missing authorization fields', () => {
    expect(() => readReleaseIdentity(JSON.stringify(buildIdentity), 'b'.repeat(40))).toThrow(/exact frontend source commit/);
    expect(() => readReleaseIdentity(JSON.stringify({ ...buildIdentity, authorizedBranchHead: null }), commit)).toThrow(/authorizedBranchHead/);
  });
  it('rejects the inventory API identity because build authority requires stable sequence evidence', () => {
    const value = readFileSync(resolve(process.cwd(), '../../../fixtures/service-inventory/canonical-release-identity.json'), 'utf8');
    expect(() => readReleaseIdentity(value, commit)).toThrow(/stableSequence/);
  });
  it('preserves promotion provenance when included in the authority record', () => {
    const promotionOrigin = { releaseId: 'insider:1.2.3-insider.9', canonicalVersion: '1.2.3-insider.9',
      sourceCommit: 'b'.repeat(40), manifestDigest: `sha256:${'c'.repeat(64)}`, evidence: 'qualification-9' };
    expect(readReleaseIdentity(JSON.stringify({ ...buildIdentity, promotionOrigin }), commit)?.promotionOrigin).toEqual(promotionOrigin);
  });
  it('does not expose arbitrary fields or turn claimed verification into attestation', () => {
    const result = readReleaseIdentity(JSON.stringify({ ...buildIdentity, hostPath: '/private', verified: true }), commit);
    expect(result).not.toHaveProperty('hostPath');
    expect(result).not.toHaveProperty('verified');
  });
});
