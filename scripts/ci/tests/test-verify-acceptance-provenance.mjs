import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createServer } from 'node:http';
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { promisify } from 'node:util';
import {
  parseArguments,
  verifyAcceptanceProvenance,
} from '../verify-acceptance-provenance.mjs';

const expectedSha = 'a'.repeat(40);
const buildTime = '2026-09-07T12:00:00.000Z';
const verifiedAt = '2026-09-07T12:05:00.000Z';
const execFileAsync = promisify(execFile);
const verifierPath = path.resolve('scripts/ci/verify-acceptance-provenance.mjs');

async function startFixtureServer({
  frontendCommit = expectedSha,
  apiCommit = expectedSha,
  frontendBody,
  frontendRaw,
  frontendStatus = 200,
  apiBody,
  apiStatus = 200,
  indexHtml = '<script type="module" src="/assets/index-test123.js"></script>',
  bundleStatus = 200,
} = {}) {
  const server = createServer((request, response) => {
    if (request.url === '/') {
      response.writeHead(200, { 'content-type': 'text/html' });
      response.end(indexHtml);
      return;
    }
    if (request.url === '/assets/index-test123.js') {
      response.writeHead(bundleStatus, { 'content-type': 'application/javascript' });
      response.end(bundleStatus === 200 ? 'console.log("fixture");' : 'not found');
      return;
    }
    if (request.url === '/version.json') {
      if (frontendRaw !== undefined) {
        response.writeHead(frontendStatus, { 'content-type': 'application/json' });
        response.end(frontendRaw);
        return;
      }
      response.writeHead(frontendStatus, { 'content-type': 'application/json' });
      response.end(JSON.stringify(frontendBody ?? {
        service: 'frontend',
        commit: frontendCommit,
        buildTime,
      }));
      return;
    }
    if (request.url === '/api/system/version') {
      response.writeHead(apiStatus, { 'content-type': 'application/json' });
      response.end(JSON.stringify(apiBody ?? {
        service: 'Farm.Web.Api',
        version: '0.2.3',
        commit: apiCommit,
        environment: 'Acceptance',
        runtime: '.NET 10.0.0',
        timestamp: buildTime,
      }));
      return;
    }
    response.writeHead(404);
    response.end();
  });

  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const address = server.address();
  if (!address || typeof address === 'string') {
    throw new Error('Fixture server did not bind to a TCP port.');
  }
  return {
    baseUrl: `http://127.0.0.1:${address.port}`,
    close: () => new Promise((resolve, reject) => {
      server.close((error) => error ? reject(error) : resolve());
    }),
  };
}

async function withEvidenceDirectory(action) {
  const directory = await mkdtemp(path.join(tmpdir(), 'acceptance-provenance-'));
  try {
    return await action(directory);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

test('writes evidence when frontend and API commits match the expected SHA', async () => {
  const fixture = await startFixtureServer();
  try {
    await withEvidenceDirectory(async (evidenceDir) => {
      const result = await verifyAcceptanceProvenance({
        expectedSha,
        baseUrl: fixture.baseUrl,
        evidenceDir,
        imageDigests: {
          frontend: `sha256:${'b'.repeat(64)}`,
        },
        now: () => new Date(verifiedAt),
      });

      assert.equal(result.evidence.frontend.commit, expectedSha);
      assert.equal(result.evidence.api.commit, expectedSha);
      assert.equal(result.evidence.frontend.buildTime, buildTime);
      assert.equal(
        result.evidence.frontend.bundleUrl,
        `${fixture.baseUrl}/assets/index-test123.js`,
      );
      assert.equal(result.evidence.api.environment, 'Acceptance');
      assert.deepEqual(result.evidence.imageDigests, {
        frontend: `sha256:${'b'.repeat(64)}`,
      });

      const written = JSON.parse(await readFile(result.evidencePath, 'utf8'));
      assert.deepEqual(written, result.evidence);
    });
  } finally {
    await fixture.close();
  }
});

test('fails closed and writes no evidence when the API commit mismatches', async () => {
  const fixture = await startFixtureServer({ apiCommit: 'b'.repeat(40) });
  try {
    await withEvidenceDirectory(async (evidenceDir) => {
      await assert.rejects(
        verifyAcceptanceProvenance({
          expectedSha,
          baseUrl: fixture.baseUrl,
          evidenceDir,
        }),
        /API commit .* does not match expected SHA/,
      );
      assert.deepEqual(await readdir(evidenceDir), []);
    });
  } finally {
    await fixture.close();
  }
});

test('fails closed and writes no evidence when the frontend commit mismatches', async () => {
  const fixture = await startFixtureServer({ frontendCommit: 'b'.repeat(40) });
  try {
    await withEvidenceDirectory(async (evidenceDir) => {
      await assert.rejects(
        verifyAcceptanceProvenance({
          expectedSha,
          baseUrl: fixture.baseUrl,
          evidenceDir,
        }),
        /Frontend commit .* does not match expected SHA/,
      );
      assert.deepEqual(await readdir(evidenceDir), []);
    });
  } finally {
    await fixture.close();
  }
});

test('fails closed when an endpoint is unreachable', async () => {
  const fixture = await startFixtureServer();
  await fixture.close();

  await withEvidenceDirectory(async (evidenceDir) => {
    await assert.rejects(
      verifyAcceptanceProvenance({
        expectedSha,
        baseUrl: fixture.baseUrl,
        evidenceDir,
        timeoutMs: 500,
      }),
      /request failed/,
    );
    assert.deepEqual(await readdir(evidenceDir), []);
  });
});

test('fails closed when either endpoint reports a reserved non-deployable commit', async () => {
  for (const invalidCommit of ['unknown', 'dev']) {
    for (const fixtureOptions of [
      { frontendCommit: invalidCommit },
      { apiCommit: invalidCommit },
    ]) {
      const fixture = await startFixtureServer(fixtureOptions);
      try {
        await withEvidenceDirectory(async (evidenceDir) => {
          await assert.rejects(
            verifyAcceptanceProvenance({
              expectedSha,
              baseUrl: fixture.baseUrl,
              evidenceDir,
            }),
            new RegExp(`non-deployable commit '${invalidCommit}'`),
          );
          assert.deepEqual(await readdir(evidenceDir), []);
        });
      } finally {
        await fixture.close();
      }
    }
  }
});

test('fails closed when either endpoint reports a short commit', async () => {
  for (const fixtureOptions of [
    { frontendCommit: 'abc1234' },
    { apiCommit: 'abc1234' },
  ]) {
    const fixture = await startFixtureServer(fixtureOptions);
    try {
      await withEvidenceDirectory(async (evidenceDir) => {
        await assert.rejects(
          verifyAcceptanceProvenance({
            expectedSha,
            baseUrl: fixture.baseUrl,
            evidenceDir,
          }),
          /commit must be a full 40-character hexadecimal SHA/,
        );
        assert.deepEqual(await readdir(evidenceDir), []);
      });
    } finally {
      await fixture.close();
    }
  }
});

test('fails closed when a version endpoint returns a non-success status', async () => {
  const fixture = await startFixtureServer({ frontendStatus: 503 });
  try {
    await withEvidenceDirectory(async (evidenceDir) => {
      await assert.rejects(
        verifyAcceptanceProvenance({
          expectedSha,
          baseUrl: fixture.baseUrl,
          evidenceDir,
        }),
        /frontend version endpoint request failed .* HTTP 503/,
      );
      assert.deepEqual(await readdir(evidenceDir), []);
    });
  } finally {
    await fixture.close();
  }
});

test('fails closed when the index omits or cross-originates the frontend bundle', async () => {
  for (const indexHtml of [
    '<html><body>No production entry</body></html>',
    '<script type="module" src="https://cdn.example/assets/index-external.js"></script>',
  ]) {
    const fixture = await startFixtureServer({ indexHtml });
    try {
      await withEvidenceDirectory(async (evidenceDir) => {
        await assert.rejects(
          verifyAcceptanceProvenance({
            expectedSha,
            baseUrl: fixture.baseUrl,
            evidenceDir,
          }),
          /does not reference an index-\*\.js bundle|bundle on another origin/,
        );
        assert.deepEqual(await readdir(evidenceDir), []);
      });
    } finally {
      await fixture.close();
    }
  }
});

test('fails closed when the resolved frontend bundle cannot be fetched', async () => {
  const fixture = await startFixtureServer({ bundleStatus: 404 });
  try {
    await withEvidenceDirectory(async (evidenceDir) => {
      await assert.rejects(
        verifyAcceptanceProvenance({
          expectedSha,
          baseUrl: fixture.baseUrl,
          evidenceDir,
        }),
        /frontend bundle request failed .* HTTP 404/,
      );
      assert.deepEqual(await readdir(evidenceDir), []);
    });
  } finally {
    await fixture.close();
  }
});

test('fails closed on a malformed version payload', async () => {
  const fixture = await startFixtureServer({ frontendBody: { commit: expectedSha } });
  try {
    await withEvidenceDirectory(async (evidenceDir) => {
      await assert.rejects(
        verifyAcceptanceProvenance({
          expectedSha,
          baseUrl: fixture.baseUrl,
          evidenceDir,
        }),
        /missing or invalid 'buildTime'/,
      );
      assert.deepEqual(await readdir(evidenceDir), []);
    });
  } finally {
    await fixture.close();
  }
});

test('fails closed when a version endpoint returns malformed JSON', async () => {
  const fixture = await startFixtureServer({ frontendRaw: '{"commit":' });
  try {
    await withEvidenceDirectory(async (evidenceDir) => {
      await assert.rejects(
        verifyAcceptanceProvenance({
          expectedSha,
          baseUrl: fixture.baseUrl,
          evidenceDir,
        }),
        /frontend version endpoint returned malformed JSON/,
      );
      assert.deepEqual(await readdir(evidenceDir), []);
    });
  } finally {
    await fixture.close();
  }
});

test('parses required CLI arguments and repeated image digests', () => {
  assert.deepEqual(
    parseArguments([
      '--expected-sha',
      expectedSha,
      '--base-url',
      'https://acceptance.example',
      '--evidence-dir',
      'acceptance-evidence',
      '--image-digest',
      `frontend=sha256:${'b'.repeat(64)}`,
      '--image-digest',
      `api=registry.example/api@sha256:${'c'.repeat(64)}`,
    ]),
    {
      expectedSha,
      baseUrl: 'https://acceptance.example',
      evidenceDir: 'acceptance-evidence',
      imageDigests: {
        frontend: `sha256:${'b'.repeat(64)}`,
        api: `registry.example/api@sha256:${'c'.repeat(64)}`,
      },
    },
  );
});

test('CLI exits non-zero and writes no evidence when verification fails', async () => {
  const fixture = await startFixtureServer({ frontendCommit: 'b'.repeat(40) });
  try {
    await withEvidenceDirectory(async (evidenceDir) => {
      await assert.rejects(
        execFileAsync(process.execPath, [
          verifierPath,
          '--expected-sha',
          expectedSha,
          '--base-url',
          fixture.baseUrl,
          '--evidence-dir',
          evidenceDir,
        ]),
        (error) => {
          assert.equal(error.code, 1);
          assert.match(error.stderr, /Frontend commit .* does not match expected SHA/);
          return true;
        },
      );
      assert.deepEqual(await readdir(evidenceDir), []);
    });
  } finally {
    await fixture.close();
  }
});
