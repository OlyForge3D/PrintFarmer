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
}

/**
 * Collapsible section with uniform header styling.
 * Supports both controlled (expanded + onToggle) and uncontrolled (defaultExpanded) usage.
 */
export function CollapsibleSection({
  title,
  children,
  defaultExpanded = true,
  headerActions,
  onToggle,
  expanded: controlledExpanded,
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

  return (
    <section aria-labelledby={headingId} className="flex flex-col gap-2">
      <div
        className="flex items-center justify-between gap-2 cursor-pointer"
        onClick={toggle}
        role="button"
        tabIndex={0}
        onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } }}
        aria-expanded={isExpanded}
        aria-controls={panelId}
      >
        <div id={headingId} className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
          {title}
        </div>
        <div className="flex items-center gap-1">
          {isExpanded && headerActions && (
            <div className="flex items-center gap-1" onClick={(e) => e.stopPropagation()}>
              {headerActions}
            </div>
          )}
          <ChevronDownIcon
            className={`h-4 w-4 text-pf-text-secondary transition-transform ${isExpanded ? '' : '-rotate-90'}`}
            ariaLabel={isExpanded ? `Collapse ${title}` : `Expand ${title}`}
          />
        </div>
      </div>
      {isExpanded && (
        <div id={panelId} role="region" aria-labelledby={headingId}>
          {children}
        </div>
      )}
    </section>
  );
}
