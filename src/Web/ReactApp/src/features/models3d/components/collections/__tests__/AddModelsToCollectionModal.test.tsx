import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AddModelsToCollectionModal } from '../AddModelsToCollectionModal';
import type { ModelCollection } from '@/types/models';

// Mock auth so ownership/admin checks are deterministic across tests (mirrors CollectionsNav.test.tsx).
const mockUseAuth = vi.fn();
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => mockUseAuth(),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getModelCollections: vi.fn(),
    addModelCollectionMember: vi.fn(),
  },
}));

import { apiClient } from '@/services/api';

const collectionA: ModelCollection = {
  id: 'col-a',
  name: 'Miniatures',
  description: null,
  ownerUserId: 'user-1',
  isShared: false,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  memberCount: 2,
  modelIds: ['m1', 'm2'],
  revision: 1,
  concurrencyToken: 'tok-a',
};

const collectionB: ModelCollection = {
  id: 'col-b',
  name: 'Client Work',
  description: null,
  ownerUserId: 'user-1',
  isShared: true,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  memberCount: 0,
  modelIds: [],
  revision: 1,
  concurrencyToken: 'tok-b',
};

function renderModal(props: Partial<React.ComponentProps<typeof AddModelsToCollectionModal>> = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onClose = vi.fn();
  const onCreateNew = vi.fn();
  render(
    <QueryClientProvider client={queryClient}>
      <AddModelsToCollectionModal isOpen modelIds={['model-1', 'model-2']} onClose={onClose} onCreateNew={onCreateNew} {...props} />
    </QueryClientProvider>
  );
  return { onClose, onCreateNew };
}

describe('AddModelsToCollectionModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // Default: current user owns collectionA/collectionB (both use ownerUserId: 'user-1')
    // and is not an admin, matching every pre-existing test below.
    mockUseAuth.mockReturnValue({ user: { id: 'user-1' }, hasRole: () => false });
  });

  it('shows the number of selected models in the title', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([collectionA]);
    renderModal();
    expect(await screen.findByRole('dialog', { name: /add 2 models to collections/i })).toBeInTheDocument();
  });

  it('shows a loading status while collections load', () => {
    vi.mocked(apiClient.getModelCollections).mockReturnValue(new Promise(() => {}));
    renderModal();
    expect(screen.getByRole('status')).toHaveTextContent(/loading collections/i);
  });

  it('shows an empty state with a "New collection" action when there are no collections', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([]);
    const { onCreateNew } = renderModal();
    const user = userEvent.setup();

    expect(await screen.findByText(/no collections yet/i)).toBeInTheDocument();
    const emptyStateButtons = screen.getAllByRole('button', { name: /new collection/i });
    await user.click(emptyStateButtons[emptyStateButtons.length - 1]);
    expect(onCreateNew).toHaveBeenCalled();
  });

  it('disables the submit action until at least one collection is checked', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([collectionA, collectionB]);
    renderModal();

    const submit = await screen.findByRole('button', { name: /add to 0 collections/i });
    expect(submit).toBeDisabled();
  });

  it('adds the selected models to each checked collection and closes on success', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([collectionA, collectionB]);
    vi.mocked(apiClient.addModelCollectionMember).mockResolvedValue({
      id: 'mem-1',
      collectionId: 'col-a',
      modelId: 'model-1',
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      revision: 1,
    });
    const user = userEvent.setup();
    const { onClose } = renderModal();

    await screen.findByText('Miniatures');
    await user.click(screen.getByRole('checkbox', { name: /miniatures/i }));
    await user.click(screen.getByRole('checkbox', { name: /client work/i }));
    await user.click(screen.getByRole('button', { name: /add to 2 collections/i }));

    await waitFor(() => expect(apiClient.addModelCollectionMember).toHaveBeenCalledTimes(4));
    expect(apiClient.addModelCollectionMember).toHaveBeenCalledWith('col-a', 'model-1');
    expect(apiClient.addModelCollectionMember).toHaveBeenCalledWith('col-a', 'model-2');
    expect(apiClient.addModelCollectionMember).toHaveBeenCalledWith('col-b', 'model-1');
    expect(apiClient.addModelCollectionMember).toHaveBeenCalledWith('col-b', 'model-2');
    await waitFor(() => expect(onClose).toHaveBeenCalled());
  });

  it('indicates shared collections with a visible label', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([collectionB]);
    renderModal();
    expect(await screen.findByText('Shared')).toBeInTheDocument();
  });

  it('resets its checkbox selection each time it is remounted for a new open (key-remount contract)', async () => {
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([collectionA]);
    const user = userEvent.setup();
    const { rerender } = render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <AddModelsToCollectionModal key="open-1" isOpen modelIds={['model-1']} onClose={vi.fn()} onCreateNew={vi.fn()} />
      </QueryClientProvider>
    );

    await user.click(await screen.findByRole('checkbox', { name: /miniatures/i }));
    expect(screen.getByRole('checkbox', { name: /miniatures/i })).toBeChecked();

    const client2 = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    rerender(
      <QueryClientProvider client={client2}>
        <AddModelsToCollectionModal key="open-2" isOpen modelIds={['model-1']} onClose={vi.fn()} onCreateNew={vi.fn()} />
      </QueryClientProvider>
    );

    expect(await screen.findByRole('checkbox', { name: /miniatures/i })).not.toBeChecked();
  });

  it('excludes a shared collection owned by another user, since the backend rejects non-owner writes', async () => {
    // collectionB is shared (isShared: true) but still owned by 'user-1'; a non-owner,
    // non-admin viewer must not be able to select it for a write (ModelCollectionService.
    // EnsureCanWrite only allows the owner or an admin).
    mockUseAuth.mockReturnValue({ user: { id: 'user-2' }, hasRole: () => false });
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([collectionA, collectionB]);
    renderModal();

    // Falls through to the empty state: user-2 owns neither collection, and both are
    // filtered out of the writable list even though collectionB is shared/readable.
    expect(await screen.findByText(/no collections yet/i)).toBeInTheDocument();
    expect(screen.queryByText('Miniatures')).not.toBeInTheDocument();
    expect(screen.queryByText('Client Work')).not.toBeInTheDocument();
  });

  it('lets a farm_admin select every collection, including ones they do not own', async () => {
    mockUseAuth.mockReturnValue({ user: { id: 'user-2' }, hasRole: (role: string) => role === 'farm_admin' });
    vi.mocked(apiClient.getModelCollections).mockResolvedValue([collectionA, collectionB]);
    renderModal();

    await screen.findByText('Miniatures');
    expect(screen.getByText('Client Work')).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: /miniatures/i })).toBeEnabled();
    expect(screen.getByRole('checkbox', { name: /client work/i })).toBeEnabled();
  });
});
