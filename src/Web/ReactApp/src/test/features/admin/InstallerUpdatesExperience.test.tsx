import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { InstallerUpdatesExperience } from '@/features/admin/components/InstallerUpdatesExperience';
import { identity, inventory, replica } from '@/test/features/system/serviceInventoryFixture';

describe('InstallerUpdatesExperience', () => {
  it('keeps execution inaccessible to view-only users and explains the security prerequisites', async () => {
    const user = userEvent.setup();
    render(<InstallerUpdatesExperience inventory={inventory()} canExecute={false} refetch={vi.fn()} />);
    const update = screen.getByRole('button', { name: 'Update now' });
    expect(update).toHaveAttribute('aria-disabled', 'true');
    expect(update).not.toHaveAttribute('disabled');
    expect(screen.getByText(/Administrator execute authorization is required/)).toBeVisible();
    await user.click(update);
    expect(screen.getByText(/Administrator execute authorization is required/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Save automatic update policy' })).toHaveAttribute('aria-disabled', 'true');
  });

  it('renders immutable provenance, persistent insider warning, and blocked downgrade guidance', async () => {
    render(<InstallerUpdatesExperience inventory={inventory({ selectedChannel: 'insider', observedChannel: 'stable', targetChannel: 'stable', eligibility: 'Blocked', compatibilityState: 'MixedChannel', services: [replica({ identity })] })} canExecute refetch={vi.fn()} />);
    expect(screen.getByText('Insider updates may arrive more frequently and have reduced stability compared with stable releases.')).toBeVisible();
    expect(screen.getByText(/Selected train/).parentElement).toHaveTextContent('insider');
    await userEvent.setup().click(screen.getByText('Immutable target provenance'));
    expect(screen.getByText(identity.releaseId)).toBeVisible();
    expect(screen.getAllByText(identity.sourceCommit)).toHaveLength(2);
    expect(screen.getByText(/downgrade is not offered as a bypass/)).toBeVisible();
  });

  it('marks offline observation unknown and reconciles through a refetch after reconnect', async () => {
    const refetch = vi.fn();
    render(<InstallerUpdatesExperience inventory={inventory()} canExecute={false} refetch={refetch} />);
    await act(async () => { window.dispatchEvent(new Event('offline')); });
    expect(screen.getByText('Connection observation unknown')).toBeVisible();
    await act(async () => { window.dispatchEvent(new Event('online')); });
    expect(refetch).toHaveBeenCalledOnce();
    expect(screen.queryByText(/Connection observation unknown/)).not.toBeInTheDocument();
  });

  it('uses named status and explained-disabled controls for keyboard and screen-reader users', async () => {
    const user = userEvent.setup();
    render(<InstallerUpdatesExperience inventory={inventory()} canExecute={false} refetch={vi.fn()} />);
    await user.tab();
    expect(screen.getByRole('button', { name: 'Update now' })).toHaveFocus();
    expect(screen.getByRole('status', { name: '' })).toHaveTextContent(/No durable update operation records/);
  });
});
