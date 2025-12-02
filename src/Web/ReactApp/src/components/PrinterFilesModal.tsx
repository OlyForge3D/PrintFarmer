import { useState, useEffect } from 'react';
import { createPortal } from 'react-dom';
import { X, FileText, AlertCircle, Play, Copy, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui';
import { apiClient } from '@/services/api';
import type { Printer } from '@/types/api';

interface PrinterFilesModalProps {
  isOpen: boolean;
  onClose: () => void;
  printer: Printer;
}

export function PrinterFilesModal({ isOpen, onClose, printer }: PrinterFilesModalProps) {
  const [files, setFiles] = useState<string[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [copiedFile, setCopiedFile] = useState<string | null>(null);

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
    try {
      // TODO: Implement queue functionality
      // For now, just show a confirmation
      console.log('Queue file:', fileName);
      // const response = await apiClient.queuePrintFile(printer.id, fileName);
    } catch (err) {
      console.error('Error queueing file:', err);
    }
  };

  const handleCopyFilename = (fileName: string) => {
    navigator.clipboard.writeText(fileName);
    setCopiedFile(fileName);
    setTimeout(() => setCopiedFile(null), 2000);
  };

  const handleDeleteFile = async (fileName: string) => {
    if (!confirm(`Are you sure you want to delete "${fileName}"?`)) {
      return;
    }
    // TODO: Implement delete functionality
    console.log('Delete file:', fileName);
  };

  if (!isOpen) {
    return null;
  }

  const modalContent = (
    <div className="fixed inset-0 z-50 overflow-y-auto">
      <div className="flex min-h-screen items-center justify-center p-4">
        <div className="fixed inset-0 bg-black bg-opacity-75" onClick={onClose} />
        
        <div className="relative bg-pf-bg-1 rounded-lg shadow-xl max-w-2xl w-full max-h-[80vh] flex flex-col">
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
                <div className="flex items-center justify-between mb-4">
                  <h3 className="text-lg font-medium text-pf-text-primary">
                    {files.length} File{files.length !== 1 ? 's' : ''}
                  </h3>
                </div>

                <div className="space-y-2">
                  {files.map((file) => (
                    <div
                      key={file}
                      className="flex items-center justify-between bg-pf-bg-0 border border-pf-border rounded-lg p-4 hover:border-pf-accent transition-colors group"
                    >
                      <div className="flex items-center flex-1 min-w-0 gap-3">
                        <FileText className="h-5 w-5 text-pf-accent flex-shrink-0" />
                        <div className="min-w-0 flex-1">
                          <p className="text-pf-text-primary break-words font-medium">{file}</p>
                          <p className="text-xs text-pf-text-tertiary">G-code file</p>
                        </div>
                      </div>

                      <div className="flex items-center gap-2 flex-shrink-0 ml-2 opacity-0 group-hover:opacity-100 transition-opacity">
                        <Button
                          type="button"
                          variant="subtle"
                          size="sm"
                          onClick={() => handleQueueFile(file)}
                          className="!p-2 !h-auto"
                          title="Queue for printing"
                        >
                          <Play className="h-4 w-4" />
                        </Button>

                        <Button
                          type="button"
                          variant="subtle"
                          size="sm"
                          onClick={() => handleCopyFilename(file)}
                          className="!p-2 !h-auto"
                          title={copiedFile === file ? 'Copied!' : 'Copy filename'}
                        >
                          <Copy className="h-4 w-4" />
                        </Button>

                        <Button
                          type="button"
                          variant="subtle"
                          size="sm"
                          onClick={() => handleDeleteFile(file)}
                          className="!p-2 !h-auto text-red-500 hover:text-red-600"
                          title="Delete file"
                        >
                          <Trash2 className="h-4 w-4" />
                        </Button>
                      </div>
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

  return createPortal(modalContent, document.body);
}
