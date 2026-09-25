import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

const { getDispatchReconciliation, recoverDispatchClaim, clearDispatchRecoveryBlock, toast } =
  vi.hoisted(() => ({
    getDispatchReconciliation: vi.fn(),
    recoverDispatchClaim: vi.fn(),
    clearDispatchRecoveryBlock: vi.fn(),
    toast: { success: vi.fn(), info: vi.fn(), error: vi.fn() },
  }));

vi.mock('@/services/api/dispatchRecoveryApi', () => ({
  getDispatchReconciliation,
  recoverDispatchClaim,
  clearDispatchRecoveryBlock,
  getDispatchRecoveryAudit: vi.fn(),
}));
vi.mock('sonner', () => ({ toast }));

import { AuthContext } from '@/common/contexts/auth-context';
import type { AuthContextType } from '@/contexts/AuthContextValue';
import { DispatchReconciliationBanner } from '@/features/dispatch-recovery/components/DispatchReconciliationBanner';
import { RecoveryBlockedJobsPanel } from '@/features/dispatch-recovery/components/RecoveryBlockedJobsPanel';
import type {
  DispatchReconciliationResource,
  QueuedPrintJobWithFileMetaDto,
} from '@/types/api';

function wrapperFor(permissions: string[] | null) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const auth = permissions
    ? ({
        hasPermission: (resource: string, action: string) =>
          permissions.includes(`${resource}:${action}`),
      } as unknown as AuthContextType)
    : undefined;
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      {auth ? <AuthContext.Provider value={auth}>{children}</AuthContext.Provider> : children}
    </QueryClientProvider>
  );
}

function resource(overrides: Partial<DispatchReconciliationResource> = {}): DispatchReconciliationResource {
  return {
    printerId: 'printer-1',
    printerName: 'Prusa A',
    hasIndeterminateClaim: true,
    jobId: 'job-12345678-aaaa',
    dispatchAttemptId: 'attempt-1',
    claimRevision: 2,
    claimAgeSeconds: 90,
    escalationLevel: 'Operational',
    senderSettled: false,
    recoveryPermission: true,
    lastEvidence: { backendCallPhase: 'AwaitingResponse', errorCode: 'Timeout' },
    ...overrides,
  };
}

describe('DispatchReconciliationBanner', () => {
  beforeEach(() => {
    getDispatchReconciliation.mockReset();
    recoverDispatchClaim.mockReset();
  });

  it('renders nothing when the printer has no indeterminate claim', async () => {
    getDispatchReconciliation.mockResolvedValue({
      resource: resource({ hasIndeterminateClaim: false, dispatchAttemptId: null }),
      etag: null,
    });
    const { container } = render(
      <DispatchReconciliationBanner printerId="printer-1" printerName="Prusa A" />,
      { wrapper: wrapperFor(null) }
    );

    await waitFor(() => expect(getDispatchReconciliation).toHaveBeenCalledWith('printer-1'));
    expect(container).toBeEmptyDOMElement();
  });

  it('warns about the indeterminate claim with evidence and a recover action', async () => {
    getDispatchReconciliation.mockResolvedValue({ resource: resource(), etag: '"r"' });
    render(<DispatchReconciliationBanner printerId="printer-1" printerName="Prusa A" />, {
      wrapper: wrapperFor(null),
    });

    const region = await screen.findByRole('status', { name: /Dispatch outcome unknown on Prusa A/ });
    expect(within(region).getByText(/Check the printer physically/)).toBeInTheDocument();
    expect(within(region).getByText('No evidence the start sender has stopped')).toBeInTheDocument();
    expect(within(region).getByText('AwaitingResponse (Timeout)')).toBeInTheDocument();
    expect(within(region).getByRole('button', { name: 'Recover dispatch on Prusa A' })).toBeInTheDocument();
    expect(within(region).queryByRole('button', { name: /cancel|retry/i })).not.toBeInTheDocument();
  });

  it('hides the recover action without server-granted recovery permission', async () => {
    getDispatchReconciliation.mockResolvedValue({
      resource: resource({ recoveryPermission: false }),
      etag: '"r"',
    });
    render(<DispatchReconciliationBanner printerId="printer-1" printerName="Prusa A" />, {
      wrapper: wrapperFor(null),
    });

    await screen.findByRole('status', { name: /Dispatch outcome unknown/ });
    expect(screen.queryByRole('button', { name: /Recover/ })).not.toBeInTheDocument();
  });

  it('opens the recovery flow against the reviewed snapshot', async () => {
    const user = userEvent.setup();
    getDispatchReconciliation.mockResolvedValue({ resource: resource(), etag: '"r"' });
    render(<DispatchReconciliationBanner printerId="printer-1" printerName="Prusa A" />, {
      wrapper: wrapperFor(null),
    });

    await user.click(await screen.findByRole('button', { name: 'Recover dispatch on Prusa A' }));
    expect(await screen.findByRole('dialog')).toHaveTextContent(/Recover dispatch on Prusa A/);
  });
});

function blockedJob(overrides: Partial<QueuedPrintJobWithFileMetaDto['job']> = {}): QueuedPrintJobWithFileMetaDto {
  return {
    job: {
      id: 'job-1',
      name: 'Bracket',
      status: 'Queued',
      blockedReasonCode: 'OperatorRecoveryRequired',
      rowVersion: 'AAAAAAAAB9E=',
      assignedPrinterId: 'printer-1',
      ...overrides,
    },
    assignedPrinter: { id: 'printer-1', name: 'Prusa A' },
  } as unknown as QueuedPrintJobWithFileMetaDto;
}

describe('RecoveryBlockedJobsPanel', () => {
  beforeEach(() => {
    clearDispatchRecoveryBlock.mockReset();
    toast.success.mockReset();
    toast.info.mockReset();
  });

  it('renders nothing when no job is recovery-blocked', () => {
    const { container } = render(
      <RecoveryBlockedJobsPanel jobs={[blockedJob({ blockedReasonCode: null })]} />,
      { wrapper: wrapperFor(['queue:reconcile']) }
    );
    expect(container).toBeEmptyDOMElement();
  });

  it('fails closed: no allow action without an auth context or queue:reconcile', () => {
    const { unmount } = render(<RecoveryBlockedJobsPanel jobs={[blockedJob()]} />, {
      wrapper: wrapperFor(null),
    });
    expect(screen.getByText(/Held after operator recovery \(1\)/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Allow dispatch/ })).not.toBeInTheDocument();
    unmount();

    render(<RecoveryBlockedJobsPanel jobs={[blockedJob()]} />, {
      wrapper: wrapperFor(['queue:read']),
    });
    expect(screen.queryByRole('button', { name: /Allow dispatch/ })).not.toBeInTheDocument();
    expect(screen.getByText(/must allow dispatch/)).toBeInTheDocument();
  });

  it('disables the allow action when the job has no rowVersion', () => {
    render(<RecoveryBlockedJobsPanel jobs={[blockedJob({ rowVersion: undefined })]} />, {
      wrapper: wrapperFor(['queue:reconcile']),
    });
    expect(screen.getByRole('button', { name: 'Allow dispatch for Bracket on Prusa A' })).toBeDisabled();
  });

  it('clears the block with the job rowVersion after confirmation', async () => {
    const user = userEvent.setup();
    clearDispatchRecoveryBlock.mockResolvedValue({ kind: 'cleared', httpStatus: 200, jobId: 'job-1', etag: null });
    render(<RecoveryBlockedJobsPanel jobs={[blockedJob()]} />, {
      wrapper: wrapperFor(['queue:reconcile']),
    });

    await user.click(screen.getByRole('button', { name: 'Allow dispatch for Bracket on Prusa A' }));
    const dialog = await screen.findByRole('dialog');
    expect(clearDispatchRecoveryBlock).not.toHaveBeenCalled();
    await user.click(within(dialog).getByRole('button', { name: 'Allow dispatch' }));

    await waitFor(() =>
      expect(clearDispatchRecoveryBlock).toHaveBeenCalledWith({ jobId: 'job-1', jobETag: 'AAAAAAAAB9E=' })
    );
    expect(toast.success).toHaveBeenCalledWith('Dispatch allowed for Bracket');
  });

  it.each([
    ['job_revision_conflict', 412, /changed since you reviewed it/],
    ['job_not_recovery_blocked', 409, /no longer held for recovery/],
  ])('explains a %s rejection', async (errorCode, httpStatus, message) => {
    const user = userEvent.setup();
    clearDispatchRecoveryBlock.mockResolvedValue({
      kind: httpStatus === 412 ? 'stale' : 'conflict',
      httpStatus,
      errorCode,
    });
    render(<RecoveryBlockedJobsPanel jobs={[blockedJob()]} />, {
      wrapper: wrapperFor(['queue:reconcile']),
    });

    await user.click(screen.getByRole('button', { name: 'Allow dispatch for Bracket on Prusa A' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Allow dispatch' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(message);
    expect(toast.success).not.toHaveBeenCalled();
    if (errorCode === 'job_not_recovery_blocked') {
      expect(toast.info).toHaveBeenCalled();
    }
  });
});
