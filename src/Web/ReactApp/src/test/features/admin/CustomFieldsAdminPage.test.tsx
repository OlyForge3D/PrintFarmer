import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CustomFieldsAdminPage } from '@/features/admin/pages/CustomFieldsAdminPage';
import { apiClient } from '@/services/api';
import type { CustomFieldDefinition } from '@/types/api';

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

  it('cycles numeric Order sorting without changing source order or edit identity', async () => {
    const user = userEvent.setup();
    const definitions: CustomFieldDefinition[] = [
      { id: 'ten', fieldName: 'Ten', fieldKey: 'ten', sortOrder: 10 },
      { id: 'two', fieldName: 'Two', fieldKey: 'two', sortOrder: 2 },
      { id: 'twenty', fieldName: 'Twenty', fieldKey: 'twenty', sortOrder: 20 },
    ].map(def => ({
      ...def, entityType: 'Printer', fieldType: 'Text', isRequired: false,
      createdDate: '2026-01-01T00:00:00Z', updatedDate: '2026-01-01T00:00:00Z',
    }));
    vi.mocked(apiClient.getCustomFieldDefinitions).mockResolvedValue(definitions);
    renderPage();
    const order = await screen.findByRole('button', { name: 'Order' });
    const header = screen.getByRole('columnheader', { name: 'Order' });
    const names = () => screen.getAllByRole('row').slice(1).map(row => row.firstElementChild?.textContent);
    expect(header).toHaveAttribute('aria-sort', 'none');
    expect(screen.getByRole('columnheader', { name: 'Actions' })).not.toHaveAttribute('aria-sort');
    for (const [direction, expected] of [
      ['ascending', ['Two', 'Ten', 'Twenty']],
      ['descending', ['Twenty', 'Ten', 'Two']],
      ['none', ['Ten', 'Two', 'Twenty']],
      ['ascending', ['Two', 'Ten', 'Twenty']],
    ] as const) {
      await user.click(order);
      expect(header).toHaveAttribute('aria-sort', direction);
      expect(names()).toEqual(expected);
      expect(definitions.map(def => def.id)).toEqual(['ten', 'two', 'twenty']);
    }
    const two = screen.getByRole('row', { name: /Two two Text/ });
    await user.click(within(two).getByRole('button', { name: 'Edit Two' }));
    expect(screen.getByLabelText(/^Field Name/)).toHaveValue('Two');
    expect(screen.getByLabelText(/^Field Key/)).toHaveValue('two');
    await user.type(screen.getByLabelText(/^Field Name/), ' edited');
    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(apiClient.updateCustomFieldDefinition).toHaveBeenCalledWith('two', expect.objectContaining({ fieldName: 'Two edited', sortOrder: 2 }));
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
