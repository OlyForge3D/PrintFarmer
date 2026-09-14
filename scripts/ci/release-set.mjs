import { readFileSync } from 'node:fs';
import {
  compareVersions, components, identityLabels, parseTag, requireThat, validateCompleteSet,
} from './release-policy.mjs';
import { command } from './release-github.mjs';
import { emitPublicReleaseAssets, privateSetPath, readPrivateJson, verifyAuthorization, writeAuthorizationSet } from './release-authorization.mjs';

export function inspectCompleteSet(record, digests, run = command) {
  const set = { schema: 1, identity: record, managedEligible: false, images: {} };
  for (const [name, expectedPlatforms] of Object.entries(components)) {
    const digest = digests[name];
    requireThat(/^sha256:[a-f0-9]{64}$/.test(digest), `Missing digest: ${name}`);
    const image = `ghcr.io/olyforge3d/printfarmer-${name}`;
    const index = JSON.parse(run('docker', ['buildx', 'imagetools', 'inspect', `${image}@${digest}`, '--raw']));
    requireThat(Array.isArray(index.manifests), `Missing multi-platform index/provenance: ${name}`);
    const declaredPlatforms = index.manifests
      .filter(item => item.annotations?.['vnd.docker.reference.type'] !== 'attestation-manifest')
      .map(item => `${item.platform?.os}/${item.platform?.architecture}`).sort();
    requireThat(declaredPlatforms.join() === [...expectedPlatforms].sort().join(),
      `Undeclared or missing platform: ${name}`);
    const platforms = {};
    for (const platform of expectedPlatforms) {
      const matches = index.manifests.filter(item => `${item.platform?.os}/${item.platform?.architecture}` === platform);
      requireThat(matches.length === 1, `Missing/duplicate platform: ${name}/${platform}`);
      const platformDigest = matches[0].digest;
      requireThat(index.manifests.some(item =>
        item.annotations?.['vnd.docker.reference.type'] === 'attestation-manifest' &&
        item.annotations?.['vnd.docker.reference.digest'] === platformDigest),
      `Missing platform provenance/SBOM descriptor: ${name}/${platform}`);
      const config = JSON.parse(run('docker', ['buildx', 'imagetools', 'inspect',
        `${image}@${platformDigest}`, '--format', '{{json .Image}}']));
      platforms[platform] = { digest: platformDigest, labels: Object.fromEntries(
        Object.keys(identityLabels(record)).map(key => [key, config.config.Labels?.[key]])) };
    }
    set.images[name] = { digest, platforms };
  }
  validateCompleteSet(record, set);
  return set;
}

export function publishImmutableTags(record, set, inspect, create) {
  validateCompleteSet(record, set);
  for (const [component, image] of Object.entries(set.images)) {
    const repository = `ghcr.io/olyforge3d/printfarmer-${component}`;
    const tag = `${repository}:${record.canonicalVersion}`;
    const existing = inspect(tag);
    requireThat(!existing || existing === image.digest, `Immutable image tag conflict: ${tag}`);
  }
  for (const [component, image] of Object.entries(set.images)) {
    const repository = `ghcr.io/olyforge3d/printfarmer-${component}`;
    const tag = `${repository}:${record.canonicalVersion}`;
    if (!inspect(tag)) create(tag, `${repository}@${image.digest}`);
    requireThat(inspect(tag) === image.digest, `Published image tag differs: ${tag}`);
  }
}

export function registryTagInspection(output) {
  let inspected;
  try { inspected = JSON.parse(output); } catch { throw new Error('Registry returned invalid image inspection JSON'); }
  const digest = inspected?.manifest?.digest;
  const image = inspected?.image;
  const entries = image?.config ? [image] : Object.values(image ?? {});
  const versions = entries.map(value => value?.config?.Labels?.['org.opencontainers.image.version']);
  requireThat(/^sha256:[a-f0-9]{64}$/.test(digest), 'Registry returned no image manifest digest');
  requireThat(entries.length > 0 && versions.every(version =>
    typeof version === 'string' && /^\d+\.\d+\.\d+(?:-(?:insider|beta|rc)\.\d+)?$/.test(version)) &&
    new Set(versions).size === 1, 'Registry image platforms lack one canonical version label');
  return { digest, version: versions[0] };
}

export function plannedReleaseAliases(record, set, inspect = () => undefined) {
  validateCompleteSet(record, set);
  const parsed = parseTag(record.sourceTag);
  return Object.fromEntries(Object.entries(set.images).map(([component, image]) => {
    const aliases = record.channel === 'stable'
      ? [
      { value: record.canonicalVersion, mutable: false },
      { value: `stable-${record.canonicalVersion}`, mutable: false, retained: true },
      { value: `${parsed.major}.${parsed.minor}`, mutable: true },
      ...[parsed.major, 'latest'].flatMap(value => {
        const existing = inspect(`ghcr.io/olyforge3d/printfarmer-${component}:${value}`);
        if (!existing || existing.digest === image.digest) return [{ value, mutable: true }];
        requireThat(parseTag(`v${existing.version}`).channel === 'stable',
          `Cross-channel alias movement rejected for ${component}:${value}`);
        return compareVersions(record.canonicalVersion, existing.version) > 0 ? [{ value, mutable: true }] : [];
      }),
    ]
      : [{ value: record.canonicalVersion, mutable: false }];
    return [component,
    aliases.map(alias => ({
      tag: `ghcr.io/olyforge3d/printfarmer-${component}:${alias.value}`,
      digest: image.digest,
      mutable: alias.mutable,
      ...(alias.retained ? { retained: true } : {}),
      channel: record.channel,
    }))];
  }));
}

export function publishReleaseAliases(record, set, inspect, create) {
  const plan = plannedReleaseAliases(record, set, inspect);
  requireThat(Object.values(plan).flat().every(alias => !alias.mutable || record.channel === 'stable'),
    'Only stable aliases may be mutable');
  for (const aliases of Object.values(plan)) {
    for (const alias of aliases) {
      const existing = inspect(alias.tag);
      if (!existing) continue;
      requireThat(existing.digest === alias.digest || alias.mutable,
        `Immutable image tag conflict: ${alias.tag}`);
      if (alias.mutable && existing.digest !== alias.digest) {
        requireThat(existing.version && compareVersions(record.canonicalVersion, existing.version) > 0,
          `Alias regression or unverified existing version: ${alias.tag}`);
        requireThat(record.channel === 'stable' && parseTag(`v${existing.version}`).channel === 'stable',
          `Cross-channel alias movement rejected: ${alias.tag}`);
      }
    }
  }
  for (const aliases of Object.values(plan)) {
    for (const alias of aliases) {
      const existing = inspect(alias.tag);
      if (!existing) create(alias.tag, alias.digest);
      else if (existing.digest !== alias.digest) create(alias.tag, alias.digest);
      const published = inspect(alias.tag);
      requireThat(published?.digest === alias.digest,
        `Alias compare-and-set conflict: ${alias.tag}`);
    }
  }
  return plan;
}

function main() {
  const record = verifyAuthorization(process.env, command);
  if (process.argv[2] === 'inspect') {
    const digests = Object.fromEntries(Object.keys(components).map(name =>
      [name, readFileSync(`artifacts/digest-${name}/digest-${name}.txt`, 'utf8').trim()]));
    const set = inspectCompleteSet(record, digests);
    writeAuthorizationSet(record, set);
    emitPublicReleaseAssets(record, set);
  } else if (process.argv[2] === 'tag') {
    const set = readPrivateJson(privateSetPath);
    publishImmutableTags(record, set, tag => {
      try {
        const output = command('docker', ['buildx', 'imagetools', 'inspect', tag]);
        const digest = output.match(/^Digest:\s+(sha256:[a-f0-9]{64})$/m)?.[1];
        requireThat(digest, 'Registry returned no digest');
        return digest;
      } catch (error) {
        // Authentication/network failures must not be interpreted as an absent tag.
        if (/\bmanifest unknown\b|\bMANIFEST_UNKNOWN\b/.test(String(error.stderr))) return undefined;
        throw error;
      }
    }, (tag, source) => command('docker', ['buildx', 'imagetools', 'create', '--tag', tag, source]));
  } else if (process.argv[2] === 'alias') {
    const set = readPrivateJson(privateSetPath);
    publishReleaseAliases(record, set, tag => {
      try {
        const output = command('docker', ['buildx', 'imagetools', 'inspect', tag, '--format', '{{json .}}']);
        return registryTagInspection(output);
      } catch (error) {
        if (/\bmanifest unknown\b|\bMANIFEST_UNKNOWN\b/.test(String(error.stderr))) return undefined;
        throw error;
      }
    }, (tag, digest) => command('docker', ['buildx', 'imagetools', 'create', '--tag', tag,
      `${tag.slice(0, tag.lastIndexOf(':'))}@${digest}`]));
  } else throw new Error('Expected inspect or tag');
}

if (process.argv[1]?.endsWith('release-set.mjs')) {
  try { main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
