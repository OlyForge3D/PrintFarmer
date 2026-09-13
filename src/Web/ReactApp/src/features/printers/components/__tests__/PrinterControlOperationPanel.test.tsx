import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PrinterControlOperationPanel } from '@/features/printers/components/PrinterControlOperationPanel';
import type { PrinterControlOperationController } from '@/features/printers/hooks/use-printer-control-operation';

const recover = vi.fn();
const refresh = vi.fn();
const retryAdmission = vi.fn();
const toastError = vi.hoisted(() => vi.fn());
vi.mock('sonner', () => ({ toast: { info: vi.fn(), error: toastError } }));

function control(overrides: Partial<PrinterControlOperationController> = {}): PrinterControlOperationController {
  return {
    isMoonraker: true, blocked: true, checking: false, submitting: false, uncertain: false, error: null,
    missingAdmission: false, saved: null, canRecover: true, canRetryAdmission: false, etag: '"actual-etag"',
    operation: {
      operationId: 'op-1', printerId: 'printer-1', kind: 'HomeAll', state: 'Recovering', rowVersion: 'v1',
      requiresRecovery: true, barrierHeld: true, senderIsolation: 'Confirmed', failure: null,
    },
    current: { physicalControl: { supportedOperations: ['HomeAll'], barrierHeld: true }, operation: null },
    tracker: { recover, refresh, retryAdmission },
    ...overrides,
  } as unknown as PrinterControlOperationController;
}
function attest() {
  fireEvent.change(screen.getByLabelText('Reason for recovery', { exact: false }), { target: { value: 'Interrupted operation reviewed' } });
  fireEvent.change(screen.getByLabelText('Prior-sender isolation evidence', { exact: false }), { target: { value: 'Reviewed service-confirmed isolation' } });
  fireEvent.change(screen.getByLabelText('Queue-clearance and physical-stationarity evidence', { exact: false }), { target: { value: 'Operator verified controller queue clear and printer stationary' } });
  for (const checkbox of screen.getAllByRole('checkbox')) fireEvent.click(checkbox);
}
beforeEach(() => { vi.clearAllMocks(); recover.mockResolvedValue(undefined); refresh.mockResolvedValue(null); retryAdmission.mockResolvedValue(undefined); });

describe('operator recovery safeguards', () => {
  it('makes admission retry deliberate and explains it can start previously unadmitted motion', () => {
    render(<PrinterControlOperationPanel control={control({
      operation: null, missingAdmission: true, uncertain: true, canRetryAdmission: true,
      saved: { operationId: 'saved-id', intent: { kind: 'HomeAll' } },
      current: {
        operation: null,
        physicalControl: { supportedOperations: ['HomeAll'], barrierHeld: false, requiresRecovery: false, operationId: null, state: null },
      },
    })} />);
    expect(screen.getByText(/this can admit and start physical motion/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Request recovery and sender isolation/ })).not.toBeInTheDocument();
    expect(retryAdmission).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: /Review re-submit/ }));
    expect(retryAdmission).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: /Decline and keep blocked/ }));
    expect(retryAdmission).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: /Review re-submit/ }));
    fireEvent.click(screen.getByRole('button', { name: /Confirm re-submit/ }));
    expect(retryAdmission).toHaveBeenCalledOnce();
    expect(recover).not.toHaveBeenCalled();
  });

  it('offers no recovery actions without recovery permission', () => {
    render(<PrinterControlOperationPanel control={control({ canRecover: false })} />);
    expect(screen.getByText(/Recovery requires queue:reconcile permission and Submit access/)).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Recovering');
    expect(screen.getByRole('button', { name: /Recheck motion status/ })).toBeEnabled();
    expect(screen.queryByRole('button', { name: /Request recovery and sender isolation/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Record verified recovery/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  });

  it('requires three separate unticked attestations plus evidence and the actual ETag', async () => {
    render(<PrinterControlOperationPanel control={control()} />);
    const submit = screen.getByRole('button', { name: /Record verified recovery/ });
    expect(submit).toBeDisabled();
    expect(screen.getAllByRole('checkbox')).toHaveLength(3);
    for (const checkbox of screen.getAllByRole('checkbox')) expect(checkbox).not.toBeChecked();
    attest();
    expect(submit).toBeEnabled();
    fireEvent.click(submit);
    await waitFor(() => expect(recover).toHaveBeenCalledWith('op-1', '"actual-etag"', {
      reason: 'Interrupted operation reviewed', senderIsolation: 'ServiceConfirmed',
      senderIsolationEvidence: 'Reviewed service-confirmed isolation',
      controllerQueueCleared: true, physicallyStationary: true,
      physicalEvidence: 'Operator verified controller queue clear and printer stationary',
    }));
  });

  it('does not allow elapsed time or attestations to substitute for pending sender isolation', () => {
    const pending = control();
    pending.operation = { ...pending.operation!, senderIsolation: 'Pending' };
    render(<PrinterControlOperationPanel control={pending} />);
    attest();
    expect(screen.getByRole('button', { name: /Record verified recovery/ })).toBeDisabled();
    expect(screen.getByText(/Completion unavailable/)).toBeInTheDocument();
  });

  it('accepts external isolation evidence only when the server explicitly requests it', async () => {
    const external = control();
    external.operation = { ...external.operation!, senderIsolation: 'ExternalVerificationRequired' };
    render(<PrinterControlOperationPanel control={external} />);
    attest();
    fireEvent.click(screen.getByRole('button', { name: /Record verified recovery/ }));
    await waitFor(() => expect(recover).toHaveBeenCalledWith('op-1', '"actual-etag"', expect.objectContaining({ senderIsolation: 'ExternallyVerified' })));
  });

  it.each([403, 404, 409, 412, 428])('explains recovery HTTP %s and clears all confirmations', async statusCode => {
    recover.mockRejectedValueOnce({ statusCode });
    render(<PrinterControlOperationPanel control={control()} />);
    attest();
    fireEvent.click(screen.getByRole('button', { name: /Record verified recovery/ }));
    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument());
    if (statusCode === 403) {
      expect(screen.getByRole('alert')).toHaveTextContent('Queue:reconcile permission and Submit access');
      expect(screen.getByRole('alert')).not.toHaveTextContent('administrator');
    }
    if (statusCode === 404) {
      expect(screen.getByRole('alert')).toHaveTextContent('unavailable or you do not have access');
      expect(screen.getByRole('alert')).toHaveTextContent('Recovery is not confirmed');
    }
    for (const checkbox of screen.getAllByRole('checkbox')) expect(checkbox).not.toBeChecked();
    expect(screen.getByRole('button', { name: /Record verified recovery/ })).toBeDisabled();
    expect(toastError).toHaveBeenCalledOnce();
  });

  it('requires fresh authoritative status/ETag even for an administrator', () => {
    render(<PrinterControlOperationPanel control={control({ etag: null, uncertain: true })} />);
    attest();
    expect(screen.getByRole('button', { name: /Record verified recovery/ })).toBeDisabled();
    expect(screen.getByText(/fresh operation GET/)).toBeInTheDocument();
  });

  it('invalidates deliberate attestations when the reviewed operation revision changes', () => {
    const original = control();
    const { rerender } = render(<PrinterControlOperationPanel control={original} />);
    attest();
    rerender(<PrinterControlOperationPanel control={control({ operation: { ...original.operation!, rowVersion: 'v2' }, etag: '"new"' })} />);
    for (const checkbox of screen.getAllByRole('checkbox')) expect(checkbox).not.toBeChecked();
  });

  it('renders Recovered explicitly as recovery, not successful execution', () => {
    const recovered = control();
    recovered.operation = { ...recovered.operation!, state: 'Recovered', requiresRecovery: false, barrierHeld: false };
    render(<PrinterControlOperationPanel control={recovered} />);
    expect(screen.getByRole('status')).toHaveTextContent('operator recovery, not successful execution');
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  });
});
