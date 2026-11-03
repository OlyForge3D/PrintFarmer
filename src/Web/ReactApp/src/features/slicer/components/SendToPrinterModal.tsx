import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Select, FormField, Checkbox, NumberStepper } from '@/common/components/ui';
import { PrinterIcon } from '@/common/components/icons/MdiIcons';
import { usePrintersFast } from '@/common/hooks/useApi';
import { sortPrintersByAvailability } from '@/utils/printerSort';
import { sliceJobService } from '@/services/sliceJobService';
import type { SendToPrinterResponse, AddSliceToQueueResponse } from '@/services/sliceJobService';
import { queueSummariesFleetQueryKey } from '@/features/printers/hooks/useQueueSummariesFleet';

type SendMode = 'queue' | 'direct';

const PRIORITY_OPTIONS: { value: string; label: string }[] = [
  { value: 'Low', label: 'Low' },
  { value: 'Normal', label: 'Normal' },
  { value: 'High', label: 'High' },
];

interface SendToPrinterModalProps {
  isOpen: boolean;
  onClose: () => void;
  jobId: string;
  artifactId: string;
  defaultPrinterId?: string | null;
  selectedSpoolId?: number | null;
  requiredPrinterModel?: string;
  requiredMaterialType?: string;
  requiredNozzleDiameter?: number;
}

export function SendToPrinterModal({ isOpen, onClose, jobId, artifactId, defaultPrinterId, selectedSpoolId, requiredPrinterModel, requiredMaterialType, requiredNozzleDiameter }: SendToPrinterModalProps) {
  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Print"
      titleIcon={<PrinterIcon className="w-5 h-5" />}
      size="sm"
    >
      {isOpen && (
        <SendToPrinterForm
          key={`${jobId}:${artifactId}`}
          jobId={jobId}
          artifactId={artifactId}
          defaultPrinterId={defaultPrinterId}
          onClose={onClose}
          selectedSpoolId={selectedSpoolId}
          requiredPrinterModel={requiredPrinterModel}
          requiredMaterialType={requiredMaterialType}
          requiredNozzleDiameter={requiredNozzleDiameter}
        />
      )}
    </Modal>
  );
}

interface SendToPrinterFormProps {
  jobId: string;
  artifactId: string;
  defaultPrinterId?: string | null;
  onClose: () => void;
  selectedSpoolId?: number | null;
  requiredPrinterModel?: string;
  requiredMaterialType?: string;
  requiredNozzleDiameter?: number;
}

function SendToPrinterForm({ jobId, artifactId, defaultPrinterId, onClose, selectedSpoolId, requiredPrinterModel, requiredMaterialType, requiredNozzleDiameter }: SendToPrinterFormProps) {
  const [sendMode, setSendMode] = useState<SendMode>('queue');
  const [selectedPrinterId, setSelectedPrinterId] = useState(defaultPrinterId ?? '');
  const { data: printers = [], isLoading, isError } = usePrintersFast();
  const onlinePrinters = sortPrintersByAvailability(printers.filter(p => p.isOnline));
  const selectedPrinter = printers.find(p => p.id === selectedPrinterId);
  const unavailable = !!selectedPrinterId && !onlinePrinters.some(p => p.id === selectedPrinterId);
  const canSubmit = !isLoading && !isError && !unavailable && (sendMode === 'queue' || !!selectedPrinterId);

  return (
    <div className="space-y-4">
      {/* Segmented mode chooser */}
      <div className="flex rounded-md border border-pf-border overflow-hidden">
        {(['queue', 'direct'] as const).map((mode) => {
          const active = sendMode === mode;
          return (
            <Button
              key={mode}
              type="button"
              variant="unstyled"
              role="radio"
              aria-checked={active}
              onClick={() => setSendMode(mode)}
              className={`flex-1 px-3 py-2 text-sm font-semibold transition-colors rounded-none ${
                active
                  ? 'bg-pf-accent text-pf-bg-1'
                  : 'text-pf-text-secondary hover:text-pf-text-primary bg-pf-bg-0'
              }`}
            >
              {mode === 'direct' ? 'Upload file only' : 'Add to Queue'}
            </Button>
          );
        })}
      </div>

      <FormField label="Printer" htmlFor="send-printer-select" required={sendMode === 'direct'}>
        <Select
          id="send-printer-select"
          value={selectedPrinterId}
          onChange={(e) => setSelectedPrinterId(e.target.value)}
          aria-label="Select printer"
        >
          <option value="">{sendMode === 'queue' ? 'Any compatible printer (auto-assign)' : 'Select a printer…'}</option>
          {unavailable && (
            <option value={selectedPrinterId} disabled>
              {isLoading ? 'Loading selected printer…' : `${selectedPrinter?.name ?? 'Selected printer'} (${selectedPrinter ? 'offline' : 'unavailable'})`}
            </option>
          )}
          {onlinePrinters.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
        </Select>
      </FormField>
      {isError && <p role="alert" className="text-sm text-pf-text-secondary">Unable to load printers. Try again before submitting.</p>}
      {!isLoading && !isError && unavailable && (
        <p role="alert" className="text-sm text-pf-text-secondary">
          The selected printer is offline or unavailable. Choose another printer{sendMode === 'queue' ? ' or explicitly choose auto-assign' : ''}.
        </p>
      )}
      {sendMode === 'direct' ? (
        <DirectSendForm jobId={jobId} artifactId={artifactId} onClose={onClose} selectedPrinterId={selectedPrinterId} printerName={selectedPrinter?.name} canSubmit={canSubmit} />
      ) : (
        <QueueForm
          jobId={jobId}
          artifactId={artifactId}
          onClose={onClose}
          selectedPrinterId={selectedPrinterId}
          canSubmit={canSubmit}
          selectedSpoolId={selectedSpoolId}
          requiredPrinterModel={requiredPrinterModel}
          requiredMaterialType={requiredMaterialType}
          requiredNozzleDiameter={requiredNozzleDiameter}
        />
      )}
    </div>
  );
}

function DirectSendForm({ jobId, artifactId, onClose, selectedPrinterId, printerName, canSubmit }: { jobId: string; artifactId: string; onClose: () => void; selectedPrinterId: string; printerName?: string; canSubmit: boolean }) {
  const [startPrint, setStartPrint] = useState(false);

  const sendMutation = useMutation({
    mutationFn: () =>
      sliceJobService.sendToPrinter(jobId, artifactId, selectedPrinterId, startPrint),
    onSuccess: (data: SendToPrinterResponse) => {
      toast.success(`Sent ${data.fileName} to ${printerName ?? 'printer'}`);
      onClose();
    },
    onError: (err: Error) => {
      toast.error(`Failed to send to printer: ${err.message}`);
    },
  });

  return (
    <>
      <p className="text-sm text-pf-text-secondary">
        Uploads the file to the printer without creating a PrintFarmer job.
      </p>

      <Checkbox
        label="Start printing immediately"
        checked={startPrint}
        onChange={(e) => setStartPrint(e.target.checked)}
      />

      <div className="flex items-center justify-end gap-2 mt-6">
        <Button variant="secondary" onClick={onClose} disabled={sendMutation.isPending}>
          Cancel
        </Button>
        <Button
          variant="primary"
          onClick={() => { if (canSubmit) sendMutation.mutate(); }}
          loading={sendMutation.isPending}
          disabled={!canSubmit || sendMutation.isPending}
          iconLeft={<PrinterIcon className="w-4 h-4" />}
        >
          Upload file only
        </Button>
      </div>
    </>
  );
}

interface QueueFormProps {
  jobId: string;
  artifactId: string;
  onClose: () => void;
  selectedPrinterId: string;
  canSubmit: boolean;
  selectedSpoolId?: number | null;
  requiredPrinterModel?: string;
  requiredMaterialType?: string;
  requiredNozzleDiameter?: number;
}

function QueueForm({ jobId, artifactId, onClose, selectedPrinterId, canSubmit, selectedSpoolId, requiredPrinterModel, requiredMaterialType, requiredNozzleDiameter }: QueueFormProps) {
  const queryClient = useQueryClient();
  const [priority, setPriority] = useState('Normal');
  const [copies, setCopies] = useState(1);

  const queueMutation = useMutation({
    mutationFn: () =>
      sliceJobService.addSliceToQueue(jobId, {
        artifactId,
        assignedPrinterId: selectedPrinterId || null,
        priority,
        copies,
        spoolId: selectedSpoolId ?? undefined,
        requiredPrinterModel: requiredPrinterModel || undefined,
        requiredMaterialType: requiredMaterialType || undefined,
        requiredNozzleDiameter: requiredNozzleDiameter,
      }),
    onSuccess: (data: AddSliceToQueueResponse) => {
      void Promise.all([
        queryClient.invalidateQueries({ queryKey: ['job-queue'] }),
        queryClient.invalidateQueries({ queryKey: ['queue-jobs'] }),
        queryClient.invalidateQueries({ queryKey: ['queue-stats'] }),
        queryClient.invalidateQueries({ queryKey: queueSummariesFleetQueryKey }),
      ]);
      const positionText = data.queuePosition != null ? ` — position ${data.queuePosition}` : '';
      toast.success(`Queued${positionText}`);
      onClose();
    },
    onError: (err: Error) => {
      toast.error(`Failed to add to queue: ${err.message}`);
    },
  });

  const hasRequirements = requiredPrinterModel || requiredMaterialType || requiredNozzleDiameter != null;

  return (
    <>
      <FormField label="Priority" htmlFor="queue-priority-select">
        <Select
          id="queue-priority-select"
          value={priority}
          onChange={(e) => setPriority(e.target.value)}
          aria-label="Queue priority"
        >
          {PRIORITY_OPTIONS.map(opt => (
            <option key={opt.value} value={opt.value}>{opt.label}</option>
          ))}
        </Select>
      </FormField>

      <FormField label="Copies" htmlFor="queue-copies">
        <NumberStepper
          id="queue-copies"
          value={copies}
          onChange={setCopies}
          min={1}
          max={99}
          aria-label="Number of copies"
        />
      </FormField>

      {hasRequirements && (
        <div className="space-y-1">
          <p className="text-xs font-medium text-pf-text-secondary">Printer requirements</p>
          <div className="flex flex-wrap gap-1.5">
            {requiredPrinterModel && (
              <span className="inline-flex items-center px-2 py-0.5 rounded-xs bg-pf-accent-bg/15 text-xs text-pf-accent font-medium">
                🖨 {requiredPrinterModel}
              </span>
            )}
            {requiredMaterialType && (
              <span className="inline-flex items-center px-2 py-0.5 rounded-xs bg-pf-accent-bg/15 text-xs text-pf-accent font-medium">
                🧵 {requiredMaterialType}
              </span>
            )}
            {requiredNozzleDiameter != null && (
              <span className="inline-flex items-center px-2 py-0.5 rounded-xs bg-pf-accent-bg/15 text-xs text-pf-accent font-medium">
                ⌀ {requiredNozzleDiameter}mm nozzle
              </span>
            )}
          </div>
          <p className="text-xs text-pf-text-muted">{selectedPrinterId ? 'The job will be queued for the selected printer.' : 'A compatible printer will be auto-assigned.'}</p>
        </div>
      )}

      {!hasRequirements && (
        <p className="text-xs text-pf-text-muted">{selectedPrinterId ? 'The job will be queued for the selected printer.' : 'A compatible printer will be auto-assigned from the queue.'}</p>
      )}

      <div className="flex items-center justify-end gap-2 mt-6">
        <Button variant="secondary" onClick={onClose} disabled={queueMutation.isPending}>
          Cancel
        </Button>
        <Button
          variant="primary"
          onClick={() => { if (canSubmit) queueMutation.mutate(); }}
          loading={queueMutation.isPending}
          disabled={!canSubmit || queueMutation.isPending}
        >
          Add to Queue
        </Button>
      </div>
    </>
  );
}
