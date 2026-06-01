/**
 * Integration test for the LoginModal ↔ AuthProvider seam on the passkey path.
 *
 * Unlike LoginModal.passkey.test.tsx, useAuth is NOT mocked here.  The real
 * AuthProvider / AuthContext.loginWithPasskey is exercised so that the error
 * propagation seam (ApiError thrown → caught in handlePasskeyLogin → shown in
 * the inline alert) is covered end-to-end without being short-circuited by a
 * mock.
 */
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { AuthProvider } from '@/common/contexts/AuthContext';
import { LoginModal } from '@/features/auth/components/LoginModal';
import type { ApiError } from '@/types/api';

// Passkey service: loginWithPasskey rejects with an ApiError — the real 401 path
vi.mock('@/services/passkeyService', () => ({
  loginWithPasskey: vi.fn(),
}));

// API client: getCurrentUser rejects (no active session on mount)
vi.mock('@/services/api', () => ({
  apiClient: {
    getCurrentUser: vi.fn().mockRejectedValue(new Error('no session')),
    login: vi.fn(),
    register: vi.fn(),
    logout: vi.fn(),
  },
}));

// ─── UI component stubs ──────────────────────────────────────────────────────
// (Same stubs as LoginModal.passkey.test.tsx — kept here so the integration
//  test is standalone and doesn't pull in real component tree deps.)

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

import { loginWithPasskey as mockPasskeyLogin } from '@/services/passkeyService';

function renderWithProvider(onClose = vi.fn(), onSwitchToRegister = vi.fn()) {
  return render(
    <AuthProvider>
      <LoginModal isOpen={true} onClose={onClose} onSwitchToRegister={onSwitchToRegister} />
    </AuthProvider>,
  );
}

describe('LoginModal + AuthProvider integration — passkey error propagation', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows an inline alert when the backend rejects assertion with ApiError (has details)', async () => {
    const apiError: ApiError = {
      message: 'Assertion failed',
      statusCode: 401,
      details: 'Credential ID not found',
    };
    vi.mocked(mockPasskeyLogin).mockRejectedValue(apiError);

    const onClose = vi.fn();
    renderWithProvider(onClose);

    fireEvent.change(screen.getByPlaceholderText(/enter your username or email/i), {
      target: { value: 'alice' },
    });
    fireEvent.click(screen.getByRole('button', { name: /sign in with passkey/i }));

    // LoginModal.handlePasskeyLogin uses apiErr?.details ?? apiErr?.message as
    // the error text shown in the role="alert" element.
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('Credential ID not found');
    });
    expect(onClose).not.toHaveBeenCalled();
  });

  it('shows an inline alert using the message fallback when ApiError has no details', async () => {
    const apiError: ApiError = {
      message: 'Authentication assertion failed',
      statusCode: 401,
    };
    vi.mocked(mockPasskeyLogin).mockRejectedValue(apiError);

    const onClose = vi.fn();
    renderWithProvider(onClose);

    fireEvent.change(screen.getByPlaceholderText(/enter your username or email/i), {
      target: { value: 'alice' },
    });
    fireEvent.click(screen.getByRole('button', { name: /sign in with passkey/i }));

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('Authentication assertion failed');
    });
    expect(onClose).not.toHaveBeenCalled();
  });
});
