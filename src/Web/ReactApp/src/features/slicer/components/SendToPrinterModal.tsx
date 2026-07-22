import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Select, FormField, Checkbox, NumberStepper } from '@/common/components/ui';
import { PrinterIcon } from '@/common/components/icons/MdiIcons';
import { usePrintersFast } from '@/common/hooks/useApi';
import { sortPrintersByAvailability } from '@/utils/printerSort';
import { sliceJobService } from '@/services/sliceJobService';
import type { SendToPrinterResponse, AddSliceToQueueResponse } from '@/services/sliceJobService';

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
  selectedSpoolId?: number | null;
  requiredPrinterModel?: string;
  requiredMaterialType?: string;
  requiredNozzleDiameter?: number;
}

export function SendToPrinterModal({ isOpen, onClose, jobId, selectedSpoolId, requiredPrinterModel, requiredMaterialType, requiredNozzleDiameter }: SendToPrinterModalProps) {
  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Send to Printer"
      titleIcon={<PrinterIcon className="w-5 h-5" />}
      size="sm"
    >
      {isOpen && (
        <SendToPrinterForm
          jobId={jobId}
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
  onClose: () => void;
  selectedSpoolId?: number | null;
  requiredPrinterModel?: string;
  requiredMaterialType?: string;
  requiredNozzleDiameter?: number;
}

function SendToPrinterForm({ jobId, onClose, selectedSpoolId, requiredPrinterModel, requiredMaterialType, requiredNozzleDiameter }: SendToPrinterFormProps) {
  const [sendMode, setSendMode] = useState<SendMode>('direct');

  return (
    <div className="space-y-4">
      {/* Segmented mode chooser */}
      <div className="flex rounded-md border border-pf-border overflow-hidden">
        {(['direct', 'queue'] as const).map((mode) => {
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
              {mode === 'direct' ? 'Send to Printer' : 'Add to Queue'}
            </Button>
          );
        })}
      </div>

      {sendMode === 'direct' ? (
        <DirectSendForm jobId={jobId} onClose={onClose} />
      ) : (
        <QueueForm
          jobId={jobId}
          onClose={onClose}
          selectedSpoolId={selectedSpoolId}
          requiredPrinterModel={requiredPrinterModel}
          requiredMaterialType={requiredMaterialType}
          requiredNozzleDiameter={requiredNozzleDiameter}
        />
      )}
    </div>
  );
}

function DirectSendForm({ jobId, onClose }: { jobId: string; onClose: () => void }) {
  const [selectedPrinterId, setSelectedPrinterId] = useState('');
  const [startPrint, setStartPrint] = useState(false);

  const { data: printers = [] } = usePrintersFast();
  const onlinePrinters = sortPrintersByAvailability(printers.filter(p => p.isOnline));

  const sendMutation = useMutation({
    mutationFn: () =>
      sliceJobService.sendToPrinter(jobId, selectedPrinterId, startPrint),
    onSuccess: (data: SendToPrinterResponse) => {
      const printerName = onlinePrinters.find(p => p.id === selectedPrinterId)?.name ?? 'printer';
      toast.success(`Sent ${data.fileName} to ${printerName}`);
      onClose();
    },
    onError: (err: Error) => {
      toast.error(`Failed to send to printer: ${err.message}`);
    },
  });

  return (
    <>
      <FormField label="Printer" htmlFor="send-printer-select" required>
        <Select
          id="send-printer-select"
          value={selectedPrinterId}
          onChange={(e) => setSelectedPrinterId(e.target.value)}
          aria-label="Select printer"
        >
          <option value="">Select a printer…</option>
          {onlinePrinters.map(p => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </Select>
      </FormField>

      {onlinePrinters.length === 0 && (
        <p className="text-sm text-pf-text-secondary">
          No online printers available. Ensure at least one printer is connected.
        </p>
      )}

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
          onClick={() => sendMutation.mutate()}
          loading={sendMutation.isPending}
          disabled={!selectedPrinterId || sendMutation.isPending}
          iconLeft={<PrinterIcon className="w-4 h-4" />}
        >
          Send to Printer
        </Button>
      </div>
    </>
  );
}

interface QueueFormProps {
  jobId: string;
  onClose: () => void;
  selectedSpoolId?: number | null;
  requiredPrinterModel?: string;
  requiredMaterialType?: string;
  requiredNozzleDiameter?: number;
}

function QueueForm({ jobId, onClose, selectedSpoolId, requiredPrinterModel, requiredMaterialType, requiredNozzleDiameter }: QueueFormProps) {
  const [priority, setPriority] = useState('Normal');
  const [copies, setCopies] = useState(1);

  const queueMutation = useMutation({
    mutationFn: () =>
      sliceJobService.addSliceToQueue(jobId, {
        priority,
        copies,
        spoolId: selectedSpoolId ?? undefined,
        requiredPrinterModel: requiredPrinterModel || undefined,
        requiredMaterialType: requiredMaterialType || undefined,
        requiredNozzleDiameter: requiredNozzleDiameter,
      }),
    onSuccess: (data: AddSliceToQueueResponse) => {
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
          <p className="text-xs font-medium text-pf-text-secondary">Auto-assigned requirements</p>
          <div className="flex flex-wrap gap-1.5">
            {requiredPrinterModel && (
              <span className="inline-flex items-center px-2 py-0.5 rounded-full bg-pf-accent-bg/15 text-xs text-pf-accent font-medium">
                🖨 {requiredPrinterModel}
              </span>
            )}
            {requiredMaterialType && (
              <span className="inline-flex items-center px-2 py-0.5 rounded-full bg-pf-accent-bg/15 text-xs text-pf-accent font-medium">
                🧵 {requiredMaterialType}
              </span>
            )}
            {requiredNozzleDiameter != null && (
              <span className="inline-flex items-center px-2 py-0.5 rounded-full bg-pf-accent-bg/15 text-xs text-pf-accent font-medium">
                ⌀ {requiredNozzleDiameter}mm nozzle
              </span>
            )}
          </div>
          <p className="text-xs text-pf-text-muted">A compatible printer will be auto-assigned.</p>
        </div>
      )}

      {!hasRequirements && (
        <p className="text-xs text-pf-text-muted">A compatible printer will be auto-assigned from the queue.</p>
      )}

      <div className="flex items-center justify-end gap-2 mt-6">
        <Button variant="secondary" onClick={onClose} disabled={queueMutation.isPending}>
          Cancel
        </Button>
        <Button
          variant="primary"
          onClick={() => queueMutation.mutate()}
          loading={queueMutation.isPending}
          disabled={queueMutation.isPending}
        >
          Add to Queue
        </Button>
      </div>
    </>
  );
}
