import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { BedTypeAdminPage } from '@/features/admin/pages/BedTypeAdminPage';
import { apiClient } from '@/services/api';

const toastMocks = vi.hoisted(() => ({
  success: vi.fn(),
  error: vi.fn(),
  info: vi.fn(),
  warning: vi.fn(),
}));

vi.mock('@/common/components/admin', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/common/components/admin')>()),
  adminToast: toastMocks,
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getBedTypes: vi.fn(),
    createBedType: vi.fn(),
    updateBedType: vi.fn(),
    deleteBedType: vi.fn(),
  },
}));

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <BedTypeAdminPage />
    </QueryClientProvider>,
  );
}

describe('BedTypeAdminPage shared admin patterns', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiClient.getBedTypes).mockResolvedValue([]);
  });

  it('uses the shared loading state', () => {
    vi.mocked(apiClient.getBedTypes).mockImplementation(() => new Promise(() => {}));
    renderPage();
    expect(screen.getByRole('status', { name: 'Loading bed types' })).toBeInTheDocument();
  });

  it('uses the shared error state', async () => {
    vi.mocked(apiClient.getBedTypes).mockRejectedValue(new Error('bed type outage'));
    renderPage();
    expect(await screen.findByRole('alert')).toHaveTextContent("Couldn't load bed types");
  });

  it('uses the shared empty state', async () => {
    renderPage();
    expect(await screen.findByText('No bed types configured')).toBeInTheDocument();
  });

  it('shows the shared save bar for dirty form state and marks the form pristine after save', async () => {
    vi.mocked(apiClient.createBedType).mockResolvedValue({
      id: 'bed-1',
      name: 'Textured PEI',
      color: '#6366f1',
      isSystem: false,
    });
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Add Bed Type' }));
    expect(screen.queryByTestId('admin-save-bar')).not.toBeInTheDocument();

    await user.type(screen.getByLabelText(/^Name/), 'Textured PEI');
    const saveBar = screen.getByTestId('admin-save-bar');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(apiClient.createBedType).toHaveBeenCalled());
    expect(toastMocks.success).toHaveBeenCalledWith('Bed type created');
    expect(saveBar).not.toBeInTheDocument();
  });
});
