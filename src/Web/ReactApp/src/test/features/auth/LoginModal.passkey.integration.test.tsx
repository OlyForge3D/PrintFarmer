/**
 * Integration test: LoginModal → AuthContext → passkeyService → ApiClient → 401 interceptor
 *
 * The full chain runs WITHOUT mocking passkeyService or ApiClient:
 *   LoginModal (click) → AuthContext.loginWithPasskey → passkeyService.loginWithPasskey
 *     → real ApiClient.request (begin + complete) → real 401 interceptor
 *       → ApiError propagates → LoginModal catches → role="alert" rendered
 *
 * HTTP layer is stubbed via a custom axios adapter swapped onto the singleton
 * apiClient.client — the same pattern used in api.interceptor.test.ts.
 *
 * @simplewebauthn/browser is mocked at the browser WebAuthn API boundary
 * (startAuthentication wraps navigator.credentials.get, which jsdom does not
 * implement).  Everything above that boundary — passkeyService, ApiClient,
 * the 401 interceptor with skipAuthRedirect=true, and AuthContext — is real.
 */
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { AxiosError } from 'axios';
import type { AxiosInstance, InternalAxiosRequestConfig } from 'axios';
import { AuthProvider } from '@/common/contexts/AuthContext';
import { LoginModal } from '@/features/auth/components/LoginModal';
import { apiClient } from '@/services/api';

// Mock at the browser WebAuthn API boundary.
// startAuthentication wraps navigator.credentials.get(), which jsdom does not
// implement.  Mocking here is correct — everything above (passkeyService,
// ApiClient, interceptors, AuthContext) remains real and is exercised.
vi.mock('@simplewebauthn/browser', () => ({
  startAuthentication: vi.fn(),
  startRegistration: vi.fn(),
}));

// Prevent jsdom from complaining about the missing VITE_API_BASE_URL env var
// (same as api.interceptor.test.ts).
vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: () => 'http://localhost:5245/api',
}));

// ─── UI component stubs ──────────────────────────────────────────────────────
// Rendering-layer concerns only; the tested seam is LoginModal → AuthContext
// → passkeyService → ApiClient.  These stubs keep the test hermetic without
// affecting the chain under test.

vi.mock('@/common/components/modals/Modal', () => ({
  Modal: ({
    isOpen,
    children,
  }: {
    isOpen: boolean;
    onClose: () => void;
    title: string;
    children: React.ReactNode;
    [key: string]: unknown;
  }) => (isOpen ? <div role="dialog">{children}</div> : null),
}));

vi.mock('@/common/components/skeletons/FormSkeleton', () => ({
  FormSkeleton: () => null,
}));

vi.mock('@/common/components/PrintFarmerLogo', () => ({
  PrintFarmerLogo: () => null,
}));

vi.mock('@/common/components/icons/MdiIcons', () => ({
  EyeIcon: () => <span data-testid="eye-icon" />,
  EyeOffIcon: () => <span data-testid="eye-off-icon" />,
  KeyIcon: () => <span data-testid="key-icon" />,
  LoginIcon: () => <span data-testid="login-icon" />,
}));

vi.mock('@/common/components/ui', () => ({
  Button: ({
    children,
    onClick,
    disabled,
    type,
    ...rest
  }: {
    children: React.ReactNode;
    onClick?: () => void;
    disabled?: boolean;
    type?: string;
    [key: string]: unknown;
  }) => (
    <button
      type={(type as 'button' | 'submit' | 'reset') ?? 'button'}
      onClick={onClick}
      disabled={disabled}
      {...rest}
    >
      {children}
    </button>
  ),
  Input: ({
    id,
    type,
    value,
    onChange,
    placeholder,
    ...rest
  }: {
    id?: string;
    type?: string;
    value?: string;
    onChange?: (e: React.ChangeEvent<HTMLInputElement>) => void;
    placeholder?: string;
    [key: string]: unknown;
  }) => (
    <input
      id={id}
      type={type}
      value={value}
      onChange={onChange}
      placeholder={placeholder}
      {...rest}
    />
  ),
  Checkbox: ({
    label,
    checked,
    onChange,
  }: {
    label: string;
    checked: boolean;
    onChange: (e: React.ChangeEvent<HTMLInputElement>) => void;
  }) => (
    <label>
      <input type="checkbox" checked={checked} onChange={onChange} />
      {label}
    </label>
  ),
}));

vi.mock('react-router', () => ({
  Link: ({ children, to }: { children: React.ReactNode; to: string }) => (
    <a href={to}>{children}</a>
  ),
}));

// ─────────────────────────────────────────────────────────────────────────────

import { startAuthentication } from '@simplewebauthn/browser';

// Access the internal AxiosInstance of the singleton apiClient so we can swap
// the adapter per-test, identical to the approach in api.interceptor.test.ts.
const axiosInstance = (apiClient as unknown as { client: AxiosInstance }).client;

type RouteStub = { status: number; data: unknown };

/**
 * Returns an axios adapter that dispatches by URL substring.
 * Unmatched routes resolve to a 401 (simulates no active session).
 */
function makeDispatchAdapter(routes: Record<string, RouteStub>) {
  return (config: InternalAxiosRequestConfig): Promise<unknown> => {
    const url = config.url ?? '';
    const matchedKey = Object.keys(routes).find((k) => url.includes(k));
    const stub: RouteStub = matchedKey
      ? routes[matchedKey]
      : { status: 401, data: { error: 'Unauthorized' } };

    if (stub.status >= 400) {
      const err = new AxiosError(
        `Request failed with status code ${stub.status}`,
        'ERR_BAD_REQUEST',
        config,
        undefined,
        {
          status: stub.status,
          data: stub.data,
          headers: {},
          config,
          statusText: stub.status === 401 ? 'Unauthorized' : 'Error',
        },
      );
      return Promise.reject(err);
    }

    return Promise.resolve({
      status: stub.status,
      data: stub.data,
      headers: {},
      config,
      statusText: 'OK',
    });
  };
}

// Fake WebAuthn challenge options returned by /auth/passkey/login/begin.
const fakeOptions = {
  challenge: 'fake-challenge',
  timeout: 60_000,
  rpId: 'localhost',
  allowCredentials: [],
  userVerification: 'required',
};

// Fake assertion returned by the browser WebAuthn API (startAuthentication mock).
const fakeAssertion = {
  id: 'fake-credential-id',
  rawId: 'fake-raw-id',
  response: { authenticatorData: '', clientDataJSON: '', signature: '' },
  type: 'public-key',
  clientExtensionResults: {},
};

function renderWithProvider(onClose = vi.fn(), onSwitchToRegister = vi.fn()) {
  return render(
    <AuthProvider>
      <LoginModal isOpen={true} onClose={onClose} onSwitchToRegister={onSwitchToRegister} />
    </AuthProvider>,
  );
}

describe('LoginModal + AuthProvider + passkeyService integration — HTTP-layer stubbing', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();

    // Prevent jsdom navigation side-effects when the interceptor writes to href.
    Object.defineProperty(window, 'location', {
      value: { pathname: '/', href: 'http://localhost/', assign: vi.fn() },
      writable: true,
      configurable: true,
    });

    // startAuthentication returns the fake browser assertion for all tests.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    vi.mocked(startAuthentication).mockResolvedValue(fakeAssertion as any);
  });

  afterEach(() => {
    // Remove the test adapter so other test files see the default axios adapter.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    delete (axiosInstance.defaults as any).adapter;
  });

  it('shows inline alert when /login/complete returns 401 — real interceptor honours skipAuthRedirect', async () => {
    // Adapter: begin succeeds, complete rejects with 401.
    // The real interceptor sees skipAuthRedirect=true on the complete request
    // and does NOT redirect or clear the token — it normalises to an ApiError
    // which propagates to LoginModal.handlePasskeyLogin's catch block.
    axiosInstance.defaults.adapter = makeDispatchAdapter({
      '/auth/passkey/login/begin': { status: 200, data: fakeOptions },
      '/auth/passkey/login/complete': {
        status: 401,
        data: { error: 'Credential ID not found' },
      },
    });

    const onClose = vi.fn();
    renderWithProvider(onClose);

    fireEvent.change(screen.getByPlaceholderText(/enter your username or email/i), {
      target: { value: 'alice' },
    });
    fireEvent.click(screen.getByRole('button', { name: /sign in with passkey/i }));

    await waitFor(() => {
      // ApiClient normalises { error: 'Credential ID not found' } into
      // ApiError.details, which LoginModal renders as the alert text.
      expect(screen.getByRole('alert')).toHaveTextContent('Credential ID not found');
    });

    // skipAuthRedirect=true: no redirect, no token clear, no modal close.
    expect(window.location.href).toBe('http://localhost/');
    expect(localStorage.getItem('auth-token')).toBeNull();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('closes modal and stores token when /login/complete returns 200 — real full chain', async () => {
    const fakeUser = {
      id: 1,
      username: 'alice',
      email: 'alice@example.com',
      isActive: true,
      roles: [],
    };

    axiosInstance.defaults.adapter = makeDispatchAdapter({
      '/auth/passkey/login/begin': { status: 200, data: fakeOptions },
      '/auth/passkey/login/complete': {
        status: 200,
        data: { success: true, token: 'fake-jwt', user: fakeUser },
      },
    });

    const onClose = vi.fn();
    renderWithProvider(onClose);

    fireEvent.change(screen.getByPlaceholderText(/enter your username or email/i), {
      target: { value: 'alice' },
    });
    fireEvent.click(screen.getByRole('button', { name: /sign in with passkey/i }));

    await waitFor(() => {
      expect(onClose).toHaveBeenCalledOnce();
    });

    // AuthContext stored the token and no error alert is visible.
    expect(localStorage.getItem('auth-token')).toBe('fake-jwt');
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
