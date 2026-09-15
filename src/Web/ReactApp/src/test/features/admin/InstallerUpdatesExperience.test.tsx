import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { InstallerUpdatesExperience } from '@/features/admin/components/InstallerUpdatesExperience';
import { blockedReadinessInventory, conflictingReplicaInventory, identity, inventory, replica } from '@/test/features/system/serviceInventoryFixture';

describe('InstallerUpdatesExperience', () => {
  it('keeps execution inaccessible to view-only users and explains the security prerequisites', async () => {
    const user = userEvent.setup();
    render(<InstallerUpdatesExperience inventory={inventory()} observation="connected" />);
    const update = screen.getByRole('button', { name: 'Update now' });
    expect(update).toHaveAttribute('aria-disabled', 'true');
    expect(update).not.toHaveAttribute('disabled');
    expect(screen.getByText(/runtime execution contract/)).toBeVisible();
    await user.click(update);
    expect(screen.getByText(/runtime execution contract/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Save automatic update policy' })).toHaveAttribute('aria-disabled', 'true');
  });

  it.each(['Eligible', 'Blocked', 'Unknown', 'NotManaged'] as const)(
    'renders %s readiness safely under #2661 semantics',
    (state) => {
      render(<InstallerUpdatesExperience inventory={inventory({
        eligibility: state,
        eligibilityReasons: null as unknown as string[],
        readiness: { state, reasons: null as unknown as string[], hops: null as unknown as string[] },
      })} observation="connected" />);
      expect(screen.getByText(new RegExp(`Readiness: ${state}[.] Eligibility: ${state}[.]`))).toBeVisible();
      expect(screen.getByText('Readiness reasons: Unknown. Readiness hops: Unknown.')).toBeVisible();
    },
  );

  it('labels service identities as observed/installed and keeps the target identity unknown', async () => {
    render(<InstallerUpdatesExperience inventory={inventory({ selectedChannel: 'insider', observedChannel: 'stable', targetChannel: 'stable', eligibility: 'Blocked', compatibilityState: 'MixedChannel', services: [replica({ identity })] })} observation="connected" />);
    expect(screen.getByText('Insider updates may arrive more frequently and have reduced stability compared with stable releases.')).toBeVisible();
    expect(screen.getByText(/Selected train/).parentElement).toHaveTextContent('insider');
    expect(screen.getByText(/Proposed target release identity: Unknown/)).toBeVisible();
    const observedReplicaSummary = screen.getByLabelText(
      'api (replica-a): insider:1.2.3-insider.10',
    );
    const observedReplica = observedReplicaSummary.closest('details');
    expect(observedReplica).not.toBeNull();
    await userEvent.setup().click(observedReplicaSummary);
    expect(within(observedReplica!).getByText(identity.releaseId)).toBeVisible();
    const sourceCommit = within(observedReplica!).getByText('Source commit')
      .parentElement;
    expect(sourceCommit).toBeVisible();
    expect(sourceCommit).toHaveTextContent(identity.sourceCommit);
    expect(screen.getByText(/downgrade is not offered as a bypass/)).toBeVisible();
    expect(screen.getByText(/Release notes and operation history are unavailable pending the read-only release contract/)).toBeVisible();
  });

  it('renders an unknown connection observation supplied by its page owner', () => {
    render(<InstallerUpdatesExperience inventory={inventory()} observation="unknown" />);
    expect(screen.getByRole('status', { name: /Connection observation unknown/ })).toHaveTextContent(/browser is disconnected/);
  });

  it('flags like-for-like replicas with the same identity but different deployment digests', async () => {
    const manifestDigest = `sha256:${'c'.repeat(64)}`;
    const platformDigest = `sha256:${'d'.repeat(64)}`;
    render(<InstallerUpdatesExperience inventory={inventory({ services: [
      replica({ identity, source: 'TrustedVerifier', verificationSource: 'local-verifier', verifiedAt: '2026-09-12T12:01:00Z', platform: 'linux/amd64', manifestDigest, platformDigest, indexDigest: `sha256:${'e'.repeat(64)}` }),
      replica({ instanceId: 'replica-b', identity, source: 'TrustedVerifier', verificationSource: 'local-verifier', verifiedAt: '2026-09-12T12:01:00Z', platform: 'linux/amd64', manifestDigest: `sha256:${'f'.repeat(64)}`, platformDigest, indexDigest: `sha256:${'e'.repeat(64)}` }),
    ] })} observation="connected" />);

    expect(screen.getByText(/Conflicting observed deployments/)).toBeVisible();
    expect(screen.getByText(/conflicting observed deployment state, not a proposed target/)).toBeVisible();
    const summary = screen.getByLabelText('api (replica-a): insider:1.2.3-insider.10');
    await userEvent.setup().click(summary);
    const details = summary.closest('details')!;
    expect(within(details).getByText('Verification quality').parentElement).toHaveTextContent('Verified');
    expect(within(details).getByText('Verification source').parentElement).toHaveTextContent('local-verifier');
    expect(within(details).getByText('Manifest digest').parentElement).toHaveTextContent(manifestDigest);
    expect(within(details).getByText('Platform digest').parentElement).toHaveTextContent(platformDigest);
  });

  it('uses named status and explained-disabled controls for keyboard and screen-reader users', async () => {
    const user = userEvent.setup();
    render(<InstallerUpdatesExperience inventory={inventory()} observation="connected" />);
    await user.tab();
    expect(screen.getByRole('button', { name: 'Update now' })).toHaveFocus();
    expect(screen.queryByRole('status', { name: '' })).not.toBeInTheDocument();
    expect(screen.getByRole('checkbox')).toHaveAttribute('aria-describedby', 'auto-update-reason');
  });


  it('presents blocked readiness even when eligibility is NotManaged and compatibility is Compatible', () => {
    render(<InstallerUpdatesExperience inventory={blockedReadinessInventory()} observation="connected" />);
    expect(screen.getByText(/The observed installation is blocked/)).toBeVisible();
    expect(screen.getByText(/Host maintenance is required/)).toBeVisible();
  });

  it('renders every observed replica and marks conflicting identities without proposing a target', async () => {
    render(<InstallerUpdatesExperience inventory={conflictingReplicaInventory()} observation="connected" />);
    expect(screen.getByText(/Conflicting observed deployments/)).toBeVisible();
    expect(screen.getByText(/Snapshot provenance: Imported/)).toBeVisible();
    expect(screen.getByText(/replica-b.*Stale/)).toBeVisible();
    expect(screen.getByText(/Proposed target release identity: Unknown/)).toBeVisible();
    await userEvent.setup().click(
      screen.getByLabelText(
        'api (replica-b): stable:1.2.4',
      ),
    );
    expect(screen.getByText('stable:1.2.4')).toBeVisible();
    const staleReplica = screen.getByLabelText('api (replica-b): stable:1.2.4').closest('details')!;
    expect(within(staleReplica).getByText('Verification quality').parentElement).toHaveTextContent('Imported; Stale');
  });
});
