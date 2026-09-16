import assert from 'node:assert/strict';
import { repository, releaseRequiredChecks } from '../../release-policy.mjs';

export function protectionFixture(channel = 'insider', approvalMode = 'single-maintainer', appId = 123) {
  const branch = channel === 'stable' ? 'main' : 'development';
  const names = ['release-canonical-tags', 'release-ledger-continuity', 'release-tag-creators', 'release-ledger-writer'];
  const rulesets = names.map((name, id) => ({
    id: id + 1, name, enforcement: 'active', privateMarker: 'raw-policy-sentinel',
    target: id % 2 === 0 ? 'tag' : 'branch',
    conditions: { ref_name: { include: [id % 2 === 0 ? 'refs/tags/v*' : 'refs/heads/release-ledger'], exclude: [] } },
    bypass_actors: id < 2 ? [] : [{ actor_type: 'Integration', actor_id: appId }],
    rules: (id === 0 ? ['update', 'deletion'] : id === 1 ? ['non_fast_forward', 'deletion']
      : id === 2 ? ['creation'] : ['update']).map(type => ({ type })),
  }));
  const environment = { name: `release-${channel}`, privateMarker: 'raw-environment-sentinel',
    can_admins_bypass: false,
    deployment_branch_policy: { custom_branch_policies: true, protected_branches: false },
    protection_rules: [{ type: 'branch_policy' }] };
  const branchRules = [
    { type: 'deletion' }, { type: 'non_fast_forward' },
    { type: 'pull_request', parameters: {
      require_code_owner_review: approvalMode === 'separation-of-duties',
      required_approving_review_count: approvalMode === 'separation-of-duties' ? 1 : 0,
      required_review_thread_resolution: true, require_last_push_approval: false,
      dismiss_stale_reviews_on_push: true,
    } },
    { type: 'required_status_checks', parameters: { strict_required_status_checks_policy: true,
      required_status_checks: releaseRequiredChecks.map(context => ({ context })) } },
  ].map(rule => ({ ...rule, ruleset_id: 5, ruleset_source_type: 'Repository', ruleset_source: repository }));
  const branchRuleset = { id: 5, name: 'protected-release-branches', enforcement: 'active', target: 'branch',
    bypass_actors: [], rules: branchRules,
    conditions: { ref_name: { include: ['refs/heads/main', 'refs/heads/development'], exclude: [] } } };
  const api = async (endpoint, method = 'GET') => {
    assert.equal(method, 'GET');
    if (endpoint === `rules/branches/${branch}?per_page=100`) return branchRules;
    if (endpoint === 'rulesets/5') return branchRuleset;
    if (endpoint === `environments/${environment.name}`) return environment;
    if (endpoint === `environments/${environment.name}/deployment-branch-policies`) {
      return { total_count: 1, branch_policies: [{ name: 'development', type: 'branch' }] };
    }
    if (endpoint === 'rulesets?per_page=100') return rulesets;
    if (endpoint.startsWith('rulesets/')) return rulesets.find(rule => rule.id === Number(endpoint.split('/')[1]));
    throw new Error(endpoint);
  };
  return { api, environment, rulesets, branchRules, branchRuleset };
}
