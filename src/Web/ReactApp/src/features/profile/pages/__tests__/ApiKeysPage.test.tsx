import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { ApiKeysPage } from '../ApiKeysPage';
import * as apiKeysService from '@/services/apiKeysService';

const { mockToast } = vi.hoisted(() => ({
  mockToast: { success: vi.fn(), error: vi.fn() },
}));

vi.mock('sonner', () => ({ toast: mockToast }));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: vi.fn(() => ({
    user: { id: 'user-1', email: 'test@example.com' },
    isAuthenticated: true,
    isLoading: false,
  })),
}));

vi.mock('@/services/apiKeysService', async () => {
  const actual = await vi.importActual<typeof apiKeysService>('@/services/apiKeysService');
  return {
    ...actual,
    listApiKeys: vi.fn(),
    createApiKey: vi.fn(),
    toggleApiKey: vi.fn(),
    deleteApiKey: vi.fn(),
    rotateApiKey: vi.fn(),
    revealApiKey: vi.fn(),
    getApiKeySettings: vi.fn(),
  };
});

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <ApiKeysPage />
    </QueryClientProvider>
  );
  return { ...utils, queryClient };
}

describe('ApiKeysPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiKeysService.getApiKeySettings).mockResolvedValue({ hashingEnabled: true });
    vi.mocked(apiKeysService.listApiKeys).mockResolvedValue([]);
  });

  it('should export ApiKeysPage component', () => {
    expect(ApiKeysPage).toBeDefined();
    expect(typeof ApiKeysPage).toBe('function');
  });

  it('should be a React component', () => {
    expect(ApiKeysPage.name).toBe('ApiKeysPage');
  });

  it('should render existing keys with purpose badges and expired indicator', async () => {
    vi.mocked(apiKeysService.listApiKeys).mockResolvedValue([
      {
        id: 'key-1',
        name: 'Slicer Key',
        isActive: true,
        createdAt: '2024-01-01T00:00:00Z',
        purpose: 'OctoPrint',
        scopes: 'None',
        isExpired: false,
      },
      {
        id: 'key-2',
        name: 'Old Desktop Key',
        isActive: true,
        createdAt: '2024-01-01T00:00:00Z',
        expiresAt: '2024-02-01T00:00:00Z',
        purpose: 'Desktop',
        scopes: 'ModelRead, LibrarySync',
        isExpired: true,
      },
    ]);

    renderPage();

    expect(await screen.findByText('Slicer Key')).toBeInTheDocument();
    expect(screen.getByText('Old Desktop Key')).toBeInTheDocument();
    expect(screen.getByText('OctoPrint')).toBeInTheDocument();
    expect(screen.getByText('Desktop')).toBeInTheDocument();
    expect(screen.getByText('Expired')).toBeInTheDocument();
  });

  it('should show optional expiry for every purpose and scopes only for Desktop', async () => {
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));

    expect(screen.queryByText(/Model Read/)).not.toBeInTheDocument();
    expect(screen.getByLabelText('Expires At')).toBeInTheDocument();

    const purposeSelect = screen.getByLabelText('Purpose');
    fireEvent.change(purposeSelect, { target: { value: 'Desktop' } });

    expect(screen.getByText(/Model Read/)).toBeInTheDocument();
    expect(screen.getByText(/Model Write/)).toBeInTheDocument();
    expect(screen.getByText(/Library Sync/)).toBeInTheDocument();
  });

  it('should require at least one scope before creating a Desktop-purpose key', async () => {
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));

    fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Desktop client' } });
    fireEvent.change(screen.getByLabelText('Purpose'), { target: { value: 'Desktop' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText(/Select at least one scope/i)).toBeInTheDocument();
    expect(apiKeysService.createApiKey).not.toHaveBeenCalled();
  });

  it('should create a Desktop-purpose key with selected scopes', async () => {
    vi.mocked(apiKeysService.createApiKey).mockResolvedValue({ key: 'raw-key', id: 'new-id' });

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));

    fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Desktop client' } });
    fireEvent.change(screen.getByLabelText('Purpose'), { target: { value: 'Desktop' } });
    fireEvent.click(screen.getByLabelText(/Model Read/));
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => {
      expect(apiKeysService.createApiKey).toHaveBeenCalledWith(
        'user-1',
        expect.objectContaining({ name: 'Desktop client', purpose: 'Desktop', scopes: 'ModelRead' })
      );
    });
    expect(await screen.findByText('raw-key')).toBeInTheDocument();
    expect(screen.getByText(/won't be able to see it again/i)).toBeInTheDocument();
  });

  it('should create an OctoPrint-purpose key with an optional expiry', async () => {
    vi.mocked(apiKeysService.createApiKey).mockResolvedValue({ key: 'raw-key', id: 'new-id' });
    const tomorrow = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString().slice(0, 16);

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
    fireEvent.change(screen.getByLabelText(/Name/), { target: { value: 'Expiring slicer' } });
    fireEvent.change(screen.getByLabelText('Expires At'), { target: { value: tomorrow } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => {
      expect(apiKeysService.createApiKey).toHaveBeenCalledWith(
        'user-1',
        expect.objectContaining({
          name: 'Expiring slicer',
          purpose: 'OctoPrint',
          expiresAt: expect.any(String),
        })
      );
    });
  });

  it('should offer legacy reveal only for OctoPrint keys when hashing is disabled', async () => {
    vi.mocked(apiKeysService.getApiKeySettings).mockResolvedValue({ hashingEnabled: false });
    vi.mocked(apiKeysService.listApiKeys).mockResolvedValue([
      {
        id: 'octoprint-key',
        name: 'Slicer Key',
        isActive: true,
        createdAt: '2024-01-01T00:00:00Z',
        purpose: 'OctoPrint',
        scopes: 'None',
        isExpired: false,
      },
      {
        id: 'desktop-key',
        name: 'Desktop Key',
        isActive: true,
        createdAt: '2024-01-01T00:00:00Z',
        expiresAt: '2026-01-01T00:00:00Z',
        purpose: 'Desktop',
        scopes: 'ModelRead',
        isExpired: false,
      },
    ]);
    vi.mocked(apiKeysService.revealApiKey).mockResolvedValue({ key: 'legacy-secret' });

    renderPage();

    const octoPrintReveal = await screen.findByRole('button', { name: 'Reveal API key Slicer Key' });
    expect(screen.queryByRole('button', { name: 'Reveal API key Desktop Key' })).not.toBeInTheDocument();

    fireEvent.click(octoPrintReveal);

    await waitFor(() => {
      expect(apiKeysService.revealApiKey).toHaveBeenCalledWith('user-1', 'octoprint-key');
    });
    expect(await screen.findByText('legacy-secret')).toBeInTheDocument();
  });

  it('should permanently dismiss the one-time key display when Done is clicked, never re-showing the secret', async () => {
    vi.mocked(apiKeysService.createApiKey).mockResolvedValue({ key: 'one-time-secret', id: 'new-id' });

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
    fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Desktop client' } });
    fireEvent.change(screen.getByLabelText('Purpose'), { target: { value: 'Desktop' } });
    fireEvent.click(screen.getByLabelText(/Model Read/));
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText('one-time-secret')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Done' }));

    // The one-time secret must be gone from the DOM entirely, not merely masked, since the
    // component holds no other reference to the raw key after dismissal.
    expect(screen.queryByText('one-time-secret')).not.toBeInTheDocument();
  });

  it('should show the newly rotated key as a fresh one-time secret, replacing any prior display', async () => {
    vi.mocked(apiKeysService.listApiKeys).mockResolvedValue([
      {
        id: 'key-1',
        name: 'Slicer Key',
        isActive: true,
        createdAt: '2024-01-01T00:00:00Z',
        purpose: 'OctoPrint',
        scopes: 'None',
        isExpired: false,
      },
    ]);
    vi.mocked(apiKeysService.rotateApiKey).mockResolvedValue({ key: 'rotated-secret', id: 'key-1' });
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Rotate API key Slicer Key' }));

    await waitFor(() => {
      expect(apiKeysService.rotateApiKey).toHaveBeenCalledWith('user-1', 'key-1');
    });
    expect(await screen.findByText('rotated-secret')).toBeInTheDocument();
    expect(screen.getByText(/won't be able to see it again/i)).toBeInTheDocument();

    confirmSpy.mockRestore();
  });

  it('should move focus to the one-time secret panel when it appears and restore focus to the trigger on dismissal', async () => {
    vi.mocked(apiKeysService.createApiKey).mockResolvedValue({ key: 'focus-secret', id: 'new-id' });

    renderPage();

    const createButton = await screen.findByRole('button', { name: /Create New API Key/i });
    fireEvent.click(createButton);

    fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Desktop client' } });
    fireEvent.change(screen.getByLabelText('Purpose'), { target: { value: 'Desktop' } });
    fireEvent.click(screen.getByLabelText(/Model Read/));

    const submitButton = screen.getByRole('button', { name: 'Create' });
    submitButton.focus();
    fireEvent.click(submitButton);

    const panel = await screen.findByRole('status', { name: 'One-time API key secret' });
    await waitFor(() => expect(panel).toHaveFocus());

    fireEvent.click(screen.getByRole('button', { name: 'Done' }));

    const recreatedButton = await screen.findByRole('button', { name: /Create New API Key/i });
    await waitFor(() => expect(recreatedButton).toHaveFocus());
  });

  it('should copy the one-time secret via the toast notification pattern rather than a blocking alert', async () => {
    vi.mocked(apiKeysService.createApiKey).mockResolvedValue({ key: 'clipboard-secret', id: 'new-id' });
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const alertSpy = vi.spyOn(window, 'alert');

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
    fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Desktop client' } });
    fireEvent.change(screen.getByLabelText('Purpose'), { target: { value: 'Desktop' } });
    fireEvent.click(screen.getByLabelText(/Model Read/));
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText('clipboard-secret')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Copy to Clipboard' }));

    await waitFor(() => expect(writeText).toHaveBeenCalledWith('clipboard-secret'));
    await waitFor(() => expect(mockToast.success).toHaveBeenCalledWith('API key copied to clipboard'));
    expect(alertSpy).not.toHaveBeenCalled();

    alertSpy.mockRestore();
  });

  it('should surface a toast error and never fall back to alert when the clipboard write fails', async () => {
    vi.mocked(apiKeysService.createApiKey).mockResolvedValue({ key: 'clipboard-fail-secret', id: 'new-id' });
    const writeText = vi.fn().mockRejectedValue(new Error('denied'));
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const alertSpy = vi.spyOn(window, 'alert');

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
    fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Desktop client' } });
    fireEvent.change(screen.getByLabelText('Purpose'), { target: { value: 'Desktop' } });
    fireEvent.click(screen.getByLabelText(/Model Read/));
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText('clipboard-fail-secret')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Copy to Clipboard' }));

    await waitFor(() => expect(mockToast.error).toHaveBeenCalledWith(
      'Could not copy API key automatically. Please select and copy it manually.'
    ));
    expect(alertSpy).not.toHaveBeenCalled();

    alertSpy.mockRestore();
  });

  it('should reject an expiry date in the past before calling createApiKey', async () => {
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
    fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Past expiry key' } });
    const pastDate = new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString().slice(0, 16);
    fireEvent.change(screen.getByLabelText('Expires At'), { target: { value: pastDate } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText(/expiry must be in the future/i)).toBeInTheDocument();
    expect(apiKeysService.createApiKey).not.toHaveBeenCalled();
  });

  it('should reject an expiry date beyond the 365-day maximum before calling createApiKey', async () => {
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
    fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Too-far expiry key' } });
    const tooFar = new Date(Date.now() + 400 * 24 * 60 * 60 * 1000).toISOString().slice(0, 16);
    fireEvent.change(screen.getByLabelText('Expires At'), { target: { value: tooFar } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText(/cannot exceed 365 days/i)).toBeInTheDocument();
    expect(apiKeysService.createApiKey).not.toHaveBeenCalled();
  });

  it('should never leak the one-time secret into the React Query cache or browser storage', async () => {
    vi.mocked(apiKeysService.createApiKey).mockResolvedValue({ key: 'no-leak-secret', id: 'new-id' });

    const { queryClient } = renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
    fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Desktop client' } });
    fireEvent.change(screen.getByLabelText('Purpose'), { target: { value: 'Desktop' } });
    fireEvent.click(screen.getByLabelText(/Model Read/));
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText('no-leak-secret')).toBeInTheDocument();

    // The secret must never be written into any React Query cache entry, and must never be
    // persisted to localStorage/sessionStorage (only ever held in transient component state).
    const cacheJson = JSON.stringify(queryClient.getQueryCache().getAll().map((q) => q.state.data));
    expect(cacheJson).not.toContain('no-leak-secret');
    expect(JSON.stringify(window.localStorage)).not.toContain('no-leak-secret');
    for (let i = 0; i < window.localStorage.length; i++) {
      const key = window.localStorage.key(i);
      expect(key && window.localStorage.getItem(key)).not.toContain('no-leak-secret');
    }
    for (let i = 0; i < window.sessionStorage.length; i++) {
      const key = window.sessionStorage.key(i);
      expect(key && window.sessionStorage.getItem(key)).not.toContain('no-leak-secret');
    }

    fireEvent.click(screen.getByRole('button', { name: 'Done' }));
    expect(screen.queryByText('no-leak-secret')).not.toBeInTheDocument();
  });

  it('should surface a generic error and never render a secret panel when key creation is rejected as unauthorized', async () => {
    vi.mocked(apiKeysService.createApiKey).mockRejectedValue(new Error('Request failed with status code 403'));

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
    fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Forbidden client' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText('Request failed with status code 403')).toBeInTheDocument();
    expect(screen.queryByRole('status', { name: 'One-time API key secret' })).not.toBeInTheDocument();
  });

  it('should surface an error when toggling (revoking) a key is rejected by the server', async () => {
    vi.mocked(apiKeysService.listApiKeys).mockResolvedValue([
      {
        id: 'key-1',
        name: 'Slicer Key',
        isActive: true,
        createdAt: '2024-01-01T00:00:00Z',
        purpose: 'OctoPrint',
        scopes: 'None',
        isExpired: false,
      },
    ]);
    vi.mocked(apiKeysService.toggleApiKey).mockRejectedValue(new Error('Request failed with status code 403'));

    renderPage();

    const toggle = await screen.findByRole('checkbox', { name: /Disable API key Slicer Key/i });
    fireEvent.click(toggle);

    expect(await screen.findByText('Request failed with status code 403')).toBeInTheDocument();
  });

  it('should surface an error and never display a secret when rotate is rejected by the server', async () => {
    vi.mocked(apiKeysService.listApiKeys).mockResolvedValue([
      {
        id: 'key-1',
        name: 'Slicer Key',
        isActive: true,
        createdAt: '2024-01-01T00:00:00Z',
        purpose: 'OctoPrint',
        scopes: 'None',
        isExpired: false,
      },
    ]);
    vi.mocked(apiKeysService.rotateApiKey).mockRejectedValue(new Error('Request failed with status code 401'));
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Rotate API key Slicer Key' }));

    expect(await screen.findByText('Request failed with status code 401')).toBeInTheDocument();
    expect(screen.queryByRole('status', { name: 'One-time API key secret' })).not.toBeInTheDocument();

    confirmSpy.mockRestore();
  });
});
