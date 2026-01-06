import { useState, useEffect, useCallback } from 'react';
import { createPortal } from 'react-dom';
import { DeleteIcon, TextIcon, AlertIcon, PlayIcon, CopyIcon, ImageIcon, SortIcon, DownloadIcon, SaveIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { Button, Select } from '@/common/components/ui';
import { Modal, ConfirmationModal } from '@/common/components/modals';
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
  const [selectedThumbnail, setSelectedThumbnail] = useState<{ fileName: string; url: string } | null>(null);
  const [sortBy, setSortBy] = useState<SortBy>('name');
  const [sortOrder, setSortOrder] = useState<SortOrder>('asc');
  const [isDeleting, setIsDeleting] = useState<string | null>(null);
  const [isDownloading, setIsDownloading] = useState<string | null>(null);
  const [isHarvesting, setIsHarvesting] = useState<string | null>(null);
  const [confirmDialog, setConfirmDialog] = useState<{ type: 'print' | 'delete'; file: PrinterFileDto } | null>(null);

  const loadFiles = useCallback(async () => {
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
  }, [printer.id]);

  useEffect(() => {
    if (isOpen) {
      loadFiles();
    }
  }, [isOpen, printer.id, loadFiles]);

  // Handle ESC key to close modal
  useEffect(() => {
    if (!isOpen) return;

    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
      }
    };

    document.addEventListener('keydown', handleEscape);
    return () => document.removeEventListener('keydown', handleEscape);
  }, [isOpen, onClose]);

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

  const handleDownloadFile = async (fileName: string) => {
    try {
      setIsDownloading(fileName);
      // Create a download link for the file
      // The API should provide a file download endpoint
      const downloadUrl = `/api/printers/${printer.id}/files/download?filename=${encodeURIComponent(fileName)}`;
      const a = document.createElement('a');
      a.href = downloadUrl;
      a.download = fileName.split('/').pop() || 'download';
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      toast.success(`Downloading: ${fileName}`);
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Failed to download file';
      toast.error(errorMessage);
      console.error('Error downloading file:', err);
    } finally {
      setIsDownloading(null);
    }
  };

  const handleHarvestFile = async (fileName: string) => {
    try {
      setIsHarvesting(fileName);
      // Start a harvest operation for this specific file
      const operation = await apiClient.harvestSingleFile(printer.id, fileName);
      toast.success(`Started harvesting: ${fileName}`);
      
      // Reload files after a brief delay to allow harvest to complete
      // The harvest is queued for background processing
      setTimeout(() => {
        loadFiles();
      }, 2000);
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Failed to start harvest';
      toast.error(errorMessage);
      console.error('Error harvesting file:', err);
    } finally {
      setIsHarvesting(null);
    }
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
        <div className="fixed inset-0 bg-black/50 backdrop-blur-sm" />
        
        <div className="relative bg-pf-bg-1 rounded-lg shadow-xl max-w-4xl w-full max-h-[80vh] flex flex-col">
          {/* Header */}
          <div className="flex items-center justify-between p-6 border-b border-pf-border">
            <div>
              <h2 className="text-xl font-semibold text-pf-text-primary">Printer Files</h2>
              <p className="text-sm text-pf-text-secondary mt-1">
                {printer.name} - Available G-code files
              </p>
            </div>
            
            {/* Close handled by Modal */}
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
                <AlertIcon className="h-12 w-12 text-red-500 mx-auto mb-4" />
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
                <TextIcon className="h-12 w-12 text-pf-text-tertiary mx-auto mb-4" />
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
                      aria-label={`Sort ${sortOrder === 'asc' ? 'descending' : 'ascending'}`}
                      iconCenter={<SortIcon className={`h-4 w-4 ${sortOrder === 'desc' ? 'rotate-180' : ''}`} />}
                    ></Button>
                  </div>
                </div>

                <div className="space-y-2">
                  {getSortedFiles().map((file) => (
                    <div
                      key={file.fileName}
                      className="relative flex items-center justify-between bg-pf-bg-1 border border-pf-border rounded-lg p-4 hover:border-pf-accent transition-colors group"
                    >
                      <div className="flex items-center flex-1 min-w-0 gap-3">
                        {file.thumbnailUrl ? (
                          <div 
                            className="relative h-12 w-12 flex-shrink-0 rounded bg-pf-bg-2 border border-pf-border overflow-hidden cursor-pointer hover:opacity-80 transition-opacity"
                            onClick={() => setSelectedThumbnail({ fileName: file.fileName, url: file.thumbnailUrl! })}
                          >
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
                            <ImageIcon className="h-6 w-6 text-pf-text-tertiary" />
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
                          aria-label="Queue for printing"
                          iconCenter={<PlayIcon className="h-4 w-4" />}
                        ></Button>

                        <Button
                          type="button"
                          variant="subtle"
                          size="sm"
                          onClick={() => handleCopyFilename(file.fileName)}
                          className="!p-2 !h-auto"
                          title={copiedFile === file.fileName ? 'Copied!' : 'Copy filename'}
                          aria-label={copiedFile === file.fileName ? 'Copied!' : 'Copy filename'}
                          iconCenter={<CopyIcon className="h-4 w-4" />}
                        ></Button>

                        <Button
                          type="button"
                          variant="subtle"
                          size="sm"
                          onClick={() => handleDownloadFile(file.fileName)}
                          disabled={isDownloading === file.fileName}
                          className="!p-2 !h-auto disabled:opacity-50 disabled:cursor-not-allowed"
                          title={isDownloading === file.fileName ? 'Downloading...' : 'Download file'}
                          aria-label={isDownloading === file.fileName ? 'Downloading...' : 'Download file'}
                          iconCenter={isDownloading === file.fileName ? (
                            <div className="h-4 w-4 border-2 border-current border-t-transparent rounded-full animate-spin" />
                          ) : (
                            <DownloadIcon className="h-4 w-4" />
                          )}
                        ></Button>

                        <Button
                          type="button"
                          variant="subtle"
                          size="sm"
                          onClick={() => handleHarvestFile(file.fileName)}
                          disabled={isHarvesting === file.fileName}
                          className="!p-2 !h-auto disabled:opacity-50 disabled:cursor-not-allowed"
                          title={isHarvesting === file.fileName ? 'Harvesting...' : 'Harvest file metadata'}
                          aria-label={isHarvesting === file.fileName ? 'Harvesting...' : 'Harvest file metadata'}
                          iconCenter={isHarvesting === file.fileName ? (
                            <div className="h-4 w-4 border-2 border-current border-t-transparent rounded-full animate-spin" />
                          ) : (
                            <SaveIcon className="h-4 w-4" />
                          )}
                        ></Button>

                        <Button
                          type="button"
                          variant="subtle"
                          size="sm"
                          onClick={() => handleDeleteFile(file.fileName)}
                          disabled={isDeleting === file.fileName}
                          className="!p-2 !h-auto text-red-500 hover:text-red-600 disabled:opacity-50 disabled:cursor-not-allowed"
                          title={isDeleting === file.fileName ? 'Deleting...' : 'Delete file'}
                          aria-label={isDeleting === file.fileName ? 'Deleting...' : 'Delete file'}
                          iconCenter={isDeleting === file.fileName ? (
                            <div className="h-4 w-4 border-2 border-red-500 border-t-transparent rounded-full animate-spin" />
                          ) : (
                            <DeleteIcon className="h-4 w-4" />
                          )}
                        ></Button>
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>

          {/* Footer */}
          <div className="flex items-center justify-between p-6 border-t border-pf-border bg-pf-bg-1">
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

  // Confirmation dialog using generic ConfirmationModal component
  const printConfirmDialog = confirmDialog?.type === 'print' ? confirmDialog : null;
  const deleteConfirmDialog = confirmDialog?.type === 'delete' ? confirmDialog : null;

  return createPortal(
    <>
      {modalContent}
      
      {/* Print Confirmation */}
      {printConfirmDialog && (
        <ConfirmationModal
          isOpen={!!printConfirmDialog}
          title="Start Printing?"
          message="Start printing this file? The printer will begin the print job."
          confirmButtonText="Start Printing"
          cancelButtonText="Cancel"
          onConfirm={() => confirmPrintFile(printConfirmDialog.file.fileName)}
          onCancel={() => setConfirmDialog(null)}
        >
          {printConfirmDialog.file.thumbnailUrl && (
            <div className="flex justify-center mb-4 -mx-6 -mt-6 px-6 pt-6 border-b border-pf-border pb-4">
              <img
                src={printConfirmDialog.file.thumbnailUrl}
                alt={printConfirmDialog.file.fileName}
                className="rounded-lg max-w-xs max-h-48 object-cover border border-pf-border"
                onError={(e) => {
                  e.currentTarget.style.display = 'none';
                }}
              />
            </div>
          )}
          <div className="bg-pf-bg-2 rounded p-3 space-y-2 text-sm">
            <div>
              <p className="text-pf-text-secondary">File:</p>
              <p className="text-pf-text-primary font-medium break-all">{printConfirmDialog.file.fileName}</p>
            </div>
            {printConfirmDialog.file.sizeBytes && (
              <div>
                <p className="text-pf-text-secondary">Size:</p>
                <p className="text-pf-text-primary">{formatFileSize(printConfirmDialog.file.sizeBytes)}</p>
              </div>
            )}
            {printConfirmDialog.file.modified && (
              <div>
                <p className="text-pf-text-secondary">Modified:</p>
                <p className="text-pf-text-primary">{formatModifiedDate(printConfirmDialog.file.modified)}</p>
              </div>
            )}
          </div>
        </ConfirmationModal>
      )}
      
      {/* Delete Confirmation */}
      {deleteConfirmDialog && (
        <ConfirmationModal
          isOpen={!!deleteConfirmDialog}
          title="Delete File?"
          message="Delete this file? This action cannot be undone."
          confirmButtonText="Delete"
          cancelButtonText="Cancel"
          isDangerous={true}
          onConfirm={() => confirmDeleteFile(deleteConfirmDialog.file.fileName)}
          onCancel={() => setConfirmDialog(null)}
        >
          <div className="bg-pf-bg-2 rounded p-3 space-y-2 text-sm">
            <div>
              <p className="text-pf-text-secondary">File:</p>
              <p className="text-pf-text-primary font-medium break-all">{deleteConfirmDialog.file.fileName}</p>
            </div>
            {deleteConfirmDialog.file.sizeBytes && (
              <div>
                <p className="text-pf-text-secondary">Size:</p>
                <p className="text-pf-text-primary">{formatFileSize(deleteConfirmDialog.file.sizeBytes)}</p>
              </div>
            )}
            {deleteConfirmDialog.file.modified && (
              <div>
                <p className="text-pf-text-secondary">Modified:</p>
                <p className="text-pf-text-primary">{formatModifiedDate(deleteConfirmDialog.file.modified)}</p>
              </div>
            )}
          </div>
        </ConfirmationModal>
      )}
      
      {/* Thumbnail Preview Modal */}
      {selectedThumbnail && (
        <Modal
          isOpen={!!selectedThumbnail}
          onClose={() => setSelectedThumbnail(null)}
          title={selectedThumbnail.fileName}
          width="max-w-2xl"
        >
          <div className="flex items-center justify-center bg-pf-bg-2 rounded-lg overflow-hidden max-h-[70vh]">
            <img
              src={selectedThumbnail.url}
              alt={selectedThumbnail.fileName}
              className="max-w-full max-h-[70vh] object-contain"
              onError={(e) => {
                e.currentTarget.style.display = 'none';
              }}
            />
          </div>
        </Modal>
      )}
    </>,
    document.body
  );
}
