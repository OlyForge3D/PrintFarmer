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
// This slice is deliberately NOT installable: it does not yet package application/infrastructure
// images, the prior recovery set, recovery instructions or replay state, and it never enables
// rollout. docs/OFFLINE_UPDATE_RECOVERY.md tracks the remaining delivery.
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import {
  closeSync, constants, fstatSync, lstatSync, mkdirSync, openSync, readSync, realpathSync, renameSync, rmdirSync, rmSync,
  writeSync,
} from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { deriveSequence, manifestDigest, validateManifest } from './release-manifest.mjs';
import { parseTag, repository, requireThat, workflow } from './release-policy.mjs';
import { hostUpdateCliArchiveName, hostUpdateCliRuntimes, hostUpdateCliSbomName, hostUpdateCliSumsBundleName,
  hostUpdateCliSumsName, parseSums, validateHostUpdateCliSbom } from './host-update-cli-package.mjs';

export const offlineBundleIndexName = 'offline-bundle.json';
export const offlineBundleVerificationName = 'offline-bundle-verification.json';
export const offlineBundleKind = 'printfarmer-offline-bundle';
const quarantineDirectoryName = '.unverified';
const manifestName = 'update-manifest.json';
const manifestSignatureName = 'update-manifest.sigstore.json';
const oidcIssuer = 'https://token.actions.githubusercontent.com';
const block = 512;
const chunk = 1024 * 1024;
const MiB = 1024 * 1024;

// Bounds are enforced from headers BEFORE any byte is written to disk. They are sized for this
// metadata-and-CLI slice; image archives (follow-up) will need their own explicit limits.
export const offlineBundleLimits = Object.freeze({
  maxBundleBytes: 2048 * MiB,
  maxMembers: 16,
  maxMetadataBytes: 4 * MiB,
  maxArchiveBytes: 512 * MiB,
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
  return role === 'cli-archive' ? limits.maxArchiveBytes : limits.maxMetadataBytes;
}

// Every member name is fixed by the signed release version, so an index can never introduce an
// unexpected file, role or runtime.
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
  return members;
}

const isRuntimeMember = role => role === 'cli-archive' || role === 'cli-sbom';

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
function verifySignatures(directory, identity, { run, trustedRoot, version }) {
  const offline = trustedRoot ? ['--trusted-root', trustedRoot] : [];
  for (const [file, bundle] of [[manifestName, manifestSignatureName],
    [hostUpdateCliSumsName(version), hostUpdateCliSumsBundleName(version)]]) {
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
  run, trustedRoot, limits = offlineBundleLimits }) {
  requireChannel(channel);
  requireThat(typeof run === 'function', 'A command runner is required');
  requireThat(typeof output === 'string' && output.length > 0, 'An output bundle path is required');
  requireThat(Array.isArray(runtimes) && runtimes.length > 0 && new Set(runtimes).size === runtimes.length &&
    runtimes.every(rid => hostUpdateCliRuntimes.includes(rid)), 'Offline bundle runtimes must be distinct supported runtimes');
  const assets = resolve(releaseAssets);
  const manifestBytes = readSmallFile(join(assets, manifestName), 'Release manifest', limits.maxMetadataBytes);
  const identity = manifestIdentity(manifestBytes, channel, version);
  const selected = new Set(runtimes.flatMap(rid =>
    [hostUpdateCliArchiveName(identity.version, rid), hostUpdateCliSbomName(identity.version, rid)]));
  const roles = expectedMembers(identity.version);
  const files = [...roles].filter(([name, role]) => !isRuntimeMember(role) || selected.has(name)).map(([name, role]) => {
    const { size, sha256 } = hashFile(join(assets, name), `Release asset ${name}`);
    requireThat(size <= roleLimit(role, limits), `Release asset exceeds its size limit: ${name}`);
    return { name, role, size, sha256 };
  }).sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  verifyCliSums(readSmallFile(join(assets, hostUpdateCliSumsName(identity.version)), 'CLI checksum list',
    limits.maxMetadataBytes), identity.version, files.filter(file => isRuntimeMember(file.role)),
  name => readSmallFile(join(assets, name), `Release asset ${name}`, limits.maxMetadataBytes));
  verifySignatures(assets, identity, { run, trustedRoot, version: identity.version });
  const index = {
    schema: 1,
    kind: offlineBundleKind,
    release: identity,
    manifestDigest: manifestDigest(manifestBytes),
    contents: {
      cliRuntimes: hostUpdateCliRuntimes.filter(rid => runtimes.includes(rid)),
      images: false, infrastructure: false, priorRecoverySet: false, recoveryInstructions: false,
    },
    installable: false,
    rolloutAuthorization: false,
    files,
  };
  const target = resolve(output);
  requireThat(!lstatSync(target, { throwIfNoEntry: false }), `Offline bundle output already exists: ${target}`);
  const partial = `${target}.partial`;
  const out = openSync(partial, 'wx', 0o644);
  try {
    const head = indexBytes(index);
    writeAll(out, tarHeader({ name: offlineBundleIndexName, size: head.length }));
    writeAll(out, head);
    writeAll(out, Buffer.alloc((block - (head.length % block)) % block));
    for (const file of files) {
      writeAll(out, tarHeader({ name: file.name, size: file.size }));
      const { fd, size } = openRegularFile(join(assets, file.name), `Release asset ${file.name}`);
      try {
        requireThat(size === file.size && copyRange(fd, 0, size, out) === file.sha256,
          `Release asset changed during assembly: ${file.name}`);
      } finally {
        closeSync(fd);
      }
      writeAll(out, Buffer.alloc((block - (file.size % block)) % block));
    }
    writeAll(out, Buffer.alloc(block * 2));
    closeSync(out);
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
  } catch (error) {
    try { closeSync(out); } catch { /* already closed */ }
    rmSync(partial, { force: true });
    throw error;
  }
  return { bundle: target, index };
}

// ---------------------------------------------------------------------------------------------
// Verification (network-denied host): bounded parse, exclusive extraction into a new staging
// directory, member hashes, offline signatures, identity/channel binding. Any failure removes the
// staging directory so no success-shaped import remains.
// ---------------------------------------------------------------------------------------------
function validateIndex(bytes, entries) {
  let index;
  try {
    index = JSON.parse(bytes.toString('utf8'));
  } catch {
    throw new Error('Offline bundle index is not valid JSON');
  }
  requireThat(index && typeof index === 'object' && !Array.isArray(index) &&
    Object.keys(index).sort().join() === ['contents', 'files', 'installable', 'kind', 'manifestDigest', 'release',
      'rolloutAuthorization', 'schema'].join(), 'Offline bundle index fields are invalid');
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
    ['images', 'infrastructure', 'priorRecoverySet', 'recoveryInstructions'].every(key => contents[key] === false) &&
    Array.isArray(contents.cliRuntimes), 'Offline bundle index contents claim material this format does not carry');
  return index;
}

export function verifyOfflineBundle({ bundle, channel, version, trustedRoot, staging, run,
  limits = offlineBundleLimits, now = () => new Date() }) {
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
    const index = validateIndex(readExactly(fd, entries[0].size, entries[0].offset), entries);
    requireThat(typeof index.release?.version === 'string', 'Offline bundle index release is invalid');
    parseTag(`v${index.release.version}`);
    const roles = expectedMembers(index.release.version);
    for (const file of index.files) {
      requireThat(roles.get(file.name) === file.role, `Offline bundle member is not part of this release: ${file.name}`);
      requireThat(file.size <= roleLimit(file.role, limits), `Offline bundle member exceeds its size limit: ${file.name}`);
    }
    for (const [name, role] of roles) {
      if (!isRuntimeMember(role)) {
        requireThat(index.files.some(file => file.name === name), `Offline bundle is missing required member: ${name}`);
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
    verifySignatures(quarantine, identity, { run, trustedRoot: root, version: identity.version });
    const record = {
      schema: 1,
      decision: 'verified-not-installable',
      release: identity,
      manifestDigest: index.manifestDigest,
      bundleSha256: copyRange(fd, 0, size),
      cliRuntimes: index.contents.cliRuntimes,
      signatureIdentity: releaseSigningIdentity(identity.channel),
      installable: false,
      rolloutAuthorization: false,
      verifiedAt: now().toISOString(),
    };
    for (const entry of entries.slice(1)) {
      requireThat(!lstatSync(join(stagingReal, entry.name), { throwIfNoEntry: false }),
        `Staging directory was modified during verification: ${entry.name}`);
      renameSync(join(quarantine, entry.name), join(stagingReal, entry.name));
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
// CLI
// ---------------------------------------------------------------------------------------------
const usage = `usage:
  node scripts/ci/offline-update-bundle.mjs assemble --release-assets <dir> --channel <stable|insider>
    --output <bundle.tar> [--version <v>] [--runtime <rid>]... [--trusted-root <trusted_root.json>] [--cosign <path>]
  node scripts/ci/offline-update-bundle.mjs verify --bundle <bundle.tar> --channel <stable|insider>
    --trusted-root <trusted_root.json> --staging <new-dir> [--version <v>] [--cosign <path>]`;

export function parseArguments(argv) {
  const [command, ...rest] = argv;
  const allowed = {
    assemble: ['release-assets', 'channel', 'output', 'version', 'runtime', 'trusted-root', 'cosign'],
    verify: ['bundle', 'channel', 'trusted-root', 'staging', 'version', 'cosign'],
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
  return { command, options };
}

async function main(argv) {
  const { command, options } = parseArguments(argv);
  const cosign = options.cosign ?? 'cosign';
  const run = (name, args) => execFileSync(name === 'cosign' ? cosign : name, args,
    { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  if (command === 'assemble') {
    const { bundle, index } = assembleOfflineBundle({
      releaseAssets: options['release-assets'], channel: options.channel, version: options.version,
      runtimes: options.runtime ?? hostUpdateCliRuntimes, output: options.output, run,
      trustedRoot: options['trusted-root'] ? resolve(options['trusted-root']) : undefined,
    });
    console.log(JSON.stringify({ bundle, release: index.release, manifestDigest: index.manifestDigest,
      cliRuntimes: index.contents.cliRuntimes, installable: false }, undefined, 2));
  } else {
    const record = verifyOfflineBundle({ bundle: options.bundle, channel: options.channel, version: options.version,
      trustedRoot: options['trusted-root'], staging: options.staging, run });
    console.log(JSON.stringify(record, undefined, 2));
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main(process.argv.slice(2)).catch(error => {
    console.error(`Offline update bundle failed: ${error.message}`);
    process.exitCode = 1;
  });
}
