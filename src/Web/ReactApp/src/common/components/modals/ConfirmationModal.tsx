import { AlertIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { Modal } from './Modal';

export interface ConfirmationModalProps {
  isOpen: boolean;
  title: string;
  message: string;
  confirmButtonText?: string;
  cancelButtonText?: string;
  isDangerous?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
  children?: React.ReactNode;
}

/**
 * Generic confirmation modal for common confirmation scenarios
 */
export function ConfirmationModal({
  isOpen,
  title,
  message,
  confirmButtonText = 'Confirm',
  cancelButtonText = 'Cancel',
  isDangerous = false,
  onConfirm,
  onCancel,
  children,
}: ConfirmationModalProps) {
  return (
    <Modal
      isOpen={isOpen}
      onClose={onCancel}
      title={title}
      titleIcon={isDangerous ? <AlertIcon className="w-6 h-6 text-pf-error-text" /> : undefined}
      width="max-w-md"
      footer={
        <div className="flex gap-3 w-full">
          <Button
            variant="secondary"
            onClick={onCancel}
          >
            {cancelButtonText}
          </Button>
          <Button
            variant={isDangerous ? 'danger' : 'primary'}
            onClick={onConfirm}
          >
            {confirmButtonText}
          </Button>
        </div>
      }
    >
      <p className="text-pf-text-secondary mb-4">
        {message}
      </p>
      {children}
    </Modal>
  );
}
