import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { assertSourceIdentity, initializeRun, type SourceIdentity } from './run-context';

const source: SourceIdentity = { schema: 1, sourceSha: 'a'.repeat(40), harnessSha256: 'b'.repeat(64), registrySha256: 'c'.repeat(64) };

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
    expect(() => assertSourceIdentity(source, source)).not.toThrow();
  });
});
