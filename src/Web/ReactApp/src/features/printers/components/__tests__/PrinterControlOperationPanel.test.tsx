import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PrinterControlOperationPanel } from '@/features/printers/components/PrinterControlOperationPanel';
import type { PrinterControlOperationController } from '@/features/printers/hooks/use-printer-control-operation';

const refresh = vi.fn();
const toastError = vi.hoisted(() => vi.fn());
vi.mock('sonner', () => ({ toast: { error: toastError } }));

function control(overrides: Partial<PrinterControlOperationController> = {}): PrinterControlOperationController {
  return {
    isMoonraker: true, blocked: false, checking: false, submitting: false, admitting: false, uncertain: false, error: null,
    saved: null,
    operation: {
      operationId: 'op-1', printerId: 'printer-1', kind: 'HomeAll', state: 'Unknown', rowVersion: 'v1',
      requiresRecovery: false, barrierHeld: false, senderIsolation: 'Pending', failure: null, completionEvidence: 'None',
    },
    current: { physicalControl: { supportedOperations: ['HomeAll'], barrierHeld: false }, operation: null },
    tracker: { refresh },
    ...overrides,
  } as unknown as PrinterControlOperationController;
}
beforeEach(() => { vi.clearAllMocks(); refresh.mockResolvedValue(null); });

describe('motion feedback without a recovery workflow', () => {
  it.each(['Queued', 'Running'] as const)('shows ordinary %s without warnings or exposed IDs', state => {
    const ordinary = control();
    ordinary.operation = { ...ordinary.operation!, kind: 'Jog', state, barrierHeld: true };
    render(<PrinterControlOperationPanel control={ordinary} />);
    expect(screen.getByRole('status')).toHaveTextContent(state === 'Queued' ? 'Jog: waiting to start' : 'Jog: in progress');
    expect(screen.queryByText(/Do not repeat/)).not.toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
    expect(screen.getByText('Operation: op-1')).not.toBeVisible();
    fireEvent.click(screen.getByText('Motion technical details'));
    expect(screen.getByText('Operation: op-1')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Recheck motion status' })).toBeVisible();
  });

  it('does not mistake a new reservation for uncertainty or show its predecessor', () => {
    const reserving = control({
      submitting: true, admitting: true, uncertain: true,
      saved: { operationId: 'new-op', intent: { kind: 'Jog', y: 10 } },
    });
    reserving.operation = {
      ...reserving.operation!, state: 'Failed',
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

  it('shows lost confirmation prominently without a resubmission action', () => {
    render(<PrinterControlOperationPanel control={control({
      operation: null, uncertain: true, saved: { operationId: 'saved-op', intent: { kind: 'Jog', y: 10 } },
      error: 'Cannot contact the printer server.',
    })} />);
    expect(screen.getByRole('status')).toHaveTextContent('Cannot contact the printer server.');
    expect(screen.getByText(/Do not repeat this movement/)).toBeVisible();
    expect(screen.getAllByRole('button')).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'Recheck motion status' })).toBeVisible();
  });

  it('shows actionable failures with diagnostics collapsed', () => {
    const failed = control();
    failed.operation = {
      ...failed.operation!, state: 'Failed', completionEvidence: 'NotSent',
      failure: { code: 'printer_not_homed', message: 'Home the requested axes before moving.' },
    };
    render(<PrinterControlOperationPanel control={failed} />);
    expect(screen.getByRole('alert')).toHaveTextContent('Home the requested axes before moving.');
    expect(screen.queryByText(/Do not repeat/)).not.toBeInTheDocument();
    expect(screen.getByText('Diagnostic code: printer_not_homed')).not.toBeVisible();
  });

  it.each(['Unknown', 'Recovering', 'Recovered'] as const)(
    'displays historical %s honestly without attestation, recovery, or retry controls', state => {
      const historical = control();
      historical.operation = { ...historical.operation!, state, requiresRecovery: true };
      render(<PrinterControlOperationPanel control={historical} />);
      expect(screen.getByRole('status')).toHaveTextContent(state === 'Recovered'
        ? 'historical operator recovery, not successful execution' : 'outcome unknown');
      expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: /recover|retry|re-submit|confirm/i })).not.toBeInTheDocument();
      expect(screen.queryByText(/queue:reconcile|authorized operator|prerequisite|requires.*access/i)).not.toBeInTheDocument();
    });

  it('rechecks unknown status without sending a physical command', () => {
    render(<PrinterControlOperationPanel control={control()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Recheck motion status' }));
    expect(refresh).toHaveBeenCalledOnce();
    expect(screen.getByRole('status')).toHaveTextContent('outcome unknown');
  });

  it('explains a failed status read without inventing a permanent lock', async () => {
    refresh.mockRejectedValueOnce(new Error('offline'));
    render(<PrinterControlOperationPanel control={control()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Recheck motion status' }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith('Unable to verify current motion status.'));
  });

  it('explains the genuinely active external command', () => {
    const external = control({ operation: null, blocked: true });
    external.current!.physicalControl.barrierHeld = true;
    render(<PrinterControlOperationPanel control={external} />);
    expect(screen.getByText(/Another printer action is active/)).toBeVisible();
    expect(screen.queryByText(/operator|recovery/)).not.toBeInTheDocument();
  });
});
