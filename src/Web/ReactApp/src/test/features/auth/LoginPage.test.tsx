import React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { MemoryRouter, Route, Routes } from 'react-router';
import { LoginPage } from '@/features/auth/pages/LoginPage';

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    login: vi.fn().mockResolvedValue(false),
    loginWithPasskey: vi.fn().mockResolvedValue(false),
    register: vi.fn().mockResolvedValue(false),
    error: null,
  }),
}));

vi.mock('@/common/components/modals/Modal', () => ({
  Modal: ({
    isOpen,
    children,
  }: {
    isOpen: boolean;
    children: React.ReactNode;
    [key: string]: unknown;
  }) => (isOpen ? <div role="dialog">{children}</div> : null),
}));

vi.mock('@/common/components/skeletons/FormSkeleton', () => ({
  FormSkeleton: () => null,
}));

vi.mock('@/common/components/PrintFarmerLogo', () => ({
  PrintFarmerLogo: () => <span data-testid="printfarmer-logo" />,
}));

vi.mock('@/common/components/icons/MdiIcons', () => ({
  EyeIcon: () => <span data-testid="eye-icon" />,
  EyeOffIcon: () => <span data-testid="eye-off-icon" />,
  KeyIcon: () => <span data-testid="key-icon" />,
  LoginIcon: () => <span data-testid="login-icon" />,
  UserPlusIcon: () => <span data-testid="user-plus-icon" />,
  CloseIcon: () => <span data-testid="close-icon" />,
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
  }) => {
    const buttonProps = { ...rest } as Record<string, unknown>;
    delete buttonProps.iconLeft;
    delete buttonProps.iconRight;
    delete buttonProps.iconCenter;
    delete buttonProps.loading;

    return (
      <button type={(type as 'button' | 'submit' | 'reset') ?? 'button'} onClick={onClick} disabled={disabled} {...buttonProps}>
        {children}
      </button>
    );
  },
  Input: ({
    id,
    name,
    type,
    value,
    onChange,
    placeholder,
    ...rest
  }: {
    id?: string;
    name?: string;
    type?: string;
    value?: string;
    onChange?: (e: React.ChangeEvent<HTMLInputElement>) => void;
    placeholder?: string;
    [key: string]: unknown;
  }) => <input id={id} name={name} type={type} value={value} onChange={onChange} placeholder={placeholder} {...rest} />,
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

function renderLoginPage() {
  return render(
    <MemoryRouter initialEntries={['/login']}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('LoginPage', () => {
  it('renders the login route as a page card instead of a modal dialog', () => {
    renderLoginPage();

    expect(screen.getByRole('heading', { name: /sign in/i, level: 1 })).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /close sign in/i })).toBeInTheDocument();
  });

  it('switches to the register page card without rendering a modal dialog', () => {
    renderLoginPage();

    fireEvent.click(screen.getByRole('button', { name: /register/i }));

    expect(screen.getByRole('heading', { name: /create account/i, level: 1 })).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});
