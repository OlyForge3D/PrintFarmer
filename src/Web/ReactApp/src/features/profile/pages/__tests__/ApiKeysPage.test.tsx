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
        purpose: 'General',
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
    expect(screen.getByText('General')).toBeInTheDocument();
    expect(screen.getByText('Desktop')).toBeInTheDocument();
    expect(screen.getByText('Expired')).toBeInTheDocument();
  });

  it('should only show scope checkboxes and expiry field when Desktop purpose is selected', async () => {
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Create New API Key/i }));

    expect(screen.queryByText(/Model Read/)).not.toBeInTheDocument();

    const purposeSelect = screen.getByLabelText('Purpose');
    fireEvent.change(purposeSelect, { target: { value: 'Desktop' } });

    expect(screen.getByText(/Model Read/)).toBeInTheDocument();
    expect(screen.getByText(/Model Write/)).toBeInTheDocument();
    expect(screen.getByText(/Library Sync/)).toBeInTheDocument();
    expect(screen.getByLabelText('Expires At')).toBeInTheDocument();
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
  });
});
