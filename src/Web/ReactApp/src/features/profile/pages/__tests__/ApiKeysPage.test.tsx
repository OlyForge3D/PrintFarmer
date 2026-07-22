import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { ApiKeysPage } from '../ApiKeysPage';
import * as apiKeysService from '@/services/apiKeysService';

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
  return render(
    <QueryClientProvider client={queryClient}>
      <ApiKeysPage />
    </QueryClientProvider>
  );
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
      expect(nonExpiringRotateButton).toBeEnabled();
    });

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
});
