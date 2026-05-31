import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { MockInstance } from 'vitest';

/* ── Hoisted mocks ── */

const mockApiClient = vi.hoisted(() => ({
  post: vi.fn(),
}));

const mockStartAuthentication = vi.hoisted(() => vi.fn());
const mockStartRegistration = vi.hoisted(() => vi.fn());

vi.mock('@/services/api', () => ({ apiClient: mockApiClient }));
vi.mock('@simplewebauthn/browser', () => ({
  startAuthentication: mockStartAuthentication,
  startRegistration: mockStartRegistration,
}));

import { passkeyLogin, passkeyRegister, isPasskeySupported } from '@/features/auth/services/passkeyService';

// ─── helpers ─────────────────────────────────────────────────────────────────

function makeAssertionOptions() {
  return {
    challenge: 'dGVzdC1jaGFsbGVuZ2U',
    timeout: 60000,
    rpId: 'localhost',
    allowCredentials: [],
    userVerification: 'required',
  };
}

function makeAssertionResult() {
  return {
    id: 'credId',
    rawId: 'credId',
    type: 'public-key',
    response: {
      authenticatorData: 'authData',
      clientDataJSON: 'clientData',
      signature: 'sig',
      userHandle: null,
    },
    authenticatorAttachment: 'platform',
  };
}

function makeRegistrationOptions() {
  return {
    rp: { name: 'PrintFarmer', id: 'localhost' },
    user: { id: 'dXNlcklk', name: 'alice', displayName: 'alice' },
    challenge: 'dGVzdC1jaGFsbGVuZ2U',
    pubKeyCredParams: [{ type: 'public-key', alg: -7 }],
    timeout: 60000,
  };
}

function makeRegistrationResult() {
  return {
    id: 'newCredId',
    rawId: 'newCredId',
    type: 'public-key',
    response: {
      attestationObject: 'attObj',
      clientDataJSON: 'clientData',
      transports: ['internal'],
    },
    authenticatorAttachment: 'platform',
  };
}

// ─── isPasskeySupported ───────────────────────────────────────────────────────

describe('isPasskeySupported', () => {
  it('returns false when PublicKeyCredential is unavailable', () => {
    const orig = (window as unknown as Record<string, unknown>).PublicKeyCredential;
    delete (window as unknown as Record<string, unknown>).PublicKeyCredential;
    expect(isPasskeySupported()).toBeFalsy();
    (window as unknown as Record<string, unknown>).PublicKeyCredential = orig;
  });
});

// ─── passkeyLogin ─────────────────────────────────────────────────────────────

describe('passkeyLogin', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('calls /auth/passkey/login/start with { username }', async () => {
    const assertionOptions = makeAssertionOptions();
    const assertionResult = makeAssertionResult();

    (mockApiClient.post as MockInstance)
      .mockResolvedValueOnce({ data: assertionOptions })
      .mockResolvedValueOnce({ data: { success: true, token: 'jwt-token', user: { id: '1', username: 'alice' } } });
    mockStartAuthentication.mockResolvedValueOnce(assertionResult);

    await passkeyLogin('alice');

    const [startUrl, startBody] = (mockApiClient.post as MockInstance).mock.calls[0];
    expect(startUrl).toBe('/auth/passkey/login/start');
    expect(startBody).toEqual({ username: 'alice' });
  });

  it('calls /auth/passkey/login/complete with { username, assertionResponse }', async () => {
    const assertionOptions = makeAssertionOptions();
    const assertionResult = makeAssertionResult();

    (mockApiClient.post as MockInstance)
      .mockResolvedValueOnce({ data: assertionOptions })
      .mockResolvedValueOnce({ data: { success: true, token: 'jwt-token', user: { id: '1', username: 'alice' } } });
    mockStartAuthentication.mockResolvedValueOnce(assertionResult);

    await passkeyLogin('alice');

    const [completeUrl, completeBody] = (mockApiClient.post as MockInstance).mock.calls[1];
    expect(completeUrl).toBe('/auth/passkey/login/complete');
    expect(completeBody).toEqual({ username: 'alice', assertionResponse: assertionResult });
  });

  it('passes assertion options directly to startAuthentication', async () => {
    const assertionOptions = makeAssertionOptions();
    const assertionResult = makeAssertionResult();

    (mockApiClient.post as MockInstance)
      .mockResolvedValueOnce({ data: assertionOptions })
      .mockResolvedValueOnce({ data: { success: true, token: 'tok', user: null } });
    mockStartAuthentication.mockResolvedValueOnce(assertionResult);

    await passkeyLogin('alice');

    expect(mockStartAuthentication).toHaveBeenCalledWith({ optionsJSON: assertionOptions });
  });

  it('returns success with token and user on valid response', async () => {
    const assertionOptions = makeAssertionOptions();
    const user = { id: '1', username: 'alice' };

    (mockApiClient.post as MockInstance)
      .mockResolvedValueOnce({ data: assertionOptions })
      .mockResolvedValueOnce({ data: { success: true, token: 'my-jwt', user } });
    mockStartAuthentication.mockResolvedValueOnce(makeAssertionResult());

    const result = await passkeyLogin('alice');

    expect(result).toEqual({ success: true, token: 'my-jwt', user });
  });

  it('returns { success: false, error: "cancelled" } on NotAllowedError', async () => {
    (mockApiClient.post as MockInstance).mockResolvedValueOnce({ data: makeAssertionOptions() });
    mockStartAuthentication.mockRejectedValueOnce(
      Object.assign(new DOMException('cancelled', 'NotAllowedError')),
    );

    const result = await passkeyLogin('alice');

    expect(result).toEqual({ success: false, error: 'cancelled' });
  });

  it('sends empty username when no hint provided', async () => {
    (mockApiClient.post as MockInstance)
      .mockResolvedValueOnce({ data: makeAssertionOptions() })
      .mockResolvedValueOnce({ data: { success: false, error: 'Passkey login not yet available' } });
    mockStartAuthentication.mockResolvedValueOnce(makeAssertionResult());

    await passkeyLogin();

    const [, startBody] = (mockApiClient.post as MockInstance).mock.calls[0];
    expect(startBody).toEqual({});

    const [, completeBody] = (mockApiClient.post as MockInstance).mock.calls[1];
    expect(completeBody.username).toBe('');
  });
});

// ─── passkeyRegister ──────────────────────────────────────────────────────────

describe('passkeyRegister', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('calls /auth/passkey/register/start', async () => {
    const regOptions = makeRegistrationOptions();
    const regResult = makeRegistrationResult();

    (mockApiClient.post as MockInstance)
      .mockResolvedValueOnce({ data: regOptions })
      .mockResolvedValueOnce({ data: { success: true, credentialId: 'newCredId' } });
    mockStartRegistration.mockResolvedValueOnce(regResult);

    await passkeyRegister();

    const [startUrl] = (mockApiClient.post as MockInstance).mock.calls[0];
    expect(startUrl).toBe('/auth/passkey/register/start');
  });

  it('calls /auth/passkey/register/complete with attestation + deviceName', async () => {
    const regOptions = makeRegistrationOptions();
    const regResult = makeRegistrationResult();

    (mockApiClient.post as MockInstance)
      .mockResolvedValueOnce({ data: regOptions })
      .mockResolvedValueOnce({ data: { success: true, credentialId: 'newCredId' } });
    mockStartRegistration.mockResolvedValueOnce(regResult);

    await passkeyRegister('My MacBook');

    const [completeUrl, completeBody] = (mockApiClient.post as MockInstance).mock.calls[1];
    expect(completeUrl).toBe('/auth/passkey/register/complete');
    expect(completeBody).toMatchObject({ ...regResult, deviceName: 'My MacBook' });
  });

  it('passes registration options directly to startRegistration', async () => {
    const regOptions = makeRegistrationOptions();

    (mockApiClient.post as MockInstance)
      .mockResolvedValueOnce({ data: regOptions })
      .mockResolvedValueOnce({ data: { success: true, credentialId: 'cid' } });
    mockStartRegistration.mockResolvedValueOnce(makeRegistrationResult());

    await passkeyRegister();

    expect(mockStartRegistration).toHaveBeenCalledWith({ optionsJSON: regOptions });
  });

  it('returns success with credentialId on valid response', async () => {
    (mockApiClient.post as MockInstance)
      .mockResolvedValueOnce({ data: makeRegistrationOptions() })
      .mockResolvedValueOnce({ data: { success: true, credentialId: 'abc123' } });
    mockStartRegistration.mockResolvedValueOnce(makeRegistrationResult());

    const result = await passkeyRegister();

    expect(result).toEqual({ success: true, credentialId: 'abc123' });
  });

  it('returns { success: false, error: "not-supported" } on NotSupportedError', async () => {
    (mockApiClient.post as MockInstance).mockResolvedValueOnce({ data: makeRegistrationOptions() });
    mockStartRegistration.mockRejectedValueOnce(
      Object.assign(new DOMException('not supported', 'NotSupportedError')),
    );

    const result = await passkeyRegister();

    expect(result).toEqual({ success: false, error: 'not-supported' });
  });
});
