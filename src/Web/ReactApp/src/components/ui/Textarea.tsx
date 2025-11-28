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
        'border rounded p-2 text-sm bg-pf-bg-0 text-pf-text-primary border-pf-border',
        'focus:outline-none focus:ring-2 focus:ring-pf-accent focus:border-pf-accent',
        'disabled:bg-pf-disabled disabled:cursor-not-allowed',
        'placeholder:text-pf-text-tertiary',
        'resize-y min-h-[80px]',
        'transition',
        invalid && 'border-pf-error focus:ring-pf-error focus:border-pf-error',
        className
      )}
      {...rest}
    />
  );
};

export default Textarea;
