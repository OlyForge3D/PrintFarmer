/**
 * Tests the REAL AuthContext.loginWithPasskey failure paths (not mocked at the
 * useAuth level). These are the regression guards requested by the trio review.
 *
 * - Backend soft-failure (success:false from POST /login/complete): returns false
 *   and sets context `error` state.
 * - Ceremony throw (user cancelled, hardware error): re-throws so callers can
 *   display an inline error near the passkey button.
 */
import React from 'react';
import { render, screen, act, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { AuthProvider } from '@/common/contexts/AuthContext';
import { useAuth } from '@/features/auth/hooks/useAuth';

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
      setCaughtError(err instanceof Error ? err.message : String(err));
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

  it('returns false and sets context error when backend returns success:false', async () => {
    vi.mocked(mockPasskeyLogin).mockResolvedValue({
      success: false,
      error: 'Assertion failed: unknown credential',
    } as never);

    renderWithAuth();

    // Wait for initial auth check to settle
    await waitFor(() => expect(screen.queryByTestId('loading')).toBeNull());

    await act(async () => {
      screen.getByRole('button', { name: 'passkey-login' }).click();
    });

    await waitFor(() => {
      expect(screen.getByTestId('result')).toHaveTextContent('false');
      expect(screen.getByTestId('context-error')).toHaveTextContent(
        'Assertion failed: unknown credential',
      );
    });
  });

  it('sets generic context error when backend success:false with no error field', async () => {
    vi.mocked(mockPasskeyLogin).mockResolvedValue({ success: false } as never);

    renderWithAuth();
    await waitFor(() => expect(screen.queryByTestId('loading')).toBeNull());

    await act(async () => {
      screen.getByRole('button', { name: 'passkey-login' }).click();
    });

    await waitFor(() => {
      expect(screen.getByTestId('result')).toHaveTextContent('false');
      expect(screen.getByTestId('context-error')).toHaveTextContent('Passkey login failed');
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
