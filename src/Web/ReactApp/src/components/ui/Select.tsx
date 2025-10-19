import React from 'react';
import clsx from 'clsx';

export interface SelectProps extends React.SelectHTMLAttributes<HTMLSelectElement> {
  invalid?: boolean;
}

export const Select: React.FC<SelectProps> = ({ invalid, className, children, ...rest }) => {
  return (
    <select
      className={clsx(
        'border rounded p-2 text-sm bg-pf-bg-0 text-pf-text-primary border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent focus:border-pf-accent transition disabled:bg-pf-disabled disabled:cursor-not-allowed',
        invalid && 'border-pf-error focus:ring-pf-error focus:border-pf-error',
        className
      )}
      {...rest}
    >
      {children}
    </select>
  );
};

export default Select;
