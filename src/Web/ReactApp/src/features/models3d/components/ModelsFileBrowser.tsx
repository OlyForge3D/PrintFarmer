/* eslint-disable @typescript-eslint/no-explicit-any */
import React, { useState } from 'react';
import { GenericFileBrowser, type GenericFileBrowserConfig, type FileItem, type FetchFilesResponse } from '@/features/gcode/components/GenericFileBrowser';
import { ModelGridView } from '@/features/models3d/components/ModelGridView';
import { ExplorerModelListView } from '@/features/models3d/components/ExplorerModelListView';
import { ModelUploadModal } from '@/common/components/modals/ModelUploadModal';
import { Button } from '@/common/components/ui';
import { TagIcon, UploadIcon } from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';
import type { Model } from '@/types/models';

// Keep models in a cache so we can reconstruct them in adapters
const modelCache: Map<string, Model> = new Map();

// Adapter to convert Model to FileItem
function modelToFileItem(model: Model): FileItem {
  modelCache.set(model.id, model);
  return {
    path: model.id,
    fileName: model.fileName,
    isDirectory: false,
    size: model.fileSize || 0,
    modifiedDate: model.uploadedAt || new Date().toISOString(),
  };
}

// Adapter component for grid view
 
const ModelGridViewAdapter: React.FC<{
  files: FileItem[];
  onNavigate: (path: string) => void;
  onDelete: (file: FileItem) => void;
  onDownload?: (path: string) => void;
  isDeleting: boolean;
}> = ({ files, onNavigate, isDeleting }) => {
  const models = files.map(f => modelCache.get(f.path) || { id: f.path, fileName: f.fileName } as Model);
  
  return (
    <ModelGridView
      models={models}
      isLoading={isDeleting}
      onViewerModel={(model) => onNavigate(model.id)}
      onTagModel={() => {}}
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

// Adapter component for explorer view
const ExplorerModelListViewAdapter: React.FC<{
  files: FileItem[];
  isLoading: boolean;
  selectedFiles: string[];
  onSelectFile: (path: string) => void;
  onSelectAll: (files: FileItem[]) => void;
  currentPath: string;
  onNavigate: (path: string) => void;
}> = ({ files, selectedFiles, onSelectFile, onSelectAll }) => {
  return (
    <ExplorerModelListView
      selectedFiles={selectedFiles}
      onFileSelect={(file) => onSelectFile(file.path)}
      onSelectAll={() => onSelectAll(files)}
      onDelete={() => {
        // Delete is handled by parent through toolbar
      }}
      onDownload={() => {
        // Download is handled by parent through toolbar
      }}
    />
  );
};

// Format bytes helper
const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 Bytes';
  const k = 1024;
  const sizes = ['Bytes', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

const formatDate = (date: string): string => new Date(date).toLocaleDateString();

interface ModelsFileBrowserProps {
  viewMode?: 'grid' | 'explorer';
  onViewModeChange?: (mode: 'grid' | 'explorer') => void;
  selectedTags?: string[];
  onDeleteModels?: (modelIds: string[]) => Promise<void>;
  onShowTagModal?: () => void;
  selectedModelIds?: string[];
}

export const ModelsFileBrowser: React.FC<ModelsFileBrowserProps> = ({
  viewMode,
  onViewModeChange,
  selectedTags = [],
  onDeleteModels,
  onShowTagModal,
  selectedModelIds = [],
}) => {
  const { hasPermission } = useAuth();
  const [showUploadModal, setShowUploadModal] = useState(false);
  const [localSelectedModelIds, setLocalSelectedModelIds] = useState<string[]>([]);

  // Use provided selectedModelIds if available, otherwise use local state
  const activeSelectedIds = selectedModelIds.length > 0 ? selectedModelIds : localSelectedModelIds;
  
  const handleSelectModel = (modelId: string, selected: boolean) => {
    setLocalSelectedModelIds(prev =>
      selected ? [...prev, modelId] : prev.filter(id => id !== modelId)
    );
  };
  
  const handleSelectAllModels = (modelIds: string[]) => {
    setLocalSelectedModelIds(modelIds);
  };

  // Fetch models with pagination
  const fetchModels = React.useCallback(
    async (params: {
      path: string;
      search: string;
      sortBy: string;
      sortOrder: 'asc' | 'desc';
      page: number;
      pageSize: number;
    }): Promise<FetchFilesResponse<FileItem>> => {
      const response = await fetch(`${getApiBaseUrl()}/3d-models/search`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...getAuthHeaders(),
        },
        body: JSON.stringify({
          query: params.search || undefined,
          tagIds: selectedTags.length > 0 ? selectedTags : undefined,
          page: params.page || 1,
          pageSize: params.pageSize || 50,
          sortBy: params.sortBy === 'name' ? 'fileName' : params.sortBy === 'size' ? 'fileSize' : 'uploadedAt',
          descending: params.sortOrder === 'desc',
        }),
      });

      if (!response.ok) {
        throw new Error('Failed to fetch models');
      }

      const data = await response.json();
      const models: Model[] = data.models || [];
      const totalSize = models.reduce((sum, m) => sum + (m.fileSize || 0), 0);

      return {
        files: models.map(modelToFileItem),
        totalFiles: data.totalModels || models.length,
        totalSize,
        page: data.page || params.page,
        totalPages: data.totalPages || Math.ceil((data.totalModels || 0) / (params.pageSize || 50)),
      };
    },
    [selectedTags]
  );

  // Create generic config
  const config: GenericFileBrowserConfig<FileItem> = {
    fetchFiles: fetchModels,
    gridViewComponent: ModelGridViewAdapter as React.ComponentType<any>,
    explorerViewComponent: ExplorerModelListViewAdapter as React.ComponentType<any>,
    onDelete: onDeleteModels,
    canDelete: true,
    viewModePreferenceKey: 'printfarmer-models-viewmode',
    sortOptions: [
      { value: 'name', label: 'Name' },
      { value: 'size', label: 'Size' },
      { value: 'date', label: 'Date' },
    ],
    defaultSort: 'date',
    formatBytes,
    formatDate,
    // Add Upload, Tag buttons to toolbar
    extraToolbarButtons: (
      <>
        {hasPermission('3d_models', 'create') && (
          <Button
            type="button"
            onClick={() => setShowUploadModal(true)}
            variant="secondary"
            size="sm"
            title="Upload models"
            iconCenter={<UploadIcon className="w-4 h-4" />}
          />
        )}
        {activeSelectedIds.length > 0 && onShowTagModal && (
          <Button
            type="button"
            onClick={onShowTagModal}
            variant="secondary"
            size="sm"
            title="Tag selected models"
          >
            <TagIcon className="w-4 h-4 mr-1" />
            ({activeSelectedIds.length})
          </Button>
        )}
      </>
    ),
  };

  return (
    <>
      <GenericFileBrowser
        config={config}
        viewMode={viewMode}
        onViewModeChange={onViewModeChange}
        initialPath="/"
      />
      <ModelUploadModal
        isOpen={showUploadModal}
        onClose={() => setShowUploadModal(false)}
      />
    </>
  );
};
