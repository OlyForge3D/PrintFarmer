import React from 'react';
import { Button } from '@/common/components/ui/Button';

export interface FloatingActionButtonProps {
  icon: React.ComponentType<{ className?: string }>;
  onClick: () => void;
  label: string;
  position?: 'bottom-right' | 'bottom-center' | 'bottom-left';
  variant?: 'primary' | 'secondary';
  className?: string;
  disabled?: boolean;
  loading?: boolean;
}

/**
 * Reusable Floating Action Button component for common actions.
 * 
 * @example
 * ```tsx
 * <FloatingActionButton
 *   icon={PlusIcon}
 *   onClick={() => setShowUploadModal(true)}
 *   label="Upload Model"
 *   position="bottom-right"
 *   variant="primary"
 * />
 * ```
 */
export const FloatingActionButton = React.forwardRef<
  HTMLButtonElement,
  FloatingActionButtonProps
>(({
  icon: Icon,
  onClick,
  label,
  position = 'bottom-right',
  variant = 'primary',
  className = '',
  disabled = false,
  loading = false,
}, ref) => {
  return (
    <Button
      ref={ref}
      onClick={onClick}
      disabled={disabled || loading}
      aria-label={label}
      title={label}
      variant={variant}
      className={`
        fixed rounded-full shadow-lg hover:shadow-xl transition-all
        ${position === 'bottom-right' ? 'bottom-6 right-6' : ''}
        ${position === 'bottom-center' ? 'bottom-6 left-1/2 -translate-x-1/2' : ''}
        ${position === 'bottom-left' ? 'bottom-6 left-6' : ''}
        z-40 w-16 h-16 p-0
        ${className}
      `}
    >
      {loading ? (
        <div className="w-6 h-6 border-2 border-transparent border-t-current rounded-full animate-spin" />
      ) : (
        <Icon className="w-6 h-6" />
      )}
    </Button>
  );
});

FloatingActionButton.displayName = 'FloatingActionButton';
