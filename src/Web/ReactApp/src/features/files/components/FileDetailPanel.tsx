import { Fragment, useEffect, useEffectEvent, useId, useRef, type ReactNode } from 'react';
import clsx from 'clsx';
import { CloseIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { useIsLgBreakpoint } from '@/common/hooks/useMediaQuery';
import { FileActionList, type FileAction } from '@/features/files/components/FileActions';

export interface FileDetailRow {
  label: string;
  value: ReactNode;
  /** Monospace + wrap anywhere (paths, identifiers). */
  mono?: boolean;
}

interface FileDetailPanelProps {
  title: string;
  onClose: () => void;
  preview: ReactNode;
  rows: FileDetailRow[];
  actions: FileAction[];
  children?: ReactNode;
}

function FileDetailBody({ preview, rows, actions, children }: Omit<FileDetailPanelProps, 'title' | 'onClose'>) {
  return (
    <div className="space-y-4 text-sm">
      {preview}
      <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1.5 text-xs">
        {rows.map((row) => (
          <Fragment key={row.label}>
            <dt className="text-pf-text-secondary">{row.label}</dt>
            <dd className={clsx('min-w-0 text-pf-text-primary', row.mono && 'break-all font-mono')}>{row.value}</dd>
          </Fragment>
        ))}
      </dl>
      {actions.length > 0 && <FileActionList actions={actions} />}
      {children}
    </div>
  );
}

function DockedFileDetailPanel({ title, onClose, ...body }: FileDetailPanelProps) {
  const headingId = useId();
  const closeButtonRef = useRef<HTMLButtonElement>(null);

  // Escape closes the docked panel, but never steals Escape from an open dialog.
  const handleKeyDown = useEffectEvent((event: KeyboardEvent) => {
    if (event.key !== 'Escape' || event.defaultPrevented) return;
    if (document.querySelector('[aria-modal="true"]')) return;
    onClose();
  });
  useEffect(() => {
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, []);
  // The panel is keyed per file by its owner, so this runs once per opened file.
  useEffect(() => {
    closeButtonRef.current?.focus();
  }, []);

  return (
    <aside
      aria-labelledby={headingId}
      className="sticky top-4 flex max-h-[calc(100vh-2rem)] w-80 shrink-0 flex-col self-start overflow-hidden rounded-lg border border-pf-border bg-pf-bg-0"
    >
      <div className="flex items-start justify-between gap-2 border-b border-pf-border px-4 py-3">
        <h2 id={headingId} className="min-w-0 break-all text-base font-semibold text-pf-text-primary">{title}</h2>
        <Button
          ref={closeButtonRef}
          type="button"
          size="sm"
          variant="subtle"
          aria-label="Close details"
          title="Close details"
          onClick={onClose}
          iconCenter={<CloseIcon className="h-4 w-4" />}
        />
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto p-4">
        <FileDetailBody {...body} />
      </div>
    </aside>
  );
}

/**
 * Right-side file details: docked beside the listing on wide screens, a dialog on narrow
 * screens. Owners should key it by file so focus moves to the panel for every new file.
 */
export function FileDetailPanel(props: FileDetailPanelProps) {
  const docked = useIsLgBreakpoint();
  if (docked) return <DockedFileDetailPanel {...props} />;
  const { title, onClose, ...body } = props;
  return (
    <Modal isOpen onClose={onClose} title={title} size="md" closeAriaLabel="Close details">
      <FileDetailBody {...body} />
    </Modal>
  );
}