import { Link } from 'react-router';
import { InfoTooltip } from '@/common/components/ui/InfoTooltip';
import type { PrinterFileCatalogEntry, PrinterFileCatalogSource } from '@/types/api';
import { FileDate } from '@/features/files/components/FileDate';
import { FileDetailPanel, type FileDetailRow } from '@/features/files/components/FileDetailPanel';
import { PrinterFileStatusGlyphs } from '@/features/files/components/FileGlyphs';
import { FileThumbnail } from '@/features/files/components/FileThumbnail';
import type { FileAction } from '@/features/files/components/FileActions';
import { PrinterPrintSettings, type PrintSettingsStatus } from '@/features/files/components/PrinterPrintSettings';
import {
  canPreviewSource,
  unsupportedPreviewReason,
  usePrinterFilePreview,
} from '@/features/files/hooks/usePrinterFilePreview';
import {
  describeReasonCode,
  fileKindFromName,
  fileTypeLabel,
  formatLayerHeight,
  formatPrintSeconds,
  formatSizeLabel,
  isCollectingState,
  parseProviderModified,
} from '@/features/files/utils/fileDisplay';

const LOCAL_COPY_NOTE = 'Not proof of current remote byte equality.';
const LISTING_ONLY_NOTE = 'Listing only: authenticated local staging is not supported by this backend.';

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
  const previewAllowed = canPreviewSource(source);
  const available = entry.availability === 'Available';
  const fetchable = previewAllowed && available;
  const { preview, thumbnail, isLoading, unavailableReason, thumbnailCollecting } = usePrinterFilePreview(entry, fetchable);
  const reason = !previewAllowed
    ? unsupportedPreviewReason(source)
    : !available
      ? 'the file is no longer available on the printer'
      : unavailableReason;
  const pending = 'Loading…';
  const collectingValue = 'Collecting…';
  const hasDurableDetails = entry.detailsFetchedAtUtc != null || entry.metadata != null;
  const metadata = hasDurableDetails ? entry.metadata : preview?.metadata;
  const hasNativeDetails = hasDurableDetails || metadata != null;
  const metadataLoading = !hasDurableDetails && !preview && isLoading;
  // The polled catalog row is the freshest status; the preview answer covers older catalogs.
  const metadataState = entry.metadataState ?? preview?.metadataState;
  // Missing files are never captured, so "collecting" only applies to files still on the printer.
  const metadataCollecting = available && isCollectingState(metadataState);
  const settingsStatus: PrintSettingsStatus = metadataLoading ? { kind: 'loading' }
    : metadataCollecting ? { kind: 'collecting' }
      : preview?.isStale === true ? { kind: 'stale' }
        : metadataState === 'Failed' ? { kind: 'failed', reason: describeReasonCode(preview?.metadataReason) }
          : { kind: 'settled' };
  const printTime = hasNativeDetails
    ? metadata?.printTimeSeconds ?? metadata?.estimatedPrintTimeSeconds
    : preview?.printTimeSeconds;
  const layerHeight = hasNativeDetails ? metadata?.layerHeightMm : preview?.layerHeightMm;
  const rows: FileDetailRow[] = [
    { label: 'Type', value: fileTypeLabel(entry.displayName) },
    { label: 'Size', value: formatSizeLabel(entry.sizeBytes ?? (hasDurableDetails ? undefined : preview?.sizeBytes)) },
    { label: 'Modified', value: <PrinterFileModified value={entry.modifiedValue} /> },
    { label: 'Path', value: `${entry.storageRoot}/${entry.relativePath}`, mono: true },
    { label: 'Source', value: source && !source.supportsCopy ? (
      <span className="inline-flex items-center gap-1">
        {entry.printerName}
        <InfoTooltip label="Listing only" content={LISTING_ONLY_NOTE} className="left-auto! right-0" />
      </span>
    ) : entry.printerName },
    { label: 'Print time', value: metadataLoading ? pending
      : printTime === 0 ? '0 s' : printTime == null && metadataCollecting ? collectingValue
        : printTime == null && hasNativeDetails ? 'Unknown' : formatPrintSeconds(printTime) },
    { label: 'Layer height', value: metadataLoading ? pending
      : layerHeight === 0 ? '0 mm' : layerHeight == null && metadataCollecting ? collectingValue
        : layerHeight == null && hasNativeDetails ? 'Unknown' : formatLayerHeight(layerHeight) },
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
        collecting={!isLoading && thumbnailCollecting}
        loading={isLoading}
        alt={`Preview of ${entry.displayName}`}
      />
      <figcaption className="text-xs text-pf-text-secondary">
        {isLoading && <span role="status">Loading preview…</span>}
        {!isLoading && preview?.capturedAtUtc && <>Preview captured <FileDate value={preview.capturedAtUtc} /></>}
        {!isLoading && thumbnail && preview?.isStale === true && (
          <span className="block">Showing an earlier preview while a refresh is pending.</span>
        )}
      </figcaption>
    </figure>
  );

  return (
    <FileDetailPanel title={entry.displayName} onClose={onClose} preview={previewNode} rows={rows} actions={actions}>
      {entry.localCopies.length > 0 && (
        <div className="flex items-start gap-1 text-xs">
          <ul aria-label="Local copies" className="min-w-0 flex-1 space-y-1">
            {entry.localCopies.map((local) => (
              <li key={`${local.gcodeFileId}-${local.capturedAtUtc}`}>
                <Link to={`/files?type=gcode&file=${encodeURIComponent(local.gcodeFileId)}`} className="text-pf-accent underline">Local copy</Link>
                {' · '}<FileDate value={local.capturedAtUtc} />
                {' · '}{local.verification === 'UnverifiedSnapshot' ? 'Captured snapshot' : local.verification}
              </li>
            ))}
          </ul>
          <InfoTooltip label="About local copies" content={LOCAL_COPY_NOTE} className="left-auto! right-0" />
        </div>
      )}
      <PrinterPrintSettings metadata={metadata} status={settingsStatus} savedAtUtc={entry.detailsFetchedAtUtc} />
    </FileDetailPanel>
  );
}