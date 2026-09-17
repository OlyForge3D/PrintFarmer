import { spawnSync } from 'node:child_process';
import { components, compareVersions, parseTag, requireThat } from './release-policy.mjs';

export function command(name, args, options = {}) {
  const result = spawnSync(name, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
    maxBuffer: 32 * 1024 * 1024, ...options });
  if (result.error) throw result.error;
  requireThat(result.status === 0, `${name} failed (${result.status}): ${result.stderr ?? ''}`);
  return result.stdout;
}

export const imageRepository = name => `ghcr.io/olyforge3d/printfarmer-${name}`;

export function inspectTag(reference, run = spawnSync) {
  const result = run('docker', ['buildx', 'imagetools', 'inspect', reference,
    '--format', '{"manifest":{{json .Manifest}},"image":{{json .Image}}}'],
  { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    // Only an explicit registry not-found is absence. Auth/network failures block publication.
    requireThat(/(?:manifest unknown|not found\s*$)/i.test(result.stderr ?? '') &&
      !/(?:unauthorized|denied|timeout|connection|no such host)/i.test(result.stderr ?? ''),
    `Cannot inspect registry tag ${reference}: ${result.stderr}`);
    return undefined;
  }
  const resultData = JSON.parse(result.stdout);
  const digest = resultData.manifest?.digest;
  requireThat(/^sha256:[a-f0-9]{64}$/.test(digest ?? ''), `Invalid registry digest: ${reference}`);
  const images = resultData.image?.config ? [resultData.image] : Object.values(resultData.image ?? {});
  const versions = images.map(image => image.config?.Labels?.['org.opencontainers.image.version']);
  requireThat(versions.length > 0 && versions.every(version => typeof version === 'string') &&
    new Set(versions).size === 1, `Missing consistent version labels: ${reference}`);
  parseTag(`v${versions[0]}`);
  return { digest, version: versions[0] };
}

export function verifyImages(version, sourceCommit, digests, run = command) {
  requireThat(Object.keys(digests).sort().join() === Object.keys(components).sort().join(), 'Incomplete image set');
  for (const [name, { platforms }] of Object.entries(components)) {
    const digest = digests[name];
    requireThat(/^sha256:[a-f0-9]{64}$/.test(digest ?? ''), `Missing image digest: ${name}`);
    const index = JSON.parse(run('docker', ['buildx', 'imagetools', 'inspect', `${imageRepository(name)}@${digest}`, '--raw']));
    requireThat(Array.isArray(index.manifests), `Missing image index: ${name}`);
    const runtime = index.manifests.filter(item =>
      item.annotations?.['vnd.docker.reference.type'] !== 'attestation-manifest');
    requireThat(runtime.map(item => `${item.platform?.os}/${item.platform?.architecture}`).sort().join() ===
      [...platforms].sort().join(), `Missing, duplicate, or unexpected platforms: ${name}`);
    for (const item of runtime) {
      requireThat(/^sha256:[a-f0-9]{64}$/.test(item.digest ?? '') &&
        index.manifests.some(proof => proof.annotations?.['vnd.docker.reference.type'] === 'attestation-manifest' &&
          proof.annotations?.['vnd.docker.reference.digest'] === item.digest),
      `Missing platform provenance/SBOM: ${name}`);
      const image = JSON.parse(run('docker', ['buildx', 'imagetools', 'inspect',
        `${imageRepository(name)}@${item.digest}`, '--format', '{{json .Image}}']));
      requireThat(image.config?.Labels?.['org.opencontainers.image.version'] === version &&
        image.config.Labels['org.opencontainers.image.revision'] === sourceCommit &&
        image.config.Labels['org.printfarmer.release-channel'] === parseTag(`v${version}`).channel,
      `Image version/source mismatch: ${name}`);
    }
  }
}

export function immutableTags(version) {
  const parsed = parseTag(`v${version}`);
  return parsed.channel === 'stable' ? [version, `stable-${version}`] : [version];
}

export function rejectExistingImages(version, inspect = inspectTag) {
  for (const name of Object.keys(components)) {
    for (const tag of immutableTags(version)) {
      requireThat(!inspect(`${imageRepository(name)}:${tag}`), `Image version already exists: ${name}:${tag}`);
    }
  }
}

export function publishImageTags(version, digests, inspect = inspectTag, run = command) {
  rejectExistingImages(version, inspect);
  const create = (name, tag, immutable = false) => {
    const reference = `${imageRepository(name)}:${tag}`;
    if (immutable) requireThat(!inspect(reference), `Image version appeared during publication: ${reference}`);
    run('docker', ['buildx', 'imagetools', 'create', '--tag', reference, `${imageRepository(name)}@${digests[name]}`]);
    requireThat(inspect(reference)?.digest === digests[name], `Published tag verification failed: ${reference}`);
  };
  for (const name of Object.keys(components)) {
    for (const tag of immutableTags(version)) create(name, tag, true);
  }
  const parsed = parseTag(`v${version}`);
  if (parsed.channel === 'stable') {
    for (const name of Object.keys(components)) {
      for (const tag of [`${parsed.major}.${parsed.minor}`, parsed.major, 'latest']) {
        const existing = inspect(`${imageRepository(name)}:${tag}`);
        if (existing) {
          requireThat(parseTag(`v${existing.version}`).channel === 'stable', `Cross-channel alias: ${name}:${tag}`);
          if (compareVersions(version, existing.version) <= 0) continue;
        }
        create(name, tag);
      }
    }
  }
}
