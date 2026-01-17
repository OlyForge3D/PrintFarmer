import { ReactNode } from 'react';
import { Checkbox } from '@/common/components/ui';
import { type FileItem } from '../types';

interface GridViewProps {
  files: FileItem[];
  selectedIds: string[];
  onToggle: (id: string) => void;
  onSelectAll: () => void;
  renderItemActions?: (file: FileItem) => ReactNode;
  renderMetadata?: (file: FileItem) => ReactNode;
  isBusy?: boolean;
}

const formatBytes = (bytes?: number) => {
  if (!bytes) return '—';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${(bytes / Math.pow(k, i)).toFixed(1)} ${sizes[i]}`;
};

export const GridView = ({
  files,
  selectedIds,
  onToggle,
  onSelectAll,
  renderItemActions,
  renderMetadata,
  isBusy,
}: GridViewProps) => {
  const isAllSelected = selectedIds.length > 0 && selectedIds.length === files.length;
  const isMixed = selectedIds.length > 0 && selectedIds.length !== files.length;

  return (
    <div
      className="flex flex-col gap-0 h-full bg-pf-bg-0 rounded-lg border border-pf-border overflow-hidden"
      role="region"
      aria-label="File grid view"
      aria-busy={isBusy}
    >
      {/* Toolbar */}
      <div className="border-b border-pf-border bg-pf-bg-1 px-4 py-3 flex items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <Checkbox
            aria-label="Select all files"
            checked={isAllSelected}
            aria-checked={isMixed ? 'mixed' : isAllSelected}
            onChange={() => onSelectAll()}
          />
          <span className="text-xs text-pf-text-secondary">
            {selectedIds.length > 0 ? `${selectedIds.length} selected` : `${files.length} items`}
          </span>
        </div>
      </div>

      {/* Grid */}
      <div className="flex-1 overflow-y-auto p-4">
        {files.length === 0 ? (
          <div className="flex items-center justify-center h-full text-pf-text-secondary">
            <p className="text-sm">No files found</p>
          </div>
        ) : (
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
            {files.map((file) => {
              const isSelected = selectedIds.includes(file.id);
              const fileExtension = file.fileName.split('.').pop()?.toUpperCase() || 'FILE';
              return (
                <div
                  key={file.id}
                  className={`rounded-lg border overflow-hidden transition-all focus-within:ring-2 focus-within:ring-pf-primary group ${
                    isSelected 
                      ? 'border-pf-primary bg-pf-primary/5 shadow-md' 
                      : 'border-pf-border bg-pf-bg-0 hover:shadow-md hover:border-pf-primary/50'
                  }`}
                  role="group"
                  aria-label={file.fileName}
                >
                  {/* Preview Section */}
                  <div className="relative bg-pf-bg-1 aspect-square flex items-center justify-center overflow-hidden">
                    {file.thumbnailUrl ? (
                      <img
                        src={file.thumbnailUrl}
                        alt={file.fileName}
                        className="w-full h-full object-contain p-2"
                      />
                    ) : (
                      <div className="flex flex-col items-center gap-1 text-pf-text-secondary">
                        <div className="text-2xl font-bold">{fileExtension[0]}</div>
                        <div className="text-xs">{fileExtension}</div>
                      </div>
                    )}
                    {/* Checkbox overlay - visible on hover or when selected */}
                    <div className={`absolute top-2 left-2 transition-opacity ${isSelected || !file.thumbnailUrl ? 'opacity-100' : 'opacity-0 group-hover:opacity-100'}`}>
                      <Checkbox
                        aria-label={`Select ${file.fileName}`}
                        checked={isSelected}
                        onChange={() => onToggle(file.id)}
                      />
                    </div>
                  </div>

                  {/* Content Section */}
                  <div className="p-3 flex flex-col gap-3 bg-pf-bg-0">
                    {/* File Name */}
                    <div className="min-w-0">
                      <h3 className="text-sm font-semibold text-pf-text line-clamp-2 break-words">
                        {file.fileName}
                      </h3>
                    </div>

                    {/* Metadata */}
                    <div className="space-y-1.5 text-xs border-t border-pf-border pt-2">
                      {/* Common properties */}
                      <div className="flex justify-between items-center gap-2">
                        <span className="text-pf-text-secondary">Type:</span>
                        <span className="text-pf-text font-medium">{fileExtension}</span>
                      </div>
                      <div className="flex justify-between items-center gap-2">
                        <span className="text-pf-text-secondary">Size:</span>
                        <span className="text-pf-text font-medium">{formatBytes(file.fileSize)}</span>
                      </div>
                      
                      {/* Extended metadata from parent */}
                      {renderMetadata?.(file)}
                    </div>

                    {/* Actions */}
                    {renderItemActions && (
                      <div className="flex gap-2 pt-1 border-t border-pf-border justify-center" aria-label={`Actions for ${file.fileName}`}>
                        {renderItemActions(file)}
                      </div>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
};