import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CustomFieldsAdminPage } from '@/features/admin/pages/CustomFieldsAdminPage';
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
    getCustomFieldDefinitions: vi.fn(),
    createCustomFieldDefinition: vi.fn(),
    updateCustomFieldDefinition: vi.fn(),
    deleteCustomFieldDefinition: vi.fn(),
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
      <CustomFieldsAdminPage />
    </QueryClientProvider>,
  );
}

describe('CustomFieldsAdminPage shared admin patterns', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiClient.getCustomFieldDefinitions).mockResolvedValue([]);
  });

  it('uses the shared loading state', () => {
    vi.mocked(apiClient.getCustomFieldDefinitions).mockImplementation(() => new Promise(() => {}));
    renderPage();
    expect(screen.getByRole('status', { name: 'Loading printer custom fields' })).toBeInTheDocument();
  });

  it('uses the shared error state', async () => {
    vi.mocked(apiClient.getCustomFieldDefinitions).mockRejectedValue(new Error('field outage'));
    renderPage();
    expect(await screen.findByRole('alert')).toHaveTextContent("Couldn't load custom fields");
  });

  it('uses the shared empty state', async () => {
    renderPage();
    expect(await screen.findByText('No printer custom fields')).toBeInTheDocument();
  });

  it('tracks generated keys as dirty and discards through the shared save bar', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Add Field' }));
    await user.type(screen.getByLabelText(/^Field Name/), 'Build Plate Code');

    expect(screen.getByLabelText(/^Field Key/)).toHaveValue('build-plate-code');
    expect(screen.getByTestId('admin-save-bar')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});
