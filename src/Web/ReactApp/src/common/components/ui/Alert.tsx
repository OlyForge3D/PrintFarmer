/* eslint-disable local/pf-no-raw-html-controls */
import React from 'react';
import clsx from 'clsx';

export type AlertType = 'success' | 'error' | 'info' | 'warning';

export interface AlertProps {
  type?: AlertType;
  variant?: AlertType;
  title?: string;
  children: React.ReactNode;
  className?: string;
  onClose?: () => void;
}

const alertStyles: Record<AlertType, string> = {
  success: 'bg-pf-success-bg border-pf-success text-pf-text-primary',
  error: 'bg-pf-error-bg border-pf-error-border text-pf-error-text',
  info: 'bg-pf-accent-bg border-pf-accent text-pf-text-primary',
  warning: 'bg-pf-warning border-pf-warning text-pf-warning-text'
};

export const Alert: React.FC<AlertProps> = ({
  type = 'info',
  variant,
  title,
  children,
  className,
  onClose
}) => {
  const alertType = variant ?? type;
  return (
    <div
      {...(alertType === 'error' ? { role: 'alert' } : {})}
      className={clsx('border rounded-sm p-3 text-sm flex items-start gap-3', alertStyles[alertType], className)}
    >
      <div className="flex-1">
        {title && <div className="font-semibold mb-0.5">{title}</div>}
        <div>{children}</div>
      </div>
      {onClose && (
        <button
          onClick={onClose}
          className="text-xs px-2 py-1 rounded-sm bg-pf-bg-0/40 hover:bg-pf-bg-1/70 transition"
          aria-label="Dismiss message"
        >
          ×
        </button>
      )}
    </div>
  );
};

export default Alert;
