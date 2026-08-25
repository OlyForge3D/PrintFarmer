import { describe, it, expect } from 'vitest';
import { isApiError, getRevisionConflict, getErrorMessage, extractValidationErrorMessage } from '../apiErrors';
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

describe('getErrorMessage', () => {
  it('prefers the ApiError.message over the fallback (apiClient rejects with a plain ApiError, not an Error instance)', () => {
    const error = makeApiError({ message: 'Tag name is required.' });
    expect(getErrorMessage(error, 'fallback')).toBe('Tag name is required.');
  });

  it('falls back to a native Error.message when the value is a real Error', () => {
    expect(getErrorMessage(new Error('network down'), 'fallback')).toBe('network down');
  });

  it('returns the fallback for unrecognized error shapes', () => {
    expect(getErrorMessage('a plain string', 'fallback')).toBe('fallback');
    expect(getErrorMessage(null, 'fallback')).toBe('fallback');
    expect(getErrorMessage(undefined, 'fallback')).toBe('fallback');
  });

  it('returns the fallback when an ApiError has an empty message', () => {
    const error = makeApiError({ message: '' });
    expect(getErrorMessage(error, 'fallback')).toBe('fallback');
  });
});

describe('extractValidationErrorMessage', () => {
  // Regression test for issue #1973: a malformed slice-job request body (a
  // non-GUID string for `model3DId`) fails ASP.NET Core model binding before
  // any controller action runs, so the API returns a bare
  // ValidationProblemDetails `errors` map with no top-level `message`/`detail`.
  it('joins messages from a ValidationProblemDetails "errors" map', () => {
    const data = {
      title: 'One or more validation errors occurred.',
      status: 400,
      errors: {
        '$.model3DId': [
          "The JSON value could not be converted to System.Nullable`1[System.Guid]. Path: $.model3DId | LineNumber: 0 | BytePositionInLine: 42.",
        ],
      },
      traceId: '00-abc-def-00',
    };
    expect(extractValidationErrorMessage(data)).toBe(
      "The JSON value could not be converted to System.Nullable`1[System.Guid]. Path: $.model3DId | LineNumber: 0 | BytePositionInLine: 42.",
    );
  });

  it('joins multiple field errors with a space', () => {
    const data = {
      errors: {
        Name: ['Name is required.'],
        Email: ['Email is invalid.'],
      },
    };
    expect(extractValidationErrorMessage(data)).toBe('Name is required. Email is invalid.');
  });

  it('returns undefined when there is no "errors" map', () => {
    expect(extractValidationErrorMessage({ message: 'plain error' })).toBeUndefined();
    expect(extractValidationErrorMessage({ errors: 'not-an-object' })).toBeUndefined();
    expect(extractValidationErrorMessage({ errors: { field: [] } })).toBeUndefined();
  });

  it('returns undefined for null, undefined, and non-object values', () => {
    expect(extractValidationErrorMessage(null)).toBeUndefined();
    expect(extractValidationErrorMessage(undefined)).toBeUndefined();
    expect(extractValidationErrorMessage('a string')).toBeUndefined();
  });
});
