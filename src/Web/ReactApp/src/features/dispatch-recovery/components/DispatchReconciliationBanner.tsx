import { useId, useState } from 'react';
import { AlertTriangle, CheckCircle2 } from 'lucide-react';
import { Button } from '@/common/components/ui';
import { useDispatchReconciliation } from '@/features/dispatch-recovery/hooks/useDispatchRecovery';
import { DispatchRecoveryModal } from '@/features/dispatch-recovery/components/DispatchRecoveryModal';
import { DispatchRecoveryAuditModal } from '@/features/dispatch-recovery/components/DispatchRecoveryAuditModal';
import { escalationLabel, formatClaimAge, shortId } from '@/features/dispatch-recovery/utils';

export interface DispatchReconciliationBannerProps {
  printerId: string;
  printerName: string;
  className?: string;
}

interface RecordedRecovery {
  auditId: string | null;
  dispatchAttemptId: string;
}

/**
 * Indeterminate-claim warning for one printer (issue #2993). Renders nothing
 * unless the server reports an indeterminate claim, or this operator just
 * recorded a recovery that a refetch has since confirmed closed. The recover
 * action is gated on the server-derived `recoveryPermission`; there are no
 * cancel or retry actions here by design.
 */
export function DispatchReconciliationBanner({
  printerId,
  printerName,
  className,
}: DispatchReconciliationBannerProps) {
  const headingId = useId();
  const reconciliation = useDispatchReconciliation(printerId);
  const [recoverOpen, setRecoverOpen] = useState(false);
  const [auditTarget, setAuditTarget] = useState<string | null>(null);
  const [recorded, setRecorded] = useState<RecordedRecovery | null>(null);

  const snapshot = reconciliation.data;
  const resource = snapshot?.resource;
  const canRecover = resource?.recoveryPermission === true;
  const displayName = resource?.printerName || printerName;

  const auditModal = (
    <DispatchRecoveryAuditModal
      isOpen={auditTarget !== null}
      onClose={() => setAuditTarget(null)}
      printerId={printerId}
      printerName={displayName}
      auditId={auditTarget}
    />
  );

  if (!resource) {
    return null;
  }

  const recordedClaimStillOpen =
    recorded !== null &&
    resource.hasIndeterminateClaim &&
    resource.dispatchAttemptId === recorded.dispatchAttemptId;

  if (!resource.hasIndeterminateClaim) {
    if (!recorded) {
      return null;
    }
    return (
      <div
        role="status"
        className={`flex flex-wrap items-center gap-2 rounded-md border border-pf-success bg-pf-success-bg px-3 py-2 text-sm text-pf-text-primary ${className ?? ''}`}
      >
        <CheckCircle2 className="h-4 w-4 shrink-0 text-pf-success" aria-hidden="true" />
        <span className="flex-1">
          Recovery recorded for {displayName}. The job is held until an operator allows dispatch.
        </span>
        {recorded.auditId && canRecover && (
          <Button variant="subtle" size="sm" onClick={() => setAuditTarget(recorded.auditId)}>
            View audit
          </Button>
        )}
        <Button
          variant="subtle"
          size="sm"
          onClick={() => setRecorded(null)}
          aria-label={`Dismiss recovery notice for ${displayName}`}
        >
          Dismiss
        </Button>
        {auditModal}
      </div>
    );
  }

  const settledText =
    resource.senderSettled === true
      ? 'Start sender settled'
      : 'No evidence the start sender has stopped';
  const evidence = resource.lastEvidence;

  return (
    <section
      role="status"
      aria-labelledby={headingId}
      className={`rounded-md border border-pf-warning bg-pf-warning px-3 py-2 text-sm text-pf-warning-text ${className ?? ''}`}
    >
      <div className="flex items-start gap-2">
        <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
        <div className="flex-1 space-y-1">
          <h3 id={headingId} className="font-semibold">
            Dispatch outcome unknown on {displayName}
          </h3>
          <p>
            This print may have started. Check the printer physically before taking any action.
            {recordedClaimStillOpen && ' Your recovery was recorded; waiting for the claim to close.'}
          </p>
          <dl className="grid grid-cols-[max-content_1fr] gap-x-3 gap-y-0.5 text-xs">
            <dt>Job</dt>
            <dd className="font-mono" title={resource.jobId ?? undefined}>{shortId(resource.jobId)}</dd>
            <dt>Attempt</dt>
            <dd className="font-mono" title={resource.dispatchAttemptId ?? undefined}>
              {shortId(resource.dispatchAttemptId)}
            </dd>
            <dt>Claim age</dt>
            <dd>{formatClaimAge(resource.claimAgeSeconds)}</dd>
            <dt>Escalation</dt>
            <dd>{escalationLabel(resource.escalationLevel)}</dd>
            <dt>Sender</dt>
            <dd>{settledText}</dd>
            {evidence?.backendCallPhase && (
              <>
                <dt>Last phase</dt>
                <dd>
                  {evidence.backendCallPhase}
                  {evidence.errorCode ? ` (${evidence.errorCode})` : ''}
                </dd>
              </>
            )}
          </dl>
        </div>
        <div className="flex shrink-0 flex-col gap-1">
          {canRecover && (
            <Button
              variant="danger"
              size="sm"
              onClick={() => setRecoverOpen(true)}
              aria-label={`Recover dispatch on ${displayName}`}
            >
              Recover…
            </Button>
          )}
          {canRecover && recorded?.auditId && (
            <Button variant="subtle" size="sm" onClick={() => setAuditTarget(recorded.auditId)}>
              View audit
            </Button>
          )}
        </div>
      </div>
      {canRecover && (
        <DispatchRecoveryModal
          isOpen={recoverOpen}
          onClose={() => setRecoverOpen(false)}
          printerId={printerId}
          printerName={displayName}
          snapshot={snapshot}
          onRefresh={() => void reconciliation.refetch()}
          onRecovered={(auditId, dispatchAttemptId) =>
            setRecorded({ auditId, dispatchAttemptId })
          }
        />
      )}
      {auditModal}
    </section>
  );
}
