import { describe, it, expect } from 'vitest';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';

describe('apiUrlHelpers', () => {
  it('returns a string base url and headers object', () => {
    const base = getApiBaseUrl();
    const headers = getAuthHeaders();

    expect(typeof base).toBe('string');
    expect(headers).toBeInstanceOf(Object);
  });
});
