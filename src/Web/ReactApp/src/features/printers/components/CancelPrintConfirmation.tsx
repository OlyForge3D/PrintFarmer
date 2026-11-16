import { Button } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';

interface CancelPrintConfirmationProps {
  isOpen: boolean;
  printerName: string;
  jobName: string | null;
  onClose: () => void;
  onConfirm: () => void;
}

export function CancelPrintConfirmation({ isOpen, printerName, jobName, onClose, onConfirm }: CancelPrintConfirmationProps) {
  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Cancel print?"
      size="sm"
      closeButtonClassName="min-w-11 min-h-11"
      footer={(
        <div className="flex justify-end gap-2">
          <Button type="button" variant="secondary" className="min-w-11 min-h-11" onClick={onClose}>
            Keep printing
          </Button>
          <Button type="button" variant="danger" className="min-w-11 min-h-11" onClick={onConfirm}>
            Cancel print
          </Button>
        </div>
      )}
    >
      <p className="text-sm text-pf-text-primary">
        Cancel {jobName ? `"${jobName}"` : 'the current print'} on {printerName}?
      </p>
      <p className="mt-2 text-xs text-pf-text-secondary">This action cannot be undone.</p>
    </Modal>
  );
}
