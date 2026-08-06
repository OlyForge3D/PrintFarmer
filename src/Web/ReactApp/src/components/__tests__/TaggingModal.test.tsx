import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

const hoisted = vi.hoisted(() => ({
  assignTagToObject: vi.fn().mockResolvedValue(undefined),
  removeTagFromObject: vi.fn().mockResolvedValue(undefined),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    assignTagToObject: hoisted.assignTagToObject,
    removeTagFromObject: hoisted.removeTagFromObject,
  },
}));

vi.mock('@/services/tagService', () => ({
  tagService: {
    assignTag: vi.fn().mockResolvedValue(undefined),
    removeTag: vi.fn().mockResolvedValue(undefined),
  },
}));

vi.mock('@/components/TagSelector', () => ({
  TagSelector: ({
    selectedTags,
    onTagsChange,
  }: {
    selectedTags: Array<{ id: string; name: string }>;
    onTagsChange: (tags: Array<{ id: string; name: string }>) => void;
  }) => (
    <div>
      <span data-testid="selected-tags">{selectedTags.map((t) => t.id).join(',')}</span>
      <button
        type="button"
        onClick={() => onTagsChange([...selectedTags, { id: 'tag-new', name: 'New Tag' }])}
      >
        add-new-tag
      </button>
      {selectedTags.map((tag) => (
        <button
          key={tag.id}
          type="button"
          onClick={() => onTagsChange(selectedTags.filter((t) => t.id !== tag.id))}
        >
          remove-{tag.id}
        </button>
      ))}
    </div>
  ),
}));

import { TaggingModal } from '../TaggingModal';
import { printerTagsFleetQueryKey } from '@/features/printers/hooks/usePrinterTagsFleet';

function renderModal(client: QueryClient, props: Partial<React.ComponentProps<typeof TaggingModal>> = {}) {
  return render(
    <QueryClientProvider client={client}>
      <TaggingModal
        objectId="printer-1"
        objectType="Printer"
        initialTags={[]}
        isOpen
        onClose={vi.fn()}
        {...props}
      />
    </QueryClientProvider>,
  );
}

describe('TaggingModal — printer tag fleet invalidation (#1146 item 1)', () => {
  beforeEach(() => {
    hoisted.assignTagToObject.mockClear();
    hoisted.removeTagFromObject.mockClear();
  });

  it('assigns the newly-selected tag through apiClient for objectType=Printer', async () => {
    const user = userEvent.setup();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderModal(client);

    await user.click(screen.getByRole('button', { name: 'add-new-tag' }));
    await user.click(screen.getByRole('button', { name: 'Save Tags' }));

    await waitFor(() => expect(hoisted.assignTagToObject).toHaveBeenCalledWith('printer-1', 'tag-new', 'Printer'));
    expect(hoisted.removeTagFromObject).not.toHaveBeenCalled();
  });

  it('invalidates the fleet tags key on save so every compact card refetches immediately', async () => {
    const user = userEvent.setup();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidateSpy = vi.spyOn(client, 'invalidateQueries');
    renderModal(client);

    await user.click(screen.getByRole('button', { name: 'add-new-tag' }));
    await user.click(screen.getByRole('button', { name: 'Save Tags' }));

    await waitFor(() =>
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: printerTagsFleetQueryKey })
    );
  });

  it('also invalidates the legacy per-object key for compatibility', async () => {
    const user = userEvent.setup();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidateSpy = vi.spyOn(client, 'invalidateQueries');
    renderModal(client);

    await user.click(screen.getByRole('button', { name: 'add-new-tag' }));
    await user.click(screen.getByRole('button', { name: 'Save Tags' }));

    await waitFor(() =>
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['printer-tags', 'printer-1'] })
    );
  });

  it('closes the modal after a successful save', async () => {
    const user = userEvent.setup();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const onClose = vi.fn();
    renderModal(client, { onClose });

    await user.click(screen.getByRole('button', { name: 'add-new-tag' }));
    await user.click(screen.getByRole('button', { name: 'Save Tags' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
  });

  it('does not invalidate the printer fleet key for non-Printer object types', async () => {
    const user = userEvent.setup();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidateSpy = vi.spyOn(client, 'invalidateQueries');
    renderModal(client, { objectType: 'Model3D', objectId: 'model-1' });

    await user.click(screen.getByRole('button', { name: 'add-new-tag' }));
    await user.click(screen.getByRole('button', { name: 'Save Tags' }));

    await waitFor(() => expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['model-tags'] }));
    expect(invalidateSpy).not.toHaveBeenCalledWith({ queryKey: printerTagsFleetQueryKey });
    expect(invalidateSpy).not.toHaveBeenCalledWith({ queryKey: ['printer-tags', 'model-1'] });
  });

  it('renders nothing extra on close and no longer relies on the card to invalidate on close/cancel', () => {
    // Regression guard: the fleet invalidation now lives entirely in this
    // modal's onSuccess, not in the calling card's onClose handler.
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const onClose = vi.fn();
    renderModal(client, { onClose });

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(onClose).toHaveBeenCalledTimes(1);
    expect(hoisted.assignTagToObject).not.toHaveBeenCalled();
    expect(hoisted.removeTagFromObject).not.toHaveBeenCalled();
  });
});

describe('TaggingModal — selection resync on open/hydrate (late-hydration data-loss fix)', () => {
  beforeEach(() => {
    hoisted.assignTagToObject.mockClear();
    hoisted.removeTagFromObject.mockClear();
  });

  it('does not remove any existing tags when Save is clicked immediately after opening with already-resolved tags (core regression guard)', async () => {
    const user = userEvent.setup();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderModal(client, { initialTags: [{ id: 'tag-existing', name: 'Existing' }] });

    expect(screen.getByTestId('selected-tags')).toHaveTextContent('tag-existing');

    await user.click(screen.getByRole('button', { name: 'Save Tags' }));

    await waitFor(() => expect(hoisted.assignTagToObject).not.toHaveBeenCalled());
    expect(hoisted.removeTagFromObject).not.toHaveBeenCalled();
  });

  it('reseeds the selection once an async tag source hydrates from [] to existing tags while the modal stays mounted and open (rerender [] -> existing tags)', async () => {
    // Mirrors CompactPrinterCard: the modal is mounted before its
    // `usePrinterTagsFromFleet` fleet query resolves, so `initialTags` starts
    // as `[]` and `tagsLoading` starts `true`.
    const user = userEvent.setup();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { rerender } = render(
      <QueryClientProvider client={client}>
        <TaggingModal
          objectId="printer-1"
          objectType="Printer"
          initialTags={[]}
          isOpen
          onClose={vi.fn()}
          tagsLoading
        />
      </QueryClientProvider>,
    );

    expect(screen.getByTestId('selected-tags')).toHaveTextContent('');
    // Save is disabled while the source hasn't resolved — the caller can't
    // submit a selection seeded from an unresolved (possibly empty) source.
    expect(screen.getByRole('button', { name: 'Loading tags…' })).toBeDisabled();

    // The fleet tags query resolves while the modal is still open.
    rerender(
      <QueryClientProvider client={client}>
        <TaggingModal
          objectId="printer-1"
          objectType="Printer"
          initialTags={[{ id: 'tag-existing', name: 'Existing' }]}
          isOpen
          onClose={vi.fn()}
          tagsLoading={false}
        />
      </QueryClientProvider>,
    );

    expect(screen.getByTestId('selected-tags')).toHaveTextContent('tag-existing');

    await user.click(screen.getByRole('button', { name: 'Save Tags' }));

    // Saving now must not diff against the stale `[]` the state started
    // with — it must not issue a "remove" for the tag that just hydrated in.
    await waitFor(() => expect(hoisted.assignTagToObject).not.toHaveBeenCalled());
    expect(hoisted.removeTagFromObject).not.toHaveBeenCalled();
  });

  it('supports both adding a new tag and removing an existing one in the same save (add/remove)', async () => {
    const user = userEvent.setup();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderModal(client, { initialTags: [{ id: 'tag-existing', name: 'Existing' }] });

    await user.click(screen.getByRole('button', { name: 'remove-tag-existing' }));
    await user.click(screen.getByRole('button', { name: 'add-new-tag' }));
    expect(screen.getByTestId('selected-tags')).toHaveTextContent('tag-new');

    await user.click(screen.getByRole('button', { name: 'Save Tags' }));

    await waitFor(() => expect(hoisted.assignTagToObject).toHaveBeenCalledWith('printer-1', 'tag-new', 'Printer'));
    expect(hoisted.removeTagFromObject).toHaveBeenCalledWith('printer-1', 'tag-existing', 'Printer');
  });

  it('discards an abandoned in-progress edit and reseeds from the true current tags on reopen', async () => {
    const user = userEvent.setup();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const onClose = vi.fn();
    const props = {
      objectId: 'printer-1',
      objectType: 'Printer' as const,
      initialTags: [{ id: 'tag-existing', name: 'Existing' }],
      onClose,
    };

    const { rerender } = render(
      <QueryClientProvider client={client}>
        <TaggingModal {...props} isOpen />
      </QueryClientProvider>,
    );

    // Start (but never save) an edit.
    await user.click(screen.getByRole('button', { name: 'add-new-tag' }));
    expect(screen.getByTestId('selected-tags')).toHaveTextContent('tag-existing,tag-new');

    // Cancel closes without sending anything to the API...
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(hoisted.assignTagToObject).not.toHaveBeenCalled();
    expect(hoisted.removeTagFromObject).not.toHaveBeenCalled();

    // ...the parent reacts by flipping isOpen to false (modal stays mounted)...
    rerender(
      <QueryClientProvider client={client}>
        <TaggingModal {...props} isOpen={false} />
      </QueryClientProvider>,
    );

    // ...and the user reopens it later. The abandoned "tag-new" addition
    // must not resurface — the selection reflects the true current tags.
    rerender(
      <QueryClientProvider client={client}>
        <TaggingModal {...props} isOpen />
      </QueryClientProvider>,
    );

    expect(screen.getByTestId('selected-tags')).toHaveTextContent('tag-existing');
    expect(screen.queryByRole('button', { name: 'remove-tag-new' })).not.toBeInTheDocument();
  });
});