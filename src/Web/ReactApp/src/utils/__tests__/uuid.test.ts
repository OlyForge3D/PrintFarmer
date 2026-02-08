import { describe, it, expect } from 'vitest';
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
});
