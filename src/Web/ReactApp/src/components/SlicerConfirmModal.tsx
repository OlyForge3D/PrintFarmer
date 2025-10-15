import React from 'react';
import { X, AlertTriangle } from 'lucide-react';

interface SlicerConfirmModalProps {
  isOpen: boolean;
  slicer?: { id: string; name: string } | null;
  onConfirm: () => void;
  onCancel: () => void;
}

export function SlicerConfirmModal({ isOpen, slicer, onConfirm, onCancel }: SlicerConfirmModalProps) {
  if (!isOpen || !slicer) return null;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div className="bg-pf-panel border border-pf-border rounded-xl shadow-xl max-w-md w-full mx-4">
        <div className="flex items-center justify-between p-6 border-b border-pf-border">
          <div className="flex items-center">
            <AlertTriangle className="w-6 h-6 text-pf-error-text mr-3" />
            <h3 className="text-lg font-bold text-pf-text-primary">Deregister Slicer</h3>
          </div>
          <button onClick={onCancel} aria-label="Close" className="text-pf-text-tertiary hover:text-pf-text-primary transition-colors">
            <X className="w-5 h-5" />
          </button>
        </div>
        <div className="p-6">
          <p className="text-pf-text-secondary mb-4">Are you sure you want to deregister the slicer <strong>{slicer.name}</strong>? This will remove it from the registry and the UI will no longer show it.</p>
          <div className="flex justify-end space-x-3">
            <button type="button" onClick={onCancel} className="px-4 py-2 border border-pf-border rounded-md text-pf-text-primary bg-pf-panel hover:bg-pf-bg-2 focus:outline-none focus:ring-2 focus:ring-pf-accent-2 transition-colors">Cancel</button>
            <button type="button" onClick={onConfirm} className="px-4 py-2 border border-transparent rounded-md text-white bg-pf-error-bg hover:bg-red-700 focus:outline-none focus:ring-2 focus:ring-red-500 transition-colors">Deregister</button>
          </div>
        </div>
      </div>
    </div>
  );
}

export default SlicerConfirmModal;
