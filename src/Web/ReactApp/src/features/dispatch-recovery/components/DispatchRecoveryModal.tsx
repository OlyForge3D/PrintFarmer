import { useId, useRef, useState } from 'react';
import { toast } from 'sonner';
import { AlertTriangle } from 'lucide-react';
import { Modal } from '@/common/components/modals/Modal';
import { Alert, Button, Checkbox, Textarea } from '@/common/components/ui';
import { generateUUID } from '@/utils/uuid';
import { useRecoverDispatchClaim } from '@/features/dispatch-recovery/hooks/useDispatchRecovery';
import { escalationLabel, formatClaimAge } from '@/features/dispatch-recovery/utils';
import type {
  DispatchReconciliationSnapshot,
  DispatchRecoveryRequest,
  DispatchRecoveryResult,
} from '@/types/api';

export const RECOVERY_NOTE_MAX_LENGTH = 1000;

export interface DispatchRecoveryModalProps {
  isOpen: boolean;
  onClose: () => void;
  printerId: string;
  printerName: string;
  /** The reviewed reconciliation snapshot (resource + dispatch-state ETag). */
  snapshot: DispatchReconciliationSnapshot | undefined;
  /** Forces a server refetch of the reconciliation resource (e.g. after 412). */
  onRefresh: () => void;
  onRecovered: (auditId: string | null, dispatchAttemptId: string) => void;
}

interface PendingSubmission {
  key: string;
  fingerprint: string;
}

function rejectionMessage(result: Extract<DispatchRecoveryResult, { kind: Exclude<DispatchRecoveryResult['kind'], 'recovered'> }>): string {
  switch (result.errorCode) {
    case 'rejected_stale':
      return 'The claim changed since you reviewed it. The latest state has been loaded — review it and confirm again.';
    case 'rejected_not_indeterminate':
      return 'This dispatch attempt is no longer the printer\u2019s indeterminate claim. Nothing was changed.';
    case 'rejected_sender_live':
      return `A start or control command for this attempt may still be running${result.liveSender ? ` (${result.liveSender})` : ''}. Wait for it to settle, then try again. Nothing was changed.`;
    case 'rejected_sender_isolation_required':
      return 'The server has no evidence that the start sender stopped. Confirm the sender is stopped or isolated from the printer before recovering.';
    case 'idempotency_key_reused':
      return 'This request was already submitted with different details. Review the confirmation and submit again.';
    case 'physical_check_required':
      return 'You must confirm the physical check before recovering.';
    case 'note_too_long':
      return `The note must be at most ${RECOVERY_NOTE_MAX_LENGTH} characters.`;
    default:
      if (result.kind === 'forbidden' || result.kind === 'not_found') {
        return 'You do not have permission to recover dispatch on this printer.';
      }
      return result.detail || 'Recovery was rejected. Nothing was changed.';
  }
}

/**
 * Operator recovery for an indeterminate dispatch claim (issue #2993).
 * Fail-closed: submission is only possible against a reviewed snapshot with an
 * ETag, the physical check (and sender isolation when unsettled) must be
 * re-confirmed for each claim revision, and nothing is updated optimistically.
 */
export function DispatchRecoveryModal({
  isOpen,
  onClose,
  printerId,
  printerName,
  snapshot,
  onRefresh,
  onRecovered,
}: DispatchRecoveryModalProps) {
  const recover = useRecoverDispatchClaim();
  const noteId = useId();
  const noteCountId = useId();
  const resource = snapshot?.resource;
  const claimKey = resource?.hasIndeterminateClaim
    ? `${resource.dispatchAttemptId ?? ''}:${resource.claimRevision ?? ''}:${snapshot?.etag ?? ''}`
    : '';

  const [reviewedClaimKey, setReviewedClaimKey] = useState(claimKey);
  const [physicalCheck, setPhysicalCheck] = useState(false);
  const [senderIsolation, setSenderIsolation] = useState(false);
  const [isolationDemanded, setIsolationDemanded] = useState(false);
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [pendingSubmission, setPendingSubmission] = useState<PendingSubmission | null>(null);
  const submittingRef = useRef(false);

  // Any change to the reviewed claim (revision bump, new attempt, new ETag)
  // invalidates prior confirmations: the operator must re-confirm.
  if (claimKey !== reviewedClaimKey) {
    setReviewedClaimKey(claimKey);
    setPhysicalCheck(false);
    setSenderIsolation(false);
    setIsolationDemanded(false);
    setPendingSubmission(null);
  }

  const claimOpen = Boolean(
    resource?.hasIndeterminateClaim &&
      resource.dispatchAttemptId &&
      resource.claimRevision != null
  );
  const requiresIsolation = claimOpen && (resource?.senderSettled !== true || isolationDemanded);
  const noteTooLong = note.trim().length > RECOVERY_NOTE_MAX_LENGTH;
  const canSubmit =
    claimOpen &&
    Boolean(snapshot?.etag) &&
    physicalCheck &&
    (!requiresIsolation || senderIsolation) &&
    !noteTooLong &&
    !recover.isPending;

  const handleClose = () => {
    if (recover.isPending) {
      return;
    }
    setPhysicalCheck(false);
    setSenderIsolation(false);
    setIsolationDemanded(false);
    setNote('');
    setError(null);
    setPendingSubmission(null);
    onClose();
  };

  const handleSubmit = async () => {
    // Synchronous re-entry guard: a second click before the pending state
    // re-renders must not start a parallel request with a different key.
    if (submittingRef.current || !canSubmit || !resource || !snapshot?.etag) {
      return;
    }
    submittingRef.current = true;
    try {
      await submit(resource, snapshot.etag);
    } finally {
      submittingRef.current = false;
    }
  };

  const submit = async (resource: DispatchReconciliationSnapshot['resource'], etag: string) => {
    const trimmedNote = note.trim();
    const body: DispatchRecoveryRequest = {
      dispatchAttemptId: resource.dispatchAttemptId as string,
      claimRevision: resource.claimRevision as number,
      physicalCheckConfirmed: true,
      senderIsolationConfirmed: requiresIsolation ? senderIsolation : false,
      note: trimmedNote ? trimmedNote : null,
    };
    const fingerprint = JSON.stringify(body);
    // Reuse the key only to retry the identical submission after a transport
    // failure (no definitive response); anything else gets a fresh key.
    const pending =
      pendingSubmission?.fingerprint === fingerprint
        ? pendingSubmission
        : { key: generateUUID(), fingerprint };
    setPendingSubmission(pending);
    setError(null);

    let result: DispatchRecoveryResult;
    try {
      result = await recover.mutateAsync({
        printerId,
        etag,
        idempotencyKey: pending.key,
        body,
      });
    } catch {
      setError(
        'The recovery request did not complete. The claim may or may not have been recovered — submitting again safely replays the same request.'
      );
      return;
    }

    setPendingSubmission(null);
    if (result.kind === 'recovered') {
      toast.success(`Dispatch recovery recorded for ${printerName}`);
      setPhysicalCheck(false);
      setSenderIsolation(false);
      setNote('');
      onRecovered(result.resource.recoveryAuditId ?? null, body.dispatchAttemptId);
      onClose();
      return;
    }

    if (result.errorCode === 'rejected_stale') {
      setPhysicalCheck(false);
      setSenderIsolation(false);
      onRefresh();
    }
    if (result.errorCode === 'rejected_sender_isolation_required') {
      setIsolationDemanded(true);
      setSenderIsolation(false);
    }
    if (result.errorCode === 'rejected_not_indeterminate') {
      onRefresh();
    }
    setError(rejectionMessage(result));
  };

  const jobLabel = resource?.jobId ?? 'unknown job';
  const attemptLabel = resource?.dispatchAttemptId ?? 'unknown attempt';

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title={`Recover dispatch on ${printerName}`}
      titleIcon={<AlertTriangle className="h-5 w-5 text-pf-warning-text" aria-hidden="true" />}
      size="md"
      isDisabled={recover.isPending}
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="subtle" onClick={handleClose} disabled={recover.isPending}>
            Cancel
          </Button>
          <Button
            variant="danger"
            onClick={() => void handleSubmit()}
            disabled={!canSubmit}
            aria-describedby={error ? `${noteId}-error` : undefined}
          >
            {recover.isPending ? 'Recording…' : 'Record recovery'}
          </Button>
        </div>
      }
    >
      <div className="space-y-4 text-sm text-pf-text-primary">
        <p>
          The printer may have started this print. Recovery records that you physically checked{' '}
          <strong>{printerName}</strong> and confirmed the dispatch did <strong>not</strong> start.
          The job returns to the queue held for an operator; it will not dispatch until
          someone explicitly allows it.
        </p>

        {!claimOpen ? (
          <Alert type="info">This printer no longer has an indeterminate dispatch claim.</Alert>
        ) : (
          <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1">
            <dt className="text-pf-text-secondary">Printer</dt>
            <dd>{printerName}</dd>
            <dt className="text-pf-text-secondary">Job</dt>
            <dd className="font-mono break-all">{jobLabel}</dd>
            <dt className="text-pf-text-secondary">Dispatch attempt</dt>
            <dd className="font-mono break-all">{attemptLabel}</dd>
            <dt className="text-pf-text-secondary">Claim revision</dt>
            <dd>{resource?.claimRevision}</dd>
            <dt className="text-pf-text-secondary">Claim age</dt>
            <dd>{formatClaimAge(resource?.claimAgeSeconds)}</dd>
            <dt className="text-pf-text-secondary">Escalation</dt>
            <dd>{escalationLabel(resource?.escalationLevel)}</dd>
          </dl>
        )}

        {claimOpen && !snapshot?.etag && (
          <Alert type="error">The reviewed claim has no revision tag. Refresh before recovering.</Alert>
        )}

        <fieldset className="space-y-2" disabled={!claimOpen || recover.isPending}>
          <legend className="sr-only">Required confirmations</legend>
          <Checkbox
            id={`${noteId}-physical`}
            checked={physicalCheck}
            onChange={(event) => setPhysicalCheck(event.target.checked)}
            label={`I physically checked ${printerName} and confirmed this dispatch did not start printing.`}
          />
          {requiresIsolation && (
            <Checkbox
              id={`${noteId}-isolation`}
              checked={senderIsolation}
              onChange={(event) => setSenderIsolation(event.target.checked)}
              label="I confirmed the start sender is stopped or isolated from the printer (no evidence it has settled)."
            />
          )}
        </fieldset>

        <div className="space-y-1">
          <label htmlFor={noteId} className="block text-sm font-medium text-pf-text-primary">
            Note (optional)
          </label>
          <Textarea
            id={noteId}
            value={note}
            onChange={(event) => setNote(event.target.value)}
            maxLength={RECOVERY_NOTE_MAX_LENGTH}
            invalid={noteTooLong}
            aria-describedby={noteCountId}
            disabled={!claimOpen || recover.isPending}
          />
          <p id={noteCountId} className="text-xs text-pf-text-secondary">
            {note.length}/{RECOVERY_NOTE_MAX_LENGTH} characters
          </p>
        </div>

        {error && (
          <div id={`${noteId}-error`} role="alert" className="rounded-sm border border-pf-error-border bg-pf-error-bg p-3 text-pf-error-text">
            {error}
          </div>
        )}
      </div>
    </Modal>
  );
}
