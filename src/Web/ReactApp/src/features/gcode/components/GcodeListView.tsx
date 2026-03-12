import React from 'react';
import { FolderIcon, DocumentIcon, TrashIcon, ArrowDownTrayIcon } from '@heroicons/react/24/outline';
import { Button, Checkbox } from '@/common/components/ui';
import { NozzleIcon, BedIcon } from '@/common/components/icons/MdiIcons';
import { SelectableRow } from '@/common/components/Table/SelectableRow';
import { GcodeFile } from '@/types/api';

interface Formatters {
  formatBytes: (bytes: number) => string;
  formatDate: (date: string | Date) => string;
}

interface GcodeListViewProps {
  files: GcodeFile[];
  isLoading: boolean;
  selectedFiles: string[];
  onSelectFile: (file: GcodeFile) => void;
  onSelectAll: (files: GcodeFile[]) => void;
  onDelete: (file: GcodeFile) => void;
  onDownload: (file: GcodeFile) => void;
  onNavigate: (file: GcodeFile) => void;
  formatters: Formatters;
}

export const GcodeListView: React.FC<GcodeListViewProps> = ({
  files,
  isLoading,
  selectedFiles,
  onSelectFile,
  onSelectAll,
  onDelete,
  onDownload,
  onNavigate,
  formatters
}) => {
  const { formatBytes, formatDate } = formatters;

  if (isLoading && files.length === 0) {
    return (
      <div className="flex items-center justify-center h-full">
        <div className="text-center">
          <p className="text-pf-text-secondary">Loading files...</p>
        </div>
      </div>
    );
  }

  if (files.length === 0) {
    return (
      <div className="flex items-center justify-center h-full">
        <div className="text-center">
          <DocumentIcon className="w-12 h-12 text-pf-text-tertiary opacity-30 mx-auto mb-3" />
          <p className="text-pf-text-secondary">No files found</p>
        </div>
      </div>
    );
  }

  return (
    <div className="bg-pf-card rounded-lg border border-pf-border overflow-x-auto flex-1">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-pf-border bg-pf-bg-2 sticky top-0">
            <th className="px-4 py-3 text-left w-8">
              {files.length > 0 && (
                <Checkbox
                  title="Select all files"
                  aria-label="Select all files"
                  checked={selectedFiles.length === files.filter(f => !f.isDirectory).length && files.filter(f => !f.isDirectory).length > 0}
                  onChange={(e) => {
                    if (e.target.checked) {
                      onSelectAll(files.filter(f => !f.isDirectory));
                    } else {
                      onSelectAll([]);
                    }
                  }}
                />
              )}
            </th>
            <th className="px-4 py-3 text-left font-semibold text-pf-text-primary">Name</th>
            <th className="px-4 py-3 text-left font-semibold text-pf-text-primary w-24">Size</th>
            <th className="px-4 py-3 text-left font-semibold text-pf-text-primary w-24">Nozzle</th>
            <th className="px-4 py-3 text-left font-semibold text-pf-text-primary w-32">Material</th>
            <th className="px-4 py-3 text-left font-semibold text-pf-text-primary w-20">
              <span className="flex items-center gap-1">
                <NozzleIcon className="w-3.5 h-3.5 text-pf-error" isOn={false} />
                Hotend
              </span>
            </th>
            <th className="px-4 py-3 text-left font-semibold text-pf-text-primary w-20">
              <span className="flex items-center gap-1">
                <BedIcon className="w-3.5 h-3.5 text-pf-accent" isOn={false} />
                Bed
              </span>
            </th>
            <th className="px-4 py-3 text-left font-semibold text-pf-text-primary w-32">Printer Model</th>
            <th className="px-4 py-3 text-left font-semibold text-pf-text-primary w-40">Modified</th>
            <th className="px-4 py-3 text-right font-semibold text-pf-text-primary">Actions</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-pf-border">
          {files.map((file: GcodeFile) => (
            <SelectableRow key={file.path} isSelected={selectedFiles.includes(file.path)}>
              <td className="px-4 py-3">
                {!file.isDirectory && (
                  <Checkbox
                    checked={selectedFiles.includes(file.path)}
                    onChange={() => onSelectFile(file)}
                    title={`Select ${file.fileName}`}
                    aria-label={`Select ${file.fileName}`}
                  />
                )}
              </td>
              <td className="px-4 py-3">
                <div className="flex items-center gap-3 min-w-0">
                  {/* Thumbnail */}
                  <div className="w-12 h-12 shrink-0 rounded-sm bg-pf-bg-2 flex items-center justify-center border border-pf-border overflow-hidden">
                    {!file.isDirectory && file.thumbnailUrl ? (
                      <img
                        src={file.thumbnailUrl}
                        alt={file.fileName}
                        className="w-full h-full object-contain"
                        onError={(e) => {
                          e.currentTarget.src = 'data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iNDgiIGhlaWdodD0iNDgiIHZpZXdCb3g9IjAgMCA0OCA0OCIgZmlsbD0ibm9uZSIgeG1sbnM9Imh0dHA6Ly93d3cudzMub3JnLzIwMDAvc3ZnIj48cmVjdCB3aWR0aD0iNDgiIGhlaWdodD0iNDgiIGZpbGw9IiNFNUU3RUIiLz48cmVjdCB4PSI4IiB5PSI4IiB3aWR0aD0iMzIiIGhlaWdodD0iMzIiIHN0cm9rZT0iIzk1OTdiMCIgc3Ryb2tlLXdpZHRoPSIyIiBmaWxsPSJub25lIi8+PGNpcmNsZSBjeD0iMjQiIGN5PSIyNCIgcj0iMiIgZmlsbD0iIzk1OTdiMCIvPjwvc3ZnPg=='
                        }}
                      />
                    ) : file.isDirectory ? (
                      <FolderIcon className="w-6 h-6 text-pf-accent opacity-50" />
                    ) : (
                      <DocumentIcon className="w-6 h-6 text-pf-text-tertiary opacity-50" />
                    )}
                  </div>
                  {/* Filename */}
                  <div className="min-w-0">
                    <div
                      className={`font-medium text-pf-text-primary truncate ${
                        file.isDirectory ? 'cursor-pointer hover:text-pf-accent' : ''
                      }`}
                      onClick={() => {
                        if (file.isDirectory) {
                          onNavigate(file);
                        }
                      }}
                    >
                      {file.name || file.fileName}
                    </div>
                  </div>
                </div>
              </td>
              <td className="px-4 py-3 text-pf-text-secondary text-xs">
                {!file.isDirectory ? formatBytes(file.fileSize) : '—'}
              </td>
              <td className="px-4 py-3 text-pf-text-secondary text-xs">
                {file.isDirectory ? '—' : file.extractedNozzleDiameter ? `${file.extractedNozzleDiameter}mm` : '—'}
              </td>
              <td className="px-4 py-3 text-pf-text-secondary text-xs">
                {file.isDirectory ? '—' : file.extractedMaterial || '—'}
              </td>
              <td className="px-4 py-3 text-pf-text-secondary text-xs">
                {file.isDirectory ? '—' : file.extractedHotendTemp ? (
                  <span className="flex items-center gap-1">
                    <NozzleIcon className="w-3 h-3 text-pf-error" isOn={false} />
                    {Math.round(file.extractedHotendTemp)}°C
                  </span>
                ) : '—'}
              </td>
              <td className="px-4 py-3 text-pf-text-secondary text-xs">
                {file.isDirectory ? '—' : file.extractedBedTemp ? (
                  <span className="flex items-center gap-1">
                    <BedIcon className="w-3 h-3 text-pf-accent" isOn={false} />
                    {Math.round(file.extractedBedTemp)}°C
                  </span>
                ) : '—'}
              </td>
              <td className="px-4 py-3 text-pf-text-secondary text-xs">
                {file.isDirectory ? '—' : file.extractedPrinterModel || file.extractedPrinterModelName || '—'}
              </td>
              <td className="px-4 py-3 text-pf-text-secondary text-xs">
                {file.uploadedAt ? formatDate(String(file.uploadedAt)) : '—'}
              </td>
              <td className="px-4 py-3 text-right">
                {!file.isDirectory && (
                  <div className="flex justify-end gap-2">
                    <Button
                      onClick={() => onDownload(file)}
                      variant="subtle"
                      size="sm"
                      title="Download file"
                    >
                      <ArrowDownTrayIcon className="w-4 h-4" />
                    </Button>
                    <Button
                      onClick={() => onDelete(file)}
                      variant="danger"
                      size="sm"
                      title="Delete file"
                    >
                      <TrashIcon className="w-4 h-4" />
                    </Button>
                  </div>
                )}
              </td>
            </SelectableRow>
          ))}
        </tbody>
      </table>
    </div>
  );
};
