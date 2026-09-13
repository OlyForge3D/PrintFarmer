import { render, screen, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ServiceVersionsTable } from '@/features/system/components/ServiceVersionsTable';
import { buildInfo } from '@/common/utils/buildInfo';
import { commit, digest, identity, inventory, replica } from '@/test/features/system/serviceInventoryFixture';

vi.mock('@/common/utils/buildInfo', () => ({ buildInfo: { commit: 'a'.repeat(40), buildTime: '2026-09-12T12:00:00Z', releaseIdentity: null } }));

beforeEach(() => { buildInfo.commit = commit; buildInfo.releaseIdentity = null; });

describe('ServiceVersionsTable', () => {
  it('separates stable selection from unknown observed provenance without an update action', () => {
    render(<ServiceVersionsTable inventory={inventory()} />);
    expect(screen.getByText('stable (Default)')).toBeInTheDocument();
    expect(screen.getAllByText(/Unknown — Unknown/).length).toBeGreaterThan(0);
    expect(screen.getByText(/self-report is not verified running image provenance/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /update|install|check/i })).not.toBeInTheDocument();
  });

  it('keeps the exact persistent insider warning for selection or observed insider', () => {
    render(<ServiceVersionsTable inventory={inventory({ selectedChannel: 'insider' })} />);
    expect(screen.getByText('Insider updates may arrive more frequently and have reduced stability compared with stable releases.')).toBeVisible();
  });

  it('preserves mixed replicas and full canonical provenance without truncation', () => {
    render(<ServiceVersionsTable inventory={inventory({ compatibilityState: 'MixedChannel', channelState: 'Mixed', eligibility: 'Blocked',
      services: [replica(), replica({ instanceId: 'replica-b', observedChannel: 'insider', identity, source: 'VerifiedImport',
        platformDigest: digest, indexDigest: `sha256:${'c'.repeat(64)}`, manifestDigest: `sha256:${'d'.repeat(64)}`,
        channelState: 'Mismatch', verificationSource: 'LocalVerifiedImport' })] })} />);
    expect(screen.getByText('Mixed channels — blocked / unsafe')).toBeVisible();
    expect(screen.getByText('replica-a')).toBeVisible();
    expect(screen.getByText('replica-b')).toBeVisible();
    expect(screen.getByText('v1.2.3-insider.10')).toBeInTheDocument();
    expect(screen.getAllByText(commit).length).toBeGreaterThan(0);
    expect(screen.getByText(digest)).toBeInTheDocument();
    expect(screen.getByText('LocalVerifiedImport')).toBeInTheDocument();
    expect(screen.getByRole('table')).toBeInTheDocument();
  });

  it('shows textual stale, unavailable, optional absence, and separate engine/build', () => {
    render(<ServiceVersionsTable inventory={inventory({ services: [
      replica({ observationState: 'Stale', engineVersion: '2.4.2' }),
      replica({ instanceId: 'offline', observationState: 'Unavailable', observedAt: null, lastSuccessAt: null }),
      replica({ serviceId: 'discovery', component: 'discovery', instanceId: null, required: false, observationState: 'NotInstalled' }),
    ] })} />);
    expect(screen.getByText(/^Stale/)).toBeVisible();
    expect(screen.getByText(/^Unavailable/)).toBeVisible();
    expect(screen.getByText(/^NotInstalled/)).toBeVisible();
    expect(screen.getByText('Optional')).toBeVisible();
    expect(screen.getByText(/Engine: 2.4.2/)).toBeVisible();
    expect(screen.getAllByText(/Last observation:/).length).toBe(3);
  });

  it('identifies cached frontend/API skew from loaded bundle metadata, not API identity', () => {
    buildInfo.commit = 'e'.repeat(40);
    render(<ServiceVersionsTable inventory={inventory()} />);
    const assets = screen.getByRole('region', { name: 'Loaded frontend assets' });
    expect(within(assets).getByText(/e{40}/)).toBeVisible();
    expect(within(assets).getByText('Frontend/API build mismatch — refresh required')).toBeVisible();
    expect(screen.getByText('Incompatible: CachedFrontendMismatch')).toBeVisible();
    expect(screen.getByText('Blocked: CachedFrontendMismatch, ReadOnlyInventory')).toBeVisible();
    expect(within(assets).getByText(/Compatibility is not established by refresh alone/)).toBeVisible();
  });

  it('displays its own canonical asset record rather than copying API version', () => {
    buildInfo.releaseIdentity = identity;
    render(<ServiceVersionsTable inventory={inventory()} />);
    expect(screen.getByText(/Frontend canonical version: 1.2.3-insider.10/)).toBeVisible();
    expect(screen.getByText(/Frontend provenance: self-report/)).toBeVisible();
  });
});
