/* eslint-disable local/pf-no-raw-html-controls */
import React from 'react';
import clsx from 'clsx';
import { ChevronDownIcon } from '@/common/components/icons/MdiIcons';

export interface SelectProps extends React.SelectHTMLAttributes<HTMLSelectElement> {
  invalid?: boolean;
  containerClassName?: string;
  label?: string;
}

export const Select: React.FC<SelectProps> = ({ invalid, className, containerClassName, children, label, ...rest }) => {
  return (
    <div className={clsx('relative w-full', containerClassName)}>
      <select
        aria-label={rest['aria-label'] ?? label}
        title={rest.title ?? label}
        className={clsx(
          'border rounded-sm p-2 text-sm bg-pf-control-bg text-pf-control-text border-pf-control-border focus:outline-hidden focus:ring-2 focus:ring-pf-control-border-focus focus:border-pf-control-border-focus transition disabled:bg-pf-control-disabled-bg disabled:text-pf-control-disabled-text disabled:cursor-not-allowed disabled:opacity-60 appearance-none bg-none w-full pr-7 [&::-webkit-outer-spin-button]:hidden [&::-webkit-inner-spin-button]:hidden',
          invalid && 'border-pf-error focus:ring-pf-error focus:border-pf-error',
          className
        )}
        {...rest}
      >
        {children}
      </select>
      <ChevronDownIcon
        className={clsx(
          'pointer-events-none absolute right-2 top-1/2 -translate-y-1/2 w-4 h-4',
          invalid ? 'text-pf-error' : 'text-pf-text-tertiary',
          rest.disabled && 'opacity-50'
        )}
        ariaLabel=""
      />
    </div>
  );
};

export default Select;
