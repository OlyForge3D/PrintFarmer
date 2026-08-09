import { describe, it, expect, vi } from 'vitest';
import { generateUUID } from '../uuid';

describe('generateUUID', () => {
  it('should generate a valid UUID format', () => {
    const uuid = generateUUID();
    // UUID format: xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx
    const uuidRegex = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
    expect(uuid).toMatch(uuidRegex);
  });

  it('should generate unique UUIDs on multiple calls', () => {
    const uuid1 = generateUUID();
    const uuid2 = generateUUID();
    const uuid3 = generateUUID();
    
    expect(uuid1).not.toBe(uuid2);
    expect(uuid2).not.toBe(uuid3);
    expect(uuid1).not.toBe(uuid3);
  });

  it('should return string type', () => {
    const uuid = generateUUID();
    expect(typeof uuid).toBe('string');
  });

  it('should generate UUID with correct length', () => {
    const uuid = generateUUID();
    expect(uuid).toHaveLength(36); // 32 hex chars + 4 dashes
  });

  it('should generate version 4 UUID (has 4 as version digit)', () => {
    const uuid = generateUUID();
    // The version digit is the first character of the third group
    expect(uuid.charAt(14)).toBe('4');
  });

  it('should have correct variant bits (8, 9, a, or b)', () => {
    const uuid = generateUUID();
    // The variant bits are the first character of the fourth group
    const variantChar = uuid.charAt(19).toLowerCase();
    expect(['8', '9', 'a', 'b']).toContain(variantChar);
  });

  describe('fallback path (no crypto.randomUUID)', () => {
    // Regression test for the js/insecure-randomness CodeQL finding: the
    // fallback used to derive IDs from Math.random(). It must now use
    // crypto.getRandomValues() instead, and never call Math.random() at all.
    it('uses crypto.getRandomValues (not Math.random) when randomUUID is unavailable', () => {
      const originalDescriptor = Object.getOwnPropertyDescriptor(crypto, 'randomUUID');
      // Force the "no crypto.randomUUID" branch regardless of the property's
      // original configurability in this test environment.
      Object.defineProperty(crypto, 'randomUUID', {
        value: undefined,
        configurable: true,
        writable: true,
      });
      const randomSpy = vi.spyOn(Math, 'random');
      const getRandomValuesSpy = vi.spyOn(crypto, 'getRandomValues');

      try {
        const uuid = generateUUID();
        const uuidRegex = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
        expect(uuid).toMatch(uuidRegex);
        expect(getRandomValuesSpy).toHaveBeenCalled();
        expect(randomSpy).not.toHaveBeenCalled();
      } finally {
        if (originalDescriptor) {
          Object.defineProperty(crypto, 'randomUUID', originalDescriptor);
        }
        randomSpy.mockRestore();
        getRandomValuesSpy.mockRestore();
      }
    });

    it('throws (never falls back to Math.random) when no CSPRNG is available at all', () => {
      const originalRandomUUIDDescriptor = Object.getOwnPropertyDescriptor(crypto, 'randomUUID');
      const originalGetRandomValuesDescriptor = Object.getOwnPropertyDescriptor(crypto, 'getRandomValues');
      Object.defineProperty(crypto, 'randomUUID', { value: undefined, configurable: true, writable: true });
      Object.defineProperty(crypto, 'getRandomValues', { value: undefined, configurable: true, writable: true });
      const randomSpy = vi.spyOn(Math, 'random');

      try {
        expect(() => generateUUID()).toThrow(/no cryptographically secure random source available/i);
        expect(randomSpy).not.toHaveBeenCalled();
      } finally {
        if (originalRandomUUIDDescriptor) {
          Object.defineProperty(crypto, 'randomUUID', originalRandomUUIDDescriptor);
        }
        if (originalGetRandomValuesDescriptor) {
          Object.defineProperty(crypto, 'getRandomValues', originalGetRandomValuesDescriptor);
        }
        randomSpy.mockRestore();
      }
    });
  });
});
