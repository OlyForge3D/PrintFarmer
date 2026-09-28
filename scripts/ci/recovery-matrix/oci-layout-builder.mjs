import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import {
  existsSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { basename, join, resolve } from 'node:path';

import { components, requireThat } from '../release-policy.mjs';
import { defaultCell } from './cell-runtime.mjs';
import { requiredInfrastructureIds, topologyFor } from './topologies.mjs';

const ociIndex = 'application/vnd.oci.image.index.v1+json';
const ociManifest = 'application/vnd.oci.image.manifest.v1+json';
const ociConfig = 'application/vnd.oci.image.config.v1+json';
const ociLayer = 'application/vnd.oci.image.layer.v1.tar';
const dockerManifest = 'application/vnd.docker.distribution.manifest.v2+json';
const dockerConfig = 'application/vnd.docker.container.image.v1+json';

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
  try {
    writeFileSync(path, buffer, { flag: 'wx' });
  } catch (error) {
    if (error.code !== 'EEXIST') throw error;
  }
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
  cell = defaultCell,
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
  const buildEnvironment = { ...process.env, DOCKER_BUILDKIT: '1' };
  const topology = topologyFor(cell.topology);
  const realServiceIds = [...new Set([...topology.serviceIds(cell.workers), 'api', 'slicer-host'])];
  const archives = Object.fromEntries(realServiceIds.map((serviceId) => [
    serviceId,
    buildServiceArchives({
      repo,
      runRoot,
      imageScratch,
      run,
      buildEnvironment,
      sourceCommit,
      serviceId,
      prior,
      target,
    }),
  ]));

  const priorImages = Object.fromEntries(Object.keys(components).map(id => [id, addTinyImage(layout, {
    id,
    version: prior.version,
    labels: { 'org.printfarmer.fixture-release': 'prior' },
  })]));
  for (const serviceId of realServiceIds) {
    priorImages[serviceId] = addDockerArchiveImage(layout, {
      archive: archives[serviceId].priorArchive,
      scratch: join(imageScratch, `extract-prior-${serviceId}`),
      reference: `${imageRepository(serviceId)}:${prior.version}`,
      extraPlatforms: components[serviceId]?.platforms?.includes('linux/arm64') ? ['linux/arm64'] : [],
    });
  }

  const targetImages = Object.fromEntries(Object.keys(components).map(id => [id, addTinyImage(layout, {
    id,
    version: target.version,
    labels: { 'org.printfarmer.fixture-release': 'target' },
  })]));
  for (const serviceId of realServiceIds) {
    targetImages[serviceId] = addDockerArchiveImage(layout, {
      archive: archives[serviceId].targetArchive,
      scratch: join(imageScratch, `extract-target-${serviceId}`),
      reference: `${imageRepository(serviceId)}:${target.version}`,
      extraPlatforms: components[serviceId]?.platforms?.includes('linux/arm64') ? ['linux/arm64'] : [],
    });
  }

  const sourceInfrastructureLock = JSON.parse(readFileSync(join(repo, 'scripts/docker/infrastructure-images.lock.json'), 'utf8'));
  const infraById = Object.fromEntries(sourceInfrastructureLock.images.map(image => [image.id, image]));

  const requiredForCell = new Set(requiredInfrastructureIds(cell));
  const infrastructureIds = ['mssql', 'nginx', 'postgres'];
  const infrastructureImages = {};
  if (requiredForCell.has('postgres')) {
    run('docker', ['pull', 'postgres:16-alpine'], { cwd: repo, stdio: ['ignore', 'inherit', 'pipe'] });
    const postgresArchive = join(imageScratch, 'postgres.docker.tar');
    run('docker', ['save', 'postgres:16-alpine', '--output', postgresArchive], { cwd: repo });
    infrastructureImages.postgres = addDockerArchiveImage(layout, {
      archive: postgresArchive,
      scratch: join(imageScratch, 'extract-postgres'),
      reference: infraById.postgres.reference,
    });
  } else {
    infrastructureImages.postgres = addTinyImage(layout, {
      id: 'postgres',
      version: target.version,
      platforms: Object.keys(infraById.postgres.platforms),
      reference: infraById.postgres.reference,
      labels: { 'org.printfarmer.fixture-infrastructure': 'postgres' },
    });
  }
  if (requiredForCell.has('mssql')) {
    run('docker', ['pull', infraById.mssql.reference], { cwd: repo, stdio: ['ignore', 'inherit', 'pipe'] });
    const mssqlArchive = join(imageScratch, 'mssql.docker.tar');
    run('docker', ['save', infraById.mssql.reference, '--output', mssqlArchive], { cwd: repo });
    infrastructureImages.mssql = addDockerArchiveImage(layout, {
      archive: mssqlArchive,
      scratch: join(imageScratch, 'extract-mssql'),
      reference: infraById.mssql.reference,
    });
  } else {
    infrastructureImages.mssql = addTinyImage(layout, {
      id: 'mssql',
      version: target.version,
      platforms: Object.keys(infraById.mssql.platforms),
      reference: infraById.mssql.reference,
      labels: { 'org.printfarmer.fixture-infrastructure': 'mssql' },
    });
  }
  if (requiredForCell.has('nginx')) {
    run('docker', ['pull', infraById.nginx.reference], { cwd: repo, stdio: ['ignore', 'inherit', 'pipe'] });
    const nginxArchive = join(imageScratch, 'nginx.docker.tar');
    run('docker', ['save', infraById.nginx.reference, '--output', nginxArchive], { cwd: repo });
    infrastructureImages.nginx = addDockerArchiveImage(layout, {
      archive: nginxArchive,
      scratch: join(imageScratch, 'extract-nginx'),
      reference: infraById.nginx.reference,
    });
  } else {
    infrastructureImages.nginx = addTinyImage(layout, {
      id: 'nginx',
      version: target.version,
      platforms: Object.keys(infraById.nginx.platforms),
      reference: infraById.nginx.reference,
      labels: { 'org.printfarmer.fixture-infrastructure': 'nginx' },
    });
  }
  const infrastructureLock = {
    schema: 1,
    kind: 'printfarmer-infrastructure-images-lock',
    images: infrastructureIds.sort().map((id) => ({
      id,
      reference: infraById[id].reference,
      mediaType: infrastructureImages[id].mediaType,
      digest: infrastructureImages[id].indexDigest,
      platforms: infrastructureImages[id].platformDigests,
    })),
  };
  return { layout, priorImages, targetImages, infrastructureLock };
}

export const buildCellImageLayout = buildC2ImageLayout;

function buildServiceArchives({
  repo,
  runRoot,
  imageScratch,
  run,
  buildEnvironment,
  sourceCommit,
  serviceId,
  prior,
  target,
}) {
  const runTag = basename(runRoot).replace(/[^A-Za-z0-9_.-]/g, '-').toLowerCase();
  const priorTag = `printfarmer-${runTag}-${serviceId}-prior:${prior.version}`;
  const targetTag = `printfarmer-${runTag}-${serviceId}-target:${target.version}`;
  run('docker', [
    'build',
    repo,
    '--file', join(repo, 'scripts/docker/dockerfiles/Dockerfile.multistage'),
    '--target', components[serviceId].target,
    '--tag', priorTag,
    '--build-arg', `GIT_SHA=${sourceCommit}`,
    '--build-arg', `VITE_GIT_SHA=${sourceCommit}`,
    '--build-arg', `BUILD_VERSION=${prior.version}`,
    '--build-arg', `VCS_REF=${sourceCommit}`,
  ], { cwd: repo, stdio: ['ignore', 'inherit', 'pipe'], env: buildEnvironment });

  const deriveDockerfile = join(runRoot, `Dockerfile.${serviceId}-target`);
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

  const priorArchive = join(imageScratch, `${serviceId}-prior.docker.tar`);
  const targetArchive = join(imageScratch, `${serviceId}-target.docker.tar`);
  run('docker', ['save', priorTag, '--output', priorArchive], { cwd: repo });
  run('docker', ['save', targetTag, '--output', targetArchive], { cwd: repo });
  return { priorArchive, targetArchive };
}
