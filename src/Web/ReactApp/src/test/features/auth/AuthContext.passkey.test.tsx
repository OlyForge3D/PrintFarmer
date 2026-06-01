/**
 * Tests the REAL AuthContext.loginWithPasskey failure paths (not mocked at the
 * useAuth level). These are the regression guards requested by the trio review.
 *
 * The backend POST /auth/passkey/login/complete never returns a 200 with
 * success:false — it returns 401 on a failed assertion, which apiClient converts
 * to a thrown ApiError.  Because loginWithPasskey has no catch block (only
 * finally), ApiErrors propagate directly to the caller, which then displays them
 * inline.
 *
 * - 401/ApiError from assertion failure (with details): propagates; details intact
 * - 401/ApiError from assertion failure (no details): propagates; message used
 * - Ceremony throw (user cancelled, hardware error): same propagation path
 */
import React from 'react';
import { render, screen, act, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { AuthProvider } from '@/common/contexts/AuthContext';
import { useAuth } from '@/features/auth/hooks/useAuth';
import type { ApiError } from '@/types/api';

// Mock the entire passkeyService module so ceremony calls never touch the DOM
vi.mock('@/services/passkeyService', () => ({
  loginWithPasskey: vi.fn(),
}));

// Mock apiClient so AuthContext's getCurrentUser (on mount) doesn't hit network
vi.mock('@/services/api', () => ({
  apiClient: {
    getCurrentUser: vi.fn().mockRejectedValue(new Error('no session')),
    login: vi.fn(),
    register: vi.fn(),
    logout: vi.fn(),
  },
}));

import { loginWithPasskey as mockPasskeyLogin } from '@/services/passkeyService';

// A minimal consumer component that exposes auth context state via the DOM
function AuthConsumer() {
  const { loginWithPasskey, error, isLoading } = useAuth();
  const [result, setResult] = React.useState<boolean | null>(null);
  const [caughtError, setCaughtError] = React.useState<string | null>(null);

  async function handlePasskeyLogin() {
    setCaughtError(null);
    try {
      const ok = await loginWithPasskey('alice');
      setResult(ok);
    } catch (err: unknown) {
      // ApiError is a plain object {message, statusCode, details?} — not an
      // Error instance.  Extract .message so both shapes are readable in the DOM.
      const errObj = err as { message?: string };
      setCaughtError(errObj?.message ?? (err instanceof Error ? err.message : String(err)));
    }
  }

  return (
    <div>
      <button onClick={handlePasskeyLogin}>passkey-login</button>
      {isLoading && <span data-testid="loading">loading</span>}
      {error && <span data-testid="context-error">{error}</span>}
      {result !== null && <span data-testid="result">{String(result)}</span>}
      {caughtError && <span data-testid="caught-error">{caughtError}</span>}
    </div>
  );
}

function renderWithAuth() {
  return render(
    <AuthProvider>
      <AuthConsumer />
    </AuthProvider>,
  );
}

describe('AuthContext.loginWithPasskey — real implementation', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  it('propagates ApiError to the caller when the backend rejects assertion (with details)', async () => {
    const apiError: ApiError = {
      message: 'Assertion failed',
      statusCode: 401,
      details: 'Credential ID not found in the database',
    };
    vi.mocked(mockPasskeyLogin).mockRejectedValue(apiError);

    renderWithAuth();
    await waitFor(() => expect(screen.queryByTestId('loading')).toBeNull());

    await act(async () => {
      screen.getByRole('button', { name: 'passkey-login' }).click();
    });

    await waitFor(() => {
      // ApiError propagated — caller receives .message
      expect(screen.getByTestId('caught-error')).toHaveTextContent('Assertion failed');
      // loginWithPasskey has no catch block: setError is never called
      expect(screen.queryByTestId('context-error')).toBeNull();
      // loginWithPasskey threw, so no boolean result was returned
      expect(screen.queryByTestId('result')).toBeNull();
      // finally block ran — loading state cleared
      expect(screen.queryByTestId('loading')).toBeNull();
    });
  });

  it('propagates ApiError and clears loading when details are absent', async () => {
    const apiError: ApiError = {
      message: 'Authentication assertion failed',
      statusCode: 401,
    };
    vi.mocked(mockPasskeyLogin).mockRejectedValue(apiError);

    renderWithAuth();
    await waitFor(() => expect(screen.queryByTestId('loading')).toBeNull());

    await act(async () => {
      screen.getByRole('button', { name: 'passkey-login' }).click();
    });

    await waitFor(() => {
      expect(screen.getByTestId('caught-error')).toHaveTextContent(
        'Authentication assertion failed',
      );
      expect(screen.queryByTestId('context-error')).toBeNull();
      expect(screen.queryByTestId('result')).toBeNull();
      expect(screen.queryByTestId('loading')).toBeNull();
    });
  });

  it('re-throws ceremony errors so callers can display them inline', async () => {
    vi.mocked(mockPasskeyLogin).mockRejectedValue(new Error('NotAllowedError: user cancelled'));

    renderWithAuth();
    await waitFor(() => expect(screen.queryByTestId('loading')).toBeNull());

    await act(async () => {
      screen.getByRole('button', { name: 'passkey-login' }).click();
    });

    await waitFor(() => {
      // Error propagates to the caller (not swallowed by AuthContext)
      expect(screen.getByTestId('caught-error')).toHaveTextContent(
        'NotAllowedError: user cancelled',
      );
      // No context-level error — caller owns display for thrown errors
      expect(screen.queryByTestId('context-error')).toBeNull();
      // No boolean result — threw before returning
      expect(screen.queryByTestId('result')).toBeNull();
    });
  });
});

