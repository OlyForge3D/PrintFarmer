import React from 'react';
import clsx from 'clsx';

export interface InputProps extends React.InputHTMLAttributes<HTMLInputElement> {
  invalid?: boolean;
  ref?: React.Ref<HTMLInputElement>;
}

export const Input: React.FC<InputProps> = ({ invalid, className, ref, ...rest }) => {
  return (
    <input
      ref={ref}
      className={clsx(
        'w-full border rounded-sm p-2 text-sm bg-pf-bg-0 text-pf-text-primary border-pf-border focus:outline-hidden focus:ring-2 focus:ring-pf-accent focus:border-pf-accent transition disabled:bg-pf-disabled disabled:cursor-not-allowed',
        invalid && 'border-pf-error focus:ring-pf-error focus:border-pf-error',
        className
      )}
      {...rest}
    />
  );
};

export default Input;
