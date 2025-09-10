import { useState, useCallback } from 'react';
import { Printer } from '@/types/api';
import { MoreVertical, Edit, Trash2, Wrench } from 'lucide-react';

interface PrinterActionsDropdownProps {
  printer: Printer;
  onEdit: (printer: Printer) => void;
  onDelete: (printer: Printer) => void;
  onManage: (printer: Printer) => void;
}

export function PrinterActionsDropdown({ printer, onEdit, onDelete, onManage }: PrinterActionsDropdownProps) {
  const [isOpen, setIsOpen] = useState(false);

  const toggleDropdown = useCallback(() => {
    setIsOpen(prev => !prev);
  }, []);

  const handleAction = useCallback((action: () => void) => {
    action();
    setIsOpen(false);
  }, []);

  return (
    <div className="relative">
      <button
        onClick={toggleDropdown}
        className="flex-shrink-0 p-2 text-pf-text-tertiary hover:text-pf-accent transition-colors rounded-md hover:bg-pf-bg-2"
        aria-label="Printer actions"
      >
        <MoreVertical className="w-5 h-5" />
      </button>

      {isOpen && (
        <>
          {/* Backdrop */}
          <div 
            className="fixed inset-0 z-10" 
            onClick={() => setIsOpen(false)}
          />
          
          {/* Dropdown Menu */}
          <div className="absolute right-0 top-full mt-1 w-48 bg-pf-panel border border-pf-border rounded-lg shadow-lg z-20">
            <div className="py-1">
              <button
                onClick={() => handleAction(() => onManage(printer))}
                className="w-full px-4 py-2 text-left text-sm text-pf-text-primary hover:bg-pf-bg-2 flex items-center transition-colors"
              >
                <Wrench className="w-4 h-4 mr-3" />
                Manage Printer
              </button>
              
              <button
                onClick={() => handleAction(() => onEdit(printer))}
                className="w-full px-4 py-2 text-left text-sm text-pf-text-primary hover:bg-pf-bg-2 flex items-center transition-colors"
              >
                <Edit className="w-4 h-4 mr-3" />
                Edit Settings
              </button>
              
              <div className="border-t border-pf-border my-1" />
              
              <button
                onClick={() => handleAction(() => onDelete(printer))}
                className="w-full px-4 py-2 text-left text-sm text-pf-error-text hover:bg-pf-error-bg hover:text-pf-error-text flex items-center transition-colors"
              >
                <Trash2 className="w-4 h-4 mr-3" />
                Delete Printer
              </button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
