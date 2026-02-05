import { useState, useCallback } from 'react';
import { Printer } from '@/types/api';
import { Button } from '@/common/components/ui';
import { MoreVerticalIcon } from '@/common/components/icons/MdiIcons';
import { EditIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';

interface PrinterActionsDropdownProps {
  printer: Printer;
  onEdit: (printer: Printer) => void;
  onDelete: (printer: Printer) => void;
}

export function PrinterActionsDropdown({ printer, onEdit, onDelete }: PrinterActionsDropdownProps) {
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
      <Button
        type="button"
        variant="subtle"
        size="sm"
        onClick={toggleDropdown}
        className="!p-2 !h-auto shrink-0"
        aria-label="Printer actions"
        iconCenter={<MoreVerticalIcon className="w-5 h-5" />}
      >
      </Button>

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
              
            <Button
              type="button"
              variant="subtle"
              size="sm"
              onClick={() => handleAction(() => onEdit(printer))}
              className="w-full text-left !justify-start"
              iconLeft={<EditIcon className="w-4 h-4 mr-3" />}
            >
              Edit Settings
            </Button>
              
              <div className="border-t border-pf-border my-1" />
              
              <Button
                type="button"
                variant="subtle"
                size="sm"
                onClick={() => handleAction(() => onDelete(printer))}
                className="w-full text-left !justify-start hover:text-pf-error hover:bg-pf-error-bg"
                iconLeft={<DeleteIcon className="w-4 h-4 mr-3" />}
              >
                Delete Printer
              </Button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
