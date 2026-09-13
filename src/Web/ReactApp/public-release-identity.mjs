export const publicIdentityFields = Object.freeze([
  'releaseId', 'channel', 'canonicalVersion', 'baseVersion', 'sourceBranch',
  'sourceTag', 'sourceCommit', 'authorizedBranchHead', 'buildId', 'buildAttempt',
  'workflowIdentity',
]);

export function publicIdentity(record) {
  if (!record || typeof record !== 'object' || Array.isArray(record)) {
    throw new Error('Invalid public identity object');
  }
  const result = {};
  for (const field of [...publicIdentityFields, 'identitySha256', 'buildTime']) {
    if (!Object.hasOwn(record, field)) continue;
    if (typeof record[field] !== 'string' || !record[field] || /[\r\n]/.test(record[field])) {
      throw new Error(`Invalid public identity field: ${field}`);
    }
    result[field] = record[field];
  }
  return result;
}
