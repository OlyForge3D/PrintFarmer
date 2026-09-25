import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

const { recoverDispatchClaim, toastSuccess } = vi.hoisted(() => ({
  recoverDispatchClaim: vi.fn(),
  toastSuccess: vi.fn(),
}));

vi.mock('@/services/api/dispatchRecoveryApi', () => ({
  recoverDispatchClaim,
  getDispatchReconciliation: vi.fn(),
  getDispatchRecoveryAudit: vi.fn(),
  clearDispatchRecoveryBlock: vi.fn(),
}));
vi.mock('sonner', () => ({ toast: { success: toastSuccess, info: vi.fn(), error: vi.fn() } }));

import { DispatchRecoveryModal } from '@/features/dispatch-recovery/components/DispatchRecoveryModal';
import type { DispatchReconciliationSnapshot } from '@/types/api';

function snapshot(
  overrides: Partial<DispatchReconciliationSnapshot['resource']> = {},
  etag: string | null = '"rev-1"'
): DispatchReconciliationSnapshot {
  return {
    etag,
    resource: {
      printerId: 'printer-1',
      printerName: 'Prusa A',
      hasIndeterminateClaim: true,
      jobId: 'job-1',
      dispatchAttemptId: 'attempt-1',
      claimRevision: 4,
      claimAgeSeconds: 125,
      escalationLevel: 'Warning',
      senderSettled: true,
      recoveryPermission: true,
      ...overrides,
    },
  };
}

function renderModal(props: {
  snap?: DispatchReconciliationSnapshot;
  onRefresh?: () => void;
  onRecovered?: (auditId: string | null, attemptId: string) => void;
  onClose?: () => void;
} = {}) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const onRefresh = props.onRefresh ?? vi.fn();
  const onRecovered = props.onRecovered ?? vi.fn();
  const onClose = props.onClose ?? vi.fn();
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  const ui = (snap: DispatchReconciliationSnapshot | undefined) => (
    <DispatchRecoveryModal
      isOpen
      onClose={onClose}
      printerId="printer-1"
      printerName="Prusa A"
      snapshot={snap}
      onRefresh={onRefresh}
      onRecovered={onRecovered}
    />
  );
  const view = render(ui(props.snap ?? snapshot()), { wrapper });
  return {
    ...view,
    onRefresh,
    onRecovered,
    onClose,
    rerenderWith: (snap: DispatchReconciliationSnapshot) => view.rerender(ui(snap)),
  };
}

const physicalLabel = /I physically checked Prusa A/;
const isolationLabel = /start sender is stopped or isolated/;
const submitName = 'Record recovery';

describe('DispatchRecoveryModal', () => {
  beforeEach(() => {
    recoverDispatchClaim.mockReset();
    toastSuccess.mockReset();
  });

  it('requires the physical check before submitting', async () => {
    const user = userEvent.setup();
    renderModal();

    const submit = screen.getByRole('button', { name: submitName });
    expect(submit).toBeDisabled();
    expect(screen.queryByLabelText(isolationLabel)).not.toBeInTheDocument();

    await user.click(screen.getByLabelText(physicalLabel));
    expect(submit).toBeEnabled();
  });

  it('also requires sender isolation when the sender has not settled', async () => {
    const user = userEvent.setup();
    renderModal({ snap: snapshot({ senderSettled: false }) });

    await user.click(screen.getByLabelText(physicalLabel));
    const submit = screen.getByRole('button', { name: submitName });
    expect(submit).toBeDisabled();

    await user.click(screen.getByLabelText(isolationLabel));
    expect(submit).toBeEnabled();
  });

  it('blocks submission without a reviewed ETag', async () => {
    const user = userEvent.setup();
    renderModal({ snap: snapshot({}, null) });

    expect(screen.getByText(/has no revision tag/)).toBeInTheDocument();
    await user.click(screen.getByLabelText(physicalLabel));
    expect(screen.getByRole('button', { name: submitName })).toBeDisabled();
  });

  it('ignores a second click before the pending state re-renders (R3044-V03)', async () => {
    const user = userEvent.setup();
    let resolveRecover: (value: unknown) => void = () => {};
    recoverDispatchClaim.mockReturnValue(
      new Promise((resolve) => {
        resolveRecover = resolve;
      })
    );
    renderModal();

    await user.click(screen.getByLabelText(physicalLabel));
    const submit = screen.getByRole('button', { name: submitName });
    act(() => {
      submit.click();
      submit.click();
    });

    await waitFor(() => expect(recoverDispatchClaim).toHaveBeenCalledTimes(1));
    await act(async () => {
      resolveRecover({
        kind: 'recovered',
        httpStatus: 200,
        etag: '"rev-2"',
        resource: { ...snapshot().resource, hasIndeterminateClaim: false, recoveryAuditId: 'a' },
      });
    });
    expect(recoverDispatchClaim).toHaveBeenCalledTimes(1);
  });

  it('submits the reviewed claim with If-Match and an idempotency key', async () => {
    const user = userEvent.setup();
    recoverDispatchClaim.mockResolvedValue({
      kind: 'recovered',
      httpStatus: 200,
      etag: '"rev-2"',
      resource: { ...snapshot().resource, hasIndeterminateClaim: false, recoveryAuditId: 'audit-9' },
    });
    const { onRecovered, onClose } = renderModal();

    await user.click(screen.getByLabelText(physicalLabel));
    await user.type(screen.getByLabelText('Note (optional)'), '  checked bed  ');
    await user.click(screen.getByRole('button', { name: submitName }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    const call = recoverDispatchClaim.mock.calls[0][0];
    expect(call.printerId).toBe('printer-1');
    expect(call.etag).toBe('"rev-1"');
    expect(call.idempotencyKey).toEqual(expect.any(String));
    expect(call.idempotencyKey.length).toBeGreaterThan(0);
    expect(call.body).toEqual({
      dispatchAttemptId: 'attempt-1',
      claimRevision: 4,
      physicalCheckConfirmed: true,
      senderIsolationConfirmed: false,
      note: 'checked bed',
    });
    expect(call.body).not.toHaveProperty('clientReportedAtUtc');
    expect(onRecovered).toHaveBeenCalledWith('audit-9', 'attempt-1');
    expect(toastSuccess).toHaveBeenCalled();
  });

  it('on 412 clears confirmations, refetches, and uses a new key next time', async () => {
    const user = userEvent.setup();
    recoverDispatchClaim.mockResolvedValueOnce({
      kind: 'stale',
      httpStatus: 412,
      errorCode: 'rejected_stale',
    });
    const { onRefresh, rerenderWith, onClose } = renderModal();

    await user.click(screen.getByLabelText(physicalLabel));
    await user.click(screen.getByRole('button', { name: submitName }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/claim changed since you reviewed it/);
    expect(onRefresh).toHaveBeenCalledTimes(1);
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByLabelText(physicalLabel)).not.toBeChecked();

    rerenderWith(snapshot({ claimRevision: 5 }, '"rev-2"'));
    recoverDispatchClaim.mockResolvedValueOnce({
      kind: 'conflict',
      httpStatus: 409,
      errorCode: 'rejected_not_indeterminate',
    });
    await user.click(screen.getByLabelText(physicalLabel));
    await user.click(screen.getByRole('button', { name: submitName }));

    await waitFor(() => expect(recoverDispatchClaim).toHaveBeenCalledTimes(2));
    const [first, second] = recoverDispatchClaim.mock.calls.map((c) => c[0]);
    expect(second.etag).toBe('"rev-2"');
    expect(second.body.claimRevision).toBe(5);
    expect(second.idempotencyKey).not.toBe(first.idempotencyKey);
  });

  it('resets confirmations when the reviewed claim changes', async () => {
    const user = userEvent.setup();
    const { rerenderWith } = renderModal();

    await user.click(screen.getByLabelText(physicalLabel));
    expect(screen.getByLabelText(physicalLabel)).toBeChecked();

    rerenderWith(snapshot({ claimRevision: 5 }, '"rev-2"'));
    expect(screen.getByLabelText(physicalLabel)).not.toBeChecked();
    expect(screen.getByRole('button', { name: submitName })).toBeDisabled();
  });

  it('reuses the same key to retry an identical request after a transport failure', async () => {
    const user = userEvent.setup();
    recoverDispatchClaim.mockRejectedValueOnce(new Error('network down'));
    recoverDispatchClaim.mockResolvedValueOnce({
      kind: 'conflict',
      httpStatus: 409,
      errorCode: 'rejected_sender_live',
      liveSender: 'StartSender',
    });
    recoverDispatchClaim.mockResolvedValueOnce({
      kind: 'conflict',
      httpStatus: 409,
      errorCode: 'rejected_sender_live',
    });
    renderModal();

    await user.click(screen.getByLabelText(physicalLabel));
    const submit = screen.getByRole('button', { name: submitName });
    await user.click(submit);
    expect(await screen.findByRole('alert')).toHaveTextContent(/may or may not have been recovered/);

    await user.click(submit);
    expect(await screen.findByText(/may still be running \(StartSender\)/)).toBeInTheDocument();

    await user.click(submit);
    await waitFor(() => expect(recoverDispatchClaim).toHaveBeenCalledTimes(3));
    const keys = recoverDispatchClaim.mock.calls.map((c) => c[0].idempotencyKey);
    expect(keys[1]).toBe(keys[0]);
    // A definitive response retires the key.
    expect(keys[2]).not.toBe(keys[1]);
  });

  it('uses a new key when the body changes after a transport failure', async () => {
    const user = userEvent.setup();
    recoverDispatchClaim.mockRejectedValueOnce(new Error('network down'));
    recoverDispatchClaim.mockRejectedValueOnce(new Error('network down'));
    renderModal();

    await user.click(screen.getByLabelText(physicalLabel));
    await user.click(screen.getByRole('button', { name: submitName }));
    await screen.findByRole('alert');

    await user.type(screen.getByLabelText('Note (optional)'), 'different');
    await user.click(screen.getByRole('button', { name: submitName }));
    await waitFor(() => expect(recoverDispatchClaim).toHaveBeenCalledTimes(2));
    const keys = recoverDispatchClaim.mock.calls.map((c) => c[0].idempotencyKey);
    expect(keys[1]).not.toBe(keys[0]);
  });

  it('demands sender isolation after the server requires it', async () => {
    const user = userEvent.setup();
    recoverDispatchClaim.mockResolvedValueOnce({
      kind: 'conflict',
      httpStatus: 409,
      errorCode: 'rejected_sender_isolation_required',
    });
    renderModal();

    await user.click(screen.getByLabelText(physicalLabel));
    await user.click(screen.getByRole('button', { name: submitName }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/no evidence that the start sender stopped/);
    const isolation = screen.getByLabelText(isolationLabel);
    expect(isolation).not.toBeChecked();
    expect(screen.getByRole('button', { name: submitName })).toBeDisabled();

    await user.click(isolation);
    recoverDispatchClaim.mockResolvedValueOnce({
      kind: 'conflict',
      httpStatus: 409,
      errorCode: 'idempotency_key_reused',
    });
    await user.click(screen.getByRole('button', { name: submitName }));
    await waitFor(() => expect(recoverDispatchClaim).toHaveBeenCalledTimes(2));
    expect(recoverDispatchClaim.mock.calls[1][0].body.senderIsolationConfirmed).toBe(true);
    expect(await screen.findByText(/already submitted with different details/)).toBeInTheDocument();
  });

  it('refetches when the claim is no longer indeterminate', async () => {
    const user = userEvent.setup();
    recoverDispatchClaim.mockResolvedValueOnce({
      kind: 'conflict',
      httpStatus: 409,
      errorCode: 'rejected_not_indeterminate',
    });
    const { onRefresh } = renderModal();

    await user.click(screen.getByLabelText(physicalLabel));
    await user.click(screen.getByRole('button', { name: submitName }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/no longer the printer/);
    expect(onRefresh).toHaveBeenCalledTimes(1);
  });

  it('shows an informational state and no submit when there is no open claim', () => {
    renderModal({ snap: snapshot({ hasIndeterminateClaim: false, dispatchAttemptId: null }) });

    expect(screen.getByText(/no longer has an indeterminate dispatch claim/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: submitName })).toBeDisabled();
  });
});
