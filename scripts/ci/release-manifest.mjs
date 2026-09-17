import { createHash } from 'node:crypto';
import { components, parseTag, requireThat } from './release-policy.mjs';

const digestPattern = /^sha256:[a-f0-9]{64}$/;
const commitPattern = /^[a-f0-9]{40}$/;
const platformPattern = /^[a-z0-9][a-z0-9._-]*$/;
const imagePattern = /^ghcr\.io\/olyforge3d\/printfarmer-[a-z0-9-]+@sha256:[a-f0-9]{64}$/;

// Cross-channel sequence contract (see docs/DEPLOYMENT_UPDATE_STRATEGY.md):
// a collision-free, stable-dominant, positional mixed-radix encoding of
// major/minor/patch plus a channel-aware suffix. Each field has an explicit,
// validated upper bound so an out-of-range component throws instead of
// silently overflowing into the next field (the prior weighted-decimal
// encoding collided once patch or the insider suffix reached 1000). Stable
// releases always encode a suffix of SEQUENCE_STABLE_SUFFIX, which is greater
// than any valid prerelease suffix, so a stable release outranks every
// prerelease of the same major.minor.patch (the prior encoding inverted this:
// an insider suffix >= 1 always outranked the matching stable release).
// All arithmetic is done in BigInt for exactness; the final value is checked
// against Number.MAX_SAFE_INTEGER before converting to a JS Number, so the
// contract never emits an unsafe-integer sequence. The encoded range
// (well under 2**53) also fits an ordinary C# `long`/`int` with no wire
// format change, so no backend deserialization change is required.
export const SEQUENCE_MAJOR_MAX = 99;
export const SEQUENCE_MINOR_MAX = 999;
export const SEQUENCE_PATCH_MAX = 99999;
export const SEQUENCE_PRERELEASE_MAX = 99998;
export const SEQUENCE_STABLE_SUFFIX = 99999;
const SEQUENCE_SUFFIX_WIDTH = 100000n;
const SEQUENCE_PATCH_WIDTH = 100000n;
const SEQUENCE_MINOR_WIDTH = 1000n;

export function deriveSequence(version) {
  const parsed = parseTag(`v${version}`);
  const major = BigInt(parsed.major);
  const minor = BigInt(parsed.minor);
  const patch = BigInt(parsed.patch);
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
  const encoded = major * SEQUENCE_MINOR_WIDTH * SEQUENCE_PATCH_WIDTH * SEQUENCE_SUFFIX_WIDTH +
    minor * SEQUENCE_PATCH_WIDTH * SEQUENCE_SUFFIX_WIDTH +
    patch * SEQUENCE_SUFFIX_WIDTH +
    suffix;
  requireThat(encoded <= BigInt(Number.MAX_SAFE_INTEGER), 'Encoded sequence exceeds safe integer range');
  return Number(encoded);
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
  const services = Object.entries(components).map(([id, policy]) => {
    const details = imageDetails[id];
    const platforms = [...policy.platforms];
    return {
      id,
      image: `ghcr.io/olyforge3d/printfarmer-${id}@${details.indexDigest}`,
      platforms: platforms.map(platform => platform.replaceAll('/', '-')),
    };
  });
  const platformEntries = Object.entries(components).flatMap(([id, policy]) =>
    policy.platforms.map(platform => [`${id}-${platform.replaceAll('/', '-')}`,
      imageDetails[id].platformDigests[platform]]));
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
    platforms: platformEntries.map(([platform]) => platform),
    platformDigests: Object.fromEntries(platformEntries),
    ...(options.minimumUpdaterVersion ? { minimumUpdaterVersion: options.minimumUpdaterVersion } : {}),
    ...(options.compatibility ? { compatibility: options.compatibility } : {}),
  };
  const bytes = `${JSON.stringify(manifest)}\n`;
  validateManifest(bytes);
  return bytes;
}

export function validateManifest(bytes, release, digests, imageDetails) {
  const manifest = JSON.parse(bytes);
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
  requireThat(new Set(ids).size === ids.length &&
    ids.sort().join() === Object.keys(components).sort().join(), 'Invalid managed update service IDs');
  for (const service of manifest.services) {
    requireThat(imagePattern.test(service.image), `Mutable or unapproved image reference: ${service.id}`);
    if (digests) requireThat(service.image === `ghcr.io/olyforge3d/printfarmer-${service.id}@${digests[service.id]}`,
      `Manifest image mismatch: ${service.id}`);
    const policy = components[service.id];
    requireThat(Array.isArray(service.platforms) &&
      service.platforms.length === policy.platforms.length &&
      [...service.platforms].sort().join() === policy.platforms.map(platform => platform.replaceAll('/', '-')).sort().join(),
    `Invalid manifest platforms: ${service.id}`);
  }
  const expectedPlatformEntries = Object.entries(components).flatMap(([id, policy]) =>
    policy.platforms.map(platform => ({ id, platform, key: `${id}-${platform.replaceAll('/', '-')}` })));
  const expectedPlatforms = expectedPlatformEntries.map(entry => entry.key);
  requireThat(Array.isArray(manifest.platforms) &&
    manifest.platforms.join() === expectedPlatforms.join(), 'Invalid manifest platform list');
  requireThat(manifest.platformDigests && Object.keys(manifest.platformDigests).join() === expectedPlatforms.join(),
    'Invalid manifest child digest map');
  for (const entry of expectedPlatformEntries) {
    requireThat(platformPattern.test(entry.key), `Invalid manifest platform: ${entry.key}`);
    validateDigest(manifest.platformDigests[entry.key], entry.key);
    if (imageDetails) {
      requireThat(manifest.platformDigests[entry.key] === imageDetails[entry.id].platformDigests[entry.platform],
        `Manifest child digest mismatch: ${entry.key}`);
    }
  }
  if (manifest.minimumUpdaterVersion !== undefined) {
    requireThat(typeof manifest.minimumUpdaterVersion === 'string' &&
      /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(manifest.minimumUpdaterVersion),
    'Invalid minimum updater version');
  }
  if (manifest.compatibility !== undefined) {
    requireThat(typeof manifest.compatibility === 'string', 'Invalid compatibility');
  }
  return manifest;
}

export function manifestDigest(bytes) {
  return `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
}
