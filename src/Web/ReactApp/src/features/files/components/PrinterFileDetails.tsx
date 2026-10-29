import { Fragment, useId } from 'react';
import { Link } from 'react-router';
import type { PrinterFileCatalogEntry, PrinterFileCatalogSource } from '@/types/api';
import { FileDate } from '@/features/files/components/FileDate';
import { FileDetailPanel, type FileDetailRow } from '@/features/files/components/FileDetailPanel';
import { PrinterFileStatusGlyphs } from '@/features/files/components/FileGlyphs';
import { FileThumbnail } from '@/features/files/components/FileThumbnail';
import type { FileAction } from '@/features/files/components/FileActions';
import { printerMetadataRows } from '@/features/files/utils/printerMetadata';
import {
  canPreviewSource,
  unsupportedPreviewReason,
  usePrinterFilePreview,
} from '@/features/files/hooks/usePrinterFilePreview';
import {
  fileKindFromName,
  fileTypeLabel,
  formatLayerHeight,
  formatPrintSeconds,
  formatSizeLabel,
  parseProviderModified,
} from '@/features/files/utils/fileDisplay';

interface PrinterFileDetailsProps {
  entry: PrinterFileCatalogEntry;
  source: PrinterFileCatalogSource | undefined;
  actions: FileAction[];
  onClose: () => void;
}

/** Provider modification values are shown date-only when parseable, otherwise verbatim. */
export function PrinterFileModified({ value }: { value?: string | null }) {
  return <FileDate value={parseProviderModified(value)} fallback={value?.trim() || 'Unknown'} />;
}

/** Detail panel for one printer-resident observation, including its captured preview. */
export function PrinterFileDetails({ entry, source, actions, onClose }: PrinterFileDetailsProps) {
  const metadataHeadingId = useId();
  const previewAllowed = canPreviewSource(source);
  const fetchable = previewAllowed && entry.availability === 'Available';
  const { preview, thumbnail, isLoading, unavailableReason } = usePrinterFilePreview(entry, fetchable);
  const reason = !previewAllowed
    ? unsupportedPreviewReason(source)
    : entry.availability !== 'Available'
      ? 'the file is no longer available on the printer'
      : unavailableReason;
  const pending = 'Loading…';
  const hasDurableDetails = entry.detailsFetchedAtUtc != null || entry.metadata != null;
  const metadata = hasDurableDetails ? entry.metadata : preview?.metadata;
  const nativeRows = printerMetadataRows(metadata);
  const hasNativeDetails = hasDurableDetails || metadata != null;
  const metadataLoading = !hasDurableDetails && !preview && isLoading;
  const printTime = hasNativeDetails
    ? metadata?.printTimeSeconds ?? metadata?.estimatedPrintTimeSeconds
    : preview?.printTimeSeconds;
  const layerHeight = hasNativeDetails ? metadata?.layerHeightMm : preview?.layerHeightMm;
  const rows: FileDetailRow[] = [
    { label: 'Type', value: fileTypeLabel(entry.displayName) },
    { label: 'Size', value: formatSizeLabel(entry.sizeBytes ?? (hasDurableDetails ? undefined : preview?.sizeBytes)) },
    { label: 'Modified', value: <PrinterFileModified value={entry.modifiedValue} /> },
    { label: 'Path', value: `${entry.storageRoot}/${entry.relativePath}`, mono: true },
    { label: 'Source', value: entry.printerName },
    { label: 'Print time', value: metadataLoading ? pending
      : printTime === 0 ? '0 s' : printTime == null && hasNativeDetails ? 'Unknown' : formatPrintSeconds(printTime) },
    { label: 'Layer height', value: metadataLoading ? pending
      : layerHeight === 0 ? '0 mm' : layerHeight == null && hasNativeDetails ? 'Unknown' : formatLayerHeight(layerHeight) },
    { label: 'Status', value: <PrinterFileStatusGlyphs entry={entry} showLabels /> },
    { label: 'Last seen', value: <FileDate value={entry.lastSeenAtUtc} /> },
    { label: 'Last complete scan', value: <FileDate value={entry.lastCompleteScanAtUtc} /> },
    { label: 'Observation', value: `Revision ${entry.observationRevision}` },
  ];

  const previewNode = (
    <figure className="space-y-1.5">
      <FileThumbnail
        kind={fileKindFromName(entry.displayName)}
        size="panel"
        blob={thumbnail}
        unavailableReason={isLoading ? null : reason}
        loading={isLoading}
        alt={`Preview of ${entry.displayName}`}
      />
      <figcaption className="text-xs text-pf-text-secondary">
        {isLoading && <span role="status">Loading preview…</span>}
        {!isLoading && preview?.capturedAtUtc && <>Preview captured <FileDate value={preview.capturedAtUtc} /></>}
      </figcaption>
    </figure>
  );

  return (
    <FileDetailPanel title={entry.displayName} onClose={onClose} preview={previewNode} rows={rows} actions={actions}>
      <section aria-labelledby={metadataHeadingId} className="space-y-2 border-t border-pf-border pt-3 text-xs">
        <h3 id={metadataHeadingId} className="font-semibold text-pf-text-primary">Native print metadata</h3>
        {entry.detailsFetchedAtUtc && (
          <p className="text-pf-text-secondary">Saved in catalog <FileDate value={entry.detailsFetchedAtUtc} /></p>
        )}
        {nativeRows.length === 0 ? (
          <p className="text-pf-text-secondary">{metadataLoading ? pending : 'Unknown — no native metadata supplied.'}</p>
        ) : (
          <>
            <p className="text-pf-text-secondary">Only supplied fields are shown. Tool numbers follow provider order.</p>
            <dl className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)] gap-x-3 gap-y-1.5">
              {nativeRows.map(({ label, value }) => (
                <Fragment key={label}>
                  <dt className="text-pf-text-secondary">{label}</dt>
                  <dd className="min-w-0 wrap-anywhere text-pf-text-primary">{value}</dd>
                </Fragment>
              ))}
            </dl>
          </>
        )}
      </section>
      <div className="space-y-1 border-t border-pf-border pt-3 text-xs">
        <h3 className="font-semibold text-pf-text-primary">Captured local copies</h3>
        {entry.localCopies.length === 0 ? <p className="text-pf-text-secondary">None captured</p> : (
          <ul className="space-y-1">
            {entry.localCopies.map((local) => (
              <li key={`${local.gcodeFileId}-${local.capturedAtUtc}`}>
                <Link to={`/files?type=gcode&file=${encodeURIComponent(local.gcodeFileId)}`} className="text-pf-accent underline">Local copy</Link>
                {' · '}<FileDate value={local.capturedAtUtc} />
                {' · '}{local.verification === 'UnverifiedSnapshot' ? 'Captured snapshot' : local.verification}
              </li>
            ))}
          </ul>
        )}
        {entry.localCopies.length > 0 && <p className="text-pf-text-secondary">Not proof of current remote byte equality.</p>}
      </div>
      {source && !source.supportsCopy && (
        <p className="text-xs text-pf-text-secondary">Listing only: authenticated local staging is not supported by this backend.</p>
      )}
    </FileDetailPanel>
  );
}