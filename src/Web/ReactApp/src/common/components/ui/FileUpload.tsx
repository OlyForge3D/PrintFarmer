/* eslint-disable local/pf-no-raw-html-controls */
import React, { useRef, useId, ReactNode } from 'react';
import { Label } from './Label';
import { Button } from './Button';

export interface FileUploadProps {
  /**
   * The input id attribute. Used for label association.
   */
  id?: string;

  /**
   * Label text displayed above the input.
   */
  label?: string;

  /**
   * Helper text displayed below the input.
   */
  helperText?: string;

  /**
   * Error message displayed below the input in red.
   */
  error?: string;

  /**
   * Comma-separated list of MIME types or file extensions to accept.
   * @example "image/*,.pdf"
   * @example ".csv,.xlsx"
   */
  accept?: string;

  /**
   * If true, multiple files can be selected.
   */
  multiple?: boolean;

  /**
   * If true, the file input is disabled.
   */
  disabled?: boolean;

  /**
   * Callback fired when files are selected.
   */
  onChange?: (files: FileList | null) => void;

  /**
   * Reference to the native input element.
   */
  ref?: React.Ref<HTMLInputElement>;

  /**
   * Custom CSS class for the wrapper container.
   */
  className?: string;

  /**
   * Optional button text to display instead of native file input.
   * If provided, renders as a styled button that opens the file picker.
   */
  buttonText?: string;

  /**
   * Optional icon or element to display in button (when buttonText is provided).
   */
  buttonIcon?: ReactNode;

  /**
   * Variant style for the button (when buttonText is provided).
   */
  buttonVariant?: 'primary' | 'secondary' | 'danger' | 'success' | 'subtle';
}

/**
 * FileUpload Component
 *
 * A reusable file input component with optional label, helper text, and error display.
 * Can render as a native file input or as a styled button that opens a file picker.
 *
 * @example
 * // Native file input
 * <FileUpload
 *   label="Upload CSV"
 *   accept=".csv"
 *   helperText="Maximum 10MB"
 *   onChange={handleFiles}
 * />
 *
 * @example
 * // Button-style file input
 * <FileUpload
 *   label="Import Models"
 *   accept=".stl,.obj"
 *   buttonText="Choose Files"
 *   multiple
 *   onChange={handleFiles}
 * />
 */
export const FileUpload = React.forwardRef<HTMLInputElement, FileUploadProps>(
  (
    {
      id,
      label,
      helperText,
      error,
      accept,
      multiple = false,
      disabled = false,
      onChange,
      className,
      buttonText,
      buttonIcon,
      buttonVariant = 'primary',
    },
    ref,
  ) => {
    const inputRef = useRef<HTMLInputElement>(null);
    const finalRef = ref || inputRef;

    const handleInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
      onChange?.(e.target.files);
    };

    const handleButtonClick = () => {
      if (inputRef.current) {
        inputRef.current.click();
      }
    };

    const generatedId = useId();
    const inputId = id || generatedId;

    // Base input styles
    const inputBaseClass = `
      block w-full px-3 py-2 rounded-lg
      border border-pf-border
      bg-pf-bg-1 text-pf-text-primary
      placeholder-pf-text-secondary
      focus:outline-none focus:ring-2 focus:ring-pf-accent
      disabled:opacity-50 disabled:cursor-not-allowed
      text-sm
    `;

    // Button variant styles
    const buttonVariantClasses: Record<string, string> = {
      primary: `
        px-4 py-2 rounded-lg font-medium
        bg-pf-accent text-white
        hover:opacity-90 active:opacity-75
        focus:outline-none focus:ring-2 focus:ring-pf-accent focus:ring-offset-2
        disabled:opacity-50 disabled:cursor-not-allowed
        transition-opacity duration-150
      `,
      secondary: `
        px-4 py-2 rounded-lg font-medium
        bg-pf-bg-2 text-pf-text-primary
        border border-pf-border
        hover:bg-pf-bg-3 active:bg-pf-bg-1
        focus:outline-none focus:ring-2 focus:ring-pf-accent
        disabled:opacity-50 disabled:cursor-not-allowed
        transition-colors duration-150
      `,
      danger: `
        px-4 py-2 rounded-lg font-medium
        bg-pf-error text-white
        hover:opacity-90 active:opacity-75
        focus:outline-none focus:ring-2 focus:ring-red-500 focus:ring-offset-2
        disabled:opacity-50 disabled:cursor-not-allowed
        transition-opacity duration-150
      `,
      success: `
        px-4 py-2 rounded-lg font-medium
        bg-pf-success-bg text-white
        hover:opacity-90 active:opacity-75
        focus:outline-none focus:ring-2 focus:ring-green-500 focus:ring-offset-2
        disabled:opacity-50 disabled:cursor-not-allowed
        transition-opacity duration-150
      `,
      subtle: `
        px-3 py-1.5 rounded-lg font-medium text-sm
        bg-transparent text-pf-accent
        hover:bg-pf-bg-2
        focus:outline-none focus:ring-2 focus:ring-pf-accent
        disabled:opacity-50 disabled:cursor-not-allowed
        transition-colors duration-150
      `,
    };

    return (
      <div className={`flex flex-col gap-1 ${className ?? ''}`}>
        {label && (
          <Label htmlFor={inputId} className="text-sm font-medium">
            {label}
          </Label>
        )}

        <input
          ref={finalRef}
          id={inputId}
          type="file"
          accept={accept}
          multiple={multiple}
          disabled={disabled}
          onChange={handleInputChange}
          className={buttonText ? 'hidden' : inputBaseClass}
          aria-describedby={
            helperText || error ? `${inputId}-description` : undefined
          }
        />

        {buttonText && (
          <Button
            type="button"
            onClick={handleButtonClick}
            disabled={disabled}
            variant={buttonVariant as 'primary' | 'secondary' | 'danger' | 'success' | 'subtle'}
            size="md"
            className={buttonVariantClasses[buttonVariant]}
            aria-label={`${buttonText} (file upload)`}
          >
            <span className="flex items-center gap-2">
              {buttonIcon}
              {buttonText}
            </span>
          </Button>
        )}

        {(helperText || error) && (
          <div
            id={`${inputId}-description`}
            className={`text-xs ${error ? 'text-pf-error' : 'text-pf-text-secondary'}`}
          >
            {error || helperText}
          </div>
        )}
      </div>
    );
  },
);

FileUpload.displayName = 'FileUpload';
