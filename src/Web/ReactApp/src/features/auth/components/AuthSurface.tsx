import React from 'react';
import clsx from 'clsx';
import { CloseIcon } from '@/common/components/icons/MdiIcons';
import { Modal, type ModalProps, type ModalSize } from '@/common/components/modals/Modal';
import { Button, type ButtonVariant } from '@/common/components/ui/Button';

export type AuthSurfaceVariant = 'modal' | 'page';

const sizeClasses: Record<ModalSize, string> = {
  sm: 'max-w-sm',
  md: 'max-w-md',
  lg: 'max-w-lg',
  xl: 'max-w-xl',
  full: 'max-w-7xl',
};

export interface AuthSurfaceProps {
  isOpen: boolean;
  onClose: () => void;
  title: string;
  children: React.ReactNode;
  footer?: React.ReactNode;
  surface?: AuthSurfaceVariant;
  size?: ModalProps['size'];
  width?: ModalProps['width'];
  maxHeight?: ModalProps['maxHeight'];
  isDisabled?: boolean;
  titleIcon?: React.ReactNode;
  closeAriaLabel?: string;
  closeOnBackdrop?: boolean;
  closeOnEscape?: boolean;
  showCloseButton?: boolean;
  closeButtonVariant?: ButtonVariant;
  closeButtonClassName?: string;
  className?: string;
}

export function AuthSurface({
  isOpen,
  onClose,
  title,
  children,
  footer,
  surface = 'modal',
  size,
  width,
  maxHeight,
  isDisabled = false,
  titleIcon,
  closeAriaLabel = 'Close dialog',
  closeOnBackdrop = false,
  closeOnEscape = true,
  showCloseButton = true,
  closeButtonVariant = 'subtle',
  closeButtonClassName,
  className,
}: AuthSurfaceProps) {
  if (!isOpen) {
    return null;
  }

  if (surface === 'modal') {
    return (
      <Modal
        isOpen={isOpen}
        onClose={onClose}
        title={title}
        titleIcon={titleIcon}
        footer={footer}
        size={size}
        width={width}
        maxHeight={maxHeight}
        isDisabled={isDisabled}
        closeAriaLabel={closeAriaLabel}
        closeOnBackdrop={closeOnBackdrop}
        closeOnEscape={closeOnEscape}
        showCloseButton={showCloseButton}
        closeButtonVariant={closeButtonVariant}
        closeButtonClassName={closeButtonClassName}
        className={className}
      >
        {children}
      </Modal>
    );
  }

  const widthClass = width ?? (size ? sizeClasses[size] : 'max-w-md');

  return (
    <div className={clsx('mx-auto w-full', widthClass)}>
      <div
        className={clsx(
          'overflow-hidden rounded-xl border border-pf-border bg-pf-bg-1 shadow-xl',
          className,
        )}
      >
        <div className="border-b border-pf-border px-6 py-5">
          <div className="flex items-start justify-between gap-4">
            <div className="flex min-w-0 items-center gap-3">
              {titleIcon && <div className="shrink-0">{titleIcon}</div>}
              <div className="min-w-0">
                <p className="text-[0.7rem] font-semibold uppercase tracking-[0.24em] text-pf-text-secondary">
                  PrintFarmer Access
                </p>
                <h1 className="text-xl font-semibold text-pf-text-primary">{title}</h1>
              </div>
            </div>
            {showCloseButton && (
              <Button
                type="button"
                onClick={onClose}
                disabled={isDisabled}
                variant={closeButtonVariant}
                size="sm"
                aria-label={closeAriaLabel}
                className={clsx('!h-auto !p-0', closeButtonClassName)}
              >
                <CloseIcon className="h-6 w-6" />
              </Button>
            )}
          </div>
        </div>

        <div className="px-6 py-6">{children}</div>

        {footer && (
          <div className="border-t border-pf-border bg-pf-bg-1 px-6 py-4">
            {footer}
          </div>
        )}
      </div>
    </div>
  );
}
