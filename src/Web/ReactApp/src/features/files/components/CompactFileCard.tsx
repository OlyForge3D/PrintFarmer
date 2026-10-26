import { useRef, type MouseEvent, type ReactNode } from 'react';
import clsx from 'clsx';
import { Checkbox } from '@/common/components/ui';
import { FileNameButton } from '@/features/files/components/FileNameButton';
import { FileActionIconButtons, type FileAction } from '@/features/files/components/FileActions';

interface CompactFileCardProps {
  name: string;
  thumbnail: ReactNode;
  /** Secondary line(s) under the name, e.g. size. */
  meta?: ReactNode;
  /** Glyph overlay at the top-right of the thumbnail. */
  status?: ReactNode;
  actions?: FileAction[];
  /** Bulk selection; omitted where the source has no bulk actions. */
  selection?: {
    checked: boolean;
    onToggle: () => void;
    /** True when no bulk action applies to this file; `title` explains why. */
    disabled?: boolean;
    title?: string;
  };
  /** True while this file is shown in the detail panel. */
  isActive?: boolean;
  onOpen: (invoker: HTMLElement | null) => void;
}

const INTERACTIVE_SELECTOR = 'button, a, input, label, select, textarea';

/** The compact card shared by the local library and printer-resident files. */
export function CompactFileCard({
  name, thumbnail, meta, status, actions = [], selection, isActive = false, onOpen,
}: CompactFileCardProps) {
  const nameButtonRef = useRef<HTMLButtonElement>(null);
  // Clicking anywhere non-interactive opens details; keyboard users use the name button.
  const handleCardClick = (event: MouseEvent<HTMLDivElement>) => {
    if (event.target instanceof Element && event.target.closest(INTERACTIVE_SELECTOR)) return;
    onOpen(nameButtonRef.current);
  };
  const selected = selection?.checked === true;

  return (
    <div
      data-file-card=""
      data-selected={selected || undefined}
      onClick={handleCardClick}
      className={clsx(
        'flex h-full cursor-pointer flex-col overflow-hidden rounded-lg border bg-pf-bg-0 text-xs transition-colors focus-within:ring-2 focus-within:ring-pf-accent',
        selected && 'ring-2 ring-pf-accent',
        selected || isActive ? 'border-pf-accent' : 'border-pf-border hover:border-pf-accent/50',
      )}
    >
      <div className="relative">
        {thumbnail}
        {selection && (
          <div className="absolute left-1.5 top-1.5 z-10 rounded-xs bg-pf-bg-0/80 p-0.5">
            <Checkbox
              aria-label={`Select ${name}`}
              checked={selection.checked}
              disabled={selection.disabled}
              title={selection.title}
              onChange={selection.onToggle}
            />
          </div>
        )}
        {status && <div className="absolute right-1.5 top-1.5 rounded-xs bg-pf-bg-0/80 px-1 py-0.5">{status}</div>}
      </div>
      <div className="flex min-w-0 flex-1 flex-col gap-0.5 p-2">
        <FileNameButton ref={nameButtonRef} name={name} onOpen={onOpen} />
        {meta && <div className="min-w-0 truncate text-pf-text-secondary">{meta}</div>}
        {actions.length > 0 && (
          <div role="group" aria-label={`Actions for ${name}`} className="mt-auto flex flex-wrap gap-1 pt-1.5">
            <FileActionIconButtons actions={actions} dense />
          </div>
        )}
      </div>
    </div>
  );
}