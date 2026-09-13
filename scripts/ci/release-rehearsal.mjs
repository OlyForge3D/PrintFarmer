import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import {
  components, repository, requireThat, requireString, validateApprovalMode,
} from './release-policy.mjs';
import { githubRequestUrl, gitLedger, verifyProtection } from './release-github.mjs';
import { qualificationRequestUrl, verifyCanonicalReleaseEvidence } from './canonical-qualification.mjs';

export const rehearsalWorkflow = '.github/workflows/release-protection-rehearsal.yml';
export const packageNames = Object.keys(components).map(name => `printfarmer-${name}`);
export const shaPattern = /^[a-f0-9]{40}$/;
const root = `https://api.github.com/repos/${repository}`;
const idPattern = /^[1-9][0-9]*$/;

function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
  }
  return value;
}

export function digest(value) {
  return createHash('sha256').update(JSON.stringify(canonical(value))).digest('hex');
}

export function rehearsalContext(env) {
  const channel = env.REHEARSAL_CHANNEL;
  requireThat(['stable', 'insider'].includes(channel), 'Invalid rehearsal channel');
  const branch = channel === 'stable' ? 'main' : 'development';
  requireThat(env.GITHUB_REPOSITORY === repository && env.GITHUB_EVENT_NAME === 'workflow_dispatch' &&
    env.GITHUB_REF === `refs/heads/${branch}` && env.GITHUB_RUN_ATTEMPT === '1' &&
    env.GITHUB_WORKFLOW_REF === `${repository}/${rehearsalWorkflow}@refs/heads/${branch}` &&
    env.GITHUB_SHA === env.GITHUB_WORKFLOW_SHA, 'Untrusted rehearsal context');
  requireString(env.GITHUB_SHA, shaPattern, 'rehearsal SHA');
  requireString(env.GITHUB_RUN_ID, idPattern, 'rehearsal run');
  requireString(env.RELEASE_LEDGER_ANCHOR, shaPattern, 'ledger anchor');
  validateApprovalMode(env.RELEASE_APPROVAL_MODE);
  return Object.freeze({
    channel, branch, sha: env.GITHUB_SHA, run: env.GITHUB_RUN_ID,
    anchor: env.RELEASE_LEDGER_ANCHOR, mode: env.RELEASE_APPROVAL_MODE,
    marker: `v-rehearsal-2668-${env.GITHUB_RUN_ID}-1`,
  });
}

export function rehearsalReadUrl(endpoint) {
  requireThat(typeof endpoint === 'string', 'Invalid rehearsal read route');
  const packages = /^packages\/(printfarmer-[a-z-]+)(\/versions\?per_page=100&page=[1-9][0-9]*)?$/;
  const match = packages.exec(endpoint);
  if (match) {
    requireThat(packageNames.includes(match[1]), 'Unapproved rehearsal package');
    return `https://api.github.com/orgs/OlyForge3D/packages/container/${match[1]}${match[2] ?? ''}`;
  }
  const reads = [
    /^git\/matching-refs\/tags\?per_page=100&page=[1-9][0-9]*$/,
    /^git\/ref\/tags\/v-rehearsal-2668-[1-9][0-9]*-1-update$/,
    /^releases\?per_page=100&page=[1-9][0-9]*$/,
    /^releases\/[1-9][0-9]*\/assets\?per_page=100&page=[1-9][0-9]*$/,
    /^actions\/runs\/[1-9][0-9]*$/,
    /^actions\/workflows\/release-protection-rehearsal\.yml$/,
    /^actions\/workflows\/(?:consolidated-release|docker-publish)\.yml\/runs\?status=(?:queued|in_progress|waiting|pending|requested)&per_page=100$/,
  ];
  if (reads.some(pattern => pattern.test(endpoint))) return `${root}/${endpoint}`;
  try { return githubRequestUrl(endpoint, 'GET'); } catch { /* Try the other read-only route inventory. */ }
  return qualificationRequestUrl(endpoint, 'GET', false);
}

export async function boundedFetch(fetcher, url, options) {
  try {
    const response = await fetcher(url, {
      ...options, redirect: 'error', signal: AbortSignal.timeout(20_000),
    });
    requireThat(response && Number.isInteger(response.status) &&
      response.status >= 200 && response.status < 600 && !response.redirected,
    'Invalid HTTP response');
    requireThat(response.status < 300 || response.status >= 400, 'Redirect rejected');
    return response;
  } catch {
    // Provider bodies, URLs and transport errors can contain credentials.
    throw new Error('Rehearsal transport failed or redirected');
  }
}

export async function responseJson(response) {
  try {
    const text = await response.text();
    requireThat(text.length <= 16 * 1024 * 1024, 'Oversized response');
    return JSON.parse(text);
  } catch {
    throw new Error('Malformed rehearsal API response');
  }
}

export function githubHeaders(token) {
  return {
    Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json',
    'X-GitHub-Api-Version': '2022-11-28',
  };
}

export function readOnlyClient(token, fetcher = fetch) {
  requireThat(typeof token === 'string' && token.length > 0, 'Missing read credential');
  let remaining = 2000;
  const api = async (endpoint, method = 'GET', body) => {
    requireThat(method === 'GET' && body === undefined, 'Rehearsal adapter is GET-only');
    requireThat(remaining-- > 0, 'Rehearsal read budget exceeded');
    const url = rehearsalReadUrl(endpoint);
    const response = await boundedFetch(fetcher, url, { method: 'GET', headers: githubHeaders(token) });
    requireThat(response.status === 200, 'Rehearsal read denied');
    const data = await responseJson(response);
    // A paginated response may not silently become an apparently complete shorter page.
    if (response.headers.get('link')) {
      requireThat(/(?:[?&])page=[1-9][0-9]*/.test(endpoint), 'Unbounded response pagination');
      api.nextPages.set(endpoint, response.headers.get('link'));
    }
    return data;
  };
  api.nextPages = new Map();
  return api;
}

export async function allPages(api, endpoint) {
  const result = [];
  const seen = new Set();
  for (let page = 1; page <= 100; page++) {
    const path = `${endpoint}${endpoint.includes('?') ? '&' : '?'}per_page=100&page=${page}`;
    const entries = await api(path);
    requireThat(Array.isArray(entries) && entries.length <= 100, 'Malformed inventory page');
    for (const entry of entries) {
      requireThat(entry && typeof entry === 'object' && !Array.isArray(entry), 'Malformed inventory entry');
      const key = entry.id ?? entry.ref;
      requireThat((typeof key === 'string' || Number.isSafeInteger(key)) && !seen.has(key),
        'Repeated or unidentified inventory entry');
      seen.add(key);
      result.push(entry);
    }
    const link = api.nextPages?.get(path);
    if (link) {
      const next = [...link.matchAll(/<([^>]+)>; rel="next"/g)];
      if (next.length) {
        requireThat(next.length === 1 && next[0][1] === rehearsalReadUrl(
          `${endpoint}${endpoint.includes('?') ? '&' : '?'}per_page=100&page=${page + 1}`),
        'Untrusted inventory pagination');
        continue;
      }
    }
    requireThat(entries.length < 100, 'Inventory completeness cannot be established');
    return result;
  }
  throw new Error('Inventory page budget exceeded');
}

function integer(value) {
  requireThat(Number.isSafeInteger(value) && value > 0, 'Invalid inventory identifier');
  return value;
}

function text(value) {
  requireString(value, /^[^\u0000-\u001f\u007f]{1,512}$/, 'inventory text');
  return value;
}

function ordered(entries, key) {
  return entries.sort((a, b) => String(a[key]).localeCompare(String(b[key]), 'en'));
}

export async function snapshot(api, context) {
  const tags = ordered((await allPages(api, 'git/matching-refs/tags')).map(ref => {
    requireThat(typeof ref.ref === 'string' && ref.ref.startsWith('refs/tags/') &&
      ['tag', 'commit'].includes(ref.object?.type), 'Malformed tag inventory');
    requireString(ref.object.sha, shaPattern, 'tag object');
    return { ref: text(ref.ref), sha: ref.object.sha, type: ref.object.type };
  }), 'ref');
  const heads = {};
  for (const branch of ['main', 'development', 'release-ledger']) {
    const ref = await api(`git/ref/heads/${branch}`);
    requireThat(ref.ref === `refs/heads/${branch}` && ref.object?.type === 'commit', 'Malformed branch ref');
    requireString(ref.object.sha, shaPattern, 'branch head');
    heads[branch] = ref.object.sha;
  }
  requireThat(heads[context.branch] === context.sha, 'Canonical HEAD drift');
  const ledgerCommit = await api(`git/commits/${heads['release-ledger']}`);
  requireThat(ledgerCommit.sha === heads['release-ledger'], 'Ledger commit mismatch');
  requireString(ledgerCommit.tree?.sha, shaPattern, 'ledger tree');
  const ledger = await gitLedger(api, context.anchor).read();
  requireThat(ledger.revision === heads['release-ledger'], 'Ledger moved during inventory');
  const releases = [];
  for (const release of await allPages(api, 'releases')) {
    integer(release.id);
    requireThat(typeof release.draft === 'boolean' && typeof release.prerelease === 'boolean',
      'Malformed release inventory');
    const assets = ordered((await allPages(api, `releases/${release.id}/assets`)).map(asset => {
      integer(asset.id);
      requireThat(Number.isSafeInteger(asset.size) && asset.size >= 0, 'Malformed asset size');
      return { id: asset.id, name: text(asset.name), size: asset.size, digest: digest(asset) };
    }), 'id');
    releases.push({ id: release.id, tag: text(release.tag_name), digest: digest(release), assets });
  }
  const packages = {};
  for (const name of packageNames) {
    const metadata = await api(`packages/${name}`);
    requireThat(metadata.name === name && metadata.package_type === 'container' &&
      Number.isSafeInteger(metadata.version_count) && metadata.version_count >= 0,
    'Malformed package metadata');
    const versions = ordered((await allPages(api, `packages/${name}/versions`)).map(version => {
      integer(version.id);
      requireThat(/^sha256:[a-f0-9]{64}$/.test(version.name) &&
        Array.isArray(version.metadata?.container?.tags) &&
        version.metadata.container.tags.every(tag => typeof tag === 'string'), 'Malformed package version');
      return { id: version.id, name: version.name, digest: digest(version) };
    }), 'id');
    requireThat(versions.length === metadata.version_count, 'Package inventory count mismatch');
    packages[name] = { id: integer(metadata.id), digest: digest(metadata), versions };
  }
  return { tags, heads, ledger: { head: ledger.revision, tree: ledgerCommit.tree.sha,
    state: digest(ledger.state) }, releases: ordered(releases, 'id'), packages };
}

export function verifyUnchanged(before, after) {
  requireThat(isDeepStrictEqual(before, after), 'Protected resource inventory changed');
}

export async function verifyRuntime(api, context) {
  const definition = await api('actions/workflows/release-protection-rehearsal.yml');
  const run = await api(`actions/runs/${context.run}`);
  requireThat(definition.path === rehearsalWorkflow && definition.state === 'active' &&
    run.workflow_id === definition.id && String(run.id) === context.run &&
    run.path === rehearsalWorkflow && run.event === 'workflow_dispatch' && run.run_attempt === 1 &&
    run.head_sha === context.sha && run.head_branch === context.branch &&
    run.repository?.full_name === repository && run.head_repository?.full_name === repository &&
    run.status === 'in_progress', 'Untrusted live rehearsal run');
  for (const workflow of ['consolidated-release', 'docker-publish']) {
    for (const status of ['queued', 'in_progress', 'waiting', 'pending', 'requested']) {
      const result = await api(`actions/workflows/${workflow}.yml/runs?status=${status}&per_page=100`);
      requireThat(result.total_count === 0 && Array.isArray(result.workflow_runs) &&
        result.workflow_runs.length === 0, 'Publisher active or inventory incomplete');
    }
  }
}

export async function positiveRehearsal(app, generic, context, settings) {
  await verifyRuntime(generic, context);
  const before = await snapshot(generic, context);
  await verifyProtection(app, context.channel, settings.appId, context.mode, settings.reviewers, context.sha);
  // This endpoint is checked separately even if protection policy were to omit status checks.
  const statuses = await app(`commits/${context.sha}/status?per_page=100`);
  requireThat(statuses.sha === context.sha && Array.isArray(statuses.statuses) &&
    statuses.total_count === statuses.statuses.length && statuses.total_count > 0 &&
    statuses.total_count < 100, 'Missing positive App commit-status read observation');
  const ledger = await gitLedger(app, context.anchor).read();
  requireThat(ledger.revision === before.ledger.head && digest(ledger.state) === before.ledger.state,
    'App ledger proof differs from inventory');
  await verifyCanonicalReleaseEvidence(generic, context.sha, context.channel, context.mode);
  await verifyRuntime(generic, context);
  const after = await snapshot(generic, context);
  verifyUnchanged(before, after);
  return { kind: 'release-rehearsal-only', schema: 1, run: context.run, source: context.sha,
    appReadsVerified: true, commitStatusReadObserved: true, commitStatusGrantEvidenceRequired: true,
    before, after, inventoryDigest: digest(after) };
}
