import { toast } from 'sonner';
import { Button } from '@/common/components/ui';
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
    case 'Recovering': return `${name}: historical interrupted operation, outcome unknown`;
    case 'Recovered': return `${name}: historical operator recovery, not successful execution`;
  }
}

export function PrinterControlOperationPanel({ control }: Props) {
  if (!control.usesDurableMotion && !control.blocked && !control.operation && !control.error) return null;
  const { saved, error, tracker, uncertain, checking, admitting } = control;
  const operation = saved && control.operation?.operationId !== saved.operationId ? null : control.operation;
  const visibleError = admitting && !operation ? null : error;
  const unknownOutcome = operation?.state === 'Unknown' || operation?.state === 'Recovering' ||
    (!!saved && uncertain && !checking && !admitting);
  const externalBarrier = !!control.current?.physicalControl.barrierHeld && !operation && !saved;
  const needsAttention = !!visibleError || unknownOutcome || externalBarrier;
  const status = visibleError ?? (operation ? operationStatus(operation)
    : admitting && saved ? `${motionNames[saved.intent.kind]}: sending request`
      : checking ? 'Checking motion availability'
        : saved ? 'Waiting for motion confirmation'
          : control.blocked ? 'Motion unavailable'
            : 'Ready');
  const recheck = (
    <Button size="sm" variant="secondary" disabled={!tracker || control.submitting} onClick={() => {
      void tracker?.refresh().catch(() => toast.error('Unable to verify current motion status.'));
    }}>Recheck motion status</Button>
  );
  return (
    <section aria-label="Motion status" className={`my-2 rounded border px-3 py-2 text-sm space-y-2 ${needsAttention ? 'border-pf-warning' : 'border-pf-border'}`}>
      <div role="status" aria-live="polite">
        <strong>Motion: </strong>
        {status}
      </div>
      {unknownOutcome && <p>Do not repeat this movement. Its completion is not confirmed and it was not retried. Leaving this page does not cancel a command already sent.</p>}
      {operation?.failure && <p role="alert" className="text-pf-error">{operation.failure.message}</p>}
      {!tracker && <p>Sign in to check motion availability.</p>}
      {externalBarrier && <p>Another printer action is active. Motion controls will be available when it finishes.</p>}
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
    </section>
  );
}
