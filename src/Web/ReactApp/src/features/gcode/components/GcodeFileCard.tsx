import React from 'react';
import { 
  FolderIcon, 
  DocumentIcon, 
  ArrowDownTrayIcon, 
  TrashIcon,
  TagIcon,
  QueueListIcon
} from '@heroicons/react/24/outline';
import { 
  BeakerIcon,
  RectangleStackIcon
} from '@heroicons/react/24/solid';
import { NozzleIcon, BedIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { GcodeFile } from '@/types/api';
import { formatPrintTimeMinutes } from '@/common/utils/datetime';
import { useState } from 'react';
import { QueueGcodeModal } from './QueueGcodeModal';
import { TaggingModal } from '@/components/TaggingModal';

interface GcodeFileCardProps {
  file: GcodeFile;
  onNavigate?: (path: string) => void;
  onDownload?: (path: string) => void;
  onDelete?: () => void;
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
  const [isQueueOpen, setIsQueueOpen] = useState(false);
  const [isTaggingOpen, setIsTaggingOpen] = useState(false);
  return (
    <>
    <div className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-hidden hover:border-pf-accent hover:shadow-lg transition-all flex flex-col group min-h-0">
      {/* Thumbnail */}
      <div className="h-48 bg-pf-bg-2 relative flex items-center justify-center overflow-hidden shrink-0">
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
          {!file.isDirectory && (
            <>
              {/* Label: Value properties grouped at top */}
              <div className="space-y-1 border-t border-pf-border/50 pt-1">
                {file.fileSize != null && file.fileSize > 0 && (
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
                {file.extractedPrinterModel && (
                  <div className="flex justify-between gap-1">
                    <span>Printer:</span>
                    <span className="font-medium text-right truncate ml-2" title={file.extractedPrinterModel}>
                      {file.extractedPrinterModel}
                    </span>
                  </div>
                )}
                {file.extractedSlicerName && (
                  <div className="flex justify-between gap-1">
                    <span>Slicer:</span>
                    <span className="font-medium text-right truncate ml-2" title={`${file.extractedSlicerName}${file.extractedSlicerVersion ? ` ${file.extractedSlicerVersion}` : ''}`}>
                      {file.extractedSlicerName}
                      {file.extractedSlicerVersion && ` ${file.extractedSlicerVersion}`}
                    </span>
                  </div>
                )}
                {file.extractedPrintTime != null && file.extractedPrintTime > 0 && (
                  <div className="flex justify-between gap-1">
                    <span>Print time:</span>
                    <span className="font-medium text-right">
                      {formatPrintTimeMinutes(file.extractedPrintTime)}
                    </span>
                  </div>
                )}
              </div>

              {/* Icon-only properties at bottom */}
              {(file.extractedNozzleDiameter || file.extractedMaterial || file.extractedHotendTemp || file.extractedBedTemp) && (
                <div className="space-y-1 border-t border-pf-border/50 pt-1">
                  {/* Nozzle & Material row */}
                  {(file.extractedNozzleDiameter || file.extractedMaterial) && (
                    <div className="flex items-center gap-2">
                      {file.extractedNozzleDiameter && (
                        <div className="flex items-center gap-1 flex-1 min-w-0">
                          <RectangleStackIcon className="w-3.5 h-3.5 text-pf-text-tertiary shrink-0" />
                          <span className="truncate" title={`Nozzle: ${file.extractedNozzleDiameter}mm`}>
                            {file.extractedNozzleDiameter}mm
                          </span>
                        </div>
                      )}
                      {file.extractedMaterial && (
                        <div className="flex items-center gap-1 flex-1 min-w-0">
                          <BeakerIcon className="w-3.5 h-3.5 text-pf-text-tertiary shrink-0" />
                          <span className="truncate" title={`Material: ${file.extractedMaterial}`}>
                            {file.extractedMaterial}
                          </span>
                        </div>
                      )}
                    </div>
                  )}

                  {/* Hotend & Bed temp row */}
                  {(file.extractedHotendTemp || file.extractedBedTemp) && (
                    <div className="flex items-center gap-2">
                      {file.extractedHotendTemp && (
                        <div className="flex items-center gap-1 flex-1">
                          <NozzleIcon className="w-3.5 h-3.5 text-pf-error shrink-0" isOn={false} />
                          <span title={`Hotend: ${formatTemperature(file.extractedHotendTemp)}`}>
                            {formatTemperature(file.extractedHotendTemp)}
                          </span>
                        </div>
                      )}
                      {file.extractedBedTemp && (
                        <div className="flex items-center gap-1 flex-1">
                          <BedIcon className="w-3.5 h-3.5 text-pf-accent shrink-0" isOn={false} />
                          <span title={`Bed: ${formatTemperature(file.extractedBedTemp)}`}>
                            {formatTemperature(file.extractedBedTemp)}
                          </span>
                        </div>
                      )}
                    </div>
                  )}
                </div>
              )}
            </>
          )}

          {/* Directory: just show modified date */}
          {file.isDirectory && file.uploadedAt && (
            <div className="flex justify-between gap-1 border-t border-pf-border/50 pt-1">
              <span>Modified:</span>
              <span className="font-medium text-right">
                {new Date(file.uploadedAt).toLocaleDateString()}
              </span>
            </div>
          )}

          {/* Tags */}
          {!file.isDirectory && file.tags && file.tags.length > 0 && (
            <div className="flex flex-wrap gap-1 pt-1 border-t border-pf-border/50">
              {file.tags.map(tag => (
                <span
                  key={tag.id}
                  className="inline-block px-2 py-0.5 rounded-sm text-[10px] font-medium text-white"
                  style={{ backgroundColor: tag.color || 'var(--pf-accent)' }}
                  title={tag.description}
                >
                  {tag.name}
                </span>
              ))}
            </div>
          )}
        </div>

        {/* Actions */}
        <div className="flex gap-1.5">
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
                onClick={() => onDelete?.()}
                disabled={isDeleting}
                variant="danger"
                size="sm"
                className="flex-1"
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
                onClick={() => setIsTaggingOpen(true)}
                disabled={isDeleting}
                variant="secondary"
                size="sm"
                className="flex-1"
                title="Tag this file"
              >
                <TagIcon className="w-4 h-4" />
              </Button>
              <Button
                onClick={() => setIsQueueOpen(true)}
                disabled={isDeleting}
                variant="primary"
                size="sm"
                className="flex-1"
                title="Queue for Print"
              >
                <QueueListIcon className="w-4 h-4" />
              </Button>
              <Button
                onClick={() => onDelete?.()}
                disabled={isDeleting}
                variant="danger"
                size="sm"
                className="flex-1"
                title="Delete File"
              >
                <TrashIcon className="w-4 h-4" />
              </Button>
            </>
          )}
        </div>
      </div>
    </div>
    {isQueueOpen && (
      <QueueGcodeModal file={file} isOpen={isQueueOpen} onClose={(added) => { setIsQueueOpen(false); if (added) { /* maybe show toast later */ } }} />
    )}
    {isTaggingOpen && !file.isDirectory && (
      <TaggingModal
        objectId={file.id}
        objectType="GcodeFile"
        initialTags={file.tags || []}
        isOpen={isTaggingOpen}
        onClose={() => setIsTaggingOpen(false)}
      />
    )}
    </>
  );
};
