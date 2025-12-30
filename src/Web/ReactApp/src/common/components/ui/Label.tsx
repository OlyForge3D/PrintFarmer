import React from 'react';
import clsx from 'clsx';

export interface LabelProps extends React.LabelHTMLAttributes<HTMLLabelElement> {
  /** Whether the field is required (shows asterisk) */
  required?: boolean;
}

export const Label: React.FC<LabelProps> = ({
  required,
  className,
  children,
  ...rest
}) => {
  return (
    <label
      className={clsx(
        'text-sm font-medium text-pf-text-secondary block',
        className
      )}
      {...rest}
    >
      {children}
      {required && <span className="text-pf-error ml-0.5">*</span>}
    </label>
  );
};

export default Label;
