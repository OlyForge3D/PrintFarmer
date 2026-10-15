import { describe, expect, it } from 'vitest';
import { readReleaseIdentity } from '@/common/utils/releaseIdentity';
import type { BuildReleaseIdentity } from '@/common/utils/releaseIdentity';

const commit = 'a'.repeat(40);
const buildIdentity: BuildReleaseIdentity = {
  canonicalVersion: '1.2.3-insider.10',
  baseVersion: '1.2.3',
  channel: 'insider',
  releaseId: 'insider:1.2.3-insider.10',
  sourceTag: 'v1.2.3-insider.10',
  sourceBranch: 'development',
  sourceCommit: commit,
  authorizedBranchHead: commit,
  buildId: '42',
  buildAttempt: '1',
  workflowIdentity: 'release-workflow',
  stableSequence: '0',
  allocationIdentity: 'allocation-10',
  promotionOrigin: null,
};

describe('shared release identity consumer', () => {
  it('leaves native/legacy asset association unknown', () => { expect(readReleaseIdentity(undefined, commit)).toBeNull(); });
  it('copies the build authority record and retains full binding without derivation', () => {
    expect(readReleaseIdentity(JSON.stringify(buildIdentity), commit)).toEqual(buildIdentity);
  });
  it('rejects cached or mismatched source SHA and missing authorization fields', () => {
    expect(() => readReleaseIdentity(JSON.stringify(buildIdentity), 'b'.repeat(40))).toThrow(/exact frontend source commit/);
    expect(() => readReleaseIdentity(JSON.stringify({ ...buildIdentity, authorizedBranchHead: null }), commit)).toThrow(/authorizedBranchHead/);
  });
  it('rejects release records without stable sequence evidence', () => {
    const identityWithoutSequence = { ...buildIdentity, stableSequence: undefined };
    expect(() => readReleaseIdentity(JSON.stringify(identityWithoutSequence), commit)).toThrow(/stableSequence/);
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
