import React, { useState, useCallback } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { CloseIcon, FileIcon, UploadIcon, DeleteIcon } from '@/components/icons/MdiIcons';
import { Button, FileUpload } from '@/components/ui';
import { toast } from 'sonner';

interface GcodeUploadModalProps {
  isOpen: boolean;
  onClose: () => void;
  onFilesSelected: (files: File[]) => void;
  harvestId?: string;
  printerId?: string;
}

interface UploadItem {
  id: string;
  file: File;
}

export const GcodeUploadModal: React.FC<GcodeUploadModalProps> = ({
  isOpen,
  onClose,
  onFilesSelected,
  harvestId,
  printerId
}) => {
  const [dragOver, setDragOver] = useState(false);
  const [selectedItems, setSelectedItems] = useState<UploadItem[]>([]);

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
      setSelectedItems(prev => [...prev, { id, file }]);
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
      setSelectedItems(prev => [...prev, { id, file }]);
    });
  }, []);

  const removeItem = (id: string) => {
    setSelectedItems(prev => prev.filter(item => item.id !== id));
  };

  const handleUpload = () => {
    const files = selectedItems.map(item => item.file);
    if (files.length === 0) {
      toast.error('No files selected');
      return;
    }
    onFilesSelected(files);
    handleClose();
  };

  const handleClose = () => {
    setSelectedItems([]);
    onClose();
  };

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
      <div className="bg-pf-bg-0 rounded-lg shadow-xl border border-pf-border max-w-2xl w-full max-h-[90vh] overflow-y-auto flex flex-col">
        {/* Header */}
        <div className="sticky top-0 bg-pf-bg-1 border-b border-pf-border px-6 py-4 flex items-center justify-between">
          <h2 className="text-xl font-semibold text-pf-text-primary">Upload G-Code Files</h2>
          <Button
            onClick={handleClose}
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

          {/* Selected Files List */}
          {selectedItems.length > 0 && (
            <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-4 space-y-3">
              <div className="flex items-center justify-between">
                <h3 className="text-sm font-semibold text-pf-text-primary">
                  Selected Files ({selectedItems.length})
                </h3>
              </div>

              <div className="max-h-48 overflow-y-auto space-y-2">
                {selectedItems.map(item => (
                  <div key={item.id} className="bg-pf-bg-2 rounded p-3 border border-pf-border">
                    <div className="flex items-center justify-between">
                      <span className="text-sm truncate text-pf-text-primary font-medium">{item.file.name}</span>
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
                ))}
              </div>

              {/* Upload Button */}
              {selectedItems.length > 0 && (
                <Button
                  onClick={handleUpload}
                  variant="primary"
                  size="sm"
                  className="w-full"
                >
                  <UploadIcon className="w-4 h-4 mr-2" />
                  Upload {selectedItems.length} File{selectedItems.length > 1 ? 's' : ''}
                </Button>
              )}
            </div>
          )}

          {selectedItems.length === 0 && (
            <p className="text-center text-pf-text-tertiary text-sm">
              Add files above to get started
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

GcodeUploadModal.displayName = 'GcodeUploadModal';
