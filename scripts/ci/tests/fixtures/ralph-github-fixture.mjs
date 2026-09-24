// Shared in-memory GitHub fixture for Ralph mailbox/runtime tests. It serves the
// private control repository plus exact PrintFarmer readbacks registered by tests.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';

export function githubFixture() {
  const blobs = new Map(), trees = new Map(), commits = new Map(), refs = new Map(), comments = new Map();
  const calls = [];
  let count = 0;
  const sha = (value) => createHash('sha1').update(value).digest('hex');
  const metadata = {
    id: 123, full_name: 'fixture/private-control', private: true, visibility: 'private',
    archived: false, disabled: false, permissions: { push: true }, default_branch: 'main',
  };
  const putRecord = (record, parents = []) => {
    const content = JSON.stringify(record);
    const blobSha = sha(`blob ${Buffer.byteLength(content)}\0${content}`);
    blobs.set(blobSha, { sha: blobSha, encoding: 'base64', size: Buffer.byteLength(content), content: Buffer.from(content).toString('base64') });
    const treeSha = sha(`tree-${++count}`);
    trees.set(treeSha, { sha: treeSha, truncated: false, tree: [{ path: 'mailbox.json', type: 'blob', mode: '100644', sha: blobSha }] });
    const commitSha = sha(`commit-${++count}`);
    commits.set(commitSha, { sha: commitSha, tree: { sha: treeSha }, parents: parents.map((parent) => ({ sha: parent })) });
    return commitSha;
  };
  let beforePatch;
  let losePatchResponse = false;
  const api = async (endpoint, method = 'GET', body) => {
    calls.push({ endpoint, method, body });
    if (comments.has(endpoint) && method === 'GET') return structuredClone(comments.get(endpoint));
    const suffix = endpoint.replace('repos/fixture/private-control', '');
    if (endpoint === 'user') return { login: 'fixture-owner' };
    if (suffix === '') return structuredClone(metadata);
    if (suffix === '/collaborators/fixture-owner/permission') return { permission: 'admin', user: { login: 'fixture-owner' } };
    if (suffix === '/branches?per_page=1') return refs.size ? [{ name: 'main' }] : [];
    if (suffix === '/contents/mailbox.json' && method === 'PUT') {
      assert.equal(body.sha, undefined);
      assert.equal(body.branch, 'main');
      if (refs.size) throw new Error('create-only conflict');
      const commitSha = putRecord(JSON.parse(Buffer.from(body.content, 'base64')));
      refs.set('heads/main', commitSha);
      return { commit: { sha: commitSha } };
    }
    if (suffix.startsWith('/git/ref/')) {
      const ref = suffix.slice('/git/ref/'.length);
      if (!refs.has(ref)) throw new Error('404 ref');
      return { ref: `refs/${ref}`, object: { sha: refs.get(ref), type: 'commit' } };
    }
    if (suffix === '/git/trees' && method === 'POST') {
      const record = JSON.parse(body.tree[0].content);
      const commitSha = putRecord(record);
      return trees.get(commits.get(commitSha).tree.sha);
    }
    if (suffix === '/git/commits' && method === 'POST') {
      const commitSha = sha(`commit-${++count}`);
      const commit = { sha: commitSha, tree: { sha: body.tree }, parents: body.parents.map((parent) => ({ sha: parent })) };
      commits.set(commitSha, commit);
      return commit;
    }
    if (suffix.startsWith('/git/commits/')) return commits.get(suffix.slice('/git/commits/'.length));
    if (suffix.startsWith('/git/trees/')) return trees.get(suffix.slice('/git/trees/'.length));
    if (suffix.startsWith('/git/blobs/')) return blobs.get(suffix.slice('/git/blobs/'.length));
    if (suffix === '/git/refs' && method === 'POST') {
      const ref = body.ref.slice('refs/'.length);
      if (refs.has(ref)) throw new Error('existing ref');
      refs.set(ref, body.sha);
      return {};
    }
    if (suffix.startsWith('/git/refs/') && method === 'PATCH') {
      assert.equal(body.force, false);
      if (beforePatch) { const hook = beforePatch; beforePatch = undefined; await hook(); }
      const ref = suffix.slice('/git/refs/'.length);
      const previous = refs.get(ref);
      const candidate = commits.get(body.sha);
      if (candidate.parents.length !== 1 || candidate.parents[0].sha !== previous) throw new Error('non-fast-forward');
      refs.set(ref, body.sha);
      if (losePatchResponse) { losePatchResponse = false; throw new Error('lost response after success'); }
      return {};
    }
    throw new Error(`Unexpected ${method} ${endpoint}`);
  };
  return { api, metadata, calls, refs, commits, blobs, comments, putRecord,
    race: (hook) => { beforePatch = hook; }, loseResponse: () => { losePatchResponse = true; } };
}

// Registers the public issue (and optional PR) readback the runtime performs for
// task packets. Tests mutate the returned record to simulate issue edits.
export function registerTaskSubject(github, evidence, { body = `Issue ${evidence.issue ?? evidence.pr} body.`, pr } = {}) {
  const subject = evidence.issue ?? evidence.pr;
  const issue = {
    number: subject, title: evidence.title, state: 'open', body,
    labels: evidence.labels.map((name) => ({ name })), assignees: [],
  };
  github.comments.set(`repos/OlyForge3D/PrintFarmer/issues/${subject}`, issue);
  if (pr) github.comments.set(`repos/OlyForge3D/PrintFarmer/pulls/${evidence.pr}`, pr);
  return issue;
}

export function registerIssueComment(github, issue, id, body) {
  const url = `https://github.com/OlyForge3D/PrintFarmer/issues/${issue}#issuecomment-${id}`;
  const comment = { id, html_url: url, issue_url: `https://api.github.com/repos/OlyForge3D/PrintFarmer/issues/${issue}`, body };
  github.comments.set(`repos/OlyForge3D/PrintFarmer/issues/comments/${id}`, comment);
  return { url, comment };
}

export function registerPullRequest(github, { number, issue, headSha, ref = `worker-${number}`, state = 'open', merged = false,
  headRepository = 'OlyForge3D/PrintFarmer' }) {
  const pr = {
    number, html_url: `https://github.com/OlyForge3D/PrintFarmer/pull/${number}`, state, merged,
    body: issue ? `Implements the assigned task.\n\nCloses #${issue}\n` : 'Repairs the assigned PR.\n',
    head: { sha: headSha, ref, repo: { full_name: headRepository } },
    base: { ref: 'development', repo: { full_name: 'OlyForge3D/PrintFarmer' } },
  };
  github.comments.set(`repos/OlyForge3D/PrintFarmer/pulls/${number}`, pr);
  return pr;
}

// Mirrors the squad/pre-pr-verdict commit status published by squad-review-verdict.yml.
export function setVerdictStatus(github, sha, description = `REVIEWED (self-attested) @ ${sha.slice(0, 12)} by bishop, hicks`, state = 'success') {
  github.comments.set(`repos/OlyForge3D/PrintFarmer/commits/${sha}/status`, {
    sha, state, statuses: [{ context: 'squad/pre-pr-verdict', state, description }],
  });
}

export function setCompare(github, base, head, status) {
  github.comments.set(`repos/OlyForge3D/PrintFarmer/compare/${base}...${head}`, { status });
}
