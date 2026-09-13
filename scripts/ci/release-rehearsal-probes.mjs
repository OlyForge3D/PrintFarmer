import { requireThat, requireString, repository } from './release-policy.mjs';
import {
  boundedFetch, digest, githubHeaders, packageNames, readOnlyClient, responseJson,
  shaPattern, snapshot, verifyRuntime, verifyUnchanged,
} from './release-rehearsal.mjs';

const root = `https://api.github.com/repos/${repository}`;

export function probeRequestUrl(endpoint, method, body, context, before, child) {
  requireString(context.run, /^[1-9][0-9]*$/, 'probe run');
  requireString(context.sha, shaPattern, 'probe source');
  requireThat(context.marker === `v-rehearsal-2668-${context.run}-1`, 'Unbound probe marker');
  requireString(before.ledger.head, shaPattern, 'probe ledger head');
  requireString(before.ledger.tree, shaPattern, 'probe ledger tree');
  let expected;
  if (method === 'POST' && endpoint === 'git/refs') {
    expected = { ref: `refs/tags/${context.marker}`, sha: context.sha };
  } else if (method === 'POST' && endpoint === 'git/commits') {
    expected = { message: `Inert #2668 rehearsal ${context.run}/1; unchanged ledger tree`,
      tree: before.ledger.tree, parents: [before.ledger.head] };
  } else if (method === 'PATCH' &&
    [`git/refs/tags/${context.marker}-update`, 'git/refs/heads/release-ledger'].includes(endpoint)) {
    requireString(child, shaPattern, 'probe child');
    requireThat(child !== before.ledger.head, 'Probe must be a real fast-forward');
    expected = { sha: child, force: false };
  }
  requireThat(expected && digest(body) === digest(expected), 'Probe route, method or payload is not bounded');
  return `${root}/${endpoint}`;
}

export function verifyGitDenial(response, body, operation) {
  const messages = {
    create: /cannot create ref due to creations being restricted/i,
    update: /cannot update this protected ref|cannot update ref due to updates being restricted/i,
    ledger: /cannot update ref due to updates being restricted/i,
  };
  requireThat(Object.hasOwn(messages, operation) && response.status === 422 &&
    !response.headers.get('retry-after') && response.headers.get('x-ratelimit-remaining') !== '0' &&
    typeof body?.message === 'string' && /^Repository rule violations found\b/.test(body.message) &&
    messages[operation].test(body.message) &&
    !/rate.limit|abuse|spam|not.fast.forward|already exists|invalid|not found/i.test(body.message),
  'No operation-specific Git policy denial');
}

export function verifyUploadDenial(response, body) {
  const challenge = response.headers.get('www-authenticate') ?? '';
  requireThat(!response.headers.get('location') && !response.headers.get('docker-upload-uuid') &&
    !response.headers.get('retry-after') && Array.isArray(body?.errors) && body.errors.length === 1 &&
    typeof body.errors[0]?.message === 'string' &&
    !/rate.limit|abuse|spam|expired|invalid|authentication required/i.test(body.errors[0].message) &&
    ((response.status === 403 && body.errors[0].code === 'DENIED' &&
      /denied|write_package/i.test(body.errors[0].message)) ||
     (response.status === 401 && body.errors[0].code === 'UNAUTHORIZED' &&
      /\berror="insufficient_scope"/.test(challenge))),
  'No authenticated GHCR write denial');
}

export function cleanupUrl(location, name) {
  requireThat(packageNames.includes(name) && typeof location === 'string', 'Invalid upload cleanup target');
  let url;
  try { url = new URL(location, 'https://ghcr.io'); } catch { throw new Error('Invalid upload cleanup URL'); }
  requireThat(url.origin === 'https://ghcr.io' && !url.username && !url.password && !url.hash &&
    new RegExp(`^/v2/olyforge3d/${name}/blobs/uploads/[a-zA-Z0-9_-]+$`).test(url.pathname) &&
    [...url.searchParams.keys()].every(key => key === '_state') &&
    url.searchParams.getAll('_state').length <= 1, 'Untrusted upload cleanup URL');
  return url.href;
}

async function uploadProbe(fetcher, genericToken, actor, name, evidence) {
  const path = `olyforge3d/${name}`;
  const auth = await boundedFetch(fetcher,
    `https://ghcr.io/token?service=ghcr.io&scope=repository%3A${path.replace('/', '%2F')}%3Apull%2Cpush`,
    { method: 'GET', headers: {
      Authorization: `Basic ${Buffer.from(`${actor}:${genericToken}`).toString('base64')}`,
    } });
  requireThat(auth.status === 200, 'Registry authentication failed');
  const grant = await responseJson(auth);
  const token = grant.token ?? grant.access_token;
  requireThat(typeof token === 'string' && token.length > 0, 'Missing registry credential');
  const headers = { Authorization: `Bearer ${token}` };
  const read = await boundedFetch(fetcher, `https://ghcr.io/v2/${path}/tags/list?n=1`, { method: 'GET', headers });
  requireThat(read.status === 200, 'No positive generic registry read');
  const listing = await responseJson(read);
  requireThat(listing.name === path && Array.isArray(listing.tags) &&
    listing.tags.every(tag => typeof tag === 'string'), 'Malformed registry read');
  evidence.attempted = true;
  evidence.unknown = true;
  const response = await boundedFetch(fetcher, `https://ghcr.io/v2/${path}/blobs/uploads/`,
    { method: 'POST', headers: { ...headers, 'Content-Length': '0' } });
  if (response.status === 202) {
    evidence.started = true;
    evidence.unknown = false;
    const url = cleanupUrl(response.headers.get('location'), name);
    const removed = await boundedFetch(fetcher, url, { method: 'DELETE', headers });
    requireThat(removed.status === 204, 'Upload cleanup failed; owner recovery required');
    const check = await boundedFetch(fetcher, url, { method: 'GET', headers });
    const body = await responseJson(check);
    requireThat(check.status === 404 && Array.isArray(body.errors) && body.errors.length === 1 &&
      body.errors[0].code === 'BLOB_UPLOAD_UNKNOWN', 'Upload cancellation unverified');
    evidence.cancelled = true;
    throw new Error('Unexpected upload acceptance; subsequent probes stopped');
  }
  verifyUploadDenial(response, await responseJson(response));
  evidence.denied = true;
  evidence.unknown = false;
}

export async function runDenialProbes(token, context, settings, fetcher = fetch) {
  requireThat(typeof token === 'string' && token.length > 0, 'Missing generic workflow token');
  requireThat(settings.approvedSha === context.sha && settings.requested === true,
    'Owner must approve this exact rehearsal SHA and its bounded fixture effects');
  requireString(settings.actor, /^[a-zA-Z0-9][a-zA-Z0-9[\]-]{0,99}$/, 'workflow actor');
  requireString(settings.positiveDigest, /^[a-f0-9]{64}$/, 'positive inventory digest');
  const api = readOnlyClient(token, fetcher);
  const result = { kind: 'release-rehearsal-only', schema: 1, run: context.run, source: context.sha,
    passed: false, fixtures: [], attempts: [], denials: [], uploads: [], inventoriesComplete: false };
  let before;
  try {
    await verifyRuntime(api, context);
    const repo = await api('');
    requireThat(repo.full_name === repository && repo.permissions?.push === true,
      'Generic contents-write capability not established');
    before = await snapshot(api, context);
    result.before = before;
    requireThat(digest(before) === settings.positiveDigest, 'Drift since protected positive rehearsal');
    const tag = `${context.marker}-update`;
    const fixture = before.tags.find(ref => ref.ref === `refs/tags/${tag}`);
    requireThat(fixture?.type === 'commit' && fixture.sha === before.ledger.head &&
      !before.tags.some(ref => ref.ref === `refs/tags/${context.marker}`),
    'Owner-provisioned unique update fixture missing, mismatched, or creation fixture already exists');
    result.fixtures.push({ kind: 'pre-provisioned-inert-tag', ref: fixture.ref, sha: fixture.sha,
      disposition: 'retain-no-deletion-bypass' });
    result.fixtures.push({ kind: 'inert-tag-creation-attempt', ref: `refs/tags/${context.marker}`,
      sha: context.sha, disposition: 'retain-if-unexpectedly-created-no-deletion-bypass' });

    const guard = async () => {
      await verifyRuntime(api, context);
      verifyUnchanged(before, await snapshot(api, context));
    };
    let childSha;
    const sent = new Set();
    const send = async (endpoint, method, body) => {
      const url = probeRequestUrl(endpoint, method, body, context, before, childSha);
      requireThat(!sent.has(endpoint), 'Probe writes are never retried');
      sent.add(endpoint);
      const attempt = { endpoint, method, outcome: 'unknown' };
      result.attempts.push(attempt);
      const response = await boundedFetch(fetcher, url, {
        method, headers: { ...githubHeaders(token), 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      attempt.status = response.status;
      attempt.outcome = response.status >= 200 && response.status < 300 ? 'accepted' : 'rejected-unclassified';
      return response;
    };
    await guard();
    const create = await send('git/refs', 'POST', { ref: `refs/tags/${context.marker}`, sha: context.sha });
    verifyGitDenial(create, await responseJson(create), 'create');
    result.denials.push('tag-create');

    // One intended Git object, no new tree/blob and no release authorization.
    await guard();
    const created = await send('git/commits', 'POST', {
      message: `Inert #2668 rehearsal ${context.run}/1; unchanged ledger tree`,
      tree: before.ledger.tree, parents: [before.ledger.head],
    });
    requireThat(created.status === 201, 'Could not create intended inert Git fixture');
    const child = await responseJson(created);
    requireString(child.sha, shaPattern, 'fixture commit');
    childSha = child.sha;
    result.fixtures.push({ kind: 'same-tree-ledger-child', sha: child.sha,
      parent: before.ledger.head, tree: before.ledger.tree, disposition: 'retain-never-reset' });
    const verified = await api(`git/commits/${child.sha}`);
    requireThat(verified.sha === child.sha && child.sha !== before.ledger.head &&
      verified.tree?.sha === before.ledger.tree && verified.parents?.length === 1 &&
      verified.parents[0].sha === before.ledger.head, 'Invalid inert Git fixture');

    await guard();
    const update = await send(`git/refs/tags/${tag}`, 'PATCH', { sha: child.sha, force: false });
    verifyGitDenial(update, await responseJson(update), 'update');
    result.denials.push('tag-update');
    await guard();
    const ledger = await send('git/refs/heads/release-ledger', 'PATCH', { sha: child.sha, force: false });
    verifyGitDenial(ledger, await responseJson(ledger), 'ledger');
    result.denials.push('ledger-update');
    for (const name of packageNames) {
      await guard();
      const evidence = { package: name, attempted: false, denied: false,
        started: false, cancelled: false, unknown: false };
      result.uploads.push(evidence);
      await uploadProbe(fetcher, token, settings.actor, name, evidence);
      result.denials.push(`upload-start:${name}`);
    }
    result.passed = true;
  } catch {
    result.failure = 'Probe failed or outcome unknown; stop and retain evidence for owner recovery';
  } finally {
    if (before) {
      try {
        result.after = await snapshot(api, context);
        result.inventoriesComplete = true;
        verifyUnchanged(before, result.after);
        await verifyRuntime(api, context);
      } catch {
        result.passed = false;
        result.failure = 'Inventory changed, incomplete, or publisher drift; owner recovery required';
      }
    }
  }
  result.passed = result.passed && result.inventoriesComplete &&
    result.denials.length === 3 + packageNames.length &&
    result.uploads.length === packageNames.length && result.uploads.every(upload =>
      upload.denied && !upload.started && !upload.unknown);
  return result;
}
