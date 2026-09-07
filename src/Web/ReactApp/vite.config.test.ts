import { afterEach, describe, expect, it } from 'vitest';
import { resolveGitHash } from './vite.config';

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

  it('keeps development startup usable without a deployable injected commit', () => {
    process.env.VITE_GIT_SHA = 'unknown';
    delete process.env.GIT_SHA;

    expect(resolveGitHash('serve')).toMatch(/^(?:dev|[0-9a-f]{40})$/);
  });
});
