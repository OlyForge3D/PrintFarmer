import { describe, it, expect } from 'vitest';
import {
  loadWireContractFixture,
  loadWireContractManifest,
  getWireContractManifestEntry,
} from './wireContracts';

describe('wireContracts loader (#2240 corpus loader)', () => {
  it('loads the full manifest as a non-empty array', () => {
    const manifest = loadWireContractManifest();
    expect(Array.isArray(manifest)).toBe(true);
    expect(manifest.length).toBeGreaterThan(0);
  });

  it('finds a manifest entry for a known fixture and reports its endpoint', () => {
    const entry = getWireContractManifestEntry('api/tasks/tasks.empty-collection.json');
    expect(entry).toBeDefined();
    expect(entry?.Endpoint).toContain('/api/tasks');
  });

  it('returns undefined for a fixture path with no manifest entry', () => {
    expect(getWireContractManifestEntry('api/tasks/does-not-exist.json')).toBeUndefined();
  });

  it('loads and parses a real fixture unchanged', () => {
    const fixture = loadWireContractFixture<unknown[]>('api/tasks/tasks.empty-collection.json');
    expect(fixture).toEqual([]);
  });

  it('throws instead of silently reading an untracked fixture path', () => {
    expect(() => loadWireContractFixture('api/tasks/does-not-exist.json')).toThrow(
      /no manifest\.json entry/
    );
  });
});
