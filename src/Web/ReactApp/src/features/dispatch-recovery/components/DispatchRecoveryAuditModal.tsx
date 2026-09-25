import { Modal } from '@/common/components/modals/Modal';
import { Alert, Button } from '@/common/components/ui';
import { useDispatchRecoveryAudit } from '@/features/dispatch-recovery/hooks/useDispatchRecovery';
import { dispatchOutcomeLabel } from '@/features/dispatch-recovery/utils';

export interface DispatchRecoveryAuditModalProps {
  isOpen: boolean;
  onClose: () => void;
  printerId: string;
  printerName: string;
  auditId: string | null;
}

function formatUtc(value: string | null | undefined): string {
  if (!value) {
    return '—';
  }
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

/** Read-only view of one immutable recovery-evidence record. */
export function DispatchRecoveryAuditModal({
  isOpen,
  onClose,
  printerId,
  printerName,
  auditId,
}: DispatchRecoveryAuditModalProps) {
  const auditQuery = useDispatchRecoveryAudit(printerId, auditId, { enabled: isOpen });
  const audit = auditQuery.data;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={`Recovery audit — ${printerName}`}
      size="md"
      footer={
        <div className="flex justify-end">
          <Button variant="subtle" onClick={onClose}>
            Close
          </Button>
        </div>
      }
    >
      {auditQuery.isLoading && <p className="text-sm text-pf-text-secondary">Loading audit record…</p>}
      {auditQuery.isError && (
        <Alert type="error">The recovery audit record could not be loaded.</Alert>
      )}
      {audit && (
        <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1 text-sm text-pf-text-primary">
          <dt className="text-pf-text-secondary">Audit</dt>
          <dd className="font-mono break-all">{audit.auditId}</dd>
          <dt className="text-pf-text-secondary">Decision</dt>
          <dd>{audit.transition}</dd>
          <dt className="text-pf-text-secondary">Job</dt>
          <dd className="font-mono break-all">{audit.jobId ?? '—'}</dd>
          <dt className="text-pf-text-secondary">Dispatch attempt</dt>
          <dd className="font-mono break-all">{audit.dispatchAttemptId}</dd>
          <dt className="text-pf-text-secondary">Claim revision</dt>
          <dd>{audit.claimRevision}</dd>
          <dt className="text-pf-text-secondary">Prior outcome</dt>
          <dd>{dispatchOutcomeLabel(audit.priorOutcome)}</dd>
          <dt className="text-pf-text-secondary">Operator</dt>
          <dd className="break-all">{audit.actorId}</dd>
          <dt className="text-pf-text-secondary">Recorded</dt>
          <dd>{formatUtc(audit.serverRecordedAtUtc)}</dd>
          <dt className="text-pf-text-secondary">Physical check</dt>
          <dd>{audit.physicalCheckConfirmed ? 'Confirmed' : 'Not confirmed'}</dd>
          <dt className="text-pf-text-secondary">Sender isolation</dt>
          <dd>{audit.senderIsolationConfirmed ? 'Confirmed' : 'Not asserted'}</dd>
          <dt className="text-pf-text-secondary">Sender settled</dt>
          <dd>{formatUtc(audit.senderSettledAtUtc)}</dd>
          <dt className="text-pf-text-secondary">Note</dt>
          <dd className="whitespace-pre-wrap break-words">{audit.note || '—'}</dd>
          <dt className="text-pf-text-secondary">Correlation</dt>
          <dd className="font-mono break-all">{audit.correlationId ?? '—'}</dd>
        </dl>
      )}
    </Modal>
  );
}
