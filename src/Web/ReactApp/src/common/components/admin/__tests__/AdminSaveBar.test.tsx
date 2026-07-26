import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { AdminSaveBar } from '../AdminSaveBar';

const noop = () => undefined;

describe('AdminSaveBar', () => {
  it('renders nothing when isDirty=false', () => {
    const { container } = render(
      <AdminSaveBar isDirty={false} onDiscard={noop} onSave={noop} />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it('renders region with unsaved-changes label when dirty', () => {
    render(<AdminSaveBar isDirty onDiscard={noop} onSave={noop} />);
    expect(screen.getByRole('region', { name: 'Unsaved changes' })).toBeInTheDocument();
  });

  it('shows singular/plural change counts', () => {
    const { rerender } = render(
      <AdminSaveBar isDirty changeCount={1} onDiscard={noop} onSave={noop} />,
    );
    expect(screen.getByText(/1 unsaved change/)).toBeInTheDocument();
    rerender(<AdminSaveBar isDirty changeCount={3} onDiscard={noop} onSave={noop} />);
    expect(screen.getByText(/3 unsaved changes/)).toBeInTheDocument();
  });

  it('renders enumerated labels when supplied, truncating past 3', () => {
    render(
      <AdminSaveBar
        isDirty
        changedLabels={['Name', 'Email', 'Timezone', 'Locale', 'Language']}
        onDiscard={noop}
        onSave={noop}
      />,
    );
    expect(screen.getByText(/Name, Email, Timezone and 2 more changed/)).toBeInTheDocument();
  });

  it('calls onDiscard when the discard button is clicked', async () => {
    const user = userEvent.setup();
    const onDiscard = vi.fn();
    render(<AdminSaveBar isDirty onDiscard={onDiscard} onSave={noop} />);
    await user.click(screen.getByRole('button', { name: /discard/i }));
    expect(onDiscard).toHaveBeenCalledTimes(1);
  });

  it('calls onSave when the save button is clicked', async () => {
    const user = userEvent.setup();
    const onSave = vi.fn();
    render(<AdminSaveBar isDirty onDiscard={noop} onSave={onSave} />);
    await user.click(screen.getByRole('button', { name: /save changes/i }));
    expect(onSave).toHaveBeenCalledTimes(1);
  });

  it('awaits async onSave without swallowing exceptions', async () => {
    const user = userEvent.setup();
    let resolve!: () => void;
    const p = new Promise<void>(r => { resolve = r; });
    const onSave = vi.fn().mockReturnValue(p);
    render(<AdminSaveBar isDirty onDiscard={noop} onSave={onSave} />);
    await user.click(screen.getByRole('button', { name: /save changes/i }));
    expect(onSave).toHaveBeenCalledTimes(1);
    resolve();
    await p;
  });

  it('disables both actions while isSaving=true', () => {
    render(
      <AdminSaveBar isDirty isSaving onDiscard={noop} onSave={noop} />,
    );
    expect(screen.getByRole('button', { name: /discard/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /please wait/i })).toBeDisabled();
  });

  it('renders an inline error alert when error is provided', () => {
    render(
      <AdminSaveBar isDirty error="Save failed" onDiscard={noop} onSave={noop} />,
    );
    const alerts = screen.getAllByRole('alert');
    expect(alerts.some(a => a.textContent === 'Save failed')).toBe(true);
  });

  it('honours custom save and discard labels', () => {
    render(
      <AdminSaveBar
        isDirty
        saveLabel="Publish"
        discardLabel="Revert"
        onDiscard={noop}
        onSave={noop}
      />,
    );
    expect(screen.getByRole('button', { name: 'Publish' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Revert' })).toBeInTheDocument();
  });
});
