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

  it.each(['Eligible', 'Blocked', 'Unknown', 'NotManaged'] as const)(
    'renders %s readiness safely under #2661 semantics',
    (state) => {
      render(<InstallerUpdatesExperience inventory={inventory({
        eligibility: state,
        eligibilityReasons: null as unknown as string[],
        readiness: { state, reasons: null as unknown as string[], hops: null as unknown as string[] },
      })} canExecute={false} refetch={vi.fn()} />);
      expect(screen.getByText(new RegExp(`Readiness: ${state}[.] Eligibility: ${state}[.]`))).toBeVisible();
      expect(screen.getByText('Readiness reasons: Unknown. Readiness hops: Unknown.')).toBeVisible();
    },
  );

  it('labels service identities as observed/installed and keeps the target identity unknown', async () => {
    render(<InstallerUpdatesExperience inventory={inventory({ selectedChannel: 'insider', observedChannel: 'stable', targetChannel: 'stable', eligibility: 'Blocked', compatibilityState: 'MixedChannel', services: [replica({ identity })] })} canExecute refetch={vi.fn()} />);
    expect(screen.getByText('Insider updates may arrive more frequently and have reduced stability compared with stable releases.')).toBeVisible();
    expect(screen.getByText(/Selected train/).parentElement).toHaveTextContent('insider');
    expect(screen.getByText(/Proposed target release identity: Unknown/)).toBeVisible();
    await userEvent.setup().click(screen.getByText('Observed/installed release identity'));
    expect(screen.getByText(identity.releaseId)).toBeVisible();
    expect(screen.getAllByText(identity.sourceCommit)).toHaveLength(2);
    expect(screen.getByText(/downgrade is not offered as a bypass/)).toBeVisible();
    expect(screen.getByText(/Release notes and operation history are unavailable pending the read-only release contract/)).toBeVisible();
  });

  it('announces offline observation in a live status region and reconciles after reconnect', async () => {
    const refetch = vi.fn();
    render(<InstallerUpdatesExperience inventory={inventory()} canExecute={false} refetch={refetch} />);
    await act(async () => { window.dispatchEvent(new Event('offline')); });
    expect(screen.getByRole('status', { name: /Connection observation unknown/ })).toHaveTextContent(/browser is disconnected/);
    await act(async () => { window.dispatchEvent(new Event('online')); });
    expect(refetch).toHaveBeenCalledOnce();
    expect(screen.queryByRole('status', { name: /Connection observation unknown/ })).not.toBeInTheDocument();
  });

  it('uses named status and explained-disabled controls for keyboard and screen-reader users', async () => {
    const user = userEvent.setup();
    render(<InstallerUpdatesExperience inventory={inventory()} canExecute={false} refetch={vi.fn()} />);
    await user.tab();
    expect(screen.getByRole('button', { name: 'Update now' })).toHaveFocus();
    expect(screen.getByRole('status', { name: '' })).toHaveTextContent(/Durable update operation records are unavailable/);
  });
});
