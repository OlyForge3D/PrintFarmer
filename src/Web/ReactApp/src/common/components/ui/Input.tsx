import React, { useCallback, useEffect, useState } from 'react';
import clsx from 'clsx';

export interface InputProps extends React.InputHTMLAttributes<HTMLInputElement> {
  invalid?: boolean;
}

export const Input = React.forwardRef<HTMLInputElement, InputProps>(function Input(
  { invalid, className, type, ...rest },
  ref
) {
  // Tracked in state (rather than a plain ref) so the wheel-listener effect
  // below re-runs whenever the underlying DOM node changes, not just when
  // `type` changes.
  const [inputNode, setInputNode] = useState<HTMLInputElement | null>(null);

  // Chrome (and other Chromium browsers) increments/decrements a focused
  // number input's value on mouse-wheel scroll instead of letting the scroll
  // bubble to a scrollable ancestor (e.g. a modal body), and re-focuses the
  // field as part of that native default action. That silently changes the
  // field's value and makes it look like scrolling is "stuck" or that focus
  // is stuck on the field (issues #1708 and #1745).
  //
  // React's `onWheel` prop is attached as a *passive* listener, so calling
  // `preventDefault()`/`blur()` from it cannot reliably win the race against
  // Chromium's native default action, which can run before (or regardless
  // of) a passive handler. A real, non-passive `wheel` listener attached
  // directly to the DOM node runs to completion — and can cancel the default
  // action — before Chromium applies it, so it reliably stops the value
  // change and lets focus move away, restoring normal scroll behavior
  // without altering the value the user last typed.
  useEffect(() => {
    if (!inputNode || type !== 'number') {
      return;
    }

    const node = inputNode;
    const handleNativeWheel = (event: WheelEvent) => {
      // Only intercept while this field is actually focused — Chromium's
      // increment/refocus default action only applies to a focused number
      // input. If we prevented the default unconditionally, the browser's
      // whole scroll gesture (including bubbling to a scrollable ancestor,
      // like the modal body) would be cancelled every time the cursor
      // happened to be over an unfocused number input, leaving the page
      // unable to scroll past it at all.
      if (document.activeElement !== node) {
        return;
      }

      event.preventDefault();
      node.blur();
    };

    node.addEventListener('wheel', handleNativeWheel, { passive: false });
    return () => node.removeEventListener('wheel', handleNativeWheel);
  }, [inputNode, type]);

  const setRefs = useCallback(
    (node: HTMLInputElement | null) => {
      // Only update state (and thus re-run the wheel-listener effect below)
      // when the node instance actually changes. Without this guard, a new
      // `setRefs` identity — or React's callback-ref detach/attach dance on
      // rerender — would otherwise cause spurious `null -> node` updates and
      // corresponding spurious detach/attach calls on any externally
      // forwarded callback ref.
      setInputNode((current) => (current === node ? current : node));
      if (typeof ref === 'function') {
        ref(node);
      } else if (ref) {
        (ref as React.MutableRefObject<HTMLInputElement | null>).current = node;
      }
    },
    [ref]
  );

  return (
    <input
      ref={setRefs}
      type={type}
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
