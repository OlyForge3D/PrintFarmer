import test from 'node:test';
import assert from 'node:assert/strict';
import { repository } from '../release-policy.mjs';
import { evidenceCollection, readEvidencePages } from '../github-evidence-pages.mjs';
import { qualificationClient, qualificationRequestUrl } from '../canonical-qualification.mjs';
import { githubClient, githubRequestUrl, verifyReleaseChecks } from '../release-github.mjs';
import { readOnlyClient } from '../release-rehearsal.mjs';

const sha = 'a'.repeat(40);
const root = `https://api.github.com/repos/${repository}/`;
const checks = `commits/${sha}/check-runs?per_page=100`;
const routes = [checks, `commits/${sha}/status?per_page=100`,
  `commits/${sha}/statuses?per_page=100`, `commits/${sha}/comments?per_page=100`,
  'pulls/60/reviews?per_page=100', 'actions/runs/10/attempts/1/jobs?per_page=100',
  `actions/workflows/ci.yml/runs?head_sha=${sha}&per_page=100`,
  'actions/workflows/qualify-canonical-release.yml/runs?created=%3E%3D2026-09-13T05%3A00%3A00Z&per_page=100'];

function pages(endpoint = checks, length = 239) {
  const field = evidenceCollection(endpoint);
  const calls = [];
  const request = async path => {
    calls.push(path);
    const page = Number(new URL(`${root}${path}`).searchParams.get('page') ?? 1);
    const items = Array.from({ length: Math.max(0, Math.min(100, length - (page - 1) * 100)) },
      (_, index) => ({ id: (page - 1) * 100 + index + 1 }));
    const data = field ? { total_count: length, [field]: items,
      ...(field === 'statuses' ? { sha } : {}) } : items;
    const last = Math.ceil(length / 100);
    const link = page < last ?
      `<${root}${endpoint}&page=${page + 1}>; rel="next", <${root}${endpoint}&page=${last}>; rel="last"` :
      page > 1 && page <= last ? `<${root}${endpoint}&page=${page - 1}>; rel="prev"` : '';
    return { data, link };
  };
  return { request, calls };
}

for (const endpoint of routes) {
  for (const length of [0, 99, 100, 101, 139, 200, 239]) {
    test(`complete ${length} entries: ${endpoint}`, async () => {
      const f = pages(endpoint, length);
      const result = await readEvidencePages(endpoint, f.request);
      const field = evidenceCollection(endpoint);
      assert.equal((field ? result[field] : result).length, length);
      assert.equal(f.calls.length, Math.max(1, Math.ceil(length / 100)) +
        (!field && length > 0 && length % 100 === 0 ? 1 : 0));
      assert.ok(f.calls.every(path => path.startsWith(endpoint)));
    });
  }
}

for (const [name, mutate] of [
  ['missing next link', result => { result.link = ''; }],
  ['short first page', result => { result.data.check_runs.pop(); }],
  ['oversized page', result => { result.data.check_runs.push({ id: 900 }); }],
  ['missing total', result => { delete result.data.total_count; }],
  ['string total', result => { result.data.total_count = '239'; }],
  ['negative total', result => { result.data.total_count = -1; }],
  ['over budget', result => { result.data.total_count = 10001; }],
  ['invalid ID', result => { result.data.check_runs[0].id = '1'; }],
  ['missing ID', result => { delete result.data.check_runs[0].id; }],
  ['duplicate ID', result => { result.data.check_runs[1].id = 1; }],
  ['missing array', result => { delete result.data.check_runs; }],
]) {
  test(`reject ${name}`, async () => {
    const f = pages();
    await assert.rejects(readEvidencePages(checks, async path => {
      const result = await f.request(path);
      mutate(result);
      return result;
    }));
    assert.equal(f.calls.length, 1);
  });
}

for (const endpoint of routes) {
  test(`reject repeated second page IDs: ${endpoint}`, async () => {
    const f = pages(endpoint);
    const field = evidenceCollection(endpoint);
    await assert.rejects(readEvidencePages(endpoint, async path => {
      const result = await f.request(path);
      if (f.calls.length === 2) (field ? result.data[field] : result.data)[0].id = 1;
      return result;
    }), /Duplicate/);
  });
  test(`reject missing second page: ${endpoint}`, async () => {
    const f = pages(endpoint);
    await assert.rejects(readEvidencePages(endpoint, async path => {
      if (path.includes('&page=2')) throw new Error('HTTP 404');
      return f.request(path);
    }), /HTTP 404/);
  });
  test(`reject truncated second page advertising continuation: ${endpoint}`, async () => {
    const f = pages(endpoint);
    const field = evidenceCollection(endpoint);
    await assert.rejects(readEvidencePages(endpoint, async path => {
      const result = await f.request(path);
      if (f.calls.length === 2) (field ? result.data[field] : result.data).pop();
      return result;
    }), /[Tt]runcated/);
  });
}

for (const link of [
  '<https://evil.example/steal>; rel="next"',
  `<http://api.github.com/repos/${repository}/${checks}&page=2>; rel="next"`,
  `<https://api.github.com.evil.example/repos/${repository}/${checks}&page=2>; rel="next"`,
  `<https://user@api.github.com/repos/${repository}/${checks}&page=2>; rel="next"`,
  `<${root}${checks}&page=2#fragment>; rel="next"`,
  `<${root}${checks.replace(sha, 'b'.repeat(40))}&page=2>; rel="next"`,
  `<${root}${checks}&page=3>; rel="next"`,
  `<${root}${checks}&page=1>; rel="next"`,
  `<${root}${checks}&page=02>; rel="next"`,
  `<${root}${checks}&page=2&page=2>; rel="next"`,
  `<${root}${checks}&page=2&per_page=100>; rel="next"`,
  `<${root}${checks}&page=2&filter=all>; rel="next"`,
  `<${root}${checks}&page=2>; rel="next", <${root}${checks}&page=2>; rel="next"`,
  `<${root}${checks}&page=2>; rel="next", <https://evil.example>; rel="last"`,
  `<${root}${checks}&page=2>; rel="last"`,
  `<${root}${checks}&page=101>; rel="last"`,
  `<${root}${checks}&page=2>; rel="unknown"`,
  '<not-a-url>; rel="next"',
  'not a link',
]) {
  test(`reject hostile or malformed link ${link}`, async () => {
    const f = pages();
    await assert.rejects(readEvidencePages(checks, async path => ({ ...await f.request(path), link })));
    assert.equal(f.calls.length, 1, 'never request link targets');
  });
}

test('accept GitHub query ordering without using the link as a request target', async () => {
  const f = pages(checks, 139);
  await readEvidencePages(checks, async path => {
    const result = await f.request(path);
    if (f.calls.length === 1) result.link = `<${root}commits/${sha}/check-runs?page=2&per_page=100>; rel="next"`;
    return result;
  });
  assert.equal(f.calls[1], `${checks}&page=2`);
});

test('reject changed totals and combined status SHA on later pages', async () => {
  for (const property of ['total_count', 'sha']) {
    const endpoint = `commits/${sha}/status?per_page=100`;
    const f = pages(endpoint);
    await assert.rejects(readEvidencePages(endpoint, async path => {
      const result = await f.request(path);
      if (f.calls.length === 2) result.data[property] = property === 'sha' ? 'b'.repeat(40) : 238;
      return result;
    }), /total|SHA/);
  }
});

test('reject surplus continuation after declared total and disappearing last pages', async () => {
  for (const endpoint of [checks, `commits/${sha}/comments?per_page=100`]) {
    const f = pages(endpoint, 139);
    await assert.rejects(readEvidencePages(endpoint, async path => {
      const result = await f.request(path);
      if (f.calls.length === 2) result.link = `<${root}${endpoint}&page=3>; rel="next"`;
      return result;
    }), /surplus/);
  }
  const endpoint = `commits/${sha}/comments?per_page=100`;
  const f = pages(endpoint);
  await assert.rejects(readEvidencePages(endpoint, async path => {
    const result = await f.request(path);
    if (f.calls.length === 2) { result.data = []; result.link = ''; }
    return result;
  }), /terminal/);
});

test('array without terminal evidence exhausts bounded page budget', async () => {
  const endpoint = `commits/${sha}/statuses?per_page=100`;
  const f = pages(endpoint, 10000);
  await assert.rejects(readEvidencePages(endpoint, f.request), /budget/);
  assert.equal(f.calls.length, 100);
});

for (const [name, client] of [
  ['qualification', fetcher => qualificationClient('test-only', false, fetcher)],
  ['release', fetcher => githubClient('test-only', fetcher)],
  ['rehearsal read-only', fetcher => readOnlyClient('test-only', fetcher)],
]) {
  test(`${name} transport collects 239 checks with exact source routes`, async () => {
    const f = pages();
    const api = client(async (url, options) => {
      assert.equal(options.redirect, 'error');
      assert.equal(options.method, 'GET');
      assert.ok(url.startsWith(root));
      const result = await f.request(url.slice(root.length));
      return new Response(JSON.stringify(result.data), { headers: { link: result.link } });
    });
    assert.equal((await api(checks)).check_runs.length, 239);
    assert.deepEqual(f.calls, [checks, `${checks}&page=2`, `${checks}&page=3`]);
  });
  for (const failure of ['403', '429', '500', 'redirect', 'malformed', 'hostile link']) {
    test(`${name} transport rejects later-page ${failure}`, async () => {
      const f = pages();
      const api = client(async url => {
        const result = await f.request(url.slice(root.length));
        if (f.calls.length === 2) {
          if (/^\d+$/.test(failure)) return new Response('', { status: Number(failure) });
          if (failure === 'redirect') return new Response('', { status: 302, headers: { location: 'https://evil.example' } });
          if (failure === 'malformed') return new Response('{');
          result.link = '<https://evil.example>; rel="next"';
        }
        return new Response(JSON.stringify(result.data), { headers: { link: result.link } });
      });
      await assert.rejects(api(checks));
      assert.equal(f.calls.length, 2);
    });
  }
}

test('page allowlists preserve exact source, attempt, method and collection constraints', () => {
  for (const endpoint of routes.filter(endpoint => !endpoint.includes('/status?'))) {
    assert.equal(qualificationRequestUrl(`${endpoint}&page=2`), `${root}${endpoint}&page=2`);
  }
  assert.equal(githubRequestUrl(`${checks}&page=2`, 'GET'), `${root}${checks}&page=2`);
  for (const endpoint of [`actions/runs/10/attempts/2/jobs?per_page=100&page=2`,
    `${checks}&page=0`, `${checks}&page=-1`, `${checks}&page=02`, `${checks}&page=2&page=3`,
    'rules/branches/main?per_page=100&page=2']) {
    assert.throws(() => qualificationRequestUrl(endpoint));
  }
  assert.throws(() => qualificationRequestUrl(`${checks}&page=2`, 'POST'));
  assert.throws(() => githubRequestUrl(`${checks}&page=2`, 'PATCH'));
});

for (const failed of [false, true]) {
  test(`release consumer reconciles required checks and statuses beyond page one: failed=${failed}`, async () => {
    const statuses = `commits/${sha}/status?per_page=100`;
    const collections = new Map([checks, statuses].map(endpoint => [endpoint, pages(endpoint, 139)]));
    const api = githubClient('test-only', async url => {
      const path = url.slice(root.length);
      const endpoint = path.replace(/&page=\d+$/, '');
      const result = await collections.get(endpoint).request(path);
      const field = evidenceCollection(endpoint);
      for (const entry of result.data[field]) {
        entry.name = entry.context = entry.id === 139 ? 'required' : `other-${entry.id}`;
        entry.head_sha = sha;
        entry.status = 'completed';
        entry.conclusion = 'success';
        entry.state = failed && entry.id === 139 ? 'failure' : 'success';
      }
      return new Response(JSON.stringify(result.data), { headers: { link: result.link } });
    });
    const result = verifyReleaseChecks(api, sha, [{ context: 'required' }]);
    if (failed) await assert.rejects(result, /qualification/);
    else await result;
    assert.ok([...collections.values()].every(f => f.calls.length === 2));
  });
}
