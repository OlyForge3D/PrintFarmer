import { readFileSync } from 'node:fs';
import { repository, workflow, requireThat, verifyEnvironmentRestrictions } from './release-policy.mjs';

export function githubClient(token, fetcher = fetch) {
  requireThat(token, 'GitHub token is required');
  return async (endpoint, { method = 'GET', body, allowMissing = false } = {}) => {
    const response = await fetcher(`https://api.github.com/repos/${repository}/${endpoint}`, {
      method, redirect: 'error',
      headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28', 'Content-Type': 'application/json' },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      signal: AbortSignal.timeout(60_000),
    });
    if (allowMissing && response.status === 404) return undefined;
    requireThat(response.ok, `GitHub ${method} ${endpoint} failed (${response.status})`);
    return response.status === 204 ? undefined : response.json();
  };
}

// Each channel dispatches from -- and must be signed and verified as -- its own
// exact workflow identity: stable from `main`, insider from `development`. This
// mirrors consolidated-release.yml's channel-aware source_branch/Cosign identity
// (see manifestSignatureIdentity in publish-release.mjs); no branch is hardcoded
// here independent of the selected channel.
export async function verifyOwnerDispatch(env, api, event = JSON.parse(readFileSync(env.GITHUB_EVENT_PATH, 'utf8'))) {
  const channel = env.RELEASE_CHANNEL;
  requireThat(['stable', 'insider'].includes(channel), 'Invalid release channel');
  const sourceBranch = channel === 'stable' ? 'main' : 'development';
  requireThat(env.GITHUB_REPOSITORY === repository && env.GITHUB_EVENT_NAME === 'workflow_dispatch' &&
    env.GITHUB_REF === `refs/heads/${sourceBranch}` && env.GITHUB_RUN_ATTEMPT === '1' &&
    env.GITHUB_WORKFLOW_REF === `${repository}/${workflow}@refs/heads/${sourceBranch}` &&
    /^[a-f0-9]{40}$/.test(env.GITHUB_WORKFLOW_SHA ?? '') &&
    env.GITHUB_SHA === env.GITHUB_WORKFLOW_SHA && /^[1-9][0-9]*$/.test(env.GITHUB_RUN_ID ?? ''),
  `Use a fresh owner manual dispatch of Consolidated Release on ${sourceBranch} for the ${channel} channel; reruns cannot publish`);
  const run = await api(`actions/runs/${env.GITHUB_RUN_ID}`);
  const definition = await api('actions/workflows/consolidated-release.yml');
  const owner = value => value?.login === 'jpapiez' && value.id === 5460061 && value.type === 'User';
  requireThat(run?.event === 'workflow_dispatch' && run.path === workflow &&
    run.workflow_id === definition?.id && definition.path === workflow && definition.state === 'active' &&
    String(run.id) === env.GITHUB_RUN_ID && run.run_attempt === 1 && run.status === 'in_progress' &&
    run.head_branch === sourceBranch && run.head_sha === env.GITHUB_WORKFLOW_SHA &&
    run.repository?.full_name === repository && run.head_repository?.full_name === repository &&
    run.head_repository.id === run.repository.id &&
    owner(run.actor) && owner(run.triggering_actor) && owner(event.sender) &&
    env.GITHUB_ACTOR === 'jpapiez' && env.GITHUB_ACTOR_ID === '5460061' &&
    env.GITHUB_TRIGGERING_ACTOR === 'jpapiez',
  'Only the current owner manual run may publish');
  const permission = await api('collaborators/jpapiez/permission');
  requireThat(owner(permission?.user) && permission.permission === 'admin' && permission.role_name === 'admin',
    'Owner must retain repository administrator access');
  requireThat(event.repository?.id === run.repository.id && event.repository.full_name === repository &&
    [sourceBranch, `refs/heads/${sourceBranch}`].includes(event.ref) &&
    event.inputs?.channel === env.RELEASE_CHANNEL && event.inputs.version === env.RELEASE_VERSION &&
    (event.inputs.source_sha ?? '') === (env.RELEASE_SOURCE_SHA ?? '') &&
    Object.keys(event.inputs).every(key => ['channel', 'version', 'source_sha'].includes(key)),
  'Executing inputs differ from the owner dispatch');
  requireThat(env.RELEASE_APPROVAL_MODE === 'single-maintainer', 'Approved release mode must remain single-maintainer');
  await verifyEnvironmentRestrictions(await api(`environments/release-${channel}`),
    await api(`environments/release-${channel}/deployment-branch-policies`), channel);
}
