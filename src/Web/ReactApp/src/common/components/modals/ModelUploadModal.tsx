import React, { useState, useCallback } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { CloseIcon, CubeIcon, UploadIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { Button, FileUpload } from '@/common/components/ui';
import { slicerService } from '@/services/slicerService';
import { toast } from 'sonner';

interface ModelUploadModalProps {
  isOpen: boolean;
  onClose: () => void;
}

interface UploadItem {
  id: string;
  file: File;
  progress: number;
  status: 'queued' | 'uploading' | 'done' | 'error';
  error?: string;
}

export const ModelUploadModal: React.FC<ModelUploadModalProps> = ({
  isOpen,
  onClose
}) => {
  const queryClient = useQueryClient();
  const [dragOver, setDragOver] = useState(false);
  const [uploadQueue, setUploadQueue] = useState<UploadItem[]>([]);

  // Upload mutation
  const uploadMutation = useMutation({
    mutationFn: (file: File) => slicerService.uploadModel(file),
    onSuccess: () => {
      // Update upload item status to done
      // Note: models-search will be invalidated on modal close
    },
    onError: (error: unknown) => {
      console.error('Upload error:', error);
    }
  });

  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    setDragOver(false);

    const files = Array.from(e.dataTransfer.files).filter(file =>
      ['stl', '3mf', 'obj', 'ply'].includes(file.name.split('.').pop()?.toLowerCase() || '')
    );

    if (files.length === 0) {
      toast.error('No valid 3D model files (STL, 3MF, OBJ, PLY)');
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
      ['stl', '3mf', 'obj', 'ply'].includes(file.name.split('.').pop()?.toLowerCase() || '')
    );

    if (validFiles.length === 0) {
      toast.error('No valid 3D model files (STL, 3MF, OBJ, PLY)');
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

  const uploadFiles = async () => {
    const queuedFiles = uploadQueue.filter(item => item.status === 'queued');
    
    for (const item of queuedFiles) {
      try {
        setUploadQueue(prev => prev.map(it =>
          it.id === item.id ? { ...it, status: 'uploading', progress: 0 } : it
        ));

        // Simulate progress
        const progressInterval = setInterval(() => {
          setUploadQueue(prev => prev.map(it => {
            if (it.id === item.id && it.progress < 90) {
              return { ...it, progress: it.progress + 10 };
            }
            return it;
          }));
        }, 200);

        await uploadMutation.mutateAsync(item.file);

        clearInterval(progressInterval);
        setUploadQueue(prev => prev.map(it =>
          it.id === item.id ? { ...it, progress: 100, status: 'done' } : it
        ));

        toast.success(`${item.file.name} uploaded successfully`);
      } catch (error) {
        const errorMsg = error instanceof Error ? error.message : 'Upload failed';
        setUploadQueue(prev => prev.map(it =>
          it.id === item.id ? { ...it, status: 'error', error: errorMsg } : it
        ));
        toast.error(`Failed to upload ${item.file.name}`);
      }
    }
  };

  const removeItem = (id: string) => {
    setUploadQueue(prev => prev.filter(item => item.id !== id));
  };

  const handleClose = () => {
    // Invalidate models-search query to refresh the models list
    queryClient.invalidateQueries({ queryKey: ['models-search'] });
    onClose();
  };

  if (!isOpen) return null;

  const queuedCount = uploadQueue.filter(item => item.status === 'queued').length;
  const uploadingCount = uploadQueue.filter(item => item.status === 'uploading').length;
  const completedCount = uploadQueue.filter(item => item.status === 'done').length;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
      <div className="bg-pf-bg-0 rounded-lg shadow-xl border border-pf-border max-w-2xl w-full max-h-[90vh] overflow-y-auto flex flex-col">
        {/* Header */}
        <div className="sticky top-0 bg-pf-bg-1 border-b border-pf-border px-6 py-4 flex items-center justify-between">
          <h2 className="text-xl font-semibold text-pf-text-primary">Upload 3D Models</h2>
          <Button
            onClick={handleClose}
            disabled={uploadingCount > 0}
            variant="subtle"
            size="sm"
            className="!p-0 !h-auto"
          >
            <CloseIcon className="w-6 h-6" />
          </Button>
        </div>

        {/* Content */}
        <div className="flex-1 flex flex-col p-6 space-y-4 overflow-y-auto">
          {/* Drag & Drop Area */}
          <div
            className={`border-2 border-dashed rounded-lg p-6 text-center transition-colors ${
              dragOver ? 'border-pf-accent bg-pf-accent-bg bg-opacity-20' : 'border-pf-border hover:border-pf-accent'
            } cursor-pointer`}
            onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
            onDragLeave={() => setDragOver(false)}
            onDrop={handleDrop}
          >
            <div className="flex flex-col items-center space-y-2">
              <CubeIcon className="w-8 h-8 text-pf-text-tertiary" />
              <p className="text-sm font-medium text-pf-text-secondary">Drag files here or click to browse</p>
              <p className="text-xs text-pf-text-tertiary">STL, 3MF, OBJ, PLY</p>
            </div>
            <FileUpload
              id="model-file-upload"
              multiple
              accept=".stl,.3mf,.obj,.ply"
              onChange={handleFileSelect}
              buttonText="Browse Files"
              buttonVariant="secondary"
              className="mt-4"
            />
          </div>

          {/* Upload Queue */}
          {uploadQueue.length > 0 && (
            <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-4 space-y-3">
              <div className="flex items-center justify-between">
                <h3 className="text-sm font-semibold text-pf-text-primary">
                  Upload Queue ({uploadQueue.length})
                </h3>
                <div className="text-xs text-pf-text-secondary">
                  {completedCount > 0 && <span>Done: {completedCount} • </span>}
                  {uploadingCount > 0 && <span>Uploading: {uploadingCount} • </span>}
                  {queuedCount > 0 && <span>Pending: {queuedCount}</span>}
                </div>
              </div>

              <div className="max-h-48 overflow-y-auto space-y-2">
                {uploadQueue.map(item => (
                  <div key={item.id} className="bg-pf-bg-2 rounded p-3 border border-pf-border">
                    <div className="flex items-center justify-between mb-2">
                      <span className="text-sm truncate text-pf-text-primary font-medium">{item.file.name}</span>
                      <div className="flex items-center gap-2">
                        <span className="text-xs text-pf-text-tertiary">
                          {item.status === 'uploading' && `${item.progress}%`}
                          {item.status === 'done' && '✓ Done'}
                          {item.status === 'error' && '✗ Error'}
                          {item.status === 'queued' && 'Pending'}
                        </span>
                        <Button
                          onClick={() => removeItem(item.id)}
                          variant="subtle"
                          size="sm"
                          className="!p-1"
                        >
                          <DeleteIcon className="w-4 h-4" />
                        </Button>
                      </div>
                    </div>

                    {/* Progress Bar */}
                    {(item.status === 'uploading' || item.status === 'done') && (
                      <div className="h-1.5 bg-pf-bg-0 rounded-full border border-pf-border overflow-hidden">
                        <div
                          className="h-full transition-all bg-pf-accent"
                          style={{ width: `${Math.min(100, item.progress)}%` }}
                        />
                      </div>
                    )}

                    {/* Error Message */}
                    {item.status === 'error' && item.error && (
                      <p className="text-xs text-pf-error mt-1">{item.error}</p>
                    )}
                  </div>
                ))}
              </div>

              {/* Upload Button */}
              {queuedCount > 0 && (
                <Button
                  onClick={uploadFiles}
                  disabled={uploadingCount > 0}
                  variant="primary"
                  size="sm"
                  className="w-full"
                >
                  <UploadIcon className="w-4 h-4 mr-2" />
                  Upload {queuedCount} File{queuedCount > 1 ? 's' : ''}
                </Button>
              )}
            </div>
          )}

          {uploadQueue.length === 0 && (
            <p className="text-center text-pf-text-tertiary text-sm">
              Add files to the queue above to get started
            </p>
          )}
        </div>

        {/* Footer */}
        <div className="border-t border-pf-border px-6 py-4 flex gap-2 justify-end">
          <Button onClick={handleClose} variant="secondary" size="sm">
            Close
          </Button>
        </div>
      </div>
    </div>
  );
};

ModelUploadModal.displayName = 'ModelUploadModal';
