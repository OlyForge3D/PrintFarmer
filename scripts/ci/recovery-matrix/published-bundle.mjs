// Read-only, network-denied verification of one real published insider offline recovery bundle (#3195).
//
// verify-published-bundle.sh orchestrates three phases through the subcommands below:
//   select    (connected)      pick the newest published insider release and its bundle assets;
//   verify    (network-denied) authenticate the downloaded bundle inside an internal-network container;
//   evidence  (host)           emit the printfarmer-published-bundle-verification record.
// There is deliberately no fallback: a missing or unverifiable published bundle fails the check. It
// never builds, re-signs, downloads again, imports, activates or resets anything.
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { offlineBundleName, offlineBundleSignatureName, releaseSigningIdentity, verifyOfflineBundle }
  from '../offline-update-bundle.mjs';
import { compareVersions, parseTag, requireThat } from '../release-policy.mjs';
import { verificationKind, verificationSigningRoot, evidenceSchema, validatePublishedBundleVerification }
  from './evidence.mjs';
import { detectUbuntuHost, networkDenial, parseToolVersions } from './cell-runtime.mjs';
import { withoutCanaryAttempts } from './network-denial.mjs';

export const oidcIssuer = 'https://token.actions.githubusercontent.com';

const usage = `Usage:
  published-bundle.mjs select --releases <releases.json> [--tag <vX.Y.Z-insider.N>]
  published-bundle.mjs verify --bundle <tar> --signature <sigstore.json> --version <v> --trusted-root <file>
    --staging <new-dir> [--cosign <path>]
  published-bundle.mjs evidence --output <file> --run-id <id> --started-at <iso> --harness-commit <sha>
    --verification <result.json> --bundle-sha256 <before> --bundle-sha256-after <after>
    --host-before <file> --host-after <file> --network-attempts <ndjson> [--cosign <path>]`;

const options = {
  select: { required: ['releases'], optional: ['tag'] },
  verify: { required: ['bundle', 'signature', 'version', 'trusted-root', 'staging'], optional: ['cosign'] },
  evidence: {
    required: ['output', 'run-id', 'started-at', 'harness-commit', 'verification', 'bundle-sha256',
      'bundle-sha256-after', 'host-before', 'host-after', 'network-attempts'],
    optional: ['cosign'],
  },
};

// Every option is fixed per subcommand. There is no flag that permits a fallback download, a local
// build, a fixture root or a reset, so an unknown flag (e.g. --allow-download) is always refused.
export function parseArguments(argv) {
  const [command, ...rest] = argv;
  requireThat(Object.hasOwn(options, command ?? ''), usage);
  const { required, optional } = options[command];
  const parsed = {};
  for (let index = 0; index < rest.length; index += 2) {
    const key = rest[index]?.startsWith('--') ? rest[index].slice(2) : undefined;
    const value = rest[index + 1];
    requireThat(key && [...required, ...optional].includes(key), `Unsupported option ${rest[index]}\n${usage}`);
    requireThat(value !== undefined && !value.startsWith('--'), `Option --${key} needs a value\n${usage}`);
    requireThat(!Object.hasOwn(parsed, key), `Duplicate option --${key}`);
    parsed[key] = value;
  }
  for (const key of required) requireThat(Object.hasOwn(parsed, key), `Missing --${key}\n${usage}`);
  return { command, options: parsed };
}

export function publishedBundleAssetNames(version) {
  return { bundle: offlineBundleName(version), signature: offlineBundleSignatureName(version) };
}

function insiderVersion(tag) {
  try {
    const parsed = parseTag(tag);
    return parsed.stage === 'insider' ? parsed.canonicalVersion : undefined;
  } catch {
    return undefined;
  }
}

function assetDigest(asset, name) {
  const match = typeof asset.digest === 'string' ? /^sha256:([0-9a-f]{64})$/.exec(asset.digest) : null;
  requireThat(match, `published_bundle_digest_missing: ${name} has no sha256 digest in the release API`);
  requireThat(Number.isSafeInteger(asset.size) && asset.size > 0, `published_bundle_empty: ${name}`);
  return { name, sha256: match[1], size: asset.size };
}

// Selects the newest published (non-draft) insider release, or exactly the pinned tag. It never falls
// back to an older release when the selected one lacks its signed bundle: that is a failed check.
export function selectPublishedInsiderRelease(releases, { tag } = {}) {
  requireThat(Array.isArray(releases), 'Release list must be an array');
  if (tag !== undefined) {
    requireThat(insiderVersion(tag), `--tag must be an insider release tag: ${tag}`);
  }
  const published = releases
    .filter(release => release && !release.draft && typeof release.tag_name === 'string')
    .map(release => ({ release, version: insiderVersion(release.tag_name) }))
    .filter(entry => entry.version !== undefined);
  const candidates = tag === undefined ? published : published.filter(entry => entry.release.tag_name === tag);
  requireThat(candidates.length > 0, tag === undefined
    ? 'published_bundle_missing: no published insider release exists'
    : `published_bundle_missing: ${tag} is not a published insider release`);
  const { release, version } = candidates.reduce((best, entry) =>
    compareVersions(entry.version, best.version) > 0 ? entry : best);
  const names = publishedBundleAssetNames(version);
  const assets = Array.isArray(release.assets) ? release.assets : [];
  const find = name => {
    const matches = assets.filter(asset => asset?.name === name);
    requireThat(matches.length === 1, `published_bundle_missing: ${release.tag_name} does not publish ${name}`);
    return assetDigest(matches[0], name);
  };
  return { tag: release.tag_name, version, bundle: find(names.bundle), signature: find(names.signature) };
}

// Runs inside the network-denied container: authenticates the published archive itself with the
// insider release identity, then runs the exact offline verifier into a fresh staging directory.
export function verifyPublishedBundle({ bundle, signature, version, trustedRoot, staging, run }) {
  requireThat(parseTag(`v${version}`).stage === 'insider', 'Only an insider bundle may be verified here');
  const root = resolve(trustedRoot);
  try {
    run('cosign', ['verify-blob', '--trusted-root', root, '--bundle', resolve(signature),
      '--certificate-oidc-issuer', oidcIssuer, '--certificate-identity', releaseSigningIdentity('insider'),
      resolve(bundle)]);
  } catch (error) {
    throw new Error(`Published bundle signature verification failed: ${error.message}`);
  }
  const record = verifyOfflineBundle({ bundle: resolve(bundle), channel: 'insider', version, trustedRoot: root,
    staging: resolve(staging), run });
  requireThat(record.decision === 'verified-not-installable', 'Published bundle verification did not complete');
  return { signatureVerified: true, release: record.release, manifestDigest: record.manifestDigest };
}

export function targetIdentity(release) {
  requireThat(release && typeof release === 'object', 'Verified release identity is missing');
  const { tag, version, channel, sourceCommit, buildId, sequence } = release;
  return { tag, version, channel, sourceCommit, buildId, sequence };
}

export function readNetworkAttempts(path) {
  return readFileSync(path, 'utf8')
    .split(/\r?\n/)
    .filter(line => line.trim().length > 0)
    .map(line => {
      const parsed = JSON.parse(line);
      const attempt = { at: parsed.at, destination: parsed.destination, protocol: parsed.protocol };
      if (parsed.source !== undefined) attempt.source = parsed.source;
      if (parsed.query !== undefined) attempt.query = parsed.query;
      return attempt;
    });
}

// The check passes only when the published signature verified, nothing tried to leave the internal
// network (the canary proof is excluded), the downloaded bundle is byte-identical afterwards, and the
// host's Docker images/volumes are unchanged.
export function buildVerificationRecord({ runId, startedAt, finishedAt, harnessCommit, host, tools, verification,
  bundleSha256, bundleSha256After, hostBefore, hostAfter, attempts }) {
  const networkAttempts = withoutCanaryAttempts(attempts);
  const hostModified = bundleSha256After !== bundleSha256 || hostBefore !== hostAfter;
  const signatureVerified = verification?.signatureVerified === true;
  const record = {
    schema: evidenceSchema,
    kind: verificationKind,
    run: { id: runId, startedAt, finishedAt, harnessCommit, entryPoint: 'bash' },
    host,
    identities: {
      target: targetIdentity(verification?.release),
      bundleSha256,
      signingRoot: verificationSigningRoot,
    },
    tools,
    networkDenial: networkDenial(networkAttempts),
    verification: { signatureVerified, imported: false, activated: false, hostModified },
    verdict: signatureVerified && !hostModified && networkAttempts.length === 0 ? 'pass' : 'fail',
  };
  const errors = validatePublishedBundleVerification(record);
  requireThat(errors.length === 0, `Published bundle verification record is invalid:\n${errors.join('\n')}`);
  return record;
}

async function main(argv) {
  const { command, options: parsed } = parseArguments(argv);
  if (command === 'select') {
    const releases = JSON.parse(readFileSync(resolve(parsed.releases), 'utf8'));
    console.log(JSON.stringify(selectPublishedInsiderRelease(releases, { tag: parsed.tag })));
    return;
  }
  const cosign = parsed.cosign ?? 'cosign';
  const run = (name, args) => execFileSync(name === 'cosign' ? cosign : name, args,
    { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  if (command === 'verify') {
    console.log(JSON.stringify(verifyPublishedBundle({ bundle: parsed.bundle, signature: parsed.signature,
      version: parsed.version, trustedRoot: parsed['trusted-root'], staging: parsed.staging, run })));
    return;
  }
  const record = buildVerificationRecord({
    runId: parsed['run-id'], startedAt: parsed['started-at'], finishedAt: new Date().toISOString(),
    harnessCommit: parsed['harness-commit'], host: detectUbuntuHost(), tools: parseToolVersions({ cosign }),
    verification: JSON.parse(readFileSync(resolve(parsed.verification), 'utf8')),
    bundleSha256: parsed['bundle-sha256'], bundleSha256After: parsed['bundle-sha256-after'],
    hostBefore: readFileSync(resolve(parsed['host-before']), 'utf8'),
    hostAfter: readFileSync(resolve(parsed['host-after']), 'utf8'),
    attempts: readNetworkAttempts(resolve(parsed['network-attempts'])),
  });
  writeFileSync(resolve(parsed.output), `${JSON.stringify(record, undefined, 2)}\n`);
  console.log(JSON.stringify({ verdict: record.verdict, target: record.identities.target.tag }));
  if (record.verdict !== 'pass') process.exitCode = 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main(process.argv.slice(2)).catch(error => {
    console.error(`Published bundle verification failed: ${error.message}`);
    process.exitCode = 1;
  });
}
