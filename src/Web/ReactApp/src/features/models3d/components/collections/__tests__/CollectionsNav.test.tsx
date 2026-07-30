import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ComponentProps } from 'react';
import { CollectionsNav } from '../CollectionsNav';
import type { ModelCollection } from '@/types/models';

// Mock auth so ownership/admin checks are deterministic across tests.
const mockUseAuth = vi.fn();
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => mockUseAuth(),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getModelCollections: vi.fn(),
    createModelCollection: vi.fn(),
    updateModelCollection: vi.fn(),
    deleteModelCollection: vi.fn(),
    shareModelCollection: vi.fn(),
    unshareModelCollection: vi.fn(),
  },
}));

import { apiClient } from '@/services/api';

const personalCollection: ModelCollection = {
  id: 'col-1',
  name: 'Miniatures',
  description: 'My minis',
  ownerUserId: 'user-1',
  isShared: false,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  memberCount: 3,
  modelIds: ['m1', 'm2', 'm3'],
  revision: 1,
  concurrencyToken: 'tok-1',
};

const sharedCollection: ModelCollection = {
  id: 'col-2',
  name: 'Client Work',
  description: null,
  ownerUserId: 'user-2',
  isShared: true,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  memberCount: 5,
  modelIds: ['m4', 'm5', 'm6', 'm7', 'm8'],
  revision: 2,
  concurrencyToken: 'tok-2',
};

/** Private collection owned by a different user - only admins can see/list these at all. */
const otherUsersPrivateCollection: ModelCollection = {
  id: 'col-3',
  name: 'Someone Else Private',
  description: null,
  ownerUserId: 'user-3',
  isShared: false,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  memberCount: 1,
  modelIds: ['m9'],
  revision: 1,
  concurrencyToken: 'tok-3',
};

function renderNav(props: Partial<ComponentProps<typeof CollectionsNav>> = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onSelectCollection = vi.fn();
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <CollectionsNav selectedCollectionId={null} onSelectCollection={onSelectCollection} {...props} />
    </QueryClientProvider>
  );
  return { ...utils, onSelectCollection, queryClient };
}

describe('CollectionsNav', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockUseAuth.mockReturnValue({
      user: { id: 'user-1' },
      hasRole: () => false,
    });
  });

  it('renders a navigation landmark with an accessible name', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([]);
    renderNav();
    expect(await screen.findByRole('navigation', { name: /model collections/i })).toBeInTheDocument();
  });

  it('shows loading state while collections are being fetched', () => {
    vi.mocked(apiClient.getModelCollections).mockReturnValue(new Promise(() => {}));
    renderNav();
    expect(screen.getByRole('status')).toHaveTextContent(/loading collections/i);
  });

  it('shows an accessible error state when loading fails', async () => {
    vi.mocked(apiClient.getModelCollections).mockRejectedValue(new Error('network down'));
    renderNav();
    expect(await screen.findByRole('alert')).toHaveTextContent(/failed to load collections/i);
  });

  it('shows empty-state copy for personal and shared sections when there are no collections', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([]);
    renderNav();
    expect(await screen.findByText(/no personal collections yet/i)).toBeInTheDocument();
    expect(screen.getByText(/no shared collections yet/i)).toBeInTheDocument();
  });

  it('splits collections into Personal (owned) and Shared (owned by others) sections', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([personalCollection, sharedCollection]);
    renderNav();

    await screen.findByText('Miniatures');
    const personalHeading = screen.getByText('Personal');
    const personalSection = personalHeading.parentElement as HTMLElement;
    expect(within(personalSection).getByText('Miniatures')).toBeInTheDocument();

    const sharedHeading = screen.getByText('Shared');
    const sharedSection = sharedHeading.closest('div')?.parentElement as HTMLElement;
    expect(within(sharedSection).getByText('Client Work')).toBeInTheDocument();
  });

  it('always renders the "All Models" entry and marks it current when nothing is selected', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([personalCollection]);
    renderNav({ selectedCollectionId: null });
    const allModels = await screen.findByRole('button', { name: /all models/i });
    expect(allModels).toHaveAttribute('aria-current', 'true');
  });

  it('calls onSelectCollection with the collection id when a row is clicked', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([personalCollection]);
    const user = userEvent.setup();
    const { onSelectCollection } = renderNav();

    await screen.findByText('Miniatures');
    const row = screen.getByText('Miniatures').closest('button') as HTMLElement;
    await user.click(row);
    expect(onSelectCollection).toHaveBeenCalledWith('col-1');
  });

  it('is fully keyboard operable: tabbing reaches a collection row and Enter activates it', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([personalCollection]);
    const user = userEvent.setup();
    const { onSelectCollection } = renderNav();

    await screen.findByText('Miniatures');
    const row = screen.getByText('Miniatures').closest('button') as HTMLElement;
    await user.tab(); // New collection button
    await user.tab(); // All Models
    await user.tab(); // Miniatures row
    expect(row).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(onSelectCollection).toHaveBeenCalledWith('col-1');
  });

  it('opens the create-collection form and submits a new collection', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([]);
    vi.mocked(apiClient.createModelCollection).mockResolvedValue(personalCollection);
    const user = userEvent.setup();
    renderNav();

    await user.click(await screen.findByRole('button', { name: /new collection/i }));
    const dialog = await screen.findByRole('dialog', { name: /new collection/i });
    await user.type(within(dialog).getByLabelText(/name/i), 'Miniatures');
    await user.click(within(dialog).getByRole('button', { name: /create/i }));

    await waitFor(() =>
      expect(apiClient.createModelCollection).toHaveBeenCalledWith({ name: 'Miniatures', description: undefined })
    );
  });

  it('shows a rename/share/delete kebab menu only when the current user owns the collection', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([personalCollection, sharedCollection]);
    const user = userEvent.setup();
    renderNav();

    await screen.findByText('Miniatures');
    // The non-owned shared collection ("Client Work") gets no actions button at all, since
    // rename/share/delete are all owner-or-admin actions the backend would otherwise reject.
    const actionButtons = screen.getAllByRole('button', { name: /actions for/i });
    expect(actionButtons).toHaveLength(1);

    await user.click(screen.getByRole('button', { name: /actions for miniatures/i }));
    const menu = await screen.findByRole('menu');
    expect(within(menu).getByRole('menuitem', { name: /delete/i })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitem', { name: /share with everyone/i })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitem', { name: /rename/i })).toBeInTheDocument();
  });

  it('does not offer an actions menu for collections the user does not own and is not admin', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([sharedCollection]);
    renderNav();

    await screen.findByText('Client Work');
    expect(screen.queryByRole('button', { name: /actions for client work/i })).not.toBeInTheDocument();
  });

  it('shares a collection via the kebab menu', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([personalCollection]);
    vi.mocked(apiClient.shareModelCollection).mockResolvedValue({ ...personalCollection, isShared: true });
    const user = userEvent.setup();
    renderNav();

    await user.click(await screen.findByRole('button', { name: /actions for miniatures/i }));
    const menu = await screen.findByRole('menu');
    await user.click(within(menu).getByRole('menuitem', { name: /share with everyone/i }));

    await waitFor(() => expect(apiClient.shareModelCollection).toHaveBeenCalledWith('col-1'));
  });

  it('confirms before deleting a collection and does not delete on cancel', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([personalCollection]);
    const user = userEvent.setup();
    renderNav();

    const trigger = await screen.findByRole('button', { name: /actions for miniatures/i });
    await user.click(trigger);
    const menu = await screen.findByRole('menu');
    await user.click(within(menu).getByRole('menuitem', { name: /delete/i }));

    const confirmDialog = await screen.findByRole('dialog', { name: /delete collection/i });
    expect(within(confirmDialog).getByText(/miniatures/i)).toBeInTheDocument();
    await user.click(within(confirmDialog).getByRole('button', { name: /cancel/i }));

    expect(apiClient.deleteModelCollection).not.toHaveBeenCalled();
  });

  it('deletes a collection and clears selection if the deleted collection was selected', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([personalCollection]);
    vi.mocked(apiClient.deleteModelCollection).mockResolvedValue(undefined);
    const user = userEvent.setup();
    const { onSelectCollection } = renderNav({ selectedCollectionId: 'col-1' });

    await user.click(await screen.findByRole('button', { name: /actions for miniatures/i }));
    const menu = await screen.findByRole('menu');
    await user.click(within(menu).getByRole('menuitem', { name: /delete/i }));

    const confirmDialog = await screen.findByRole('dialog', { name: /delete collection/i });
    await user.click(within(confirmDialog).getByRole('button', { name: /^delete$/i }));

    await waitFor(() => expect(apiClient.deleteModelCollection).toHaveBeenCalledWith('col-1'));
    await waitFor(() => expect(onSelectCollection).toHaveBeenCalledWith(null));
  });

  it('grants manage actions (delete/unshare) to admins even on collections they do not own', async () => {
    mockUseAuth.mockReturnValue({ user: { id: 'someone-else' }, hasRole: (role: string) => role === 'farm_admin' });
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([sharedCollection]);
    const user = userEvent.setup();
    renderNav();

    await user.click(await screen.findByRole('button', { name: /actions for client work/i }));
    const menu = await screen.findByRole('menu');
    expect(within(menu).getByRole('menuitem', { name: /delete/i })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitem', { name: /unshare/i })).toBeInTheDocument();
  });

  it('surfaces other users\u2019 private collections to admins in a dedicated section instead of hiding them', async () => {
    mockUseAuth.mockReturnValue({ user: { id: 'admin-user' }, hasRole: (role: string) => role === 'farm_admin' });
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([otherUsersPrivateCollection]);
    renderNav();

    expect(await screen.findByText('Someone Else Private')).toBeInTheDocument();
    expect(screen.getByText(/other users.? private collections/i)).toBeInTheDocument();
  });

  it('does not show other users\u2019 private collections, or the section itself, to non-admins', async () => {
    mockUseAuth.mockReturnValue({ user: { id: 'regular-user' }, hasRole: () => false });
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([otherUsersPrivateCollection]);
    renderNav();

    await screen.findByText(/no personal collections yet/i);
    expect(screen.queryByText('Someone Else Private')).not.toBeInTheDocument();
    expect(screen.queryByText(/other users.? private collections/i)).not.toBeInTheDocument();
  });
});
