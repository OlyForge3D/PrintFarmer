import React, { useEffect, useState } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui';
import { printerHubService, PrinterImportProgress } from '@/services/printerHubService';

// Using PrinterImportProgress from printerHubService which has status: 'Pending' | 'Imported' | 'Skipped' | 'Failed'
type ImportProgressItem = PrinterImportProgress;

interface ImportProgressModalProps {
  isOpen: boolean;
  onClose: () => void;
  fileName: string;
  totalCount: number;
}

const ImportProgressModal: React.FC<ImportProgressModalProps> = ({
  isOpen,
  onClose,
  fileName,
  totalCount
}) => {
  const [items, setItems] = useState<ImportProgressItem[]>([]);
  const [isComplete, setIsComplete] = useState(false);

  useEffect(() => {
    if (!isOpen) {
      // Reset state when modal closes
      setItems([]);
      setIsComplete(false);
      return;
    }

    // Initialize all items as Pending
    const initialItems: ImportProgressItem[] = Array.from({ length: totalCount }, (_, i) => ({
      index: i,
      name: `Printer ${i + 1}`,
      status: 'Pending'
    }));
    setItems(initialItems);

    // Ensure SignalR connection is started before subscribing
    const setupSignalR = async () => {
      try {
        if (!printerHubService.isConnected()) {
          await printerHubService.start();
        }

        // Subscribe to SignalR progress updates
        const unsubscribe = printerHubService.onPrinterImportProgress((progress: PrinterImportProgress) => {
          if (window.PrintFarmerDebug?.import) {
            console.log('[ImportProgress] Received update:', progress);
          }
          
          setItems(prevItems => {
            const newItems = [...prevItems];
            const index = progress.index;
            
            if (index >= 0 && index < newItems.length) {
              newItems[index] = {
                index: progress.index,
                name: progress.name || newItems[index].name,
                status: progress.status || 'Pending',
                id: progress.id,
                reason: progress.reason
              };
            }
            
            return newItems;
          });
        });

        return unsubscribe;
      } catch (error) {
        console.error('Failed to set up SignalR for import progress:', error);
        return () => {}; // Return empty cleanup function on error
      }
    };

    let unsubscribe: (() => void) | undefined;
    
    setupSignalR().then(unsub => {
      unsubscribe = unsub;
    });

    return () => {
      if (unsubscribe) {
        unsubscribe();
      }
    };
  }, [isOpen, totalCount]);

  // Check if all items are processed
  useEffect(() => {
    if (items.length > 0 && items.every(item => item.status !== 'Pending')) {
      setIsComplete(true);
    }
  }, [items]);

  const getStatusIcon = (status: string) => {
    switch (status) {
      case 'Imported':
        return <span style={{ color: 'var(--pf-success)' }}>✓</span>;
      case 'Failed':
        return <span style={{ color: 'var(--pf-error)' }}>✗</span>;
      case 'Skipped':
        return <span style={{ color: 'var(--pf-warning)' }}>⊘</span>;
      case 'Pending':
        return <span style={{ color: 'var(--pf-text-secondary)' }}>●</span>;
      default:
        return null;
    }
  };

  const getStatusClass = (status: string) => {
    switch (status) {
      case 'Imported':
        return 'bg-pf-bg-0 bg-opacity-50';
      case 'Failed':
        return '';
      case 'Skipped':
        return '';
      case 'Pending':
        return '';
      default:
        return '';
    }
  };

  const successCount = items.filter(i => i.status === 'Imported').length;
  const failedCount = items.filter(i => i.status === 'Failed').length;
  const skippedCount = items.filter(i => i.status === 'Skipped').length;
  const pendingCount = items.filter(i => i.status === 'Pending').length;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Importing Printers"
      width="max-w-2xl"
    >
      <div className="flex flex-col gap-4">
        {/* File info */}
        <div className="text-sm text-pf-text-secondary">
          <strong>File:</strong> {fileName}
        </div>

        {/* Progress summary */}
        <div className="flex gap-4 text-sm">
          <div className="flex items-center gap-1">
            <span className="font-semibold">{successCount}</span>
            <span style={{ color: 'var(--pf-success)' }}>Imported</span>
          </div>
          <div className="flex items-center gap-1">
            <span className="font-semibold">{skippedCount}</span>
            <span style={{ color: 'var(--pf-warning)' }}>Skipped</span>
          </div>
          <div className="flex items-center gap-1">
            <span className="font-semibold">{failedCount}</span>
            <span style={{ color: 'var(--pf-error)' }}>Failed</span>
          </div>
          <div className="flex items-center gap-1">
            <span className="font-semibold">{pendingCount}</span>
            <span style={{ color: 'var(--pf-text-secondary)' }}>Pending</span>
          </div>
        </div>

        {/* Progress bar */}
        <div className="w-full bg-pf-bg-2 rounded-full h-2">
          <div
            className="bg-pf-accent h-2 rounded-full transition-all duration-300"
            style={{ width: `${((totalCount - pendingCount) / totalCount) * 100}%` }}
          />
        </div>

        {/* Results table */}
        <div className="max-h-96 overflow-y-auto border border-pf-border rounded">
          <table className="w-full text-sm">
            <thead className="bg-pf-bg-1 sticky top-0 border-b border-pf-border">
              <tr>
                <th className="text-left p-2 font-semibold">#</th>
                <th className="text-left p-2 font-semibold">Name</th>
                <th className="text-left p-2 font-semibold">Status</th>
                <th className="text-left p-2 font-semibold">Details</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item, idx) => (
                <tr
                  key={idx}
                  className={`border-b border-pf-border ${getStatusClass(item.status)}`}
                >
                  <td className="p-2">{item.index + 1}</td>
                  <td className="p-2 font-medium">{item.name}</td>
                  <td className="p-2">
                    <div className="flex items-center gap-2">
                      {getStatusIcon(item.status)}
                      {item.status}
                    </div>
                  </td>
                  <td className="p-2 text-xs text-pf-text-secondary">
                    {item.status === 'Imported' 
                      ? (item.id ? `ID: ${item.id}` : 'Successfully imported')
                      : (item.reason || '-')
                    }
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        {/* Action buttons */}
        <div className="flex justify-end gap-2">
          {isComplete ? (
            <Button
              onClick={onClose}
            >
              Close
            </Button>
          ) : (
            <div className="text-sm text-pf-text-secondary">
              Import in progress...
            </div>
          )}
        </div>
      </div>
    </Modal>
  );
};

export default ImportProgressModal;
