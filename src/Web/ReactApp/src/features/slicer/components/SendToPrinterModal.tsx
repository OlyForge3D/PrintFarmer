import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Select } from '@/common/components/ui/Select';
import { FormField } from '@/common/components/ui/FormField';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { PrinterIcon } from '@/common/components/icons/MdiIcons';
import { usePrintersFast } from '@/common/hooks/useApi';
import { sliceJobService } from '@/services/sliceJobService';
import type { SendToPrinterResponse } from '@/services/sliceJobService';

interface SendToPrinterModalProps {
  isOpen: boolean;
  onClose: () => void;
  jobId: string;
}

export function SendToPrinterModal({ isOpen, onClose, jobId }: SendToPrinterModalProps) {
  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Send to Printer"
      titleIcon={<PrinterIcon className="w-5 h-5" />}
      size="sm"
    >
      {isOpen && (
        <SendToPrinterForm jobId={jobId} onClose={onClose} />
      )}
    </Modal>
  );
}

function SendToPrinterForm({ jobId, onClose }: { jobId: string; onClose: () => void }) {
  const [selectedPrinterId, setSelectedPrinterId] = useState('');
  const [startPrint, setStartPrint] = useState(false);

  const { data: printers = [] } = usePrintersFast();
  const onlinePrinters = printers.filter(p => p.isOnline);

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
      <div className="space-y-4">
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
      </div>

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
