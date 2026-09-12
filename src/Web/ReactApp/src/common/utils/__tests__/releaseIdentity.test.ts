import { describe, expect, it } from 'vitest';
import { readReleaseIdentity } from '@/common/utils/releaseIdentity';
import { commit, identity } from '@/test/features/system/serviceInventoryFixture';

describe('shared release identity consumer', () => {
  it('leaves native/legacy asset association unknown', () => { expect(readReleaseIdentity(undefined, commit)).toBeNull(); });
  it('copies the canonical authority record and retains full binding without derivation', () => {
    expect(readReleaseIdentity(JSON.stringify(identity), commit)).toEqual(identity);
  });
  it('rejects cached or mismatched source SHA and missing authorization fields', () => {
    expect(() => readReleaseIdentity(JSON.stringify(identity), 'b'.repeat(40))).toThrow(/exact frontend source commit/);
    expect(() => readReleaseIdentity(JSON.stringify({ ...identity, authorizedBranchHead: null }), commit)).toThrow(/authorizedBranchHead/);
  });
  it('does not expose arbitrary fields or turn claimed verification into attestation', () => {
    const result = readReleaseIdentity(JSON.stringify({ ...identity, hostPath: '/private', verified: true }), commit);
    expect(result).not.toHaveProperty('hostPath');
    expect(result).not.toHaveProperty('verified');
  });
});
