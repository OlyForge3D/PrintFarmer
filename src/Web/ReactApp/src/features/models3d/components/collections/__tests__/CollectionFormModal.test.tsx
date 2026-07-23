import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { CollectionFormModal } from '../CollectionFormModal';
import type { ModelCollection } from '@/types/models';

const collection: ModelCollection = {
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

describe('CollectionFormModal', () => {
  it('renders "New Collection" title and empty fields in create mode', () => {
    render(<CollectionFormModal isOpen onSubmit={vi.fn()} onClose={vi.fn()} />);
    expect(screen.getByRole('dialog', { name: /new collection/i })).toBeInTheDocument();
    expect(screen.getByLabelText(/^name/i)).toHaveValue('');
  });

  it('renders "Rename Collection" title pre-filled with the collection values in edit mode', () => {
    render(<CollectionFormModal isOpen collection={collection} onSubmit={vi.fn()} onClose={vi.fn()} />);
    expect(screen.getByRole('dialog', { name: /rename collection/i })).toBeInTheDocument();
    expect(screen.getByLabelText(/^name/i)).toHaveValue('Miniatures');
    expect(screen.getByLabelText(/description/i)).toHaveValue('My minis');
  });

  it('shows a validation error and does not submit when name is blank', async () => {
    const onSubmit = vi.fn();
    const user = userEvent.setup();
    render(<CollectionFormModal isOpen onSubmit={onSubmit} onClose={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: /create/i }));

    expect(await screen.findByText(/collection name is required/i)).toBeInTheDocument();
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('submits trimmed name and description', async () => {
    const onSubmit = vi.fn();
    const user = userEvent.setup();
    render(<CollectionFormModal isOpen onSubmit={onSubmit} onClose={vi.fn()} />);

    await user.type(screen.getByLabelText(/^name/i), '  Client Work  ');
    await user.click(screen.getByRole('button', { name: /create/i }));

    expect(onSubmit).toHaveBeenCalledWith({ name: 'Client Work', description: undefined });
  });

  it('disables inputs and shows a loading submit button while saving', () => {
    render(<CollectionFormModal isOpen isSaving onSubmit={vi.fn()} onClose={vi.fn()} />);
    expect(screen.getByLabelText(/^name/i)).toBeDisabled();
    expect(screen.getByRole('button', { name: /please wait/i })).toBeDisabled();
  });

  it('calls onClose when Cancel is clicked', async () => {
    const onClose = vi.fn();
    const user = userEvent.setup();
    render(<CollectionFormModal isOpen onSubmit={vi.fn()} onClose={onClose} />);

    await user.click(screen.getByRole('button', { name: /cancel/i }));
    expect(onClose).toHaveBeenCalled();
  });

  it('initializes fresh state each time it is remounted for a different collection (key-remount contract)', () => {
    const { rerender } = render(
      <CollectionFormModal key="a" isOpen collection={collection} onSubmit={vi.fn()} onClose={vi.fn()} />
    );
    expect(screen.getByLabelText(/^name/i)).toHaveValue('Miniatures');

    // Simulate the parent using a different `key` for a fresh "create" open, as documented
    // on the component: this forces a full remount rather than relying on an effect reset.
    rerender(<CollectionFormModal key="b" isOpen collection={null} onSubmit={vi.fn()} onClose={vi.fn()} />);
    expect(screen.getByLabelText(/^name/i)).toHaveValue('');
  });
});
