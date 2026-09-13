import type { CanonicalReleaseIdentity, PromotionOrigin } from '@/types/api';

/** Copies the release authority's build record. Reject inconsistent commit bindings; never derive identity. */
export function readReleaseIdentity(value: string | undefined, sourceCommit: string): CanonicalReleaseIdentity | null {
  if (!value) return null;
  const record: unknown = JSON.parse(value);
  if (!record || typeof record !== 'object' || Array.isArray(record)) throw new Error('Release identity must be an object.');
  const input = record as Record<string, unknown>;
  const keys = ['canonicalVersion', 'baseVersion', 'channel', 'releaseId', 'sourceTag', 'sourceBranch',
    'sourceCommit', 'authorizedBranchHead', 'buildId', 'buildAttempt', 'workflowIdentity', 'allocationIdentity'] as const;
  for (const key of keys) {
    if (typeof input[key] !== 'string' || !input[key]) throw new Error(`Release identity is missing ${key}.`);
  }
  if (input.sourceCommit !== sourceCommit || input.authorizedBranchHead !== sourceCommit) {
    throw new Error('Release identity does not bind the exact frontend source commit.');
  }
  // Do not copy arbitrary fields, evidence paths or purported verification flags into public assets.
  const identity = Object.fromEntries(keys.map(key => [key, input[key]]));
  let promotionOrigin: PromotionOrigin | null = null;
  if (input.promotionOrigin != null) {
    if (typeof input.promotionOrigin !== 'object' || Array.isArray(input.promotionOrigin)) throw new Error('Invalid promotion origin.');
    const promotion = input.promotionOrigin as Record<string, unknown>;
    const promotionKeys = ['releaseId', 'canonicalVersion', 'sourceCommit', 'manifestDigest', 'evidence'] as const;
    if (promotionKeys.some(key => typeof promotion[key] !== 'string' || !promotion[key])) throw new Error('Incomplete promotion origin.');
    promotionOrigin = Object.fromEntries(promotionKeys.map(key => [key, promotion[key]])) as unknown as PromotionOrigin;
  }
  return { ...identity, promotionOrigin } as unknown as CanonicalReleaseIdentity;
}
