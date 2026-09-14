import test from 'node:test';
import assert from 'node:assert/strict';
import { repository } from '../release-policy.mjs';
import { evidenceBaseEndpoint, evidenceCollection, readEvidencePages } from '../github-evidence-pages.mjs';
import { qualificationClient, qualificationRequestUrl } from '../canonical-qualification.mjs';
import { githubClient, githubRequestUrl, verifyReleaseChecks } from '../release-github.mjs';

const sha = 'a'.repeat(40);
const root = `https://api.github.com/repos/${repository}/`;
const numericRoot = 'https://api.github.com/repositories/1044049720/';
const checks = `commits/${sha}/check-runs?per_page=100`;
const routes = [checks, `commits/${sha}/status?per_page=100`,
  `commits/${sha}/statuses?per_page=100`, `commits/${sha}/comments?per_page=100`,
  'pulls/60/reviews?per_page=100', 'actions/runs/10/attempts/1/jobs?per_page=100',
  'actions/runs/10/attempts/17/jobs?per_page=100',
  `actions/workflows/ci.yml/runs?head_sha=${sha}&per_page=100`,
  'actions/workflows/qualify-canonical-release.yml/runs?created=%3E%3D2026-09-13T05%3A00%3A00Z&per_page=100'];

function pages(endpoint = checks, length = 239, linkRoot = root) {
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
    const relations = [];
    if (page > 1) relations.push(['prev', page - 1]);
    if (page < last) relations.push(['next', page + 1], ['last', last]);
    if (page > 1) relations.push(['first', 1]);
    if (page > last && last > 0) relations.push(['last', last]);
    const link = relations.map(([rel, target]) =>
      `<${linkRoot}${endpoint}&page=${target}>; rel="${rel}"`).join(', ');
    return { data, link };
  };
  return { request, calls };
}

for (const endpoint of routes) {
  for (const linkRoot of [root, numericRoot]) {
    for (const length of [0, 99, 100, 101, 139, 141, 200, 239]) {
      test(`complete ${length} entries (${linkRoot}): ${endpoint}`, async () => {
        const f = pages(endpoint, length, linkRoot);
        const result = await readEvidencePages(endpoint, f.request);
        const field = evidenceCollection(endpoint);
        assert.equal((field ? result[field] : result).length, length);
        const expectedPages = Math.max(1, Math.ceil(length / 100)) +
          (!field && length > 0 && length % 100 === 0 ? 1 : 0);
        assert.deepEqual(f.calls, Array.from({ length: expectedPages },
          (_, index) => index === 0 ? endpoint : `${endpoint}&page=${index + 1}`));
      });
    }
  }
}

for (const endpoint of routes) {
  for (const [name, mutate] of [
    ['foreign repository ID', link => link.replaceAll('1044049720', '1044049721')],
    ['prefixed repository ID', link => link.replaceAll('1044049720', '01044049720')],
    ['suffixed repository ID', link => link.replaceAll('1044049720', '10440497200')],
    ['foreign repository name', link => link.replaceAll(numericRoot, 'https://api.github.com/repos/outsider/PrintFarmer/')],
    ['foreign source or collection', link => link.replaceAll(endpoint, checks === endpoint ?
      checks.replace(sha, 'b'.repeat(40)) : checks)],
    ['changed query', link => link.replaceAll('per_page=100', 'per_page=99')],
    ['changed query filters', link => link.replaceAll('&page=', '&filter=all&page=')],
    ['foreign host', link => link.replaceAll('api.github.com', 'evil.example')],
  ]) {
    test(`numeric links reject ${name}: ${endpoint}`, async () => {
      const f = pages(endpoint, 141, numericRoot);
      await assert.rejects(readEvidencePages(endpoint, async path => {
        const result = await f.request(path);
        result.link = mutate(result.link);
        return result;
      }), /Untrusted evidence pagination/);
      assert.deepEqual(f.calls, [endpoint], 'never request link targets');
    });
  }
}

for (const endpoint of routes.filter(endpoint => evidenceCollection(endpoint) === '')) {
  for (const length of [100, 200]) {
    for (const [name, mutate] of [
      ['nonempty probe', result => { result.data = [{ id: 999 }]; }],
      ['empty probe with next', result => {
        result.link += `, <${numericRoot}${endpoint}&page=${length / 100 + 2}>; rel="next"`;
      }],
      ['last two pages behind', result => {
        result.link = result.link.replace(/page=\d+>; rel="last"/, `page=${length / 100 - 1}>; rel="last"`);
      }],
      ['last ahead of terminal page', result => {
        result.link = result.link.replace(/page=\d+>; rel="last"/, `page=${length / 100 + 2}>; rel="last"`);
      }],
      ['foreign last repository', result => {
        result.link = result.link.replace(/1044049720([^>]+)>; rel="last"/, '1044049721$1>; rel="last"');
      }],
      ['wrong previous page', result => {
        result.link = result.link.replace(/page=\d+>; rel="prev"/, `page=${length / 100 + 1}>; rel="prev"`);
      }],
      ['wrong first page', result => {
        result.link = result.link.replace('page=1>; rel="first"', 'page=2>; rel="first"');
      }],
    ]) {
      test(`reject ${length}-entry ${name}: ${endpoint}`, async () => {
        const f = pages(endpoint, length, numericRoot);
        await assert.rejects(readEvidencePages(endpoint, async path => {
          const result = await f.request(path);
          if (f.calls.length === length / 100 + 1) mutate(result);
          return result;
        }), /evidence/);
        assert.equal(f.calls.length, length / 100 + 1);
      });
    }
  }
}

test('counted pages never accept a previous-page last link', async () => {
  for (const endpoint of routes.filter(endpoint => evidenceCollection(endpoint))) {
    const f = pages(endpoint, 141, numericRoot);
    await assert.rejects(readEvidencePages(endpoint, async path => {
      const result = await f.request(path);
      if (f.calls.length === 2) result.link += `, <${numericRoot}${endpoint}&page=1>; rel="last"`;
      return result;
    }), /Skipped or repeated/);
  }
});

test('non-string endpoints fail closed without coercion or requests', async () => {
  for (const endpoint of [undefined, null, 10, {}, { toString: () => checks }]) {
    assert.equal(evidenceCollection(endpoint), undefined);
    assert.throws(() => evidenceBaseEndpoint(endpoint), /Invalid evidence endpoint/);
    await assert.rejects(readEvidencePages(endpoint, () => assert.fail('must not request')), /Unsupported/);
  }
});

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
    if (f.calls.length === 1) result.link = `<${numericRoot}commits/${sha}/check-runs?page=2&per_page=100>; rel="next"`;
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
]) {
  test(`${name} transport collects 141 checks with numeric links and exact source routes`, async () => {
    const f = pages(checks, 141, numericRoot);
    const api = client(async (url, options) => {
      assert.equal(options.redirect, 'error');
      assert.equal(options.method, 'GET');
      assert.ok(url.startsWith(root));
      const result = await f.request(url.slice(root.length));
      return new Response(JSON.stringify(result.data), { headers: { link: result.link } });
    });
    assert.equal((await api(checks)).check_runs.length, 141);
    assert.deepEqual(f.calls, [checks, `${checks}&page=2`]);
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
  for (const endpoint of [`actions/runs/10/attempts/0/jobs?per_page=100&page=2`,
    `actions/runs/10/attempts/01/jobs?per_page=100&page=2`,
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
    const evidenceAt = new Date().toISOString();
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
        entry.completed_at = evidenceAt;
        entry.created_at = evidenceAt;
        entry.updated_at = evidenceAt;
      }
      return new Response(JSON.stringify(result.data), { headers: { link: result.link } });
    });
    const result = verifyReleaseChecks(api, sha, [{ context: 'required' }]);
    if (failed) await assert.rejects(result, /qualification/);
    else await result;
    assert.ok([...collections.values()].every(f => f.calls.length === 2));
  });
}
