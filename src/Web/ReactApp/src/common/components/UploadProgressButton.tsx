import React from 'react';
import { Button } from '@/common/components/ui';
import { UploadIcon } from '@/common/components/icons/MdiIcons';

export interface UploadProgressButtonProps {
  isUploading: boolean;
  progress: number; // 0-100
  error: string | null;
  onClick: () => void;
  label?: string;
  disabled?: boolean;
  className?: string;
  variant?: 'primary' | 'secondary';
}

/**
 * Button component that displays upload progress with visual feedback.
 * Shows spinner during upload, error state if upload fails.
 */
export const UploadProgressButton = React.forwardRef<
  HTMLButtonElement,
  UploadProgressButtonProps
>(({
  isUploading,
  progress,
  error,
  onClick,
  label = 'Upload',
  disabled = false,
  className = '',
  variant = 'primary',
}, ref) => {
  return (
    <div className={`flex flex-col gap-2 ${className}`}>
      <Button
        ref={ref}
        onClick={onClick}
        disabled={disabled || isUploading}
        variant={error ? 'danger' : variant}
        iconLeft={
          isUploading ? (
            <div className="w-4 h-4 border-2 border-transparent border-t-current rounded-full animate-spin" />
          ) : (
            <UploadIcon className="w-4 h-4" />
          )
        }
      >
        {isUploading ? `Uploading (${progress}%)...` : error ? 'Upload Failed' : label}
      </Button>

      {/* Progress bar */}
      {isUploading && (
        <div className="w-full bg-pf-bg-2 rounded-full h-2 overflow-hidden">
          <div
            className="bg-pf-accent h-full transition-all duration-300"
            style={{ width: `${progress}%` }}
          />
        </div>
      )}

      {/* Error message */}
      {error && (
        <p className="text-sm text-pf-error">{error}</p>
      )}
    </div>
  );
});

UploadProgressButton.displayName = 'UploadProgressButton';
