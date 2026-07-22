import React, { useEffect, useCallback, useId, useRef } from 'react';
import { createPortal } from 'react-dom';
import { CloseIcon } from '@/common/components/icons/MdiIcons';
import { Button, type ButtonVariant } from '@/common/components/ui/Button';
import clsx from 'clsx';

export type ModalSize = 'sm' | 'md' | 'lg' | 'xl' | 'full';

const sizeClasses: Record<ModalSize, string> = {
  sm: 'max-w-sm',
  md: 'max-w-md',
  lg: 'max-w-lg',
  xl: 'max-w-xl',
  full: 'max-w-7xl',
};

const FOCUSABLE_SELECTOR = [
  'button:not([disabled])',
  '[href]',
  'input:not([disabled]):not([type="hidden"])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[contenteditable="true"]',
  'summary',
  '[tabindex]:not([tabindex="-1"])',
].join(', ');
const AUTOFOCUS_SELECTOR = '[data-autofocus], [autofocus]';
const MODAL_OWNED_SELECTOR = '[data-sonner-toaster], [data-modal-keep-live], [data-modal-portal]';

interface ManagedSiblingState {
  inert: string | null;
  ariaHidden: string | null;
}

const modalStack: HTMLElement[] = [];
const managedSiblingState = new Map<HTMLElement, ManagedSiblingState>();
let bodyScrollLockCount = 0;
let previousBodyOverflow: string | null = null;

function isElementRendered(element: HTMLElement) {
  const style = window.getComputedStyle(element);

  if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') {
    return false;
  }

  const clientRects = Array.from(element.getClientRects());
  const hasRenderedSize = element.offsetWidth > 0
    || element.offsetHeight > 0
    || clientRects.some((rect) => rect.width > 0 || rect.height > 0);

  if (!hasRenderedSize) {
    return clientRects.length === 0;
  }

  return style.position === 'fixed' || element.offsetParent !== null;
}

function getFocusableElements(container: HTMLElement | null): HTMLElement[] {
  if (!container) {
    return [];
  }

  return Array.from(container.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)).filter((element) => {
    if (
      element.hasAttribute('disabled') ||
      element.getAttribute('aria-hidden') === 'true' ||
      element.closest('[hidden], [aria-hidden="true"]') ||
      !isElementRendered(element)
    ) {
      return false;
    }

    return element.tabIndex >= 0;
  });
}

function restoreSiblingState(element: HTMLElement) {
  const previousState = managedSiblingState.get(element);
  if (!previousState) {
    return;
  }

  if (previousState.inert === null) {
    element.removeAttribute('inert');
  } else {
    element.setAttribute('inert', previousState.inert);
  }

  if (previousState.ariaHidden === null) {
    element.removeAttribute('aria-hidden');
  } else {
    element.setAttribute('aria-hidden', previousState.ariaHidden);
  }

  managedSiblingState.delete(element);
}

function hideSiblingFromModal(element: HTMLElement) {
  if (!managedSiblingState.has(element)) {
    managedSiblingState.set(element, {
      inert: element.getAttribute('inert'),
      ariaHidden: element.getAttribute('aria-hidden'),
    });
  }

  element.setAttribute('inert', '');
  element.setAttribute('aria-hidden', 'true');
}

function applyBackgroundInertState() {
  const activeModal = modalStack.at(-1);

  if (!activeModal) {
    for (const element of Array.from(managedSiblingState.keys())) {
      restoreSiblingState(element);
    }
    return;
  }

  const hiddenThisPass = new Set<HTMLElement>();

  const applyToChildren = (container: ParentNode) => {
    for (const child of Array.from(container.children)) {
      if (!(child instanceof HTMLElement)) {
        continue;
      }

      if (child === activeModal || child.matches(MODAL_OWNED_SELECTOR)) {
        restoreSiblingState(child);
        continue;
      }

      if (child.contains(activeModal) || child.querySelector(MODAL_OWNED_SELECTOR)) {
        restoreSiblingState(child);
        applyToChildren(child);
        continue;
      }

      hideSiblingFromModal(child);
      hiddenThisPass.add(child);
    }
  };

  applyToChildren(document.body);

  for (const element of Array.from(managedSiblingState.keys())) {
    if (!element.isConnected) {
      managedSiblingState.delete(element);
    } else if (!hiddenThisPass.has(element)) {
      restoreSiblingState(element);
    }
  }
}

function registerModalRoot(element: HTMLElement) {
  if (!modalStack.includes(element)) {
    modalStack.push(element);
  }

  applyBackgroundInertState();
}

function unregisterModalRoot(element: HTMLElement) {
  const index = modalStack.lastIndexOf(element);
  if (index !== -1) {
    modalStack.splice(index, 1);
  }

  restoreSiblingState(element);
  applyBackgroundInertState();
}

function getActiveModalRoot() {
  return modalStack.at(-1) ?? null;
}

function lockBodyScroll() {
  if (bodyScrollLockCount === 0) {
    previousBodyOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
  }

  bodyScrollLockCount += 1;
}

function unlockBodyScroll() {
  bodyScrollLockCount = Math.max(0, bodyScrollLockCount - 1);

  if (bodyScrollLockCount === 0) {
    document.body.style.overflow = previousBodyOverflow ?? '';
    previousBodyOverflow = null;
  }
}

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
  const modalRootRef = useRef<HTMLDivElement>(null);
  const previouslyFocusedElementRef = useRef<HTMLElement | null>(null);
  const titleId = useId();

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

  useEffect(() => {
    if (!isOpen) return;

    const modalRoot = modalRootRef.current;
    if (!modalRoot) return;

    previouslyFocusedElementRef.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;

    registerModalRoot(modalRoot);

    const frame = window.requestAnimationFrame(() => {
      if (document.activeElement instanceof HTMLElement && modalRoot.contains(document.activeElement)) {
        return;
      }

      const focusableElements = getFocusableElements(modalRoot);
      const focusTarget = focusableElements.find((element) => element.matches(AUTOFOCUS_SELECTOR))
        ?? focusableElements[0]
        ?? modalRoot;
      focusTarget.focus();
    });

    return () => {
      window.cancelAnimationFrame(frame);
      unregisterModalRoot(modalRoot);

      const previouslyFocusedElement = previouslyFocusedElementRef.current;
      if (previouslyFocusedElement?.isConnected) {
        window.requestAnimationFrame(() => previouslyFocusedElement.focus());
      }
    };
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen) return;

    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key !== 'Tab') {
        return;
      }

      const modalRoot = modalRootRef.current;
      if (!modalRoot || modalRoot !== getActiveModalRoot()) {
        return;
      }

      const focusableElements = getFocusableElements(modalRoot);
      if (focusableElements.length === 0) {
        e.preventDefault();
        modalRoot.focus();
        return;
      }

      const firstElement = focusableElements[0];
      const lastElement = focusableElements[focusableElements.length - 1];
      const activeElement = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      const containsFocus = activeElement ? modalRoot.contains(activeElement) : false;
      // Body-portaled modal-owned UI (for example dropdowns) manages its own focus movement.
      if (!containsFocus && activeElement?.closest(MODAL_OWNED_SELECTOR)) {
        return;
      }

      if (e.shiftKey) {
        if (!containsFocus || activeElement === firstElement) {
          e.preventDefault();
          lastElement.focus();
        }
        return;
      }

      if (!containsFocus || activeElement === lastElement) {
        e.preventDefault();
        firstElement.focus();
      }
    };

    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [isOpen]);

  // Handle Escape key globally with stopPropagation to prevent parent modals from closing
  useEffect(() => {
    if (!isOpen || isDisabled || !closeOnEscape) return;

    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && modalRootRef.current === getActiveModalRoot()) {
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
      lockBodyScroll();
    }
    return () => {
      if (isOpen) {
        unlockBodyScroll();
      }
    };
  }, [isOpen]);

  if (!isOpen) return null;

  const showHeader = title || showCloseButton;

  return createPortal(
    <div 
      ref={modalRootRef}
      className="fixed inset-0 bg-black/50 backdrop-blur-xs flex items-center justify-center z-50 p-4"
      onClick={handleBackdropClick}
      role="dialog"
      aria-modal="true"
      aria-labelledby={title ? titleId : undefined}
      tabIndex={-1}
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
                <h2 id={titleId} className="text-lg font-semibold text-pf-text-primary">
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
                className={clsx('p-1! h-auto!', closeButtonClassName)}
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
    </div>,
    document.body,
  );
}
