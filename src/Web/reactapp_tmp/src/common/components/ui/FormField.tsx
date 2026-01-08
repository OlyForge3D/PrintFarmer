import React from 'react';
import clsx from 'clsx';

export interface FormFieldProps {
  label?: string;
  htmlFor?: string;
  helper?: string | React.ReactNode;
  error?: string | React.ReactNode;
  children: React.ReactNode;
  required?: boolean;
  className?: string;
  inline?: boolean;
}

export const FormField: React.FC<FormFieldProps> = ({
  label,
  htmlFor,
  helper,
  error,
  children,
  required = false,
  className,
  inline = false
}) => {
  return (
    <div className={clsx('flex flex-col gap-1', inline && 'md:flex-row md:items-center', className)}>
      {label && (
        <label
          htmlFor={htmlFor}
          className={clsx('text-sm font-medium text-pf-text-primary', inline && 'md:w-48')}
        >
          {label} {required && <span className="text-pf-error" aria-hidden>*</span>}
        </label>
      )}
      <div className={clsx('flex flex-col gap-1', inline && 'flex-1')}>
        {children}
        {helper && !error && (
          <div className="text-xs text-pf-text-muted leading-relaxed">{helper}</div>
        )}
        {error && (
          <div className="text-xs text-pf-error-text" role="alert">{error}</div>
        )}
      </div>
    </div>
  );
};

export default FormField;
