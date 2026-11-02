import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import { assertSourceGuard, assertSourceIdentity, initializeRun, sourceIdentity, startSourceGuard, type SourceIdentity } from './run-context';

const source: SourceIdentity = { schema: 1, sourceSha: 'a'.repeat(40), treeSha: 'e'.repeat(40), harnessSha256: 'b'.repeat(64), registrySha256: 'c'.repeat(64) };

function fixture() {
  const repository = mkdtempSync(join(tmpdir(), 'pf-source-fixture-'));
  const git = (...args: string[]) => execFileSync('git', args, { cwd: repository, stdio: 'pipe' });
  git('init', '--quiet');
  git('config', 'user.name', 'Source fixture');
  git('config', 'user.email', 'fixture@example.invalid');
  writeFileSync(join(repository, 'harness.ts'), 'export const fixture = true;\n');
  writeFileSync(join(repository, 'editor.tsx'), 'export const editor = true;\n');
  git('add', '.');
  git('commit', '--quiet', '-m', 'Fixture source');
  return repository;
}

describe('owned settings run provenance', () => {
  it('creates distinct run roots and rejects external reuse and stale worker handoffs', () => {
    const parent = mkdtempSync(join(tmpdir(), 'pf-run-context-'));
    try {
      const firstEnv: NodeJS.ProcessEnv = {};
      const first = initializeRun(parent, source, firstEnv, 123);
      const second = initializeRun(parent, source, {}, 456);
      expect(second.root).not.toBe(first.root);
      expect(second.runId).not.toBe(first.runId);
      expect(() => initializeRun(parent, source, firstEnv)).toThrow('external run reuse');
      const workerEnv = { ...firstEnv, TEST_WORKER_INDEX: '0' };
      expect(initializeRun(parent, source, workerEnv, 789, 123)).toEqual(first);
      expect(() => initializeRun(parent, source, workerEnv, 789, 456)).toThrow('live Playwright launcher');
      expect(() => initializeRun(parent, { ...source, sourceSha: 'd'.repeat(40) }, workerEnv, 789, 123)).toThrow('provenance');
    } finally {
      rmSync(parent, { recursive: true });
    }
  });

  it('rejects missing and changed source identities instead of trusting a matching engine string', () => {
    expect(() => assertSourceIdentity(source, { ...source, registrySha256: '' })).toThrow('provenance');
    expect(() => assertSourceIdentity(source, { ...source, harnessSha256: 'd'.repeat(64) })).toThrow('provenance');
    expect(() => assertSourceIdentity(source, { ...source, sourceSha: 'd'.repeat(40) })).toThrow('provenance');
    expect(() => assertSourceIdentity(source, { ...source, treeSha: 'd'.repeat(40) })).toThrow('provenance');
    expect(() => assertSourceIdentity(source, source)).not.toThrow();
  });

  it('rejects dirty tracked editor code outside the selected harness hashes', () => {
    const repository = fixture();
    try {
      expect(sourceIdentity(repository, ['harness.ts']).treeSha).toMatch(/^[a-f\d]{40}$/);
      writeFileSync(join(repository, 'editor.tsx'), 'export const editor = false;\n');
      expect(() => sourceIdentity(repository, ['harness.ts'])).toThrow('tracked source changes');
    } finally {
      rmSync(repository, { recursive: true, force: true });
    }
  });

  it('latches tracked edits after restoration, ignores build outputs and rejects a dead monitor', async () => {
    const repository = fixture();
    const parent = mkdtempSync(join(tmpdir(), 'pf-source-run-'));
    let stop: (() => void) | undefined;
    try {
      const identity = sourceIdentity(repository, ['harness.ts']);
      const context = initializeRun(parent, identity, {});
      stop = startSourceGuard(context, repository);
      expect(() => assertSourceGuard(context)).not.toThrow();
      writeFileSync(join(repository, 'build-output.tmp'), 'untracked output');
      await new Promise(resolve => setTimeout(resolve, 100));
      expect(() => assertSourceGuard(context)).not.toThrow();
      writeFileSync(join(repository, 'editor.tsx'), 'export const editor = false;\n');
      writeFileSync(join(repository, 'editor.tsx'), 'export const editor = true;\n');
      expect(sourceIdentity(repository, ['harness.ts'])).toEqual(identity);
      await vi.waitFor(() => expect(() => assertSourceGuard(context)).toThrow('Tracked source changed'));
      const cleanContext = initializeRun(parent, identity, {});
      const stopClean = startSourceGuard(cleanContext, repository);
      stopClean();
      await vi.waitFor(() => expect(() => assertSourceGuard(cleanContext)).toThrow('no longer alive'));
    } finally {
      stop?.();
      await new Promise(resolve => setTimeout(resolve, 100));
      rmSync(repository, { recursive: true, force: true });
      rmSync(parent, { recursive: true, force: true });
    }
  });
});
