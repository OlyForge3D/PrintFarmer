import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import {
  requireThat, validateCompleteSet, validateRecord, publicAuthorization, validatePublicAuthorization, writePublicSet,
} from './release-policy.mjs';
export { publicAuthorization, writePublicSet } from './release-policy.mjs';

export const authorizationDirectory = '.artifacts/release-authorization';
export const authorizationPath = `${authorizationDirectory}/release-identity.json`;
export const authorizationBundle = `${authorizationDirectory}/release-identity.bundle.json`;
export const privateSetPath = `${authorizationDirectory}/release-set.json`;

export function validateAuthorizationRecord(record) {
  validateRecord(record);
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
  validatePublicAuthorization(projected);
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

export function writeAuthorizationSet(record, set) {
  validateAuthorizationRecord(record);
  validateCompleteSet(record, set);
  const normalized = { ...writePublicSet(record, set), identity: record };
  writeFileSync(privateSetPath, JSON.stringify(normalized), { mode: 0o600 });
}

export function emitPublicReleaseAssets(record, set, root = '.') {
  const projected = writePublicSet(record, set);
  const directory = join(root, 'release-assets');
  mkdirSync(directory, { recursive: true });
  writeFileSync(join(directory, 'release-identity.json'), JSON.stringify(projected.identity));
  writeFileSync(join(directory, 'release-set.json'), JSON.stringify(projected));
}
