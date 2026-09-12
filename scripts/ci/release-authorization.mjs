import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { hash, identityLabels, requireThat, validateCompleteSet, verifyProtectionEvidence } from './release-policy.mjs';
import { publicIdentity, publicIdentityFields } from '../../src/Web/ReactApp/public-release-identity.mjs';

export const authorizationDirectory = '.artifacts/release-authorization';
export const authorizationPath = `${authorizationDirectory}/release-identity.json`;
export const authorizationBundle = `${authorizationDirectory}/release-identity.bundle.json`;
export const privateSetPath = `${authorizationDirectory}/release-set.json`;

export function validateAuthorizationRecord(record) {
  const fields = ['schema', 'repository', 'channel', 'baseVersion', 'sourceBranch', 'stage',
    'sourceCommit', 'authorizedBranchHead', 'buildId', 'buildAttempt', 'workflowIdentity',
    'workflowCommit', 'releaseId', 'canonicalVersion', 'sourceTag', 'sequence', 'allocationKey',
    'created', 'protection', 'qualification'];
  requireThat(record && !Array.isArray(record) && Object.keys(record).every(field => fields.includes(field)),
    'Unknown authorization field');
  requireThat(record.schema === 1, 'Invalid authorization schema');
  for (const field of fields.filter(field => !['schema', 'protection', 'qualification'].includes(field))) {
    requireThat((['stage', 'sequence'].includes(field) && record[field] === undefined) ||
      (typeof record[field] === 'string' && record[field].length > 0 && !/[\r\n]/.test(record[field])),
    'Invalid authorization field');
  }
  verifyProtectionEvidence(record.protection, record.channel);
  requireThat(Number.isFinite(Date.parse(record.created)) &&
    Date.parse(record.protection.verifiedAt) <= Date.parse(record.created),
  'Protection attestation postdates authorization');
  if (record.channel === 'stable') {
    const qualification = record.qualification;
    requireThat(qualification && Object.keys(qualification).sort().join() ===
      ['sourceCommit', 'reviewed', 'tests', 'compatibility', 'migrations', 'recovery', 'mode'].sort().join() &&
      qualification.sourceCommit === record.sourceCommit && qualification.reviewed === true &&
      ['tests', 'compatibility', 'migrations', 'recovery'].every(field => qualification[field] === 'passed') &&
      ['promotion', 'hotfix'].includes(qualification.mode), 'Invalid normalized stable qualification');
  } else {
    requireThat(record.qualification === undefined, 'Unexpected stable qualification');
  }
}

export function publicAuthorization(record) {
  const identity = publicIdentity(record);
  requireThat(typeof record.created === 'string' && Number.isFinite(Date.parse(record.created)) &&
    !/[\r\n]/.test(record.created), 'Invalid authorization timestamp');
  return { ...Object.fromEntries(publicIdentityFields.filter(field => identity[field] !== undefined)
    .map(field => [field, identity[field]])), buildTime: record.created, identitySha256: hash(record) };
}

export function writeAuthorization(record) {
  validateAuthorizationRecord(record);
  const projection = JSON.stringify(publicAuthorization(record));
  mkdirSync(dirname(authorizationPath), { recursive: true, mode: 0o700 });
  writeFileSync(authorizationPath, JSON.stringify(record), { mode: 0o600 });
  writeFileSync('release-identity.json', projection);
  writeFileSync(`${authorizationDirectory}/public-identity.json`, projection);
}

export function verifyAuthorization(env, run) {
  requireThat(!env.RELEASE_IDENTITY, 'Full identity environment transport is forbidden');
  const projected = JSON.parse(env.RELEASE_PUBLIC_IDENTITY || '{}');
  requireThat(Object.keys(publicIdentity(projected)).sort().join() === Object.keys(projected).sort().join() &&
    /^[a-f0-9]{64}$/.test(projected.identitySha256), 'Invalid public authorization projection');
  const identity = `${env.GITHUB_REPOSITORY}/.github/workflows/consolidated-release.yml@${env.GITHUB_REF}`;
  requireThat(env.GITHUB_REPOSITORY === 'OlyForge3D/PrintFarmer' &&
    ['refs/heads/main', 'refs/heads/development'].includes(env.GITHUB_REF) &&
    projected.workflowIdentity === identity, 'Untrusted authorization signer');
  // Never return command output: verification tools may echo signed payloads.
  try {
    run('cosign', ['verify-blob', '--bundle', authorizationBundle,
      '--certificate-identity', `https://github.com/${identity}`,
      '--certificate-oidc-issuer', 'https://token.actions.githubusercontent.com', authorizationPath]);
  } catch {
    throw new Error('Authorization signature verification failed');
  }
  const record = readPrivateAuthorization();
  requireThat(JSON.stringify(publicAuthorization(record)) === JSON.stringify(projected),
    'Signed authorization differs from public identity/hash');
  validateAuthorizationRecord(record);
  return record;
}

export function readPrivateAuthorization() {
  return readPrivateJson(authorizationPath);
}

export function readPrivateJson(path) {
  try { return JSON.parse(readFileSync(path, 'utf8')); }
  catch { throw new Error('Private authorization unavailable or malformed'); }
}

export function writePublicSet(record, set, labels) {
  const images = Object.fromEntries(Object.entries(set.images).map(([name, image]) => [name, {
    digest: image.digest,
    platforms: Object.fromEntries(Object.entries(image.platforms).map(([platform, value]) => [platform, {
      digest: value.digest,
      labels: Object.fromEntries(Object.keys(labels).map(key => [key, value.labels[key]])),
    }])),
  }]));
  return { schema: 1, identity: publicAuthorization(record), managedEligible: false, images };
}

export function writeAuthorizationSet(record, set) {
  validateAuthorizationRecord(record);
  validateCompleteSet(record, set);
  const normalized = { ...writePublicSet(record, set, identityLabels(record)), identity: record };
  writeFileSync(privateSetPath, JSON.stringify(normalized), { mode: 0o600 });
}

export function emitPublicReleaseAssets(record, set, labels, root = '.') {
  const directory = join(root, 'release-assets');
  mkdirSync(directory, { recursive: true });
  writeFileSync(join(directory, 'release-identity.json'), JSON.stringify(publicAuthorization(record)));
  writeFileSync(join(directory, 'release-set.json'), JSON.stringify(writePublicSet(record, set, labels)));
}
