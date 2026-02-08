import { describe, it, expect, vi, beforeEach } from 'vitest';
import detectBrowser from '../browserCompat';

describe('browserCompat', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('should be a function', () => {
    expect(typeof detectBrowser).toBe('function');
  });

  it('should return boolean', () => {
    const result = detectBrowser();
    expect(typeof result).toBe('boolean');
  });
});
