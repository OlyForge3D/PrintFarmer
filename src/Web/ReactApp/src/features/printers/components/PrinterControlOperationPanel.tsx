import { useId, useState } from 'react';
import { toast } from 'sonner';
import { Button, FormField, Input } from '@/common/components/ui';
import { mutationErrorMessage, mutationErrorStatus } from '@/common/utils/mutationError';
import type { PrinterControlOperationController } from '@/features/printers/hooks/use-printer-control-operation';
import type { PrinterControlOperationKind, PrinterControlOperation } from '@/types/api';

interface Props { control: PrinterControlOperationController }

const motionNames: Record<PrinterControlOperationKind, string> = {
  HomeAll: 'Home all axes', HomeXY: 'Home X/Y', HomeZ: 'Home Z',
  Jog: 'Jog', MoveTo: 'Move to position',
};

function operationStatus(operation: PrinterControlOperation): string {
  const name = motionNames[operation.kind];
  switch (operation.state) {
    case 'Queued': return `${name}: waiting to start`;
    case 'Running': return `${name}: in progress`;
    case 'Succeeded': return operation.completionEvidence === 'MotionQueueDrained'
      ? `${name}: complete` : `${name}: completion not confirmed`;
    case 'Failed': return `${name}: ${operation.completionEvidence === 'NotSent' ? 'not sent'
      : operation.completionEvidence === 'BackendRejected' ? 'rejected by printer' : 'did not complete'}`;
    case 'Unknown': return `${name}: outcome unknown`;
    case 'Recovering': return `${name}: recovery in progress`;
    case 'Recovered': return `${name}: operator recovery, not successful execution`;
  }
}

export function PrinterControlOperationPanel({ control }: Props) {
  if (!control.isMoonraker) return null;
  const { saved, error, tracker, uncertain, checking, canRecover, etag, admitting } = control;
  const operation = saved && control.operation?.operationId !== saved.operationId ? null : control.operation;
  const visibleError = admitting && !operation ? null : error;
  const unresolved = !!operation?.requiresRecovery || operation?.state === 'Unknown' ||
    operation?.state === 'Recovering' || (!!saved && uncertain && !checking && !admitting);
  const externalBarrier = !!control.current?.physicalControl.barrierHeld && !operation && !saved;
  const needsAttention = !!visibleError || unresolved || externalBarrier;
  const status = visibleError ?? (operation ? operationStatus(operation)
    : admitting && saved ? `${motionNames[saved.intent.kind]}: sending request`
      : checking ? 'Checking motion availability'
        : saved ? 'Waiting for motion confirmation'
          : control.blocked ? 'Motion unavailable'
            : 'Ready');
  const recheck = (
    <Button size="sm" variant="secondary" disabled={!tracker || control.submitting} onClick={() => {
      void tracker?.refresh().catch(() => toast.error('Unable to verify motion status. Controls remain locked.'));
    }}>Recheck motion status</Button>
  );
  return (
    <section aria-label="Motion status" className={`my-2 rounded border px-3 py-2 text-sm space-y-2 ${needsAttention ? 'border-pf-warning' : 'border-pf-border'}`}>
      <div role="status" aria-live="polite">
        <strong>Motion: </strong>
        {status}
      </div>
      {unresolved && <p>Do not repeat this movement. Its completion is not confirmed. Recheck its status before proceeding; leaving this page does not cancel it.</p>}
      {operation?.failure && <p role="alert" className="text-pf-error">{operation.failure.message}</p>}
      {!tracker && <p>Sign in to check motion availability.</p>}
      {control.current && !control.current.physicalControl.supportedOperations.length && <p>Update the server to enable Moonraker motion controls.</p>}
      {externalBarrier && <p>Another printer action is holding motion controls. Recheck its status or ask an authorized operator; this form cannot clear it.</p>}
      {needsAttention && recheck}
      {(operation || saved || tracker) && (
        <details className="text-xs text-pf-text-secondary">
          <summary className="cursor-pointer py-1 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent">Motion technical details</summary>
          <div className="space-y-2 pt-2">
            {(operation || saved) && <p className="break-all">Operation: {operation?.operationId ?? saved?.operationId}</p>}
            {operation && <p>State: {operation.state}. Completion evidence: {operation.completionEvidence}.</p>}
            {operation?.failure && <p className="break-all">Diagnostic code: {operation.failure.code}</p>}
            {!needsAttention && recheck}
          </div>
        </details>
      )}
      {control.canRetryAdmission && saved && <AdmissionRetry key={saved.operationId} control={control} />}
      {operation?.requiresRecovery && (
        canRecover ? <RecoveryForm key={`${operation.operationId}:${operation.rowVersion}`} control={control} />
          : <p>Recovery requires queue:reconcile permission and Submit access to this printer. Ask an authorized operator.</p>
      )}
      {canRecover && operation?.requiresRecovery && (uncertain || !etag) && <p>Recovery is unavailable until a fresh operation GET provides a valid revision and status.</p>}
    </section>
  );
}

function AdmissionRetry({ control }: Props) {
  const [reviewing, setReviewing] = useState(false);
  const { saved, tracker } = control;
  if (!saved) return null;
  const intentSummary = [saved.intent.kind, ...(['x', 'y', 'z', 'f'] as const)
    .filter(axis => saved.intent[axis] !== undefined)
    .map(axis => `${axis}=${saved.intent[axis]}`)].join(', ');
  return (
    <div className="space-y-2" role="group" aria-label="Review saved motion admission">
      <p>The saved ID is not currently visible. Recovery cannot resolve a client-only receipt. Re-submitting uses this exact saved ID and intent, never a new ID. If the server never admitted it, this can admit and start physical motion. If already admitted, it returns that same operation without sending motion twice. This is not a retry of known Unknown execution.</p>
      <p className="break-all">Saved motion: {intentSummary}</p>
      {reviewing ? (
        <>
          <p>Confirm only if you still intend this original movement. Declining keeps motion blocked and sends nothing.</p>
          <Button size="sm" variant="danger" disabled={control.submitting} onClick={() => {
            setReviewing(false);
            void tracker?.retryAdmission().catch(error => toast.error(mutationErrorMessage(error, 'Admission still uncertain')));
          }}>Confirm re-submit saved motion</Button>
          <Button size="sm" variant="secondary" onClick={() => setReviewing(false)}>Decline and keep blocked</Button>
        </>
      ) : <Button size="sm" disabled={control.submitting} onClick={() => setReviewing(true)}>Review re-submit of saved motion</Button>}
    </div>
  );
}

function RecoveryForm({ control }: Props) {
  const id = useId();
  const [reason, setReason] = useState('');
  const [isolationEvidence, setIsolationEvidence] = useState('');
  const [physicalEvidence, setPhysicalEvidence] = useState('');
  const [isolated, setIsolated] = useState(false);
  const [queueCleared, setQueueCleared] = useState(false);
  const [stationary, setStationary] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { operation, etag, tracker } = control;
  if (!operation) return null;
  const serviceConfirmed = operation.senderIsolation === 'Confirmed';
  const externalRequired = operation.senderIsolation === 'ExternalVerificationRequired';
  const fresh = !control.uncertain && !!etag && !control.submitting;
  const canComplete = fresh && operation.state === 'Recovering' && (serviceConfirmed || externalRequired) &&
    isolated && queueCleared && stationary && !!reason.trim() && !!isolationEvidence.trim() && !!physicalEvidence.trim();
  const recover = async (complete: boolean) => {
    if (!etag || !tracker || (complete && !canComplete)) return;
    setError(null);
    try {
      await tracker.recover(operation.operationId, etag, complete ? {
        reason: reason.trim(),
        senderIsolation: serviceConfirmed ? 'ServiceConfirmed' : 'ExternallyVerified',
        senderIsolationEvidence: isolationEvidence.trim(),
        controllerQueueCleared: true, physicallyStationary: true, physicalEvidence: physicalEvidence.trim(),
      } : undefined);
      toast.info(complete ? 'Recovery recorded. Recheck current motion before proceeding.' : 'Recovery requested. Sender isolation must be confirmed before completion.');
    } catch (failure) {
      const status = mutationErrorStatus(failure);
      const message = status === 403 ? 'Recovery denied. Queue:reconcile permission and Submit access to this printer are required.'
        : status === 404 ? 'The printer or operation is unavailable or you do not have access. Recovery is not confirmed; do not assume the barrier was cleared.'
          : status === 412 || status === 428 ? 'The reviewed revision is stale or missing. Recheck and review all attestations again.'
            : status === 409 ? 'Recovery prerequisites are not satisfied. Recheck the operation and sender isolation.'
              : mutationErrorMessage(failure, 'Recovery response uncertain. Recheck; do not assume the barrier was cleared.');
      setError(message);
      setIsolated(false); setQueueCleared(false); setStationary(false);
      toast.error(message);
    }
  };
  return (
    <div className="border-t border-pf-border pt-2 space-y-3">
      <h3 className="font-semibold">Operator recovery</h3>
      <p>Requires queue:reconcile permission and printer Submit access, verified by the server. This form sends no stop, reset, or hardware commands.</p>
      <p>Prior sender isolation: {operation.senderIsolation}. {operation.senderIsolation === 'Pending' ? 'Wait for service confirmation; elapsed time is not evidence.' : ''}</p>
      {operation.state !== 'Recovering' && <Button size="sm" disabled={!fresh} onClick={() => void recover(false)}>Request recovery and sender isolation</Button>}
      {operation.state === 'Recovering' && (
        <>
          {!serviceConfirmed && !externalRequired && <p>Completion unavailable: the prior sender has not been isolated. Recheck for confirmation or an explicit external-verification requirement.</p>}
          <FormField label="Reason for recovery" htmlFor={`${id}-reason`} required>
            <Input id={`${id}-reason`} value={reason} onChange={e => setReason(e.target.value)} />
          </FormField>
          <FormField label="Prior-sender isolation evidence" htmlFor={`${id}-isolation`} required helper={serviceConfirmed ? 'Record the reviewed service confirmation.' : 'Describe how you independently verified the prior sender cannot issue further commands.'}>
            <Input id={`${id}-isolation`} value={isolationEvidence} onChange={e => setIsolationEvidence(e.target.value)} />
          </FormField>
          <FormField label="Queue-clearance and physical-stationarity evidence" htmlFor={`${id}-physical`} required>
            <Input id={`${id}-physical`} value={physicalEvidence} onChange={e => setPhysicalEvidence(e.target.value)} />
          </FormField>
          <label className="flex items-start gap-2"><Input type="checkbox" checked={isolated} onChange={e => setIsolated(e.target.checked)} />I verified that the prior sender is isolated and cannot send more commands.</label>
          <label className="flex items-start gap-2"><Input type="checkbox" checked={queueCleared} onChange={e => setQueueCleared(e.target.checked)} />I independently verified the controller motion queue is cleared.</label>
          <label className="flex items-start gap-2"><Input type="checkbox" checked={stationary} onChange={e => setStationary(e.target.checked)} />I physically verified the printer is stationary.</label>
          <p>All three confirmations and evidence fields are required. Position telemetry, homed axes, and waiting are not recovery evidence.</p>
          <Button size="sm" variant="danger" disabled={!canComplete} onClick={() => void recover(true)}>Record verified recovery (not motion success)</Button>
        </>
      )}
      {error && <p role="alert" className="text-pf-error">{error}</p>}
    </div>
  );
}
