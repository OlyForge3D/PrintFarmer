import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { InstallerUpdatesExperience } from '@/features/admin/components/InstallerUpdatesExperience';
import { blockedReadinessInventory, conflictingReplicaInventory, digest, identity, inventory, replica } from '@/test/features/system/serviceInventoryFixture';

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
    expect(screen.getByText(/Proposed target train/).parentElement).toHaveTextContent('Unknown - target-release contract unavailable');
    expect(screen.getByText(/Proposed target train/).parentElement).not.toHaveTextContent('stable');
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

  it('compares canonical release/source identity across platforms but treats incomplete evidence as not comparable', () => {
    render(<InstallerUpdatesExperience inventory={inventory({ services: [
      replica({ identity, platform: 'linux/amd64', manifestDigest: digest, platformDigest: digest, indexDigest: digest }),
      replica({ instanceId: 'replica-b', identity: { ...identity, sourceCommit: 'f'.repeat(40) }, platform: 'linux/arm64', manifestDigest: `sha256:${'d'.repeat(64)}`, platformDigest: `sha256:${'e'.repeat(64)}`, indexDigest: digest }),
      replica({ instanceId: 'replica-c', identity: null, platform: null }),
    ] })} observation="connected" />);

    expect(screen.getByText(/different canonical release, application, or source evidence/)).toBeVisible();
    expect(screen.getByText('Missing canonical identity evidence')).toBeVisible();
    expect(screen.getByText('Missing platform digest evidence')).toBeVisible();
    expect(screen.getByText(/missing digest evidence is not reported as a conflict/)).toBeVisible();
  });

  it('does not render an empty replica observations heading or list', () => {
    render(<InstallerUpdatesExperience inventory={inventory({ services: [] })} observation="connected" />);
    expect(screen.queryByText('Replica observations')).not.toBeInTheDocument();
    expect(screen.queryByRole('list')).not.toBeInTheDocument();
  });

  it('uses named status and explained-disabled controls for keyboard and screen-reader users', async () => {
    const user = userEvent.setup();
    render(<InstallerUpdatesExperience inventory={inventory()} observation="connected" />);
    await user.tab();
    expect(screen.getByRole('button', { name: 'Update now' })).toHaveFocus();
    expect(screen.queryByRole('status', { name: '' })).not.toBeInTheDocument();
    const autoUpdate = screen.getByRole('checkbox', { name: /Enable auto-update for the selected train/i });
    expect(autoUpdate).toHaveAttribute('aria-describedby', 'auto-update-reason');
    expect(autoUpdate).toHaveAttribute('aria-disabled', 'true');
    expect(autoUpdate).not.toHaveAttribute('disabled');
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

  it('compares canonical evidence across coordinated services', () => {
    render(<InstallerUpdatesExperience inventory={inventory({ services: [
      replica({ serviceId: 'api', component: 'api', identity, applicationVersion: '1.2.3', sourceCommit: identity.sourceCommit }),
      replica({ serviceId: 'worker', component: 'worker', identity, applicationVersion: '1.2.4', sourceCommit: identity.sourceCommit }),
    ] })} observation="connected" />);

    expect(screen.getByText(/different canonical release, application, or source evidence/)).toBeVisible();
  });

  it('detects a partial known platform digest divergence', () => {
    render(<InstallerUpdatesExperience inventory={inventory({ services: [
      replica({ identity, platform: 'linux/amd64', manifestDigest: digest, platformDigest: null, indexDigest: null }),
      replica({ instanceId: 'replica-b', identity, platform: 'linux/amd64', manifestDigest: `sha256:${'f'.repeat(64)}`, platformDigest: null, indexDigest: null }),
    ] })} observation="connected" />);

    expect(screen.getByText(/Like-for-like observed replicas report different platform digest evidence/)).toBeVisible();
    expect(screen.queryByText('Missing platform digest evidence')).not.toBeInTheDocument();
  });

  it.each(['MixedRelease', 'Incompatible'] as const)(
    'renders authoritative observed compatibility conflict details for %s',
    (compatibilityState) => {
      render(<InstallerUpdatesExperience inventory={inventory({
        compatibilityState,
        compatibilityReasons: ['CanonicalReleaseDivergence'],
      })} observation="connected" />);

      expect(screen.getByText('Observed compatibility state').parentElement).toHaveTextContent(compatibilityState);
      expect(screen.getByText('Observed compatibility reasons').parentElement).toHaveTextContent('CanonicalReleaseDivergence');
      expect(screen.getByText('Observed compatibility conflict')).toBeVisible();
      expect(screen.getByText(new RegExp(`Observed compatibility is ${compatibilityState}`))).toBeVisible();
    },
  );

});
