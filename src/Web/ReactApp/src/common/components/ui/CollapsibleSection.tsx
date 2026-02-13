import { useState, useId, type ReactNode } from 'react';
import { ChevronDownIcon } from '@/common/components/icons/MdiIcons';

interface CollapsibleSectionProps {
  /** Section title displayed in header */
  title: string;
  /** Content rendered when expanded */
  children: ReactNode;
  /** Whether section starts expanded */
  defaultExpanded?: boolean;
  /** Optional action buttons rendered in the header row (right side) */
  headerActions?: ReactNode;
  /** Called when section is expanded/collapsed */
  onToggle?: (expanded: boolean) => void;
  /** Override internal expanded state for controlled usage */
  expanded?: boolean;
  /** Title shown when collapsed (e.g. "Control and Quick Access"). When expanded, the regular title is shown instead. */
  collapsedTitle?: string;
  /** When true, hides the title row below the line when expanded (useful when content has its own inline headers) */
  hideExpandedTitle?: boolean;
}

/**
 * Collapsible section with a horizontal line divider header.
 * Supports both controlled (expanded + onToggle) and uncontrolled (defaultExpanded) usage.
 *
 * When expanded: icon + line, then title below the line, then content
 * When collapsed: icon + collapsedTitle (or title) on the line
 */
export function CollapsibleSection({
  title,
  children,
  defaultExpanded = true,
  headerActions,
  onToggle,
  expanded: controlledExpanded,
  collapsedTitle,
  hideExpandedTitle,
}: CollapsibleSectionProps) {
  const [internalExpanded, setInternalExpanded] = useState(defaultExpanded);
  const isControlled = controlledExpanded !== undefined;
  const isExpanded = isControlled ? controlledExpanded : internalExpanded;
  const generatedId = useId();
  const headingId = `section-heading-${generatedId}`;
  const panelId = `section-panel-${generatedId}`;

  const toggle = () => {
    const next = !isExpanded;
    if (!isControlled) {
      setInternalExpanded(next);
    }
    onToggle?.(next);
  };

  const collapsedLabel = collapsedTitle ?? title;

  return (
    <section aria-labelledby={headingId} className="flex flex-col gap-2">
      <div
        className="flex items-center gap-2 cursor-pointer"
        onClick={toggle}
        role="button"
        tabIndex={0}
        onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } }}
        aria-expanded={isExpanded}
        aria-controls={panelId}
      >
        <ChevronDownIcon
          className={`h-3.5 w-3.5 text-pf-text-tertiary shrink-0 transition-transform ${isExpanded ? '' : '-rotate-90'}`}
          ariaLabel={isExpanded ? `Collapse ${title}` : `Expand ${title}`}
        />
        {!isExpanded && (
          <span id={headingId} className="text-[10px] uppercase text-pf-text-secondary font-bold tracking-wide whitespace-nowrap">
            {collapsedLabel}
          </span>
        )}
        <div className="flex-1 border-t border-pf-border" />
      </div>
      {isExpanded && (
        <>
          {!hideExpandedTitle && (
            <div className="flex items-center justify-between">
              <span id={headingId} className="text-[10px] uppercase text-pf-text-secondary font-bold tracking-wide">
                {title}
              </span>
              {headerActions && (
                <div className="flex items-center gap-1 shrink-0" onClick={(e) => e.stopPropagation()}>
                  {headerActions}
                </div>
              )}
            </div>
          )}
          {hideExpandedTitle && headerActions && (
            <div className="flex justify-end">
              <div className="flex items-center gap-1 shrink-0" onClick={(e) => e.stopPropagation()}>
                {headerActions}
              </div>
            </div>
          )}
          <div id={panelId} role="region" aria-labelledby={headingId}>
            {children}
          </div>
        </>
      )}
    </section>
  );
}
