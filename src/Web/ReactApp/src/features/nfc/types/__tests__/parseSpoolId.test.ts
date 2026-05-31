import { describe, it, expect } from 'vitest';
import { parseSpoolId } from '../nfc';

describe('parseSpoolId', () => {
  it('parses a numeric string to a number', () => {
    expect(parseSpoolId('42')).toBe(42);
  });

  it('parses string with leading zeros', () => {
    expect(parseSpoolId('007')).toBe(7);
  });

  it('returns undefined for empty string', () => {
    expect(parseSpoolId('')).toBeUndefined();
  });

  it('returns undefined for whitespace-only string', () => {
    expect(parseSpoolId('   ')).toBeUndefined();
  });

  it('returns undefined for undefined input', () => {
    expect(parseSpoolId(undefined)).toBeUndefined();
  });

  it('returns undefined for null input', () => {
    expect(parseSpoolId(null)).toBeUndefined();
  });

  it('returns undefined for non-numeric string', () => {
    expect(parseSpoolId('abc')).toBeUndefined();
  });

  it('parses string with trailing non-numeric chars (parseInt behavior)', () => {
    expect(parseSpoolId('123abc')).toBe(123);
  });
});
