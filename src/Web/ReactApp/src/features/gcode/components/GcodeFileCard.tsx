import React from 'react';
import { 
  FolderIcon, 
  DocumentIcon, 
  ArrowDownTrayIcon, 
  TrashIcon 
} from '@heroicons/react/24/outline';
import { 
  CubeIcon,
  BeakerIcon,
  FireIcon,
  RectangleStackIcon
} from '@heroicons/react/24/solid';
import { Button } from '@/common/components/ui';
import { GcodeFile } from '@/types/api';
import { formatPrintTimeMinutes } from '@/common/utils/datetime';

interface GcodeFileCardProps {
  file: GcodeFile;
  onNavigate?: (path: string) => void;
  onDownload?: (path: string) => void;
  onDelete?: (path: string) => void;
  isDeleting?: boolean;
}

const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 Bytes';
  const k = 1024;
  const sizes = ['Bytes', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

const formatTemperature = (temp: number | undefined): string => {
  if (!temp) return 'N/A';
  return `${temp}°C`;
};

export const GcodeFileCard: React.FC<GcodeFileCardProps> = ({
  file,
  onNavigate,
  onDownload,
  onDelete,
  isDeleting = false
}) => {
  return (
    <div className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-hidden hover:border-pf-accent hover:shadow-lg transition-all flex flex-col group">
      {/* Thumbnail */}
      <div className="aspect-square bg-pf-bg-2 relative flex items-center justify-center min-h-32 overflow-hidden">
        {!file.isDirectory && file.thumbnailUrl ? (
          <img
            src={file.thumbnailUrl}
            alt={file.name}
            className="w-full h-full object-contain group-hover:scale-105 transition-transform"
          />
        ) : file.isDirectory ? (
          <FolderIcon className="w-12 h-12 text-pf-accent opacity-50" />
        ) : (
          <DocumentIcon className="w-12 h-12 text-pf-text-tertiary opacity-30" />
        )}
      </div>

      {/* File info */}
      <div className="p-2.5 flex-1 flex flex-col">
        <h3 className="font-semibold text-pf-text-primary line-clamp-2 mb-1.5 text-sm">
          {file.name}
        </h3>

        {/* Metadata */}
        <div className="text-xs text-pf-text-secondary space-y-1 mb-2 flex-1">
          {/* Basic file info */}
          {!file.isDirectory && file.fileSize && (
            <div className="flex justify-between gap-1">
              <span>Size:</span>
              <span className="font-medium text-right">{formatBytes(file.fileSize)}</span>
            </div>
          )}
          {file.uploadedAt && (
            <div className="flex justify-between gap-1">
              <span>Modified:</span>
              <span className="font-medium text-right">
                {new Date(file.uploadedAt).toLocaleDateString()}
              </span>
            </div>
          )}

          {/* Extracted metadata with icons */}
          {!file.isDirectory && (
            <>
              {/* Printer Model */}
              {file.extractedPrinterModel && (
                <div className="flex items-center gap-1.5 pt-1 border-t border-pf-border/50">
                  <CubeIcon className="w-3.5 h-3.5 text-pf-accent flex-shrink-0" />
                  <span className="truncate" title={file.extractedPrinterModel}>
                    {file.extractedPrinterModel}
                  </span>
                </div>
              )}

              {/* Nozzle Size & Material on same row */}
              {(file.extractedNozzleDiameter || file.extractedMaterial) && (
                <div className="flex items-center gap-2">
                  {file.extractedNozzleDiameter && (
                    <div className="flex items-center gap-1 flex-1 min-w-0">
                      <RectangleStackIcon className="w-3.5 h-3.5 text-pf-text-tertiary flex-shrink-0" />
                      <span className="truncate" title={`Nozzle: ${file.extractedNozzleDiameter}mm`}>
                        {file.extractedNozzleDiameter}mm
                      </span>
                    </div>
                  )}
                  {file.extractedMaterial && (
                    <div className="flex items-center gap-1 flex-1 min-w-0">
                      <BeakerIcon className="w-3.5 h-3.5 text-pf-text-tertiary flex-shrink-0" />
                      <span className="truncate" title={`Material: ${file.extractedMaterial}`}>
                        {file.extractedMaterial}
                      </span>
                    </div>
                  )}
                </div>
              )}

              {/* Temperatures on same row */}
              {(file.extractedHotendTemp || file.extractedBedTemp) && (
                <div className="flex items-center gap-2">
                  {file.extractedHotendTemp && (
                    <div className="flex items-center gap-1 flex-1">
                      <FireIcon className="w-3.5 h-3.5 text-orange-500 flex-shrink-0" />
                      <span title={`Hotend: ${formatTemperature(file.extractedHotendTemp)}`}>
                        {formatTemperature(file.extractedHotendTemp)}
                      </span>
                    </div>
                  )}
                  {file.extractedBedTemp && (
                    <div className="flex items-center gap-1 flex-1">
                      <RectangleStackIcon className="w-3.5 h-3.5 text-blue-500 flex-shrink-0" />
                      <span title={`Bed: ${formatTemperature(file.extractedBedTemp)}`}>
                        {formatTemperature(file.extractedBedTemp)}
                      </span>
                    </div>
                  )}
                </div>
              )}

              {/* Slicer info */}
              {file.extractedSlicerName && (
                <div className="flex items-center gap-1.5 text-[11px] text-pf-text-tertiary">
                  <span className="truncate" title={`Slicer: ${file.extractedSlicerName}${file.extractedSlicerVersion ? ` ${file.extractedSlicerVersion}` : ''}`}>
                    {file.extractedSlicerName}
                    {file.extractedSlicerVersion && ` ${file.extractedSlicerVersion}`}
                  </span>
                </div>
              )}

              {/* Print time */}
              {file.extractedPrintTime && (
                <div className="flex justify-between gap-1 text-[11px]">
                  <span>Print time:</span>
                  <span className="font-medium">
                    {formatPrintTimeMinutes(file.extractedPrintTime)}
                  </span>
                </div>
              )}
            </>
          )}
        </div>

        {/* Actions */}
        <div className="flex gap-2">
          {file.isDirectory ? (
            <>
              <Button
                onClick={() => onNavigate?.(file.path)}
                variant="primary"
                size="sm"
                className="flex-1"
                title="Open Folder"
              >
                <FolderIcon className="w-4 h-4" />
              </Button>
              <Button
                onClick={() => onDelete?.(file.path)}
                disabled={isDeleting}
                variant="danger"
                size="sm"
                className="px-2"
                title="Delete Folder"
              >
                <TrashIcon className="w-4 h-4" />
              </Button>
            </>
          ) : (
            <>
              <Button
                onClick={() => onDownload?.(file.path)}
                disabled={isDeleting}
                variant="secondary"
                size="sm"
                className="flex-1"
                title="Download File"
              >
                <ArrowDownTrayIcon className="w-4 h-4" />
              </Button>
              <Button
                onClick={() => onDelete?.(file.path)}
                disabled={isDeleting}
                variant="danger"
                size="sm"
                className="px-2"
                title="Delete File"
              >
                <TrashIcon className="w-4 h-4" />
              </Button>
            </>
          )}
        </div>
      </div>
    </div>
  );
};
