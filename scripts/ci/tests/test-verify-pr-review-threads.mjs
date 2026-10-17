import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { exitCodeFor, runCli } from '../verify-pr-review-threads.mjs';

const head = 'a'.repeat(40);
const movedHead = 'b'.repeat(40);
const cliArgs = ['--repo', 'OlyForge3D/PrintFarmer', '--pr', '2794', '--expected-head', head, '--json'];

function thread(id = 'thread-1', overrides = {}) {
  return {
    id, isResolved: false, isOutdated: false, path: 'scripts/example.mjs', line: 42,
    comments: { nodes: [{
      url: `https://github.com/OlyForge3D/PrintFarmer/pull/2794#discussion_${id}`,
      author: { login: 'human-reviewer' },
    }] },
    ...overrides,
  };
}

function page(nodes = [], { totalCount = nodes.length, hasNextPage = false, endCursor = null, headSha = head } = {}) {
  return { data: { repository: { pullRequest: {
    headRefOid: headSha,
    reviewThreads: { totalCount, nodes, pageInfo: { hasNextPage, endCursor } },
  } } } };
}

async function invoke(responses, args = cliArgs) {
  const calls = [];
  let output = '';
  const exitCode = await runCli(args, {
    api: async (query, variables) => {
      calls.push({ query, variables });
      assert.ok(responses.length, 'unexpected API request');
      const response = responses.shift();
      if (response instanceof Error) throw response;
      return response;
    },
    write: (text) => { output += text; },
  });
  return { exitCode, output, report: args.includes('--json') ? JSON.parse(output) : undefined, calls };
}

test('empty and fully resolved connections pass, retaining resolved diagnostics', async () => {
  for (const nodes of [[], [thread('resolved', { isResolved: true })]]) {
    const result = await invoke([page(nodes), page()]);
    assert.equal(result.exitCode, 0);
    assert.equal(result.report.classification, 'CLEAR');
    assert.equal(result.report.headSha, head);
    assert.equal(result.report.unresolvedCount, 0);
    assert.equal(result.report.threads.length, nodes.length);
    assert.equal(result.calls.length, 2);
    assert.doesNotMatch(result.calls[1].query, /reviewThreads|mutation/);
  }
});

test('human, CodeQL and outdated unresolved threads all block with actionable diagnostics', async () => {
  const nodes = [
    thread(),
    thread('codeql', { comments: { nodes: [{
      url: 'https://github.com/OlyForge3D/PrintFarmer/pull/2794#discussion_2',
      author: { login: 'github-advanced-security' },
    }] } }),
    thread('outdated', { isOutdated: true, line: null }),
  ];
  const result = await invoke([page(nodes), page()]);
  assert.equal(result.exitCode, 2);
  assert.equal(result.report.reason, 'UNRESOLVED_REVIEW_THREADS: 3');
  assert.equal(result.report.unresolvedCount, 3);
  assert.deepEqual(result.report.threads.map(({ url, author, path, isOutdated }) =>
    ({ url, author, path, isOutdated })), nodes.map((node) => ({
    url: node.comments.nodes[0].url, author: node.comments.nodes[0].author.login,
    path: node.path, isOutdated: node.isOutdated,
  })));
});

test('pagination reaches an outdated unresolved thread on page three', async () => {
  const result = await invoke([
    page([thread('1', { isResolved: true })], { totalCount: 3, hasNextPage: true, endCursor: 'cursor-1' }),
    page([thread('2', { isResolved: true })], { totalCount: 3, hasNextPage: true, endCursor: 'cursor-2' }),
    page([thread('3', { isOutdated: true })], { totalCount: 3 }),
    page(),
  ]);
  assert.equal(result.exitCode, 2);
  assert.equal(result.report.unresolvedCount, 1);
  assert.equal(result.report.threads.length, 3);
  assert.deepEqual(result.calls.map(({ variables }) => variables.after), [
    undefined, 'cursor-1', 'cursor-2', undefined,
  ]);
  for (const { query, variables } of result.calls) {
    assert.match(query, /^query\(/);
    assert.equal(variables.owner, 'OlyForge3D');
    assert.equal(variables.name, 'PrintFarmer');
    assert.equal(variables.number, 2794);
  }
});

const malformedCases = [
  ['missing response', undefined, 'MALFORMED_RESPONSE'],
  ['missing repository', { data: { repository: null } }, 'MALFORMED_RESPONSE'],
  ['missing PR', { data: { repository: { pullRequest: null } } }, 'MALFORMED_RESPONSE'],
  ['missing head', { data: { repository: { pullRequest: {} } } }, 'MALFORMED_RESPONSE'],
  ['invalid head', page([], { headSha: 'invalid' }), 'MALFORMED_RESPONSE'],
  ['missing connection', { data: { repository: { pullRequest: { headRefOid: head } } } }, 'MALFORMED_PAGE'],
  ['missing total', page(), 'MALFORMED_PAGE', (p) => { delete p.totalCount; }],
  ['invalid total', page(), 'MALFORMED_PAGE', (p) => { p.totalCount = -1; }],
  ['missing nodes', page(), 'MALFORMED_PAGE', (p) => { delete p.nodes; }],
  ['missing pageInfo', page(), 'MALFORMED_PAGE', (p) => { delete p.pageInfo; }],
  ['missing hasNextPage', page(), 'MALFORMED_PAGE', (p) => { p.pageInfo = {}; }],
  ['nonboolean next', page(), 'MALFORMED_PAGE', (p) => { p.pageInfo.hasNextPage = 'false'; }],
  ['truncated final page', page([], { totalCount: 1 }), 'INCOMPLETE_PAGE'],
  ['too many threads', page([thread()], { totalCount: 0 }), 'INCOMPLETE_PAGE'],
  ['empty continuation', page([], { totalCount: 2, hasNextPage: true, endCursor: 'next' }), 'INCOMPLETE_PAGE'],
  ['missing cursor', page([thread()], { totalCount: 2, hasNextPage: true }), 'INCOMPLETE_PAGE'],
  ['empty cursor', page([thread()], { totalCount: 2, hasNextPage: true, endCursor: '' }), 'INCOMPLETE_PAGE'],
  ['next after total', page([thread()], { hasNextPage: true, endCursor: 'next' }), 'INCOMPLETE_PAGE'],
  ['null thread', page([null]), 'MALFORMED_PAGE'],
  ['missing id', page([thread('', {})]), 'MALFORMED_PAGE'],
  ['missing resolution', page([thread('1', { isResolved: undefined })]), 'MALFORMED_PAGE'],
  ['invalid outdated flag', page([thread('1', { isOutdated: 'false' })]), 'MALFORMED_PAGE'],
  ['missing path', page([thread('1', { path: undefined })]), 'MALFORMED_PAGE'],
  ['invalid line', page([thread('1', { line: -1 })]), 'MALFORMED_PAGE'],
  ['missing comments', page([thread('1', { comments: undefined })]), 'MALFORMED_PAGE'],
  ['empty comments', page([thread('1', { comments: { nodes: [] } })]), 'MALFORMED_PAGE'],
  ['missing comment URL', page([thread('1', { comments: { nodes: [{ author: { login: 'x' } }] } })]), 'MALFORMED_PAGE'],
  ['missing author', page([thread('1', { comments: { nodes: [{ url: 'https://github.com/thread' }] } })]), 'MALFORMED_PAGE'],
];

for (const [name, response, classification, mutate] of malformedCases) {
  test(`${name} fails closed without an unresolved count`, async () => {
    if (mutate) mutate(response.data.repository.pullRequest.reviewThreads);
    const result = await invoke([response]);
    assert.equal(result.exitCode, 1);
    assert.equal(result.report.classification, classification);
    assert.ok(result.report.reason);
    assert.equal(result.report.unresolvedCount, undefined);
    assert.equal(result.report.threads, undefined);
  });
}

test('GraphQL errors including partial data and malformed errors fail closed', async () => {
  for (const errors of [[{ message: 'Resource not accessible' }], null, 'bad']) {
    const result = await invoke([{ ...page(), errors }]);
    assert.equal(result.exitCode, 1);
    assert.equal(result.report.classification, 'GRAPHQL_ERROR');
    assert.equal(result.report.unresolvedCount, undefined);
  }
});

test('HTTP 403, transport, JSON and later-page errors fail closed', async () => {
  for (const error of [new Error('HTTP 403: Forbidden'), new Error('ENOBUFS'),
    new SyntaxError('Unexpected end of JSON input')]) {
    for (const prefix of [[], [page([thread()], { totalCount: 2, hasNextPage: true, endCursor: 'next' })]]) {
      const result = await invoke([...prefix, error]);
      assert.equal(result.exitCode, 1);
      assert.equal(result.report.classification, 'API_ERROR');
      assert.match(result.report.reason, new RegExp(error.message));
      assert.equal(result.report.unresolvedCount, undefined);
    }
  }
});

test('duplicate IDs, repeated cursors and changing totals fail closed', async () => {
  const first = page([thread('1')], { totalCount: 4, hasNextPage: true, endCursor: 'same' });
  for (const second of [
    page([thread('1')], { totalCount: 4 }),
    page([thread('2')], { totalCount: 4, hasNextPage: true, endCursor: 'same' }),
    page([thread('2')], { totalCount: 5 }),
  ]) {
    const result = await invoke([first, second]);
    assert.equal(result.exitCode, 1);
    assert.equal(result.report.classification, 'INCOMPLETE_PAGE');
    assert.equal(result.report.unresolvedCount, undefined);
  }
});

test('head mismatch on first, subsequent or final request has distinct exit 3', async () => {
  for (const responses of [
    [page([], { headSha: movedHead })],
    [page([thread()], { totalCount: 2, hasNextPage: true, endCursor: 'next' }), page([], { headSha: movedHead })],
    [page(), page([], { headSha: movedHead })],
  ]) {
    const result = await invoke(responses);
    assert.equal(result.exitCode, 3);
    assert.equal(result.report.classification, 'HEAD_MISMATCH');
    assert.match(result.report.reason, new RegExp(`${head}.*${movedHead}`));
    assert.equal(result.report.unresolvedCount, undefined);
  }
});

test('without expected-head, report binds to initial head and still detects movement', async () => {
  const args = ['--repo', 'OlyForge3D/PrintFarmer', '--pr', '2794', '--json'];
  assert.equal((await invoke([page(), page()], args)).exitCode, 0);
  assert.equal((await invoke([page(), page([], { headSha: movedHead })], args)).exitCode, 3);
});

test('deleted authors are explicit and human-readable output includes outdated URL', async () => {
  const node = thread('deleted', { isOutdated: true, comments: { nodes: [{
    url: 'https://github.com/OlyForge3D/PrintFarmer/pull/2794#discussion_deleted', author: null,
  }] } });
  const result = await invoke([page([node]), page()], cliArgs.filter((arg) => arg !== '--json'));
  assert.equal(result.exitCode, 2);
  assert.match(result.output, /UNRESOLVED_REVIEW_THREADS: 1/);
  assert.match(result.output, /https:\/\/.*#discussion_deleted author=\[deleted\].*outdated=true/);
});

test('invalid CLI arguments fail before any API call', async () => {
  for (const args of [
    [], ['--repo', '--json'], ['--repo', 'owner/name', '--pr', '0'],
    ['--repo', 'owner/name', '--pr', '9007199254740992'],
    [...cliArgs, '--expected-head', movedHead], [...cliArgs, '--unknown'],
    ['--repo', 'bad', '--pr', '1'], [...cliArgs.slice(0, 4), '--expected-head', 'bad'],
  ]) {
    const result = await invoke([], [...args, '--json']);
    assert.equal(result.exitCode, 1);
    assert.equal(result.calls.length, 0);
    assert.equal(result.report.classification, 'VERIFIER_ERROR');
  }
});

test('real executable returns nonzero and valid JSON for bad arguments', () => {
  const result = spawnSync(process.execPath, [
    fileURLToPath(new URL('../verify-pr-review-threads.mjs', import.meta.url)),
    '--json',
  ], { encoding: 'utf8' });
  assert.equal(result.status, 1, result.stderr);
  assert.equal(JSON.parse(result.stdout).classification, 'VERIFIER_ERROR');
});

test('unknown classifications cannot pass', () => {
  assert.equal(exitCodeFor('new-unknown'), 1);
});

test('required CI tooling job explicitly runs this regression suite', () => {
  const workflow = readFileSync(new URL('../../../.github/workflows/ci.yml', import.meta.url), 'utf8');
  const tooling = workflow.split('  ci-tools:')[1]?.split('\n  dependency-compliance:')[0];
  assert.match(tooling, /node --test scripts\/ci\/tests\/test-verify-pr-review-threads\.mjs/);
});
