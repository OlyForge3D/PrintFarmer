import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { InstallerUpdatesExperience } from '@/features/admin/components/InstallerUpdatesExperience';
import type { InstallerUpdatesExperienceProps } from '@/features/admin/components/InstallerUpdatesExperience';
import { UpdateChannelSaveRejectedError } from '@/features/admin/utils/updateChannelSaveErrors';
import { blockedReadinessInventory, conflictingReplicaInventory, digest, identity, inventory, replica } from '@/test/features/system/serviceInventoryFixture';
import type { UpdateSchedulingExecutorState } from '@/types/api';

type TestInstallerUpdatesExperienceProps = Omit<
  InstallerUpdatesExperienceProps,
  'onGetHostUpdateStatus'
> & Partial<Pick<InstallerUpdatesExperienceProps, 'onGetHostUpdateStatus'>>;

const defaultGetHostUpdateStatus: InstallerUpdatesExperienceProps['onGetHostUpdateStatus'] = vi.fn().mockRejectedValue({
  statusCode: 404,
  message: 'Not found',
});

function TestInstallerUpdatesExperience(props: TestInstallerUpdatesExperienceProps) {
  return (
    <InstallerUpdatesExperience
      {...props}
      onGetHostUpdateStatus={props.onGetHostUpdateStatus ?? defaultGetHostUpdateStatus}
    />
  );
}

describe('InstallerUpdatesExperience', () => {
  afterEach(() => {
    vi.useRealTimers();
    window.localStorage.removeItem('printfarmer.manual-host-update.release-id');
  });

  it('keeps execution inaccessible to view-only users and explains the security prerequisites', async () => {
    const user = userEvent.setup();
    render(<TestInstallerUpdatesExperience inventory={inventory()} observation="connected" />);
    const update = screen.getByRole('button', { name: 'Update now' });
    expect(update).toHaveAttribute('aria-disabled', 'true');
    expect(update).not.toHaveAttribute('disabled');
    expect(screen.getByText(/runtime execution contract/)).toBeVisible();
    await user.click(update);
    expect(screen.getByText(/runtime execution contract/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Save automatic update policy' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByText(/Availability is read-only until trusted host evidence/)).toBeVisible();
  });

  it('confirms, reports progress, and offers recovery for a manual update', async () => {
    const user = userEvent.setup();
    const authorize = vi.fn().mockResolvedValue({
      authorizationId: 'auth-1',
      releaseId: 'stable:1.2.4',
      sequence: 4,
      channel: 'stable',
      candidateFingerprint: 'candidate',
      policyRevision: 1,
      policyFingerprint: 'policy',
      expiresAt: '2026-09-19T20:00:00Z',
    });

    const execute = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'RecoveryRequired',
      activities: [{
        activityId: 'activity-1',
        releaseId: 'stable:1.2.4',
        state: 'RecoveryRequired',
        phase: 'apply',
        recordedAt: '2026-09-19T19:00:00Z',
      }],
    });
    const recover = vi.fn().mockResolvedValue({
      outcome: 'RolledBack',
      detail: 'image_only_rollback',
    });
    const status = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'Completed',
      activities: [{
        activityId: 'activity-2',
        releaseId: 'stable:1.2.4',
        state: 'Completed',
        phase: 'verify',
        recordedAt: '2026-09-19T19:01:00Z',
      }],
    });

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
      onGetHostUpdateStatus={status}
      onRecoverHostUpdate={recover}
    />);

    await user.click(screen.getByRole('button', { name: 'Update now' }));
    expect(screen.getByRole('heading', { name: 'Confirm host update' })).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Authorize and update' }));
    expect(authorize).toHaveBeenCalledOnce();
    expect(execute).toHaveBeenCalledWith('auth-1');
    expect(await screen.findByText('RecoveryRequired')).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Recover update' }));
    await screen.findByText(/RolledBack: image_only_rollback/);
    expect(recover).toHaveBeenCalledWith('stable:1.2.4');
    expect(status).toHaveBeenCalledWith('stable:1.2.4');
    expect(screen.getByRole('list', { name: 'Host update progress' })).toBeVisible();
    expect(screen.getByRole('heading', { name: 'Host update progress' })).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Authorize and update' })).not.toBeInTheDocument();
    expect(screen.getByText('RolledBack: image_only_rollback')).toBeVisible();
  });

  it('rehydrates a persisted in-flight update after a page-level remount', async () => {
    const status = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'Applying',
      activities: [],
    });

    const props = {
      inventory: inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } }),
      observation: 'connected' as const,
      onGetHostUpdateStatus: status,
    };
    window.localStorage.setItem('printfarmer.manual-host-update.release-id', 'stable:1.2.4');
    const firstMount = render(<TestInstallerUpdatesExperience {...props} />);

    expect(await screen.findByText('Applying')).toBeVisible();
    expect(status).toHaveBeenCalledWith('stable:1.2.4');
    expect(screen.getByRole('heading', { name: 'Host update progress' })).toBeVisible();
    const freshStatus = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'Applying',
      activities: [],
    });
    const { rerender } = firstMount;
    await userEvent.setup().click(screen.getByRole('button', { name: 'Close' }));
    rerender(<TestInstallerUpdatesExperience {...props} onGetHostUpdateStatus={freshStatus} />);
    expect(screen.queryByRole('heading', { name: 'Host update progress' })).not.toBeInTheDocument();
    expect(freshStatus).not.toHaveBeenCalled();
    firstMount.unmount();
    status.mockClear();
    render(<TestInstallerUpdatesExperience {...props} />);
    expect(await screen.findByText('Applying')).toBeVisible();
    expect(status).toHaveBeenCalledWith('stable:1.2.4');
  });

  it('preserves progress when the update modal is closed and reopened', async () => {
    const user = userEvent.setup();
    const execute = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'Applying',
      activities: [],
    });
    const authorize = vi.fn().mockResolvedValue({
      authorizationId: 'auth-1',
      releaseId: 'stable:1.2.4',
      sequence: 4,
      channel: 'stable',
      candidateFingerprint: 'candidate',
      policyRevision: 1,
      policyFingerprint: 'policy',
      expiresAt: '2026-09-19T20:00:00Z',
    });

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
    />);

    await user.click(screen.getByRole('button', { name: 'Update now' }));
    await user.click(screen.getByRole('button', { name: 'Authorize and update' }));
    expect(await screen.findByText('Applying')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Close' }));
    await user.click(screen.getByRole('button', { name: 'Update now' }));

    expect(screen.getByText('Applying')).toBeVisible();
    expect(authorize).toHaveBeenCalledOnce();
    expect(execute).toHaveBeenCalledOnce();
  });

  it('reconciles status after a timed-out execute using the retained release id', async () => {
    const user = userEvent.setup();
    const authorize = vi.fn().mockResolvedValue({
      authorizationId: 'auth-1',
      releaseId: 'stable:1.2.4',
      sequence: 4,
      channel: 'stable',
      candidateFingerprint: 'candidate',
      policyRevision: 1,
      policyFingerprint: 'policy',
      expiresAt: '2026-09-19T20:00:00Z',
    });
    const execute = vi.fn().mockImplementation(
      () => new Promise((_, reject) => {
        window.setTimeout(() => reject({
          statusCode: 504,
          message: 'The host update request timed out while execution may still be running.',
        }), 30_000);
      }),
    );
    const status = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'Applying',
      activities: [],
    });

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
      onGetHostUpdateStatus={status}
    />);

    await user.click(screen.getByRole('button', { name: 'Update now' }));
    vi.useFakeTimers();
    fireEvent.click(screen.getByRole('button', { name: 'Authorize and update' }));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(30_000);
    });
    vi.useRealTimers();
    expect(await screen.findByText('Applying')).toBeVisible();
    expect(status).toHaveBeenCalledWith('stable:1.2.4');
    expect(authorize).toHaveBeenCalledOnce();
    expect(execute).toHaveBeenCalledOnce();
  });

  it('renders plain ApiError messages when the response body is absent', async () => {
    const user = userEvent.setup();
    const authorize = vi.fn().mockResolvedValue({
      authorizationId: 'auth-1',
      releaseId: 'stable:1.2.4',
      sequence: 4,
      channel: 'stable',
      candidateFingerprint: 'candidate',
      policyRevision: 1,
      policyFingerprint: 'policy',
      expiresAt: '2026-09-19T20:00:00Z',
    });
    const execute = vi.fn().mockRejectedValue({
      statusCode: 500,
      message: 'The server could not finish the update request.',
    });

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
    />);

    await user.click(screen.getByRole('button', { name: 'Update now' }));
    await user.click(screen.getByRole('button', { name: 'Authorize and update' }));

    expect(await screen.findByText('The server could not finish the update request.')).toBeVisible();
  });

  it('prevents a second execute after the first attempt has an uncertain outcome', async () => {
    const user = userEvent.setup();
    const execute = vi.fn().mockRejectedValue({
      statusCode: 504,
      message: 'The host update request timed out while execution may still be running.',
    });
    const authorize = vi.fn().mockResolvedValue({
      authorizationId: 'auth-1',
      releaseId: 'stable:1.2.4',
      sequence: 4,
      channel: 'stable',
      candidateFingerprint: 'candidate',
      policyRevision: 1,
      policyFingerprint: 'policy',
      expiresAt: '2026-09-19T20:00:00Z',
    });

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
    />);

    await user.click(screen.getByRole('button', { name: 'Update now' }));
    const action = screen.getByRole('button', { name: 'Authorize and update' });
    await user.click(action);
    expect(await screen.findByText('The host update request timed out while execution may still be running.')).toBeVisible();
    expect(action).toHaveAttribute('title', "The previous attempt's outcome is unknown — refresh status or reload before retrying.");
    await user.click(action);

    expect(authorize).toHaveBeenCalledOnce();
    expect(execute).toHaveBeenCalledOnce();
  });

  it('serializes rapid execute events before the disabled state commits', async () => {
    let resolveAuthorization!: (value: {
      authorizationId: string;
      releaseId: string;
    }) => void;
    const authorize = vi.fn().mockImplementation(() => new Promise((resolve) => {
      resolveAuthorization = resolve;
    }));
    const execute = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'Applying',
      activities: [],
    });

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
    />);

    await userEvent.setup().click(screen.getByRole('button', { name: 'Update now' }));
    const action = screen.getByRole('button', { name: 'Authorize and update' });
    act(() => {
      fireEvent.click(action);
      fireEvent.click(action);
    });
    expect(authorize).toHaveBeenCalledOnce();

    await act(async () => {
      resolveAuthorization({ authorizationId: 'auth-1', releaseId: 'stable:1.2.4' });
    });
    await waitFor(() => expect(execute).toHaveBeenCalledOnce());
  });

  it('unlocks a stale persisted update after status rehydration returns 404', async () => {
    window.localStorage.setItem('printfarmer.manual-host-update.release-id', 'stale-release');
    const status = vi.fn().mockRejectedValue({ statusCode: 404, message: 'Not found' });
    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onGetHostUpdateStatus={status}
      onAuthorizeHostUpdate={vi.fn()}
      onExecuteHostUpdate={vi.fn()}
    />);

    await waitFor(() => expect(screen.getByRole('button', { name: 'Update now' })).not.toBeDisabled());
    expect(status).toHaveBeenCalledWith('stale-release');
  });

  it('self-heals an empty persisted release id without leaving the update control busy', async () => {
    window.localStorage.setItem('printfarmer.manual-host-update.release-id', '');
    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onGetHostUpdateStatus={vi.fn()}
      onAuthorizeHostUpdate={vi.fn()}
      onExecuteHostUpdate={vi.fn()}
    />);

    const update = screen.getByRole('button', { name: 'Update now' });
    expect(update).not.toHaveAttribute('aria-disabled', 'true');
    expect(update).not.toHaveAttribute('aria-busy', 'true');
    expect(window.localStorage.getItem('printfarmer.manual-host-update.release-id')).toBeNull();
  });

  it('serializes rapid recovery events before the busy state commits', async () => {
    let resolveRecovery!: (value: { outcome: 'NeedsOperator'; detail: string }) => void;
    const recover = vi.fn().mockImplementation(() => new Promise((resolve) => {
      resolveRecovery = resolve;
    }));
    const status = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'RecoveryRequired',
      activities: [],
    });
    window.localStorage.setItem('printfarmer.manual-host-update.release-id', 'stable:1.2.4');
    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onGetHostUpdateStatus={status}
      onRecoverHostUpdate={recover}
    />);

    await screen.findByRole('button', { name: 'Recover update' });
    const action = screen.getByRole('button', { name: 'Recover update' });
    act(() => {
      fireEvent.click(action);
      fireEvent.click(action);
    });
    expect(recover).toHaveBeenCalledOnce();

    await act(async () => {
      resolveRecovery({ outcome: 'NeedsOperator', detail: 'manual_intervention_required' });
    });
  });

  it('reports a rolled-back terminal activity instead of update success', async () => {
    window.localStorage.setItem('printfarmer.manual-host-update.release-id', 'stable:1.2.4');
    const status = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'Completed',
      activities: [{
        activityId: 'recovery-1',
        releaseId: 'stable:1.2.4',
        state: 'Completed',
        phase: 'recovery:rolled_back',
        recordedAt: '2026-09-19T19:01:00Z',
      }],
    });
    const mounted = render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onGetHostUpdateStatus={status}
    />);

    expect(await screen.findByText('Host update rolled back')).toBeVisible();
    expect(screen.queryByText('Host update completed')).not.toBeInTheDocument();
    expect(window.localStorage.getItem('printfarmer.manual-host-update.release-id')).toBeNull();
    status.mockClear();
    mounted.unmount();
    const remounted = render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onGetHostUpdateStatus={status}
      onAuthorizeHostUpdate={vi.fn()}
      onExecuteHostUpdate={vi.fn()}
    />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Update now' })).not.toHaveAttribute('aria-disabled', 'true'));
    expect(status).not.toHaveBeenCalled();
    remounted.unmount();
  });

  it('uses the terminal activity when a later retry completes successfully', async () => {
    window.localStorage.setItem('printfarmer.manual-host-update.release-id', 'stable:1.2.4');
    const status = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'Completed',
      activities: [
        {
          activityId: 'recovery-1',
          releaseId: 'stable:1.2.4',
          state: 'Completed',
          phase: 'recovery:rolled_back',
          recordedAt: '2026-09-19T19:01:00Z',
        },
        {
          activityId: 'verify-2',
          releaseId: 'stable:1.2.4',
          state: 'Completed',
          phase: 'verify',
          recordedAt: '2026-09-19T19:02:00Z',
        },
      ],
    });
    const mounted = render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onGetHostUpdateStatus={status}
    />);

    expect(await screen.findByText('Host update completed')).toBeVisible();
    expect(screen.queryByText('Host update rolled back')).not.toBeInTheDocument();
    expect(window.localStorage.getItem('printfarmer.manual-host-update.release-id')).toBeNull();
    status.mockClear();
    mounted.unmount();
    const remounted = render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onGetHostUpdateStatus={status}
      onAuthorizeHostUpdate={vi.fn()}
      onExecuteHostUpdate={vi.fn()}
    />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Update now' })).not.toHaveAttribute('aria-disabled', 'true'));
    expect(status).not.toHaveBeenCalled();
    remounted.unmount();
  });

  it('refreshes a non-terminal update status after dispatch', async () => {
    const user = userEvent.setup();
    let resolveStatus!: (value: { releaseId: string; currentState: 'Applying'; activities: never[] }) => void;
    const execute = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'Applying',
      activities: [],
    });
    const status = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'Applying',
      activities: [],
    });
    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={vi.fn().mockResolvedValue({ authorizationId: 'auth-1', releaseId: 'stable:1.2.4' })}
      onExecuteHostUpdate={execute}
      onGetHostUpdateStatus={status}
    />);

    await user.click(screen.getByRole('button', { name: 'Update now' }));
    await user.click(screen.getByRole('button', { name: 'Authorize and update' }));
    await screen.findByText('Applying');
    status.mockClear();
    status.mockImplementation(() => new Promise((resolve) => {
      resolveStatus = resolve;
    }));
    await user.click(screen.getByRole('button', { name: 'Refresh update status' }));
    await waitFor(() => expect(status).toHaveBeenCalledWith('stable:1.2.4'));
    await act(async () => {
      resolveStatus({ releaseId: 'stable:1.2.4', currentState: 'Applying', activities: [] });
    });
  });

  it('serializes rapid refresh events before the busy state commits', async () => {
    let resolveStatus!: (value: { releaseId: string; currentState: 'Applying'; activities: never[] }) => void;
    const status = vi.fn().mockResolvedValueOnce({
      releaseId: 'stable:1.2.4',
      currentState: 'Applying',
      activities: [],
    }).mockImplementation(() => new Promise((resolve) => {
      resolveStatus = resolve;
    }));
    window.localStorage.setItem('printfarmer.manual-host-update.release-id', 'stable:1.2.4');
    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onGetHostUpdateStatus={status}
    />);

    await screen.findByText('Applying');
    status.mockClear();
    const action = screen.getByRole('button', { name: 'Refresh update status' });
    act(() => {
      fireEvent.click(action);
      fireEvent.click(action);
    });
    expect(status).toHaveBeenCalledOnce();
    await act(async () => {
      resolveStatus({ releaseId: 'stable:1.2.4', currentState: 'Applying', activities: [] });
    });
  });

  it('clears busy state when stale rehydration resolves after persisted identity changes', async () => {
    let resolveStatus!: (value: { releaseId: string; currentState: 'Applying'; activities: never[] }) => void;
    const status = vi.fn().mockImplementation(() => new Promise((resolve) => {
      resolveStatus = resolve;
    }));
    window.localStorage.setItem('printfarmer.manual-host-update.release-id', 'stable:1.2.4');
    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onGetHostUpdateStatus={status}
      onAuthorizeHostUpdate={vi.fn()}
      onExecuteHostUpdate={vi.fn()}
    />);

    await waitFor(() => expect(status).toHaveBeenCalledWith('stable:1.2.4'));
    window.localStorage.setItem('printfarmer.manual-host-update.release-id', 'stable:1.2.5');
    await act(async () => {
      resolveStatus({ releaseId: 'stable:1.2.4', currentState: 'Applying', activities: [] });
    });
    expect(screen.queryByText('Applying')).not.toBeInTheDocument();
    const update = screen.getByRole('button', { name: 'Update now' });
    expect(update).not.toHaveAttribute('aria-busy', 'true');
    expect(update).not.toHaveAttribute('aria-disabled', 'true');
    expect(window.localStorage.getItem('printfarmer.manual-host-update.release-id')).toBe('stable:1.2.5');
  });

  it('allows a second direct update after the first one completes', async () => {
    const authorize = vi.fn()
      .mockResolvedValueOnce({
        authorizationId: 'auth-1',
        releaseId: 'stable:1.2.4',
        sequence: 4,
        channel: 'stable',
        candidateFingerprint: 'candidate',
        policyRevision: 1,
        policyFingerprint: 'policy',
        expiresAt: '2026-09-19T20:00:00Z',
      })
      .mockResolvedValueOnce({
        authorizationId: 'auth-2',
        releaseId: 'stable:1.2.5',
        sequence: 5,
        channel: 'stable',
        candidateFingerprint: 'candidate-2',
        policyRevision: 1,
        policyFingerprint: 'policy',
        expiresAt: '2026-09-19T20:00:00Z',
      });
    const execute = vi.fn()
      .mockResolvedValueOnce({ releaseId: 'stable:1.2.4', currentState: 'Completed', activities: [] })
      .mockResolvedValueOnce({ releaseId: 'stable:1.2.5', currentState: 'Completed', activities: [] });
    const user = userEvent.setup();

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
    />);

    await user.click(screen.getByRole('button', { name: 'Update now' }));
    await user.click(screen.getByRole('button', { name: 'Authorize and update' }));
    await screen.findByText('Host update completed');
    await user.click(screen.getByRole('button', { name: 'Close' }));
    await user.click(screen.getByRole('button', { name: 'Update now' }));
    await user.click(screen.getByRole('button', { name: 'Authorize and update' }));

    await waitFor(() => expect(authorize).toHaveBeenCalledTimes(2));
    expect(execute).toHaveBeenCalledTimes(2);
  });

  it('shows a blocked alert when execute returns a non-status conflict body', async () => {
    const authorize = vi.fn().mockResolvedValue({
      authorizationId: 'auth-1',
      releaseId: 'stable:1.2.4',
      sequence: 4,
      channel: 'stable',
      candidateFingerprint: 'candidate',
      policyRevision: 1,
      policyFingerprint: 'policy',
      expiresAt: '2026-09-19T20:00:00Z',
    });
    const execute = vi.fn()
      .mockRejectedValueOnce({
        statusCode: 409,
        message: 'The host update authorization was rejected.',
        data: { code: 'request_not_authorized' },
      })
      .mockResolvedValueOnce({
        releaseId: 'stable:1.2.4',
        currentState: 'Completed',
        activities: [],
      });

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
    />);

    await userEvent.setup().click(screen.getByRole('button', { name: 'Update now' }));
    await userEvent.setup().click(screen.getByRole('button', { name: 'Authorize and update' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The host update authorization was rejected.');
    expect(screen.queryByRole('list', { name: 'Host update progress' })).not.toBeInTheDocument();

    await userEvent.setup().click(screen.getByRole('button', { name: 'Close' }));
    await userEvent.setup().click(screen.getByRole('button', { name: 'Update now' }));
    await userEvent.setup().click(screen.getByRole('button', { name: 'Authorize and update' }));
    await waitFor(() => expect(authorize).toHaveBeenCalledTimes(2));
    expect(execute).toHaveBeenCalledTimes(2);
  });

  it('reports an existing update when execute returns a status conflict', async () => {
    const authorize = vi.fn().mockResolvedValue({
      authorizationId: 'auth-1',
      releaseId: 'stable:1.2.4',
      sequence: 4,
      channel: 'stable',
      candidateFingerprint: 'candidate',
      policyRevision: 1,
      policyFingerprint: 'policy',
      expiresAt: '2026-09-19T20:00:00Z',
    });
    const execute = vi.fn().mockResolvedValue({
      kind: 'conflict',
      status: {
        releaseId: 'stable:1.2.5',
        currentState: 'Applying',
        activities: [],
      },
    });
    const user = userEvent.setup();

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
      onGetHostUpdateStatus={vi.fn()}
    />);

    await user.click(screen.getByRole('button', { name: 'Update now' }));
    await user.click(screen.getByRole('button', { name: 'Authorize and update' }));

    expect(await screen.findByText('Another host update is already in progress.')).toBeVisible();
    expect(screen.getByText('Applying')).toBeVisible();
    expect(window.localStorage.getItem('printfarmer.manual-host-update.release-id')).toBe('stable:1.2.5');
  });

  it('releases a rejected execute after a non-terminal status recheck', async () => {
    const authorize = vi.fn()
      .mockResolvedValueOnce({
        authorizationId: 'auth-1',
        releaseId: 'stable:1.2.4',
        sequence: 4,
        channel: 'stable',
        candidateFingerprint: 'candidate',
        policyRevision: 1,
        policyFingerprint: 'policy',
        expiresAt: '2026-09-19T20:00:00Z',
      })
      .mockResolvedValueOnce({
        authorizationId: 'auth-2',
        releaseId: 'stable:1.2.5',
        sequence: 5,
        channel: 'stable',
        candidateFingerprint: 'candidate-2',
        policyRevision: 1,
        policyFingerprint: 'policy',
        expiresAt: '2026-09-19T20:00:00Z',
      });

    const execute = vi.fn()
      .mockRejectedValueOnce({
        statusCode: 409,
        message: 'The host update authorization was rejected.',
        data: { code: 'request_not_authorized' },
      })
      .mockResolvedValueOnce({
        releaseId: 'stable:1.2.5',
        currentState: 'Completed',
        activities: [],
      });
    const status = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'Applying',
      activities: [],
    });
    const user = userEvent.setup();

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
      onGetHostUpdateStatus={status}
    />);

    await user.click(screen.getByRole('button', { name: 'Update now' }));
    await user.click(screen.getByRole('button', { name: 'Authorize and update' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('The host update authorization was rejected.');
    expect(status).toHaveBeenCalledWith('stable:1.2.4');
    expect(screen.getByRole('button', { name: 'Authorize and update' })).not.toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Authorize and update' }));
    await waitFor(() => expect(authorize).toHaveBeenCalledTimes(2));
    expect(execute).toHaveBeenCalledTimes(2);
  });

  it.each([401, 403])('releases an execute rejection for HTTP %s', async (statusCode) => {
    const authorize = vi.fn().mockResolvedValue({
      authorizationId: 'auth-1',
      releaseId: 'stable:1.2.4',
      sequence: 4,
      channel: 'stable',
      candidateFingerprint: 'candidate',
      policyRevision: 1,
      policyFingerprint: 'policy',
      expiresAt: '2026-09-19T20:00:00Z',
    });
    const execute = vi.fn().mockRejectedValue({ statusCode, message: 'Authorization required.' });
    const user = userEvent.setup();

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
    />);

    await user.click(screen.getByRole('button', { name: 'Update now' }));
    await user.click(screen.getByRole('button', { name: 'Authorize and update' }));
    expect(await screen.findByText('Authorization required.')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Close' }));
    await user.click(screen.getByRole('button', { name: 'Update now' }));
    const retry = screen.getByRole('button', { name: 'Authorize and update' });
    expect(retry).not.toHaveAttribute('aria-disabled', 'true');
    await user.click(retry);
    await waitFor(() => expect(authorize).toHaveBeenCalledTimes(2));
    expect(execute).toHaveBeenCalledTimes(2);
  });

  it('allows a retry after recovery terminates with NeedsOperator', async () => {
    const authorize = vi.fn().mockResolvedValue({
      authorizationId: 'auth-1',
      releaseId: 'stable:1.2.4',
      sequence: 4,
      channel: 'stable',
      candidateFingerprint: 'candidate',
      policyRevision: 1,
      policyFingerprint: 'policy',
      expiresAt: '2026-09-19T20:00:00Z',
    });

    const execute = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'RecoveryRequired',
      activities: [],
    });
    const recover = vi.fn().mockResolvedValue({
      outcome: 'NeedsOperator',
      detail: 'manual_intervention_required',
    });
    const status = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'RecoveryRequired',
      activities: [],
    });
    const user = userEvent.setup();

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
      onGetHostUpdateStatus={status}
      onRecoverHostUpdate={recover}
    />);

    await user.click(screen.getByRole('button', { name: 'Update now' }));
    await user.click(screen.getByRole('button', { name: 'Authorize and update' }));
    await user.click(await screen.findByRole('button', { name: 'Recover update' }));
    expect(await screen.findByText(/NeedsOperator: manual_intervention_required/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Recover update' })).toBeVisible();
    expect(window.localStorage.getItem('printfarmer.manual-host-update.release-id')).toBe('stable:1.2.4');
  });

  it('explains fence-release-pending recovery without releasing the operation latch', async () => {
    const authorize = vi.fn().mockResolvedValue({
      authorizationId: 'auth-1',
      releaseId: 'stable:1.2.4',
      sequence: 4,
      channel: 'stable',
      candidateFingerprint: 'candidate',
      policyRevision: 1,
      policyFingerprint: 'policy',
      expiresAt: '2026-09-19T20:00:00Z',
    });
    const execute = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'RecoveryRequired',
      activities: [],
    });
    const recover = vi.fn().mockResolvedValue({
      outcome: 'FenceReleasePending',
      detail: 'fence_pending',
    });
    const status = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'RecoveryRequired',
      activities: [],
    });
    const user = userEvent.setup();

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
      onGetHostUpdateStatus={status}
      onRecoverHostUpdate={recover}
    />);

    await user.click(screen.getByRole('button', { name: 'Update now' }));
    await user.click(screen.getByRole('button', { name: 'Authorize and update' }));
    await user.click(await screen.findByRole('button', { name: 'Recover update' }));
    expect(await screen.findByText(/Recovery is waiting for the host fence to be released/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Recover update' })).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Close' }));
    expect(screen.getByRole('button', { name: 'Update now' })).toHaveAttribute('aria-disabled', 'true');
  });

  it('keeps recovery available after NeedsOperator when status cannot be rechecked', async () => {
    const execute = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'RecoveryRequired',
      activities: [],
    });
    const recover = vi.fn().mockResolvedValue({
      outcome: 'NeedsOperator',
      detail: 'manual_intervention_required',
    });
    const status = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'RecoveryRequired',
      activities: [],
    });
    const user = userEvent.setup();

    window.localStorage.setItem('printfarmer.manual-host-update.release-id', 'stable:1.2.4');
    const mounted = render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onExecuteHostUpdate={execute}
      onGetHostUpdateStatus={status}
      onRecoverHostUpdate={recover}
    />);

    expect(await screen.findByRole('button', { name: 'Recover update' })).toBeVisible();
    mounted.rerender(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onExecuteHostUpdate={execute}
      onRecoverHostUpdate={recover}
    />);
    await user.click(screen.getByRole('button', { name: 'Recover update' }));
    expect(await screen.findByText(/NeedsOperator: manual_intervention_required/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Recover update' })).toBeVisible();
    expect(window.localStorage.getItem('printfarmer.manual-host-update.release-id')).toBe('stable:1.2.4');
  });

  it('blocks retry after authorization reports an unsupported host', async () => {
    const authorize = vi.fn()
      .mockRejectedValueOnce({ statusCode: 503, message: 'Authorization unavailable.' })
      .mockResolvedValueOnce({
        authorizationId: 'auth-2',
        releaseId: 'stable:1.2.4',
        sequence: 4,
        channel: 'stable',
        candidateFingerprint: 'candidate',
        policyRevision: 1,
        policyFingerprint: 'policy',
        expiresAt: '2026-09-19T20:00:00Z',
      });
    const execute = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'Applying',
      activities: [],
    });
    const user = userEvent.setup();

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
    />);

    await user.click(screen.getByRole('button', { name: 'Update now' }));
    const action = screen.getByRole('button', { name: 'Authorize and update' });
    await user.click(action);
    expect(await screen.findByText('The host update subsystem is unavailable on this host. No update was started.')).toBeVisible();
    expect(action).toHaveAttribute('aria-disabled', 'true');
    expect(execute).not.toHaveBeenCalled();
    expect(authorize).toHaveBeenCalledTimes(1);
  });

  it('does not reconcile a 503 execute failure or show stale progress', async () => {
    const authorize = vi.fn().mockResolvedValue({
      authorizationId: 'auth-1',
      releaseId: 'stable:1.2.4',
      sequence: 4,
      channel: 'stable',
      candidateFingerprint: 'candidate',
      policyRevision: 1,
      policyFingerprint: 'policy',
      expiresAt: '2026-09-19T20:00:00Z',
    });
    const execute = vi.fn().mockRejectedValue({
      statusCode: 503,
      message: 'The host update subsystem is unavailable on this host.',
    });
    const status = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'Completed',
      activities: [],
    });

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
      onGetHostUpdateStatus={status}
    />);

    await userEvent.setup().click(screen.getByRole('button', { name: 'Update now' }));
    await userEvent.setup().click(screen.getByRole('button', { name: 'Authorize and update' }));

    expect(await screen.findByText('The host update subsystem is unavailable on this host. No update was started.')).toBeVisible();
    expect(status).not.toHaveBeenCalled();
    expect(screen.queryByRole('list', { name: 'Host update progress' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Authorize and update' })).toHaveAttribute('aria-disabled', 'true');
  });

  it('executes when release-id persistence is unavailable', async () => {
    const setItem = vi.spyOn(window.localStorage, 'setItem').mockImplementation(() => {
      throw new Error('storage blocked');
    });
    const authorize = vi.fn().mockResolvedValue({
      authorizationId: 'auth-1',
      releaseId: 'stable:1.2.4',
      sequence: 4,
      channel: 'stable',
      candidateFingerprint: 'candidate',
      policyRevision: 1,
      policyFingerprint: 'policy',
      expiresAt: '2026-09-19T20:00:00Z',
    });
    const execute = vi.fn().mockResolvedValue({
      releaseId: 'stable:1.2.4',
      currentState: 'Applying',
      activities: [],
    });
    const user = userEvent.setup();

    render(<TestInstallerUpdatesExperience
      inventory={inventory({ eligibility: 'Eligible', readiness: { state: 'Eligible', reasons: [], hops: [] } })}
      observation="connected"
      onAuthorizeHostUpdate={authorize}
      onExecuteHostUpdate={execute}
    />);

    await user.click(screen.getByRole('button', { name: 'Update now' }));
    await user.click(screen.getByRole('button', { name: 'Authorize and update' }));
    await waitFor(() => expect(execute).toHaveBeenCalledWith('auth-1'));
    setItem.mockRestore();
  });

  it.each(['Eligible', 'Blocked', 'Unknown', 'NotManaged'] as const)(
    'renders %s readiness safely under #2661 semantics',
    (state) => {
      render(<TestInstallerUpdatesExperience inventory={inventory({
        eligibility: state,
        eligibilityReasons: null as unknown as string[],
        readiness: { state, reasons: null as unknown as string[], hops: null as unknown as string[] },
      })} observation="connected" />);
      expect(screen.getByText(new RegExp(`Readiness: ${state}[.] Eligibility: ${state}[.]`))).toBeVisible();
      expect(screen.getByText('Readiness reasons: Unknown. Readiness hops: Unknown.')).toBeVisible();
    },
  );

  it('labels service identities as observed/installed and keeps the target identity unknown', async () => {
    render(<TestInstallerUpdatesExperience inventory={inventory({ selectedChannel: 'insider', observedChannel: 'stable', targetChannel: 'stable', eligibility: 'Blocked', compatibilityState: 'MixedChannel', services: [replica({ identity })] })} observation="connected" />);
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
    render(<TestInstallerUpdatesExperience inventory={inventory()} observation="unknown" />);
    expect(screen.getByRole('status', { name: /Connection observation unknown/ })).toHaveTextContent(/browser is disconnected/);
  });

  it('flags like-for-like replicas with the same identity but different deployment digests', async () => {
    const manifestDigest = `sha256:${'c'.repeat(64)}`;
    const platformDigest = `sha256:${'d'.repeat(64)}`;
    render(<TestInstallerUpdatesExperience inventory={inventory({ services: [
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
    render(<TestInstallerUpdatesExperience inventory={inventory({ services: [
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
    render(<TestInstallerUpdatesExperience inventory={inventory({ services: [] })} observation="connected" />);
    expect(screen.queryByText('Replica observations')).not.toBeInTheDocument();
    expect(screen.queryByRole('list')).not.toBeInTheDocument();
  });

  it('uses named status and explained-disabled controls for keyboard and screen-reader users', async () => {
    const user = userEvent.setup();
    render(<TestInstallerUpdatesExperience inventory={inventory()} observation="connected" />);
    await user.tab();
    expect(screen.getByRole('button', { name: 'Update now' })).toHaveFocus();
    expect(screen.queryByRole('status', { name: '' })).not.toBeInTheDocument();
    const autoUpdate = screen.getByRole('checkbox', { name: /Enable auto-update for the selected train/i });
    expect(autoUpdate).toHaveAttribute('aria-describedby', 'auto-update-reason');
    expect(autoUpdate).toHaveAttribute('aria-disabled', 'true');
    expect(autoUpdate).not.toHaveAttribute('disabled');
  });

  it('presents blocked readiness even when eligibility is NotManaged and compatibility is Compatible', () => {
    render(<TestInstallerUpdatesExperience inventory={blockedReadinessInventory()} observation="connected" />);
    expect(screen.getByText(/The observed installation is blocked/)).toBeVisible();
    expect(screen.getByText(/Host maintenance is required/)).toBeVisible();
  });

  it('distinguishes unsigned legacy installations from executor facility blockers and gives the manual path', () => {
    render(<TestInstallerUpdatesExperience inventory={inventory({
      eligibility: 'NotManaged',
      eligibilityReasons: ['SignedReleaseEvidenceUnavailableManualOnly', 'ReadOnlyInventory'],
      compatibilityState: 'Compatible',
    })} observation="connected" />);

    expect(screen.getByText(/cannot establish managed eligibility/)).toBeVisible();
    expect(screen.getByText('Manual signed install required')).toBeVisible();
    expect(screen.getByText(/manually install a current signed release/)).toBeVisible();
    expect(screen.getByRole('alert')).toBeVisible();
  });

  it('does not show the legacy manual path for facility-only blockers', () => {
    const { rerender } = render(<TestInstallerUpdatesExperience inventory={inventory({
      eligibility: 'NotManaged',
      eligibilityReasons: ['ManagedEligibilityNotEstablished', 'ReadOnlyInventory'],
      compatibilityState: 'Compatible',
      readiness: {
        state: 'Blocked',
        reasons: [
          'facility_unavailable:target_image_migration_runner_unavailable',
          'facility_unavailable:queue_reconciliation_writer_fence_unavailable',
          'facility_unavailable:sql_server_visible_backup_path_mapping_unverified',
        ],
        hops: [],
      },
    })} observation="connected" />);

    expect(screen.getByText(/facility_unavailable:target_image_migration_runner_unavailable/)).toBeVisible();
    expect(screen.queryByText('Manual signed install required')).not.toBeInTheDocument();
    expect(screen.queryByText(/cannot establish managed eligibility/)).not.toBeInTheDocument();

    rerender(<TestInstallerUpdatesExperience inventory={inventory({
      eligibility: 'NotManaged',
      eligibilityReasons: ['SignedReleaseEvidenceUnavailableManualOnly', 'ReadOnlyInventory'],
      compatibilityState: 'Compatible',
    })} observation="connected" />);
    expect(screen.getByText('Manual signed install required')).toBeVisible();
  });

  it('keeps mixed legacy and facility evidence visible as distinct categories', () => {
    render(<TestInstallerUpdatesExperience inventory={inventory({
      eligibility: 'NotManaged',
      eligibilityReasons: [
        'SignedReleaseEvidenceUnavailableManualOnly',
        'ReadOnlyInventory',
      ],
      compatibilityState: 'Compatible',
      readiness: {
        state: 'Blocked',
        reasons: ['facility_unavailable:target_image_migration_runner_unavailable'],
        hops: [],
      },
    })} observation="connected" />);

    const availability = screen.getByText('Read-only release availability').closest('[role="alert"]');
    expect(availability).not.toBeNull();
    expect(availability).toHaveTextContent(/This installation cannot present verified signed release evidence/);
    expect(availability).toHaveTextContent(/install a current signed release manually once/);
    expect(availability).toHaveTextContent(/The observed installation is blocked/);
    expect(screen.getByText(/facility_unavailable:target_image_migration_runner_unavailable/)).toBeVisible();
  });

  it('renders every observed replica and marks conflicting identities without proposing a target', async () => {
    render(<TestInstallerUpdatesExperience inventory={conflictingReplicaInventory()} observation="connected" />);
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
    render(<TestInstallerUpdatesExperience inventory={inventory({ services: [
      replica({ serviceId: 'api', component: 'api', identity, applicationVersion: '1.2.3', sourceCommit: identity.sourceCommit }),
      replica({ serviceId: 'worker', component: 'worker', identity, applicationVersion: '1.2.4', sourceCommit: identity.sourceCommit }),
    ] })} observation="connected" />);

    expect(screen.getByText(/different canonical release, application, or source evidence/)).toBeVisible();
  });

  it('detects a partial known platform digest divergence', () => {
    render(<TestInstallerUpdatesExperience inventory={inventory({ services: [
      replica({ identity, platform: 'linux/amd64', manifestDigest: digest, platformDigest: null, indexDigest: null }),
      replica({ instanceId: 'replica-b', identity, platform: 'linux/amd64', manifestDigest: `sha256:${'f'.repeat(64)}`, platformDigest: null, indexDigest: null }),
    ] })} observation="connected" />);

    expect(screen.getByText(/Like-for-like observed replicas report different platform digest evidence/)).toBeVisible();
    expect(screen.queryByText('Missing platform digest evidence')).not.toBeInTheDocument();
  });

  it.each(['MixedRelease', 'Incompatible'] as const)(
    'renders authoritative observed compatibility conflict details for %s',
    (compatibilityState) => {
      render(<TestInstallerUpdatesExperience inventory={inventory({
        compatibilityState,
        compatibilityReasons: ['CanonicalReleaseDivergence'],
      })} observation="connected" />);

      expect(screen.getByText('Observed compatibility state').parentElement).toHaveTextContent(compatibilityState);
      expect(screen.getByText('Observed compatibility reasons').parentElement).toHaveTextContent('CanonicalReleaseDivergence');
      expect(screen.getByText('Observed compatibility conflict')).toBeVisible();
      expect(screen.getByText(new RegExp(`Observed compatibility is ${compatibilityState}`))).toBeVisible();
    },
  );

  it('keeps draft channel changes out of the observed identity card and top-level warning', async () => {
    const user = userEvent.setup();
    render(<TestInstallerUpdatesExperience inventory={inventory({ selectedChannel: 'stable', observedChannel: null, readiness: { state: 'Blocked', reasons: ['Host maintenance is required'], hops: ['host-check'] }, eligibility: 'NotManaged' })} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} onSaveUpdateChannel={vi.fn()} />);

    expect(screen.getByText(/Selected train/).parentElement).toHaveTextContent('stable');
    expect(screen.getByText(/Observed train/).parentElement).toHaveTextContent('Unknown');
    expect(screen.getByText(/Readiness: Blocked/)).toBeVisible();
    expect(screen.queryByText('Insider channel')).not.toBeInTheDocument();

    await user.selectOptions(screen.getByRole('combobox', { name: 'Release channel' }), 'insider');
    expect(screen.getByText(/Selected train/).parentElement).toHaveTextContent('stable');
    expect(screen.getByRole('heading', { name: 'Release trains and observed identity' }).closest('[data-pf-card], section, div')).not.toHaveTextContent('Pending Insider channel selection');
    expect(screen.getByText('Pending Insider channel selection')).toBeVisible();
  });

  it('defaults to stable and saves the complete UpdateChannel group', async () => {
    const user = userEvent.setup();
    const save = vi.fn().mockResolvedValue({ channel: 'stable', insiderAcknowledged: false });
    render(<TestInstallerUpdatesExperience inventory={inventory()} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} onSaveUpdateChannel={save} />);

    expect(screen.getByRole('combobox', { name: 'Release channel' })).toHaveValue('stable');
    await user.selectOptions(screen.getByRole('combobox', { name: 'Release channel' }), 'stable');
    await user.click(screen.getByRole('button', { name: 'Save update channel' }));
    await waitFor(() => expect(save).toHaveBeenCalledWith({ channel: 'stable', insiderAcknowledged: false }));
    expect(screen.getByRole('status', { name: 'Update channel save status' })).toHaveTextContent('Update channel saved.');
  });

  it('serializes synchronous duplicate channel saves', async () => {
    const resolveSave: Array<(settings: { channel: 'stable'; insiderAcknowledged: false }) => void> = [];
    const save = vi.fn(() => new Promise<{ channel: 'stable'; insiderAcknowledged: false }>((resolve) => {
      resolveSave.push(resolve);
    }));
    render(<TestInstallerUpdatesExperience inventory={inventory()} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} onSaveUpdateChannel={save} />);

    const saveButton = screen.getByRole('button', { name: 'Save update channel' });
    act(() => {
      fireEvent.click(saveButton);
      fireEvent.click(saveButton);
    });

    expect(save).toHaveBeenCalledOnce();
    resolveSave[0]({ channel: 'stable', insiderAcknowledged: false });
    await waitFor(() => expect(screen.getByRole('status', { name: 'Update channel save status' })).toHaveTextContent('Update channel saved.'));

    fireEvent.click(saveButton);
    expect(save).toHaveBeenCalledTimes(2);
    resolveSave[1]({ channel: 'stable', insiderAcknowledged: false });
    await waitFor(() => expect(screen.getByRole('status', { name: 'Update channel save status' })).toHaveTextContent('Update channel saved.'));
  });

  it('releases the channel save mutex after a rejected save', async () => {
    const rejectSave: Array<(error: unknown) => void> = [];
    const save = vi.fn(() => new Promise<{ channel: 'stable'; insiderAcknowledged: false }>((_resolve, reject) => {
      rejectSave.push(reject);
    }));
    render(<TestInstallerUpdatesExperience inventory={inventory()} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} onSaveUpdateChannel={save} />);

    const saveButton = screen.getByRole('button', { name: 'Save update channel' });
    fireEvent.click(saveButton);
    expect(save).toHaveBeenCalledOnce();
    rejectSave[0](new UpdateChannelSaveRejectedError({ channel: 'stable', insiderAcknowledged: false }));
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(/was not saved/));

    fireEvent.click(saveButton);
    expect(save).toHaveBeenCalledTimes(2);
  });

  it('disables UpdateChannel retry while a save is in flight', async () => {
    let resolveSave: ((settings: { channel: 'stable'; insiderAcknowledged: false }) => void) | undefined;
    const save = vi.fn(() => new Promise<{ channel: 'stable'; insiderAcknowledged: false }>((resolve) => {
      resolveSave = resolve;
    }));
    const retry = vi.fn().mockResolvedValue({ channel: 'stable', insiderAcknowledged: false });
    const { rerender } = render(<TestInstallerUpdatesExperience
      inventory={inventory()}
      observation="connected"
      updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }}
      onSaveUpdateChannel={save}
      onRetryUpdateChannel={retry}
    />);

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Save update channel' }));
      rerender(<TestInstallerUpdatesExperience
        inventory={inventory()}
        observation="connected"
        updateChannelIsError
        updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }}
        onSaveUpdateChannel={save}
        onRetryUpdateChannel={retry}
      />);
    });

    expect(screen.getByRole('button', { name: 'Retry UpdateChannel settings' })).toBeDisabled();
    await act(async () => {
      resolveSave?.({ channel: 'stable', insiderAcknowledged: false });
    });
  });

  it('serializes synchronous duplicate UpdateChannel retries', async () => {
    const resolveRetry: Array<(settings: { channel: 'stable'; insiderAcknowledged: false }) => void> = [];
    const retry = vi.fn(() => new Promise<{ channel: 'stable'; insiderAcknowledged: false }>((resolve) => {
      resolveRetry.push(resolve);
    }));
    render(<TestInstallerUpdatesExperience
      inventory={inventory()}
      observation="connected"
      updateChannelIsError
      onRetryUpdateChannel={retry}
      updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }}
    />);

    const retryButton = screen.getByRole('button', { name: 'Retry UpdateChannel settings' });
    act(() => {
      fireEvent.click(retryButton);
      fireEvent.click(retryButton);
    });

    expect(retry).toHaveBeenCalledOnce();
    resolveRetry[0]({ channel: 'stable', insiderAcknowledged: false });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Retry UpdateChannel settings' })).not.toBeDisabled());

    fireEvent.click(retryButton);
    expect(retry).toHaveBeenCalledTimes(2);
    resolveRetry[1]({ channel: 'stable', insiderAcknowledged: false });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Retry UpdateChannel settings' })).not.toBeDisabled());
  });

  it('releases the UpdateChannel retry mutex after a rejected retry', async () => {
    const rejectRetry: Array<(error: unknown) => void> = [];
    const retry = vi.fn(() => new Promise<{ channel: 'stable'; insiderAcknowledged: false }>((_resolve, reject) => {
      rejectRetry.push(reject);
    }));
    render(<TestInstallerUpdatesExperience
      inventory={inventory()}
      observation="connected"
      updateChannelIsError
      onRetryUpdateChannel={retry}
      updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }}
    />);

    const retryButton = screen.getByRole('button', { name: 'Retry UpdateChannel settings' });
    fireEvent.click(retryButton);
    expect(retry).toHaveBeenCalledOnce();
    rejectRetry[0](new Error('retry failed'));
    await waitFor(() => expect(retryButton).not.toBeDisabled());

    fireEvent.click(retryButton);
    expect(retry).toHaveBeenCalledTimes(2);
  });

  it('requires explicit acknowledgement before saving Insider', async () => {
    const user = userEvent.setup();
    const save = vi.fn().mockResolvedValue({ channel: 'insider', insiderAcknowledged: true });
    render(<TestInstallerUpdatesExperience inventory={inventory()} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} onSaveUpdateChannel={save} />);

    await user.selectOptions(screen.getByRole('combobox', { name: 'Release channel' }), 'insider');
    await user.click(screen.getByRole('button', { name: 'Save update channel' }));
    expect(screen.getByRole('dialog', { name: 'Acknowledge Insider channel risk' })).toBeVisible();
    expect(save).not.toHaveBeenCalled();
    await user.click(screen.getByRole('checkbox', { name: /accept the prerelease risk/i }));
    await user.click(screen.getByRole('button', { name: 'Acknowledge and save' }));
    await waitFor(() => expect(save).toHaveBeenCalledWith({ channel: 'insider', insiderAcknowledged: true }));
  });

  it('keeps the acknowledgement dialog open while saving and closes it only after confirmed success', async () => {
    const user = userEvent.setup();
    let confirmSave: ((settings: { channel: 'insider'; insiderAcknowledged: true }) => void) | undefined;
    const save = vi.fn(() => new Promise<{ channel: 'insider'; insiderAcknowledged: true }>((resolve) => { confirmSave = resolve; }));
    render(<TestInstallerUpdatesExperience inventory={inventory()} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} onSaveUpdateChannel={save} />);

    await user.selectOptions(screen.getByRole('combobox', { name: 'Release channel' }), 'insider');
    await user.click(screen.getByRole('button', { name: 'Save update channel' }));
    await user.click(screen.getByRole('checkbox', { name: /accept the prerelease risk/i }));
    await user.click(screen.getByRole('button', { name: 'Acknowledge and save' }));

    expect(screen.getByRole('dialog', { name: 'Acknowledge Insider channel risk' })).toBeVisible();
    expect(screen.getByRole('button', { name: /Please wait/i })).toBeVisible();

    confirmSave?.({ channel: 'insider', insiderAcknowledged: true });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Acknowledge Insider channel risk' })).not.toBeInTheDocument());
    expect(screen.getByRole('status', { name: 'Update channel save status' })).toHaveTextContent('Update channel saved.');
  });

  it('keeps the acknowledgement dialog open and reports unknown outcome when save confirmation fails', async () => {
    const user = userEvent.setup();
    const save = vi.fn().mockRejectedValue(new Error('refetch failed'));
    render(<TestInstallerUpdatesExperience inventory={inventory()} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} onSaveUpdateChannel={save} />);

    await user.selectOptions(screen.getByRole('combobox', { name: 'Release channel' }), 'insider');
    await user.click(screen.getByRole('button', { name: 'Save update channel' }));
    await user.click(screen.getByRole('checkbox', { name: /accept the prerelease risk/i }));
    await user.click(screen.getByRole('button', { name: 'Acknowledge and save' }));

    expect(await screen.findByRole('dialog', { name: 'Acknowledge Insider channel risk' })).toBeVisible();
    expect(screen.getByRole('alert')).toHaveTextContent(/outcome is unknown/);
    expect(screen.getByRole('checkbox', { name: /accept the prerelease risk/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Acknowledge and save' })).toBeDisabled();
    expect(document.querySelector('[aria-label="Update channel save status"]')).toHaveTextContent('');
  });

  it('keeps mutation controls disabled after an unknown save outcome until a fresh authoritative refetch resolves it (GET-only retry)', async () => {
    const user = userEvent.setup();
    const save = vi.fn().mockRejectedValue(new Error('refetch failed'));
    const initialSettings = { channel: 'stable' as const, insiderAcknowledged: false };
    const { rerender } = render(
      <TestInstallerUpdatesExperience inventory={inventory()} observation="connected" updateChannelSettings={initialSettings} onSaveUpdateChannel={save} />,
    );

    expect(screen.getByRole('combobox', { name: 'Release channel' })).not.toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Save update channel' }));

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(/outcome is unknown/));
    // The outcome is unknown: mutation controls stay disabled and there is
    // no race that re-enables them while reconciliation is unresolved.
    expect(screen.getByRole('combobox', { name: 'Release channel' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Save update channel' })).toBeDisabled();

    // A fresh authoritative GET (e.g. the page's GET-only retry succeeding)
    // delivers a new settings object; only then do mutation controls
    // re-enable and the stale error clears.
    rerender(
      <TestInstallerUpdatesExperience inventory={inventory()} observation="connected" updateChannelSettings={{ ...initialSettings }} onSaveUpdateChannel={save} />,
    );

    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Release channel' })).not.toBeDisabled());
    expect(screen.getByRole('button', { name: 'Save update channel' })).not.toBeDisabled();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('reconciles the selector to the authoritative channel and reports a truthful rejection, without claiming success, when the refetch disagrees with the request', async () => {
    const user = userEvent.setup();
    const save = vi.fn().mockRejectedValue(new UpdateChannelSaveRejectedError({ channel: 'stable', insiderAcknowledged: false }));
    render(<TestInstallerUpdatesExperience inventory={inventory()} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} onSaveUpdateChannel={save} />);

    await user.selectOptions(screen.getByRole('combobox', { name: 'Release channel' }), 'insider');
    await user.click(screen.getByRole('button', { name: 'Save update channel' }));
    await user.click(screen.getByRole('checkbox', { name: /accept the prerelease risk/i }));
    await user.click(screen.getByRole('button', { name: 'Acknowledge and save' }));

    // The outcome is conclusively known (not pending): the acknowledgement
    // dialog closes rather than staying open over a reverted control.
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Acknowledge Insider channel risk' })).not.toBeInTheDocument());
    expect(await screen.findByRole('alert')).toHaveTextContent(/was not saved/);
    expect(screen.getByRole('alert')).toHaveTextContent(/"stable"/);
    expect(document.querySelector('[aria-label="Update channel save status"]')).toHaveTextContent('');
    // The authoritative (unchanged) state is known, not unknown: mutation
    // controls re-enable so the admin can see the reverted selection and retry.
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Release channel' })).toHaveValue('stable'));
    expect(screen.getByRole('combobox', { name: 'Release channel' })).not.toBeDisabled();
    expect(screen.getByRole('button', { name: 'Save update channel' })).not.toBeDisabled();
  });

  it('resets modal-local acknowledgement on cancel and requires a fresh acknowledgement', async () => {
    const user = userEvent.setup();
    const save = vi.fn().mockResolvedValue({ channel: 'insider', insiderAcknowledged: true });
    render(<TestInstallerUpdatesExperience inventory={inventory()} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} onSaveUpdateChannel={save} />);

    await user.selectOptions(screen.getByRole('combobox', { name: 'Release channel' }), 'insider');
    await user.click(screen.getByRole('button', { name: 'Save update channel' }));
    await user.click(screen.getByRole('checkbox', { name: /accept the prerelease risk/i }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    await user.click(screen.getByRole('button', { name: 'Save update channel' }));

    expect(save).not.toHaveBeenCalled();
    expect(screen.getByRole('dialog', { name: 'Acknowledge Insider channel risk' })).toBeVisible();
    expect(screen.getByRole('checkbox', { name: /accept the prerelease risk/i })).not.toBeChecked();
  });

  it('resets modal-local acknowledgement on close and Escape', async () => {
    const user = userEvent.setup();
    render(<TestInstallerUpdatesExperience inventory={inventory()} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} onSaveUpdateChannel={vi.fn()} />);

    await user.selectOptions(screen.getByRole('combobox', { name: 'Release channel' }), 'insider');
    await user.click(screen.getByRole('button', { name: 'Save update channel' }));
    await user.click(screen.getByRole('checkbox', { name: /accept the prerelease risk/i }));
    await user.click(screen.getByRole('button', { name: 'Close modal' }));
    await user.click(screen.getByRole('button', { name: 'Save update channel' }));
    expect(screen.getByRole('checkbox', { name: /accept the prerelease risk/i })).not.toBeChecked();

    await user.click(screen.getByRole('checkbox', { name: /accept the prerelease risk/i }));
    await user.keyboard('{Escape}');
    await user.click(screen.getByRole('button', { name: 'Save update channel' }));
    expect(screen.getByRole('checkbox', { name: /accept the prerelease risk/i })).not.toBeChecked();
  });

  it('disables channel controls until authoritative settings load and wires errors to the select', () => {
    render(<TestInstallerUpdatesExperience inventory={inventory()} observation="connected" updateChannelIsLoading updateChannelIsError />);

    const select = screen.getByRole('combobox', { name: 'Release channel' });
    expect(select).toHaveValue('stable');
    expect(select).toBeDisabled();
    expect(select).toHaveAttribute('aria-invalid', 'true');
    expect(select).toHaveAttribute('aria-describedby', 'update-channel-help update-channel-error');
    expect(screen.getByRole('button', { name: 'Save update channel' })).toBeDisabled();
    expect(screen.getByRole('alert')).toHaveTextContent(/authoritative UpdateChannel settings/);
  });

  it('keeps manual and automatic controls unavailable after Insider selection and save', async () => {
    const user = userEvent.setup();
    const save = vi.fn().mockResolvedValue({ channel: 'insider', insiderAcknowledged: true });
    render(<TestInstallerUpdatesExperience inventory={inventory()} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} onSaveUpdateChannel={save} />);

    await user.selectOptions(screen.getByRole('combobox', { name: 'Release channel' }), 'insider');
    await user.click(screen.getByRole('button', { name: 'Save update channel' }));
    await user.click(screen.getByRole('checkbox', { name: /accept the prerelease risk/i }));
    await user.click(screen.getByRole('button', { name: 'Acknowledge and save' }));
    await screen.findByText('Update channel saved.');

    expect(screen.getByRole('button', { name: 'Update now' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Update now' })).toHaveAttribute('title', expect.stringContaining('runtime execution contract'));
    expect(screen.getByRole('button', { name: 'Later' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('checkbox', { name: /Enable Auto-update for the selected train/i })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Save automatic update policy' })).toHaveAttribute('aria-disabled', 'true');
  });

  it('renders the canonical scheduler status example without implying installability', () => {
    render(<TestInstallerUpdatesExperience inventory={inventory({ updateScheduling: {
      configuredEnabled: true,
      effectiveEnabled: false,
      selectedChannel: 'insider',
      effectiveChannel: null,
      policyRevision: 7,
      lastAttemptAt: null,
      nextAttemptAt: '2026-09-17T16:00:00Z',
      backoff: { state: 'Waiting', consecutiveFailures: 0, until: null, reasons: ['InsiderAcknowledgementRequired'] },
      killSwitch: { enabled: false, reason: null },
      executor: { state: 'Unavailable', reason: 'ExecutorNotWired' },
      reasons: ['InsiderAcknowledgementRequired', 'ExecutorNotWired'],
    } })} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} />);

    expect(screen.getByText('Scheduler status')).toBeVisible();
    expect(screen.getByText('Configured scheduling').parentElement).toHaveTextContent('Enabled');
    expect(screen.getByText('Effective scheduling').parentElement).toHaveTextContent('Disabled');
    expect(screen.getByText('Selected channel').parentElement).toHaveTextContent('insider');
    expect(screen.getByText('Effective channel').parentElement).toHaveTextContent('Unknown');
    expect(screen.getByText('Backoff state').parentElement).toHaveTextContent('Waiting');
    expect(screen.getByText('Executor state').parentElement).toHaveTextContent('Unavailable');
    expect(screen.getByText(/InsiderAcknowledgementRequired, ExecutorNotWired/)).toBeVisible();
    expect(screen.getByText(/does not infer installability/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Update now' })).toHaveAttribute('aria-disabled', 'true');
  });

  it('renders nullable and executor-unavailable scheduling as unavailable without alarm', () => {
    const { rerender } = render(<TestInstallerUpdatesExperience inventory={inventory({ updateScheduling: null })} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} />);

    expect(screen.getByText('Scheduler unavailable')).toBeVisible();
    expect(screen.getByText(/not wired for this host/)).toBeVisible();

    rerender(<TestInstallerUpdatesExperience inventory={inventory({ updateScheduling: {
      configuredEnabled: false,
      effectiveEnabled: false,
      selectedChannel: 'stable',
      effectiveChannel: 'stable',
      policyRevision: 1,
      lastAttemptAt: '2026-09-17T15:00:00Z',
      nextAttemptAt: null,
      backoff: { state: 'Waiting', consecutiveFailures: 2, until: '2026-09-17T16:00:00Z', reasons: [] },
      killSwitch: { enabled: true, reason: 'maintenance' },
      executor: { state: 'Unavailable', reason: null },
      reasons: [],
    } })} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} />);

    expect(screen.getByText('Executor unavailable')).toBeVisible();
    expect(screen.getByText('Kill switch').parentElement).toHaveTextContent('Enabled');
    expect(screen.getByText('Scheduler reasons: None reported.')).toBeVisible();
    expect(screen.queryByText(/Ready to install/i)).not.toBeInTheDocument();
  });

  it.each([
    'Unknown',
    'Unavailable',
    'Available',
    'Busy',
    'RecoveryRequired',
  ] as const)('keeps Update now/Later/automatic controls unavailable when executor state is %s, including Available and RecoveryRequired', (executorState: UpdateSchedulingExecutorState) => {
    render(<TestInstallerUpdatesExperience inventory={inventory({ updateScheduling: {
      configuredEnabled: true,
      effectiveEnabled: true,
      selectedChannel: 'stable',
      effectiveChannel: 'stable',
      policyRevision: 5,
      lastAttemptAt: null,
      nextAttemptAt: null,
      backoff: { state: 'None', consecutiveFailures: 0, until: null, reasons: [] },
      killSwitch: { enabled: false, reason: null },
      executor: { state: executorState, reason: executorState === 'Busy' ? 'ApplyInProgress' : null },
      reasons: [],
    } })} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} />);

    expect(screen.getByText('Executor state').parentElement).toHaveTextContent(executorState);
    expect(screen.getByRole('button', { name: 'Update now' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Later' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('checkbox', { name: /Enable Auto-update for the selected train/i })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Save automatic update policy' })).toHaveAttribute('aria-disabled', 'true');
  });

  it('keeps Update now/Later/automatic controls unavailable when updateScheduling is null regardless of executor state', () => {
    render(<TestInstallerUpdatesExperience inventory={inventory({ updateScheduling: null })} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} />);

    expect(screen.getByText('Scheduler unavailable')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Update now' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Later' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('checkbox', { name: /Enable Auto-update for the selected train/i })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Save automatic update policy' })).toHaveAttribute('aria-disabled', 'true');
  });

  it('renders Busy executor status as a read-only in-progress report, not permission for another action', () => {
    render(<TestInstallerUpdatesExperience inventory={inventory({ updateScheduling: {
      configuredEnabled: true,
      effectiveEnabled: true,
      selectedChannel: 'stable',
      effectiveChannel: 'stable',
      policyRevision: 5,
      lastAttemptAt: '2026-09-17T15:00:00Z',
      nextAttemptAt: null,
      backoff: { state: 'None', consecutiveFailures: 0, until: null, reasons: [] },
      killSwitch: { enabled: false, reason: null },
      executor: { state: 'Busy', reason: 'ApplyInProgress' },
      reasons: [],
    } })} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} />);

    expect(screen.getByText('Executor state').parentElement).toHaveTextContent('Busy');
    expect(screen.getByText('Executor reason').parentElement).toHaveTextContent('ApplyInProgress');
    expect(screen.queryByText('Executor unavailable')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Update now' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Later' })).toHaveAttribute('aria-disabled', 'true');
  });

  it('renders RecoveryRequired executor status without suggesting an unsafe recovery action', () => {
    render(<TestInstallerUpdatesExperience inventory={inventory({ updateScheduling: {
      configuredEnabled: true,
      effectiveEnabled: true,
      selectedChannel: 'stable',
      effectiveChannel: 'stable',
      policyRevision: 5,
      lastAttemptAt: '2026-09-17T15:00:00Z',
      nextAttemptAt: null,
      backoff: { state: 'None', consecutiveFailures: 0, until: null, reasons: [] },
      killSwitch: { enabled: false, reason: null },
      executor: { state: 'RecoveryRequired', reason: 'PriorApplyIncomplete' },
      reasons: [],
    } })} observation="connected" updateChannelSettings={{ channel: 'stable', insiderAcknowledged: false }} />);

    expect(screen.getByText('Executor state').parentElement).toHaveTextContent('RecoveryRequired');
    expect(screen.getByText('Executor reason').parentElement).toHaveTextContent('PriorApplyIncomplete');
    expect(screen.queryByRole('button', { name: /recover/i })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Update now' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Later' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('checkbox', { name: /Enable Auto-update for the selected train/i })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Save automatic update policy' })).toHaveAttribute('aria-disabled', 'true');
  });

});
