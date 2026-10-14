export interface BuildReleasePromotionOrigin {
  releaseId: string;
  canonicalVersion: string;
  sourceCommit: string;
  manifestDigest: string;
  evidence: string;
}

export interface BuildReleaseIdentity {
  canonicalVersion: string;
  baseVersion: string;
  channel: string;
  releaseId: string;
  sourceTag: string;
  sourceBranch: string;
  sourceCommit: string;
  authorizedBranchHead: string;
  buildId: string;
  buildAttempt: string;
  workflowIdentity: string;
  stableSequence: string;
  allocationIdentity: string;
  promotionOrigin: BuildReleasePromotionOrigin | null;
}

/** Copies the release authority's build record. Reject inconsistent commit bindings; never derive identity. */
export function readReleaseIdentity(value: string | undefined, sourceCommit: string): BuildReleaseIdentity | null {
  if (!value) return null;
  const record: unknown = JSON.parse(value);
  if (!record || typeof record !== 'object' || Array.isArray(record)) throw new Error('Release identity must be an object.');
  const input = record as Record<string, unknown>;
  const keys = ['canonicalVersion', 'baseVersion', 'channel', 'releaseId', 'sourceTag', 'sourceBranch',
    'sourceCommit', 'authorizedBranchHead', 'buildId', 'buildAttempt', 'workflowIdentity', 'stableSequence',
    'allocationIdentity'] as const;
  for (const key of keys) {
    if (typeof input[key] !== 'string' || !input[key]) throw new Error(`Release identity is missing ${key}.`);
  }
  if (input.sourceCommit !== sourceCommit || input.authorizedBranchHead !== sourceCommit) {
    throw new Error('Release identity does not bind the exact frontend source commit.');
  }
  // Do not copy arbitrary fields, evidence paths or purported verification flags into public assets.
  const getString = (key: typeof keys[number]): string => input[key] as string;
  let promotionOrigin: BuildReleasePromotionOrigin | null = null;
  if (input.promotionOrigin != null) {
    if (typeof input.promotionOrigin !== 'object' || Array.isArray(input.promotionOrigin)) throw new Error('Invalid promotion origin.');
    const promotion = input.promotionOrigin as Record<string, unknown>;
    const promotionKeys = ['releaseId', 'canonicalVersion', 'sourceCommit', 'manifestDigest', 'evidence'] as const;
    if (promotionKeys.some(key => typeof promotion[key] !== 'string' || !promotion[key])) throw new Error('Incomplete promotion origin.');
    promotionOrigin = {
      releaseId: promotion.releaseId as string,
      canonicalVersion: promotion.canonicalVersion as string,
      sourceCommit: promotion.sourceCommit as string,
      manifestDigest: promotion.manifestDigest as string,
      evidence: promotion.evidence as string,
    };
  }
  return {
    canonicalVersion: getString('canonicalVersion'),
    baseVersion: getString('baseVersion'),
    channel: getString('channel'),
    releaseId: getString('releaseId'),
    sourceTag: getString('sourceTag'),
    sourceBranch: getString('sourceBranch'),
    sourceCommit: getString('sourceCommit'),
    authorizedBranchHead: getString('authorizedBranchHead'),
    buildId: getString('buildId'),
    buildAttempt: getString('buildAttempt'),
    workflowIdentity: getString('workflowIdentity'),
    stableSequence: getString('stableSequence'),
    allocationIdentity: getString('allocationIdentity'),
    promotionOrigin,
  };
}
