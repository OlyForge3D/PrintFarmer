import { describe, expect, it } from 'vitest';
import { buildInfo } from '../buildInfo';

describe('buildInfo', () => {
  it('exposes the compile-time git commit and build time as strings', () => {
    expect(typeof buildInfo.commit).toBe('string');
    expect(buildInfo.commit.length).toBeGreaterThan(0);
    expect(typeof buildInfo.buildTime).toBe('string');
    expect(buildInfo.buildTime.length).toBeGreaterThan(0);
  });
});
