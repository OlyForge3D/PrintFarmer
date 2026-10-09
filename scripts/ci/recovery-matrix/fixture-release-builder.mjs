import { createHash, randomBytes, randomUUID } from 'node:crypto';
import {
  copyFileSync,
  mkdirSync,
  readFileSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join, resolve } from 'node:path';

import { assembleOfflineBundle } from '../offline-update-bundle.mjs';
import {
  hostUpdateCliArchiveName,
  hostUpdateCliRuntimes,
  hostUpdateCliSbomName,
  hostUpdateCliSumsBundleName,
  hostUpdateCliSumsName,
  packageHostUpdateCli,
  validateHostUpdateCliSbom,
} from '../host-update-cli-package.mjs';
import { buildManifest, deriveSequence } from '../release-manifest.mjs';
import { components, requireThat } from '../release-policy.mjs';
import {
  infrastructureImagesDocument,
  infrastructureImagesName,
  infrastructureImagesSignatureName,
  infrastructureLockPath,
  validateInfrastructureLock,
} from '../offline-bundle-images.mjs';
import {
  deploymentSetDocument,
  deploymentSetName,
  deploymentSetSignatureName,
  offlineToolsLockPath,
  readDeploymentTemplates,
  validateOfflineToolsLock,
} from '../offline-deployment-set.mjs';
import {
  recoveryInstructionsDocument,
  recoveryInstructionsName,
  recoveryInstructionsSignatureName,
} from '../offline-recovery-instructions.mjs';
import { fixtureReleaseIdentity } from './fixture-sigstore.mjs';

const sha256Pattern = /^[a-f0-9]{64}$/;
const versionPattern = /^\d+\.\d+\.\d+(?:-insider\.[1-9][0-9]*)?$/;
const sourceCommitPattern = /^[a-f0-9]{40}$/;
const channelBranches = Object.freeze({ stable: 'main', insider: 'development' });

export function fixtureRelease({ version, channel = 'insider', sourceCommit, buildId }) {
  requireThat(versionPattern.test(version ?? ''), 'Fixture release version is invalid');
  requireThat(Object.hasOwn(channelBranches, channel), 'Fixture release channel must be stable or insider');
  requireThat(sourceCommitPattern.test(sourceCommit ?? ''), 'Fixture release sourceCommit must be a lowercase full SHA');
  const release = {
    version,
    tag: `v${version}`,
    channel,
    sourceBranch: channelBranches[channel],
    sourceCommit,
    buildId: String(buildId ?? deriveSequence(version)),
    sequence: deriveSequence(version),
  };
  return release;
}

export function releaseEvidenceIdentity(release) {
  return {
    tag: release.tag,
    version: release.version,
    channel: release.channel,
    sourceCommit: release.sourceCommit,
    buildId: String(release.buildId),
    sequence: release.sequence,
  };
}

export function protectedBackupReference(priorRelease, { id = `fixture-${priorRelease.version}`, sha256 } = {}) {
  const digest = sha256 ?? createHash('sha256')
    .update(`printfarmer fixture protected backup ${priorRelease.version}\n`)
    .digest('hex');
  requireThat(sha256Pattern.test(digest), 'Protected backup checksum must be a lowercase SHA-256');
  return {
    id,
    locationClass: 'host-local',
    releaseVersion: priorRelease.version,
    sha256: digest,
  };
}

export function writeTrustedRootApproval(path, trustedRootBytes, {
  approvedAt = new Date().toISOString(),
  approvedBy = 'recovery-matrix',
} = {}) {
  const trustedRootSha256 = createHash('sha256').update(trustedRootBytes).digest('hex');
  writeJson(path, {
    schema: 1,
    kind: 'printfarmer-trusted-root-approval',
    trustedRootSha256,
    approvedAt,
    approvedBy,
  });
  return { trustedRootSha256, approvedAt, approvedBy };
}

export function writeJson(path, value) {
  mkdirSync(dirname(resolve(path)), { recursive: true });
  writeFileSync(path, `${JSON.stringify(value, undefined, 2)}\n`);
}

export function signFixtureAsset({ root, assetPath, bundlePath, channel }) {
  const identity = fixtureReleaseIdentity(channel);
  const bundle = root.signBlob(readFileSync(assetPath), { identity });
  writeJson(bundlePath, bundle);
  return bundle;
}

export function signFixtureReleaseAssets({ root, assets, release }) {
  const signatures = [
    ['update-manifest.json', 'update-manifest.sigstore.json'],
    [hostUpdateCliSumsName(release.version), hostUpdateCliSumsBundleName(release.version)],
    [infrastructureImagesName, infrastructureImagesSignatureName],
    [recoveryInstructionsName, recoveryInstructionsSignatureName],
    [deploymentSetName, deploymentSetSignatureName],
  ];
  for (const [asset, bundle] of signatures) {
    signFixtureAsset({
      root,
      assetPath: join(assets, asset),
      bundlePath: join(assets, bundle),
      channel: release.channel,
    });
  }
}

export function dummySpdxDocument({ name, version, rid }) {
  return {
    spdxVersion: 'SPDX-2.3',
    dataLicense: 'CC0-1.0',
    SPDXID: 'SPDXRef-DOCUMENT',
    name: `${name}-${rid}`,
    documentNamespace: `https://printfarmer.invalid/recovery-matrix/${name}/${version}/${rid}/${randomUUID()}`,
    creationInfo: {
      created: '2026-09-26T00:00:00Z',
      creators: ['Tool: printfarmer-recovery-matrix-fixture'],
    },
    packages: [{
      name,
      SPDXID: 'SPDXRef-Package-host-update-cli',
      downloadLocation: 'NOASSERTION',
      filesAnalyzed: false,
      versionInfo: version,
    }],
  };
}

function sha256File(path) {
  return createHash('sha256').update(readFileSync(path)).digest('hex');
}

function checksumLine(name, sha256) {
  requireThat(/^[A-Za-z0-9._+-]+$/.test(name), `Invalid checksum entry name: ${name}`);
  requireThat(sha256Pattern.test(sha256), `Invalid checksum for ${name}`);
  return `${sha256}  ${name}\n`;
}

export function completeHostUpdateCliSums({ assets, version }) {
  const entries = [];
  for (const rid of hostUpdateCliRuntimes) {
    for (const name of [hostUpdateCliArchiveName(version, rid), hostUpdateCliSbomName(version, rid)]) {
      const path = join(assets, name);
      const sha256 = statSync(path, { throwIfNoEntry: false })?.isFile()
        ? sha256File(path)
        : createHash('sha256').update(`fixture placeholder ${name}\n`).digest('hex');
      entries.push({ name, sha256 });
    }
  }
  entries.sort((a, b) => a.name.localeCompare(b.name));
  writeFileSync(join(assets, hostUpdateCliSumsName(version)), entries.map(({ name, sha256 }) => checksumLine(name, sha256)).join(''));
  return entries;
}

export function hostUpdateCliToolIdentity({ assets, version, runtime = 'linux-x64' }) {
  const name = hostUpdateCliArchiveName(version, runtime);
  const path = join(assets, name);
  const sha256 = sha256File(path);
  return `${version}/${runtime} sha256:${sha256}`;
}

export function packageFixtureHostUpdateCli({ source, assets, release, run, scratch, runtimes = ['linux-x64'] }) {
  packageHostUpdateCli(release, source, assets, {
    run,
    runtimes,
    scratch,
    sbom: ({ sbomPath, rid }) => {
      const document = dummySpdxDocument({ name: 'printfarmer-host-update-cli', version: release.version, rid });
      writeJson(sbomPath, document);
      validateHostUpdateCliSbom(readFileSync(sbomPath, 'utf8'), sbomPath);
    },
  });
  return completeHostUpdateCliSums({ assets, version: release.version });
}

// Extra import-cell releases reuse an already packaged CLI archive under the new version's name
// instead of publishing the CLI again; only the signed checksum list binds the archive bytes.
export function reuseFixtureHostUpdateCli({ from, assets, release, runtimes = ['linux-x64'] }) {
  for (const rid of runtimes) {
    copyFileSync(join(from.assets, hostUpdateCliArchiveName(from.version, rid)),
      join(assets, hostUpdateCliArchiveName(release.version, rid)));
    const sbomPath = join(assets, hostUpdateCliSbomName(release.version, rid));
    writeJson(sbomPath, dummySpdxDocument({ name: 'printfarmer-host-update-cli', version: release.version, rid }));
    validateHostUpdateCliSbom(readFileSync(sbomPath, 'utf8'), sbomPath);
  }
  return completeHostUpdateCliSums({ assets, version: release.version });
}

export function buildFixtureRelease({
  source,
  output,
  release,
  imageDetails,
  infrastructureLock,
  sigstoreRoot,
  run,
  scratch,
  runtimes = ['linux-x64'],
  cliFrom,
}) {
  requireThat(typeof source === 'string' && source.length > 0, 'source is required');
  requireThat(typeof output === 'string' && output.length > 0, 'output is required');
  requireThat(typeof run === 'function', 'run is required');
  requireThat(sigstoreRoot && typeof sigstoreRoot.signBlob === 'function', 'sigstoreRoot is required');
  requireCompleteImageDetails(imageDetails);
  const assets = resolve(output);
  mkdirSync(assets, { recursive: true });
  if (cliFrom) {
    reuseFixtureHostUpdateCli({ from: cliFrom, assets, release, runtimes });
  } else {
    packageFixtureHostUpdateCli({ source, assets, release, run, scratch, runtimes });
  }

  const manifest = buildManifest(release, imageDetails);
  writeFileSync(join(assets, 'update-manifest.json'), manifest);

  const selectedInfrastructureLock = infrastructureLock ??
    validateInfrastructureLock(readFileSync(join(source, ...infrastructureLockPath.split('/'))));
  validateInfrastructureLock(Buffer.from(JSON.stringify(selectedInfrastructureLock)));
  writeFileSync(join(assets, infrastructureImagesName), infrastructureImagesDocument(release, selectedInfrastructureLock));

  writeFileSync(join(assets, recoveryInstructionsName), recoveryInstructionsDocument(release));

  writeFileSync(join(assets, deploymentSetName), deploymentSetDocument(release, {
    templates: readDeploymentTemplates(source),
    lock: validateOfflineToolsLock(readFileSync(join(source, ...offlineToolsLockPath.split('/')))),
  }));

  signFixtureReleaseAssets({ root: sigstoreRoot, assets, release });
  return { assets, release, manifestDigest: createHash('sha256').update(manifest).digest('hex') };
}

export function assembleFixtureBundle({
  releaseAssets,
  channel,
  output,
  run,
  trustedRoot,
  cosign,
  imageLayout,
  tools,
  priorReleaseAssets,
  priorImages,
  protectedBackup,
  runtimes = ['linux-x64'],
}) {
  return assembleOfflineBundle({
    releaseAssets,
    channel,
    output,
    run,
    trustedRoot,
    cosign,
    images: imageLayout,
    tools,
    priorReleaseAssets,
    priorImages,
    protectedBackup,
    runtimes,
  });
}

export function fixtureSourceCommit(seed) {
  return createHash('sha1').update(String(seed)).digest('hex');
}

export function randomHex(bytes = 32) {
  return randomBytes(bytes).toString('hex');
}

export function mutateMonolithDigest(imageDetails, replacement) {
  requireCompleteImageDetails(imageDetails);
  requireThat(replacement?.indexDigest && replacement?.platformDigests, 'replacement image details are required');
  return {
    ...imageDetails,
    monolith: {
      ...imageDetails.monolith,
      indexDigest: replacement.indexDigest,
      platforms: [...replacement.platforms],
      platformDigests: { ...replacement.platformDigests },
    },
  };
}

export function atomicWriteJson(path, value) {
  mkdirSync(dirname(resolve(path)), { recursive: true });
  const partial = `${path}.${process.pid}.${Date.now()}.partial`;
  writeJson(partial, value);
  renameSync(partial, path);
}

export function cleanDirectory(path) {
  rmSync(path, { recursive: true, force: true });
  mkdirSync(path, { recursive: true });
}

function requireCompleteImageDetails(imageDetails) {
  requireThat(imageDetails && typeof imageDetails === 'object', 'imageDetails is required');
  const expected = Object.keys(components).sort();
  requireThat(Object.keys(imageDetails).sort().join() === expected.join(), 'imageDetails must name every release component');
  for (const [id, policy] of Object.entries(components)) {
    const detail = imageDetails[id];
    requireThat(detail && /^sha256:[a-f0-9]{64}$/.test(detail.indexDigest ?? ''), `imageDetails.${id}.indexDigest is invalid`);
    requireThat(
      Array.isArray(detail.platforms) &&
        detail.platforms.slice().sort().join() === policy.platforms.slice().sort().join(),
      `imageDetails.${id}.platforms must match release policy`,
    );
    for (const platform of policy.platforms) {
      requireThat(/^sha256:[a-f0-9]{64}$/.test(detail.platformDigests?.[platform] ?? ''), `imageDetails.${id}.${platform} digest is invalid`);
    }
  }
}
