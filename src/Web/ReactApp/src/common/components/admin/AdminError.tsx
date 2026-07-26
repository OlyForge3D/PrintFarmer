import { useId } from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
import { AlertCircleIcon, RefreshIcon } from '@/common/components/icons/MdiIcons';

export interface AdminErrorProps {
  /** Human-friendly headline. Default: `Something went wrong`. */
  title?: string;
  /**
   * Optional paragraph shown under the title. Explain what failed and, if you can,
   * how the user might recover. Keep it short.
   */
  description?: string;
  /**
   * The underlying error object or string. When present, its message and any
   * available stack are rendered inside a collapsible `<details>` disclosure so
   * they don't dominate the layout but stay recoverable for support.
   */
  error?: unknown;
  /**
   * Called when the user clicks the retry button. Omit to hide the button — some
   * failures aren't retryable and offering a dead button is worse than none.
   */
  onRetry?: () => void;
  /** Label for the retry button. Defaults to `Try again`. */
  retryLabel?: string;
  /**
   * Density preset. `default` gives generous padding for full-page failures;
   * `compact` fits inside a card, tab, or column body.
   */
  size?: 'default' | 'compact';
  className?: string;
}

function stringifyError(error: unknown): string {
  if (error == null) return '';
  if (error instanceof Error) {
    const parts = [error.message];
    if (error.stack) parts.push(error.stack);
    return parts.filter(Boolean).join('\n\n');
  }
  if (typeof error === 'string') return error;
  try {
    return JSON.stringify(error, null, 2);
  } catch {
    return String(error);
  }
}

/**
 * Unified error state for admin surfaces. Replaces the four existing bespoke
 * error patterns, including the one that used `window.alert()`.
 *
 * The raw error detail is always hidden behind a disclosure so it doesn't shout
 * at the user, but is one click away for anyone diagnosing the failure.
 */
export function AdminError({
  title = 'Something went wrong',
  description,
  error,
  onRetry,
  retryLabel = 'Try again',
  size = 'default',
  className,
}: AdminErrorProps) {
  const detailsId = useId();
  const errorText = stringifyError(error);
  const hasErrorDetail = errorText.length > 0;
  const paddingClass = size === 'compact' ? 'p-4' : 'p-6';

  return (
    <div
      role="alert"
      className={clsx(
        'rounded-md border border-pf-error/30 bg-pf-error-bg',
        paddingClass,
        className,
      )}
    >
      <div className="flex items-start gap-3">
        <div className="text-pf-error mt-0.5 shrink-0" aria-hidden="true">
          <AlertCircleIcon className={size === 'compact' ? 'w-5 h-5' : 'w-6 h-6'} />
        </div>
        <div className="flex-1 min-w-0">
          <h3 className="text-sm font-semibold text-pf-error-text">{title}</h3>
          {description && (
            <p className="mt-1 text-sm text-pf-text-secondary">{description}</p>
          )}
          {hasErrorDetail && (
            <details className="mt-3 group">
              <summary
                aria-controls={detailsId}
                className="text-xs text-pf-text-secondary cursor-pointer inline-flex items-center gap-1 hover:text-pf-text-primary focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent rounded-sm"
              >
                <span className="group-open:hidden">Show details</span>
                <span className="hidden group-open:inline">Hide details</span>
              </summary>
              <div
                id={detailsId}
                className="mt-2 text-xs text-pf-text-secondary bg-pf-bg-2 border border-pf-border rounded-sm p-3 overflow-x-auto whitespace-pre-wrap break-words max-h-64 font-mono"
              >
                {errorText}
              </div>
            </details>
          )}
          {onRetry && (
            <div className="mt-4 flex flex-wrap gap-2">
              <Button
                variant="secondary"
                size="sm"
                onClick={onRetry}
                iconLeft={<RefreshIcon className="w-3.5 h-3.5" />}
              >
                {retryLabel}
              </Button>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

export default AdminError;
