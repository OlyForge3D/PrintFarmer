import { afterEach, describe, expect, it } from 'vitest';
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
  it('embeds the shared record without package-version or API derivation', () => {
    const identity = { sourceCommit: 'a'.repeat(40), canonicalVersion: '1.2.3-rc.10',
      releaseId: 'insider:1.2.3-rc.10', channel: 'insider', buildId: '45',
      buildAttempt: '2', buildTime: '2026-09-12T20:00:00Z' };
    expect(frontendVersionMetadata(identity.sourceCommit, 'local-clock', identity))
      .toEqual({ service: 'frontend', commit: identity.sourceCommit, ...identity });
  });
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
