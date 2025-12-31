import React from 'react';
import { AlertIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';

interface SlicerConfirmModalProps {
  isOpen: boolean;
  slicer?: { id: string; name: string } | null;
  onConfirm: () => void;
  onCancel: () => void;
}

export function SlicerConfirmModal({ isOpen, slicer, onConfirm, onCancel }: SlicerConfirmModalProps) {
  if (!isOpen || !slicer) return null;

  return (
    <div 
      className="fixed inset-0 bg-black/50 backdrop-blur-sm flex items-center justify-center z-50"
      onClick={(e) => {
        if (e.target === e.currentTarget) {
          onCancel();
        }
      }}
      onKeyDown={(e) => {
        if (e.key === 'Escape') {
          onCancel();
        }
      }}
    >
      <div className="bg-pf-bg-0 border border-pf-border rounded-xl shadow-xl max-w-md w-full mx-4">
        <div className="flex items-center justify-between p-6 border-b border-pf-border">
          <div className="flex items-center">
            <AlertIcon className="w-6 h-6 text-pf-error-text mr-3" />
            <h3 className="text-lg font-bold text-pf-text-primary">Deregister Slicer</h3>
          </div>
          <Button
            type="button"
            onClick={onCancel}
            aria-label="Close"
            variant="subtle"
            size="sm"
            className="!p-0 !h-auto"
          >
            <CloseIcon className="w-5 h-5" />
          </Button>
        </div>
        <div className="p-6">
          <p className="text-pf-text-secondary mb-4">Are you sure you want to deregister the slicer <strong>{slicer.name}</strong>? This will remove it from the registry and the UI will no longer show it.</p>
          <div className="flex justify-end space-x-3">
            <Button
              type="button"
              onClick={onCancel}
              variant="secondary"
            >
              Cancel
            </Button>
            <Button
              type="button"
              onClick={onConfirm}
              variant="danger"
            >
              Deregister
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}

export default SlicerConfirmModal;
