// Offline bundle container images (issue #3061): the pinned, release-signed infrastructure image
// list, a canonical nested OCI image-layout archive writer, and a strict bounded verifier.
//
// Trust: application image identity comes only from the signed update-manifest.json (index digest
// plus per-platform child digests). Infrastructure image identity comes only from
// infrastructure-images.json, which the release workflow builds from the repository lock
// (scripts/docker/infrastructure-images.lock.json), checks against the registry, binds to the
// release identity and signs with the same keyless workflow identity as the manifest. Nothing in an
// image archive -- its index.json, annotations or blobs -- is trusted until it matches that identity.
//
// Each image member is an uncompressed ustar OCI image layout holding exactly: `oci-layout`,
// `index.json` (one canonical descriptor naming the pinned root digest and alias) and
// `blobs/sha256/<hex>` for the root, each selected platform manifest, its config and its layers.
// Descriptors for platforms or attestations the release does not select must be absent. Missing,
// extra, unreachable, tampered, wrong-platform or mixed-digest content fails closed before load.
import { createHash } from 'node:crypto';
import { closeSync, constants, fstatSync, lstatSync, openSync, readSync, rmSync, writeSync } from 'node:fs';
import { join } from 'node:path';
import { components, requireThat } from './release-policy.mjs';

export const infrastructureImagesName = 'infrastructure-images.json';
export const infrastructureImagesSignatureName = 'infrastructure-images.sigstore.json';
export const infrastructureImagesKind = 'printfarmer-infrastructure-images';
export const infrastructureLockKind = 'printfarmer-infrastructure-images-lock';
export const infrastructureLockPath = 'scripts/docker/infrastructure-images.lock.json';

const MiB = 1024 * 1024;
const GiB = 1024 * MiB;
const block = 512;
const chunk = MiB;

// Per-archive bounds, enforced from headers and descriptors before any blob is trusted.
export const imageArchiveLimits = Object.freeze({
  maxArchiveBytes: 6 * GiB,
  maxBlobs: 1024,
  maxJsonBytes: 4 * MiB,
  maxDescriptors: 256,
});

export const mediaTypes = Object.freeze({
  ociIndex: 'application/vnd.oci.image.index.v1+json',
  dockerList: 'application/vnd.docker.distribution.manifest.list.v2+json',
  ociManifest: 'application/vnd.oci.image.manifest.v1+json',
  dockerManifest: 'application/vnd.docker.distribution.manifest.v2+json',
});
const indexTypes = [mediaTypes.ociIndex, mediaTypes.dockerList];
const manifestTypes = [mediaTypes.ociManifest, mediaTypes.dockerManifest];
const supportedPlatforms = ['linux/amd64', 'linux/arm64'];
const digestPattern = /^sha256:[a-f0-9]{64}$/;
const idPattern = /^[a-z][a-z0-9-]{0,39}$/;
const referencePattern = /^[a-z0-9-]+(?:\.[a-z0-9-]+)+(?::[0-9]{1,5})?(?:\/[a-z0-9]+(?:[._-][a-z0-9]+)*)+:[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$/;
const blobNamePattern = /^blobs\/sha256\/([a-f0-9]{64})$/;
const layoutBytes = Buffer.from('{"imageLayoutVersion":"1.0.0"}');
export const infrastructureImageArchivePattern = /^infrastructure-([a-z][a-z0-9-]{0,39})\.oci\.tar$/;

export const applicationImageMember = id => `image-${id}.oci.tar`;
export const infrastructureImageMember = id => `infrastructure-${id}.oci.tar`;
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const exactKeys = (value, keys) => value && typeof value === 'object' && !Array.isArray(value) &&
  Object.keys(value).sort().join() === [...keys].sort().join();

// ---------------------------------------------------------------------------------------------
// Pinned infrastructure identity
// ---------------------------------------------------------------------------------------------
function validateInfrastructureEntries(images, label) {
  requireThat(Array.isArray(images) && images.length > 0 && images.length <= 16, `${label} image list is invalid`);
  const references = new Set();
  let previous = '';
  for (const image of images) {
    requireThat(exactKeys(image, ['id', 'reference', 'mediaType', 'digest', 'platforms']) &&
      idPattern.test(image.id ?? '') && image.id > previous, `${label} image entry is invalid or unsorted`);
    previous = image.id;
    requireThat(typeof image.reference === 'string' && referencePattern.test(image.reference) &&
      !references.has(image.reference), `${label} image reference must be a unique fully qualified tag: ${image.id}`);
    references.add(image.reference);
    requireThat([...indexTypes, ...manifestTypes].includes(image.mediaType), `${label} image media type is invalid: ${image.id}`);
    requireThat(digestPattern.test(image.digest ?? ''), `${label} image digest is invalid: ${image.id}`);
    const platforms = image.platforms;
    const keys = platforms && typeof platforms === 'object' && !Array.isArray(platforms) ? Object.keys(platforms) : [];
    requireThat(keys.length > 0 && keys.join() === supportedPlatforms.filter(key => keys.includes(key)).join() &&
      keys.every(key => digestPattern.test(platforms[key] ?? '')), `${label} image platforms are invalid: ${image.id}`);
    if (manifestTypes.includes(image.mediaType)) {
      requireThat(keys.length === 1 && platforms[keys[0]] === image.digest,
        `${label} single-manifest image must name exactly its own digest: ${image.id}`);
    } else {
      requireThat(new Set(keys.map(key => platforms[key])).size === keys.length &&
        keys.every(key => platforms[key] !== image.digest), `${label} image index platform digests are invalid: ${image.id}`);
    }
  }
  return images;
}

export function validateInfrastructureLock(bytes) {
  let lock;
  try {
    lock = JSON.parse(Buffer.from(bytes).toString('utf8'));
  } catch {
    throw new Error('Infrastructure image lock is not valid JSON');
  }
  requireThat(exactKeys(lock, ['schema', 'kind', 'images']) && lock.schema === 1 && lock.kind === infrastructureLockKind,
    'Infrastructure image lock schema is not supported');
  validateInfrastructureEntries(lock.images, 'Infrastructure image lock');
  return lock;
}

const identityKeys = ['tag', 'version', 'channel', 'sourceBranch', 'sourceCommit', 'buildId', 'sequence'];

// The signed release asset: the lock's images bound to one exact release identity.
export function infrastructureImagesDocument(identity, lock) {
  requireThat(exactKeys(identity, identityKeys), 'Infrastructure image list requires a complete release identity');
  validateInfrastructureEntries(lock.images, 'Infrastructure image lock');
  return Buffer.from(`${JSON.stringify({
    schema: 1, kind: infrastructureImagesKind,
    release: Object.fromEntries(identityKeys.map(key => [key, identity[key]])),
    images: lock.images,
  }, undefined, 2)}\n`);
}

export function validateInfrastructureImages(bytes, identity) {
  let document;
  try {
    document = JSON.parse(Buffer.from(bytes).toString('utf8'));
  } catch {
    throw new Error('Infrastructure image list is not valid JSON');
  }
  requireThat(exactKeys(document, ['schema', 'kind', 'release', 'images']) && document.schema === 1 &&
    document.kind === infrastructureImagesKind, 'Infrastructure image list schema is not supported');
  requireThat(exactKeys(document.release, identityKeys) && identityKeys.every(key => document.release[key] === identity[key]),
    'Infrastructure image list is not bound to this release identity');
  return validateInfrastructureEntries(document.images, 'Infrastructure image list');
}

// Release time only: prove each pinned digest and platform child against the registry before the
// list is signed. `run` returns stdout; `imagetools inspect --raw` prints the exact stored bytes.
export function verifyInfrastructureLockAgainstRegistry(lock, run) {
  for (const image of validateInfrastructureEntries(lock.images, 'Infrastructure image lock')) {
    const pinned = `${image.reference}@${image.digest}`;
    const raw = Buffer.from(run('docker', ['buildx', 'imagetools', 'inspect', pinned, '--raw']), 'utf8');
    requireThat(`sha256:${sha256(raw)}` === image.digest, `Registry content does not match the pinned digest: ${pinned}`);
    const root = JSON.parse(raw.toString('utf8'));
    requireThat(root?.mediaType === image.mediaType, `Registry media type does not match the lock: ${pinned}`);
    if (indexTypes.includes(image.mediaType)) {
      for (const [platform, digest] of Object.entries(image.platforms)) {
        const matches = (root.manifests ?? []).filter(descriptor => platformMatches(descriptor?.platform, platform));
        requireThat(matches.length === 1 && matches[0].digest === digest,
          `Registry index does not select the pinned ${platform} manifest: ${pinned}`);
      }
    } else {
      const config = JSON.parse(run('docker', ['buildx', 'imagetools', 'inspect', pinned, '--format', '{{json .Image}}']));
      requireThat(configMatches(config, Object.keys(image.platforms)[0]), `Registry image platform does not match the lock: ${pinned}`);
    }
  }
}

// ---------------------------------------------------------------------------------------------
// Required image set: every manifest service with every declared platform, plus every pinned
// infrastructure image. Each entry is the complete expectation an archive must satisfy.
// ---------------------------------------------------------------------------------------------
export function requiredImages(manifest, infrastructure) {
  const application = manifest.services.map(service => {
    const policy = components[service.id];
    requireThat(policy, `Manifest names an unknown service: ${service.id}`);
    const digest = service.image.slice(service.image.indexOf('@') + 1);
    return {
      member: applicationImageMember(service.id), kind: 'application', id: service.id,
      reference: `${service.image.slice(0, service.image.indexOf('@'))}:${manifest.version}`,
      mediaTypes: indexTypes, digest,
      platforms: Object.fromEntries(policy.platforms.map(platform =>
        [platform, manifest.platformDigests[`${service.id}/${platform.replaceAll('/', '-')}`]])),
    };
  });
  const infra = infrastructure.map(image => ({
    member: infrastructureImageMember(image.id), kind: 'infrastructure', id: image.id, reference: image.reference,
    mediaTypes: [image.mediaType], digest: image.digest, platforms: image.platforms,
  }));
  const required = [...application, ...infra];
  for (const image of required) {
    requireThat(digestPattern.test(image.digest) && Object.values(image.platforms).every(d => digestPattern.test(d ?? '')),
      `Required image identity is incomplete: ${image.id}`);
  }
  return required;
}

// ---------------------------------------------------------------------------------------------
// Platform matching. linux/arm64 accepts the canonical `v8` variant; any other variant is a
// different platform and never matches.
// ---------------------------------------------------------------------------------------------
function variantMatches(variant, architecture) {
  return variant === undefined || variant === '' || (architecture === 'arm64' && variant === 'v8');
}

export function platformMatches(platform, key) {
  const [os, architecture] = key.split('/');
  return Boolean(platform) && platform.os === os && platform.architecture === architecture &&
    variantMatches(platform.variant, architecture);
}

function configMatches(config, key) {
  const [os, architecture] = key.split('/');
  return Boolean(config) && config.os === os && config.architecture === architecture &&
    variantMatches(config.variant, architecture);
}

// ---------------------------------------------------------------------------------------------
// Closure: from the pinned root, the exact set of blobs the selected platforms need. `source`
// supplies sizes and hash-verified JSON bytes; the same walk drives assembly and verification.
// ---------------------------------------------------------------------------------------------
function descriptor(value, label, limits) {
  requireThat(value && typeof value === 'object' && typeof value.mediaType === 'string' &&
    digestPattern.test(value.digest ?? '') && Number.isSafeInteger(value.size) && value.size >= 0,
  `Image ${label} descriptor is invalid`);
  requireThat(value.urls === undefined, `Image ${label} descriptor references external content`);
  requireThat(value.size <= limits.maxArchiveBytes, `Image ${label} descriptor exceeds the archive size limit`);
  return value;
}

function readDocument(source, desc, label, limits) {
  requireThat(desc.size <= limits.maxJsonBytes, `Image ${label} exceeds its size limit`);
  requireThat(source.has(desc.digest), `Image is missing its ${label}: ${desc.digest}`);
  requireThat(source.size(desc.digest) === desc.size, `Image ${label} size does not match its descriptor`);
  let document;
  try {
    document = JSON.parse(source.read(desc.digest).toString('utf8'));
  } catch {
    throw new Error(`Image ${label} is not valid JSON`);
  }
  requireThat(document && typeof document === 'object' && !Array.isArray(document), `Image ${label} is invalid`);
  return document;
}

function manifestClosure(source, desc, platform, closure, limits) {
  const label = `${platform} manifest`;
  requireThat(manifestTypes.includes(desc.mediaType), `Image ${label} media type is not supported`);
  const manifest = readDocument(source, desc, label, limits);
  requireThat(manifest.schemaVersion === 2 && manifest.mediaType === desc.mediaType,
    `Image ${label} media type does not match its descriptor`);
  closure.add(desc.digest);
  const config = descriptor(manifest.config, `${platform} config`, limits);
  requireThat(configMatches(readDocument(source, config, `${platform} config`, limits), platform),
    `Image config is not ${platform}`);
  closure.add(config.digest);
  requireThat(Array.isArray(manifest.layers) && manifest.layers.length > 0 &&
    manifest.layers.length <= limits.maxDescriptors, `Image ${label} layer list is invalid`);
  for (const layer of manifest.layers) {
    const value = descriptor(layer, `${platform} layer`, limits);
    requireThat(source.has(value.digest), `Image is missing a ${platform} layer: ${value.digest}`);
    requireThat(source.size(value.digest) === value.size, `Image ${platform} layer size does not match its descriptor`);
    closure.add(value.digest);
  }
}

export function imageClosure(source, root, expected, limits = imageArchiveLimits) {
  requireThat(root.digest === expected.digest, `Image root digest does not match the pinned identity: ${expected.id}`);
  requireThat(expected.mediaTypes.includes(root.mediaType), `Image root media type is not allowed: ${expected.id}`);
  const closure = new Set();
  const platforms = Object.keys(expected.platforms);
  if (manifestTypes.includes(root.mediaType)) {
    requireThat(platforms.length === 1 && expected.platforms[platforms[0]] === root.digest,
      `Single-manifest image does not match its pinned platform: ${expected.id}`);
    manifestClosure(source, root, platforms[0], closure, limits);
  } else {
    const index = readDocument(source, root, 'index', limits);
    requireThat(index.schemaVersion === 2 && index.mediaType === root.mediaType,
      'Image index media type does not match its descriptor');
    requireThat(Array.isArray(index.manifests) && index.manifests.length <= limits.maxDescriptors,
      'Image index manifest list is invalid');
    closure.add(root.digest);
    const children = index.manifests.map(value => descriptor(value, 'index child', limits));
    for (const platform of platforms) {
      const matches = children.filter(child => platformMatches(child.platform, platform));
      requireThat(matches.length === 1, `Image index does not select exactly one ${platform} manifest: ${expected.id}`);
      requireThat(matches[0].digest === expected.platforms[platform],
        `Image ${platform} manifest digest does not match the pinned identity: ${expected.id}`);
      manifestClosure(source, matches[0], platform, closure, limits);
    }
  }
  requireThat(closure.size <= limits.maxBlobs, `Image has too many blobs: ${expected.id}`);
  let total = 0;
  for (const digest of closure) total += source.size(digest);
  requireThat(total <= limits.maxArchiveBytes, `Image exceeds the archive size limit: ${expected.id}`);
  return closure;
}

// ---------------------------------------------------------------------------------------------
// Canonical nested ustar (same strict rules as the outer bundle, plus a fixed member order).
// ---------------------------------------------------------------------------------------------
function octalField(value, width) {
  const text = value.toString(8);
  requireThat(text.length <= width - 1, 'Image archive tar field overflow');
  return `${text.padStart(width - 1, '0')}\0`;
}

function headerChecksum(header) {
  let sum = 0;
  for (let index = 0; index < block; index += 1) sum += index >= 148 && index < 156 ? 0x20 : header[index];
  return sum;
}

export function imageTarHeader({ name, size, type = '0' }) {
  const header = Buffer.alloc(block);
  header.write(name, 0, 100, 'utf8');
  header.write(octalField(0o644, 8), 100, 'ascii');
  header.write(octalField(0, 8), 108, 'ascii');
  header.write(octalField(0, 8), 116, 'ascii');
  header.write(octalField(size, 12), 124, 'ascii');
  header.write(octalField(0, 12), 136, 'ascii');
  header.write(type, 156, 1, 'ascii');
  header.write('ustar\0', 257, 6, 'ascii');
  header.write('00', 263, 2, 'ascii');
  header.write('root', 265, 32, 'ascii');
  header.write('root', 297, 32, 'ascii');
  header.write(octalField(0, 8), 329, 'ascii');
  header.write(octalField(0, 8), 337, 'ascii');
  header.write(`${headerChecksum(header).toString(8).padStart(6, '0')}\0 `, 148, 8, 'ascii');
  return header;
}

function readExactly(fd, length, position) {
  const buffer = Buffer.alloc(length);
  let done = 0;
  while (done < length) {
    const read = readSync(fd, buffer, done, length - done, position + done);
    requireThat(read > 0, 'Image archive is truncated');
    done += read;
  }
  return buffer;
}

function writeAll(fd, buffer) {
  let done = 0;
  while (done < buffer.length) done += writeSync(fd, buffer, done, buffer.length - done);
}

function hashRange(fd, position, size, out) {
  const hash = createHash('sha256');
  const buffer = Buffer.alloc(Math.min(chunk, Math.max(size, 1)));
  let done = 0;
  while (done < size) {
    const read = readSync(fd, buffer, 0, Math.min(buffer.length, size - done), position + done);
    requireThat(read > 0, 'Image content ended early');
    hash.update(buffer.subarray(0, read));
    if (out !== undefined) writeAll(out, buffer.subarray(0, read));
    done += read;
  }
  return hash.digest('hex');
}

const isZero = buffer => buffer.every(byte => byte === 0);

function nestedName(header) {
  const field = header.subarray(0, 100);
  const end = field.indexOf(0);
  requireThat(end > 0 && isZero(field.subarray(end)), 'Image archive member name is malformed');
  const name = field.subarray(0, end).toString('latin1');
  requireThat(name === 'oci-layout' || name === 'index.json' || blobNamePattern.test(name),
    `Image archive member is not part of an OCI layout: ${JSON.stringify(name)}`);
  return name;
}

// Bounded, strict parse of one nested archive located at [base, base + length) inside `fd`.
export function readImageArchiveEntries(fd, base, length, limits = imageArchiveLimits) {
  requireThat(length <= limits.maxArchiveBytes, 'Image archive exceeds its size limit');
  requireThat(length >= block * 4 && length % block === 0, 'Image archive is not a complete tar archive');
  const entries = [];
  let offset = 0;
  for (;;) {
    requireThat(offset + block <= length, 'Image archive is truncated before its end marker');
    const header = readExactly(fd, block, base + offset);
    if (isZero(header)) {
      requireThat(offset + 2 * block <= length && isZero(readExactly(fd, block, base + offset + block)),
        'Image archive end marker is incomplete');
      for (let tail = offset + 2 * block; tail < length; tail += chunk) {
        requireThat(isZero(readExactly(fd, Math.min(chunk, length - tail), base + tail)), 'Image archive has data after its end marker');
      }
      break;
    }
    requireThat(entries.length < limits.maxBlobs + 2, 'Image archive has too many members');
    requireThat(header.subarray(257, 263).toString('latin1') === 'ustar\0' &&
      header.subarray(263, 265).toString('latin1') === '00', 'Image archive member is not a POSIX ustar entry');
    const stored = header.subarray(148, 156).toString('latin1');
    requireThat(/^[0-7]{6}\0 $/.test(stored) && Number.parseInt(stored.slice(0, 6), 8) === headerChecksum(header),
      'Image archive tar header checksum mismatch');
    requireThat(String.fromCharCode(header[156]) === '0', 'Image archive member is not a regular file');
    requireThat(isZero(header.subarray(157, 257)) && isZero(header.subarray(345, 500)),
      'Image archive member carries a link target or path prefix');
    const name = nestedName(header);
    requireThat(header.subarray(100, 108).toString('latin1') === '0000644\0', `Image archive member mode is not 0644: ${name}`);
    const sizeField = header.subarray(124, 136).toString('latin1');
    requireThat(/^[0-7]{11}\0$/.test(sizeField), `Image archive member size is malformed: ${name}`);
    const size = Number.parseInt(sizeField.slice(0, 11), 8);
    const position = entries.length;
    requireThat(position === 0 ? name === 'oci-layout' : position === 1 ? name === 'index.json' :
      blobNamePattern.test(name) && (position === 2 || name > entries[position - 1].name),
    `Image archive members are not in canonical order: ${name}`);
    const dataOffset = offset + block;
    const padded = Math.ceil(size / block) * block;
    requireThat(dataOffset + padded <= length, `Image archive member is truncated: ${name}`);
    if (padded > size) {
      requireThat(isZero(readExactly(fd, padded - size, base + dataOffset + size)), `Image archive member padding is not zero: ${name}`);
    }
    entries.push({ name, size, offset: base + dataOffset });
    offset = dataOffset + padded;
  }
  requireThat(entries.length >= 3, 'Image archive carries no blobs');
  return entries;
}

export function canonicalImageIndex(expected, root) {
  return Buffer.from(JSON.stringify({
    schemaVersion: 2,
    mediaType: mediaTypes.ociIndex,
    manifests: [{
      mediaType: root.mediaType, digest: root.digest, size: root.size,
      annotations: { 'io.containerd.image.name': expected.reference, 'org.opencontainers.image.ref.name': expected.reference },
    }],
  }));
}

// Verifies one nested archive against `expected` without trusting anything it declares. Returns
// the verified root descriptor.
export function verifyImageArchive(fd, base, length, expected, limits = imageArchiveLimits) {
  const entries = readImageArchiveEntries(fd, base, length, limits);
  requireThat(readExactly(fd, entries[0].size, entries[0].offset).equals(layoutBytes), 'Image archive oci-layout is not supported');
  requireThat(entries[1].size <= limits.maxJsonBytes, 'Image archive index exceeds its size limit');
  const indexBytes = readExactly(fd, entries[1].size, entries[1].offset);
  let index;
  try {
    index = JSON.parse(indexBytes.toString('utf8'));
  } catch {
    throw new Error('Image archive index is not valid JSON');
  }
  requireThat(Array.isArray(index?.manifests) && index.manifests.length === 1, 'Image archive index must name exactly one image');
  const root = descriptor(index.manifests[0], 'archive root', limits);
  requireThat(index.manifests[0].annotations?.['io.containerd.image.name'] === expected.reference &&
    index.manifests[0].annotations?.['org.opencontainers.image.ref.name'] === expected.reference,
  `Image archive alias does not match the pinned reference: ${expected.id}`);
  requireThat(root.digest === expected.digest, `Image archive root digest does not match the pinned identity: ${expected.id}`);
  const blobs = new Map();
  for (const entry of entries.slice(2)) {
    const digest = `sha256:${blobNamePattern.exec(entry.name)[1]}`;
    requireThat(`sha256:${hashRange(fd, entry.offset, entry.size)}` === digest, `Image blob was modified: ${digest}`);
    blobs.set(digest, entry);
  }
  requireThat(blobs.has(root.digest) && blobs.get(root.digest).size === root.size, 'Image archive root blob is missing or resized');
  requireThat(indexBytes.equals(canonicalImageIndex(expected, root)), 'Image archive index is not canonical');
  const source = {
    has: digest => blobs.has(digest),
    size: digest => blobs.get(digest).size,
    read: digest => readExactly(fd, blobs.get(digest).size, blobs.get(digest).offset),
  };
  const closure = imageClosure(source, root, expected, limits);
  requireThat(closure.size === blobs.size && [...blobs.keys()].every(digest => closure.has(digest)),
    `Image archive carries blobs outside the selected platforms: ${expected.id}`);
  return { mediaType: root.mediaType, digest: root.digest, size: root.size, blobs: blobs.size };
}

// ---------------------------------------------------------------------------------------------
// Assembly input: one local OCI image layout directory (for example populated with
// `skopeo copy --all --preserve-digests docker://<ref>@<digest> oci:<dir>:<name>`). Only its
// content-addressed blobs are read; its own index.json is ignored.
// ---------------------------------------------------------------------------------------------
function openBlob(layout, digest) {
  const path = join(layout, 'blobs', 'sha256', digest.slice('sha256:'.length));
  let fd;
  try {
    fd = openSync(path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
  } catch {
    throw new Error(`Image layout is missing blob ${digest}`);
  }
  // Checked after opening, on the descriptor: the path must still name this same regular file
  // (not a link to it), so nothing swapped between lookup and use is ever read.
  try {
    const opened = fstatSync(fd);
    const link = lstatSync(path, { throwIfNoEntry: false });
    requireThat(opened.isFile() && link?.isFile() && opened.dev === link.dev && opened.ino === link.ino,
      `Image layout blob is not a regular file or changed while being opened: ${digest}`);
    return { fd, size: opened.size };
  } catch (error) {
    closeSync(fd);
    throw error;
  }
}

function layoutSource(layout, limits) {
  const sizes = new Map();
  const size = digest => {
    if (!sizes.has(digest)) {
      const { fd, size: bytes } = openBlob(layout, digest);
      closeSync(fd);
      sizes.set(digest, bytes);
    }
    return sizes.get(digest);
  };
  return {
    has: digest => Boolean(lstatSync(join(layout, 'blobs', 'sha256', digest.slice(7)), { throwIfNoEntry: false })?.isFile()),
    size,
    read: digest => {
      const { fd, size: bytes } = openBlob(layout, digest);
      try {
        requireThat(bytes <= limits.maxJsonBytes, `Image layout document exceeds its size limit: ${digest}`);
        const buffer = readExactly(fd, bytes, 0);
        requireThat(`sha256:${sha256(buffer)}` === digest, `Image layout blob does not match its digest: ${digest}`);
        return buffer;
      } finally {
        closeSync(fd);
      }
    },
  };
}

export function writeImageArchive({ layout, expected, output, limits = imageArchiveLimits }) {
  const source = layoutSource(layout, limits);
  const rootBytes = source.read(expected.digest);
  const root = { mediaType: JSON.parse(rootBytes.toString('utf8')).mediaType, digest: expected.digest, size: rootBytes.length };
  const closure = imageClosure(source, root, expected, limits);
  const out = openSync(output, 'wx+', 0o644);
  try {
    const member = (name, bytes) => {
      writeAll(out, imageTarHeader({ name, size: bytes.length }));
      writeAll(out, bytes);
      writeAll(out, Buffer.alloc((block - (bytes.length % block)) % block));
    };
    member('oci-layout', layoutBytes);
    member('index.json', canonicalImageIndex(expected, root));
    for (const digest of [...closure].sort()) {
      const { fd, size } = openBlob(layout, digest);
      try {
        requireThat(size === source.size(digest), `Image layout blob changed during assembly: ${digest}`);
        writeAll(out, imageTarHeader({ name: `blobs/sha256/${digest.slice(7)}`, size }));
        requireThat(`sha256:${hashRange(fd, 0, size, out)}` === digest, `Image layout blob does not match its digest: ${digest}`);
        writeAll(out, Buffer.alloc((block - (size % block)) % block));
      } finally {
        closeSync(fd);
      }
    }
    writeAll(out, Buffer.alloc(block * 2));
    // Prove the written archive with the importer's own verifier, on the same descriptor, before it
    // is bundled.
    const verified = verifyImageArchive(out, 0, fstatSync(out).size, expected, limits);
    closeSync(out);
    return verified;
  } catch (error) {
    try { closeSync(out); } catch { /* already closed */ }
    rmSync(output, { force: true });
    throw error;
  }
}
