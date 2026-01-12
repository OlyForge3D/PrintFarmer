import React from 'react';

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
    <button
      ref={ref}
      onClick={onClick}
      disabled={disabled || loading}
      aria-label={label}
      title={label}
      className={`
        fixed rounded-full p-4 shadow-lg hover:shadow-xl transition-all
        focus:outline-none focus:ring-2 focus:ring-pf-accent focus:ring-offset-2
        ${variant === 'primary' 
          ? 'bg-pf-accent text-white hover:bg-pf-accent-dark' 
          : 'bg-pf-bg-2 text-pf-text-primary hover:bg-pf-bg-3'
        }
        ${disabled || loading ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'}
        ${position === 'bottom-right' && 'bottom-6 right-6'}
        ${position === 'bottom-center' && 'bottom-6 left-1/2 -translate-x-1/2'}
        ${position === 'bottom-left' && 'bottom-6 left-6'}
        z-40
        ${className}
      `}
    >
      {loading ? (
        <div className="w-6 h-6 border-2 border-transparent border-t-current rounded-full animate-spin" />
      ) : (
        <Icon className="w-6 h-6" />
      )}
    </button>
  );
});

FloatingActionButton.displayName = 'FloatingActionButton';
