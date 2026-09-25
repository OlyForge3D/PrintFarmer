// Network-denied update/recovery bundle: assembly and bounded verification (issue #2981, first
// slice).
//
// A bundle carries the release's ORIGINAL signed bytes unchanged -- update-manifest.json, its
// Cosign bundle, the host-update CLI checksum list, its Cosign bundle and the selected CLI
// archives -- inside one flat, uncompressed ustar archive with an unsigned index. The index is a
// convenience for bounded pre-extraction checks only; it never supplies identity or trust. Trust
// comes from re-verifying both release signatures offline against a Sigstore trusted root that the
// operator supplies from outside the bundle, so bundle-supplied signer material cannot enroll
// itself. There is no skip-verification, force or reset option.
//
// Issue #3061: a bundle may also carry every manifest-selected application image and every pinned
// infrastructure image as nested OCI layout archives (offline-bundle-images.mjs). Images are all or
// nothing, verified by digest and platform after the signatures, and loaded only by `load` from a
// verified staging directory -- never pulled or built.
//
// A bundle may also carry the prior recovery set (#3062): the previously verified release's
// original signed manifest and CLI checksum list with their Cosign bundles -- packaged, or bound by
// digest to an operator-supplied local copy -- plus an identity/checksum/location-class reference to
// the protected backup taken before the change. The prior set must be signature-valid for the same
// channel and strictly older than the target. Backup contents and secrets are never carried.
//
// Issue #3063: a bundle may also carry the release's signed host-local recovery instructions
// (offline-recovery-instructions.mjs). They are both-or-neither with their Cosign bundle, verified
// offline and byte-bound to the signed release identity; contents.recoveryInstructions is true only
// when they are present and complete. `import` is the host-local operator path: it requires a
// complete bundle (images, infrastructure and recovery instructions), verifies it, loads only
// verified images and always writes a durable, redacted decision record.
//
// Bundles are still NOT installable: they do not carry replay state, and never enable rollout.
// docs/OFFLINE_UPDATE_RECOVERY.md tracks the remaining delivery.
import { createHash, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import {
  closeSync, constants, fstatSync, fsyncSync, lstatSync, mkdirSync, mkdtempSync, openSync, readSync, realpathSync, renameSync,
  rmdirSync, rmSync, writeSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { deriveSequence, manifestDigest, validateManifest } from './release-manifest.mjs';
import { compareVersions, components, parseTag, repository, requireThat, workflow } from './release-policy.mjs';
import { hostUpdateCliArchiveName, hostUpdateCliRuntimes, hostUpdateCliSbomName, hostUpdateCliSumsBundleName,
  hostUpdateCliSumsName, parseSums, validateHostUpdateCliSbom } from './host-update-cli-package.mjs';
import { applicationImageMember, imageArchiveLimits, infrastructureImageArchivePattern, infrastructureImagesName,
  infrastructureImagesSignatureName, requiredImages, validateInfrastructureImages, verifyImageArchive,
  writeImageArchive } from './offline-bundle-images.mjs';
import { recoveryInstructionsName, recoveryInstructionsSignatureName,
  validateRecoveryInstructions } from './offline-recovery-instructions.mjs';

export const offlineBundleIndexName = 'offline-bundle.json';
export const offlineBundleVerificationName = 'offline-bundle-verification.json';
export const offlineBundleKind = 'printfarmer-offline-bundle';
export const offlineImportDecisionKind = 'printfarmer-offline-import-decision';
const quarantineDirectoryName = '.unverified';
const manifestName = 'update-manifest.json';
const manifestSignatureName = 'update-manifest.sigstore.json';
const oidcIssuer = 'https://token.actions.githubusercontent.com';
const block = 512;
const chunk = 1024 * 1024;
const MiB = 1024 * 1024;

// Bounds are enforced from headers BEFORE any byte is written to disk. Image archives have their
// own per-archive limit here and nested blob/count limits in offline-bundle-images.mjs.
export const offlineBundleLimits = Object.freeze({
  maxBundleBytes: 64 * 1024 * MiB,
  maxMembers: 64,
  maxMetadataBytes: 4 * MiB,
  maxArchiveBytes: 512 * MiB,
  maxImageArchiveBytes: imageArchiveLimits.maxArchiveBytes,
});

const memberNamePattern = /^[A-Za-z0-9][A-Za-z0-9._+-]{0,99}$/;
const windowsReservedNames = /^(con|prn|aux|nul|com[0-9]|lpt[0-9])$/i;
const sha256Pattern = /^[a-f0-9]{64}$/;
const commitPattern = /^[a-f0-9]{40}$/;

export function offlineBundleName(version) {
  parseTag(`v${version}`);
  return `printfarmer-offline-bundle-v${version}.tar`;
}

// Same keyless identity publish-release.mjs verifies immediately before upload.
export function releaseSigningIdentity(channel) {
  requireThat(['stable', 'insider'].includes(channel), 'Offline bundle channel must be stable or insider');
  return `https://github.com/${repository}/${workflow}@refs/heads/${channel === 'stable' ? 'main' : 'development'}`;
}

function requireChannel(channel) {
  requireThat(['stable', 'insider'].includes(channel),
    'An explicit expected channel (stable or insider) is required; it is never defaulted');
  return channel;
}

function requireMemberName(name) {
  requireThat(typeof name === 'string' && name.length > 0, 'Offline bundle member has an empty name');
  requireThat(!name.includes('/') && !name.includes('\\') && !isAbsolute(name) && !/^[A-Za-z]:/.test(name),
    `Offline bundle member is nested or absolute: ${JSON.stringify(name)}`);
  requireThat(memberNamePattern.test(name) && !name.endsWith('.') &&
    !windowsReservedNames.test(name.split('.')[0]), `Offline bundle member name is not allowed: ${JSON.stringify(name)}`);
  return name;
}

function roleLimit(role, limits) {
  if (isImageArchive(role)) return limits.maxImageArchiveBytes;
  return role === 'cli-archive' ? limits.maxArchiveBytes : limits.maxMetadataBytes;
}

// Every member name is fixed by the signed release version, so an index can never introduce an
// unexpected file, role or runtime. Infrastructure archive names follow a fixed pattern here and
// must equal the signed infrastructure image list exactly once it is verified.
export function expectedMembers(version) {
  const members = new Map([
    [manifestName, 'manifest'],
    [manifestSignatureName, 'manifest-signature'],
    [hostUpdateCliSumsName(version), 'cli-sums'],
    [hostUpdateCliSumsBundleName(version), 'cli-sums-signature'],
  ]);
  for (const rid of hostUpdateCliRuntimes) {
    members.set(hostUpdateCliArchiveName(version, rid), 'cli-archive');
    members.set(hostUpdateCliSbomName(version, rid), 'cli-sbom');
  }
  members.set(infrastructureImagesName, 'infrastructure-list');
  members.set(infrastructureImagesSignatureName, 'infrastructure-list-signature');
  members.set(recoveryInstructionsName, 'recovery-instructions');
  members.set(recoveryInstructionsSignatureName, 'recovery-instructions-signature');
  for (const id of Object.keys(components)) members.set(applicationImageMember(id), 'application-image');
  return members;
}

function memberRole(roles, name) {
  return roles.get(name) ?? (infrastructureImageArchivePattern.test(name) ? 'infrastructure-image' : undefined);
}

const isRuntimeMember = role => role === 'cli-archive' || role === 'cli-sbom';
const isImageArchive = role => role === 'application-image' || role === 'infrastructure-image';
const isImageMember = role => isImageArchive(role) || role === 'infrastructure-list' ||
  role === 'infrastructure-list-signature';
const isRecoveryMember = role => role === 'recovery-instructions' || role === 'recovery-instructions-signature';
const isOptionalMember = role => isRuntimeMember(role) || isImageMember(role) || isRecoveryMember(role);

// ---------------------------------------------------------------------------------------------
// Prior recovery set and protected-backup reference (#3062).
// ---------------------------------------------------------------------------------------------
export const priorRecoveryModes = Object.freeze(['packaged', 'local-reference']);
export const protectedBackupLocationClasses = Object.freeze(['host-local', 'attached-volume', 'external-storage']);
const priorMemberPrefix = 'prior-';
const protectedBackupFields = 'id,locationClass,releaseVersion,sha256';
const backupIdPattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;

export const priorMemberName = name => `${priorMemberPrefix}${name}`;

// The prior set is the prior release's original signed metadata; its names are fixed by its version.
export function priorRecoveryFiles(version) {
  return new Map([
    [manifestName, 'prior-manifest'],
    [manifestSignatureName, 'prior-manifest-signature'],
    [hostUpdateCliSumsName(version), 'prior-cli-sums'],
    [hostUpdateCliSumsBundleName(version), 'prior-cli-sums-signature'],
  ]);
}

// Identity, checksum and location class only: a closed field set leaves no place for backup
// contents, credentials, connection strings or paths.
export function validateProtectedBackupReference(value, priorVersion) {
  requireThat(value && typeof value === 'object' && !Array.isArray(value) &&
    Object.keys(value).sort().join() === protectedBackupFields,
  'Protected backup reference must contain exactly id, sha256, locationClass and releaseVersion (no contents, secrets or paths)');
  requireThat(typeof value.id === 'string' && backupIdPattern.test(value.id), 'Protected backup reference id is invalid');
  requireThat(typeof value.sha256 === 'string' && sha256Pattern.test(value.sha256),
    'Protected backup reference checksum must be a lowercase SHA-256');
  requireThat(protectedBackupLocationClasses.includes(value.locationClass),
    'Protected backup reference location class is not supported');
  requireThat(value.releaseVersion === priorVersion,
    `Protected backup reference was not taken for the prior release ${priorVersion}`);
  return { id: value.id, sha256: value.sha256, locationClass: value.locationClass, releaseVersion: value.releaseVersion };
}

// Authenticates a prior set found through `pathOf(originalName)`: same channel, strictly older than
// the target, the signed CLI checksum list names exactly the supported assets, and both Cosign
// bundles verify for the channel's release workflow identity.
function verifyPriorRecoverySet({ pathOf, target, channel, run, trustedRoot, limits }) {
  const manifestBytes = readSmallFile(pathOf(manifestName), 'Prior recovery manifest', limits.maxMetadataBytes);
  let identity;
  try {
    identity = manifestIdentity(manifestBytes, channel);
  } catch (error) {
    throw new Error(`Prior recovery set: ${error.message}`);
  }
  requireThat(identity.sequence < target.sequence && compareVersions(identity.version, target.version) < 0,
    `Prior recovery set ${identity.version} is not strictly older than the target ${target.version}`);
  const sumsBytes = readSmallFile(pathOf(hostUpdateCliSumsName(identity.version)), 'Prior recovery CLI checksum list',
    limits.maxMetadataBytes);
  let entries;
  try {
    entries = parseSums(sumsBytes.toString('utf8'));
  } catch (error) {
    throw new Error(`Prior recovery set CLI checksum list is invalid: ${error.message}`);
  }
  const expected = hostUpdateCliRuntimes.flatMap(rid =>
    [hostUpdateCliArchiveName(identity.version, rid), hostUpdateCliSbomName(identity.version, rid)]);
  requireThat(entries.size === expected.length && expected.every(name => entries.has(name)),
    'Prior recovery set CLI checksum list does not name exactly the supported archives and SBOMs');
  const offline = trustedRoot ? ['--trusted-root', trustedRoot] : [];
  for (const [file, bundle] of [[manifestName, manifestSignatureName],
    [hostUpdateCliSumsName(identity.version), hostUpdateCliSumsBundleName(identity.version)]]) {
    try {
      run('cosign', ['verify-blob', ...offline, '--bundle', pathOf(bundle),
        '--certificate-oidc-issuer', oidcIssuer, '--certificate-identity', releaseSigningIdentity(identity.channel),
        pathOf(file)]);
    } catch (error) {
      throw new Error(`Prior recovery set signature verification failed for ${file}: ${error.message}`);
    }
  }
  return { identity, manifestDigest: manifestDigest(manifestBytes) };
}

function validatePriorIndex(prior, limits) {
  requireThat(prior && typeof prior === 'object' && !Array.isArray(prior) &&
    Object.keys(prior).sort().join() === 'files,manifestDigest,mode,protectedBackup,release',
  'Offline bundle prior recovery set fields are invalid');
  requireThat(priorRecoveryModes.includes(prior.mode), 'Offline bundle prior recovery mode is not supported');
  requireThat(prior.release && typeof prior.release.version === 'string', 'Offline bundle prior recovery release is invalid');
  parseTag(`v${prior.release.version}`);
  requireThat(typeof prior.manifestDigest === 'string' && /^sha256:[a-f0-9]{64}$/.test(prior.manifestDigest),
    'Offline bundle prior recovery manifest digest is invalid');
  const roles = priorRecoveryFiles(prior.release.version);
  requireThat(Array.isArray(prior.files) && prior.files.length === roles.size &&
    prior.files.every(file => file && Object.keys(file).sort().join() === 'name,role,sha256,size' &&
      roles.get(file.name) === file.role && sha256Pattern.test(file.sha256 ?? '') &&
      Number.isSafeInteger(file.size) && file.size >= 0 && file.size <= limits.maxMetadataBytes) &&
    new Set(prior.files.map(file => file.name)).size === roles.size,
  'Offline bundle prior recovery set does not list exactly the prior signed metadata');
  validateProtectedBackupReference(prior.protectedBackup, prior.release.version);
  return prior;
}

// ---------------------------------------------------------------------------------------------
// Release identity: taken only from the signed manifest bytes, and cross-checked against the
// channel policy (validateManifest checks shape; this binds tag/version/channel/branch/sequence).
// ---------------------------------------------------------------------------------------------
export function manifestIdentity(bytes, expectedChannel, expectedVersion) {
  requireChannel(expectedChannel);
  let manifest;
  try {
    manifest = validateManifest(bytes.toString('utf8'));
  } catch (error) {
    throw new Error(`Offline bundle manifest is invalid: ${error.message}`);
  }
  const parsed = parseTag(`v${manifest.version}`);
  requireThat(manifest.tag === `v${manifest.version}`, 'Offline bundle manifest tag/version mismatch');
  requireThat(parsed.channel === manifest.channel && (!parsed.stage || parsed.stage === 'insider'),
    'Offline bundle manifest version does not match its channel');
  requireThat(manifest.channel === expectedChannel,
    `Offline bundle channel ${manifest.channel} does not match the expected ${expectedChannel} channel`);
  requireThat(manifest.sourceBranch === (manifest.channel === 'stable' ? 'main' : 'development'),
    'Offline bundle manifest source branch does not match its channel');
  requireThat(commitPattern.test(manifest.sourceCommit), 'Offline bundle manifest source commit is invalid');
  requireThat(typeof manifest.buildId === 'string' && /^[1-9][0-9]*$/.test(manifest.buildId), 'Offline bundle manifest build ID is invalid');
  requireThat(manifest.sequence === deriveSequence(manifest.version), 'Offline bundle manifest sequence mismatch');
  if (expectedVersion !== undefined) {
    requireThat(manifest.version === expectedVersion,
      `Offline bundle version ${manifest.version} does not match the expected ${expectedVersion}`);
  }
  return {
    tag: manifest.tag, version: manifest.version, channel: manifest.channel, sourceBranch: manifest.sourceBranch,
    sourceCommit: manifest.sourceCommit, buildId: manifest.buildId, sequence: manifest.sequence,
  };
}

// The signed checksum list names every runtime's archive and SPDX SBOM (#3045). Each carried runtime
// must bring both, byte-identical to the signed entries, and each SBOM must be structurally valid.
function verifyCliSums(bytes, version, carried, readMember) {
  let entries;
  try {
    entries = parseSums(bytes.toString('utf8'));
  } catch (error) {
    throw new Error(`Offline bundle CLI checksum list is invalid: ${error.message}`);
  }
  const expected = hostUpdateCliRuntimes.flatMap(rid =>
    [hostUpdateCliArchiveName(version, rid), hostUpdateCliSbomName(version, rid)]);
  requireThat(entries.size === expected.length && expected.every(name => entries.has(name)),
    'Offline bundle CLI checksum list does not name exactly the supported archives and SBOMs');
  const names = new Set(carried.map(file => file.name));
  const runtimes = hostUpdateCliRuntimes.filter(rid => names.has(hostUpdateCliArchiveName(version, rid)));
  requireThat(runtimes.length > 0, 'Offline bundle carries no host-update CLI archive');
  for (const rid of hostUpdateCliRuntimes) {
    requireThat(names.has(hostUpdateCliArchiveName(version, rid)) === names.has(hostUpdateCliSbomName(version, rid)),
      `Offline bundle must carry the host-update CLI archive and SBOM together: ${rid}`);
  }
  for (const { name, role, sha256 } of carried) {
    requireThat(entries.get(name) === sha256, `Offline bundle CLI asset does not match the signed checksum list: ${name}`);
    if (role === 'cli-sbom') {
      try {
        validateHostUpdateCliSbom(readMember(name).toString('utf8'), name);
      } catch (error) {
        throw new Error(`Offline bundle ${error.message}`);
      }
    }
  }
  return runtimes;
}

// Cosign authenticates the exact bytes and the channel's release workflow identity. When a trusted
// root is given, verification is fully offline; there is no fallback to online verification.
function verifySignatures(directory, identity, { run, trustedRoot, version, images = false, instructions = false }) {
  const offline = trustedRoot ? ['--trusted-root', trustedRoot] : [];
  for (const [file, bundle] of [[manifestName, manifestSignatureName],
    [hostUpdateCliSumsName(version), hostUpdateCliSumsBundleName(version)],
    ...(images ? [[infrastructureImagesName, infrastructureImagesSignatureName]] : []),
    ...(instructions ? [[recoveryInstructionsName, recoveryInstructionsSignatureName]] : [])]) {
    try {
      run('cosign', ['verify-blob', ...offline, '--bundle', join(directory, bundle),
        '--certificate-oidc-issuer', oidcIssuer, '--certificate-identity', releaseSigningIdentity(identity.channel),
        join(directory, file)]);
    } catch (error) {
      throw new Error(`Offline bundle signature verification failed for ${file}: ${error.message}`);
    }
  }
}

// ---------------------------------------------------------------------------------------------
// Minimal deterministic ustar writer and a strict reader that only accepts what it writes.
// ---------------------------------------------------------------------------------------------
function octalField(value, width) {
  const text = value.toString(8);
  requireThat(text.length <= width - 1, 'Offline bundle tar field overflow');
  return `${text.padStart(width - 1, '0')}\0`;
}

function headerChecksum(header) {
  let sum = 0;
  for (let index = 0; index < block; index += 1) sum += index >= 148 && index < 156 ? 0x20 : header[index];
  return sum;
}

// Exported so tests can forge malicious headers; the writer only ever emits type '0'.
export function tarHeader({ name, size, type = '0', mode = 0o644, linkname = '', prefix = '',
  magic = 'ustar\0', version = '00' }) {
  const header = Buffer.alloc(block);
  header.write(name, 0, 100, 'utf8');
  header.write(octalField(mode, 8), 100, 'ascii');
  header.write(octalField(0, 8), 108, 'ascii');
  header.write(octalField(0, 8), 116, 'ascii');
  header.write(octalField(size, 12), 124, 'ascii');
  header.write(octalField(0, 12), 136, 'ascii');
  header.write(type, 156, 1, 'ascii');
  header.write(linkname, 157, 100, 'utf8');
  header.write(magic, 257, 6, 'ascii');
  header.write(version, 263, 2, 'ascii');
  header.write('root', 265, 32, 'ascii');
  header.write('root', 297, 32, 'ascii');
  header.write(octalField(0, 8), 329, 'ascii');
  header.write(octalField(0, 8), 337, 'ascii');
  header.write(prefix, 345, 155, 'utf8');
  header.write(`${headerChecksum(header).toString(8).padStart(6, '0')}\0 `, 148, 8, 'ascii');
  return header;
}

function isZero(buffer) {
  return buffer.every(byte => byte === 0);
}

function nulTerminated(field, label) {
  const end = field.indexOf(0);
  const text = field.subarray(0, end === -1 ? field.length : end);
  requireThat(end === -1 || isZero(field.subarray(end)), `Offline bundle tar ${label} field is malformed`);
  requireThat(text.every(byte => byte >= 0x20 && byte < 0x7f), `Offline bundle tar ${label} is not printable ASCII`);
  return text.toString('ascii');
}

function readExactly(fd, length, position) {
  const buffer = Buffer.alloc(length);
  let done = 0;
  while (done < length) {
    const read = readSync(fd, buffer, done, length - done, position + done);
    requireThat(read > 0, 'Offline bundle is truncated');
    done += read;
  }
  return buffer;
}

const typeNames = { 1: 'hard link', 2: 'symbolic link', 3: 'character device', 4: 'block device', 5: 'directory',
  6: 'FIFO', 7: 'contiguous file', x: 'extended header', g: 'global extended header', L: 'long name',
  K: 'long link name', '\0': 'legacy regular file' };

function parseHeader(header) {
  requireThat(header.subarray(257, 263).toString('latin1') === 'ustar\0' &&
    header.subarray(263, 265).toString('latin1') === '00', 'Offline bundle member is not a POSIX ustar entry');
  const stored = header.subarray(148, 156).toString('latin1');
  requireThat(/^[0-7]{6}\0 $/.test(stored) && Number.parseInt(stored.slice(0, 6), 8) === headerChecksum(header),
    'Offline bundle tar header checksum mismatch');
  const type = String.fromCharCode(header[156]);
  requireThat(type === '0', `Offline bundle member type is not allowed: ${typeNames[type] ?? JSON.stringify(type)}`);
  requireThat(isZero(header.subarray(157, 257)), 'Offline bundle member carries a link target');
  requireThat(isZero(header.subarray(345, 500)), 'Offline bundle member uses a path prefix');
  const name = requireMemberName(nulTerminated(header.subarray(0, 100), 'name'));
  requireThat(header.subarray(100, 108).toString('latin1') === '0000644\0', `Offline bundle member mode is not 0644: ${name}`);
  const sizeField = header.subarray(124, 136).toString('latin1');
  requireThat(/^[0-7]{11}\0$/.test(sizeField), `Offline bundle member size is malformed: ${name}`);
  return { name, size: Number.parseInt(sizeField.slice(0, 11), 8) };
}

// Parses every header and proves the archive shape (bounded counts/sizes, unique names, zero
// padding, a proper end marker and nothing after it) without writing anything.
export function readOfflineBundleEntries(fd, fileSize, limits = offlineBundleLimits) {
  requireThat(fileSize <= limits.maxBundleBytes, 'Offline bundle exceeds the maximum bundle size');
  requireThat(fileSize >= block * 3 && fileSize % block === 0, 'Offline bundle is not a complete tar archive');
  const entries = [];
  const seen = new Set();
  let offset = 0;
  for (;;) {
    requireThat(offset + block <= fileSize, 'Offline bundle is truncated before its end marker');
    const header = readExactly(fd, block, offset);
    if (isZero(header)) {
      requireThat(offset + 2 * block <= fileSize && isZero(readExactly(fd, block, offset + block)),
        'Offline bundle end marker is incomplete');
      for (let tail = offset + 2 * block; tail < fileSize; tail += chunk) {
        requireThat(isZero(readExactly(fd, Math.min(chunk, fileSize - tail), tail)),
          'Offline bundle has data after its end marker');
      }
      break;
    }
    requireThat(entries.length < limits.maxMembers, 'Offline bundle has too many members');
    const { name, size } = parseHeader(header);
    requireThat(!seen.has(name.toLowerCase()), `Offline bundle has a duplicate or conflicting member: ${name}`);
    seen.add(name.toLowerCase());
    const dataOffset = offset + block;
    const padded = Math.ceil(size / block) * block;
    requireThat(dataOffset + padded <= fileSize, `Offline bundle member is truncated: ${name}`);
    if (padded > size) {
      requireThat(isZero(readExactly(fd, padded - size, dataOffset + size)), `Offline bundle member padding is not zero: ${name}`);
    }
    entries.push({ name, size, offset: dataOffset });
    offset = dataOffset + padded;
  }
  requireThat(entries.length > 0, 'Offline bundle is empty');
  return entries;
}

// Copies `size` bytes from `fd` at `position` to `out` (or only hashes when `out` is undefined).
function copyRange(fd, position, size, out) {
  const hash = createHash('sha256');
  const buffer = Buffer.alloc(Math.min(chunk, Math.max(size, 1)));
  let done = 0;
  while (done < size) {
    const read = readSync(fd, buffer, 0, Math.min(buffer.length, size - done), position + done);
    requireThat(read > 0, 'Offline bundle source ended early');
    hash.update(buffer.subarray(0, read));
    if (out !== undefined) writeAll(out, buffer.subarray(0, read));
    done += read;
  }
  return hash.digest('hex');
}

function writeAll(fd, buffer) {
  let done = 0;
  while (done < buffer.length) done += writeSync(fd, buffer, done, buffer.length - done);
}

function openRegularFile(path, label) {
  const notRegular = `${label} must be a regular file (not a link): ${path}`;
  // Open first, then prove the descriptor is the regular, unlinked file at this path.
  let fd;
  try {
    // O_NONBLOCK keeps a FIFO planted at the path from blocking the open; it does not affect regular files.
    fd = openSync(path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0) | (constants.O_NONBLOCK ?? 0));
  } catch (error) {
    if (['ENOENT', 'ELOOP', 'EISDIR', 'ENOTDIR', 'EMLINK'].includes(error.code)) throw new Error(notRegular);
    throw error;
  }
  try {
    const opened = fstatSync(fd);
    requireThat(opened.isFile(), notRegular);
    const link = lstatSync(path, { throwIfNoEntry: false });
    requireThat(link?.isFile() && !link.isSymbolicLink(), notRegular);
    requireThat(link.dev === opened.dev && link.ino === opened.ino && link.size === opened.size,
      `${label} changed while being opened: ${path}`);
    return { fd, size: opened.size };
  } catch (error) {
    closeSync(fd);
    throw error;
  }
}

function hashFile(path, label) {
  const { fd, size } = openRegularFile(path, label);
  try {
    return { size, sha256: copyRange(fd, 0, size) };
  } finally {
    closeSync(fd);
  }
}

function readSmallFile(path, label, limit) {
  const { fd, size } = openRegularFile(path, label);
  try {
    requireThat(size <= limit, `${label} exceeds its size limit`);
    return readExactly(fd, size, 0);
  } finally {
    closeSync(fd);
  }
}

function indexBytes(index) {
  return Buffer.from(`${JSON.stringify(index, undefined, 2)}\n`);
}

// ---------------------------------------------------------------------------------------------
// Assembly (connected host): reads only local, already-downloaded release assets. Never fetches.
// ---------------------------------------------------------------------------------------------
export function assembleOfflineBundle({ releaseAssets, channel, version, runtimes = hostUpdateCliRuntimes, output,
  run, trustedRoot, images, priorReleaseAssets, priorMode = 'packaged', protectedBackup, limits = offlineBundleLimits,
  imageLimits = imageArchiveLimits }) {
  requireChannel(channel);
  requireThat(typeof run === 'function', 'A command runner is required');
  requireThat(typeof output === 'string' && output.length > 0, 'An output bundle path is required');
  requireThat(Array.isArray(runtimes) && runtimes.length > 0 && new Set(runtimes).size === runtimes.length &&
    runtimes.every(rid => hostUpdateCliRuntimes.includes(rid)), 'Offline bundle runtimes must be distinct supported runtimes');
  requireThat(images === undefined || (typeof images === 'string' && images.length > 0),
    'The image layout directory must be a path');
  requireThat((priorReleaseAssets === undefined) === (protectedBackup === undefined),
    'A prior recovery set and a protected backup reference must be supplied together; neither is complete alone');
  const assets = resolve(releaseAssets);
  const manifestBytes = readSmallFile(join(assets, manifestName), 'Release manifest', limits.maxMetadataBytes);
  const identity = manifestIdentity(manifestBytes, channel, version);
  const selected = new Set(runtimes.flatMap(rid =>
    [hostUpdateCliArchiveName(identity.version, rid), hostUpdateCliSbomName(identity.version, rid)]));
  if (images !== undefined) selected.add(infrastructureImagesName).add(infrastructureImagesSignatureName);
  // Releases before #3063 publish no recovery instructions; when either is present both must be.
  const present = [recoveryInstructionsName, recoveryInstructionsSignatureName]
    .filter(name => lstatSync(join(assets, name), { throwIfNoEntry: false }) !== undefined);
  requireThat(present.length !== 1,
    'Release recovery instructions and their signature bundle must be present together; neither is complete alone');
  const instructions = present.length === 2;
  if (instructions) selected.add(recoveryInstructionsName).add(recoveryInstructionsSignatureName);
  const roles = expectedMembers(identity.version);
  const sources = new Map();
  const files = [...roles].filter(([name, role]) => !isOptionalMember(role) || selected.has(name))
    .map(([name, role]) => {
      const { size, sha256 } = hashFile(join(assets, name), `Release asset ${name}`);
      requireThat(size <= roleLimit(role, limits), `Release asset exceeds its size limit: ${name}`);
      sources.set(name, join(assets, name));
      return { name, role, size, sha256 };
    });
  verifyCliSums(readSmallFile(join(assets, hostUpdateCliSumsName(identity.version)), 'CLI checksum list',
    limits.maxMetadataBytes), identity.version, files.filter(file => isRuntimeMember(file.role)),
  name => readSmallFile(join(assets, name), `Release asset ${name}`, limits.maxMetadataBytes));
  verifySignatures(assets, identity, { run, trustedRoot, version: identity.version, images: images !== undefined,
    instructions });
  if (instructions) {
    validateRecoveryInstructions(readSmallFile(join(assets, recoveryInstructionsName), 'Release recovery instructions',
      limits.maxMetadataBytes), identity);
  }
  let prior;
  if (priorReleaseAssets !== undefined) {
    requireThat(priorRecoveryModes.includes(priorMode), 'Prior recovery mode must be packaged or local-reference');
    const priorAssets = resolve(priorReleaseAssets);
    const verified = verifyPriorRecoverySet({ pathOf: name => join(priorAssets, name), target: identity, channel, run,
      trustedRoot, limits });
    const priorFiles = [...priorRecoveryFiles(verified.identity.version)].map(([name, role]) => {
      const { size, sha256 } = hashFile(join(priorAssets, name), `Prior release asset ${name}`);
      requireThat(size <= limits.maxMetadataBytes, `Prior release asset exceeds its size limit: ${name}`);
      if (priorMode === 'packaged') {
        files.push({ name: priorMemberName(name), role, size, sha256 });
        sources.set(priorMemberName(name), join(priorAssets, name));
      }
      return { name, role, size, sha256 };
    });
    prior = { mode: priorMode, release: verified.identity, manifestDigest: verified.manifestDigest, files: priorFiles,
      protectedBackup: validateProtectedBackupReference(protectedBackup, verified.identity.version) };
  }
  const target = resolve(output);
  requireThat(!lstatSync(target, { throwIfNoEntry: false }), `Offline bundle output already exists: ${target}`);
  const imageStage = `${target}.images.partial`;
  let staged = false;
  let opened = false;
  const partial = `${target}.partial`;
  try {
    if (images !== undefined) {
      // Only the signed manifest and the signed infrastructure list decide which images and digests
      // are packaged; the layout directory supplies content-addressed bytes and nothing else.
      const required = requiredImages(validateManifest(manifestBytes.toString('utf8')), validateInfrastructureImages(
        readSmallFile(join(assets, infrastructureImagesName), 'Infrastructure image list', limits.maxMetadataBytes), identity));
      mkdirSync(imageStage, { mode: 0o700 });
      staged = true;
      for (const expected of required) {
        const path = join(imageStage, expected.member);
        writeImageArchive({ layout: resolve(images), expected, output: path, limits: imageLimits });
        const { size, sha256 } = hashFile(path, `Image archive ${expected.member}`);
        requireThat(size <= roleLimit(roles.get(expected.member) ?? 'infrastructure-image', limits),
          `Image archive exceeds its size limit: ${expected.member}`);
        sources.set(expected.member, path);
        files.push({ name: expected.member, role: expected.kind === 'application' ? 'application-image' : 'infrastructure-image',
          size, sha256 });
      }
    }
    files.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
    const index = {
      schema: 1,
      kind: offlineBundleKind,
      release: identity,
      manifestDigest: manifestDigest(manifestBytes),
      contents: {
        cliRuntimes: hostUpdateCliRuntimes.filter(rid => runtimes.includes(rid)),
        images: images !== undefined, infrastructure: images !== undefined, priorRecoverySet: prior !== undefined,
        recoveryInstructions: instructions,
      },
      installable: false,
      rolloutAuthorization: false,
      files,
      ...(prior ? { priorRecoverySet: prior } : {}),
    };
    const out = openSync(partial, 'wx', 0o644);
    opened = true;
    try {
      const head = indexBytes(index);
      writeAll(out, tarHeader({ name: offlineBundleIndexName, size: head.length }));
      writeAll(out, head);
      writeAll(out, Buffer.alloc((block - (head.length % block)) % block));
      for (const file of files) {
        writeAll(out, tarHeader({ name: file.name, size: file.size }));
        const { fd, size } = openRegularFile(sources.get(file.name), `Release asset ${file.name}`);
        try {
          requireThat(size === file.size && copyRange(fd, 0, size, out) === file.sha256,
            `Release asset changed during assembly: ${file.name}`);
        } finally {
          closeSync(fd);
        }
        writeAll(out, Buffer.alloc((block - (file.size % block)) % block));
      }
      writeAll(out, Buffer.alloc(block * 2));
    } finally {
      closeSync(out);
    }
    // Re-read what was written with the importer's own bounded parser before publishing it.
    const { fd, size } = openRegularFile(partial, 'Offline bundle');
    try {
      const entries = readOfflineBundleEntries(fd, size, limits);
      requireThat(entries.map(entry => entry.name).join() === [offlineBundleIndexName, ...files.map(f => f.name)].join(),
        'Offline bundle self-check failed');
    } finally {
      closeSync(fd);
    }
    requireThat(!lstatSync(target, { throwIfNoEntry: false }), `Offline bundle output already exists: ${target}`);
    renameSync(partial, target);
    return { bundle: target, index };
  } catch (error) {
    if (opened) rmSync(partial, { force: true });
    throw error;
  } finally {
    if (staged) rmSync(imageStage, { force: true, recursive: true });
  }
}

// ---------------------------------------------------------------------------------------------
// Verification (network-denied host): bounded parse, exclusive extraction into a new staging
// directory, member hashes, offline signatures, identity/channel binding. Any failure removes the
// staging directory so no success-shaped import remains.
// ---------------------------------------------------------------------------------------------
function validateIndex(bytes, entries, limits) {
  let index;
  try {
    index = JSON.parse(bytes.toString('utf8'));
  } catch {
    throw new Error('Offline bundle index is not valid JSON');
  }
  const baseFields = ['contents', 'files', 'installable', 'kind', 'manifestDigest', 'release', 'rolloutAuthorization',
    'schema'];
  const hasPrior = index && typeof index === 'object' && Object.hasOwn(index, 'priorRecoverySet');
  requireThat(index && typeof index === 'object' && !Array.isArray(index) &&
    Object.keys(index).sort().join() === [...baseFields, ...(hasPrior ? ['priorRecoverySet'] : [])].sort().join(),
  'Offline bundle index fields are invalid');
  requireThat(index.schema === 1 && index.kind === offlineBundleKind, 'Offline bundle index schema is not supported');
  requireThat(index.installable === false && index.rolloutAuthorization === false,
    'Offline bundle index claims installation or rollout authority this format cannot grant');
  requireThat(Array.isArray(index.files) && index.files.length === entries.length - 1,
    'Offline bundle index does not list exactly the bundle members');
  const members = new Map(entries.slice(1).map(entry => [entry.name, entry]));
  for (const file of index.files) {
    requireThat(file && Object.keys(file).sort().join() === 'name,role,sha256,size' &&
      typeof file.name === 'string' && typeof file.role === 'string' && sha256Pattern.test(file.sha256 ?? '') &&
      Number.isSafeInteger(file.size), 'Offline bundle index file entry is invalid');
    const member = members.get(file.name);
    requireThat(member && member.size === file.size, `Offline bundle index does not match member: ${file.name}`);
    members.delete(file.name);
  }
  requireThat(members.size === 0, 'Offline bundle index does not list exactly the bundle members');
  const contents = index.contents;
  requireThat(contents && typeof contents === 'object' && Object.keys(contents).sort().join() ===
    'cliRuntimes,images,infrastructure,priorRecoverySet,recoveryInstructions' &&
    ['priorRecoverySet', 'recoveryInstructions'].every(key => typeof contents[key] === 'boolean') &&
    typeof contents.images === 'boolean' && contents.infrastructure === contents.images &&
    Array.isArray(contents.cliRuntimes), 'Offline bundle index contents claim material this format does not carry');
  // contents.priorRecoverySet is true only when the complete prior set and backup reference are present.
  requireThat(contents.priorRecoverySet === hasPrior,
    'Offline bundle index prior recovery claim does not match its prior recovery set');
  if (hasPrior) validatePriorIndex(index.priorRecoverySet, limits);
  return index;
}

export function verifyOfflineBundle({ bundle, channel, version, trustedRoot, staging, run, priorRecoverySet,
  protectedBackup, limits = offlineBundleLimits, imageLimits = imageArchiveLimits, now = () => new Date() }) {
  requireChannel(channel);
  requireThat(typeof run === 'function', 'A command runner is required');
  requireThat(typeof trustedRoot === 'string' && trustedRoot.length > 0,
    'Network-denied verification requires an operator-supplied Sigstore trusted root (--trusted-root)');
  requireThat(typeof staging === 'string' && staging.length > 0, 'A new staging directory is required');
  const stagingPath = resolve(staging);
  requireThat(!lstatSync(stagingPath, { throwIfNoEntry: false }),
    `Staging directory must not already exist: ${stagingPath}`);
  const parent = realpathSync(dirname(stagingPath));
  const root = realpathSync(resolve(trustedRoot));
  requireThat(lstatSync(root).isFile(), 'Trusted root must be a regular file');
  const bundlePath = resolve(bundle);
  const { fd, size } = openRegularFile(bundlePath, 'Offline bundle');
  let created = false;
  try {
    const entries = readOfflineBundleEntries(fd, size, limits);
    requireThat(entries[0].name === offlineBundleIndexName, 'Offline bundle index must be the first member');
    requireThat(entries[0].size <= limits.maxMetadataBytes, 'Offline bundle index exceeds its size limit');
    const index = validateIndex(readExactly(fd, entries[0].size, entries[0].offset), entries, limits);
    requireThat(typeof index.release?.version === 'string', 'Offline bundle index release is invalid');
    parseTag(`v${index.release.version}`);
    const roles = expectedMembers(index.release.version);
    const prior = index.priorRecoverySet;
    const packagedPrior = prior?.mode === 'packaged';
    if (packagedPrior) {
      for (const file of prior.files) {
        roles.set(priorMemberName(file.name), file.role);
        const carried = index.files.find(candidate => candidate.name === priorMemberName(file.name));
        requireThat(carried && carried.size === file.size && carried.sha256 === file.sha256,
          `Offline bundle is missing or mismatches its packaged prior recovery member: ${file.name}`);
      }
    }
    if (prior?.mode === 'local-reference') {
      requireThat(typeof priorRecoverySet === 'string' && priorRecoverySet.length > 0,
        'Offline bundle binds a local prior recovery set; supply it with --prior-recovery-set');
    } else {
      requireThat(priorRecoverySet === undefined, prior
        ? 'Offline bundle packages its prior recovery set; a local prior recovery set must not be supplied'
        : 'A local prior recovery set was supplied but the bundle carries no prior recovery set');
    }
    // The index is unsigned, so the backup reference it carries is only a claim; the operator's own
    // expected reference is the trust source and must match it exactly.
    if (prior) {
      requireThat(protectedBackup !== undefined,
        'Offline bundle binds a protected backup reference; supply the expected reference with --protected-backup');
      const expected = validateProtectedBackupReference(protectedBackup, prior.release.version);
      requireThat(protectedBackupFields.split(',').every(key => expected[key] === prior.protectedBackup[key]),
        'Offline bundle protected backup reference does not match the expected reference');
    } else {
      requireThat(protectedBackup === undefined,
        'A protected backup reference was supplied but the bundle carries no prior recovery set');
    }
    for (const file of index.files) {
      requireThat(memberRole(roles, file.name) === file.role, `Offline bundle member is not part of this release: ${file.name}`);
      requireThat(file.size <= roleLimit(file.role, limits), `Offline bundle member exceeds its size limit: ${file.name}`);
    }
    for (const [name, role] of roles) {
      if (!isOptionalMember(role)) {
        requireThat(index.files.some(file => file.name === name), `Offline bundle is missing required member: ${name}`);
      }
    }
    const recoveryFiles = index.files.filter(file => isRecoveryMember(file.role));
    requireThat(recoveryFiles.length === (index.contents.recoveryInstructions ? 2 : 0),
      'Offline bundle recovery instruction members do not match its recovery instructions flag');
    const imageFiles = index.files.filter(file => isImageMember(file.role));
    requireThat(index.contents.images === (imageFiles.length > 0),
      'Offline bundle image members do not match its image contents flag');
    if (index.contents.images) {
      for (const name of [infrastructureImagesName, infrastructureImagesSignatureName]) {
        requireThat(imageFiles.some(file => file.name === name), `Offline bundle is missing required member: ${name}`);
      }
    }
    mkdirSync(stagingPath, { mode: 0o700 });
    created = true;
    const stagingReal = realpathSync(stagingPath);
    requireThat(dirname(stagingReal) === parent, 'Staging directory resolved outside its parent');
    const inside = relative(stagingReal, root);
    requireThat(inside.startsWith('..') || isAbsolute(inside), 'Trusted root must not be inside the staging directory');
    // Members stay in a quarantine subdirectory until every check passes; the verification record is written last.
    const quarantine = join(stagingReal, quarantineDirectoryName);
    mkdirSync(quarantine, { mode: 0o700 });
    const hashes = new Map();
    for (const entry of entries.slice(1)) {
      const out = openSync(join(quarantine, entry.name), 'wx', 0o644);
      try {
        hashes.set(entry.name, copyRange(fd, entry.offset, entry.size, out));
      } finally {
        closeSync(out);
      }
    }
    for (const file of index.files) {
      requireThat(hashes.get(file.name) === file.sha256, `Offline bundle member was modified: ${file.name}`);
    }
    const manifestBytes = readSmallFile(join(quarantine, manifestName), 'Offline bundle manifest', limits.maxMetadataBytes);
    const identity = manifestIdentity(manifestBytes, channel, version);
    requireThat(JSON.stringify(index.release) === JSON.stringify(identity),
      'Offline bundle index identity does not equal the signed manifest identity');
    requireThat(index.manifestDigest === manifestDigest(manifestBytes), 'Offline bundle manifest digest mismatch');
    const runtimes = verifyCliSums(readSmallFile(join(quarantine, hostUpdateCliSumsName(identity.version)),
      'Offline bundle CLI checksum list', limits.maxMetadataBytes), identity.version,
    index.files.filter(file => isRuntimeMember(file.role)),
    name => readSmallFile(join(quarantine, name), `Offline bundle member ${name}`, limits.maxMetadataBytes));
    requireThat(JSON.stringify(index.contents.cliRuntimes) === JSON.stringify(runtimes),
      'Offline bundle runtime list does not match its archives');
    verifySignatures(quarantine, identity, { run, trustedRoot: root, version: identity.version,
      images: index.contents.images, instructions: index.contents.recoveryInstructions });
    let recoveryRecord = false;
    if (index.contents.recoveryInstructions) {
      const instructions = readSmallFile(join(quarantine, recoveryInstructionsName), 'Offline bundle recovery instructions',
        limits.maxMetadataBytes);
      let document;
      try {
        document = validateRecoveryInstructions(instructions, identity);
      } catch (error) {
        throw new Error(`Offline bundle ${error.message}`);
      }
      recoveryRecord = { sha256: createHash('sha256').update(instructions).digest('hex'),
        operations: document.operations.map(operation => operation.id) };
    }
    // Images are proven only after every signature: the signed manifest and signed infrastructure
    // list are the sole source of the required set, digests and platforms.
    const images = [];
    if (index.contents.images) {
      const required = requiredImages(validateManifest(manifestBytes.toString('utf8')), validateInfrastructureImages(
        readSmallFile(join(quarantine, infrastructureImagesName), 'Offline bundle infrastructure image list',
          limits.maxMetadataBytes), identity));
      const archives = imageFiles.filter(file => isImageArchive(file.role)).map(file => file.name).sort();
      requireThat(archives.join() === required.map(image => image.member).sort().join(),
        'Offline bundle does not carry exactly the release-selected image set');
      for (const expected of required) {
        const image = openRegularFile(join(quarantine, expected.member), `Offline bundle member ${expected.member}`);
        try {
          const verified = verifyImageArchive(image.fd, 0, image.size, expected, imageLimits);
          images.push({ member: expected.member, kind: expected.kind, id: expected.id, reference: expected.reference,
            mediaType: verified.mediaType, digest: verified.digest, platforms: expected.platforms, size: image.size,
            sha256: hashes.get(expected.member) });
        } catch (error) {
          throw new Error(`Offline bundle image ${expected.member} failed verification: ${error.message}`);
        } finally {
          closeSync(image.fd);
        }
      }
    }
    const imported = entries.slice(1).map(entry => entry.name);
    let priorRecord = false;
    if (prior) {
      if (!packagedPrior) {
        // Bind the operator's local copy by the digests recorded at assembly, copying it into quarantine
        // so the staged prior set is exactly the bytes that were authenticated.
        const local = resolve(priorRecoverySet);
        for (const file of prior.files) {
          const source = openRegularFile(join(local, file.name), `Local prior recovery file ${file.name}`);
          try {
            requireThat(source.size === file.size && source.size <= limits.maxMetadataBytes,
              `Local prior recovery file does not match the bundle's bound digest: ${file.name}`);
            const out = openSync(join(quarantine, priorMemberName(file.name)), 'wx', 0o644);
            try {
              requireThat(copyRange(source.fd, 0, source.size, out) === file.sha256,
                `Local prior recovery file does not match the bundle's bound digest: ${file.name}`);
            } finally {
              closeSync(out);
            }
          } finally {
            closeSync(source.fd);
          }
          imported.push(priorMemberName(file.name));
        }
      }
      const verified = verifyPriorRecoverySet({ pathOf: name => join(quarantine, priorMemberName(name)), target: identity,
        channel, run, trustedRoot: root, limits });
      requireThat(JSON.stringify(prior.release) === JSON.stringify(verified.identity),
        'Offline bundle prior recovery identity does not equal the signed prior manifest identity');
      requireThat(prior.manifestDigest === verified.manifestDigest, 'Offline bundle prior recovery manifest digest mismatch');
      priorRecord = { mode: prior.mode, release: verified.identity, manifestDigest: verified.manifestDigest,
        protectedBackup: validateProtectedBackupReference(prior.protectedBackup, verified.identity.version) };
    }
    const record = {
      schema: 1,
      decision: 'verified-not-installable',
      release: identity,
      manifestDigest: index.manifestDigest,
      bundleSha256: copyRange(fd, 0, size),
      cliRuntimes: index.contents.cliRuntimes,
      images,
      priorRecoverySet: priorRecord,
      recoveryInstructions: recoveryRecord,
      signatureIdentity: releaseSigningIdentity(identity.channel),
      installable: false,
      rolloutAuthorization: false,
      verifiedAt: now().toISOString(),
    };
    for (const name of imported) {
      requireThat(!lstatSync(join(stagingReal, name), { throwIfNoEntry: false }),
        `Staging directory was modified during verification: ${name}`);
      renameSync(join(quarantine, name), join(stagingReal, name));
    }
    rmdirSync(quarantine);
    const recordFd = openSync(join(stagingReal, offlineBundleVerificationName), 'wx', 0o644);
    try {
      writeAll(recordFd, Buffer.from(`${JSON.stringify(record, undefined, 2)}\n`));
    } finally {
      closeSync(recordFd);
    }
    return record;
  } catch (error) {
    if (created) rmSync(stagingPath, { force: true, recursive: true });
    throw error;
  } finally {
    closeSync(fd);
  }
}

// ---------------------------------------------------------------------------------------------
// Load (network-denied host): hands only verified image archives to the local engine. The
// staging directory's verification record is mutable, so it never supplies expectations: load
// re-authenticates the staged signed manifest and infrastructure list offline against the
// operator's trusted root (from private copies, so the verified bytes are the parsed bytes) and
// derives the required set, digests and platforms from them alone. Every archive is opened once,
// re-hashed and re-verified on that descriptor before anything is loaded, and the same descriptor
// is passed to `docker load` as stdin, so a path swapped after verification is never read.
// Nothing is pulled, built or fetched.
// ---------------------------------------------------------------------------------------------
export function loadVerifiedImages({ staging, channel, trustedRoot, run, limits = offlineBundleLimits,
  imageLimits = imageArchiveLimits }) {
  requireChannel(channel);
  requireThat(typeof run === 'function', 'A command runner is required');
  requireThat(typeof trustedRoot === 'string' && trustedRoot.length > 0,
    'Loading images requires an operator-supplied Sigstore trusted root (--trusted-root)');
  requireThat(typeof staging === 'string' && staging.length > 0, 'A verified staging directory is required');
  const directory = realpathSync(resolve(staging));
  const root = realpathSync(resolve(trustedRoot));
  requireThat(lstatSync(root).isFile(), 'Trusted root must be a regular file');
  const inside = relative(directory, root);
  requireThat(inside.startsWith('..') || isAbsolute(inside), 'Trusted root must not be inside the staging directory');
  let record;
  try {
    record = JSON.parse(readSmallFile(join(directory, offlineBundleVerificationName), 'Offline bundle verification record',
      limits.maxMetadataBytes).toString('utf8'));
  } catch (error) {
    throw new Error(`Offline bundle verification record is unusable: ${error.message}`);
  }
  requireThat(record?.schema === 1 && record.decision === 'verified-not-installable' && Array.isArray(record.images) &&
    record.images.length > 0, 'Staging directory holds no verified image set');
  const signed = {};
  for (const name of [manifestName, manifestSignatureName, infrastructureImagesName, infrastructureImagesSignatureName]) {
    signed[name] = readSmallFile(join(directory, name), `Staged signed file ${name}`, limits.maxMetadataBytes);
  }
  const identity = manifestIdentity(signed[manifestName], channel);
  requireThat(JSON.stringify(record.release) === JSON.stringify(identity),
    'Offline bundle verification record does not match the staged signed manifest');
  const copies = mkdtempSync(join(tmpdir(), 'printfarmer-offline-load-'));
  try {
    for (const [name, bytes] of Object.entries(signed)) {
      const out = openSync(join(copies, name), 'wx', 0o600);
      try {
        writeAll(out, bytes);
      } finally {
        closeSync(out);
      }
    }
    for (const [file, bundle] of [[manifestName, manifestSignatureName],
      [infrastructureImagesName, infrastructureImagesSignatureName]]) {
      try {
        run('cosign', ['verify-blob', '--trusted-root', root, '--bundle', join(copies, bundle),
          '--certificate-oidc-issuer', oidcIssuer, '--certificate-identity', releaseSigningIdentity(identity.channel),
          join(copies, file)]);
      } catch (error) {
        throw new Error(`Offline bundle signature verification failed for ${file}: ${error.message}`);
      }
    }
  } finally {
    rmSync(copies, { force: true, recursive: true });
  }
  const required = requiredImages(validateManifest(signed[manifestName].toString('utf8')),
    validateInfrastructureImages(signed[infrastructureImagesName], identity));
  const recorded = new Map();
  for (const image of record.images) {
    requireThat(image && typeof image.member === 'string' && sha256Pattern.test(image.sha256 ?? '') &&
      Number.isSafeInteger(image.size) && !recorded.has(image.member),
    'Offline bundle verification record image entry is invalid');
    requireMemberName(image.member);
    recorded.set(image.member, image);
  }
  requireThat([...recorded.keys()].sort().join() === required.map(image => image.member).sort().join(),
    'Offline bundle verification record does not name exactly the signed release-selected image set');
  const opened = [];
  try {
    for (const expected of required) {
      const image = recorded.get(expected.member);
      const file = openRegularFile(join(directory, expected.member), `Verified image ${expected.member}`);
      opened.push({ expected, fd: file.fd });
      requireThat(file.size === image.size && copyRange(file.fd, 0, file.size) === image.sha256,
        `Verified image changed after verification: ${expected.member}`);
      try {
        verifyImageArchive(file.fd, 0, file.size, expected, imageLimits);
      } catch (error) {
        throw new Error(`Verified image ${expected.member} does not match the signed release: ${error.message}`);
      }
    }
    const loaded = [];
    for (const { expected, fd } of opened) {
      try {
        run('docker', ['load'], { stdin: fd });
      } catch (error) {
        // Earlier loads persist in the engine; report them so the caller's decision record is accurate.
        const failure = error instanceof Error ? error : new Error(String(error));
        failure.loaded = [...loaded];
        failure.attempted = expected.member;
        throw failure;
      }
      loaded.push({ member: expected.member, reference: expected.reference, digest: expected.digest });
    }
    return { loaded };
  } finally {
    for (const { fd } of opened) closeSync(fd);
  }
}

// ---------------------------------------------------------------------------------------------
// Import (network-denied host, #3063): the one host-local operator path. It reserves its durable
// decision record first, so every decision -- imported or refused -- is recorded; then verifies
// the bundle, requires it to be complete (every release-selected image, the infrastructure list
// and the signed recovery instructions), publishes an `in-progress` record before the first
// `docker load`, loads only verified images, and replaces that record with the final one (listing
// every image loaded before any failure). A refused import removes
// the staging directory it created. The record is redacted: it names the bundle by digest, never
// by path, and replaces host paths in failure reasons with placeholders.
// ---------------------------------------------------------------------------------------------
const operatorPattern = /^[A-Za-z0-9][A-Za-z0-9._@-]{0,63}$/;
const maxReasonLength = 512;

function requireRecordsDirectory(records) {
  requireThat(typeof records === 'string' && isAbsolute(records),
    'A decision records directory (--records) must be an absolute path');
  const link = lstatSync(records, { throwIfNoEntry: false });
  requireThat(link?.isDirectory() && !link.isSymbolicLink(),
    'The decision records directory must be an existing directory (not a link)');
  const real = realpathSync(records);
  requireThat(lstatSync(real).ino === link.ino, 'The decision records directory changed while being opened');
  return real;
}

export function redactReason(message, paths) {
  let text = String(message ?? 'unknown failure');
  const known = [];
  for (const [placeholder, path] of Object.entries(paths)) {
    if (typeof path !== 'string' || path.length === 0) continue;
    known.push([placeholder, resolve(path)], [placeholder, path]);
    try {
      known.push([placeholder, realpathSync(resolve(path))]);
    } catch {
      // A path that does not resolve can only appear verbatim.
    }
  }
  for (const [placeholder, path] of known.sort((a, b) => b[1].length - a[1].length)) {
    text = text.split(path).join(`<${placeholder}>`);
  }
  // Anything else that still looks like a host path is replaced too.
  text = text.replace(/(^|[\s'"(=])(?:[A-Za-z]:[\\/]|\\\\|\/)[^\s'"()]*/g, '$1<path>');
  // eslint-disable-next-line no-control-regex
  text = text.replace(/[\u0000-\u001f\u007f]+/g, ' ').trim();
  return text.length > maxReasonLength ? `${text.slice(0, maxReasonLength - 3)}...` : text;
}

function bundleDigest(bundle) {
  try {
    return hashFile(resolve(bundle), 'Offline bundle').sha256;
  } catch {
    return null;
  }
}

export function importOfflineBundle({ bundle, channel, version, trustedRoot, staging, records, operator, priorRecoverySet,
  protectedBackup, run, limits = offlineBundleLimits, imageLimits = imageArchiveLimits, now = () => new Date(),
  newId = randomUUID }) {
  requireThat(typeof operator === 'string' && operatorPattern.test(operator),
    'An operator identifier (--operator) matching [A-Za-z0-9][A-Za-z0-9._@-]{0,63} is required');
  requireChannel(channel);
  requireThat(typeof version === 'string' && version.length > 0, 'An explicit expected version (--version) is required');
  parseTag(`v${version}`);
  requireThat(typeof run === 'function', 'A command runner is required');
  const directory = requireRecordsDirectory(records);
  const decisionId = newId();
  requireThat(/^[0-9a-f-]{36}$/.test(decisionId), 'Decision identifier is invalid');
  const decidedAt = now().toISOString();
  const name = `${decidedAt.replace(/[:.]/g, '-')}-${decisionId}.json`;
  const target = join(directory, name);
  requireThat(!lstatSync(target, { throwIfNoEntry: false }), 'Decision record already exists');
  let generation = 0;
  let published = false;
  // Opens a fresh exclusive partial file, proven to be the file just created (not a planted link).
  const openPartial = () => {
    const partial = join(directory, `.${name}.${generation++}.partial`);
    const fd = openSync(partial, 'wx', 0o600);
    try {
      const opened = fstatSync(fd);
      const link = lstatSync(partial);
      requireThat(opened.isFile() && link.isFile() && link.ino === opened.ino && link.dev === opened.dev,
        'Decision record changed while being opened');
    } catch (error) {
      closeSync(fd);
      rmSync(partial, { force: true });
      throw error;
    }
    return { fd, partial };
  };
  // Durably publishes one record: write + fsync the partial, rename over the target, fsync the directory.
  const publish = ({ fd, partial }, record) => {
    let closed = false;
    try {
      writeAll(fd, Buffer.from(`${JSON.stringify(record, undefined, 2)}\n`));
      fsyncSync(fd);
      closeSync(fd);
      closed = true;
      if (!published) requireThat(!lstatSync(target, { throwIfNoEntry: false }), 'Decision record already exists');
      renameSync(partial, target);
      published = true;
    } catch (error) {
      if (!closed) closeSync(fd);
      rmSync(partial, { force: true });
      throw error;
    }
    try {
      const directoryFd = openSync(directory, 'r');
      try {
        fsyncSync(directoryFd);
      } finally {
        closeSync(directoryFd);
      }
    } catch (error) {
      // Windows cannot open or fsync a directory handle; the renamed file itself is already fsynced.
      if (process.platform !== 'win32') throw error;
    }
  };
  // Reserve the record before any verification work, so an unwritable records directory refuses first.
  const reservation = openPartial();
  const stagingPath = typeof staging === 'string' && staging.length > 0 ? resolve(staging) : undefined;
  let stagingExisted;
  try {
    stagingExisted = stagingPath !== undefined && lstatSync(stagingPath, { throwIfNoEntry: false }) !== undefined;
  } catch (error) {
    closeSync(reservation.fd);
    rmSync(reservation.partial, { force: true });
    throw error;
  }
  let verification;
  let loaded = [];
  let attempted = null;
  let outcome = 'refused';
  let reason = null;
  const redact = error => redactReason(error?.message ?? error, { bundle, 'trusted-root': trustedRoot, staging,
    records: directory, 'prior-recovery-set': priorRecoverySet });
  const recordOf = () => ({
    schema: 1,
    kind: offlineImportDecisionKind,
    decisionId,
    decidedAt,
    operator,
    outcome,
    reason,
    expected: { channel, version },
    bundleSha256: verification?.bundleSha256 ?? bundleDigest(bundle),
    release: verification?.release ?? null,
    verifiedDigests: verification ? {
      manifest: verification.manifestDigest,
      images: verification.images.map(image => ({ member: image.member, reference: image.reference, digest: image.digest,
        sha256: image.sha256 })),
      recoveryInstructions: verification.recoveryInstructions ? verification.recoveryInstructions.sha256 : null,
      priorRecoverySet: verification.priorRecoverySet ? verification.priorRecoverySet.manifestDigest : null,
    } : null,
    loadedImages: loaded.map(image => ({ member: image.member, digest: image.digest })),
    failedLoad: attempted,
    installable: false,
    rolloutAuthorization: false,
  });
  let pending = reservation;
  try {
    verification = verifyOfflineBundle({ bundle, channel, version, trustedRoot, staging, run, priorRecoverySet,
      protectedBackup, limits, imageLimits, now });
    const missing = [
      ...(verification.images.length > 0 ? [] : ['release-selected images and the infrastructure image list']),
      ...(verification.recoveryInstructions ? [] : ['signed recovery instructions']),
    ];
    requireThat(missing.length === 0, `Offline bundle is incomplete and cannot be imported; it lacks ${missing.join(' and ')}`);
  } catch (error) {
    reason = redact(error);
    // verifyOfflineBundle removes its own staging on failure; a refusal after it succeeded removes it here.
    if (verification && stagingPath && !stagingExisted) rmSync(stagingPath, { force: true, recursive: true });
  }
  if (reason === null) {
    // Durable evidence precedes every side effect: an `in-progress` record is published before the
    // first `docker load`. If finalization later fails, it remains as the record that images may have
    // been loaded without a final outcome.
    outcome = 'in-progress';
    try {
      publish(pending, recordOf());
    } catch (error) {
      if (stagingPath && !stagingExisted) rmSync(stagingPath, { force: true, recursive: true });
      throw error;
    }
    try {
      loaded = loadVerifiedImages({ staging, channel, trustedRoot, run, limits, imageLimits }).loaded;
      outcome = 'imported';
    } catch (error) {
      loaded = Array.isArray(error?.loaded) ? error.loaded : [];
      attempted = typeof error?.attempted === 'string' ? error.attempted : null;
      outcome = 'refused';
      reason = redact(error);
      if (stagingPath && !stagingExisted) rmSync(stagingPath, { force: true, recursive: true });
    }
    pending = openPartial();
  }
  publish(pending, recordOf());
  return { record: recordOf(), path: target };
}
// ---------------------------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------------------------
const usage = `usage:
  node scripts/ci/offline-update-bundle.mjs assemble --release-assets <dir> --channel <stable|insider>
    --output <bundle.tar> [--version <v>] [--runtime <rid>]... [--images <oci-layout-dir>]
    [--trusted-root <trusted_root.json>] [--cosign <path>]
    [--prior-release-assets <dir> --protected-backup <reference.json> [--prior-mode <packaged|local-reference>]]
  node scripts/ci/offline-update-bundle.mjs verify --bundle <bundle.tar> --channel <stable|insider>
    --trusted-root <trusted_root.json> --staging <new-dir> [--version <v>] [--prior-recovery-set <dir>]
    [--protected-backup <reference.json>] [--cosign <path>]
  node scripts/ci/offline-update-bundle.mjs load --staging <verified-dir> --channel <stable|insider>
    --trusted-root <trusted_root.json> [--cosign <path>] [--docker <path>]
  node scripts/ci/offline-update-bundle.mjs import --bundle <bundle.tar> --channel <stable|insider> --version <v>
    --trusted-root <trusted_root.json> --staging <new-dir> --records <decision-records-dir> --operator <id>
    [--prior-recovery-set <dir>] [--protected-backup <reference.json>] [--cosign <path>] [--docker <path>]`;

export function parseArguments(argv) {
  const [command, ...rest] = argv;
  const allowed = {
    assemble: ['release-assets', 'channel', 'output', 'version', 'runtime', 'images', 'trusted-root', 'cosign',
      'prior-release-assets', 'prior-mode', 'protected-backup'],
    verify: ['bundle', 'channel', 'trusted-root', 'staging', 'version', 'prior-recovery-set', 'protected-backup',
      'cosign'],
    load: ['staging', 'channel', 'trusted-root', 'cosign', 'docker'],
    import: ['bundle', 'channel', 'version', 'trusted-root', 'staging', 'records', 'operator', 'prior-recovery-set',
      'protected-backup', 'cosign', 'docker'],
  };
  const required = {
    import: ['bundle', 'channel', 'version', 'trusted-root', 'staging', 'records', 'operator'],
  };
  requireThat(Object.hasOwn(allowed, command ?? ''), usage);
  const options = {};
  for (let index = 0; index < rest.length; index += 2) {
    const key = rest[index]?.startsWith('--') ? rest[index].slice(2) : undefined;
    const value = rest[index + 1];
    requireThat(key && allowed[command].includes(key) && value !== undefined && !value.startsWith('--'), usage);
    if (key === 'runtime') options.runtime = [...(options.runtime ?? []), value];
    else {
      requireThat(!Object.hasOwn(options, key), `Duplicate option --${key}`);
      options[key] = value;
    }
  }
  for (const key of required[command] ?? []) requireThat(Object.hasOwn(options, key), usage);
  return { command, options };
}

async function main(argv) {
  const { command, options } = parseArguments(argv);
  const cosign = options.cosign ?? 'cosign';
  const executables = { cosign, docker: options.docker ?? 'docker' };
  const run = (name, args, { stdin } = {}) => execFileSync(executables[name] ?? name, args,
    { encoding: 'utf8', stdio: [stdin ?? 'ignore', 'pipe', 'pipe'] });
  let protectedBackup;
  if (options['protected-backup'] !== undefined) {
    try {
      protectedBackup = JSON.parse(readSmallFile(resolve(options['protected-backup']), 'Protected backup reference',
        64 * 1024).toString('utf8'));
    } catch (error) {
      throw new Error(`Protected backup reference is unreadable: ${error.message}`);
    }
  }
  if (command === 'assemble') {
    const { bundle, index } = assembleOfflineBundle({
      releaseAssets: options['release-assets'], channel: options.channel, version: options.version,
      runtimes: options.runtime ?? hostUpdateCliRuntimes, output: options.output, run, images: options.images,
      trustedRoot: options['trusted-root'] ? resolve(options['trusted-root']) : undefined,
      priorReleaseAssets: options['prior-release-assets'], priorMode: options['prior-mode'], protectedBackup,
    });
    console.log(JSON.stringify({ bundle, release: index.release, manifestDigest: index.manifestDigest,
      cliRuntimes: index.contents.cliRuntimes, images: index.contents.images,
      priorRecoverySet: index.priorRecoverySet?.release ?? false, installable: false }, undefined, 2));
  } else if (command === 'load') {
    console.log(JSON.stringify(loadVerifiedImages({ staging: options.staging, channel: options.channel,
      trustedRoot: options['trusted-root'], run }), undefined, 2));
  } else if (command === 'import') {
    const { record, path } = importOfflineBundle({ bundle: options.bundle, channel: options.channel,
      version: options.version, trustedRoot: options['trusted-root'], staging: options.staging, records: options.records,
      operator: options.operator, priorRecoverySet: options['prior-recovery-set'], protectedBackup, run });
    console.log(JSON.stringify({ ...record, recordFile: path }, undefined, 2));
    if (record.outcome !== 'imported') {
      console.error(`Offline bundle import refused: ${record.reason}`);
      process.exitCode = 1;
    }
  } else {
    const record = verifyOfflineBundle({ bundle: options.bundle, channel: options.channel, version: options.version,
      trustedRoot: options['trusted-root'], staging: options.staging, priorRecoverySet: options['prior-recovery-set'],
      protectedBackup, run });
    console.log(JSON.stringify(record, undefined, 2));
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main(process.argv.slice(2)).catch(error => {
    console.error(`Offline update bundle failed: ${error.message}`);
    process.exitCode = 1;
  });
}
