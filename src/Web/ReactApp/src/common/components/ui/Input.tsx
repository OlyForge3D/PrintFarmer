import React from 'react';
import clsx from 'clsx';

export interface InputProps extends React.InputHTMLAttributes<HTMLInputElement> {
  invalid?: boolean;
}

export const Input = React.forwardRef<HTMLInputElement, InputProps>(function Input(
  { invalid, className, type, onWheel, ...rest },
  ref
) {
  // Chrome (and other Chromium browsers) increments/decrements a focused
  // number input's value on mouse-wheel scroll instead of letting the scroll
  // bubble to a scrollable ancestor (e.g. a modal body). That silently changes
  // the field's value and makes it look like scrolling is "stuck", since the
  // page/modal never actually scrolls. Blurring the input on wheel restores
  // normal scroll behavior without altering the value the user last typed.
  const handleWheel = (event: React.WheelEvent<HTMLInputElement>) => {
    if (type === 'number') {
      event.currentTarget.blur();
    }
    onWheel?.(event);
  };

  return (
    <input
      ref={ref}
      type={type}
      onWheel={handleWheel}
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
