import React from 'react';
import { ModelGridView } from '@/features/models3d/components/ModelGridView';
import type { Model } from '@/types/models';

interface ModelsGridViewAdapterProps {
  files: Model[];
  onNavigate: (path: string) => void;
  isDeleting: boolean;
}

export const ModelsGridViewAdapter: React.FC<ModelsGridViewAdapterProps> = ({
  files,
  onNavigate,
  isDeleting,
}) => {
  // Adapt the generic interface to ModelGridView's expected interface
  return (
    <ModelGridView
      models={files}
      isLoading={isDeleting}
      onViewerModel={(model) => {
        // Navigate to model viewer with the model ID
        onNavigate(model.id);
      }}
      onTagModel={() => {
        // Tag handling is managed by parent toolbar
      }}
      formatFileSize={(bytes) => {
        if (bytes === 0) return '0 B';
        const k = 1024;
        const sizes = ['B', 'KB', 'MB', 'GB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
      }}
    />
  );
};
