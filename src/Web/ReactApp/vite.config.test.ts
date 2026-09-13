import { afterEach, describe, expect, it } from 'vitest';
import { execFileSync } from 'node:child_process';
import { copyFileSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { runInNewContext } from 'node:vm';
import { buildMetadata } from '../../../scripts/ci/release-metadata.mjs';
import { allocationKey } from '../../../scripts/ci/release-policy.mjs';
import { frontendVersionMetadata, resolveGitHash } from './vite.config';
import { identity as inventoryIdentity } from './src/test/features/system/serviceInventoryFixture';

const originalViteGitSha = process.env.VITE_GIT_SHA;
const originalGitSha = process.env.GIT_SHA;

function restoreEnvironment() {
  if (originalViteGitSha === undefined) {
    delete process.env.VITE_GIT_SHA;
  } else {
    process.env.VITE_GIT_SHA = originalViteGitSha;
  }
  if (originalGitSha === undefined) {
    delete process.env.GIT_SHA;
  } else {
    process.env.GIT_SHA = originalGitSha;
  }
}

afterEach(restoreEnvironment);

describe('canonical frontend release identity', () => {
  it.each(['stable', 'insider'])('embeds the production consumer output for %s releases', (channel) => {
    const root = resolve('.artifacts', `production-identity-${channel}-${process.pid}`);
    const branch = channel === 'stable' ? 'main' : 'development';
    const version = channel === 'stable' ? '1.2.3' : '1.2.3-insider.10';
    const record = {
      repository: 'OlyForge3D/PrintFarmer', releaseId: `${channel}:${version}`,
      channel, canonicalVersion: version, baseVersion: '1.2.3',
      sourceBranch: branch, sourceTag: `v${version}`, sourceCommit: 'a'.repeat(40),
      authorizedBranchHead: 'a'.repeat(40), buildId: '45', buildAttempt: '2',
      workflowIdentity: `OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/${branch}`,
      created: '2026-09-12T20:00:00.000Z',
    };
    const allocation = allocationKey(record);
    const metadata = buildMetadata({ ...record, allocationKey: allocation,
      protection: { reviewerId: 'private-reviewer' }, futurePrivate: 'private-value' });
    const expectedIdentity = { ...JSON.parse(metadata.frontendIdentity), promotionOrigin: null };
    const vite = resolve('node_modules/vite/bin/vite.js');
    const build = () => execFileSync(process.execPath, [vite, 'build', '--config', resolve(root, 'vite.config.ts')], {
      cwd: root, encoding: 'utf8', timeout: 60_000, stdio: 'pipe',
      env: { ...process.env, VITE_GIT_SHA: record.sourceCommit,
        PRINTFARMER_RELEASE_IDENTITY: metadata.frontendIdentity },
    });
    mkdirSync(resolve(root, 'public'), { recursive: true });
    mkdirSync(resolve(root, 'src/common/utils'), { recursive: true });
    try {
      writeFileSync(resolve(root, 'index.html'),
        '<!doctype html><title>Production identity</title><script type="module" src="/main.js"></script>');
      writeFileSync(resolve(root, 'main.js'), 'globalThis.releaseIdentity = __RELEASE_IDENTITY__;');
      writeFileSync(resolve(root, 'public/sw.js'), '// __PRINTFARMER_BUILD_TIME__ __PRINTFARMER_GIT_HASH__');
      writeFileSync(resolve(root, 'public/release-identity.json'), metadata.frontend);
      for (const file of ['vite.config.ts', 'public-release-identity.mjs', 'src/common/utils/releaseIdentity.ts']) {
        copyFileSync(resolve(file), resolve(root, file));
      }
      build();
      for (const file of ['version.json', 'release-identity.json']) {
        const emitted = readFileSync(resolve(root, 'dist', file), 'utf8');
        expect(JSON.parse(emitted).releaseIdentity).toEqual(expectedIdentity);
        expect(emitted).not.toMatch(/protection|reviewerId|futurePrivate|private-/);
      }
      const bundle = readdirSync(resolve(root, 'dist/assets')).find(file => /^index-.*\.js$/.test(file))!;
      const javascript = readFileSync(resolve(root, 'dist/assets', bundle), 'utf8');
      const browser: { releaseIdentity?: unknown; document: unknown } = {
        document: { createElement: () => ({ relList: { supports: () => true } }) },
      };
      runInNewContext(javascript, browser);
      expect(browser.releaseIdentity).toEqual(expectedIdentity);
      expect(browser.releaseIdentity).toMatchObject({
        releaseId: record.releaseId, channel, sourceCommit: record.sourceCommit, allocationIdentity: allocation,
      });
      expect(javascript).not.toMatch(/protection|reviewerId|futurePrivate|private-/);

      writeFileSync(resolve(root, 'public/release-identity.json'),
        JSON.stringify({ ...JSON.parse(metadata.frontend), releaseId: 'different-release' }));
      expect(build).toThrow(/Frontend release identity inputs disagree on releaseId/);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  }, 130_000);
  it('retains the service inventory identity without deriving missing allocation evidence', () => {
    expect(frontendVersionMetadata(inventoryIdentity.sourceCommit!, 'now', undefined, inventoryIdentity))
      .toEqual({ service: 'frontend', commit: inventoryIdentity.sourceCommit, buildTime: 'now',
        releaseIdentity: inventoryIdentity });
    expect(frontendVersionMetadata(inventoryIdentity.sourceCommit!, 'now',
      { sourceCommit: inventoryIdentity.sourceCommit, releaseId: inventoryIdentity.releaseId }, inventoryIdentity))
      .toMatchObject({ releaseId: inventoryIdentity.releaseId, releaseIdentity: inventoryIdentity });
  });
  it.each(['releaseId', 'canonicalVersion', 'channel', 'sourceCommit'])(
    'rejects disagreement between file and environment identity on %s', (field) => {
      expect(() => frontendVersionMetadata(inventoryIdentity.sourceCommit!, 'now',
        { sourceCommit: inventoryIdentity.sourceCommit, [field]: 'different' }, inventoryIdentity)).toThrow();
    },
  );
  it('embeds the public identity without package-version or API derivation', () => {
    const identity = { sourceCommit: 'a'.repeat(40), canonicalVersion: '1.2.3-rc.10',
      releaseId: 'insider:1.2.3-rc.10', channel: 'insider', buildId: '45',
      buildAttempt: '2', buildTime: '2026-09-12T20:00:00Z' };
    expect(frontendVersionMetadata(identity.sourceCommit, 'local-clock', identity))
      .toEqual({ service: 'frontend', commit: identity.sourceCommit, ...identity });
  });
  it('excludes private and unknown fields and prevents service/commit overrides', () => {
    const identity = { sourceCommit: 'a'.repeat(40), releaseId: 'insider:1.2.3-rc.10',
      protection: { reviewers: [{ id: 'private-reviewer' }] },
      futureAuthorization: 'private-future', service: 'private-service', commit: 'private-commit' };
    expect(frontendVersionMetadata(identity.sourceCommit, 'now', identity)).toEqual({
      service: 'frontend', commit: identity.sourceCommit, buildTime: 'now',
      sourceCommit: identity.sourceCommit, releaseId: identity.releaseId,
    });
  });
  it.each([false, true])('emits only approved identity in built assets (inventory record: %s)', (includeInventory) => {
    const root = resolve('.artifacts', `public-metadata-${process.pid}`);
    const vite = resolve('node_modules/vite/bin/vite.js');
    const config = resolve('vite.config.ts');
    const identity = {
      releaseId: 'insider:1.2.3-rc.10', channel: 'insider', canonicalVersion: '1.2.3-rc.10',
      baseVersion: '1.2.3', sourceBranch: 'development', sourceTag: 'v1.2.3-rc.10',
      sourceCommit: 'a'.repeat(40), authorizedBranchHead: 'a'.repeat(40),
      buildId: '45', buildAttempt: '2',
      workflowIdentity: 'OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/development',
      identitySha256: 'b'.repeat(64), buildTime: '2026-09-12T20:00:00Z',
    };
    const inventory = { ...identity, allocationIdentity: 'allocation-45', promotionOrigin: null };
    const expectedInventory = Object.fromEntries(Object.entries(inventory)
      .filter(([key]) => key !== 'identitySha256' && key !== 'buildTime'));
    const input = JSON.stringify({ ...identity,
      protection: { rulesets: [{ id: 'private-ruleset-id' }],
        environment: { id: 'private-environment-id', reviewers: [{ id: 'private-reviewer-id' }] } },
      rulesetId: 'private-ruleset-id', environmentId: 'private-environment-id',
      reviewerId: 'private-reviewer-id', futureAuthorization: { policy: 'private-future-value' },
      service: 'private-service', commit: 'private-commit',
    });
    mkdirSync(resolve(root, 'public'), { recursive: true });
    try {
      writeFileSync(resolve(root, 'index.html'), '<!doctype html><title>Release metadata fixture</title>');
      writeFileSync(resolve(root, 'public/sw.js'), '// __PRINTFARMER_BUILD_TIME__ __PRINTFARMER_GIT_HASH__');
      writeFileSync(resolve(root, 'public/release-identity.json'), input);
      copyFileSync(config, resolve(root, 'vite.config.ts'));
      copyFileSync(resolve('public-release-identity.mjs'), resolve(root, 'public-release-identity.mjs'));
      mkdirSync(resolve(root, 'src/common/utils'), { recursive: true });
      copyFileSync(resolve('src/common/utils/releaseIdentity.ts'), resolve(root, 'src/common/utils/releaseIdentity.ts'));
      execFileSync(process.execPath, [vite, 'build', '--config', resolve(root, 'vite.config.ts')], {
        cwd: root, encoding: 'utf8', timeout: 60_000, stdio: 'pipe',
        env: { ...process.env, VITE_GIT_SHA: identity.sourceCommit,
          PRINTFARMER_RELEASE_IDENTITY: includeInventory
            ? JSON.stringify({ ...inventory, privateField: 'private-value' }) : '' },
      });
      for (const file of ['version.json', 'release-identity.json']) {
        const emitted = readFileSync(resolve(root, 'dist', file), 'utf8');
        expect(JSON.parse(emitted)).toEqual({ service: 'frontend', commit: identity.sourceCommit, ...identity,
          ...(includeInventory ? { releaseIdentity: expectedInventory } : {}) });
        expect(emitted).not.toMatch(/protection|ruleset|environment|reviewer|futureAuthorization|private-/);
      }
      expect(readFileSync(resolve(root, 'public/release-identity.json'), 'utf8')).toBe(input);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  }, 65_000);
  it('rejects metadata for a different source and preserves local build behavior', () => {
    expect(() => frontendVersionMetadata('a'.repeat(40), 'now', { sourceCommit: 'b'.repeat(40) }))
      .toThrow(/does not match/);
    expect(frontendVersionMetadata('dev', 'now')).toEqual({ service: 'frontend', commit: 'dev', buildTime: 'now' });
  });
  it('fails closed on malformed known fields instead of silently dropping or coercing them', () => {
    for (const field of ['releaseId', 'identitySha256', 'buildTime', 'channel']) {
      for (const value of [42, {}, [], null, '', 'line\nbreak']) {
        expect(() => frontendVersionMetadata('a'.repeat(40), 'now',
          { sourceCommit: 'a'.repeat(40), [field]: value })).toThrow(/Invalid public identity field/);
      }
    }
  });
});

describe('production commit provenance', () => {
  it.each(['unknown', 'dev', 'abc1234'])(
    'rejects the non-deployable injected commit %s',
    (commit) => {
      process.env.VITE_GIT_SHA = commit;
      delete process.env.GIT_SHA;

      expect(() => resolveGitHash('build')).toThrow(/full 40-character commit SHA/);
    },
  );

  it('accepts and normalizes an injected full commit', () => {
    process.env.VITE_GIT_SHA = 'A'.repeat(40);
    delete process.env.GIT_SHA;

    expect(resolveGitHash('build')).toBe('a'.repeat(40));
  });

  it('does not hide an invalid explicit Vite commit behind a valid fallback', () => {
    process.env.VITE_GIT_SHA = 'unknown';
    process.env.GIT_SHA = 'a'.repeat(40);

    expect(() => resolveGitHash('build')).toThrow(/full 40-character commit SHA/);
  });

  it('keeps development startup usable without a deployable injected commit', () => {
    process.env.VITE_GIT_SHA = 'unknown';
    delete process.env.GIT_SHA;

    expect(resolveGitHash('serve')).toMatch(/^(?:dev|[0-9a-f]{40})$/);
  });
});
