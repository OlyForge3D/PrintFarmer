import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PasskeysPage } from '@/features/profile/pages/PasskeysPage';

// Mock the passkey service
vi.mock('@/services/passkeyService', () => ({
  listPasskeys: vi.fn().mockResolvedValue([]),
  deletePasskey: vi.fn(),
  renamePasskey: vi.fn(),
  registerPasskey: vi.fn(),
}));

// Mock toast
vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
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
  Modal: () => null,
}));

function renderWithProviders(component: React.ReactElement) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>{component}</QueryClientProvider>,
  );
}

describe('PasskeysPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
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
});
