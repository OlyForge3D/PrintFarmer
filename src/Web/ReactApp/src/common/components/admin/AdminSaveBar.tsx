import { useCallback } from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
import { SaveIcon, CloseIcon } from '@/common/components/icons/MdiIcons';

export interface AdminSaveBarProps {
  /**
   * When true the bar is visible. When false the bar collapses to nothing (no
   * placeholder, no wasted vertical space). Wire this to a `useDirtyState` result.
   */
  isDirty: boolean;
  /**
   * Number of changed fields. Used to render the summary text
   * ("3 unsaved changes"). Optional — falls back to a generic label.
   */
  changeCount?: number;
  /**
   * Optional human-readable field labels of the changed items. When present, the
   * first few are enumerated in the summary ("Name, Email and 2 more changed").
   */
  changedLabels?: string[];
  /** Called when the user clicks the discard button. Typically wired to `state.reset`. */
  onDiscard: () => void;
  /** Called when the user clicks save. Can be async — the button shows a spinner while pending. */
  onSave: () => void | Promise<void>;
  /** While true, both buttons disable and save shows a loading spinner. */
  isSaving?: boolean;
  /**
   * Optional error message rendered above the actions. Prefer toasts for transient
   * failures; use this slot for inline validation-summary messages that should
   * remain visible while the user fixes the problem.
   */
  error?: string | null;
  /** Optional confirm label. Defaults to `Save changes`. */
  saveLabel?: string;
  /** Optional discard label. Defaults to `Discard`. */
  discardLabel?: string;
  /** Optional extra classes on the outer sticky container. */
  className?: string;
}

const MAX_ENUMERATED_LABELS = 3;

function formatSummary(changeCount: number | undefined, changedLabels: string[] | undefined): string {
  if (changedLabels && changedLabels.length > 0) {
    const shown = changedLabels.slice(0, MAX_ENUMERATED_LABELS);
    const remaining = changedLabels.length - shown.length;
    const list = shown.join(', ');
    if (remaining > 0) {
      return `${list} and ${remaining} more changed`;
    }
    if (shown.length === 1) {
      return `${list} changed`;
    }
    return `${list} changed`;
  }
  const count = changeCount ?? 0;
  if (count === 0) return 'Unsaved changes';
  if (count === 1) return '1 unsaved change';
  return `${count} unsaved changes`;
}

/**
 * Floating scoped save bar shown when a form has unsaved changes. Sticks to the
 * bottom of the containing scroll area, animates in when `isDirty` flips true,
 * and disappears entirely when clean so the form breathes.
 *
 * Pairs with `useDirtyState` for the state, and with `adminToast` for the
 * success/failure feedback after save completes.
 */
export function AdminSaveBar({
  isDirty,
  changeCount,
  changedLabels,
  onDiscard,
  onSave,
  isSaving = false,
  error,
  saveLabel = 'Save changes',
  discardLabel = 'Discard',
  className,
}: AdminSaveBarProps) {
  const handleSave = useCallback(() => {
    // Fire-and-forget — the parent owns the promise. We do NOT swallow errors:
    // they should surface via the `error` prop or a toast raised by the parent.
    void onSave();
  }, [onSave]);

  if (!isDirty) return null;

  const summary = formatSummary(changeCount, changedLabels);

  return (
    <div
      role="region"
      aria-label="Unsaved changes"
      className={clsx(
        'sticky bottom-0 z-40 mt-4',
        'border-t border-pf-border bg-pf-bg-0/95 backdrop-blur-sm',
        'shadow-[0_-8px_16px_-8px_rgba(0,0,0,0.35)]',
        className,
      )}
      data-testid="admin-save-bar"
    >
      {error && (
        <div
          role="alert"
          className="border-b border-pf-error/30 bg-pf-error-bg/70 px-4 py-2 text-sm text-pf-error-text"
        >
          {error}
        </div>
      )}
      <div
        className={clsx(
          'flex flex-col-reverse gap-3 px-4 py-3',
          'sm:flex-row sm:items-center sm:justify-between sm:gap-4',
        )}
      >
        <p
          className="text-sm text-pf-text-secondary min-w-0 truncate"
          aria-live="polite"
          aria-atomic="true"
        >
          <span
            className="inline-block w-2 h-2 rounded-full bg-pf-warning mr-2 align-middle"
            aria-hidden="true"
          />
          {summary}
        </p>
        <div className="flex items-center gap-2 shrink-0">
          <Button
            type="button"
            variant="secondary"
            size="sm"
            onClick={onDiscard}
            disabled={isSaving}
            iconLeft={<CloseIcon className="w-3.5 h-3.5" />}
          >
            {discardLabel}
          </Button>
          <Button
            type="button"
            variant="primary"
            size="sm"
            onClick={handleSave}
            loading={isSaving}
            iconLeft={!isSaving ? <SaveIcon className="w-3.5 h-3.5" /> : undefined}
          >
            {isSaving ? saveLabel : saveLabel}
          </Button>
        </div>
      </div>
    </div>
  );
}

export default AdminSaveBar;
