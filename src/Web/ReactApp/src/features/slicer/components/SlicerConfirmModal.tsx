import React from 'react';
import { Button } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';

interface SlicerConfirmModalProps {
  isOpen: boolean;
  slicer: { id: string; name: string } | null;
  onConfirm: () => void;
  onCancel: () => void;
}

export function SlicerConfirmModal({
  isOpen,
  slicer,
  onConfirm,
  onCancel,
}: SlicerConfirmModalProps) {
  if (!isOpen || !slicer) return null;

  const modalFooter = (
    <div className="flex gap-3">
      <Button
        variant="outline"
        onClick={onCancel}
      >
        Cancel
      </Button>
      <Button
        variant="danger"
        onClick={onConfirm}
      >
        Deregister
      </Button>
    </div>
  );

  return (
    <Modal
      isOpen={isOpen}
      onClose={onCancel}
      title="Deregister Slicer?"
      width="max-w-sm"
      footer={modalFooter}
    >
      <p className="text-pf-text-secondary">
        Are you sure you want to deregister <strong>{slicer.name}</strong>? This action cannot be undone.
      </p>
    </Modal>
  );
}
