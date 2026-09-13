import { sign } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { repository, requireThat, requireString } from './release-policy.mjs';
import { verifyCanonicalReleaseEvidence } from './canonical-qualification.mjs';
import {
  boundedFetch, digest, githubHeaders, readOnlyClient, rehearsalContext, rehearsalWorkflow, responseJson,
  shaPattern, snapshot, verifyRuntime, verifyUnchanged,
} from './release-rehearsal.mjs';

export const fixtureAppId = '4927270';
const repositoryId = 1044049720;
const root = `https://api.github.com/repos/${repository}`;

export async function verifyFixtureEnvironment(api, context) {
  const name = `release-rehearsal-fixture-${context.channel}`;
  const environment = await api(`environments/${name}`);
  const policies = await api(`environments/${name}/deployment-branch-policies`);
  const reviewers = environment.protection_rules?.filter(rule => rule.type === 'required_reviewers');
  requireThat(environment.name === name && environment.can_admins_bypass === false &&
    environment.deployment_branch_policy?.custom_branch_policies === true &&
    environment.deployment_branch_policy?.protected_branches === false &&
    policies.branch_policies?.length === 1 &&
    policies.branch_policies[0].name === context.branch && policies.branch_policies[0].type === 'branch' &&
    reviewers?.length === 1 && reviewers[0].prevent_self_review === (context.mode === 'separation-of-duties') &&
    reviewers[0].reviewers?.length === 1 && reviewers[0].reviewers[0].type === 'User' &&
    reviewers[0].reviewers[0].reviewer?.login === 'jpapiez' &&
    Number.isSafeInteger(reviewers[0].reviewers[0].reviewer?.id) &&
    reviewers[0].reviewers[0].reviewer.id > 0,
  'Fixture environment must require explicit owner approval on the canonical branch');
}

export function intendedFixture(context, before) {
  requireString(context.run, /^[1-9][0-9]*$/, 'fixture run');
  requireString(context.sha, shaPattern, 'fixture source');
  requireThat(context.marker === `v-rehearsal-2668-${context.run}-1`, 'Unbound fixture marker');
  requireString(before.ledger.head, shaPattern, 'fixture target');
  requireThat(!before.tags.some(tag =>
    [ `refs/tags/${context.marker}`, `refs/tags/${context.marker}-update` ].includes(tag.ref)),
  'Fixture must be absent; conflicts are never reused');
  return { ref: `refs/tags/${context.marker}-update`, sha: before.ledger.head, type: 'commit' };
}

export function fixtureRequestUrl(endpoint, method, body, context, before) {
  const fixture = intendedFixture(context, before);
  requireThat(endpoint === 'git/refs' && method === 'POST' &&
    isDeepStrictEqual(body, { ref: fixture.ref, sha: fixture.sha }),
  'Only the exact absent inert fixture creation is allowed');
  return `${root}/git/refs`;
}

export function fixtureWriter(token, context, before, fetcher = fetch) {
  let used = false;
  return async (endpoint, method, body) => {
    const url = fixtureRequestUrl(endpoint, method, body, context, before);
    requireThat(!used, 'Fixture writes are single-use and never retried');
    used = true;
    return appRequest(fetcher, url, method, token, body);
  };
}

async function verifyFixtureDefaultBranch(api) {
  const metadata = await api('');
  requireThat(metadata.full_name === repository && metadata.default_branch === 'development',
    'Fixture workflow audit requires development as the repository default branch');
}

export function verifyFixtureInventory(before, after, fixture) {
  requireThat(!before.tags.some(tag => tag.ref === fixture.ref) &&
    after.tags.filter(tag => tag.ref === fixture.ref).length === 1 &&
    isDeepStrictEqual(after.tags.find(tag => tag.ref === fixture.ref), fixture),
  'Intended fixture missing or changed');
  verifyUnchanged(before, { ...after, tags: after.tags.filter(tag => tag.ref !== fixture.ref) });
}

async function appRequest(fetcher, url, method, token, body) {
  return boundedFetch(fetcher, url, {
    method, headers: { ...githubHeaders(token), 'Content-Type': 'application/json' },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
}

export async function withFixtureToken(privateKey, operation, fetcher = fetch) {
  let token;
  try {
    const now = Math.floor(Date.now() / 1000);
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    const unsigned = `${encode({ alg: 'RS256', typ: 'JWT' })}.${encode({
      iat: now - 60, exp: now + 300, iss: fixtureAppId,
    })}`;
    const jwt = `${unsigned}.${sign('RSA-SHA256', Buffer.from(unsigned), privateKey).toString('base64url')}`;
    const installationResponse = await appRequest(fetcher, `${root}/installation`, 'GET', jwt);
    requireThat(installationResponse.status === 200, 'Fixture installation lookup failed');
    const installation = await responseJson(installationResponse);
    requireThat(Number.isSafeInteger(installation.id) && installation.id > 0 &&
      String(installation.app_id) === fixtureAppId && installation.account?.login === 'OlyForge3D' &&
      installation.target_type === 'Organization' && !installation.suspended_at,
    'Untrusted fixture installation');
    const issued = await appRequest(fetcher,
      `https://api.github.com/app/installations/${installation.id}/access_tokens`, 'POST', jwt,
      { repository_ids: [repositoryId], permissions: { contents: 'write' } });
    requireThat(issued.status === 201, 'Fixture token issuance failed');
    const grant = await responseJson(issued);
    // Capture only for revocation, including when scope validation fails.
    if (typeof grant?.token === 'string') token = grant.token;
    requireThat(typeof token === 'string' && token.length > 0 && !/[^A-Za-z0-9_]/.test(token),
      'Invalid fixture credential');
    requireThat(isDeepStrictEqual(grant.permissions, { contents: 'write', metadata: 'read' }) &&
      grant.repository_selection === 'selected' && grant.repositories?.length === 1 &&
      grant.repositories[0].id === repositoryId && grant.repositories[0].full_name === repository &&
      Number.isFinite(Date.parse(grant.expires_at)) && Date.parse(grant.expires_at) > Date.now() &&
      Date.parse(grant.expires_at) <= Date.now() + 65 * 60_000,
    'Fixture credential scope is not repository-only contents-write');
    return await operation(token);
  } catch {
    throw new Error('Fixture credential or operation failed closed');
  } finally {
    if (token !== undefined) {
      const revoked = await appRequest(fetcher, 'https://api.github.com/installation/token', 'DELETE', token);
      token = undefined;
      requireThat(revoked.status === 204, 'Fixture credential revocation unconfirmed');
    }
  }
}

export async function provisionFixture(env, fetcher = fetch) {
  const context = rehearsalContext(env);
  requireThat(env.REHEARSAL_DENIAL_PROBES === 'true' && env.RELEASE_PUBLISHER_APP_ID === fixtureAppId &&
    !env.REHEARSAL_APP_TOKEN && !env.RELEASE_PUBLISHER_TOKEN && !env.RELEASE_REGISTRY_TOKEN &&
    !env.RELEASE_PUBLISHER_PRIVATE_KEY && !env.RELEASE_OWNER_APPROVED_REVIEWERS,
  'Fixture requires its own App credential and explicit bounded-probe request');
  requireString(env.REHEARSAL_POSITIVE_DIGEST, /^[a-f0-9]{64}$/, 'positive inventory digest');
  const api = readOnlyClient(env.GH_TOKEN, fetcher);
  const result = { kind: 'release-rehearsal-only', schema: 1, repository,
    workflow: rehearsalWorkflow, workflowSha: context.sha, branch: context.branch, channel: context.channel,
    run: context.run, runAttempt: 1, source: context.sha,
    passed: false, fixtures: [], attempts: [], inventoriesComplete: false };
  let before;
  let fixture;
  try {
    await verifyRuntime(api, context);
    await verifyFixtureEnvironment(api, context);
    await verifyCanonicalReleaseEvidence(api, context.sha, context.channel, context.mode);
    before = await snapshot(api, context);
    result.before = before;
    requireThat(digest(before) === env.REHEARSAL_POSITIVE_DIGEST, 'Drift since positive rehearsal');
    fixture = intendedFixture(context, before);
    const { verifyFixtureTarget } = await import('./release-rehearsal-target.mjs');
    await verifyFixtureTarget(api, before.ledger.tree);
    await verifyFixtureDefaultBranch(api);
    const defaultCommit = await api(`git/commits/${before.heads.development}`);
    requireThat(defaultCommit.sha === before.heads.development, 'Default workflow commit mismatch');
    await verifyFixtureTarget(api, defaultCommit.tree?.sha);
    result.workflowTriggersVerified = true;
    result.fixtures.push({ kind: 'app-provisioned-inert-tag', ...fixture,
      appId: fixtureAppId, expectedAbsent: true, disposition: 'retain-no-deletion-bypass' });
    await withFixtureToken(env.RELEASE_REHEARSAL_FIXTURE_PRIVATE_KEY, async token => {
      requireThat(token !== env.GH_TOKEN, 'Generic workflow token cannot create fixture');
      const write = fixtureWriter(token, context, before, fetcher);
      await verifyRuntime(api, context);
      await verifyFixtureEnvironment(api, context);
      await verifyFixtureDefaultBranch(api);
      verifyUnchanged(before, await snapshot(api, context));
      const body = { ref: fixture.ref, sha: fixture.sha };
      const attempt = { endpoint: 'git/refs', method: 'POST', outcome: 'unknown' };
      result.attempts.push(attempt);
      const response = await write('git/refs', 'POST', body);
      attempt.status = response.status;
      attempt.outcome = response.status === 201 ? 'accepted-unverified' : 'failed';
      requireThat(response.status === 201, 'Fixture create failed; never retry');
      const created = await responseJson(response);
      requireThat(created.ref === fixture.ref && created.object?.sha === fixture.sha &&
        created.object?.type === 'commit', 'Ambiguous fixture creation response');
      const observed = await api(`git/ref/tags/${context.marker}-update`);
      requireThat(observed.ref === fixture.ref && observed.object?.sha === fixture.sha &&
        observed.object?.type === 'commit', 'Fixture creation not independently observed');
      attempt.outcome = 'created-and-read-verified';
    }, fetcher);
    result.passed = true;
  } catch {
    result.failure = 'Fixture failed or outcome unknown; retain evidence, do not retry or delete';
  } finally {
    if (before) {
      try {
        result.after = await snapshot(api, context);
        result.inventoriesComplete = true;
        if (fixture && result.attempts.length === 1) verifyFixtureInventory(before, result.after, fixture);
        else verifyUnchanged(before, result.after);
        await verifyRuntime(api, context);
        await verifyFixtureDefaultBranch(api);
      } catch {
        result.passed = false;
        result.failure = 'Fixture inventory changed or incomplete; owner recovery required';
      }
    }
  }
  result.passed = result.passed && result.inventoriesComplete && result.attempts.length === 1;
  if (result.passed) result.inventoryDigest = digest(result.after);
  return result;
}
