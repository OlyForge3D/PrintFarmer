import React, { useState, useCallback } from 'react';
import { FileIcon, UploadIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { Button, FileUpload } from '@/common/components/ui';
import { toast } from 'sonner';
import { Modal } from './Modal';

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
  onFilesSelected
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
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Upload G-Code Files"
      footer={
        <Button onClick={handleClose} variant="secondary" size="sm">
          Close
        </Button>
      }
    >
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
                  iconLeft={<UploadIcon className="w-4 h-4" />}
                >
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
        </Modal>
    );
  };

GcodeUploadModal.displayName = 'GcodeUploadModal';
