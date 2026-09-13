import {
  closeSync, constants, fchmodSync, fstatSync, ftruncateSync, lstatSync,
  mkdirSync, openSync, readFileSync, writeFileSync,
} from 'node:fs';
import { join } from 'node:path';
import {
  requireThat, validateCompleteSet, validateRecord, publicAuthorization, validatePublicAuthorization, writePublicSet,
} from './release-policy.mjs';
export { publicAuthorization, writePublicSet } from './release-policy.mjs';

export const authorizationDirectory = '.artifacts/release-authorization';
export const authorizationPath = `${authorizationDirectory}/release-identity.json`;
export const authorizationBundle = `${authorizationDirectory}/release-identity.bundle.json`;
export const privateSetPath = `${authorizationDirectory}/release-set.json`;

function writeAuthorizationFile(path, content) {
  requireThat([authorizationPath, privateSetPath, 'release-identity.json',
    `${authorizationDirectory}/public-identity.json`].includes(path), 'Invalid authorization destination');
  if (path !== 'release-identity.json') {
    for (const directory of ['.artifacts', authorizationDirectory]) {
      if (!lstatSync(directory, { throwIfNoEntry: false })) mkdirSync(directory, { mode: 0o700 });
      const info = lstatSync(directory);
      requireThat(info.isDirectory() && !info.isSymbolicLink(), 'Authorization directory must not be linked');
    }
  }
  const previous = lstatSync(path, { throwIfNoEntry: false });
  requireThat(!previous || (previous.isFile() && !previous.isSymbolicLink() && previous.nlink === 1),
    'Authorization destination must be a single-link regular file');
  const mode = path === 'release-identity.json' ? 0o644 : 0o600;
  const descriptor = openSync(path, constants.O_WRONLY | constants.O_CREAT | constants.O_NOFOLLOW, mode);
  try {
    const actual = fstatSync(descriptor);
    requireThat(actual.isFile() && actual.nlink === 1 &&
      (!previous || (actual.ino === previous.ino && actual.dev === previous.dev)),
    'Authorization destination changed');
    fchmodSync(descriptor, mode);
    ftruncateSync(descriptor, 0);
    writeFileSync(descriptor, content);
  } finally {
    closeSync(descriptor);
  }
}

export function validateAuthorizationRecord(record) {
  validateRecord(record);
}

export function writeAuthorization(record) {
  validateAuthorizationRecord(record);
  const projection = JSON.stringify(publicAuthorization(record));
  writeAuthorizationFile(authorizationPath, JSON.stringify(record));
  writeAuthorizationFile('release-identity.json', projection);
  writeAuthorizationFile(`${authorizationDirectory}/public-identity.json`, projection);
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
  requireThat([authorizationPath, privateSetPath].includes(path), 'Invalid authorization source');
  try { return JSON.parse(readFileSync(path, 'utf8')); }
  catch { throw new Error('Private authorization unavailable or malformed'); }
}

export function writeAuthorizationSet(record, set) {
  validateAuthorizationRecord(record);
  validateCompleteSet(record, set);
  const normalized = { ...writePublicSet(record, set), identity: record };
  writeAuthorizationFile(privateSetPath, JSON.stringify(normalized));
}

export function emitPublicReleaseAssets(record, set, root = '.') {
  const projected = writePublicSet(record, set);
  const directory = join(root, 'release-assets');
  mkdirSync(directory, { recursive: true });
  writeFileSync(join(directory, 'release-identity.json'), JSON.stringify(projected.identity));
  writeFileSync(join(directory, 'release-set.json'), JSON.stringify(projected));
}
