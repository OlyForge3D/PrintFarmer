import { useState } from 'react';
import { useSearchParams } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Alert, Button, Checkbox } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { apiClient } from '@/services/api';
import type { PrinterFileCatalogEntry } from '@/types/api';

export interface PrinterFileDeleteIntent {
  entry: PrinterFileCatalogEntry;
  key: string;
}

interface Props {
  intent: PrinterFileDeleteIntent | null;
  onClose: () => void;
  canDelete: boolean;
  printers: Array<{ id: string; name: string }>;
}

export function PrinterFileDeleteControl({ intent, onClose, canDelete, printers }: Props) {
  const [params, setParams] = useSearchParams();
  const id = params.get('deleteOperation') ?? '';
  const queryClient = useQueryClient();
  const [irreversibleBinding, setIrreversibleBinding] = useState('');
  const [raceBinding, setRaceBinding] = useState('');
  const [uncertainConfirmation, setUncertainConfirmation] = useState('');
  const progress = useQuery({
    queryKey: ['printer-file-delete', id],
    queryFn: ({ signal }) => apiClient.getPrinterFileDeleteOperation(id, signal),
    enabled: canDelete && !!id,
    retry: false,
  });
  const operation = progress.data;
  const invalidate = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['printer-file-delete', id] }),
      queryClient.invalidateQueries({ queryKey: ['printer-file-catalog'] }),
    ]);
  };
  const prepare = useMutation({
    mutationFn: () => {
      if (!intent) throw new Error('Select an exact source observation.');
      return apiClient.prepareIndexedPrinterFileDelete(intent.entry.id, intent.entry.observationRevision, intent.key);
    },
    onSuccess: async result => {
      setIrreversibleBinding('');
      setRaceBinding('');
      setUncertainConfirmation('');
      setParams(previous => {
        const next = new URLSearchParams(previous);
        next.set('deleteOperation', result.id);
        return next;
      });
      onClose();
      toast.info('Delete intent prepared. Nothing is deleted until separately confirmed.');
      await queryClient.invalidateQueries({ queryKey: ['printer-file-delete', result.id] });
    },
    onError: (error: Error) => toast.error(`Failed to prepare delete: ${error.message}`),
  });
  const confirm = useMutation({
    mutationFn: () => {
      if (!operation || operation.state !== 'AwaitingDeleteConfirmation' ||
        irreversibleBinding !== operation.confirmationBinding ||
        (operation.guarantee === 'CurrentExactPath' && raceBinding !== operation.confirmationBinding)) {
        throw new Error('Review and accept the exact delete warnings first.');
      }
      return apiClient.confirmPrinterFileDelete(operation.id, {
        confirmationBinding: operation.confirmationBinding,
        acceptIrreversibleDelete: true,
        acceptCurrentPathRace: operation.guarantee === 'CurrentExactPath',
      });
    },
    onSuccess: async result => {
      setIrreversibleBinding('');
      setRaceBinding('');
      if (result.state === 'Deleted' && result.outcome === 'Acknowledged') {
        toast.success('The printer acknowledged deletion of this exact source location.');
      } else {
        toast.info('Delete is not confirmed successful. Review its recorded outcome.');
      }
      await invalidate();
    },
    onError: async (error: Error) => {
      setUncertainConfirmation(id);
      setIrreversibleBinding('');
      setRaceBinding('');
      toast.error(`Delete confirmation response unavailable: ${error.message}. Check status; do not resend.`);
      await invalidate();
    },
  });
  const reconcile = useMutation({
    mutationFn: () => apiClient.reconcilePrinterFileDelete(id),
    onSuccess: invalidate,
    onError: (error: Error) => toast.error(`Failed to reconcile delete: ${error.message}`),
  });
  if (!canDelete) return null;

  const binding = operation?.confirmationBinding ?? '';
  const waiting = operation?.state === 'AwaitingDeleteConfirmation' && uncertainConfirmation !== id;
  const unknown = operation?.outcome === 'OutcomeUnknown' || operation?.state === 'SendingDelete' ||
    operation?.state === 'ReconcilingDelete' || uncertainConfirmation === id;
  return (
    <>
      <Modal isOpen={!!intent} onClose={() => { if (!prepare.isPending) onClose(); }}
        title="Prepare remote delete" footer={<>
          <Button variant="secondary" disabled={prepare.isPending} onClick={onClose}>Keep file</Button>
          <Button variant="danger" loading={prepare.isPending} onClick={() => prepare.mutate()}>
            {prepare.isError ? 'Recover same delete intent' : 'Review exact delete'}
          </Button>
        </>}>
        {intent && <div className="space-y-3">
          <strong>{intent.entry.printerName}</strong>
          <p className="break-all font-mono">{intent.entry.storageRoot}/{intent.entry.relativePath}</p>
          <p>Observation {intent.entry.observationRevision}. This action targets only this printer and exact path.</p>
          <Alert variant="warning">Remote deletion is irreversible. No backup is created automatically. Local library copies, other source locations, provenance and accepted jobs are retained.</Alert>
          {prepare.error && <Alert variant="error">{prepare.error.message}. Keep this intent key when recovering a lost response; do not create a replacement delete.</Alert>}
        </div>}
      </Modal>
      {progress.error && <Alert variant="error">Failed to read delete status: {progress.error.message}. Do not resend an uncertain delete.</Alert>}
      {operation && <section aria-labelledby="printer-delete-heading" className="space-y-3 rounded-sm border border-pf-border bg-pf-bg-1 p-4">
        <h3 id="printer-delete-heading" className="font-semibold">Remote delete - exact source only</h3>
        <p>{printers.find(printer => printer.id === operation.sourcePrinterId)?.name ?? operation.sourcePrinterId}</p>
        <p className="break-all font-mono">{operation.storageRoot}/{operation.relativePath}</p>
        <p>Observation {operation.observationRevision} - configuration {operation.configurationGeneration}</p>
        <p className="break-all text-xs">Operation: {operation.id}</p>
        <p role="status" aria-live="polite">{operation.state} - {operation.outcome}</p>
        {operation.reasonCode && <p>{operation.reasonCode.replaceAll('_', ' ')}</p>}
        {waiting && <div className="space-y-3">
          <Alert variant="warning">Deleting this source is irreversible and does not create a backup. Copy and Queue confirmations are not Delete consent. The server still requires current permission, Manage access and confirmed idle immediately before sending.</Alert>
          <Checkbox label="I accept irreversible deletion of this exact source without an automatic backup"
            checked={irreversibleBinding === binding} onChange={event => setIrreversibleBinding(event.target.checked ? binding : '')} />
          {operation.guarantee === 'CurrentExactPath' && <>
            <Alert variant="warning">This backend cannot atomically match a file version. Another client could replace the file after the last check; Delete would remove the file currently at this exact path.</Alert>
            <Checkbox label="I accept the current-path replacement race for this exact delete"
              checked={raceBinding === binding} onChange={event => setRaceBinding(event.target.checked ? binding : '')} />
          </>}
          {operation.guarantee === 'NativeConditional' && <p>The backend supports a certified native version-conditional delete.</p>}
          <Button variant="danger" loading={confirm.isPending}
            disabled={irreversibleBinding !== binding || (operation.guarantee === 'CurrentExactPath' && raceBinding !== binding)}
            onClick={() => confirm.mutate()}>Confirm irreversible delete</Button>
        </div>}
        {unknown && <Alert variant="warning" title="Outcome unknown - do not resend">
          The request may have reached the printer. Read-only reconciliation cannot prove historical deletion success or release its physical I/O quarantine, even if the path is absent now. Contact an administrator with this operation ID. Do not bypass quarantine by re-registering the printer or creating another intent.
        </Alert>}
        {operation.state === 'Deleted' && operation.outcome === 'Acknowledged' &&
          <Alert variant="success">The printer acknowledged this source deletion. Local assets and queued jobs remain independent.</Alert>}
        <div className="flex flex-wrap gap-2">
          <Button variant="secondary" disabled={progress.isFetching || confirm.isPending} onClick={() => { void progress.refetch(); }}>Read delete status</Button>
          {unknown && <Button variant="secondary" loading={reconcile.isPending} onClick={() => reconcile.mutate()}>Read-only reconcile</Button>}
        </div>
      </section>}
    </>
  );
}
