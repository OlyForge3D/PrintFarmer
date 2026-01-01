import React from 'react';
import { Button } from '@/common/components/ui';

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

  return (
    <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50">
      <div className="bg-pf-bg-1 border border-pf-border rounded-lg shadow-lg p-6 max-w-sm">
        <h2 className="text-lg font-semibold text-pf-text-primary mb-4">
          Deregister Slicer?
        </h2>
        <p className="text-pf-text-secondary mb-6">
          Are you sure you want to deregister <strong>{slicer.name}</strong>? This action cannot be undone.
        </p>
        <div className="flex gap-3 justify-end">
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
      </div>
    </div>
  );
}
