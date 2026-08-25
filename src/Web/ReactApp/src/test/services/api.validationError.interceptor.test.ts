/**
 * Tests the ApiClient response interceptor's handling of ASP.NET Core
 * automatic model-binding failures (issue #1973).
 *
 * Those failures return a bare `ValidationProblemDetails`-shaped body —
 * `{ title, status, errors: { "$.field": ["..."] }, traceId }` — with no
 * top-level `message`/`detail`. Before the fix, the interceptor only checked
 * `message`/`detail`, so such a response fell through to the generic axios
 * error message (e.g. "Request failed with status code 400") and the real
 * validation detail (e.g. which field/why) was lost to the caller.
 *
 * Real axios is used (not mocked) so the interceptor actually runs, matching
 * the existing `api.interceptor.test.ts` pattern for the 401 path.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { AxiosError } from 'axios';
import type { AxiosInstance, InternalAxiosRequestConfig } from 'axios';
import { ApiClient } from '@/services/api';
import type { PfRequestConfig } from '@/services/api';

vi.mock('@/common/auth/authenticatedSignalRSession', () => ({
  resetAuthenticatedSignalRSession: vi.fn().mockResolvedValue(undefined),
}));
vi.mock('@/common/auth/authenticationExpiration', () => ({
  notifyAuthenticationExpired: vi.fn(),
}));

// Do NOT mock axios — the real interceptor must run.
vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: () => 'http://localhost:5245/api',
}));

/** Returns an adapter that rejects with the given 400 response body. */
function make400Adapter(data: unknown) {
  return (config: InternalAxiosRequestConfig) => {
    const err = new AxiosError(
      'Request failed with status code 400',
      'ERR_BAD_REQUEST',
      config,
      undefined,
      {
        status: 400,
        data,
        headers: {},
        config,
        statusText: 'Bad Request',
      },
    );
    return Promise.reject(err);
  };
}

describe('ApiClient — ValidationProblemDetails response interceptor (issue #1973)', () => {
  let client: ApiClient;
  let axiosInstance: AxiosInstance;

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    client = new ApiClient();
    axiosInstance = (client as unknown as { client: AxiosInstance }).client;
  });

  it('surfaces the field-level validation detail for a bare ValidationProblemDetails body (no message/detail)', async () => {
    axiosInstance.defaults.adapter = make400Adapter({
      title: 'One or more validation errors occurred.',
      status: 400,
      errors: {
        '$.model3DId': [
          "The JSON value could not be converted to System.Nullable\u00601[System.Guid]. Path: $.model3DId | LineNumber: 0 | BytePositionInLine: 45.",
        ],
      },
      traceId: '00-abc123-def456-00',
    });

    const config: PfRequestConfig = { method: 'POST', url: '/slice', data: { model3DId: 'url-123' } };
    await expect(client.request(config)).rejects.toMatchObject({
      statusCode: 400,
      message: expect.stringContaining('model3DId'),
      details: expect.stringContaining('model3DId'),
    });
  });

  it('joins multiple field errors into a single readable message', async () => {
    axiosInstance.defaults.adapter = make400Adapter({
      errors: {
        '$.model3DId': ['Must be a valid GUID.'],
        '$.printerId': ['Required.'],
      },
    });

    const config: PfRequestConfig = { method: 'POST', url: '/slice' };
    await expect(client.request(config)).rejects.toMatchObject({
      statusCode: 400,
      message: expect.stringContaining('Must be a valid GUID.'),
    });
    await expect(client.request(config)).rejects.toMatchObject({
      message: expect.stringContaining('Required.'),
    });
  });

  it('still prefers a top-level message/detail over the errors map when both are present', async () => {
    axiosInstance.defaults.adapter = make400Adapter({
      message: 'A friendlier top-level message.',
      errors: { '$.model3DId': ['raw validation detail'] },
    });

    const config: PfRequestConfig = { method: 'POST', url: '/slice' };
    await expect(client.request(config)).rejects.toMatchObject({
      statusCode: 400,
      message: 'A friendlier top-level message.',
    });
  });

  it('falls back to the generic axios error message when the body has no errors map', async () => {
    axiosInstance.defaults.adapter = make400Adapter({ status: 400 });

    const config: PfRequestConfig = { method: 'POST', url: '/slice' };
    await expect(client.request(config)).rejects.toMatchObject({
      statusCode: 400,
      message: 'Request failed with status code 400',
    });
  });
});
