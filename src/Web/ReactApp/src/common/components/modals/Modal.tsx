import React, { useEffect, useCallback } from 'react';
import { CloseIcon } from '@/common/components/icons/MdiIcons';
import { Button, type ButtonVariant } from '@/common/components/ui/Button';
import clsx from 'clsx';

export type ModalSize = 'sm' | 'md' | 'lg' | 'xl' | 'full';

const sizeClasses: Record<ModalSize, string> = {
  sm: 'max-w-sm',
  md: 'max-w-md',
  lg: 'max-w-lg',
  xl: 'max-w-xl',
  full: 'max-w-4xl',
};

export interface ModalProps {
  /** Whether the modal is open */
  isOpen: boolean;
  /** Callback when modal should close (backdrop click, escape key, or close button) */
  onClose: () => void;
  /** Modal title (optional - header hidden if no title and showCloseButton is false) */
  title?: string;
  /** Modal content */
  children: React.ReactNode;
  /** Optional footer content (buttons, etc.) */
  footer?: React.ReactNode;
  /** Size preset for modal width (alternative to width prop) */
  size?: ModalSize;
  /** Optional custom width class - takes precedence over size (default: max-w-2xl) */
  width?: string;
  /** Optional custom max height class (default: max-h-[90vh]) */
  maxHeight?: string;
  /** Whether interactions should be disabled (e.g., during loading) */
  isDisabled?: boolean;
  /** Optional icon to display next to title */
  titleIcon?: React.ReactNode;
  /** Optional close button aria-label */
  closeAriaLabel?: string;
  /** Whether clicking the backdrop closes the modal (default: false for controlled behavior) */
  closeOnBackdrop?: boolean;
  /** Whether pressing Escape closes the modal (default: true) */
  closeOnEscape?: boolean;
  /** Whether to show the close button (default: true) */
  showCloseButton?: boolean;
  /** Close button variant (default: subtle) */
  closeButtonVariant?: ButtonVariant;
  /** Additional className for the close button */
  closeButtonClassName?: string;
  /** Additional className for the modal content */
  className?: string;
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
  size,
  width,
  maxHeight = 'max-h-[90vh]',
  isDisabled = false,
  titleIcon,
  closeAriaLabel = 'Close modal',
  closeOnBackdrop = false,
  closeOnEscape = true,
  showCloseButton = true,
  closeButtonVariant = 'subtle',
  closeButtonClassName,
  className,
}: ModalProps) {
  // Compute width class - explicit width takes precedence, then size, then default
  const widthClass = width ?? (size ? sizeClasses[size] : 'max-w-2xl');

  // Handle backdrop click
  const handleBackdropClick = useCallback(
    (e: React.MouseEvent<HTMLDivElement>) => {
      if (closeOnBackdrop && e.target === e.currentTarget && !isDisabled) {
        onClose();
      }
    },
    [closeOnBackdrop, isDisabled, onClose]
  );

  // Handle Escape key globally with stopPropagation to prevent parent modals from closing
  useEffect(() => {
    if (!isOpen || isDisabled || !closeOnEscape) return;

    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        onClose();
      }
    };

    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, isDisabled, closeOnEscape, onClose]);

  // Lock body scroll when modal is open
  useEffect(() => {
    if (isOpen) {
      document.body.style.overflow = 'hidden';
    }
    return () => {
      document.body.style.overflow = '';
    };
  }, [isOpen]);

  if (!isOpen) return null;

  const showHeader = title || showCloseButton;

  return (
    <div 
      className="fixed inset-0 bg-black/50 backdrop-blur-xs flex items-center justify-center z-50 p-4"
      onClick={handleBackdropClick}
      role="dialog"
      aria-modal="true"
      aria-labelledby={title ? 'modal-title' : undefined}
    >
      <div 
        className={clsx(
          'bg-pf-bg-1 rounded-xl shadow-xl border border-pf-border w-full overflow-hidden flex flex-col',
          widthClass,
          maxHeight,
          className
        )}
        onClick={(e) => e.stopPropagation()}
      >
        {/* Header */}
        {showHeader && (
          <div className="sticky top-0 bg-pf-bg-1 border-b border-pf-border px-6 py-4 flex items-center justify-between shrink-0">
            <div className="flex items-center gap-3">
              {titleIcon && (
                <div className="shrink-0">
                  {titleIcon}
                </div>
              )}
              {title && (
                <h2 id="modal-title" className="text-lg font-semibold text-pf-text-primary">
                  {title}
                </h2>
              )}
            </div>
            {showCloseButton && (
              <Button
                onClick={onClose}
                disabled={isDisabled}
                variant={closeButtonVariant}
                size="sm"
                aria-label={closeAriaLabel}
                className={clsx('!p-1 !h-auto', closeButtonClassName)}
              >
                <CloseIcon className="w-6 h-6" />
              </Button>
            )}
          </div>
        )}

        {/* Content */}
        <div className="overflow-y-auto flex-1 px-6 py-6">
          {children}
        </div>

        {/* Footer */}
        {footer && (
          <div className="sticky bottom-0 bg-pf-bg-1 border-t border-pf-border px-6 py-4 flex items-center justify-end gap-3 shrink-0">
            {footer}
          </div>
        )}
      </div>
    </div>
  );
}
