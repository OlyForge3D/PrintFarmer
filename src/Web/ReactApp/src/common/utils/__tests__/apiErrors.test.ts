import { describe, it, expect } from 'vitest';
import { isApiError, getRevisionConflict } from '../apiErrors';
import type { ApiError } from '@/types/api';

function makeApiError(overrides: Partial<ApiError> = {}): ApiError {
  return {
    message: 'Request failed',
    statusCode: 500,
    ...overrides,
  } as ApiError;
}

describe('isApiError', () => {
  it('returns true for objects with a numeric statusCode', () => {
    expect(isApiError(makeApiError({ statusCode: 404 }))).toBe(true);
  });

  it('returns false for plain Error instances', () => {
    expect(isApiError(new Error('boom'))).toBe(false);
  });

  it('returns false for null/undefined/primitives', () => {
    expect(isApiError(null)).toBe(false);
    expect(isApiError(undefined)).toBe(false);
    expect(isApiError('error string')).toBe(false);
    expect(isApiError(42)).toBe(false);
  });
});

describe('getRevisionConflict', () => {
  it('returns null for non-ApiError values', () => {
    expect(getRevisionConflict(new Error('boom'))).toBeNull();
  });

  it('returns null when statusCode is not 409 or 412', () => {
    const error = makeApiError({ statusCode: 400, data: { expectedRevision: 1, actualRevision: 2 } });
    expect(getRevisionConflict(error)).toBeNull();
  });

  it('returns null for a 409 without a structured conflict body', () => {
    const error = makeApiError({ statusCode: 409, data: { message: 'conflict' } });
    expect(getRevisionConflict(error)).toBeNull();
  });

  it('returns null for a 409 with a missing/non-numeric revision field', () => {
    const error = makeApiError({ statusCode: 409, data: { expectedRevision: '1', actualRevision: 2 } });
    expect(getRevisionConflict(error)).toBeNull();
  });

  it('parses a structured 409 conflict body', () => {
    const error = makeApiError({
      statusCode: 409,
      data: { error: 'Revision mismatch', expectedRevision: 3, actualRevision: 5 },
    });
    expect(getRevisionConflict(error)).toEqual({
      error: 'Revision mismatch',
      expectedRevision: 3,
      actualRevision: 5,
    });
  });

  it('parses a structured 412 conflict body', () => {
    const error = makeApiError({
      statusCode: 412,
      data: { expectedRevision: 1, actualRevision: 2 },
    });
    expect(getRevisionConflict(error)).toEqual({
      error: error.message,
      expectedRevision: 1,
      actualRevision: 2,
    });
  });
});
