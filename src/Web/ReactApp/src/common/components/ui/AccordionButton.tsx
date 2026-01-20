/* eslint-disable local/pf-no-raw-html-controls */
import React from 'react';
import clsx from 'clsx';
import { ChevronDownIcon, ChevronRightIcon } from '@/common/components/icons/MdiIcons';

export interface AccordionButtonProps {
  /** Whether the accordion is currently expanded */
  isExpanded: boolean;
  /** Click handler for toggling expansion */
  onClick: () => void;
  /** Main title/label for the accordion header */
  title: React.ReactNode;
  /** Optional badge to show next to the title (e.g., "Primary" indicator) */
  badge?: React.ReactNode;
  /** Optional summary info to show on the right side */
  summary?: React.ReactNode;
  /** Optional action buttons (rendered with stopPropagation) */
  actions?: React.ReactNode;
  /** Additional className for the button */
  className?: string;
  /** Size variant */
  size?: 'sm' | 'md' | 'lg';
  /** Whether the button is disabled */
  disabled?: boolean;
}

/**
 * AccordionButton - A full-width clickable header for accordion/collapsible sections.
 * 
 * Features:
 * - Expand/collapse chevron icon
 * - Title with optional badge
 * - Summary info on the right
 * - Action buttons that don't trigger expansion
 * 
 * @example
 * ```tsx
 * <AccordionButton
 *   isExpanded={isOpen}
 *   onClick={() => setIsOpen(!isOpen)}
 *   title="Toolhead 1"
 *   badge={<span className="badge">Primary</span>}
 *   summary={<span>Ø0.4mm • Max 300°C</span>}
 *   actions={<button onClick={handleDelete}>Delete</button>}
 * />
 * ```
 */
export const AccordionButton: React.FC<AccordionButtonProps> = ({
  isExpanded,
  onClick,
  title,
  badge,
  summary,
  actions,
  className,
  size = 'md',
  disabled = false,
}) => {
  const sizeClasses = {
    sm: 'p-2 text-sm',
    md: 'p-3',
    lg: 'p-4 text-lg',
  };

  const iconSizes = {
    sm: 'w-4 h-4',
    md: 'w-5 h-5',
    lg: 'w-6 h-6',
  };

  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={clsx(
        'w-full flex items-center justify-between',
        'bg-pf-bg-secondary hover:bg-pf-bg-tertiary',
        'transition-colors',
        'disabled:opacity-50 disabled:cursor-not-allowed disabled:hover:bg-pf-bg-secondary',
        sizeClasses[size],
        className
      )}
    >
      {/* Left side: Chevron + Title + Badge */}
      <div className="flex items-center space-x-3 min-w-0">
        {isExpanded ? (
          <ChevronDownIcon className={clsx(iconSizes[size], 'text-pf-text-secondary flex-shrink-0')} />
        ) : (
          <ChevronRightIcon className={clsx(iconSizes[size], 'text-pf-text-secondary flex-shrink-0')} />
        )}
        <span className="font-medium text-pf-text-primary truncate">
          {title}
        </span>
        {badge}
      </div>

      {/* Right side: Summary + Actions */}
      <div className="flex items-center space-x-4 text-sm text-pf-text-secondary flex-shrink-0">
        {summary}
        {actions && (
          <div
            onClick={(e) => e.stopPropagation()}
            onKeyDown={(e) => e.stopPropagation()}
            className="flex items-center"
          >
            {actions}
          </div>
        )}
      </div>
    </button>
  );
};

export default AccordionButton;
