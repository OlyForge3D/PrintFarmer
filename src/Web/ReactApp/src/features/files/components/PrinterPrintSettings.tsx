import { Fragment, useId } from 'react';
import clsx from 'clsx';
import type { PrinterPrintMetadata } from '@/types/api';
import { FileDate } from '@/features/files/components/FileDate';
import { COLLECTING_DETAILS } from '@/features/files/utils/fileDisplay';
import { filamentToolRows, printSettingRows, printerMetadataRows } from '@/features/files/utils/printerMetadata';

/** Where the metadata snapshot is in its background capture lifecycle. */
export type PrintSettingsStatus =
  | { kind: 'loading' | 'collecting' | 'stale' | 'settled' }
  | { kind: 'failed'; reason: string | null };

interface PrinterPrintSettingsProps {
  metadata: PrinterPrintMetadata | null | undefined;
  status: PrintSettingsStatus;
  /** When the durable details were saved in the catalog, if known. */
  savedAtUtc?: string | null;
}

/** Unknown values: a visual dash, announced as "Unknown" instead of "em dash". */
function UnknownValue() {
  return (
    <>
      <span aria-hidden="true">—</span>
      <span className="sr-only">Unknown</span>
    </>
  );
}

function statusNote(status: PrintSettingsStatus, hasValues: boolean): string | null {
  switch (status.kind) {
    case 'collecting':
      return hasValues ? `${COLLECTING_DETAILS} Earlier values are shown until it finishes.` : COLLECTING_DETAILS;
    case 'stale':
      return 'Showing earlier details while a refresh is pending.';
    case 'failed':
      return status.reason ? `Some details could not be read: ${status.reason}.` : 'Some details could not be read.';
    default:
      return null;
  }
}

/**
 * Curated print settings, any other supplied native fields and compact per-tool filament cards
 * for one printer file. Every value comes from the server snapshot; unknown is shown as "—" and is
 * never replaced by a default.
 */
export function PrinterPrintSettings({ metadata, status, savedAtUtc }: PrinterPrintSettingsProps) {
  const headingId = useId();
  const settings = printSettingRows(metadata);
  const tools = filamentToolRows(metadata);
  const otherRows = printerMetadataRows(metadata);
  const hasValues = settings.some((row) => row.value != null) || tools.length > 0 || otherRows.length > 0;
  const note = statusNote(status, hasValues);

  let body;
  if (!hasValues) {
    body = status.kind === 'collecting' ? null : (
      <p className="text-pf-text-secondary">
        {status.kind === 'loading' ? 'Loading…' : 'Unknown — no native metadata supplied.'}
      </p>
    );
  } else {
    body = (
      <>
        <dl className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)] gap-x-3 gap-y-1.5">
          {settings.map(({ label, value }) => (
            <Fragment key={label}>
              <dt className="text-pf-text-secondary">{label}</dt>
              <dd className="min-w-0 wrap-anywhere text-pf-text-primary">{value ?? <UnknownValue />}</dd>
            </Fragment>
          ))}
          {otherRows.map(({ label, value }) => (
            <Fragment key={`other-${label}`}>
              <dt className="text-pf-text-secondary">{label}</dt>
              <dd className="min-w-0 wrap-anywhere text-pf-text-primary">{value}</dd>
            </Fragment>
          ))}
        </dl>
        {tools.length > 0 && (
          <ul aria-label="Filament by tool" className="flex flex-wrap gap-1.5 pt-1">
            {tools.map((tool) => (
              <li
                key={tool.key}
                className="inline-flex min-w-0 max-w-full items-center gap-1.5 rounded-sm border border-pf-border px-2 py-1 text-pf-text-primary"
                title={tool.color ?? undefined}
              >
                <span
                  aria-hidden="true"
                  data-testid="tool-color-swatch"
                  className={clsx(
                    'inline-block h-3 w-3 shrink-0 rounded-xs border border-pf-border',
                    !tool.color && 'border-dashed',
                  )}
                  style={tool.color ? { backgroundColor: tool.color } : undefined}
                />
                <span className="sr-only">{`${tool.label}, color ${tool.color ?? 'unknown'}, `}</span>
                {tool.material && <span className="wrap-anywhere">{tool.material}</span>}
                <span className="wrap-anywhere">{tool.used ?? <UnknownValue />}</span>
              </li>
            ))}
          </ul>
        )}
      </>
    );
  }

  return (
    <section aria-labelledby={headingId} className="space-y-2 border-t border-pf-border pt-3 text-xs">
      <h3 id={headingId} className="font-semibold text-pf-text-primary">Print settings</h3>
      {/* Always mounted so a later "Collecting…" or "earlier details" change is announced politely. */}
      <p role="status" className={clsx('text-pf-text-secondary', !note && 'sr-only')}>{note}</p>
      {savedAtUtc && (
        <p className="text-pf-text-secondary">Saved in catalog <FileDate value={savedAtUtc} /></p>
      )}
      {body}
    </section>
  );
}
