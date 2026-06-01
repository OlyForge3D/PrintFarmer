import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { LoginModal } from '@/features/auth/components/LoginModal';

const mockLogin = vi.fn();
const mockLoginWithPasskey = vi.fn();

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    login: mockLogin,
    loginWithPasskey: mockLoginWithPasskey,
    error: null,
  }),
}));

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
    <button type={(type as 'button' | 'submit' | 'reset') ?? 'button'} onClick={onClick} disabled={disabled} {...rest}>
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
    <input id={id} type={type} value={value} onChange={onChange} placeholder={placeholder} {...rest} />
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
  Link: ({ children, to }: { children: React.ReactNode; to: string }) => <a href={to}>{children}</a>,
}));

function renderModal(onClose = vi.fn(), onSwitchToRegister = vi.fn()) {
  return render(
    <LoginModal isOpen={true} onClose={onClose} onSwitchToRegister={onSwitchToRegister} />,
  );
}

describe('LoginModal — passkey sign-in', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockLogin.mockResolvedValue(false);
    mockLoginWithPasskey.mockResolvedValue(false);
  });

  it('renders the "Sign in with passkey" button', () => {
    renderModal();
    expect(screen.getByRole('button', { name: /sign in with passkey/i })).toBeInTheDocument();
  });

  it('disables the passkey button when username is empty', () => {
    renderModal();
    const passkeyBtn = screen.getByRole('button', { name: /sign in with passkey/i });
    expect(passkeyBtn).toBeDisabled();
  });

  it('enables the passkey button when username is filled in', () => {
    renderModal();
    const usernameInput = screen.getByPlaceholderText(/enter your username or email/i);
    fireEvent.change(usernameInput, { target: { value: 'alice' } });
    const passkeyBtn = screen.getByRole('button', { name: /sign in with passkey/i });
    expect(passkeyBtn).not.toBeDisabled();
  });

  it('calls loginWithPasskey with the entered username on click', async () => {
    mockLoginWithPasskey.mockResolvedValue(false);
    renderModal();

    fireEvent.change(screen.getByPlaceholderText(/enter your username or email/i), {
      target: { value: 'alice' },
    });
    fireEvent.click(screen.getByRole('button', { name: /sign in with passkey/i }));

    await waitFor(() => {
      expect(mockLoginWithPasskey).toHaveBeenCalledWith('alice');
    });
  });

  it('calls onClose after a successful passkey login', async () => {
    mockLoginWithPasskey.mockResolvedValue(true);
    const onClose = vi.fn();
    renderModal(onClose);

    fireEvent.change(screen.getByPlaceholderText(/enter your username or email/i), {
      target: { value: 'alice' },
    });
    fireEvent.click(screen.getByRole('button', { name: /sign in with passkey/i }));

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });
  });

  it('does not call onClose after a failed passkey login', async () => {
    mockLoginWithPasskey.mockResolvedValue(false);
    const onClose = vi.fn();
    renderModal(onClose);

    fireEvent.change(screen.getByPlaceholderText(/enter your username or email/i), {
      target: { value: 'alice' },
    });
    fireEvent.click(screen.getByRole('button', { name: /sign in with passkey/i }));

    await waitFor(() => {
      expect(mockLoginWithPasskey).toHaveBeenCalled();
    });
    expect(onClose).not.toHaveBeenCalled();
  });

  it('shows an inline error when the passkey ceremony throws', async () => {
    mockLoginWithPasskey.mockRejectedValue(new Error('User cancelled'));
    renderModal();

    fireEvent.change(screen.getByPlaceholderText(/enter your username or email/i), {
      target: { value: 'alice' },
    });
    fireEvent.click(screen.getByRole('button', { name: /sign in with passkey/i }));

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('User cancelled');
    });
  });
});
