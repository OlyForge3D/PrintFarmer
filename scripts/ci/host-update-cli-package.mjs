// Host-update recovery CLI package (issue #3041).
//
// Publishes src/tools/Farm.HostUpdate.Cli as a self-contained, per-RID archive that carries the
// fixed-operation wrappers beside the CLI, so an operator can run status/recovery on a supported
// host without a source checkout, the API or a .NET runtime. The release signs one checksum list
// (keyless Cosign, consolidated-release.yml) that binds every archive; operators and offline
// bundles (#2981) verify that list and then the archive hash. This never enables rollout.
import { createHash } from 'node:crypto';
import { chmodSync, copyFileSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { requireThat } from './release-policy.mjs';

// The supported host matrix (docs/HOST_UPDATE_RUNBOOK.md). macOS is deliberately absent: an
// apphost cross-published from Linux is not code-signed, so Apple silicon refuses to run it.
export const hostUpdateCliRuntimes = Object.freeze(['linux-x64', 'linux-arm64', 'win-x64']);

export const hostUpdateCliProject = 'src/tools/Farm.HostUpdate.Cli/Farm.HostUpdate.Cli.csproj';
export const hostUpdateCliPackageManifest = 'host-update-cli-package.json';
const wrapperFiles = Object.freeze([
  ['scripts/printfarmer-host-update.sh', 'printfarmer-host-update.sh', 0o755],
  ['scripts/common-utils.sh', 'common-utils.sh', 0o644],
  ['scripts/printfarmer-host-update.ps1', 'printfarmer-host-update.ps1', 0o644],
  ['LICENSE', 'LICENSE', 0o644],
  ['THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.md', 0o644],
]);

export function hostUpdateCliArchiveName(version, rid) {
  requireThat(hostUpdateCliRuntimes.includes(rid), `Unsupported host-update CLI runtime: ${rid}`);
  return `printfarmer-host-update-cli-v${version}-${rid}.tar.gz`;
}

// Issue #3045: one SPDX SBOM per archive, bound by the same signed checksum list.
export function hostUpdateCliSbomName(version, rid) {
  requireThat(hostUpdateCliRuntimes.includes(rid), `Unsupported host-update CLI runtime: ${rid}`);
  return `printfarmer-host-update-cli-v${version}-${rid}.spdx.json`;
}

export function hostUpdateCliSumsName(version) {
  return `printfarmer-host-update-cli-v${version}-SHA256SUMS`;
}

export function hostUpdateCliSumsBundleName(version) {
  return `${hostUpdateCliSumsName(version)}.sigstore.json`;
}

export function hostUpdateCliAssets(version) {
  return [
    ...hostUpdateCliRuntimes.map(rid => hostUpdateCliArchiveName(version, rid)),
    ...hostUpdateCliRuntimes.map(rid => hostUpdateCliSbomName(version, rid)),
    hostUpdateCliSumsName(version),
    hostUpdateCliSumsBundleName(version),
  ];
}

function sha256File(path) {
  return createHash('sha256').update(readFileSync(path)).digest('hex');
}

// `sha256sum --check` format: "<hex>  <name>\n", sorted by name so the signed bytes are stable.
export function formatSums(entries) {
  return [...entries].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))
    .map(({ name, sha256 }) => {
      requireThat(/^[a-f0-9]{64}$/.test(sha256), `Invalid SHA-256 for ${name}`);
      requireThat(/^[A-Za-z0-9._+-]+$/.test(name), `Invalid checksum entry name: ${name}`);
      return `${sha256}  ${name}\n`;
    }).join('');
}

export function parseSums(text) {
  const entries = new Map();
  requireThat(typeof text === 'string' && text.endsWith('\n'), 'Host-update CLI checksum list is malformed');
  for (const line of text.slice(0, -1).split('\n')) {
    const match = line.match(/^([a-f0-9]{64}) {2}([A-Za-z0-9._+-]+)$/);
    requireThat(match, 'Host-update CLI checksum list is malformed');
    requireThat(!entries.has(match[2]), `Duplicate host-update CLI checksum entry: ${match[2]}`);
    entries.set(match[2], match[1]);
  }
  return entries;
}

// Minimal structural proof that an SBOM is an SPDX 2.x document with at least one package, so
// an empty or truncated scanner output can never be signed as this archive's inventory.
export function validateHostUpdateCliSbom(text, name) {
  let document;
  try {
    document = JSON.parse(text);
  } catch {
    throw new Error(`Host-update CLI SBOM is not JSON: ${name}`);
  }
  requireThat(document && typeof document === 'object' && /^SPDX-2\./.test(document.spdxVersion ?? '') &&
    document.SPDXID === 'SPDXRef-DOCUMENT' && Array.isArray(document.packages) && document.packages.length > 0,
  `Host-update CLI SBOM is not an SPDX 2.x document with packages: ${name}`);
  return document;
}

// Proves the checksum list names exactly the supported archives and their SBOMs, and matches their bytes.
export function verifyHostUpdateCliSums(assets, version) {
  const entries = parseSums(readFileSync(join(assets, hostUpdateCliSumsName(version)), 'utf8'));
  const sboms = hostUpdateCliRuntimes.map(rid => hostUpdateCliSbomName(version, rid));
  const expected = [...hostUpdateCliRuntimes.map(rid => hostUpdateCliArchiveName(version, rid)), ...sboms];
  requireThat(entries.size === expected.length && expected.every(name => entries.has(name)),
    'Host-update CLI checksum list does not name exactly the supported archives and SBOMs');
  for (const name of expected) {
    requireThat(sha256File(join(assets, name)) === entries.get(name), `Host-update CLI asset hash mismatch: ${name}`);
  }
  for (const name of sboms) validateHostUpdateCliSbom(readFileSync(join(assets, name), 'utf8'), name);
  return entries;
}

function normalizeModes(directory, executables) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      chmodSync(path, 0o755);
      normalizeModes(path, executables);
    } else {
      chmodSync(path, executables.has(path) ? 0o755 : 0o644);
    }
  }
}

function isGnuTar(run) {
  try {
    return /GNU tar/.test(run('tar', ['--version']) ?? '');
  } catch {
    return false;
  }
}

// Archive members are owned by root/0 and carry normalized modes, so an administrator's
// extraction never inherits the build runner's uid.
function tarOwnership(run) {
  return isGnuTar(run)
    ? ['--force-local', '--owner=0', '--group=0', '--numeric-owner']
    : ['--uid', '0', '--gid', '0', '--uname', 'root', '--gname', 'root'];
}

export function packageHostUpdateCli(release, source, output, {
  run, runtimes = hostUpdateCliRuntimes, scratch = tmpdir(), sbom,
} = {}) {
  requireThat(typeof run === 'function', 'A command runner is required');
  requireThat(sbom === undefined || typeof sbom === 'function', 'The SBOM generator must be a function');
  requireThat(/^[a-f0-9]{40}$/.test(release.sourceCommit ?? ''), 'Host-update CLI package needs the source commit');
  mkdirSync(output, { recursive: true });
  const entries = [];
  const ownership = tarOwnership(run);
  for (const rid of runtimes) {
    const archive = hostUpdateCliArchiveName(release.version, rid);
    const stage = mkdtempSync(join(scratch, 'host-update-cli-'));
    try {
      const cliDirectory = join(stage, 'cli');
      run('dotnet', ['publish', hostUpdateCliProject, '--configuration', 'Release', '--runtime', rid,
        '--self-contained', 'true', '--output', cliDirectory, '-p:UseAppHost=true',
        `-p:Version=${release.version}`, `-p:SourceRevisionId=${release.sourceCommit}`,
        '-p:ContinuousIntegrationBuild=true', '-p:Deterministic=true', '-nologo'],
      { cwd: source, stdio: ['ignore', 'inherit', 'pipe'] });
      const apphost = join(cliDirectory, rid.startsWith('win-') ? 'Farm.HostUpdate.Cli.exe' : 'Farm.HostUpdate.Cli');
      requireThat(statSync(apphost, { throwIfNoEntry: false })?.isFile(), `Self-contained CLI launcher missing for ${rid}`);
      const executables = new Set([apphost]);
      for (const [from, to, mode] of wrapperFiles) {
        copyFileSync(join(source, from), join(stage, to));
        if (mode === 0o755) executables.add(join(stage, to));
      }
      writeFileSync(join(stage, hostUpdateCliPackageManifest), `${JSON.stringify({
        schema: 1, package: 'printfarmer-host-update-cli', version: release.version, tag: release.tag,
        channel: release.channel, sourceCommit: release.sourceCommit, runtime: rid,
        selfContained: true, entryPoint: rid.startsWith('win-') ? 'printfarmer-host-update.ps1' : 'printfarmer-host-update.sh',
        rolloutAuthorization: false,
      }, undefined, 2)}\n`);
      normalizeModes(stage, executables);
      if (sbom) {
        // Scanned from the exact staged tree that is archived next, so the SBOM describes these bytes.
        const sbomName = hostUpdateCliSbomName(release.version, rid);
        const sbomPath = join(resolve(output), sbomName);
        sbom({ stage, sbomPath, rid });
        validateHostUpdateCliSbom(readFileSync(sbomPath, 'utf8'), sbomName);
        entries.push({ name: sbomName, sha256: sha256File(sbomPath) });
      }
      // Members sit at the archive root; operators extract into the versioned placement directory.
      run('tar', ['-czf', join(resolve(output), archive), ...ownership, '-C', stage, '.'],
        { stdio: ['ignore', 'inherit', 'pipe'] });
      entries.push({ name: archive, sha256: sha256File(join(output, archive)) });
    } finally {
      rmSync(stage, { force: true, recursive: true });
    }
  }
  writeFileSync(join(output, hostUpdateCliSumsName(release.version)), formatSums(entries));
  return entries;
}

// Local/CI entry point: builds the archives for one or more runtimes from a checkout. This
// package is unsigned and carries no SBOM; only the release workflow's signed checksum list,
// which also binds each archive's SBOM, is installable evidence.
async function main(argv) {
  const { execFileSync } = await import('node:child_process');
  const options = {};
  for (let index = 0; index < argv.length; index += 2) {
    requireThat(['--version', '--runtime', '--output', '--source'].includes(argv[index]) && argv[index + 1],
      'usage: host-update-cli-package.mjs --version <v> --output <dir> [--runtime <rid>]... [--source <checkout>]');
    const key = argv[index].slice(2);
    options[key] = key === 'runtime' ? [...(options.runtime ?? []), argv[index + 1]] : argv[index + 1];
  }
  requireThat(options.version && options.output, 'usage: --version and --output are required');
  const source = resolve(options.source ?? '.');
  const run = (name, args, runOptions = {}) => execFileSync(name, args, { encoding: 'utf8', ...runOptions });
  const sourceCommit = run('git', ['rev-parse', 'HEAD'], { cwd: source }).trim();
  const release = { version: options.version, tag: `v${options.version}`, channel: 'local', sourceCommit };
  const archives = packageHostUpdateCli(release, source, resolve(options.output),
    { run, runtimes: options.runtime ?? hostUpdateCliRuntimes });
  for (const { name, sha256 } of archives) console.log(`${sha256}  ${name}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main(process.argv.slice(2)).catch(error => {
    console.error(`Host-update CLI package failed: ${error.message}`);
    process.exitCode = 1;
  });
}
