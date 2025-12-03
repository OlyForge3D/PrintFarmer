import { useState, useEffect } from 'react';
import { createPortal } from 'react-dom';
import { X, FileText, AlertCircle, Play, Copy, Trash2, Image, ArrowUpDown } from 'lucide-react';
import { Button, Select } from '@/components/ui';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import type { Printer, PrinterFileDto } from '@/types/api';

interface PrinterFilesModalProps {
  isOpen: boolean;
  onClose: () => void;
  printer: Printer;
}

type SortBy = 'name' | 'modified' | 'size';
type SortOrder = 'asc' | 'desc';

export function PrinterFilesModal({ isOpen, onClose, printer }: PrinterFilesModalProps) {
  const [files, setFiles] = useState<PrinterFileDto[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [copiedFile, setCopiedFile] = useState<string | null>(null);
  const [hoveredThumbnail, setHoveredThumbnail] = useState<string | null>(null);
  const [sortBy, setSortBy] = useState<SortBy>('name');
  const [sortOrder, setSortOrder] = useState<SortOrder>('asc');
  const [isDeleting, setIsDeleting] = useState<string | null>(null);
  const [confirmDialog, setConfirmDialog] = useState<{ type: 'print' | 'delete'; file: PrinterFileDto } | null>(null);

  useEffect(() => {
    if (isOpen && printer) {
      loadFiles();
    }
  }, [isOpen, printer]);

  const loadFiles = async () => {
    try {
      setIsLoading(true);
      setError(null);
      const fileList = await apiClient.getPrinterFileList(printer.id);
      setFiles(fileList || []);
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Failed to load files';
      setError(errorMessage);
      console.error('Error loading printer files:', err);
    } finally {
      setIsLoading(false);
    }
  };

  const handleQueueFile = async (fileName: string) => {
    const file = files.find(f => f.fileName === fileName);
    if (file) {
      setConfirmDialog({ type: 'print', file });
    }
  };

  const confirmPrintFile = async (fileName: string) => {
    setConfirmDialog(null);
    try {
      const success = await apiClient.startPrintFromFile(printer.id, fileName);
      if (success) {
        toast.success(`Started printing: ${fileName}`);
        onClose();
      } else {
        toast.error('Failed to start print - printer may not be ready');
      }
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Failed to start print';
      toast.error(errorMessage);
      console.error('Error starting print:', err);
    }
  };

  const handleCopyFilename = (fileName: string) => {
    navigator.clipboard.writeText(fileName);
    setCopiedFile(fileName);
    setTimeout(() => setCopiedFile(null), 2000);
  };

  const handleDeleteFile = async (fileName: string) => {
    const file = files.find(f => f.fileName === fileName);
    if (file) {
      setConfirmDialog({ type: 'delete', file });
    }
  };

  const confirmDeleteFile = async (fileName: string) => {
    setConfirmDialog(null);
    try {
      setIsDeleting(fileName);
      const success = await apiClient.deletePrinterFile(printer.id, fileName);
      if (success) {
        toast.success(`Deleted: ${fileName}`);
        // Reload the file list after deletion
        await loadFiles();
      } else {
        toast.error('Failed to delete file');
      }
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Failed to delete file';
      toast.error(errorMessage);
      console.error('Error deleting file:', err);
    } finally {
      setIsDeleting(null);
    }
  };;

  const getSortedFiles = () => {
    const sorted = [...files];
    
    sorted.sort((a, b) => {
      let aValue: string | number;
      let bValue: string | number;

      if (sortBy === 'name') {
        aValue = a.fileName.toLowerCase();
        bValue = b.fileName.toLowerCase();
      } else if (sortBy === 'modified') {
        aValue = a.modified ?? 0;
        bValue = b.modified ?? 0;
      } else if (sortBy === 'size') {
        aValue = a.sizeBytes ?? 0;
        bValue = b.sizeBytes ?? 0;
      } else {
        return 0;
      }

      if (aValue < bValue) return sortOrder === 'asc' ? -1 : 1;
      if (aValue > bValue) return sortOrder === 'asc' ? 1 : -1;
      return 0;
    });

    return sorted;
  };

  const toggleSortOrder = () => {
    setSortOrder(sortOrder === 'asc' ? 'desc' : 'asc');
  };

  const formatFileSize = (bytes?: number): string => {
    if (!bytes) return 'N/A';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  };

  const formatModifiedDate = (timestamp?: number): string => {
    if (!timestamp) return 'N/A';
    return new Date(timestamp * 1000).toLocaleDateString() + ' ' + new Date(timestamp * 1000).toLocaleTimeString();
  };

  if (!isOpen) {
    return null;
  }

  const modalContent = (
    <div className="fixed inset-0 z-50 overflow-y-auto">
      <div className="flex min-h-screen items-center justify-center p-4">
        <div className="fixed inset-0 bg-black bg-opacity-75" onClick={onClose} />
        
        <div className="relative bg-pf-bg-1 rounded-lg shadow-xl max-w-4xl w-full max-h-[80vh] flex flex-col">
          {/* Header */}
          <div className="flex items-center justify-between p-6 border-b border-pf-border">
            <div>
              <h2 className="text-xl font-semibold text-pf-text-primary">Printer Files</h2>
              <p className="text-sm text-pf-text-secondary mt-1">
                {printer.name} - Available G-code files
              </p>
            </div>
            
            <Button
              type="button"
              variant="subtle"
              size="sm"
              onClick={onClose}
              className="!p-2 !h-auto"
              title="Close"
            >
              <X className="h-5 w-5" />
            </Button>
          </div>

          {/* Content */}
          <div className="flex-1 overflow-y-auto p-6">
            {isLoading ? (
              <div className="flex items-center justify-center py-8">
                <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent"></div>
                <span className="ml-3 text-pf-text-secondary">Loading files...</span>
              </div>
            ) : error ? (
              <div className="text-center py-8">
                <AlertCircle className="h-12 w-12 text-red-500 mx-auto mb-4" />
                <h3 className="text-lg font-medium text-pf-text-primary mb-2">Failed to Load Files</h3>
                <p className="text-pf-text-secondary mb-4">{error}</p>
                <Button
                  type="button"
                  variant="primary"
                  onClick={loadFiles}
                >
                  Try Again
                </Button>
              </div>
            ) : !files || files.length === 0 ? (
              <div className="text-center py-8">
                <FileText className="h-12 w-12 text-pf-text-tertiary mx-auto mb-4" />
                <h3 className="text-lg font-medium text-pf-text-primary mb-2">No Files</h3>
                <p className="text-pf-text-secondary">
                  No G-code files found on this printer.
                </p>
              </div>
            ) : (
              <div>
                <div className="flex items-center justify-between mb-6">
                  <h3 className="text-lg font-medium text-pf-text-primary">
                    {files.length} File{files.length !== 1 ? 's' : ''}
                  </h3>
                  <div className="flex items-center gap-2">
                    <label htmlFor="sort-by" className="text-sm text-pf-text-secondary">Sort by:</label>
                    <Select
                      id="sort-by"
                      value={sortBy}
                      onChange={(e) => setSortBy(e.target.value as SortBy)}
                      className="text-sm"
                    >
                      <option value="name">Name</option>
                      <option value="modified">Date Modified</option>
                      <option value="size">File Size</option>
                    </Select>
                    <Button
                      type="button"
                      variant="subtle"
                      size="sm"
                      onClick={toggleSortOrder}
                      className="!p-2 !h-auto"
                      title={`Sort ${sortOrder === 'asc' ? 'descending' : 'ascending'}`}
                    >
                      <ArrowUpDown className={`h-4 w-4 ${sortOrder === 'desc' ? 'rotate-180' : ''}`} />
                    </Button>
                  </div>
                </div>

                <div className="space-y-2">
                  {getSortedFiles().map((file) => (
                    <div
                      key={file.fileName}
                      className="relative flex items-center justify-between bg-pf-bg-0 border border-pf-border rounded-lg p-4 hover:border-pf-accent transition-colors group"
                      onMouseEnter={() => file.thumbnailUrl && setHoveredThumbnail(file.fileName)}
                      onMouseLeave={() => setHoveredThumbnail(null)}
                    >
                      <div className="flex items-center flex-1 min-w-0 gap-3">
                        {file.thumbnailUrl ? (
                          <div className="relative h-12 w-12 flex-shrink-0 rounded bg-pf-bg-2 border border-pf-border overflow-hidden">
                            <img
                              src={file.thumbnailUrl}
                              alt={file.fileName}
                              className="h-full w-full object-cover"
                              onError={(e) => {
                                e.currentTarget.style.display = 'none';
                              }}
                            />
                          </div>
                        ) : (
                          <div className="h-12 w-12 flex-shrink-0 rounded bg-pf-bg-2 border border-pf-border flex items-center justify-center">
                            <Image className="h-6 w-6 text-pf-text-tertiary" />
                          </div>
                        )}
                        <div className="min-w-0 flex-1">
                          <p className="text-pf-text-primary break-words font-medium">{file.fileName}</p>
                          <div className="flex gap-4 text-xs text-pf-text-tertiary mt-1">
                            <span>G-code file</span>
                            {file.sizeBytes && <span>{formatFileSize(file.sizeBytes)}</span>}
                            {file.modified && <span>{formatModifiedDate(file.modified)}</span>}
                          </div>
                        </div>
                      </div>

                      <div className="flex items-center gap-2 flex-shrink-0 ml-2 opacity-0 group-hover:opacity-100 transition-opacity">
                        <Button
                          type="button"
                          variant="subtle"
                          size="sm"
                          onClick={() => handleQueueFile(file.fileName)}
                          className="!p-2 !h-auto"
                          title="Queue for printing"
                        >
                          <Play className="h-4 w-4" />
                        </Button>

                        <Button
                          type="button"
                          variant="subtle"
                          size="sm"
                          onClick={() => handleCopyFilename(file.fileName)}
                          className="!p-2 !h-auto"
                          title={copiedFile === file.fileName ? 'Copied!' : 'Copy filename'}
                        >
                          <Copy className="h-4 w-4" />
                        </Button>

                        <Button
                          type="button"
                          variant="subtle"
                          size="sm"
                          onClick={() => handleDeleteFile(file.fileName)}
                          disabled={isDeleting === file.fileName}
                          className="!p-2 !h-auto text-red-500 hover:text-red-600 disabled:opacity-50 disabled:cursor-not-allowed"
                          title={isDeleting === file.fileName ? 'Deleting...' : 'Delete file'}
                        >
                          {isDeleting === file.fileName ? (
                            <div className="h-4 w-4 border-2 border-red-500 border-t-transparent rounded-full animate-spin" />
                          ) : (
                            <Trash2 className="h-4 w-4" />
                          )}
                        </Button>
                      </div>

                      {/* Thumbnail Preview on Hover */}
                      {hoveredThumbnail === file.fileName && file.thumbnailUrl && (
                        <div className="absolute left-1/2 bottom-full mb-2 -translate-x-1/2 z-10 hidden group-hover:block">
                          <img
                            src={file.thumbnailUrl}
                            alt={file.fileName}
                            className="max-h-48 max-w-xs rounded shadow-lg border border-pf-border"
                            onError={(e) => {
                              e.currentTarget.style.display = 'none';
                            }}
                          />
                        </div>
                      )}
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>

          {/* Footer */}
          <div className="flex items-center justify-between p-6 border-t border-pf-border bg-pf-bg-0">
            <p className="text-sm text-pf-text-secondary">
              {files.length} file{files.length !== 1 ? 's' : ''} available on printer
            </p>
            
            <Button
              type="button"
              variant="secondary"
              onClick={onClose}
            >
              Close
            </Button>
          </div>
        </div>
      </div>
    </div>
  );

  // Confirmation dialog component
  const confirmDialogContent = confirmDialog && (
    <div className="fixed inset-0 z-[60] overflow-y-auto">
      <div className="flex min-h-screen items-center justify-center p-4">
        <div className="fixed inset-0 bg-black bg-opacity-75" onClick={() => setConfirmDialog(null)} />
        
        <div className="relative bg-pf-bg-1 rounded-lg shadow-xl max-w-md w-full">
          {/* Header */}
          <div className="flex items-center justify-between p-6 border-b border-pf-border">
            <h3 className="text-lg font-semibold text-pf-text-primary">
              {confirmDialog.type === 'print' ? 'Start Printing?' : 'Delete File?'}
            </h3>
            <Button
              type="button"
              variant="subtle"
              size="sm"
              onClick={() => setConfirmDialog(null)}
              className="!p-2 !h-auto"
            >
              <X className="h-5 w-5" />
            </Button>
          </div>

          {/* Content */}
          <div className="p-6 space-y-4">
            {/* Thumbnail Preview */}
            {confirmDialog.file.thumbnailUrl && (
              <div className="flex justify-center mb-4">
                <img
                  src={confirmDialog.file.thumbnailUrl}
                  alt={confirmDialog.file.fileName}
                  className="rounded-lg max-w-xs max-h-48 object-cover border border-pf-border"
                  onError={(e) => {
                    e.currentTarget.style.display = 'none';
                  }}
                />
              </div>
            )}

            {/* File Details */}
            <div className="bg-pf-bg-0 rounded p-3 space-y-2">
              <p className="text-sm text-pf-text-secondary">File:</p>
              <p className="text-pf-text-primary font-medium break-all">{confirmDialog.file.fileName}</p>
              
              {confirmDialog.file.sizeBytes && (
                <div>
                  <p className="text-sm text-pf-text-secondary">Size:</p>
                  <p className="text-pf-text-primary">{formatFileSize(confirmDialog.file.sizeBytes)}</p>
                </div>
              )}
              
              {confirmDialog.file.modified && (
                <div>
                  <p className="text-sm text-pf-text-secondary">Modified:</p>
                  <p className="text-pf-text-primary text-sm">{formatModifiedDate(confirmDialog.file.modified)}</p>
                </div>
              )}
            </div>

            {/* Confirmation Message */}
            <div className="bg-pf-bg-0 rounded p-3 border border-pf-border-warning">
              {confirmDialog.type === 'print' ? (
                <p className="text-sm text-pf-text-primary">
                  Start printing this file? The printer will begin the print job.
                </p>
              ) : (
                <p className="text-sm text-red-400">
                  Delete this file? This action cannot be undone.
                </p>
              )}
            </div>
          </div>

          {/* Footer */}
          <div className="flex items-center justify-end gap-3 p-6 border-t border-pf-border bg-pf-bg-0">
            <Button
              type="button"
              variant="secondary"
              onClick={() => setConfirmDialog(null)}
            >
              Cancel
            </Button>
            
            <Button
              type="button"
              variant={confirmDialog.type === 'print' ? 'primary' : 'danger'}
              onClick={() => {
                if (confirmDialog.type === 'print') {
                  confirmPrintFile(confirmDialog.file.fileName);
                } else {
                  confirmDeleteFile(confirmDialog.file.fileName);
                }
              }}
            >
              {confirmDialog.type === 'print' ? 'Start Printing' : 'Delete'}
            </Button>
          </div>
        </div>
      </div>
    </div>
  );

  return createPortal(
    <>
      {modalContent}
      {confirmDialogContent}
    </>,
    document.body
  );
}
