import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

const { getDispatchReconciliation, getQueueCandidatePage } = vi.hoisted(() => ({
  getDispatchReconciliation: vi.fn(),
  getQueueCandidatePage: vi.fn(),
}));

vi.mock('@/services/api/dispatchRecoveryApi', () => ({
  getDispatchReconciliation,
  getQueueCandidatePage,
  recoverDispatchClaim: vi.fn(),
  clearDispatchRecoveryBlock: vi.fn(),
  getDispatchRecoveryAudit: vi.fn(),
}));
vi.mock('@/features/dispatch-recovery/components/DispatchRecoveryModal', () => ({
  DispatchRecoveryModal: ({
    isOpen,
    onRecovered,
    onClose,
  }: {
    isOpen: boolean;
    onRecovered: (auditId: string | null, attemptId: string) => void;
    onClose: () => void;
  }) =>
    isOpen ? (
      <button
        type="button"
        onClick={() => {
          onRecovered('audit-1', 'attempt-1');
          onClose();
        }}
      >
        Stub record recovery
      </button>
    ) : null,
}));

import { DispatchRecoveryQueueSection } from '@/features/dispatch-recovery/components/DispatchRecoveryQueueSection';
import type { QueuedPrintJobWithFileMetaDto } from '@/types/api';

function unknownJob(id: string, printerId: string, unknown = true): QueuedPrintJobWithFileMetaDto {
  return {
    job: {
      id,
      name: `Job ${id}`,
      status: 'Starting',
      assignedPrinterId: printerId,
      dispatchResult: unknown
        ? { outcome: 'Unknown', requiresReconciliation: true }
        : { outcome: 'Accepted', requiresReconciliation: false },
    },
    assignedPrinter: { id: printerId, name: `Printer ${printerId}` },
  } as unknown as QueuedPrintJobWithFileMetaDto;
}

function openClaim(printerId: string, open = true) {
  return {
    etag: '"r"',
    resource: {
      printerId,
      printerName: `Printer ${printerId}`,
      hasIndeterminateClaim: open,
      dispatchAttemptId: open ? 'attempt-1' : null,
      claimRevision: open ? 1 : null,
      escalationLevel: 'Warning',
      senderSettled: true,
      recoveryPermission: false,
    },
  };
}

function page(jobs: QueuedPrintJobWithFileMetaDto[], hasMore: boolean | null = false) {
  return { jobs, hasMore };
}

function setup() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, refetchInterval: false } },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  return { queryClient, wrapper };
}

describe('DispatchRecoveryQueueSection', () => {
  beforeEach(() => {
    getDispatchReconciliation.mockReset();
    getQueueCandidatePage.mockReset();
  });

  it('discovers a claim hidden from the filtered dashboard rows', async () => {
    getQueueCandidatePage.mockResolvedValue(page([unknownJob('j1', 'p1')]));
    getDispatchReconciliation.mockResolvedValue(openClaim('p1'));
    const { wrapper } = setup();

    render(<DispatchRecoveryQueueSection jobs={[]} />, { wrapper });

    expect(
      await screen.findByRole('status', { name: /Dispatch outcome unknown on Printer p1/ })
    ).toBeInTheDocument();
    expect(getQueueCandidatePage).toHaveBeenCalledWith(1000, 0);
  });

  it('pages through the whole active queue to find candidates beyond the first page', async () => {
    const firstPage = Array.from({ length: 1000 }, (_, i) => unknownJob(`a${i}`, 'p-first', false));
    getQueueCandidatePage
      .mockResolvedValueOnce(page(firstPage, true))
      .mockResolvedValueOnce(page([unknownJob('late', 'p-late')], false));
    getDispatchReconciliation.mockResolvedValue(openClaim('p-late'));
    const { wrapper } = setup();

    render(<DispatchRecoveryQueueSection jobs={[]} />, { wrapper });

    expect(
      await screen.findByRole('status', { name: /Dispatch outcome unknown on Printer p-late/ })
    ).toBeInTheDocument();
    expect(getQueueCandidatePage).toHaveBeenCalledTimes(2);
    expect(getQueueCandidatePage.mock.calls[1]).toEqual([1000, 1000]);
  });

  it('keeps paging past a short authorization-filtered page while the server reports more (R3044-H01)', async () => {
    // A scoped caller: raw pages are full, but authorization leaves the first
    // page empty and the second page short. The candidate is on page three.
    getQueueCandidatePage
      .mockResolvedValueOnce(page([], true))
      .mockResolvedValueOnce(page([unknownJob('x', 'p-x', false)], true))
      .mockResolvedValueOnce(page([unknownJob('late', 'p-scoped')], false));
    getDispatchReconciliation.mockResolvedValue(openClaim('p-scoped'));
    const { wrapper } = setup();

    render(<DispatchRecoveryQueueSection jobs={[]} />, { wrapper });

    expect(
      await screen.findByRole('status', { name: /Dispatch outcome unknown on Printer p-scoped/ })
    ).toBeInTheDocument();
    expect(getQueueCandidatePage).toHaveBeenCalledTimes(3);
    expect(getQueueCandidatePage.mock.calls[2]).toEqual([1000, 2000]);
  });

  it('stops paging when the server reports the unscoped end', async () => {
    getQueueCandidatePage.mockResolvedValue(page([], false));
    const { wrapper } = setup();

    render(<DispatchRecoveryQueueSection jobs={[]} />, { wrapper });

    await waitFor(() => expect(getQueueCandidatePage).toHaveBeenCalledTimes(1));
  });

  it('keeps the warning until reconciliation confirms the claim closed, even if the queue row changes first', async () => {
    getQueueCandidatePage.mockResolvedValue(page([unknownJob('j1', 'p1')]));
    getDispatchReconciliation.mockResolvedValue(openClaim('p1'));
    const { wrapper, queryClient } = setup();

    render(<DispatchRecoveryQueueSection jobs={[]} />, { wrapper });
    await screen.findByRole('status', { name: /Dispatch outcome unknown on Printer p1/ });

    // Queue row no longer reports Unknown, but the claim is still open.
    getQueueCandidatePage.mockResolvedValue(page([unknownJob('j1', 'p1', false)]));
    await queryClient.invalidateQueries({ queryKey: ['queue-jobs'] });
    await waitFor(() => expect(getQueueCandidatePage).toHaveBeenCalledTimes(2));
    expect(
      screen.getByRole('status', { name: /Dispatch outcome unknown on Printer p1/ })
    ).toBeInTheDocument();

    // Authoritative read reports the claim closed: the warning goes away.
    getDispatchReconciliation.mockResolvedValue(openClaim('p1', false));
    await queryClient.invalidateQueries({ queryKey: ['dispatch-reconciliation'] });
    await waitFor(() =>
      expect(
        screen.queryByRole('status', { name: /Dispatch outcome unknown/ })
      ).not.toBeInTheDocument()
    );
  });

  it('keeps the recovery confirmation and audit link after the queue refetch closes the claim (R3044-V02)', async () => {
    const user = userEvent.setup();
    getQueueCandidatePage.mockResolvedValue(page([unknownJob('j1', 'p1')]));
    getDispatchReconciliation.mockResolvedValue({
      ...openClaim('p1'),
      resource: { ...openClaim('p1').resource, recoveryPermission: true },
    });
    const { wrapper, queryClient } = setup();

    render(<DispatchRecoveryQueueSection jobs={[]} />, { wrapper });
    await user.click(await screen.findByRole('button', { name: /Recover dispatch on Printer p1/ }));
    await user.click(screen.getByRole('button', { name: 'Stub record recovery' }));

    // Recovery settles: the row is no longer Unknown and the claim is closed.
    getQueueCandidatePage.mockResolvedValue(page([unknownJob('j1', 'p1', false)]));
    getDispatchReconciliation.mockResolvedValue({
      ...openClaim('p1', false),
      resource: { ...openClaim('p1', false).resource, recoveryPermission: true },
    });
    await queryClient.invalidateQueries({ queryKey: ['queue-jobs'] });
    await queryClient.invalidateQueries({ queryKey: ['dispatch-reconciliation'] });

    expect(await screen.findByText(/Recovery recorded for Printer p1/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'View audit' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Dismiss recovery notice/ }));
    await waitFor(() =>
      expect(screen.queryByText(/Recovery recorded for Printer p1/)).not.toBeInTheDocument()
    );
  });

  it('falls back to the dashboard rows when the discovery read fails', async () => {
    getQueueCandidatePage.mockRejectedValue(new Error('boom'));
    getDispatchReconciliation.mockResolvedValue(openClaim('p2'));
    const { wrapper } = setup();

    render(<DispatchRecoveryQueueSection jobs={[unknownJob('j2', 'p2')]} />, { wrapper });

    expect(
      await screen.findByRole('status', { name: /Dispatch outcome unknown on Printer p2/ })
    ).toBeInTheDocument();
  });
});
