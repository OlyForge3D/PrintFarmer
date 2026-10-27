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
 * Curated print settings, the per-tool filament table and any other supplied native fields for
 * one printer file. Every value comes from the server snapshot; unknown is shown as "—" and is
 * never replaced by a default.
 */
export function PrinterPrintSettings({ metadata, status, savedAtUtc }: PrinterPrintSettingsProps) {
  const headingId = useId();
  const otherHeadingId = useId();
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
        </dl>
        {tools.length > 0 && (
          <table className="w-full table-fixed border-collapse text-left">
            <caption className="pb-1 text-left font-semibold text-pf-text-primary">Filament by tool</caption>
            <thead className="text-pf-text-secondary">
              <tr className="border-b border-pf-border">
                <th scope="col" className="py-1 pr-2 font-medium">Tool</th>
                <th scope="col" className="py-1 pr-2 font-medium">Color</th>
                <th scope="col" className="py-1 pr-2 font-medium">Material</th>
                <th scope="col" className="py-1 font-medium">Used</th>
              </tr>
            </thead>
            <tbody className="text-pf-text-primary">
              {tools.map((tool) => (
                <tr key={tool.key} className="border-b border-pf-border last:border-b-0">
                  <th scope="row" className="py-1 pr-2 text-left font-normal wrap-anywhere">{tool.label}</th>
                  <td className="py-1 pr-2">
                    <span className="inline-flex min-w-0 items-center gap-1.5">
                      {/* Decorative: the hex text beside it carries the color, never color alone. */}
                      <span
                        aria-hidden="true"
                        data-testid="tool-color-swatch"
                        className={clsx(
                          'inline-block h-3 w-3 shrink-0 rounded-xs border border-pf-border',
                          !tool.color && 'border-dashed',
                        )}
                        style={tool.color ? { backgroundColor: tool.color } : undefined}
                      />
                      {tool.color ? <span className="font-mono wrap-anywhere">{tool.color}</span> : <UnknownValue />}
                    </span>
                  </td>
                  <td className="py-1 pr-2 wrap-anywhere">{tool.material ?? <UnknownValue />}</td>
                  <td className="py-1 wrap-anywhere">{tool.used ?? <UnknownValue />}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        {otherRows.length > 0 && (
          <section aria-labelledby={otherHeadingId} className="space-y-1.5">
            <h4 id={otherHeadingId} className="font-semibold text-pf-text-primary">Other reported fields</h4>
            <p className="text-pf-text-secondary">Only supplied fields are shown. Tool numbers follow provider order.</p>
            <dl className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)] gap-x-3 gap-y-1.5">
              {otherRows.map(({ label, value }) => (
                <Fragment key={label}>
                  <dt className="text-pf-text-secondary">{label}</dt>
                  <dd className="min-w-0 wrap-anywhere text-pf-text-primary">{value}</dd>
                </Fragment>
              ))}
            </dl>
          </section>
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
