import { toValidDate } from '@/features/files/utils/fileDisplay';

interface FileDateProps {
  /** API timestamp (may omit the trailing `Z`) or an already-parsed date. */
  value: string | Date | null | undefined;
  /** Shown when the value is missing or unparseable. */
  fallback?: string;
  className?: string;
}

/**
 * File dates are shown date-only; the full localized timestamp stays available in the
 * `title` tooltip and the machine-readable `dateTime`. Sorting uses the raw timestamp.
 */
export function FileDate({ value, fallback = 'Unknown', className }: FileDateProps) {
  const date = toValidDate(value);
  if (!date) return <span className={className}>{fallback}</span>;
  return (
    <time dateTime={date.toISOString()} title={date.toLocaleString()} className={className}>
      {date.toLocaleDateString()}
    </time>
  );
}