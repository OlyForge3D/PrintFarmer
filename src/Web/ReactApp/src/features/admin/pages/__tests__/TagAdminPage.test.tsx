import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { TagAdminPage } from '../TagAdminPage';
import type { ApiError } from '@/types/api';
import type { TagOption } from '@/types/admin';

vi.mock('@/components/TagAnalyticsDashboard', () => ({
  default: () => <div data-testid="tag-analytics-dashboard-stub" />,
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getTags: vi.fn(),
    get3DModels: vi.fn(),
    getGcodeFilesQuery: vi.fn(),
    createNewTag: vi.fn(),
    deleteTagById: vi.fn(),
    getTag: vi.fn(),
    updateTag: vi.fn(),
  },
}));

import { apiClient } from '@/services/api';

function makeApiError(overrides: Partial<ApiError> = {}): ApiError {
  return { message: 'Request failed', statusCode: 500, ...overrides } as ApiError;
}

const resin: TagOption = { id: 'tag-1', name: 'Resin', color: '#ff0000', description: 'Resin prints', revision: 1 };

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <TagAdminPage />
    </QueryClientProvider>
  );
}

async function startEditingResin(user: ReturnType<typeof userEvent.setup>) {
  await screen.findAllByText('Resin');
  const nameCell = screen.getAllByText('Resin')[0];
  const row = nameCell.closest('tr') as HTMLElement;
  await user.click(within(row).getByRole('button', { name: /^edit$/i }));
}

describe('TagAdminPage - revision-aware tag editing (#844/#846)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiClient.getTags).mockResolvedValue([resin]);
    vi.mocked(apiClient.get3DModels).mockResolvedValue([]);
    vi.mocked(apiClient.getGcodeFilesQuery).mockResolvedValue({ files: [] });
  });

  it('renders the tags table once loaded', async () => {
    renderPage();
    expect((await screen.findAllByText('Resin')).length).toBeGreaterThan(0);
  });

  it('captures the tag revision when starting an edit and sends it as expectedRevision on save', async () => {
    vi.mocked(apiClient.updateTag).mockResolvedValue({ ...resin, name: 'Resin (Updated)', revision: 2 });
    const user = userEvent.setup();
    renderPage();

    await startEditingResin(user);
    const nameInput = screen.getByDisplayValue('Resin');
    await user.clear(nameInput);
    await user.type(nameInput, 'Resin (Updated)');

    const row = nameInput.closest('tr') as HTMLElement;
    await user.click(within(row).getByRole('button', { name: /^check$/i }));

    await waitFor(() =>
      expect(apiClient.updateTag).toHaveBeenCalledWith(
        'tag-1',
        expect.objectContaining({ name: 'Resin (Updated)', expectedRevision: 1 })
      )
    );
  });

  it('shows an inline accessible error and preserves the edit form on a generic failure', async () => {
    vi.mocked(apiClient.updateTag).mockRejectedValue(new Error('network unreachable'));
    const user = userEvent.setup();
    renderPage();

    await startEditingResin(user);
    const nameInput = screen.getByDisplayValue('Resin');
    const row = nameInput.closest('tr') as HTMLElement;
    await user.click(within(row).getByRole('button', { name: /^check$/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/network unreachable/i);
    // The edit form must remain open/preserved rather than silently discarding the attempt.
    expect(screen.getByDisplayValue('Resin')).toBeInTheDocument();
  });

  it('opens a non-destructive revision conflict dialog on HTTP 409, preserving the user\'s attempted values', async () => {
    vi.mocked(apiClient.updateTag).mockRejectedValue(
      makeApiError({ statusCode: 409, data: { error: 'Revision mismatch', expectedRevision: 1, actualRevision: 2 } })
    );
    vi.mocked(apiClient.getTag).mockResolvedValue({ ...resin, name: 'Resin (renamed elsewhere)', revision: 2 });
    const user = userEvent.setup();
    renderPage();

    await startEditingResin(user);
    const nameInput = screen.getByDisplayValue('Resin');
    await user.clear(nameInput);
    await user.type(nameInput, 'My Local Edit');
    const row = nameInput.closest('tr') as HTMLElement;
    await user.click(within(row).getByRole('button', { name: /^check$/i }));

    const dialog = await screen.findByRole('dialog', { name: /conflict updating "my local edit"/i });
    expect(within(dialog).getByRole('table')).toHaveTextContent('My Local Edit');
    expect(within(dialog).getByRole('table')).toHaveTextContent('Resin (renamed elsewhere)');

    // The attempted value must still be visible in the (still-open) edit input underneath.
    expect(screen.getByDisplayValue('My Local Edit')).toBeInTheDocument();
  });

  it('reloads the latest revision on "Reload latest version" without discarding the typed name, then allows retrying save', async () => {
    vi.mocked(apiClient.updateTag)
      .mockRejectedValueOnce(
        makeApiError({ statusCode: 409, data: { error: 'Revision mismatch', expectedRevision: 1, actualRevision: 2 } })
      )
      .mockResolvedValueOnce({ ...resin, name: 'My Local Edit', revision: 3 });
    vi.mocked(apiClient.getTag).mockResolvedValue({ ...resin, name: 'Resin (renamed elsewhere)', revision: 2 });
    const user = userEvent.setup();
    renderPage();

    await startEditingResin(user);
    const nameInput = screen.getByDisplayValue('Resin');
    await user.clear(nameInput);
    await user.type(nameInput, 'My Local Edit');
    const row = nameInput.closest('tr') as HTMLElement;
    await user.click(within(row).getByRole('button', { name: /^check$/i }));

    await screen.findByRole('dialog', { name: /conflict updating "my local edit"/i });
    await user.click(screen.getByRole('button', { name: /reload latest version/i }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    // Attempted value must survive the reload.
    expect(screen.getByDisplayValue('My Local Edit')).toBeInTheDocument();

    await user.click(within(row).getByRole('button', { name: /^check$/i }));

    await waitFor(() =>
      expect(apiClient.updateTag).toHaveBeenLastCalledWith(
        'tag-1',
        expect.objectContaining({ name: 'My Local Edit', expectedRevision: 2 })
      )
    );
  });

  it('supports canceling out of an edit via Escape without saving', async () => {
    const user = userEvent.setup();
    renderPage();

    await startEditingResin(user);
    const nameInput = screen.getByDisplayValue('Resin');
    await user.type(nameInput, ' extra text');
    await user.keyboard('{Escape}');

    await waitFor(() => expect(screen.queryByDisplayValue(/extra text/)).not.toBeInTheDocument());
    expect(apiClient.updateTag).not.toHaveBeenCalled();
  });
});
