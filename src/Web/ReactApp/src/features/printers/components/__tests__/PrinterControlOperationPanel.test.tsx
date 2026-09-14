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

describe('compact motion feedback', () => {
  it.each(['Queued', 'Running'] as const)('shows ordinary %s without recovery warnings or exposed IDs', state => {
    const ordinary = control();
    ordinary.operation = { ...ordinary.operation!, kind: 'Jog', state, requiresRecovery: false };
    render(<PrinterControlOperationPanel control={ordinary} />);
    expect(screen.getByRole('status')).toHaveTextContent(state === 'Queued' ? 'Jog: waiting to start' : 'Jog: in progress');
    expect(screen.queryByText(/Do not repeat/)).not.toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
    expect(screen.getByText('Operation: op-1')).not.toBeVisible();
    fireEvent.click(screen.getByText('Motion technical details'));
    expect(screen.getByText('Operation: op-1')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Recheck motion status' })).toBeVisible();
  });

  it('does not mistake an ordinary new reservation for uncertainty or show its predecessor', () => {
    const reserving = control({
      submitting: true, admitting: true, uncertain: true,
      saved: { operationId: 'new-op', intent: { kind: 'Jog', y: 10 } },
    });
    reserving.operation = {
      ...reserving.operation!, state: 'Failed', requiresRecovery: false,
      failure: { code: 'old-failure', message: 'Previous failure' },
    };
    render(<PrinterControlOperationPanel control={reserving} />);
    expect(screen.getByRole('status')).toHaveTextContent('Jog: sending request');
    expect(screen.queryByText(/Do not repeat|Previous failure|Operation: op-1/)).not.toBeInTheDocument();
    expect(screen.getByText('Operation: new-op')).not.toBeVisible();
  });

  it('keeps initial availability checking free of uncertain-motion warnings', () => {
    render(<PrinterControlOperationPanel control={control({
      operation: null, current: null, checking: true, uncertain: true,
    })} />);
    expect(screen.getByRole('status')).toHaveTextContent('Checking motion availability');
    expect(screen.queryByText(/Do not repeat/)).not.toBeInTheDocument();
  });

  it('shows actual lost confirmation prominently, not in technical details', () => {
    render(<PrinterControlOperationPanel control={control({
      operation: null, uncertain: true, saved: { operationId: 'saved-op', intent: { kind: 'Jog', y: 10 } },
      error: 'Cannot contact the printer server.',
    })} />);
    expect(screen.getByRole('status')).toHaveTextContent('Cannot contact the printer server.');
    expect(screen.getByText(/Do not repeat this movement/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Recheck motion status' })).toBeVisible();
  });

  it('shows actionable failures while keeping diagnostic codes in the disclosure', () => {
    const failed = control();
    failed.operation = {
      ...failed.operation!, state: 'Failed', requiresRecovery: false, completionEvidence: 'NotSent',
      failure: { code: 'printer_not_homed', message: 'Home the requested axes before moving.' },
    };
    render(<PrinterControlOperationPanel control={failed} />);
    expect(screen.getByRole('alert')).toHaveTextContent('Home the requested axes before moving.');
    expect(screen.queryByText(/Do not repeat/)).not.toBeInTheDocument();
    expect(screen.getByText('Diagnostic code: printer_not_homed')).not.toBeVisible();
  });
});

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
    expect(screen.getByRole('status')).toHaveTextContent('recovery in progress');
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
