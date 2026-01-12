import { Printer } from '@/types/api';
import { AlertIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { Modal } from './Modal';

interface DeleteConfirmationModalProps {
  isOpen: boolean;
  printers: Printer[];
  onConfirm: () => void;
  onCancel: () => void;
}

export function DeleteConfirmationModal({ 
  isOpen, 
  printers, 
  onConfirm, 
  onCancel 
}: DeleteConfirmationModalProps) {
  const isMultiple = printers.length > 1;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onCancel}
      title={`Delete Printer${isMultiple ? 's' : ''}`}
      titleIcon={<AlertIcon className="w-6 h-6 text-pf-error-text" />}
      width="max-w-md"
      footer={
        <div className="flex gap-3 w-full">
          <Button
            variant="secondary"
            onClick={onCancel}
          >
            Cancel
          </Button>
          <Button
            variant="danger"
            onClick={onConfirm}
          >
            Delete {isMultiple ? `${printers.length} Printers` : 'Printer'}
          </Button>
        </div>
      }
    >
      <p className="text-pf-text-secondary mb-4">
        {isMultiple 
          ? `Are you sure you want to delete ${printers.length} printers? This action cannot be undone.`
          : `Are you sure you want to delete "${printers[0]?.name}"? This action cannot be undone.`
        }
      </p>

      {isMultiple && (
        <div className="p-3 bg-pf-bg-2 rounded-lg border border-pf-border">
          <h4 className="text-sm font-medium text-pf-text-primary mb-2">
            Printers to be deleted:
          </h4>
          <ul className="text-sm text-pf-text-secondary space-y-1 max-h-32 overflow-y-auto">
            {printers.map((printer) => (
              <li key={printer.id} className="truncate">
                • {printer.name} ({printer.manufacturerName} {printer.modelName})
              </li>
            ))}
          </ul>
        </div>
      )}
    </Modal>
  );
}
