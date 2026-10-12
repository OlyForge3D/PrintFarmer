import React, { useState, useRef, useEffect, useLayoutEffect, useId, useCallback } from 'react';
import clsx from 'clsx';

export type TooltipPosition = 'top' | 'bottom' | 'left' | 'right';

export interface TooltipProps {
  /** The content to show in the tooltip */
  content: React.ReactNode;
  /** The element that triggers the tooltip */
  children: React.ReactNode;
  /** Position of the tooltip relative to the trigger */
  position?: TooltipPosition;
  /** Delay before showing tooltip on hover (ms). Keyboard focus shows it immediately. */
  delay?: number;
  /** Whether the tooltip is disabled */
  disabled?: boolean;
  /** Additional className for the tooltip */
  className?: string;
  /** Additional className for the wrapper around the trigger */
  wrapperClassName?: string;
  /**
   * `describe` (default) exposes the tooltip as the trigger's accessible
   * description. Use `hidden` when the text only repeats the trigger's
   * accessible name, so assistive technology does not announce it twice.
   */
  ariaMode?: 'describe' | 'hidden';
}

const positionClasses: Record<TooltipPosition, string> = {
  top: 'bottom-full left-1/2 -translate-x-1/2 mb-2',
  bottom: 'top-full left-1/2 -translate-x-1/2 mt-2',
  left: 'right-full top-1/2 -translate-y-1/2 mr-2',
  right: 'left-full top-1/2 -translate-y-1/2 ml-2',
};

const arrowClasses: Record<TooltipPosition, string> = {
  top: 'top-full left-1/2 -translate-x-1/2 border-t-pf-bg-2 border-x-transparent border-b-transparent',
  bottom: 'bottom-full left-1/2 -translate-x-1/2 border-b-pf-bg-2 border-x-transparent border-t-transparent',
  left: 'left-full top-1/2 -translate-y-1/2 border-l-pf-bg-2 border-y-transparent border-r-transparent',
  right: 'right-full top-1/2 -translate-y-1/2 border-r-pf-bg-2 border-y-transparent border-l-transparent',
};

const EDGE_MARGIN = 4;

/** Horizontal bounds the tooltip must stay inside: the viewport, narrowed by any clipping ancestor. */
function horizontalBounds(from: HTMLElement): { left: number; right: number } {
  let left = 0;
  let right = window.innerWidth || document.documentElement.clientWidth;
  for (let node = from.parentElement; node && node !== document.body; node = node.parentElement) {
    const overflowX = window.getComputedStyle(node).overflowX;
    if (overflowX && overflowX !== 'visible') {
      const rect = node.getBoundingClientRect();
      if (rect.width > 0) {
        left = Math.max(left, rect.left);
        right = Math.min(right, rect.right);
      }
    }
  }
  return { left, right };
}

export const Tooltip: React.FC<TooltipProps> = ({
  content,
  children,
  position = 'top',
  delay = 200,
  disabled = false,
  className,
  wrapperClassName,
  ariaMode = 'describe',
}) => {
  const [isVisible, setIsVisible] = useState(false);
  const timeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const triggerRef = useRef<HTMLDivElement>(null);
  const tooltipRef = useRef<HTMLDivElement>(null);
  const arrowRef = useRef<HTMLDivElement>(null);
  const tooltipId = useId();
  // Focus that follows a pointer press is mouse/touch focus; anything else
  // (Tab, arrow-key roving, programmatic focus) is shown without delay.
  const pointerPressRef = useRef(false);

  const clearTimer = () => {
    if (timeoutRef.current) {
      clearTimeout(timeoutRef.current);
      timeoutRef.current = null;
    }
  };

  const showAfterDelay = () => {
    if (disabled) return;
    clearTimer();
    timeoutRef.current = setTimeout(() => {
      setIsVisible(true);
    }, delay);
  };

  const showOnFocus = () => {
    if (disabled) return;
    if (pointerPressRef.current) {
      pointerPressRef.current = false;
      showAfterDelay();
      return;
    }
    clearTimer();
    setIsVisible(true);
  };

  const hideTooltip = useCallback(() => {
    clearTimer();
    setIsVisible(false);
  }, []);

  useEffect(() => clearTimer, []);

  // WCAG 1.4.13: the tooltip is dismissible without moving pointer or focus.
  // Escape is consumed while the tooltip is open so it does not also close the
  // surrounding dialog or drawer.
  useEffect(() => {
    if (!isVisible) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.stopPropagation();
      hideTooltip();
    };
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, [isVisible, hideTooltip]);

  // Keep horizontally placed tooltips inside the viewport and any clipping
  // container (e.g. the narrow printer sidebar) by sliding them along the axis.
  // The offset is applied straight to the DOM so measuring never re-renders.
  useLayoutEffect(() => {
    const node = tooltipRef.current;
    if (!isVisible || !node) return;
    const arrow = arrowRef.current;
    node.style.marginLeft = '';
    if (arrow) arrow.style.marginLeft = '';
    if (position !== 'top' && position !== 'bottom') return;
    const rect = node.getBoundingClientRect();
    if (rect.width === 0) return;
    const bounds = horizontalBounds(node);
    let next = 0;
    if (rect.right > bounds.right - EDGE_MARGIN) next = bounds.right - EDGE_MARGIN - rect.right;
    if (rect.left + next < bounds.left + EDGE_MARGIN) next = bounds.left + EDGE_MARGIN - rect.left;
    if (next === 0) return;
    node.style.marginLeft = `${next}px`;
    if (arrow) arrow.style.marginLeft = `${-next}px`;
  }, [isVisible, position, content]);

  const showContent = isVisible && !!content;
  const describes = showContent && ariaMode === 'describe';

  let trigger = children;
  if (describes && React.isValidElement<{ 'aria-describedby'?: string }>(children)) {
    const existing = children.props['aria-describedby'];
    trigger = React.cloneElement(children, {
      'aria-describedby': existing ? `${existing} ${tooltipId}` : tooltipId,
    });
  }

  return (
    <div
      ref={triggerRef}
      className={clsx('relative inline-flex', wrapperClassName)}
      onMouseEnter={showAfterDelay}
      onMouseLeave={hideTooltip}
      onPointerDown={() => { pointerPressRef.current = true; }}
      onPointerUp={() => { pointerPressRef.current = false; }}
      onFocus={showOnFocus}
      onBlur={hideTooltip}
    >
      {trigger}
      {showContent && (
        <div
          ref={tooltipRef}
          id={tooltipId}
          className={clsx(
            'absolute z-50 px-2 py-1 text-xs font-medium rounded-sm shadow-lg',
            'bg-pf-bg-2 text-pf-text-primary border border-pf-border',
            'whitespace-nowrap pointer-events-none',
            'animate-in fade-in-0 zoom-in-95 duration-150',
            positionClasses[position],
            className
          )}
          role="tooltip"
          aria-hidden={ariaMode === 'hidden' ? true : undefined}
        >
          {content}
          <div
            ref={arrowRef}
            className={clsx(
              'absolute w-0 h-0 border-4',
              arrowClasses[position]
            )}
          />
        </div>
      )}
    </div>
  );
};

export default Tooltip;
