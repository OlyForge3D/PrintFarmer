import { Printer } from '@/types/api';
import { AlertTriangle, X } from 'lucide-react';

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
  if (!isOpen) return null;

  const isMultiple = printers.length > 1;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div className="bg-pf-panel border border-pf-border rounded-xl shadow-xl max-w-md w-full mx-4">
        <div className="flex items-center justify-between p-6 border-b border-pf-border">
          <div className="flex items-center">
            <AlertTriangle className="w-6 h-6 text-pf-error-text mr-3" />
            <h3 className="text-lg font-bold text-pf-text-primary">
              Delete Printer{isMultiple ? 's' : ''}
            </h3>
          </div>
          <button
            onClick={onCancel}
            className="text-pf-text-tertiary hover:text-pf-text-primary transition-colors"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        <div className="p-6">
          <p className="text-pf-text-secondary mb-4">
            {isMultiple 
              ? `Are you sure you want to delete ${printers.length} printers? This action cannot be undone.`
              : `Are you sure you want to delete "${printers[0]?.name}"? This action cannot be undone.`
            }
          </p>

          {isMultiple && (
            <div className="mb-4 p-3 bg-pf-bg-2 rounded-lg border border-pf-border">
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

          <div className="flex justify-end space-x-3">
            <button
              onClick={onCancel}
              className="px-4 py-2 border border-pf-border rounded-md text-pf-text-primary bg-pf-panel hover:bg-pf-bg-2 focus:outline-none focus:ring-2 focus:ring-pf-accent-2 transition-colors"
            >
              Cancel
            </button>
            <button
              onClick={onConfirm}
              className="px-4 py-2 border border-transparent rounded-md text-white bg-pf-error-bg hover:bg-red-700 focus:outline-none focus:ring-2 focus:ring-red-500 transition-colors"
            >
              Delete {isMultiple ? `${printers.length} Printers` : 'Printer'}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
