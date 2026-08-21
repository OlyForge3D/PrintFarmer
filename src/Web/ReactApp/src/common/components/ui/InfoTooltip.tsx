import React, { useEffect, useId, useRef, useState } from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui/Button';
import { InfoIcon } from '@/common/components/icons/MdiIcons';

export interface InfoTooltipProps {
  /** The descriptive content shown when the tooltip is open */
  content: React.ReactNode;
  /** Accessible name for the trigger button (announced to screen readers) */
  label?: string;
  /** Optional id override for the tooltip content element */
  id?: string;
  /** Additional className applied to the tooltip content panel */
  className?: string;
}

/**
 * A small, focusable "i" affordance that reveals supplemental help text on
 * demand instead of rendering it permanently inline. Opens on hover or
 * keyboard focus, is dismissible with `Escape`, and wires the trigger to the
 * tooltip content via `aria-describedby` so screen readers announce it.
 */
export const InfoTooltip: React.FC<InfoTooltipProps> = ({
  content,
  label = 'More information',
  id,
  className,
}) => {
  const generatedId = useId();
  const tooltipId = id ?? `info-tooltip-${generatedId}`;
  const [isVisible, setIsVisible] = useState(false);
  const buttonRef = useRef<HTMLButtonElement>(null);

  const show = () => setIsVisible(true);
  const hide = () => setIsVisible(false);

  useEffect(() => {
    if (!isVisible) return;

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.stopPropagation();
        hide();
        buttonRef.current?.focus();
      }
    };

    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [isVisible]);

  return (
    <span className="relative inline-flex" onMouseEnter={show} onMouseLeave={hide}>
      <Button
        ref={buttonRef}
        type="button"
        variant="ghost"
        aria-describedby={tooltipId}
        aria-expanded={isVisible}
        onFocus={show}
        onBlur={hide}
        className="aspect-square w-5 h-5 rounded-full p-0 text-pf-text-muted hover:text-pf-text-primary"
      >
        <InfoIcon className="w-3.5 h-3.5" ariaLabel="" />
        <span className="sr-only">{label}</span>
      </Button>
      <div
        id={tooltipId}
        role="tooltip"
        className={clsx(
          'absolute z-50 left-0 top-full mt-1.5 w-64 px-2.5 py-2 text-xs leading-snug rounded-md shadow-lg',
          'bg-pf-bg-2 text-pf-text-primary border border-pf-border',
          isVisible ? 'block' : 'hidden',
          className
        )}
      >
        {content}
      </div>
    </span>
  );
};

export default InfoTooltip;
