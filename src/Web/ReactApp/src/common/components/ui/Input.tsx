import React from 'react';
import clsx from 'clsx';

export interface InputProps extends React.InputHTMLAttributes<HTMLInputElement> {
  invalid?: boolean;
}

export const Input = React.forwardRef<HTMLInputElement, InputProps>(function Input(
  { invalid, className, ...rest },
  ref
) {
  return (
    <input
      ref={ref}
      className={clsx(
        'w-full border rounded-sm p-2 text-sm bg-pf-control-bg text-pf-control-text placeholder:text-pf-control-placeholder border-pf-control-border focus:outline-hidden focus:ring-2 focus:ring-pf-control-border-focus focus:border-pf-control-border-focus transition disabled:bg-pf-control-disabled-bg disabled:text-pf-control-disabled-text disabled:cursor-not-allowed disabled:opacity-60 read-only:bg-pf-control-disabled-bg read-only:border-pf-control-border',
        invalid && 'border-pf-error focus:ring-pf-error focus:border-pf-error',
        className
      )}
      {...rest}
    />
  );
});

export default Input;
