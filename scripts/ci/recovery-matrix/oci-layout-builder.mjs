import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import {
  cpSync,
  existsSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { basename, join, resolve } from 'node:path';

import { components, requireThat } from '../release-policy.mjs';

const ociIndex = 'application/vnd.oci.image.index.v1+json';
const ociManifest = 'application/vnd.oci.image.manifest.v1+json';
const ociConfig = 'application/vnd.oci.image.config.v1+json';
const ociLayer = 'application/vnd.oci.image.layer.v1.tar';
const dockerManifest = 'application/vnd.docker.distribution.manifest.v2+json';
const dockerConfig = 'application/vnd.docker.container.image.v1+json';

const digest = bytes => `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
const digestHex = bytes => createHash('sha256').update(bytes).digest('hex');
const platformParts = platform => {
  const [os, architecture] = platform.split('/');
  return { os, architecture };
};

export function imageRepository(id) {
  return `ghcr.io/olyforge3d/printfarmer-${id}`;
}

export function ensureOciLayout(layout) {
  mkdirSync(join(layout, 'blobs', 'sha256'), { recursive: true });
  writeFileSync(join(layout, 'oci-layout'), '{"imageLayoutVersion":"1.0.0"}');
  if (!existsSync(join(layout, 'index.json'))) {
    writeFileSync(join(layout, 'index.json'), '{"schemaVersion":2,"mediaType":"application/vnd.oci.image.index.v1+json","manifests":[]}');
  }
}

export function writeBlob(layout, bytes) {
  ensureOciLayout(layout);
  const buffer = Buffer.isBuffer(bytes) ? bytes : Buffer.from(bytes);
  const hex = digestHex(buffer);
  const path = join(layout, 'blobs', 'sha256', hex);
  if (!existsSync(path)) writeFileSync(path, buffer);
  return { digest: `sha256:${hex}`, size: buffer.length };
}

export function tinyPlatformManifest(layout, {
  platform,
  name,
  version,
  labels = {},
}) {
  const layer = Buffer.alloc(1024);
  const layerDescriptor = { mediaType: ociLayer, ...writeBlob(layout, layer) };
  const config = Buffer.from(`${JSON.stringify({
    created: '2026-09-26T00:00:00Z',
    ...platformParts(platform),
    config: {
      Labels: {
        'org.opencontainers.image.title': name,
        'org.opencontainers.image.version': version,
        ...labels,
      },
    },
    rootfs: {
      type: 'layers',
      diff_ids: [layerDescriptor.digest],
    },
    history: [{ created: '2026-09-26T00:00:00Z', created_by: 'printfarmer recovery fixture' }],
  })}\n`);
  const configDescriptor = { mediaType: ociConfig, ...writeBlob(layout, config) };
  const manifest = Buffer.from(`${JSON.stringify({
    schemaVersion: 2,
    mediaType: ociManifest,
    config: configDescriptor,
    layers: [layerDescriptor],
  })}\n`);
  return {
    platform,
    descriptor: { mediaType: ociManifest, ...writeBlob(layout, manifest), platform: platformParts(platform) },
  };
}

export function writeImageIndex(layout, {
  reference,
  manifests,
  mediaType = ociIndex,
}) {
  requireThat(Array.isArray(manifests) && manifests.length > 0, 'image index requires at least one manifest');
  const index = Buffer.from(`${JSON.stringify({
    schemaVersion: 2,
    mediaType,
    manifests: manifests.map(({ descriptor }) => descriptor),
  })}\n`);
  const root = writeBlob(layout, index);
  return {
    reference,
    mediaType,
    indexDigest: root.digest,
    platforms: manifests.map(manifest => manifest.platform),
    platformDigests: Object.fromEntries(manifests.map(manifest => [manifest.platform, manifest.descriptor.digest])),
  };
}

export function addTinyImage(layout, {
  id,
  version,
  platforms = components[id]?.platforms,
  reference = `${imageRepository(id)}:${version}`,
  labels = {},
}) {
  requireThat(Array.isArray(platforms) && platforms.length > 0, `platforms are required for ${id}`);
  const manifests = platforms.map(platform => tinyPlatformManifest(layout, {
    platform,
    name: `printfarmer-${id}`,
    version,
    labels: { 'org.printfarmer.fixture-component': id, ...labels },
  }));
  return writeImageIndex(layout, { reference, manifests });
}

export function addDockerArchiveImage(layout, {
  archive,
  scratch,
  reference,
  platform = 'linux/amd64',
  extraPlatforms = [],
}) {
  ensureOciLayout(layout);
  rmSync(scratch, { recursive: true, force: true });
  mkdirSync(scratch, { recursive: true });
  const tar = process.platform === 'win32' ? 'tar.exe' : 'tar';
  execFileSync(tar, ['-xf', archive, '-C', scratch], { stdio: ['ignore', 'pipe', 'pipe'] });
  const manifestList = JSON.parse(readFileSync(join(scratch, 'manifest.json'), 'utf8'));
  requireThat(Array.isArray(manifestList) && manifestList.length === 1, `docker archive must contain one image: ${archive}`);
  const entry = manifestList[0];
  const configBytes = readFileSync(join(scratch, entry.Config));
  const configDescriptor = { mediaType: dockerConfig, ...writeBlob(layout, configBytes) };
  const layers = entry.Layers.map(layerPath => {
    const bytes = readFileSync(join(scratch, layerPath));
    return { mediaType: ociLayer, ...writeBlob(layout, bytes) };
  });
  requireThat(layers.length > 0, `docker archive image has no layers: ${archive}`);
  const manifestBytes = Buffer.from(`${JSON.stringify({
    schemaVersion: 2,
    mediaType: dockerManifest,
    config: configDescriptor,
    layers,
  })}\n`);
  const platformDescriptor = {
    platform,
    descriptor: {
      mediaType: dockerManifest,
      ...writeBlob(layout, manifestBytes),
      platform: platformParts(platform),
    },
  };
  const synthetic = extraPlatforms.map(extra => tinyPlatformManifest(layout, {
    platform: extra,
    name: basename(reference),
    version: reference,
    labels: { 'org.printfarmer.fixture-synthetic-platform': 'true' },
  }));
  return writeImageIndex(layout, {
    reference,
    manifests: [platformDescriptor, ...synthetic],
  });
}

export function buildC2ImageLayout({
  repo,
  runRoot,
  prior,
  target,
  run,
}) {
  const layout = resolve(runRoot, 'oci-layout');
  rmSync(layout, { recursive: true, force: true });
  ensureOciLayout(layout);
  const imageScratch = join(runRoot, 'image-scratch');
  mkdirSync(imageScratch, { recursive: true });
  let sourceCommit;
  try {
    sourceCommit = run('git', ['--no-pager', 'rev-parse', 'HEAD'], { cwd: repo }).trim();
  } catch {
    sourceCommit = run('git.exe', ['--no-pager', 'rev-parse', 'HEAD'], { cwd: repo }).trim();
  }
  const priorTag = `printfarmer-c2-monolith-prior:${prior.version}`;
  const targetTag = `printfarmer-c2-monolith-target:${target.version}`;
  const priorSlicerTag = `printfarmer-c2-slicer-host-prior:${prior.version}`;
  const targetSlicerTag = `printfarmer-c2-slicer-host-target:${target.version}`;
  const buildEnvironment = { ...process.env, DOCKER_BUILDKIT: '1' };
  run('docker', [
    'build',
    repo,
    '--file', join(repo, 'scripts/docker/dockerfiles/Dockerfile.multistage'),
    '--target', 'monolith-runtime',
    '--tag', priorTag,
    '--build-arg', `GIT_SHA=${sourceCommit}`,
    '--build-arg', `VITE_GIT_SHA=${sourceCommit}`,
    '--build-arg', `BUILD_VERSION=${prior.version}`,
    '--build-arg', `VCS_REF=${sourceCommit}`,
  ], { cwd: repo, stdio: ['ignore', 'inherit', 'pipe'], env: buildEnvironment });

  const deriveDockerfile = join(runRoot, 'Dockerfile.monolith-target');
  writeFileSync(deriveDockerfile, [
    `FROM ${priorTag}`,
    `LABEL org.printfarmer.recovery-fixture-target="${target.version}"`,
    '',
  ].join('\n'));
  run('docker', ['build', runRoot, '--file', deriveDockerfile, '--tag', targetTag], {
    cwd: repo,
    stdio: ['ignore', 'inherit', 'pipe'],
    env: buildEnvironment,
  });
  run('docker', [
    'build',
    repo,
    '--file', join(repo, 'scripts/docker/dockerfiles/Dockerfile.multistage'),
    '--target', 'slicer-host-runtime',
    '--tag', priorSlicerTag,
    '--build-arg', `GIT_SHA=${sourceCommit}`,
    '--build-arg', `VITE_GIT_SHA=${sourceCommit}`,
    '--build-arg', `BUILD_VERSION=${prior.version}`,
    '--build-arg', `VCS_REF=${sourceCommit}`,
  ], { cwd: repo, stdio: ['ignore', 'inherit', 'pipe'], env: buildEnvironment });
  const deriveSlicerDockerfile = join(runRoot, 'Dockerfile.slicer-host-target');
  writeFileSync(deriveSlicerDockerfile, [
    `FROM ${priorSlicerTag}`,
    `LABEL org.printfarmer.recovery-fixture-target="${target.version}"`,
    '',
  ].join('\n'));
  run('docker', ['build', runRoot, '--file', deriveSlicerDockerfile, '--tag', targetSlicerTag], {
    cwd: repo,
    stdio: ['ignore', 'inherit', 'pipe'],
    env: buildEnvironment,
  });

  const priorArchive = join(imageScratch, 'monolith-prior.docker.tar');
  const targetArchive = join(imageScratch, 'monolith-target.docker.tar');
  const priorSlicerArchive = join(imageScratch, 'slicer-host-prior.docker.tar');
  const targetSlicerArchive = join(imageScratch, 'slicer-host-target.docker.tar');
  run('docker', ['save', priorTag, '--output', priorArchive], { cwd: repo });
  run('docker', ['save', targetTag, '--output', targetArchive], { cwd: repo });
  run('docker', ['save', priorSlicerTag, '--output', priorSlicerArchive], { cwd: repo });
  run('docker', ['save', targetSlicerTag, '--output', targetSlicerArchive], { cwd: repo });

  const priorImages = Object.fromEntries(Object.keys(components).map(id => [id, addTinyImage(layout, {
    id,
    version: prior.version,
    labels: { 'org.printfarmer.fixture-release': 'prior' },
  })]));
  priorImages.monolith = addDockerArchiveImage(layout, {
    archive: priorArchive,
    scratch: join(imageScratch, 'extract-prior-monolith'),
    reference: `${imageRepository('monolith')}:${prior.version}`,
    extraPlatforms: ['linux/arm64'],
  });
  priorImages['slicer-host'] = addDockerArchiveImage(layout, {
    archive: priorSlicerArchive,
    scratch: join(imageScratch, 'extract-prior-slicer-host'),
    reference: `${imageRepository('slicer-host')}:${prior.version}`,
    extraPlatforms: ['linux/arm64'],
  });
  for (const serviceId of ['api']) {
    priorImages[serviceId] = addDockerArchiveImage(layout, {
      archive: priorArchive,
      scratch: join(imageScratch, `extract-prior-${serviceId}`),
      reference: `${imageRepository(serviceId)}:${prior.version}`,
      extraPlatforms: ['linux/arm64'],
    });
  }

  const targetImages = Object.fromEntries(Object.keys(components).map(id => [id, addTinyImage(layout, {
    id,
    version: target.version,
    labels: { 'org.printfarmer.fixture-release': 'target' },
  })]));
  targetImages.monolith = addDockerArchiveImage(layout, {
    archive: targetArchive,
    scratch: join(imageScratch, 'extract-target-monolith'),
    reference: `${imageRepository('monolith')}:${target.version}`,
    extraPlatforms: ['linux/arm64'],
  });
  targetImages['slicer-host'] = addDockerArchiveImage(layout, {
    archive: targetSlicerArchive,
    scratch: join(imageScratch, 'extract-target-slicer-host'),
    reference: `${imageRepository('slicer-host')}:${target.version}`,
    extraPlatforms: ['linux/arm64'],
  });
  for (const serviceId of ['api']) {
    targetImages[serviceId] = addDockerArchiveImage(layout, {
      archive: targetArchive,
      scratch: join(imageScratch, `extract-target-${serviceId}`),
      reference: `${imageRepository(serviceId)}:${target.version}`,
      extraPlatforms: ['linux/arm64'],
    });
  }

  const sourceInfrastructureLock = JSON.parse(readFileSync(join(repo, 'scripts/docker/infrastructure-images.lock.json'), 'utf8'));
  const infraById = Object.fromEntries(sourceInfrastructureLock.images.map(image => [image.id, image]));

  run('docker', ['pull', 'postgres:16-alpine'], { cwd: repo, stdio: ['ignore', 'inherit', 'pipe'] });
  const postgresArchive = join(imageScratch, 'postgres.docker.tar');
  run('docker', ['save', 'postgres:16-alpine', '--output', postgresArchive], { cwd: repo });
  const postgres = addDockerArchiveImage(layout, {
    archive: postgresArchive,
    scratch: join(imageScratch, 'extract-postgres'),
    reference: 'docker.io/library/postgres:16-alpine',
  });
  const nginx = addTinyImage(layout, {
    id: 'nginx',
    version: target.version,
    platforms: Object.keys(infraById.nginx.platforms),
    reference: infraById.nginx.reference,
    labels: { 'org.printfarmer.fixture-infrastructure': 'nginx' },
  });
  const mssql = addTinyImage(layout, {
    id: 'mssql',
    version: target.version,
    platforms: Object.keys(infraById.mssql.platforms),
    reference: infraById.mssql.reference,
    labels: { 'org.printfarmer.fixture-infrastructure': 'mssql' },
  });
  const infrastructureLock = {
    schema: 1,
    kind: 'printfarmer-infrastructure-images-lock',
    images: [
      {
        id: 'mssql',
        reference: infraById.mssql.reference,
        mediaType: mssql.mediaType,
        digest: mssql.indexDigest,
        platforms: mssql.platformDigests,
      },
      {
        id: 'nginx',
        reference: infraById.nginx.reference,
        mediaType: nginx.mediaType,
        digest: nginx.indexDigest,
        platforms: nginx.platformDigests,
      },
      {
        id: 'postgres',
        reference: infraById.postgres.reference,
        mediaType: postgres.mediaType,
        digest: postgres.indexDigest,
        platforms: postgres.platformDigests,
      },
    ],
  };
  return { layout, priorImages, targetImages, infrastructureLock };
}
