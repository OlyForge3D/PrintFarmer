import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { RevisionConflictDialog } from '../RevisionConflictDialog';

describe('RevisionConflictDialog', () => {
  const fields = [
    { label: 'Name', yourValue: 'New Name', serverValue: 'Someone Else Renamed It' },
    { label: 'Description', yourValue: '', serverValue: 'Existing description' },
  ];

  it('is not rendered when closed', () => {
    render(
      <RevisionConflictDialog
        isOpen={false}
        entityLabel="tag"
        fields={fields}
        onReloadLatest={vi.fn()}
        onCancel={vi.fn()}
      />
    );
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('renders an accessible dialog with the entity name in the title', () => {
    render(
      <RevisionConflictDialog
        isOpen
        entityLabel="tag"
        entityName="Resin"
        fields={fields}
        onReloadLatest={vi.fn()}
        onCancel={vi.fn()}
      />
    );
    expect(screen.getByRole('dialog', { name: /conflict updating "resin"/i })).toBeInTheDocument();
  });

  it('announces the conflict explanation via an alert region', () => {
    render(
      <RevisionConflictDialog isOpen entityLabel="tag" fields={fields} onReloadLatest={vi.fn()} onCancel={vi.fn()} />
    );
    expect(screen.getByRole('alert')).toHaveTextContent(/changed by someone else/i);
  });

  it('renders a field-by-field diff table showing both "your change" and "current version"', () => {
    render(
      <RevisionConflictDialog isOpen entityLabel="tag" fields={fields} onReloadLatest={vi.fn()} onCancel={vi.fn()} />
    );
    const table = screen.getByRole('table');
    expect(table).toHaveTextContent('New Name');
    expect(table).toHaveTextContent('Someone Else Renamed It');
    expect(table).toHaveTextContent('(empty)'); // blank "your value" for Description
    expect(table).toHaveTextContent('Existing description');
  });

  it('calls onReloadLatest when "Reload latest version" is clicked', async () => {
    const onReloadLatest = vi.fn();
    const user = userEvent.setup();
    render(
      <RevisionConflictDialog
        isOpen
        entityLabel="tag"
        fields={fields}
        onReloadLatest={onReloadLatest}
        onCancel={vi.fn()}
      />
    );
    await user.click(screen.getByRole('button', { name: /reload latest version/i }));
    expect(onReloadLatest).toHaveBeenCalled();
  });

  it('calls onCancel when Cancel is clicked, discarding the attempted change', async () => {
    const onCancel = vi.fn();
    const user = userEvent.setup();
    render(
      <RevisionConflictDialog isOpen entityLabel="tag" fields={fields} onReloadLatest={vi.fn()} onCancel={onCancel} />
    );
    await user.click(screen.getByRole('button', { name: /^cancel$/i }));
    expect(onCancel).toHaveBeenCalled();
  });

  it('disables both actions while a reload is in flight, never allowing a silent double-submit', () => {
    render(
      <RevisionConflictDialog
        isOpen
        entityLabel="tag"
        fields={fields}
        isReloading
        onReloadLatest={vi.fn()}
        onCancel={vi.fn()}
      />
    );
    expect(screen.getByRole('button', { name: /^cancel$/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /please wait/i })).toBeDisabled();
  });
});
