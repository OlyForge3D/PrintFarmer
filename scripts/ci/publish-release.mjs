import {
  appendFileSync,
  copyFileSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { components, repository, requireThat, validateVersion, compareVersions, parseTag, workflow } from './release-policy.mjs';
import { githubClient, verifyOwnerDispatch } from './release-dispatch.mjs';
import { emitBuildMetadata } from './release-metadata.mjs';
import { command, imageRepository, rejectExistingImages, verifyImages, publishImageTags } from './release-set.mjs';
import { releaseNotes } from './release-notes.mjs';
import { buildManifest, deriveSequence, validateManifest } from './release-manifest.mjs';

export async function rejectExistingVersion(api, tag) {
  requireThat(!await api(`git/ref/tags/${tag}`, { allowMissing: true }), `Tag ${tag} already exists; choose a new version`);
  requireThat(!await api(`releases/tags/${tag}`, { allowMissing: true }), `Release ${tag} already exists; choose a new version`);
}

export async function selectRelease(env, api) {
  const channel = env.RELEASE_CHANNEL;
  requireThat(['stable', 'insider'].includes(channel), 'Invalid release channel');
  const sourceBranch = channel === 'stable' ? 'main' : 'development';
  const branch = await api(`git/ref/heads/${sourceBranch}`);
  const sourceCommit = env.RELEASE_SELECTED_SOURCE || env.RELEASE_SOURCE_SHA || branch.object?.sha;
  requireThat(/^[a-f0-9]{40}$/.test(sourceCommit ?? ''), 'Source must be a full lowercase commit SHA');
  requireThat(!env.RELEASE_SOURCE_SHA || env.RELEASE_SOURCE_SHA === sourceCommit,
    'Pinned source differs from the requested source');
  if (sourceCommit !== branch.object?.sha) {
    const comparison = await api(`compare/${sourceCommit}...${branch.object.sha}`);
    requireThat(['ahead', 'identical'].includes(comparison.status),
      `Source is not an ancestor of ${sourceBranch}`);
  }
  const file = await api(`contents/VERSION?ref=${sourceCommit}`);
  requireThat(file.encoding === 'base64' && typeof file.content === 'string', 'Selected-source VERSION is unavailable');
  validateVersion(env.RELEASE_VERSION, channel, Buffer.from(file.content, 'base64').toString('utf8'));
  const release = { version: env.RELEASE_VERSION, tag: `v${env.RELEASE_VERSION}`,
    channel, sourceBranch, sourceCommit, buildId: env.GITHUB_RUN_ID };
  await rejectExistingVersion(api, release.tag);
  return release;
}

export function buildImages(release, source, assets, run = command, rejectImages = rejectExistingImages) {
  requireThat(run('git', ['rev-parse', 'HEAD'], { cwd: source }).trim() === release.sourceCommit,
    'Build checkout does not match selected source');
  validateVersion(release.version, release.channel, readFileSync(join(source, 'VERSION'), 'utf8'));
  rejectImages(release.version);
  mkdirSync(assets, { recursive: true });
  const execute = (name, args) => run(name, args, { cwd: source, stdio: ['ignore', 'inherit', 'pipe'] });
  run('dotnet', ['restore', 'farm-web.sln'], { cwd: join(source, 'src'), stdio: ['ignore', 'inherit', 'pipe'] });
  execute('node', ['scripts/compliance/validate-compliance.mjs']);
  const sourceBundleAssets = mkdtempSync(join(source, '.release-assets-'));
  try {
    execute('node', ['scripts/compliance/create-source-bundle.mjs',
      '--revision', release.sourceCommit, '--version', release.tag, '--output', sourceBundleAssets]);
    for (const file of sourceBundleFiles(release)) {
      copyFileSync(join(sourceBundleAssets, file), join(assets, file));
    }
  } finally {
    rmSync(sourceBundleAssets, { force: true, recursive: true });
  }
  execute('node', ['scripts/compliance/create-license-inventory.mjs', '--version', release.tag,
    '--revision', release.sourceCommit, '--output', join(assets, 'license-inventory.json')]);
  emitBuildMetadata(release, source);
  const digests = {};
  const baseUrl = `https://github.com/${repository}/releases/download/${release.tag}`;
  for (const [name, { target, platforms }] of Object.entries(components)) {
    console.log(`Building ${name} (${platforms.join(', ')})`);
    const metadataPath = join(assets, `${name}-build.json`);
    const sbom = join(assets, `printfarmer-${name}-${release.tag}.spdx.json`);
    execute('docker', ['buildx', 'build', '.', '--file', 'scripts/docker/dockerfiles/Dockerfile.multistage',
      '--target', target, '--platform', platforms.join(','),
      '--label', `org.opencontainers.image.source=https://github.com/${repository}`,
      '--label', 'org.opencontainers.image.licenses=AGPL-3.0-only',
      '--label', `org.opencontainers.image.revision=${release.sourceCommit}`,
      '--label', `org.opencontainers.image.version=${release.version}`,
      '--label', `org.printfarmer.release-channel=${release.channel}`,
      '--build-arg', `GIT_SHA=${release.sourceCommit}`, '--build-arg', `VITE_GIT_SHA=${release.sourceCommit}`,
      '--build-arg', `BUILD_VERSION=${release.version}`, '--build-arg', `VCS_REF=${release.sourceCommit}`,
      '--build-arg', `SOURCE_REPOSITORY=https://github.com/${repository}`,
      '--build-arg', `SOURCE_ARCHIVE_URL=${baseUrl}/PrintFarmer-${release.tag}-source.tar.gz`,
      '--build-arg', `SBOM_URL=${baseUrl}/printfarmer-${name}-${release.tag}.spdx.json`,
      '--output', `type=image,name=${imageRepository(name)},push-by-digest=true,name-canonical=true,push=true`,
      '--provenance=mode=max', '--sbom=true', '--metadata-file', metadataPath]);
    const digest = JSON.parse(readFileSync(metadataPath, 'utf8'))['containerimage.digest'];
    requireThat(/^sha256:[a-f0-9]{64}$/.test(digest ?? ''), `Build returned no digest: ${name}`);
    digests[name] = digest;
    execute('syft', [`registry:${imageRepository(name)}@${digest}`, '-o', `spdx-json=${sbom}`]);
    execute('node', ['scripts/compliance/enrich-sbom.mjs', '--sbom', sbom,
      '--inventory', join(assets, 'license-inventory.json'), '--version', release.tag,
      '--revision', release.sourceCommit, ...(['frontend', 'monolith'].includes(name) ? ['--include-npm'] : [])]);
  }
  // The source-bundle contract links this name as its aggregate application SBOM.
  copyFileSync(join(assets, `printfarmer-monolith-${release.tag}.spdx.json`),
    join(assets, `printfarmer-${release.tag}.spdx.json`));
  for (const file of ['LICENSE', 'THIRD-PARTY-NOTICES.md']) copyFileSync(join(source, file), join(assets, file));
  const imageDetails = verifyImages(release.version, release.sourceCommit, digests, run);
  const smokeCommands = {
    api: 'test -f /app/Farm.Web.Api.dll && dotnet --info >/dev/null',
    'slicer-host': 'test -f /app/Farm.Slicer.Host.dll && test -d /app/plugins/slicer && dotnet --info >/dev/null',
    frontend: 'nginx -t',
    'printer-discovery': 'test -f /app/PrinterDiscoveryService.dll && dotnet --info >/dev/null',
    monolith: 'test -f /app/Farm.Web.Api.dll && test -f /app/wwwroot/index.html && dotnet --info >/dev/null',
  };
  for (const [name, smoke] of Object.entries(smokeCommands)) {
    execute('docker', ['run', '--rm', '--platform', 'linux/arm64', '--entrypoint', '/bin/sh',
      `${imageRepository(name)}@${digests[name]}`, '-c', smoke]);
  }
  writeFileSync(join(assets, 'container-images.json'), `${JSON.stringify({
    schema: 1, ...release, managedUpdateEligible: false, images: Object.fromEntries(
      Object.keys(components).map(name => [name, { reference: `${imageRepository(name)}@${digests[name]}`,
        platforms: components[name].platforms }])),
  }, undefined, 2)}\n`);
  writeFileSync(join(assets, 'update-manifest.json'), buildManifest(
    { ...release, sequence: deriveSequence(release.version) }, imageDetails));
  writeFileSync(join(assets, 'digests.json'), JSON.stringify(digests));
  return digests;
}

export function sourceBundleFiles(release) {
  return [
    `PrintFarmer-${release.tag}-source.tar.gz`,
    `PrintFarmer-${release.tag}-source.json`,
  ];
}

export function releaseAssets(release) {
  return [
    ...sourceBundleFiles(release),
    'LICENSE', 'THIRD-PARTY-NOTICES.md', 'license-inventory.json', 'container-images.json', 'release-notes.md',
    'update-manifest.json', 'update-manifest.sigstore.json',
    `printfarmer-${release.tag}.spdx.json`,
    ...Object.keys(components).map(name => `printfarmer-${name}-${release.tag}.spdx.json`),
  ];
}

// The workflow signs and verifies update-manifest.json in two prior steps
// (sign, then re-verify) before this script even starts. Re-verify the exact
// bytes about to be uploaded here too, immediately before the upload call,
// so nothing between those earlier steps and the actual upload (a rebuilt
// asset, a manual edit, disk corruption) can present an unsigned or
// mismatched manifest as this release's signed managed-update contract.
function manifestSignatureIdentity() {
  return `https://github.com/${repository}/${workflow}@refs/heads/development`;
}

function verifyManifestSignatureBeforeUpload(assets, run) {
  const manifestPath = join(assets, 'update-manifest.json');
  const bundlePath = join(assets, 'update-manifest.sigstore.json');
  requireThat(readFileSync(manifestPath).length > 0, 'Missing update manifest immediately before upload');
  requireThat(readFileSync(bundlePath).length > 0,
    'Missing update manifest signature bundle immediately before upload');
  run('cosign', ['verify-blob', '--bundle', bundlePath,
    '--certificate-oidc-issuer', 'https://token.actions.githubusercontent.com',
    '--certificate-identity', manifestSignatureIdentity(), manifestPath]);
}

export async function publishRelease(release, assets, api, {
  run = command, verify = verifyImages, rejectImages = rejectExistingImages, tagImages = publishImageTags,
} = {}) {
  const digests = JSON.parse(readFileSync(join(assets, 'digests.json'), 'utf8'));
  const imageDetails = verify(release.version, release.sourceCommit, digests);
  requireThat(imageDetails && typeof imageDetails === 'object',
    'Image verification must return OCI index and child platform digests');
  const files = releaseAssets(release);
  for (const name of files.filter(name => name !== 'release-notes.md')) {
    requireThat(readFileSync(join(assets, name)).length > 0, `Missing release asset: ${name}`);
  }
  validateManifest(readFileSync(join(assets, 'update-manifest.json'), 'utf8'), release, digests, imageDetails);
  const notes = await releaseNotes(api, release, digests);
  writeFileSync(join(assets, 'release-notes.md'), notes);
  await rejectExistingVersion(api, release.tag);
  rejectImages(release.version);
  const latest = release.channel === 'stable' ? await api('releases/latest', { allowMissing: true }) : undefined;
  const makeLatest = release.channel === 'stable' &&
    (!latest || compareVersions(release.version, parseTag(latest.tag_name).canonicalVersion) > 0);
  // GitHub creates the ref atomically and rejects an existing tag; it is never patched or deleted.
  await api('git/refs', { method: 'POST', body: { ref: `refs/tags/${release.tag}`, sha: release.sourceCommit } });
  const draft = await api('releases', { method: 'POST', body: {
    tag_name: release.tag, target_commitish: release.sourceCommit, name: `PrintFarmer ${release.version}`,
    body: notes, draft: true, prerelease: release.channel === 'insider', make_latest: 'false',
  } });
  requireThat(Number.isSafeInteger(draft?.id), 'GitHub did not return a draft release ID');
  verifyManifestSignatureBeforeUpload(assets, run);
  run('gh', ['release', 'upload', release.tag, ...files.map(name => join(assets, name)), '--repo', repository]);
  const uploaded = await api(`releases/${draft.id}/assets?per_page=100`);
  requireThat(Array.isArray(uploaded) && uploaded.length === files.length &&
    uploaded.map(file => file.name).sort().join() === [...files].sort().join() &&
    uploaded.every(file => {
      const bytes = readFileSync(join(assets, file.name));
      return file.state === 'uploaded' && file.size === bytes.length &&
        file.digest === `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
    }),
  'Draft release asset inventory is incomplete');
  tagImages(release.version, digests);
  const published = await api(`releases/${draft.id}`, { method: 'PATCH', body: {
    draft: false, make_latest: String(makeLatest),
  } });
  requireThat(published?.draft === false && published.tag_name === release.tag &&
    published.prerelease === (release.channel === 'insider'), 'GitHub did not confirm publication');
  return published.html_url;
}

async function main() {
  const env = process.env;
  const api = githubClient(env.GH_TOKEN);
  await verifyOwnerDispatch(env, api);
  const release = await selectRelease(env, api);
  const operation = process.argv[2];
  requireThat(['select', 'build', 'publish'].includes(operation), 'Unknown release command');
  if (operation === 'select') {
    for (const [key, value] of Object.entries({ source_sha: release.sourceCommit,
      version: release.version, environment: `release-${release.channel}` })) {
      appendFileSync(env.GITHUB_OUTPUT, `${key}=${value}\n`);
    }
    return;
  }
  requireThat(env.RELEASE_SELECTED_SOURCE === release.sourceCommit, 'Pinned source output is required');
  requireThat(env.RELEASE_PUBLICATION_ENVIRONMENT === `release-${release.channel}`, 'Protected environment is required');
  const assets = resolve('release-assets');
  if (operation === 'build') buildImages(release, resolve('source'), assets);
  else {
    const url = await publishRelease(release, assets, api);
    appendFileSync(env.GITHUB_OUTPUT, `release_url=${url}\n`);
    appendFileSync(env.GITHUB_STEP_SUMMARY, `Published [${release.tag}](${url}) from \`${release.sourceCommit}\`.\n`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main().catch(error => {
    console.error(`Release failed: ${error.message}. No automatic recovery or overwrite. Inspect this run; use a new version if its tag exists.`);
    process.exitCode = 1;
  });
}
