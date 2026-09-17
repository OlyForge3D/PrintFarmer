export const repository = 'OlyForge3D/PrintFarmer';
export const workflow = '.github/workflows/consolidated-release.yml';
export const components = Object.freeze({
  api: { target: 'api-runtime', platforms: ['linux/amd64', 'linux/arm64'] },
  frontend: { target: 'frontend-runtime', platforms: ['linux/amd64', 'linux/arm64'] },
  'slicer-host': { target: 'slicer-host-runtime', platforms: ['linux/amd64', 'linux/arm64'] },
  'printer-discovery': { target: 'printer-discovery-runtime', platforms: ['linux/amd64', 'linux/arm64'] },
  'orcaslicer-worker': { target: 'orcaslicer-worker', platforms: ['linux/amd64'] },
  monolith: { target: 'monolith-runtime', platforms: ['linux/amd64', 'linux/arm64'] },
});

export function requireThat(condition, message) {
  if (!condition) throw new Error(message);
}

export function parseTag(tag) {
  const numeric = '(0|[1-9][0-9]*)';
  const match = typeof tag === 'string' &&
    tag.match(new RegExp(`^v(${numeric}\\.${numeric}\\.${numeric})(?:-(insider|beta|rc)\\.([1-9][0-9]*))?$`));
  requireThat(match && match[0] === tag, 'Use X.Y.Z or X.Y.Z-insider.N without leading zeros');
  const [, baseVersion, major, minor, patch, stage, sequence] = match;
  return { baseVersion, major, minor, patch, stage, sequence,
    canonicalVersion: tag.slice(1), channel: stage ? 'insider' : 'stable' };
}

export function parseVersionFile(text) {
  const parsed = parseTag(text.replace(/\r?\n$/, ''));
  requireThat(!parsed.stage, 'VERSION must contain vX.Y.Z');
  return parsed.baseVersion;
}

export function compareVersions(left, right) {
  const a = parseTag(`v${left}`);
  const b = parseTag(`v${right}`);
  for (const field of ['major', 'minor', 'patch']) {
    if (BigInt(a[field]) !== BigInt(b[field])) return BigInt(a[field]) > BigInt(b[field]) ? 1 : -1;
  }
  if (a.stage !== b.stage) {
    if (!a.stage) return 1;
    if (!b.stage) return -1;
    return a.stage > b.stage ? 1 : -1;
  }
  return !a.stage || a.sequence === b.sequence ? 0 : BigInt(a.sequence) > BigInt(b.sequence) ? 1 : -1;
}

export function validateVersion(version, channel, versionFile) {
  requireThat(['stable', 'insider'].includes(channel), 'Unknown release channel');
  const parsed = parseTag(`v${version}`);
  requireThat(parsed.channel === channel && (!parsed.stage || parsed.stage === 'insider'),
    'Version must match the selected channel (stable X.Y.Z / insider X.Y.Z-insider.N)');
  requireThat(parsed.baseVersion === parseVersionFile(versionFile), 'Release base must match selected-source VERSION');
  requireThat(BigInt(parsed.major) > 0n, 'Signed release major version must be greater than zero');
  requireThat(BigInt(parsed.major) <= 99n, 'Major version exceeds sequence encoding limit of 99');
  requireThat(BigInt(parsed.minor) <= 999n, 'Minor version exceeds sequence encoding limit of 999');
  requireThat(BigInt(parsed.patch) <= 99999n, 'Patch version exceeds sequence encoding limit of 99999');
  requireThat(!parsed.sequence || BigInt(parsed.sequence) <= 99998n,
    'Prerelease sequence exceeds encoding limit of 99998');
  requireThat(parsed.baseVersion.split('.').every(part => BigInt(part) <= 65534n),
    'Version exceeds .NET assembly version limits');
  return parsed;
}

export function verifyEnvironmentRestrictions(environment, policies, channel) {
  requireThat(environment?.name === `release-${channel}` &&
    environment.deployment_branch_policy?.custom_branch_policies === true &&
    environment.deployment_branch_policy.protected_branches === false &&
    environment.can_admins_bypass === false,
  'Release environment protection is missing or changed');
  requireThat(policies?.total_count === 1 && policies.branch_policies?.length === 1 &&
    policies.branch_policies[0].name === 'development' && policies.branch_policies[0].type === 'branch',
  'Release environment must allow only development');
  requireThat(Array.isArray(environment.protection_rules) && environment.protection_rules.length === 1 &&
    environment.protection_rules[0].type === 'branch_policy',
  'Release environment must retain the approved owner-manual, no-second-reviewer configuration');
}

export function validateCandidate(candidate, now, maximumDays) {
  requireThat(Number.isInteger(maximumDays) && maximumDays > 0, 'Owner must choose candidate expiry limit');
  const target = parseTag(`v${candidate.target}`);
  requireThat(!target.stage && candidate.branch === `release/v${target.baseVersion}`, 'Invalid stabilization branch');
  requireThat(/^[a-f0-9]{40}$/.test(candidate.sourceCommit) && candidate.owner &&
    candidate.qualification && candidate.created && candidate.expires, 'Incomplete candidate ownership/evidence');
  const created = Date.parse(candidate.created);
  const expires = Date.parse(candidate.expires);
  requireThat(Number.isFinite(created) && Number.isFinite(expires) && expires > created &&
    expires - created <= maximumDays * 86400000 && Date.parse(now) < expires, 'Expired candidate or invalid lifetime');
  if (candidate.action === 'delete') {
    requireThat(candidate.mergeBack?.development === true && candidate.mergeBack?.activeCandidates === true &&
      candidate.mergeBack?.versionDidNotRegress === true, 'Candidate deletion requires merge-back parity');
    requireThat(candidate.publication || candidate.abandonmentReason, 'Retain candidate until publication or documented abandonment');
  }
  requireThat(!candidate.publish, 'Stabilization branches never publish');
  return candidate;
}
