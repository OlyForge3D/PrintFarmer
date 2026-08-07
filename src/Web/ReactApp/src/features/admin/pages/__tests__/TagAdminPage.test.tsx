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

async function saveCurrentEdit(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: /save changes/i }));
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

  it('uses the shared loading state', () => {
    vi.mocked(apiClient.getTags).mockImplementation(() => new Promise(() => {}));
    renderPage();
    expect(screen.getByRole('status', { name: 'Loading tags' })).toBeInTheDocument();
  });

  it('uses the shared error state', async () => {
    vi.mocked(apiClient.getTags).mockRejectedValue(new Error('tag outage'));
    renderPage();
    expect(await screen.findByRole('alert')).toHaveTextContent("Couldn't load tags");
  });

  it('uses the shared empty state', async () => {
    vi.mocked(apiClient.getTags).mockResolvedValue([]);
    renderPage();
    expect(await screen.findByText('No tags created yet')).toBeInTheDocument();
  });

  it('captures the tag revision when starting an edit and sends it as expectedRevision on save', async () => {
    vi.mocked(apiClient.updateTag).mockResolvedValue({ ...resin, name: 'Resin (Updated)', revision: 2 });
    const user = userEvent.setup();
    renderPage();

    await startEditingResin(user);
    const nameInput = screen.getByDisplayValue('Resin');
    await user.clear(nameInput);
    await user.type(nameInput, 'Resin (Updated)');

    await saveCurrentEdit(user);

    await waitFor(() =>
      expect(apiClient.updateTag).toHaveBeenCalledWith(
        'tag-1',
        expect.objectContaining({ name: 'Resin (Updated)', expectedRevision: 1 })
      )
    );
  });

  it('does not overwrite optional fields that were absent when renaming a tag', async () => {
    vi.mocked(apiClient.getTags).mockResolvedValue([{ id: 'tag-1', name: 'Resin', revision: 1 }]);
    vi.mocked(apiClient.updateTag).mockResolvedValue({ id: 'tag-1', name: 'Renamed', revision: 2 });
    const user = userEvent.setup();
    renderPage();

    await startEditingResin(user);
    const nameInput = screen.getByDisplayValue('Resin');
    await user.clear(nameInput);
    await user.type(nameInput, 'Renamed');
    await saveCurrentEdit(user);

    await waitFor(() => expect(apiClient.updateTag).toHaveBeenCalledWith(
      'tag-1',
      { name: 'Renamed', expectedRevision: 1 },
    ));
  });

  it('shows an inline accessible error and preserves the edit form on a generic failure', async () => {
    vi.mocked(apiClient.updateTag).mockRejectedValue(new Error('network unreachable'));
    const user = userEvent.setup();
    renderPage();

    await startEditingResin(user);
    const nameInput = screen.getByDisplayValue('Resin');
    await user.type(nameInput, ' updated');
    await saveCurrentEdit(user);

    expect(await screen.findByRole('alert')).toHaveTextContent(/network unreachable/i);
    // The edit form must remain open/preserved rather than silently discarding the attempt.
    expect(screen.getByDisplayValue('Resin updated')).toBeInTheDocument();
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
    await saveCurrentEdit(user);

    const dialog = await screen.findByRole('dialog', { name: /conflict updating "my local edit"/i });
    expect(within(dialog).getByRole('table')).toHaveTextContent('My Local Edit');
    expect(within(dialog).getByRole('table')).toHaveTextContent('Resin (renamed elsewhere)');

    // The attempted value must still be visible in the (still-open) edit input underneath.
    expect(screen.getByDisplayValue('My Local Edit')).toBeInTheDocument();
  });

  it('shows a neutral not-found placeholder (not the user\'s own attempted name) when the server tag can no longer be found after a conflict', async () => {
    vi.mocked(apiClient.updateTag).mockRejectedValue(
      makeApiError({ statusCode: 409, data: { error: 'Revision mismatch', expectedRevision: 1, actualRevision: 2 } })
    );
    // Tag was deleted by someone else - getTag resolves to null.
    vi.mocked(apiClient.getTag).mockResolvedValue(null);
    const user = userEvent.setup();
    renderPage();

    await startEditingResin(user);
    const nameInput = screen.getByDisplayValue('Resin');
    await user.clear(nameInput);
    await user.type(nameInput, 'My Local Edit');
    await saveCurrentEdit(user);

    const dialog = await screen.findByRole('dialog', { name: /conflict updating "my local edit"/i });
    // The "current version" column must not mirror the user's own attempted value.
    expect(within(dialog).getByRole('table')).toHaveTextContent(/tag not found/i);
    expect(within(dialog).getByRole('table')).toHaveTextContent('My Local Edit');
  });

  it('surfaces a clear inline error and keeps the edit form open on a duplicate-name collision (HTTP 409, no revision fields) — #942', async () => {
    // A rename that collides with another tag: TagService throws DuplicateEntityException,
    // TagsController returns HTTP 409 with `{ error: "A tag named 'X' already exists" }`
    // and NO expectedRevision/actualRevision fields (that distinguishes it from a
    // concurrency conflict). The frontend must NOT open the revision-conflict dialog,
    // must NOT silently close the edit form, and must surface the backend's own message
    // (not a generic fallback) as an accessible inline alert.
    vi.mocked(apiClient.updateTag).mockRejectedValue(
      makeApiError({
        statusCode: 409,
        message: "A tag named 'Wood' already exists",
        data: { error: "A tag named 'Wood' already exists" },
      })
    );
    const user = userEvent.setup();
    renderPage();

    await startEditingResin(user);
    const nameInput = screen.getByDisplayValue('Resin');
    await user.clear(nameInput);
    await user.type(nameInput, 'Wood');
    await saveCurrentEdit(user);

    // Backend message reaches the user — not a generic "Failed to update tag" fallback.
    expect(await screen.findByRole('alert')).toHaveTextContent(/A tag named 'Wood' already exists/);
    // No revision-conflict dialog should open for a duplicate-name conflict.
    expect(screen.queryByRole('dialog', { name: /conflict updating/i })).not.toBeInTheDocument();
    // The edit form must remain open with the attempted rename visible so the user
    // can pick a different name — not silently closed as if the save had succeeded.
    expect(screen.getByDisplayValue('Wood')).toBeInTheDocument();
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
    await saveCurrentEdit(user);

    await screen.findByRole('dialog', { name: /conflict updating "my local edit"/i });
    await user.click(screen.getByRole('button', { name: /reload latest version/i }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    // Attempted value must survive the reload.
    expect(screen.getByDisplayValue('My Local Edit')).toBeInTheDocument();

    await saveCurrentEdit(user);

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

  it('blocks saving and shows an actionable error instead of guessing expectedRevision when the tag has no revision baseline', async () => {
    // A tag without a revision (e.g. loaded from a legacy source) must never be saved
    // with a guessed expectedRevision (like 0), since that could spuriously conflict on
    // every save or silently match an unrelated revision. The user must be told to refresh.
    const noRevisionTag: TagOption = { id: 'tag-2', name: 'Unversioned', color: '#00ff00' };
    vi.mocked(apiClient.getTags).mockResolvedValue([noRevisionTag]);
    const user = userEvent.setup();
    renderPage();

    await screen.findAllByText('Unversioned');
    const row = screen.getAllByText('Unversioned')[0].closest('tr') as HTMLElement;
    await user.click(within(row).getByRole('button', { name: /^edit$/i }));

    const nameInput = screen.getByDisplayValue('Unversioned');
    await user.type(nameInput, ' updated');
    await saveCurrentEdit(user);

    expect(await screen.findByRole('alert')).toHaveTextContent(/refresh the page/i);
    expect(apiClient.updateTag).not.toHaveBeenCalled();
    // The edit form must remain open/preserved rather than silently discarding the attempt.
    expect(screen.getByDisplayValue('Unversioned updated')).toBeInTheDocument();
  });
});
