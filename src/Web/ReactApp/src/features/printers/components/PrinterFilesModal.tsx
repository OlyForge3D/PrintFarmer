import { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import { Link } from 'react-router';
import { DeleteIcon, TextIcon, AlertIcon, PlayIcon, CopyIcon, ImageIcon, DownloadIcon } from '@/common/components/icons/MdiIcons';
import { Button, ToolbarField } from '@/common/components/ui';
import { SortControl, Toolbar } from '@/common/components/ui/toolbar';
import { Modal, ConfirmationModal } from '@/common/components/modals';
import { apiClient } from '@/services/api';
import { usePrinterFileThumbnails } from '@/features/printers/hooks/usePrinterFileThumbnails';
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
  const [selectedThumbnail, setSelectedThumbnail] = useState<{ fileName: string; thumbnailUrl: string } | null>(null);
  const [sortBy, setSortBy] = useState<SortBy>('name');
  const [sortOrder, setSortOrder] = useState<SortOrder>('asc');
  const [isDownloading, setIsDownloading] = useState<string | null>(null);
  const [confirmDialog, setConfirmDialog] = useState<{ type: 'print'; file: PrinterFileDto } | null>(null);
  const [visibleFileNames, setVisibleFileNames] = useState<Set<string>>(new Set());
  const thumbnailObserverRef = useRef<IntersectionObserver | null>(null);
  const observedRowsRef = useRef<Map<string, HTMLDivElement>>(new Map());
  const rowRefCallbacksRef = useRef<Map<string, (node: HTMLDivElement | null) => void>>(new Map());

  // Only fetch thumbnails for rows that are visible or near-visible in the scrollable file
  // list, instead of eagerly fetching every file's thumbnail as soon as the list loads -
  // large libraries otherwise trigger a thumbnail fetch storm the moment the modal opens.
  // See issue #2393.
  useEffect(() => {
    // jsdom (used by the test suite) has no native IntersectionObserver; degrade to "treat
    // every row as visible is unnecessary here" by simply not gating - mirrors the guard
    // already used by usePrinterSnapshotPreview.ts for the same environment gap.
    if (typeof IntersectionObserver === 'undefined') {
      return;
    }

    const observer = new IntersectionObserver(
      (entries) => {
        const newlyVisible = entries.filter((entry) => entry.isIntersecting);
        if (newlyVisible.length === 0) {
          return;
        }

        setVisibleFileNames((prev) => {
          let changed = false;
          const next = new Set(prev);
          for (const entry of newlyVisible) {
            const fileName = (entry.target as HTMLElement).dataset.fileName;
            if (fileName && !next.has(fileName)) {
              next.add(fileName);
              changed = true;
            }
          }
          return changed ? next : prev;
        });
      },
      // rootMargin extends the trigger zone beyond the visible viewport so thumbnails for
      // rows just off-screen are ready before the user scrolls to them.
      { rootMargin: '300px 0px', threshold: 0 }
    );
    thumbnailObserverRef.current = observer;

    // Capture the ref containers at effect-setup time: the cleanup below intentionally clears
    // whatever these refs point to at teardown (component unmount / re-open), not a snapshot
    // frozen from this render.
    const observedRows = observedRowsRef.current;
    const rowRefCallbacks = rowRefCallbacksRef.current;

    // Rows already mounted (and registered via getRowRef) before this effect ran need to be
    // attached to the observer now that it exists.
    for (const node of observedRows.values()) {
      observer.observe(node);
    }

    return () => {
      observer.disconnect();
      thumbnailObserverRef.current = null;
      observedRows.clear();
      rowRefCallbacks.clear();
    };
  }, []);

  // Returns a stable ref callback per file name so that re-renders (e.g. re-sorting) don't
  // churn the IntersectionObserver by detaching and reattaching every row on every render.
  const getRowRef = useCallback((fileName: string) => {
    let callback = rowRefCallbacksRef.current.get(fileName);
    if (!callback) {
      callback = (node: HTMLDivElement | null) => {
        const previousNode = observedRowsRef.current.get(fileName);
        if (previousNode && previousNode !== node) {
          thumbnailObserverRef.current?.unobserve(previousNode);
          observedRowsRef.current.delete(fileName);
        }

        if (node) {
          node.dataset.fileName = fileName;
          if (typeof IntersectionObserver === 'undefined') {
            // No visibility tracking available in this environment (e.g. older browsers, or
            // jsdom in tests) - fall back to eagerly fetching every row's thumbnail rather
            // than never fetching any, matching the pre-#2393 behavior for this case.
            setVisibleFileNames((prev) => (prev.has(fileName) ? prev : new Set(prev).add(fileName)));
            return;
          }

          observedRowsRef.current.set(fileName, node);
          thumbnailObserverRef.current?.observe(node);
        }
      };
      rowRefCallbacksRef.current.set(fileName, callback);
    }
    return callback;
  }, []);

  // Memoized so the array reference is stable across renders that don't actually change which
  // files are visible - usePrinterFileThumbnails' effect depends on this array by reference, so
  // an unmemoized `.filter()` here would re-trigger an abort+refetch of every thumbnail on every
  // unrelated re-render of this component (e.g. sort changes, progress updates). See #2393.
  const filesForThumbnails = useMemo(
    () => files.filter((file) => visibleFileNames.has(file.fileName)),
    [files, visibleFileNames]
  );
  const { objectUrls: thumbnailObjectUrls } = usePrinterFileThumbnails(filesForThumbnails);

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
    let downloadUrl: string | undefined;
    let link: HTMLAnchorElement | undefined;
    try {
      setIsDownloading(fileName);
      const response = await apiClient.get<Blob>(
        `/printers/${printer.id}/files/download`,
        {
          params: { filename: fileName },
          responseType: 'blob',
        },
      );
      downloadUrl = window.URL.createObjectURL(response.data);
      link = document.createElement('a');
      link.href = downloadUrl;
      link.download = fileName.split('/').pop() || 'download';
      document.body.appendChild(link);
      link.click();
      toast.success(`Downloaded: ${fileName}`);
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Failed to download file';
      toast.error(errorMessage);
      console.error('Error downloading file:', err);
    } finally {
      link?.remove();
      if (downloadUrl) {
        window.URL.revokeObjectURL(downloadUrl);
      }
      setIsDownloading(null);
    }
  };

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
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Printer Files"
      width="max-w-4xl"
      maxHeight="max-h-[80vh]"
      closeOnBackdrop
      footer={
        <div className="flex w-full items-center justify-between">
          <p className="text-sm text-pf-text-secondary">
            {files.length} file{files.length !== 1 ? 's' : ''} available on printer
          </p>
          <Button type="button" variant="secondary" onClick={onClose}>
            Close
          </Button>
        </div>
      }
    >
      <p className="mb-4 text-sm text-pf-text-secondary">
        {printer.name} - Available G-code files
      </p>
      <p className="mb-4 text-sm text-pf-text-secondary">
        To copy a file to the library or manage printer locations, open{' '}
        <Link
          to={`/files?source=printers&printer=${encodeURIComponent(printer.id)}`}
          onClick={onClose}
          className="text-pf-accent underline"
        >
          Files &gt; On printers
        </Link>.
      </p>
      <div>
        {isLoading ? (
              <div className="flex items-center justify-center py-8">
                <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent"></div>
                <span className="ml-3 text-pf-text-secondary">Loading files...</span>
              </div>
            ) : error ? (
              <div className="text-center py-8">
                <AlertIcon className="h-12 w-12 text-pf-error mx-auto mb-4" />
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
                <div className="mb-6 flex flex-wrap items-center justify-between gap-3">
                  <h3 className="text-lg font-medium text-pf-text-primary">
                    {files.length} File{files.length !== 1 ? 's' : ''}
                  </h3>
                  <Toolbar aria-label="Printer file sorting" labelled className="mb-0">
                    <ToolbarField label="Sort by" htmlFor="sort-by">
                      <SortControl
                        id="sort-by"
                        value={sortBy}
                        onChange={(event) => setSortBy(event.target.value as SortBy)}
                        direction={sortOrder}
                        onDirectionChange={toggleSortOrder}
                        aria-label="Sort by"
                      >
                        <option value="name">Name</option>
                        <option value="modified">Date Modified</option>
                        <option value="size">File Size</option>
                      </SortControl>
                    </ToolbarField>
                  </Toolbar>
                </div>

                <div className="space-y-2">
                  {getSortedFiles().map((file) => {
                    return (
                      <div
                        key={file.fileName}
                        ref={getRowRef(file.fileName)}
                        className="relative flex flex-col bg-pf-bg-1 border border-pf-border rounded-lg p-4 hover:border-pf-accent transition-colors group"
                      >
                        <div className="flex items-center justify-between">
                          <div className="flex items-center flex-1 min-w-0 gap-3">
                            {file.thumbnailUrl && thumbnailObjectUrls[file.thumbnailUrl] ? (
                              <div
                                className="relative h-12 w-12 shrink-0 rounded-sm bg-pf-bg-2 border border-pf-border overflow-hidden cursor-pointer hover:opacity-80 transition-opacity"
                                onClick={() => setSelectedThumbnail({ fileName: file.fileName, thumbnailUrl: file.thumbnailUrl! })}
                              >
                                <img
                                  src={thumbnailObjectUrls[file.thumbnailUrl]}
                                  alt={file.fileName}
                                  className="h-full w-full object-cover"
                                  onError={(e) => {
                                    e.currentTarget.style.display = 'none';
                                  }}
                                />
                              </div>
                            ) : (
                              <div className="h-12 w-12 shrink-0 rounded-sm bg-pf-bg-2 border border-pf-border flex items-center justify-center">
                                <ImageIcon className="h-6 w-6 text-pf-text-tertiary" />
                              </div>
                            )}
                            <div className="min-w-0 flex-1">
                              <p className="text-pf-text-primary wrap-break-word font-medium">{file.fileName}</p>
                              <div className="flex gap-4 text-xs text-pf-text-tertiary mt-1">
                                <span>G-code file</span>
                                {file.sizeBytes && <span>{formatFileSize(file.sizeBytes)}</span>}
                                {file.modified && <span>{formatModifiedDate(file.modified)}</span>}
                              </div>
                            </div>
                          </div>

                          <div className="flex items-center gap-2 shrink-0 ml-2 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity">
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
                              disabled
                              className="!p-2 !h-auto text-pf-error-text enabled:hover:bg-pf-error/10 disabled:opacity-50 disabled:cursor-not-allowed"
                              title="Use the printer-file catalog to safely delete a confirmed location"
                              aria-label="Delete requires the printer-file catalog"
                              iconCenter={<DeleteIcon className="h-4 w-4" />}
                            ></Button>
                          </div>
                        </div>

                      </div>
                    );
                  })}
                </div>
              </div>
            )}
      </div>
    </Modal>
  );

  // Confirmation dialog using generic ConfirmationModal component
  const printConfirmDialog = confirmDialog?.type === 'print' ? confirmDialog : null;

  return (
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
          {printConfirmDialog.file.thumbnailUrl && thumbnailObjectUrls[printConfirmDialog.file.thumbnailUrl] && (
            <div className="flex justify-center mb-4 -mx-6 -mt-6 px-6 pt-6 border-b border-pf-border pb-4">
              <img
                src={thumbnailObjectUrls[printConfirmDialog.file.thumbnailUrl]}
                alt={printConfirmDialog.file.fileName}
                className="rounded-lg max-w-xs max-h-48 object-cover border border-pf-border"
                onError={(e) => {
                  e.currentTarget.style.display = 'none';
                }}
              />
            </div>
          )}
          <div className="bg-pf-bg-2 rounded-sm p-3 space-y-2 text-sm">
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
              src={thumbnailObjectUrls[selectedThumbnail.thumbnailUrl]}
              alt={selectedThumbnail.fileName}
              className="max-w-full max-h-[70vh] object-contain"
              onError={(e) => {
                e.currentTarget.style.display = 'none';
              }}
            />
          </div>
        </Modal>
      )}
    </>
  );
}
