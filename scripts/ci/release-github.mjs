import { execFileSync } from 'node:child_process';
import {
  repository, ledgerBranch, requireThat, validateLedger, verifyTag, compareVersions,
} from './release-policy.mjs';

export function githubClient(token = process.env.GH_TOKEN) {
  requireThat(token, 'Missing GitHub credential');
  return async (endpoint, method = 'GET', body) => {
    const response = await fetch(`https://api.github.com/repos/${repository}/${endpoint}`, {
      method, headers: {
        Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28',
        ...(body ? { 'Content-Type': 'application/json' } : {}),
      }, body: body ? JSON.stringify(body) : undefined,
    });
    if (!response.ok) {
      const error = new Error(`GitHub ${method} ${endpoint}: HTTP ${response.status}`);
      error.status = response.status;
      throw error;
    }
    return response.status === 204 ? undefined : response.json();
  };
}

export async function branchHead(api, branch) {
  return (await api(`git/ref/heads/${branch}`)).object.sha;
}

export async function readVersion(api, sha) {
  const file = await api(`contents/VERSION?ref=${sha}`);
  requireThat(file.encoding === 'base64', 'VERSION response is not a file');
  return Buffer.from(file.content, 'base64').toString('utf8');
}

export async function readTag(api, tag) {
  let ref;
  try { ref = await api(`git/ref/tags/${tag}`); }
  catch (error) { if (error.status === 404) return undefined; throw error; }
  const object = ref.object.sha;
  let peeled = ref.object;
  for (let depth = 0; peeled.type === 'tag' && depth < 8; depth++) {
    peeled = (await api(`git/tags/${peeled.sha}`)).object;
  }
  requireThat(peeled.type === 'commit', 'Tag does not peel to commit');
  return { object, commit: peeled.sha };
}

export function gitLedger(api, anchor) {
  async function snapshot(revision) {
    const commit = await api(`git/commits/${revision}`);
    const tree = await api(`git/trees/${commit.tree.sha}`);
    requireThat(tree.truncated === false, 'Ledger tree is truncated');
    const entry = tree.tree.find(file => file.path === 'state.json' && file.type === 'blob');
    requireThat(entry, 'Ledger state is missing; owner recovery required');
    const blob = await api(`git/blobs/${entry.sha}`);
    requireThat(blob.encoding === 'base64', 'Unsupported ledger blob encoding');
    return { commit, state: JSON.parse(Buffer.from(blob.content, 'base64').toString('utf8')) };
  }
  return {
    async read() {
      requireThat(/^[a-f0-9]{40}$/.test(anchor || ''), 'Owner must approve and pin RELEASE_LEDGER_ANCHOR');
      const revision = await branchHead(api, ledgerBranch);
      const ancestry = await api(`compare/${anchor}...${revision}`);
      requireThat(['ahead', 'identical'].includes(ancestry.status), 'Ledger anchor not in ancestry');
      const { commit: head, state } = await snapshot(revision);
      validateLedger(state, anchor);
      requireThat(head.parents.length === 1, 'Ledger must have a linear single-parent history');
      if (head.parents[0].sha !== anchor) {
        const { state: previous } = await snapshot(head.parents[0].sha);
        validateLedger(previous, anchor);
        requireThat(BigInt(state.counter) >= BigInt(previous.counter), 'Ledger counter rollback');
        for (const [key, reservation] of Object.entries(previous.reservations)) {
          requireThat(JSON.stringify(state.reservations[key]?.record) === JSON.stringify(reservation.record),
            'Ledger lost or changed an immutable reservation');
          for (const field of ['tagObject', 'tagPublished', 'setHash', 'set']) {
            if (reservation[field] !== undefined) {
              requireThat(JSON.stringify(state.reservations[key]?.[field]) === JSON.stringify(reservation[field]),
                `Ledger lost or changed immutable ${field}`);
            }
          }
          for (const [channel, pointer] of Object.entries(previous.pointers)) {
            const next = state.pointers[channel];
            requireThat(next && compareVersions(next.canonicalVersion, pointer.canonicalVersion) >= 0,
              'Ledger pointer rollback');
            if (next.canonicalVersion === pointer.canonicalVersion) {
              requireThat(JSON.stringify(next) === JSON.stringify(pointer), 'Ledger changed immutable pointer identity');
            }
          }
          for (const [base, stage] of Object.entries(previous.stages || {})) {
            requireThat(state.stages?.[base] && compareVersions(state.stages[base], stage) >= 0,
              'Ledger stage rollback');
          }
        }
      }
      return { revision, state };
    },
    async compareAndSet(revision, state) {
      const parent = await api(`git/commits/${revision}`);
      const blob = await api('git/blobs', 'POST', { content: JSON.stringify(state), encoding: 'utf-8' });
      const tree = await api('git/trees', 'POST', {
        base_tree: parent.tree.sha, tree: [{ path: 'state.json', mode: '100644', type: 'blob', sha: blob.sha }],
      });
      const commit = await api('git/commits', 'POST', {
        message: 'release: append allocation/authorization/complete-set transaction',
        tree: tree.sha, parents: [revision],
      });
      try {
        await api(`git/refs/heads/${ledgerBranch}`, 'PATCH', { sha: commit.sha, force: false });
        return true;
      } catch (error) {
        if (![409, 422].includes(error.status)) throw error;
        requireThat(await branchHead(api, ledgerBranch) !== revision,
          'Ledger update rejected by policy, not a CAS conflict');
        return false;
      }
    },
  };
}

export async function verifyProtection(api, channel, publisherAppId = process.env.RELEASE_PUBLISHER_APP_ID) {
  const branch = channel === 'stable' ? 'main' : 'development';
  const rules = await api(`rules/branches/${branch}`);
  for (const type of ['deletion', 'non_fast_forward', 'pull_request', 'required_status_checks']) {
    requireThat(rules.some(rule => rule.type === type), `Owner blocker: ${branch} lacks active ${type} rule`);
  }
  requireThat(rules.some(rule => rule.type === 'pull_request' &&
    rule.parameters?.require_code_owner_review && rule.parameters?.required_approving_review_count >= 1),
  'Owner blocker: workflow/VERSION code-owner review not enforced');
  requireThat(rules.some(rule => rule.type === 'required_status_checks' &&
    rule.parameters?.required_status_checks?.length > 0),
  'Owner blocker: no exact-SHA required checks');
  const environment = await api(`environments/release-${channel}`);
  requireThat(environment.deployment_branch_policy?.custom_branch_policies,
    'Owner blocker: publishing environment lacks branch restrictions');
  const policies = await api(`environments/release-${channel}/deployment-branch-policies`);
  requireThat(policies.branch_policies?.length === 1 &&
    policies.branch_policies[0].name === branch && policies.branch_policies[0].type === 'branch',
  'Owner blocker: publishing environment must allow only its canonical branch');
  requireThat(environment.protection_rules?.some(rule => rule.type === 'required_reviewers' &&
    rule.prevent_self_review === true && rule.reviewers?.length > 0),
  'Owner blocker: publishing environment requires non-self reviewer approval');
  const allRulesets = await api('rulesets?per_page=100');
  requireThat(allRulesets.length < 100, 'Ruleset listing may be truncated');
  const evidence = [];
  for (const name of ['release-canonical-tags', 'release-ledger-continuity']) {
    const summary = allRulesets.find(rule => rule.name === name && rule.enforcement === 'active');
    requireThat(summary, `Owner blocker: active ${name} ruleset missing`);
    const rule = await api(`rulesets/${summary.id}`);
    requireThat(rule.bypass_actors?.length === 0, `Owner blocker: ${name} permits continuity bypass`);
    const target = name === 'release-canonical-tags' ? 'tag' : 'branch';
    const include = target === 'tag' ? 'refs/tags/v*' : `refs/heads/${ledgerBranch}`;
    requireThat(rule.target === target && rule.conditions?.ref_name?.include?.includes(include) &&
      rule.conditions?.ref_name?.exclude?.length === 0, `Owner blocker: ${name} has incorrect scope`);
    for (const type of target === 'tag' ? ['update', 'deletion'] : ['non_fast_forward', 'deletion']) {
      requireThat(rule.rules.some(item => item.type === type), `Owner blocker: ${name} lacks ${type}`);
    }
    evidence.push({ id: rule.id, name: rule.name, enforcement: rule.enforcement });
  }
  for (const name of ['release-tag-creators', 'release-ledger-writer']) {
      const summary = allRulesets.find(rule => rule.name === name && rule.enforcement === 'active');
      requireThat(summary, `Owner blocker: active ${name} restriction missing`);
      const rule = await api(`rulesets/${summary.id}`);
      const tag = name === 'release-tag-creators';
      requireThat(rule.target === (tag ? 'tag' : 'branch') &&
        rule.conditions?.ref_name?.include?.includes(tag ? 'refs/tags/v*' : `refs/heads/${ledgerBranch}`) &&
        rule.conditions?.ref_name?.exclude?.length === 0 &&
        rule.rules.some(item => item.type === (tag ? 'creation' : 'update')) &&
        rule.bypass_actors?.length === 1 && rule.bypass_actors[0].actor_type === 'Integration' &&
        String(rule.bypass_actors[0].actor_id) === publisherAppId,
      `Owner blocker: ${name} must restrict writes to one explicitly approved publisher app`);
      evidence.push({ id: rule.id, name: rule.name, publisher: rule.bypass_actors[0].actor_id });
  }
  return { branch, environment: environment.name, rulesets: evidence };
}

export async function ensureSourceTag(api, store, record, transact) {
  // Reserve the annotated object in the ledger BEFORE creating its public ref.
  await transact(store, async state => {
    const entry = state.reservations[record.allocationKey];
    if (entry.tagObject) return;
    requireThat(!await readTag(api, record.sourceTag), 'Existing tag has no immutable authorization');
    const tag = await api('git/tags', 'POST', {
      tag: record.sourceTag, object: record.sourceCommit, type: 'commit',
      message: JSON.stringify(record),
      tagger: { name: 'PrintFarmer Release', email: 'release@users.noreply.github.com', date: record.created },
    });
    entry.tagObject = tag.sha;
  });
  const { state } = await store.read();
  const expected = state.reservations[record.allocationKey].tagObject;
  const actual = await readTag(api, record.sourceTag);
  requireThat(!state.reservations[record.allocationKey].tagPublished || actual,
    'Previously published tag was deleted; never recreate it');
  if (!actual) {
    await api('git/refs', 'POST', { ref: `refs/tags/${record.sourceTag}`, sha: expected });
  }
  verifyTag(record, expected, await readTag(api, record.sourceTag));
  await transact(store, state => { state.reservations[record.allocationKey].tagPublished = true; });
}

export function command(file, args) {
  return execFileSync(file, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}
