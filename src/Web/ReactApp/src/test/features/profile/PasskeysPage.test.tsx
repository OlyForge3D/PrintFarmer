import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PasskeysPage } from '@/features/profile/pages/PasskeysPage';
import {
  listPasskeys,
  registerPasskey,
  renamePasskey,
} from '@/services/passkeyService';
import { toast } from 'sonner';

// Mock the passkey service
vi.mock('@/services/passkeyService', () => ({
  listPasskeys: vi.fn().mockResolvedValue([]),
  deletePasskey: vi.fn(),
  renamePasskey: vi.fn(),
  registerPasskey: vi.fn(),
}));

// Mock toast
vi.mock('sonner', () => ({
  toast: {
    loading: vi.fn().mockReturnValue('registration-toast'),
    success: vi.fn(),
    error: vi.fn(),
  },
}));

// Mock PageTemplate to render children directly
vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));

// Mock icons
vi.mock('@/common/components/icons/MdiIcons', () => ({
  KeyIcon: () => <span data-testid="key-icon" />,
  PlusIcon: () => <span data-testid="plus-icon" />,
  DeleteIcon: () => <span data-testid="delete-icon" />,
  EditIcon: () => <span data-testid="edit-icon" />,
}));

// Mock Modal
vi.mock('@/common/components/modals/Modal', () => ({
  Modal: ({
    isOpen,
    title,
    footer,
    children,
  }: {
    isOpen: boolean;
    title: string;
    footer: React.ReactNode;
    children: React.ReactNode;
  }) =>
    isOpen ? (
      <div role="dialog" aria-label={title}>
        {children}
        {footer}
      </div>
    ) : null,
}));

function renderWithProviders(component: React.ReactElement) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  const result = render(
    <QueryClientProvider client={queryClient}>{component}</QueryClientProvider>,
  );
  return { ...result, queryClient };
}

describe('PasskeysPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(listPasskeys).mockResolvedValue([]);
  });

  it('renders the "Add passkey" button without navigating to a dead route', () => {
    renderWithProviders(<PasskeysPage />);
    const addButton = screen.getByRole('button', { name: /add passkey/i });
    expect(addButton).toBeInTheDocument();
    // The button must NOT contain a link or navigate via href to a nonexistent route
    expect(addButton.closest('a')).toBeNull();
  });

  it('does not contain any links to /profile/passkeys/register', () => {
    const { container } = renderWithProviders(<PasskeysPage />);
    const links = container.querySelectorAll('a[href*="passkeys/register"]');
    expect(links.length).toBe(0);
  });

  it('"Add passkey" button triggers registration (not navigation)', () => {
    renderWithProviders(<PasskeysPage />);
    const addButton = screen.getByRole('button', { name: /add passkey/i });
    // Verify it's a real button (not anchor disguised as button)
    expect(addButton.tagName).toBe('BUTTON');
  });

  it('dismisses the modal before starting the browser registration ceremony', async () => {
    vi.mocked(registerPasskey).mockImplementation(async () => {
      expect(screen.queryByRole('dialog', { name: 'Add passkey' })).not.toBeInTheDocument();
      return { credentialId: 'credential-1', newCredentialId: 1 };
    });

    renderWithProviders(<PasskeysPage />);
    fireEvent.click(screen.getByRole('button', { name: /add passkey/i }));
    fireEvent.change(screen.getByLabelText('Device name (optional)'), {
      target: { value: '  Edge laptop  ' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Register passkey' }));

    expect(screen.queryByRole('dialog', { name: 'Add passkey' })).not.toBeInTheDocument();
    expect(toast.loading).toHaveBeenCalledWith('Complete the passkey prompt in your browser');

    await waitFor(() => expect(registerPasskey).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(renamePasskey).toHaveBeenCalledWith(1, 'Edge laptop'));
  });

  it('shows success feedback and refreshes the passkey list', async () => {
    vi.mocked(registerPasskey).mockResolvedValue({
      credentialId: 'credential-1',
      newCredentialId: 1,
    });
    const { queryClient } = renderWithProviders(<PasskeysPage />);
    const invalidateQueries = vi.spyOn(queryClient, 'invalidateQueries');

    fireEvent.click(screen.getByRole('button', { name: /add passkey/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Register passkey' }));

    await waitFor(() =>
      expect(toast.success).toHaveBeenCalledWith('Passkey registered successfully', {
        id: 'registration-toast',
      }),
    );
    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['passkeys'] });
  });

  it('shows failure feedback outside the dismissed modal', async () => {
    vi.mocked(registerPasskey).mockRejectedValue(new Error('Credential creation was cancelled'));

    renderWithProviders(<PasskeysPage />);
    fireEvent.click(screen.getByRole('button', { name: /add passkey/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Register passkey' }));

    expect(screen.queryByRole('dialog', { name: 'Add passkey' })).not.toBeInTheDocument();
    await waitFor(() =>
      expect(toast.error).toHaveBeenCalledWith('Credential creation was cancelled', {
        id: 'registration-toast',
      }),
    );
  });

  it('allows the modal to reopen and retry after a failed registration', async () => {
    vi.mocked(registerPasskey)
      .mockRejectedValueOnce(new Error('Try again'))
      .mockResolvedValueOnce({ credentialId: 'credential-2', newCredentialId: 2 });

    renderWithProviders(<PasskeysPage />);
    fireEvent.click(screen.getByRole('button', { name: /add passkey/i }));
    fireEvent.change(screen.getByLabelText('Device name (optional)'), {
      target: { value: 'LastPass key' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Register passkey' }));

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('button', { name: /add passkey/i }));

    expect(screen.getByRole('dialog', { name: 'Add passkey' })).toBeInTheDocument();
    expect(screen.getByLabelText('Device name (optional)')).toHaveValue('LastPass key');

    fireEvent.click(screen.getByRole('button', { name: 'Register passkey' }));

    await waitFor(() => expect(registerPasskey).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(renamePasskey).toHaveBeenCalledWith(2, 'LastPass key'));
  });
});
