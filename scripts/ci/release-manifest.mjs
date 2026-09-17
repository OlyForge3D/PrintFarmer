import { createHash } from 'node:crypto';
import { components, parseTag, requireThat } from './release-policy.mjs';

const digestPattern = /^sha256:[a-f0-9]{64}$/;
const commitPattern = /^[a-f0-9]{40}$/;
const platformPattern = /^[a-z0-9][a-z0-9._-]*$/;
const imagePattern = /^ghcr\.io\/olyforge3d\/printfarmer-[a-z0-9-]+@sha256:[a-f0-9]{64}$/;
const semanticVersionPattern = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/;

// Cross-channel sequence contract (see docs/DEPLOYMENT_UPDATE_STRATEGY.md):
// sequence = ((((major * 1000) + minor) * 100000) + patch) * 100000 + suffix.
// Stable releases use the reserved suffix (SEQUENCE_STABLE_SUFFIX), which sits
// strictly above every valid insider suffix so stable always outranks insider
// at equal major/minor/patch.
// All arithmetic is done in BigInt for exactness; the final value is checked
// against signed C# Int64 and Number.MAX_SAFE_INTEGER before converting to a
// JS Number, so the contract never emits an unsafe JSON integer. The supported
// range fits a signed 64-bit C# `long`/`Int64`, but not `int`; the manifest
// wire value remains a JSON integer.
export const SEQUENCE_MAJOR_MAX = 99;
export const SEQUENCE_MINOR_MAX = 999;
export const SEQUENCE_PATCH_MAX = 99_999;
export const SEQUENCE_PRERELEASE_MAX = 99_998;
export const SEQUENCE_STABLE_SUFFIX = 99_999;
export const MINIMUM_UPDATER_VERSION = '0.0.0';
const INT64_MAX = 9223372036854775807n;

export function deriveSequence(version) {
  const parsed = parseTag(`v${version}`);
  requireThat(!parsed.stage || parsed.stage === 'insider', 'Sequence version must be stable or insider');
  const major = BigInt(parsed.major);
  const minor = BigInt(parsed.minor);
  const patch = BigInt(parsed.patch);
  requireThat(parsed.stage || major >= 1n, 'Stable signed release major version must be greater than zero');
  requireThat(major <= BigInt(SEQUENCE_MAJOR_MAX), `Major version exceeds sequence encoding limit of ${SEQUENCE_MAJOR_MAX}`);
  requireThat(minor <= BigInt(SEQUENCE_MINOR_MAX), `Minor version exceeds sequence encoding limit of ${SEQUENCE_MINOR_MAX}`);
  requireThat(patch <= BigInt(SEQUENCE_PATCH_MAX), `Patch version exceeds sequence encoding limit of ${SEQUENCE_PATCH_MAX}`);
  let suffix;
  if (parsed.stage) {
    const prerelease = BigInt(parsed.sequence ?? 0);
    requireThat(prerelease >= 1n && prerelease <= BigInt(SEQUENCE_PRERELEASE_MAX),
      `Prerelease sequence exceeds encoding limit of ${SEQUENCE_PRERELEASE_MAX}`);
    suffix = prerelease;
  } else {
    suffix = BigInt(SEQUENCE_STABLE_SUFFIX);
  }
  const encoded = (((major * 1_000n + minor) * 100_000n) + patch) * 100_000n + suffix;
  requireThat(encoded <= INT64_MAX, 'Encoded sequence exceeds C# Int64 range');
  requireThat(encoded <= BigInt(Number.MAX_SAFE_INTEGER), 'Encoded sequence exceeds safe integer range');
  return Number(encoded);
}

function manifestPlatform(platform) {
  const normalized = platform.replaceAll('/', '-');
  validatePlatform(normalized, platform);
  return normalized;
}

function manifestPlatforms() {
  return [...new Set(Object.values(components).flatMap(policy => policy.platforms.map(manifestPlatform)))];
}

// Child digest keys are `${serviceId}/${platform}` (slash-separated), crossing
// every declared service with its declared platforms. orcaslicer-worker is
// amd64-only, so it contributes a single key; every other service contributes
// one key per platform in the bare top-level platform union. This yields the
// 11 keys the verifier's schema requires, in stable declaration order.
function serviceDigestKeys() {
  return Object.entries(components).flatMap(([id, policy]) =>
    policy.platforms.map(platform => `${id}/${manifestPlatform(platform)}`));
}

function validatePlatform(platform, label) {
  requireThat(typeof platform === 'string' && platformPattern.test(platform), `Invalid platform: ${label}`);
}

function validateDigest(digest, label) {
  requireThat(typeof digest === 'string' && digestPattern.test(digest), `Invalid digest: ${label}`);
}

export function validateManifestInput(release, imageDetails) {
  requireThat(release && ['stable', 'insider'].includes(release.channel), 'Invalid release channel');
  const parsed = parseTag(`v${release.version}`);
  requireThat(parsed.channel === release.channel, 'Manifest version does not match channel');
  requireThat(release.tag === `v${release.version}`, 'Manifest tag/version mismatch');
  requireThat(release.sourceBranch === (release.channel === 'stable' ? 'main' : 'development'),
    'Manifest source branch does not match channel');
  requireThat(commitPattern.test(release.sourceCommit ?? ''), 'Invalid manifest source commit');
  requireThat(/^[1-9][0-9]*$/.test(String(release.buildId ?? '')), 'Invalid manifest build ID');
  requireThat(Number.isSafeInteger(release.sequence) && release.sequence === deriveSequence(release.version),
    'Manifest sequence mismatch');
  requireThat(imageDetails && typeof imageDetails === 'object', 'Missing image verification details');
  const names = Object.keys(imageDetails);
  requireThat(names.length === Object.keys(components).length &&
    names.sort().join() === Object.keys(components).sort().join(), 'Incomplete manifest service set');

  for (const [name, policy] of Object.entries(components)) {
    const details = imageDetails[name];
    requireThat(details && typeof details.indexDigest === 'string', `Missing image index: ${name}`);
    validateDigest(details.indexDigest, `${name} index`);
    requireThat(Array.isArray(details.platforms) &&
      details.platforms.length === policy.platforms.length &&
      [...details.platforms].sort().join() === [...policy.platforms].sort().join(),
    `Missing, duplicate, or unexpected platforms: ${name}`);
    requireThat(details.platformDigests && typeof details.platformDigests === 'object',
      `Missing child digests: ${name}`);
    const digestNames = Object.keys(details.platformDigests);
    requireThat(digestNames.length === policy.platforms.length &&
      digestNames.sort().join() === [...policy.platforms].sort().join(),
    `Missing, duplicate, or unexpected child digests: ${name}`);
    for (const platform of policy.platforms) {
      validatePlatform(platform.replaceAll('/', '-'), `${name}/${platform}`);
      validateDigest(details.platformDigests[platform], `${name}/${platform}`);
    }
  }
}

export function buildManifest(release, imageDetails, options = {}) {
  const normalizedRelease = { ...release, sequence: release.sequence ?? deriveSequence(release.version) };
  validateManifestInput(normalizedRelease, imageDetails);
  const minimumUpdaterVersion = options.minimumUpdaterVersion ?? MINIMUM_UPDATER_VERSION;
  const services = Object.entries(components).map(([id, policy]) => {
    const details = imageDetails[id];
    return {
      id,
      image: `ghcr.io/olyforge3d/printfarmer-${id}@${details.indexDigest}`,
      platforms: policy.platforms.map(manifestPlatform),
    };
  });
  const platformDigestEntries = Object.entries(components).flatMap(([id, policy]) =>
    policy.platforms.map(platform =>
      [`${id}/${manifestPlatform(platform)}`, imageDetails[id].platformDigests[platform]]));
  const manifest = {
    schema: 1,
    tag: normalizedRelease.tag,
    version: normalizedRelease.version,
    channel: normalizedRelease.channel,
    sourceBranch: normalizedRelease.sourceBranch,
    sourceCommit: normalizedRelease.sourceCommit,
    buildId: String(normalizedRelease.buildId),
    sequence: normalizedRelease.sequence,
    managedUpdateEligible: true,
    services,
    platforms: manifestPlatforms(),
    platformDigests: Object.fromEntries(platformDigestEntries),
    minimumUpdaterVersion,
    ...(options.compatibility ? { compatibility: options.compatibility } : {}),
  };
  const bytes = `${JSON.stringify(manifest)}\n`;
  validateManifest(bytes);
  return bytes;
}

export function validateManifest(bytes, release, digests, imageDetails) {
  const manifest = JSON.parse(bytes);
  const requiredFields = ['schema', 'tag', 'version', 'channel', 'sourceBranch', 'sourceCommit', 'buildId',
    'sequence', 'managedUpdateEligible', 'services', 'platforms', 'platformDigests', 'minimumUpdaterVersion'];
  const permittedFields = [...requiredFields, 'compatibility'];
  requireThat(requiredFields.every(field => Object.hasOwn(manifest, field)) &&
    Object.keys(manifest).every(field => permittedFields.includes(field)),
  'Invalid managed update manifest fields');
  requireThat(manifest.schema === 1 && manifest.managedUpdateEligible === true,
    'Invalid managed update manifest header');
  requireThat(Array.isArray(manifest.services) && manifest.services.length === Object.keys(components).length,
    'Invalid managed update service set');
  if (release) {
    requireThat(manifest.tag === release.tag && manifest.version === release.version &&
      manifest.channel === release.channel && manifest.sourceBranch === release.sourceBranch &&
      manifest.sourceCommit === release.sourceCommit && String(manifest.buildId) === String(release.buildId) &&
      manifest.sequence === deriveSequence(release.version), 'Manifest release identity mismatch');
  }
  const ids = manifest.services.map(service => service?.id);
  requireThat(ids.length === Object.keys(components).length &&
    ids.join() === Object.keys(components).join(), 'Invalid managed update service IDs');
  for (const service of manifest.services) {
    requireThat(service && Object.keys(service).sort().join() === ['id', 'image', 'platforms'].sort().join(),
      'Invalid managed update service fields');
    requireThat(imagePattern.test(service.image), `Mutable or unapproved image reference: ${service.id}`);
    if (digests) requireThat(service.image === `ghcr.io/olyforge3d/printfarmer-${service.id}@${digests[service.id]}`,
      `Manifest image mismatch: ${service.id}`);
    const policy = components[service.id];
    requireThat(Array.isArray(service.platforms) &&
      service.platforms.join() === policy.platforms.map(manifestPlatform).join(),
    `Invalid manifest platforms: ${service.id}`);
  }
  const expectedPlatforms = manifestPlatforms();
  requireThat(Array.isArray(manifest.platforms) &&
    manifest.platforms.join() === expectedPlatforms.join(), 'Invalid manifest platform list');
  const expectedDigestKeys = serviceDigestKeys();
  requireThat(manifest.platformDigests &&
    Object.keys(manifest.platformDigests).join() === expectedDigestKeys.join(),
    'Invalid manifest child digest map');
  for (const [id, policy] of Object.entries(components)) {
    for (const platform of policy.platforms) {
      const key = `${id}/${manifestPlatform(platform)}`;
      validatePlatform(manifestPlatform(platform), key);
      validateDigest(manifest.platformDigests[key], key);
      if (imageDetails) {
        const sourceDigest = imageDetails[id]?.platformDigests?.[platform];
        requireThat(manifest.platformDigests[key] === sourceDigest,
          `Manifest child digest mismatch: ${key}`);
      }
    }
  }
  requireThat(typeof manifest.minimumUpdaterVersion === 'string' &&
      semanticVersionPattern.test(manifest.minimumUpdaterVersion),
    'Invalid minimum updater version');
  if (manifest.compatibility !== undefined) {
    requireThat(typeof manifest.compatibility === 'string', 'Invalid compatibility');
  }
  return manifest;
}

export function manifestDigest(bytes) {
  return `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
}