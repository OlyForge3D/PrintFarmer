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

/** Creates a promise whose resolution/rejection is controlled externally, for testing races. */
function createDeferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

async function openDesktopCreateFormWithScope(name: string) {
  fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
  fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: name } });
  fireEvent.change(screen.getByLabelText('Purpose'), { target: { value: 'Desktop' } });
  fireEvent.click(screen.getByLabelText(/Model Read/));
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

  it('should not allow expired keys to be rotated', async () => {
    vi.mocked(apiKeysService.listApiKeys).mockResolvedValue([
      {
        id: 'expired-key',
        name: 'Expired Desktop Key',
        isActive: true,
        createdAt: '2024-01-01T00:00:00Z',
        expiresAt: '2024-02-01T00:00:00Z',
        purpose: 'Desktop',
        scopes: 'ModelRead',
        isExpired: true,
      },
    ]);

    renderPage();

    const rotateButton = await screen.findByRole('button', { name: 'Rotate API key Expired Desktop Key' });
    expect(rotateButton).toBeDisabled();
    expect(rotateButton).toHaveAttribute('title', 'Expired API keys cannot be rotated');

    fireEvent.click(rotateButton);

    expect(apiKeysService.rotateApiKey).not.toHaveBeenCalled();
  });

  it('should allow valid expiring and non-expiring keys to be rotated', async () => {
    vi.mocked(apiKeysService.listApiKeys).mockResolvedValue([
      {
        id: 'valid-expiring-key',
        name: 'Valid Desktop Key',
        isActive: true,
        createdAt: '2026-01-01T00:00:00Z',
        expiresAt: '2099-01-01T00:00:00Z',
        purpose: 'Desktop',
        scopes: 'ModelRead',
        isExpired: false,
      },
      {
        id: 'non-expiring-key',
        name: 'Non-expiring Slicer Key',
        isActive: true,
        createdAt: '2026-01-01T00:00:00Z',
        purpose: 'OctoPrint',
        scopes: 'None',
        isExpired: false,
      },
    ]);
    vi.mocked(apiKeysService.rotateApiKey).mockResolvedValue({ key: 'rotated-key', id: 'rotated-id' });
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);

    renderPage();

    const validExpiringRotateButton = await screen.findByRole('button', { name: 'Rotate API key Valid Desktop Key' });
    const nonExpiringRotateButton = screen.getByRole('button', { name: 'Rotate API key Non-expiring Slicer Key' });

    expect(validExpiringRotateButton).toBeEnabled();
    expect(nonExpiringRotateButton).toBeEnabled();

    fireEvent.click(validExpiringRotateButton);
    await waitFor(() => {
      expect(apiKeysService.rotateApiKey).toHaveBeenCalledWith('user-1', 'valid-expiring-key');
    });
    expect(await screen.findByText('rotated-key')).toBeInTheDocument();
    expect(nonExpiringRotateButton).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Done' }));
    await waitFor(() => expect(nonExpiringRotateButton).toBeEnabled());

    fireEvent.click(nonExpiringRotateButton);
    await waitFor(() => {
      expect(apiKeysService.rotateApiKey).toHaveBeenCalledWith('user-1', 'non-expiring-key');
    });

    confirmSpy.mockRestore();
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

    const panel = await screen.findByRole('status', { name: 'API Key Created Successfully' });
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

    expect(await screen.findByText(/expiry must be in the future|date and time in the future/i)).toBeInTheDocument();
    expect(apiKeysService.createApiKey).not.toHaveBeenCalled();
  });

  it('should reject an expiry date beyond the 365-day maximum before calling createApiKey', async () => {
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
    fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Too-far expiry key' } });
    const tooFar = new Date(Date.now() + 400 * 24 * 60 * 60 * 1000).toISOString().slice(0, 16);
    fireEvent.change(screen.getByLabelText('Expires At'), { target: { value: tooFar } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText(/cannot exceed 365 days|no more than 365 days/i)).toBeInTheDocument();
    expect(apiKeysService.createApiKey).not.toHaveBeenCalled();
  });

  it('should never leak the one-time secret into the React Query query or mutation cache, before, during, or after display', async () => {
    vi.mocked(apiKeysService.createApiKey).mockResolvedValue({ key: 'no-leak-secret', id: 'new-id' });

    const { queryClient } = renderPage();

    const serializeCaches = () =>
      JSON.stringify([
        ...queryClient.getQueryCache().getAll().map((q) => q.state.data),
        ...queryClient.getMutationCache().getAll().map((m) => m.state.data),
      ]);

    // Before the secret is ever created/revealed.
    expect(serializeCaches()).not.toContain('no-leak-secret');

    await openDesktopCreateFormWithScope('Desktop client');
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText('no-leak-secret')).toBeInTheDocument();

    // While the sentinel secret is visible on screen, it must still be absent from every
    // query AND mutation cache entry — the mutationFn must never resolve with the raw
    // secret, so mutation.state.data can never contain it, not even briefly.
    expect(serializeCaches()).not.toContain('no-leak-secret');
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

    // After dismissal, still absent everywhere.
    expect(serializeCaches()).not.toContain('no-leak-secret');
  });

  it('should never leak a rotated one-time secret into the React Query query or mutation cache', async () => {
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
    vi.mocked(apiKeysService.rotateApiKey).mockResolvedValue({ key: 'rotate-no-leak-secret', id: 'key-1' });
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);

    const { queryClient } = renderPage();
    const serializeCaches = () =>
      JSON.stringify([
        ...queryClient.getQueryCache().getAll().map((q) => q.state.data),
        ...queryClient.getMutationCache().getAll().map((m) => m.state.data),
      ]);

    fireEvent.click(await screen.findByRole('button', { name: 'Rotate API key Slicer Key' }));

    expect(await screen.findByText('rotate-no-leak-secret')).toBeInTheDocument();
    expect(serializeCaches()).not.toContain('rotate-no-leak-secret');

    fireEvent.click(screen.getByRole('button', { name: 'Done' }));
    expect(serializeCaches()).not.toContain('rotate-no-leak-secret');

    confirmSpy.mockRestore();
  });

  it('should fail safely with a generic message and no secret panel or cache leak when the create response is missing the secret', async () => {
    vi.mocked(apiKeysService.createApiKey).mockResolvedValue({ key: '', id: 'new-id' });

    const { queryClient } = renderPage();

    await openDesktopCreateFormWithScope('Malformed client');
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText(/missing required API key data/i)).toBeInTheDocument();
    expect(screen.queryByRole('status', { name: 'API Key Created Successfully' })).not.toBeInTheDocument();
    const cacheJson = JSON.stringify([
      ...queryClient.getQueryCache().getAll().map((q) => q.state.data),
      ...queryClient.getMutationCache().getAll().map((m) => m.state.data),
    ]);
    expect(cacheJson).not.toContain('new-id');
  });

  it('should fail safely with a generic message and no secret panel when the create response is missing the id', async () => {
    vi.mocked(apiKeysService.createApiKey).mockResolvedValue({ key: 'orphan-secret', id: '' });

    renderPage();

    await openDesktopCreateFormWithScope('Malformed client');
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText(/missing required API key data/i)).toBeInTheDocument();
    expect(screen.queryByText('orphan-secret')).not.toBeInTheDocument();
    expect(screen.queryByRole('status', { name: 'API Key Created Successfully' })).not.toBeInTheDocument();
  });

  it('should fail safely with a generic message and no secret panel when the rotate response is malformed', async () => {
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
    vi.mocked(apiKeysService.rotateApiKey).mockResolvedValue({ key: '', id: 'key-1' });
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Rotate API key Slicer Key' }));

    expect(await screen.findByText(/missing required API key data/i)).toBeInTheDocument();
    expect(screen.queryByRole('status', { name: 'API Key Created Successfully' })).not.toBeInTheDocument();

    confirmSpy.mockRestore();
  });

  it('should surface a generic error and never render a secret panel when key creation is rejected as unauthorized', async () => {
    vi.mocked(apiKeysService.createApiKey).mockRejectedValue(new Error('Request failed with status code 403'));

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
    fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Forbidden client' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText('Request failed with status code 403')).toBeInTheDocument();
    expect(screen.queryByRole('status', { name: 'API Key Created Successfully' })).not.toBeInTheDocument();
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
    expect(screen.queryByRole('status', { name: 'API Key Created Successfully' })).not.toBeInTheDocument();

    confirmSpy.mockRestore();
  });

  describe('single secret-operation lock', () => {
    it('should prevent a second create from starting while the first create is still pending', async () => {
      const deferred = createDeferred<{ key: string; id: string }>();
      vi.mocked(apiKeysService.createApiKey).mockReturnValue(deferred.promise);

      renderPage();

      await openDesktopCreateFormWithScope('Locked client');
      const submitButton = screen.getByRole('button', { name: 'Create' });
      fireEvent.click(submitButton);
      fireEvent.click(submitButton);

      await waitFor(() => expect(apiKeysService.createApiKey).toHaveBeenCalledTimes(1));

      deferred.resolve({ key: 'locked-secret', id: 'new-id' });
      expect(await screen.findByText('locked-secret')).toBeInTheDocument();
    });

    it('should prevent rotate from starting while a create is pending', async () => {
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
      const deferred = createDeferred<{ key: string; id: string }>();
      vi.mocked(apiKeysService.createApiKey).mockReturnValue(deferred.promise);
      const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);

      renderPage();

      await openDesktopCreateFormWithScope('Locked client');
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));

      const rotateButton = await screen.findByRole('button', { name: 'Rotate API key Slicer Key' });
      fireEvent.click(rotateButton);

      expect(confirmSpy).not.toHaveBeenCalled();
      expect(apiKeysService.rotateApiKey).not.toHaveBeenCalled();

      deferred.resolve({ key: 'locked-secret', id: 'new-id' });
      await screen.findByText('locked-secret');

      confirmSpy.mockRestore();
    });

    it('should prevent create and another rotate from starting while a rotate is pending', async () => {
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
          name: 'Other Key',
          isActive: true,
          createdAt: '2024-01-01T00:00:00Z',
          purpose: 'OctoPrint',
          scopes: 'None',
          isExpired: false,
        },
      ]);
      const deferred = createDeferred<{ key: string; id: string }>();
      vi.mocked(apiKeysService.rotateApiKey).mockReturnValue(deferred.promise);
      const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);

      renderPage();

      const rotateButton1 = await screen.findByRole('button', { name: 'Rotate API key Slicer Key' });
      fireEvent.click(rotateButton1);
      await waitFor(() => expect(apiKeysService.rotateApiKey).toHaveBeenCalledTimes(1));

      const rotateButton2 = screen.getByRole('button', { name: 'Rotate API key Other Key' });
      fireEvent.click(rotateButton2);
      expect(apiKeysService.rotateApiKey).toHaveBeenCalledTimes(1);

      const createEntryPoint = screen.getByRole('button', { name: /Create New API Key/i });
      expect(createEntryPoint).toBeDisabled();
      fireEvent.click(createEntryPoint);
      expect(screen.queryByPlaceholderText(/descriptive name/i)).not.toBeInTheDocument();
      expect(apiKeysService.createApiKey).not.toHaveBeenCalled();

      deferred.resolve({ key: 'rotate-locked-secret', id: 'key-1' });
      await screen.findByText('rotate-locked-secret');

      confirmSpy.mockRestore();
    });

    it('should prevent a second secret-generating operation while the first secret is displayed, keeping it unchanged until Done', async () => {
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
      vi.mocked(apiKeysService.createApiKey).mockResolvedValue({ key: 'first-secret', id: 'new-id' });
      const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);

      renderPage();

      await openDesktopCreateFormWithScope('First client');
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));

      expect(await screen.findByText('first-secret')).toBeInTheDocument();

      const rotateButton = screen.getByRole('button', { name: 'Rotate API key Slicer Key' });
      expect(rotateButton).toBeDisabled();
      fireEvent.click(rotateButton);
      expect(apiKeysService.rotateApiKey).not.toHaveBeenCalled();
      expect(confirmSpy).not.toHaveBeenCalled();

      const createEntryPoint = screen.getByRole('button', { name: /Create New API Key/i });
      expect(createEntryPoint).toBeDisabled();

      expect(screen.getByText('first-secret')).toBeInTheDocument();

      fireEvent.click(screen.getByRole('button', { name: 'Done' }));
      expect(screen.queryByText('first-secret')).not.toBeInTheDocument();

      confirmSpy.mockRestore();
    });
  });

  describe('secret region accessibility', () => {
    it('should expose a useful accessible name/description while the secret is shown, and remove it entirely after Done', async () => {
      vi.mocked(apiKeysService.createApiKey).mockResolvedValue({ key: 'a11y-secret', id: 'new-id' });

      renderPage();

      await openDesktopCreateFormWithScope('A11y client');
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));

      const panel = await screen.findByRole('status', { name: 'API Key Created Successfully' });
      expect(panel).not.toHaveAttribute('aria-label');
      expect(panel).toHaveAttribute('aria-labelledby', 'created-key-heading');

      const describedByIds = (panel.getAttribute('aria-describedby') ?? '').split(' ').filter(Boolean);
      expect(describedByIds.length).toBeGreaterThan(0);
      const describedText = describedByIds.map((id) => document.getElementById(id)?.textContent ?? '').join(' ');
      expect(describedText).toMatch(/won't be able to see it again/i);
      expect(describedText).toContain('a11y-secret');

      fireEvent.click(screen.getByRole('button', { name: 'Done' }));

      expect(screen.queryByRole('status', { name: 'API Key Created Successfully' })).not.toBeInTheDocument();
      expect(document.getElementById('created-key-heading')).not.toBeInTheDocument();
      expect(document.getElementById('created-key-warning')).not.toBeInTheDocument();
      expect(document.getElementById('created-key-value')).not.toBeInTheDocument();
    });
  });

  describe('field validation accessibility', () => {
    it('should associate and focus the name field when the name is missing', async () => {
      renderPage();

      fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));

      const nameInput = screen.getByPlaceholderText(/descriptive name/i);
      expect(await screen.findByText(/enter a name/i)).toBeInTheDocument();
      expect(nameInput).toHaveAttribute('aria-invalid', 'true');
      expect(nameInput.getAttribute('aria-describedby')).toBeTruthy();
      await waitFor(() => expect(nameInput).toHaveFocus());
    });

    it('should associate and focus the scopes group when no Desktop scope is selected', async () => {
      renderPage();

      fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
      fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Desktop client' } });
      fireEvent.change(screen.getByLabelText('Purpose'), { target: { value: 'Desktop' } });
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));

      expect(await screen.findByText(/select at least one scope/i)).toBeInTheDocument();
      const scopesGroup = screen.getByRole('group', { name: /Scopes/i });
      expect(scopesGroup).toHaveAttribute('aria-invalid', 'true');
      expect(scopesGroup).toHaveAttribute('tabindex', '-1');
      expect(scopesGroup.getAttribute('aria-describedby')).toBeTruthy();
      await waitFor(() => expect(scopesGroup).toHaveFocus());
    });

    it('should associate and focus the expiry field for a past date', async () => {
      renderPage();

      fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
      fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Past expiry key' } });
      const pastDate = new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString().slice(0, 16);
      fireEvent.change(screen.getByLabelText('Expires At'), { target: { value: pastDate } });
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));

      const expiryInput = screen.getByLabelText('Expires At');
      expect(await screen.findByText(/date and time in the future/i)).toBeInTheDocument();
      expect(expiryInput).toHaveAttribute('aria-invalid', 'true');
      expect(expiryInput.getAttribute('aria-describedby')).toBeTruthy();
      await waitFor(() => expect(expiryInput).toHaveFocus());
    });

    it('should associate and focus the expiry field for an expiry beyond 365 days', async () => {
      renderPage();

      fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
      fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Too-far expiry key' } });
      const tooFar = new Date(Date.now() + 400 * 24 * 60 * 60 * 1000).toISOString().slice(0, 16);
      fireEvent.change(screen.getByLabelText('Expires At'), { target: { value: tooFar } });
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));

      const expiryInput = screen.getByLabelText('Expires At');
      expect(await screen.findByText(/no more than 365 days/i)).toBeInTheDocument();
      await waitFor(() => expect(expiryInput).toHaveFocus());
    });

    it('should focus the first invalid field in DOM order when name, scopes, and expiry are all invalid', async () => {
      renderPage();

      fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
      fireEvent.change(screen.getByLabelText('Purpose'), { target: { value: 'Desktop' } });
      const tooFar = new Date(Date.now() + 400 * 24 * 60 * 60 * 1000).toISOString().slice(0, 16);
      fireEvent.change(screen.getByLabelText('Expires At'), { target: { value: tooFar } });
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));

      expect(await screen.findByText(/enter a name/i)).toBeInTheDocument();
      expect(screen.getByText(/select at least one scope/i)).toBeInTheDocument();
      expect(screen.getByText(/no more than 365 days/i)).toBeInTheDocument();

      const nameInput = screen.getByPlaceholderText(/descriptive name/i);
      await waitFor(() => expect(nameInput).toHaveFocus());
    });

    it('should clear the name error as soon as the user edits the name', async () => {
      renderPage();

      fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));
      expect(await screen.findByText(/enter a name/i)).toBeInTheDocument();

      fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Now valid' } });
      expect(screen.queryByText(/enter a name/i)).not.toBeInTheDocument();
    });

    it('should clear the scopes error when a scope is selected, and when purpose changes away from Desktop', async () => {
      renderPage();

      fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
      fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Desktop client' } });
      fireEvent.change(screen.getByLabelText('Purpose'), { target: { value: 'Desktop' } });
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));
      expect(await screen.findByText(/select at least one scope/i)).toBeInTheDocument();

      fireEvent.click(screen.getByLabelText(/Model Read/));
      expect(screen.queryByText(/select at least one scope/i)).not.toBeInTheDocument();
    });

    it('should clear the expiry error when the expiry value changes', async () => {
      renderPage();

      fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
      fireEvent.change(screen.getByPlaceholderText(/descriptive name/i), { target: { value: 'Past expiry key' } });
      const pastDate = new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString().slice(0, 16);
      fireEvent.change(screen.getByLabelText('Expires At'), { target: { value: pastDate } });
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));
      expect(await screen.findByText(/date and time in the future/i)).toBeInTheDocument();

      const tomorrow = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString().slice(0, 16);
      fireEvent.change(screen.getByLabelText('Expires At'), { target: { value: tomorrow } });
      expect(screen.queryByText(/date and time in the future/i)).not.toBeInTheDocument();
    });

    it('should clear all field errors when the form is reset via Cancel', async () => {
      renderPage();

      fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));
      expect(await screen.findByText(/enter a name/i)).toBeInTheDocument();

      fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
      fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));

      expect(screen.queryByText(/enter a name/i)).not.toBeInTheDocument();
    });
  });

  describe('rotate focus hardening', () => {
    it('should restore focus to the same rotate button after Done when the row still exists', async () => {
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
      vi.mocked(apiKeysService.rotateApiKey).mockResolvedValue({ key: 'rotate-focus-secret', id: 'key-1' });
      const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);

      renderPage();

      const rotateButton = await screen.findByRole('button', { name: 'Rotate API key Slicer Key' });
      fireEvent.click(rotateButton);

      await screen.findByText('rotate-focus-secret');
      fireEvent.click(screen.getByRole('button', { name: 'Done' }));

      await waitFor(() => expect(screen.getByRole('button', { name: 'Rotate API key Slicer Key' })).toHaveFocus());

      confirmSpy.mockRestore();
    });

    it('should fall back deterministically to the Create button when the rotated row is removed before Done', async () => {
      vi.mocked(apiKeysService.listApiKeys)
        .mockResolvedValueOnce([
          {
            id: 'key-1',
            name: 'Slicer Key',
            isActive: true,
            createdAt: '2024-01-01T00:00:00Z',
            purpose: 'OctoPrint',
            scopes: 'None',
            isExpired: false,
          },
        ])
        .mockResolvedValue([]);
      vi.mocked(apiKeysService.rotateApiKey).mockResolvedValue({ key: 'orphan-focus-secret', id: 'key-1' });
      const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);

      renderPage();

      const rotateButton = await screen.findByRole('button', { name: 'Rotate API key Slicer Key' });
      fireEvent.click(rotateButton);

      await screen.findByText('orphan-focus-secret');

      // Simulate the row disappearing (e.g. revoked/deleted elsewhere) before Done is
      // pressed: the invalidated query refetches and now resolves to an empty list.
      await waitFor(() => {
        expect(screen.queryByRole('button', { name: 'Rotate API key Slicer Key' })).not.toBeInTheDocument();
      });

      fireEvent.click(screen.getByRole('button', { name: 'Done' }));

      await waitFor(() => expect(screen.getByRole('button', { name: /Create New API Key/i })).toHaveFocus());
      expect(document.activeElement).not.toBe(document.body);

      confirmSpy.mockRestore();
    });

    it('should fall back to the page heading when both the rotated row and the Create button are unavailable', async () => {
      vi.mocked(apiKeysService.listApiKeys)
        .mockResolvedValueOnce([
          {
            id: 'key-1',
            name: 'Slicer Key',
            isActive: true,
            createdAt: '2024-01-01T00:00:00Z',
            purpose: 'OctoPrint',
            scopes: 'None',
            isExpired: false,
          },
        ])
        .mockResolvedValue([]);
      vi.mocked(apiKeysService.rotateApiKey).mockResolvedValue({ key: 'heading-fallback-secret', id: 'key-1' });
      const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);

      renderPage();

      // Open the create form so the Create button unmounts (unavailable as a fallback).
      fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));

      const rotateButton = await screen.findByRole('button', { name: 'Rotate API key Slicer Key' });
      fireEvent.click(rotateButton);

      await screen.findByText('heading-fallback-secret');

      await waitFor(() => {
        expect(screen.queryByRole('button', { name: 'Rotate API key Slicer Key' })).not.toBeInTheDocument();
      });
      expect(screen.queryByRole('button', { name: /Create New API Key/i })).not.toBeInTheDocument();

      fireEvent.click(screen.getByRole('button', { name: 'Done' }));

      await waitFor(() => expect(screen.getByRole('heading', { name: 'Your API Keys' })).toHaveFocus());
      expect(document.activeElement).not.toBe(document.body);

      confirmSpy.mockRestore();
    });
  });
});
