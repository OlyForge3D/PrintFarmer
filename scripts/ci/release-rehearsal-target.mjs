import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { load } from 'js-yaml';
import { requireThat, requireString } from './release-policy.mjs';
import { shaPattern } from './release-rehearsal.mjs';

// This existing tag-push consumer only synchronizes issue labels, never publication.
export const labelSyncBlob = 'beb3ab32471fe9569402b5fff4ee89ef6d820d7b';

export function verifyFixtureWorkflow(source, blobSha) {
  const document = load(source);
  const events = document?.on;
  requireThat(events && typeof events === 'object' && !Array.isArray(events) &&
    !Object.hasOwn(events, 'create'), 'Unbounded fixture event consumer');
  if (Object.hasOwn(events, 'workflow_run')) {
    requireThat(isDeepStrictEqual(events.workflow_run?.workflows, ['Qualify canonical release']),
      'Unbounded fixture workflow-run consumer');
  }
  if (!Object.hasOwn(events, 'push')) return;
  const push = events.push;
  requireThat(push && typeof push === 'object' && !Array.isArray(push), 'Unfiltered fixture push');
  if (push.tags) {
    requireThat(isDeepStrictEqual(push.tags, ['v[0-9]+.[0-9]+.[0-9]+']) ||
      isDeepStrictEqual(push.tags, ['ios/v*-alpha*', 'ios/v*-beta*', 'ios/v*-rc*']),
    'Tag trigger is not proven disjoint from inert fixtures');
    return;
  }
  if (Object.hasOwn(push, 'branches') || Object.hasOwn(push, 'branches-ignore')) {
    requireThat(!Object.hasOwn(push, 'tags-ignore'), 'Unbounded ignored-tag consumer');
    return;
  }
  requireThat(blobSha === labelSyncBlob, 'Unreviewed unfiltered fixture consumer');
}

export async function verifyFixtureTarget(api, treeSha) {
  requireString(treeSha, shaPattern, 'fixture tree');
  const readTree = async sha => {
    const tree = await api(`git/trees/${sha}`);
    requireThat(tree.sha === sha && tree.truncated === false && Array.isArray(tree.tree) &&
      tree.tree.length < 1000 &&
      new Set(tree.tree.map(entry => entry.path)).size === tree.tree.length,
    'Incomplete fixture workflow inventory');
    return tree.tree;
  };
  let entries = await readTree(treeSha);
  for (const path of ['.github', 'workflows']) {
    const entry = entries.find(entry => entry.path === path);
    if (!entry) return;
    requireThat(entry.type === 'tree', 'Non-directory workflow inventory');
    requireString(entry.sha, shaPattern, 'workflow directory');
    entries = await readTree(entry.sha);
  }
  for (const entry of entries.filter(entry => /\.ya?ml$/i.test(entry.path))) {
    requireThat(entry.type === 'blob' && ['100644', '100755'].includes(entry.mode),
      'Non-file workflow definition');
    requireString(entry.sha, shaPattern, 'workflow blob');
    const blob = await api(`git/blobs/${entry.sha}`);
    requireThat(blob.sha === entry.sha && blob.encoding === 'base64' && typeof blob.content === 'string',
      'Malformed fixture workflow blob');
    const bytes = Buffer.from(blob.content, 'base64');
    requireThat(bytes.length <= 1024 * 1024 && blob.size === bytes.length &&
      createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex') === entry.sha,
    'Fixture workflow blob mismatch');
    verifyFixtureWorkflow(bytes.toString('utf8'), entry.sha);
  }
}
