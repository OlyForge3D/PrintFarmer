import { TagChip } from '@/common/components/ui';
import { FileDate } from '@/features/files/components/FileDate';
import { FileDetailPanel, type FileDetailRow } from '@/features/files/components/FileDetailPanel';
import { FileThumbnail } from '@/features/files/components/FileThumbnail';
import type { FileAction } from '@/features/files/components/FileActions';
import { PrinterPrintSettings } from '@/features/files/components/PrinterPrintSettings';
import {
  FILE_KIND_LABEL,
  formatLayerHeight,
  formatPrintMinutes,
  formatSizeLabel,
  type FileKind,
} from '@/features/files/utils/fileDisplay';
import { printSettingRows } from '@/features/files/utils/printerMetadata';
import type { FileItem } from '@/features/fileBrowser/types';
import type { GcodeFile, PrinterPrintMetadata } from '@/types/api';

interface LocalFileDetailsProps {
  file: FileItem;
  kind: FileKind;
  /** Lowercase extension without the dot; empty when unknown. */
  extension: string;
  actions: FileAction[];
  onClose: () => void;
}

/** Detail panel content for an owned local-library asset. */
export function LocalFileDetails({ file, kind, extension, actions, onClose }: LocalFileDetailsProps) {
  const gcode = file.meta?.gcode as GcodeFile | undefined;
  // Map the library's extracted G-code fields onto the on-printer settings shape so both panels
  // read the same; settings the library does not extract are omitted rather than shown as unknown.
  const printMetadata: PrinterPrintMetadata | null = gcode ? {
    slicerName: gcode.extractedSlicerName,
    slicerVersion: gcode.extractedSlicerVersion,
    printerModel: gcode.extractedPrinterModel || gcode.extractedPrinterModelName,
    nozzleDiameterMm: gcode.extractedNozzleDiameter,
    nozzleTemperatureC: gcode.extractedHotendTemp,
    bedTemperatureC: gcode.extractedBedTemp,
    infillPercentage: gcode.extractedInfill,
    filamentType: gcode.extractedMaterial,
    filamentUsedMm: gcode.extractedFilamentLength,
  } : null;
  const hasPrintSettings = printSettingRows(printMetadata).some((row) => row.value != null);
  const rows: FileDetailRow[] = [
    { label: 'Type', value: extension ? `${FILE_KIND_LABEL[kind]} (${extension.toUpperCase()})` : FILE_KIND_LABEL[kind] },
    { label: 'Size', value: formatSizeLabel(file.fileSize) },
    // The library only records when a file was added, so the date is labelled as such.
    { label: 'Uploaded', value: <FileDate value={file.uploadedAt} /> },
    { label: 'Path', value: file.path || '/', mono: true },
    { label: 'Source', value: 'Local library' },
    // Extracted G-code metadata is in minutes; models have none, so both read "Unavailable".
    { label: 'Print time', value: formatPrintMinutes(gcode?.extractedPrintTime) },
    { label: 'Layer height', value: formatLayerHeight(gcode?.extractedLayerHeight) },
    // The extracted material is listed as "Filament type" under the print settings.
    ...(gcode?.requiredMaterial ? [{ label: 'Material', value: gcode.requiredMaterial }] : []),
  ];

  return (
    <FileDetailPanel
      title={file.fileName}
      onClose={onClose}
      preview={(
        <FileThumbnail
          kind={kind}
          size="panel"
          src={file.thumbnailUrl}
          alt={file.thumbnailUrl ? `Preview of ${file.fileName}` : ''}
        />
      )}
      rows={rows}
      actions={actions}
    >
      {file.tags && file.tags.length > 0 && (
        <div>
          <h3 className="text-xs font-semibold text-pf-text-primary">Tags</h3>
          <ul className="mt-1 flex flex-wrap gap-1">
            {file.tags.map((tag) => (
              <li key={tag.id}>
                <TagChip label={tag.name} color={tag.color} size="sm" />
              </li>
            ))}
          </ul>
        </div>
      )}
      {hasPrintSettings && (
        <PrinterPrintSettings metadata={printMetadata} status={{ kind: 'settled' }} hideUnknown />
      )}
    </FileDetailPanel>
  );
}