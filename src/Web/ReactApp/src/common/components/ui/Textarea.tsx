/* eslint-disable local/pf-no-raw-html-controls */
import React from 'react';
import clsx from 'clsx';

export interface TextareaProps extends React.TextareaHTMLAttributes<HTMLTextAreaElement> {
  /** Whether the textarea is in an invalid state */
  invalid?: boolean;
}

export const Textarea: React.FC<TextareaProps> = ({
  invalid,
  className,
  ...rest
}) => {
  return (
    <textarea
      className={clsx(
        'w-full border rounded-sm p-2 text-sm bg-pf-control-bg text-pf-control-text border-pf-border',
        'focus:outline-hidden focus:ring-2 focus:ring-pf-accent focus:border-pf-accent',
        'disabled:bg-pf-control-disabled-bg disabled:text-pf-control-disabled-text disabled:cursor-not-allowed',
        'read-only:bg-pf-bg-1 read-only:border-pf-border-light',
        'placeholder:text-pf-control-placeholder',
        'resize-none min-h-[80px]',
        'transition',
        invalid && 'border-pf-error focus:ring-pf-error focus:border-pf-error',
        className
      )}
      {...rest}
    />
  );
};

export default Textarea;
