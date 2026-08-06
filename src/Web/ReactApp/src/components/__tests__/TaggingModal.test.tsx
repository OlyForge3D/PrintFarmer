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
    <button
      type="button"
      onClick={() => onTagsChange([...selectedTags, { id: 'tag-new', name: 'New Tag' }])}
    >
      add-new-tag
    </button>
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