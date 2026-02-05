import React, { useState, useCallback } from 'react';
import { FileIcon, UploadIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { Button, FileUpload } from '@/common/components/ui';
import { toast } from 'sonner';
import { Modal } from './Modal';

interface GcodeUploadModalProps {
  isOpen: boolean;
  onClose: () => void;
  onFilesSelected: (
    files: File[],
    onProgress?: (fileName: string, progress: number) => void,
    onItemComplete?: (fileName: string, status: 'done' | 'error', error?: string) => void
  ) => void;
  harvestId?: string;
  printerId?: string;
}

interface UploadItem {
  id: string;
  file: File;
  progress: number;
  status: 'queued' | 'uploading' | 'done' | 'error';
  error?: string;
}

export const GcodeUploadModal: React.FC<GcodeUploadModalProps> = ({
  isOpen,
  onClose,
  onFilesSelected
}) => {
  const [dragOver, setDragOver] = useState(false);
  const [uploadQueue, setUploadQueue] = useState<UploadItem[]>([]);

  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    setDragOver(false);

    const files = Array.from(e.dataTransfer.files).filter(file =>
      file.name.toLowerCase().endsWith('.gcode') || 
      file.name.toLowerCase().endsWith('.ngc') ||
      file.name.toLowerCase().endsWith('.gc')
    );

    if (files.length === 0) {
      toast.error('No valid G-code files (*.gcode, *.ngc, *.gc)');
      return;
    }

    files.forEach(file => {
      const id = `${file.name}-${Date.now()}-${Math.random()}`;
      setUploadQueue(prev => [...prev, {
        id,
        file,
        progress: 0,
        status: 'queued'
      }]);
    });
  }, []);

  const handleFileSelect = useCallback((files: FileList | null) => {
    if (!files) return;

    const validFiles = Array.from(files).filter(file =>
      file.name.toLowerCase().endsWith('.gcode') || 
      file.name.toLowerCase().endsWith('.ngc') ||
      file.name.toLowerCase().endsWith('.gc')
    );

    if (validFiles.length === 0) {
      toast.error('No valid G-code files (*.gcode, *.ngc, *.gc)');
      return;
    }

    validFiles.forEach(file => {
      const id = `${file.name}-${Date.now()}-${Math.random()}`;
      setUploadQueue(prev => [...prev, {
        id,
        file,
        progress: 0,
        status: 'queued'
      }]);
    });
  }, []);

  const removeItem = (id: string) => {
    setUploadQueue(prev => prev.filter(item => item.id !== id));
  };

  const handleUpload = () => {
    const queuedFiles = uploadQueue.filter(item => item.status === 'queued');
    if (queuedFiles.length === 0) {
      toast.error('No files to upload');
      return;
    }
    
    // Mark all queued files as uploading
    queuedFiles.forEach(item => {
      setUploadQueue(prev => prev.map(it =>
        it.id === item.id ? { ...it, status: 'uploading', progress: 0 } : it
      ));
    });

    // Create progress callback
    const handleProgress = (fileName: string, progress: number) => {
      setUploadQueue(prev => prev.map(item =>
        item.file.name === fileName
          ? { ...item, progress: Math.round(progress) }
          : item
      ));
    };

    // Create completion callback
    const handleItemComplete = (fileName: string, status: 'done' | 'error', error?: string) => {
      setUploadQueue(prev => prev.map(item =>
        item.file.name === fileName
          ? { ...item, status, error, progress: status === 'done' ? 100 : item.progress }
          : item
      ));
    };

    // Pass files to parent for upload handling with callbacks
    onFilesSelected(queuedFiles.map(item => item.file), handleProgress, handleItemComplete);
  };

  const handleClose = () => {
    const uploadingCount = uploadQueue.filter(item => item.status === 'uploading').length;
    if (uploadingCount > 0) return; // Don't close while uploading
    setUploadQueue([]);
    onClose();
  };

  if (!isOpen) return null;

  const queuedCount = uploadQueue.filter(item => item.status === 'queued').length;
  const uploadingCount = uploadQueue.filter(item => item.status === 'uploading').length;
  const completedCount = uploadQueue.filter(item => item.status === 'done').length;
  const failedCount = uploadQueue.filter(item => item.status === 'error').length;

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Upload G-Code Files"
      isDisabled={uploadingCount > 0}
      footer={
        <Button onClick={handleClose} variant="secondary" size="sm" disabled={uploadingCount > 0}>
          Close
        </Button>
      }
    >
      {/* Upload Queue / File List */}
      {uploadQueue.length > 0 && (
        <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-4 space-y-3 mb-4">
          <div className="flex items-center justify-between">
            <h3 className="text-sm font-semibold text-pf-text-primary">
              Upload Queue ({uploadQueue.length})
            </h3>
            <div className="text-xs text-pf-text-secondary space-x-2">
              {failedCount > 0 && <span>Failed: {failedCount} •</span>}
              {completedCount > 0 && <span>Done: {completedCount} •</span>}
              {uploadingCount > 0 && <span>Uploading: {uploadingCount} •</span>}
              {queuedCount > 0 && <span>Pending: {queuedCount}</span>}
            </div>
          </div>

          <div className="max-h-48 overflow-y-auto space-y-2">
            {uploadQueue.map(item => (
              <div key={item.id} className="bg-pf-bg-2 rounded-sm p-3 border border-pf-border">
                <div className="flex items-center justify-between mb-2">
                  <span className="text-sm truncate text-pf-text-primary font-medium">{item.file.name}</span>
                  <div className="flex items-center gap-2">
                    <span className="text-xs text-pf-text-tertiary">
                      {item.status === 'uploading' && `${item.progress}%`}
                      {item.status === 'done' && '✓ Done'}
                      {item.status === 'error' && '✗ Error'}
                      {item.status === 'queued' && 'Pending'}
                    </span>
                    {item.status !== 'uploading' && (
                      <Button
                        onClick={() => removeItem(item.id)}
                        variant="subtle"
                        size="sm"
                        className="!p-1"
                      >
                        <DeleteIcon className="w-4 h-4" />
                      </Button>
                    )}
                  </div>
                </div>

                {item.status === 'uploading' && (
                  <div className="w-full bg-pf-bg rounded-full h-1.5">
                    <div
                      className="bg-pf-accent h-full rounded-full transition-all duration-300"
                      style={{ width: `${item.progress}%` }}
                    />
                  </div>
                )}

                {item.error && (
                  <p className="text-xs text-pf-error mt-2">{item.error}</p>
                )}
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Drag & Drop Area (only show if not uploading) */}
      {uploadingCount === 0 && (
        <>
          <div
            className={`border-2 border-dashed rounded-lg p-6 text-center transition-colors ${
              dragOver ? 'border-pf-accent bg-pf-accent-bg/20' : 'border-pf-border hover:border-pf-accent'
            } cursor-pointer`}
            onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
            onDragLeave={() => setDragOver(false)}
            onDrop={handleDrop}
          >
            <div className="flex flex-col items-center space-y-2">
              <FileIcon className="w-8 h-8 text-pf-text-tertiary" />
              <p className="text-sm font-medium text-pf-text-secondary">Drag files here or click to browse</p>
              <p className="text-xs text-pf-text-tertiary">GCODE, NGC, GC</p>
            </div>
            <FileUpload
              id="gcode-file-upload"
              multiple
              accept=".gcode,.ngc,.gc"
              onChange={handleFileSelect}
              buttonText="Browse Files"
              buttonVariant="secondary"
              className="mt-4"
            />
          </div>

          {uploadQueue.length > 0 && queuedCount > 0 && (
            <Button
              onClick={handleUpload}
              variant="primary"
              size="sm"
              className="w-full mt-4"
              iconLeft={<UploadIcon className="w-4 h-4" />}
            >
              Upload {queuedCount} File{queuedCount > 1 ? 's' : ''}
            </Button>
          )}

          {uploadQueue.length === 0 && (
            <p className="text-center text-pf-text-tertiary text-sm mt-4">
              Add files above to get started
            </p>
          )}
        </>
      )}
    </Modal>
  );
};

GcodeUploadModal.displayName = 'GcodeUploadModal';
