import React, { useEffect } from 'react';
import { CloseIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui/Button';

export interface ModalProps {
  /** Whether the modal is open */
  isOpen: boolean;
  /** Callback when modal should close (backdrop click, escape key, or close button) */
  onClose: () => void;
  /** Modal title */
  title: string;
  /** Modal content */
  children: React.ReactNode;
  /** Optional footer content (buttons, etc.) */
  footer?: React.ReactNode;
  /** Optional custom width class (default: max-w-2xl) */
  width?: string;
  /** Optional custom max height class (default: max-h-[90vh]) */
  maxHeight?: string;
  /** Whether interactions should be disabled (e.g., during loading) */
  isDisabled?: boolean;
  /** Optional icon to display next to title */
  titleIcon?: React.ReactNode;
  /** Optional close button aria-label */
  closeAriaLabel?: string;
}

/**
 * Reusable Modal component with consistent styling across the application.
 * 
 * Provides:
 * - Fixed backdrop with blur effect
 * - Rounded border with shadow
 * - Sticky header with title and close button
 * - Scrollable content area
 * - Optional footer with action buttons
 * - Modal behavior: only close via header close button or footer buttons
 * 
 * @example
 * ```tsx
 * const [isOpen, setIsOpen] = useState(false);
 * 
 * <Modal
 *   isOpen={isOpen}
 *   onClose={() => setIsOpen(false)}
 *   title="Import Profile"
 *   footer={
 *     <>
 *       <Button onClick={() => setIsOpen(false)}>Cancel</Button>
 *       <Button variant="primary" onClick={handleImport}>Import</Button>
 *     </>
 *   }
 * >
 *   <p>Modal content goes here</p>
 * </Modal>
 * ```
 */
export function Modal({
  isOpen,
  onClose,
  title,
  children,
  footer,
  width = 'max-w-2xl',
  maxHeight = 'max-h-[90vh]',
  isDisabled = false,
  titleIcon,
  closeAriaLabel = 'Close modal'
}: ModalProps) {
  // Handle Escape key globally with stopPropagation to prevent parent modals from closing
  useEffect(() => {
    if (!isOpen || isDisabled) return;

    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        onClose();
      }
    };

    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, isDisabled, onClose]);

  if (!isOpen) return null;

  return (
    <div 
      className="fixed inset-0 bg-black/50 backdrop-blur-sm flex items-center justify-center z-50 p-4"
      role="presentation"
    >
      <div 
        className={`bg-pf-bg-1 rounded-xl shadow-xl border border-pf-border ${width} w-full ${maxHeight} overflow-hidden flex flex-col`}
        onClick={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="sticky top-0 bg-pf-bg-1 border-b border-pf-border px-6 py-4 flex items-center justify-between flex-shrink-0">
          <div className="flex items-center gap-3">
            {titleIcon && (
              <div className="flex-shrink-0">
                {titleIcon}
              </div>
            )}
            <h2 className="text-lg font-semibold text-pf-text-primary">
              {title}
            </h2>
          </div>
          <Button
            onClick={onClose}
            disabled={isDisabled}
            variant="subtle"
            size="sm"
            aria-label={closeAriaLabel}
            className="!p-1 !h-auto"
          >
            <CloseIcon className="w-6 h-6" />
          </Button>
        </div>

        {/* Content */}
        <div className="overflow-y-auto flex-1 px-6 py-6">
          {children}
        </div>

        {/* Footer */}
        {footer && (
          <div className="sticky bottom-0 bg-pf-bg-1 border-t border-pf-border px-6 py-4 flex items-center justify-end gap-3 flex-shrink-0">
            {footer}
          </div>
        )}
      </div>
    </div>
  );
}
