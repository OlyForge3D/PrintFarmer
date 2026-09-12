import { afterEach, describe, expect, it } from 'vitest';
import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { frontendVersionMetadata, resolveGitHash } from './vite.config';

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
  it('emits only approved identity in both built JSON assets even with a private input record', () => {
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
      execFileSync(process.execPath, [vite, 'build', '--config', config], {
        cwd: root, encoding: 'utf8', timeout: 60_000, stdio: 'pipe',
        env: { ...process.env, VITE_GIT_SHA: identity.sourceCommit },
      });
      for (const file of ['version.json', 'release-identity.json']) {
        const emitted = readFileSync(resolve(root, 'dist', file), 'utf8');
        expect(JSON.parse(emitted)).toEqual({ service: 'frontend', commit: identity.sourceCommit, ...identity });
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
