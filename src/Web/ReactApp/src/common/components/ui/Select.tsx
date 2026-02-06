/* eslint-disable local/pf-no-raw-html-controls */
import React from 'react';
import clsx from 'clsx';

export interface SelectProps extends React.SelectHTMLAttributes<HTMLSelectElement> {
  invalid?: boolean;
  containerClassName?: string;
}

export const Select: React.FC<SelectProps> = ({ invalid, className, containerClassName, children, ...rest }) => {
  return (
    <div className={clsx('relative w-full', containerClassName)}>
      <select
        className={clsx(
          'border rounded-sm p-2 text-sm bg-pf-bg-0 text-pf-text-primary border-pf-border focus:outline-hidden focus:ring-2 focus:ring-pf-accent focus:border-pf-accent transition disabled:bg-pf-disabled disabled:cursor-not-allowed appearance-none w-full pr-7 [&::-webkit-outer-spin-button]:hidden [&::-webkit-inner-spin-button]:hidden',
          invalid && 'border-pf-error focus:ring-pf-error focus:border-pf-error',
          className
        )}
        {...rest}
      >
        {children}
      </select>
    </div>
  );
};

export default Select;
