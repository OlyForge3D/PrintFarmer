import React from 'react';
import { Modal } from '@/common/components/ui/Modal';
import { Button } from '@/common/components/ui/Button';

interface ConfirmationDialogProps {
  /** Whether the dialog is open */
  isOpen: boolean;
  /** Callback when the dialog should close */
  onClose: () => void;
  /** Dialog title */
  title: string;
  /** Dialog message/content */
  message: string;
  /** Text for the confirm button (default: "Delete") */
  confirmButtonText?: string;
  /** Text for the cancel button (default: "Cancel") */
  cancelButtonText?: string;
  /** Callback when user confirms */
  onConfirm: () => void | Promise<void>;
  /** Whether the action is destructive (red button) */
  isDestructive?: boolean;
  /** Whether the confirm button is loading */
  isLoading?: boolean;
}

export const ConfirmationDialog: React.FC<ConfirmationDialogProps> = ({
  isOpen,
  onClose,
  title,
  message,
  confirmButtonText = 'Delete',
  cancelButtonText = 'Cancel',
  onConfirm,
  isDestructive = true,
  isLoading = false,
}) => {
  const handleConfirm = async () => {
    await onConfirm();
    onClose();
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={title}
      size="sm"
      closeOnBackdrop={!isLoading}
      closeOnEscape={!isLoading}
      footer={
        <div className="flex justify-end space-x-3">
          <Button
            type="button"
            variant="secondary"
            onClick={onClose}
            disabled={isLoading}
          >
            {cancelButtonText}
          </Button>
          <Button
            type="button"
            variant={isDestructive ? 'danger' : 'primary'}
            onClick={handleConfirm}
            disabled={isLoading}
            loading={isLoading}
          >
            {confirmButtonText}
          </Button>
        </div>
      }
    >
      <p className="text-sm text-gray-700 dark:text-gray-300">{message}</p>
    </Modal>
  );
};
