/* eslint-disable @typescript-eslint/no-explicit-any */
import React, { useState, useCallback } from 'react';
import { GenericFileBrowser, type GenericFileBrowserConfig, type FetchFilesResponse } from '@/features/gcode/components/GenericFileBrowser';
import { ExplorerFileBrowser } from '@/features/gcode/components/ExplorerFileBrowser';
import { GcodeFileCard } from '@/features/gcode/components/GcodeFileCard';
import { GcodeUploadModal } from '@/common/components/modals/GcodeUploadModal';
import { Button } from '@/common/components/ui';
import { UploadIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { toast } from 'sonner';
import type { GcodeFile, GetGcodeFilesResponse } from '@/types/api';

// Format bytes helper
const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 Bytes';
  const k = 1024;
  const sizes = ['Bytes', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

const formatDate = (date: string): string => new Date(date).toLocaleDateString();

// Grid view wrapper that iterates over files and renders GcodeFileCard for each
const GcodeGridView: React.FC<{
  files: any[];
  onNavigate: (path: string) => void;
  onDelete: (file: any) => void;
  onDownload?: (path: string) => void;
  isDeleting: boolean;
}> = ({ files, onNavigate, onDelete, onDownload, isDeleting }) => (
  <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-3 overflow-y-auto">
    {files.map((file: GcodeFile) => (
      <GcodeFileCard
        key={file.path}
        file={file}
        onNavigate={onNavigate}
        onDelete={() => onDelete(file)}
        onDownload={onDownload}
        isDeleting={isDeleting}
      />
    ))}
  </div>
);

interface GcodeFileBrowserProps {
  harvestId?: string;
  printerId?: string;
  viewMode?: 'grid' | 'explorer';
  onViewModeChange?: (mode: 'grid' | 'explorer') => void;
  isModal?: boolean;
}

export const GcodeFileBrowser: React.FC<GcodeFileBrowserProps> = ({
  harvestId,
  printerId,
  viewMode,
  onViewModeChange,
  isModal = false,
}) => {
  const { hasPermission } = useAuth();
  const [showUploadModal, setShowUploadModal] = useState(false);
  
  // Fetch gcode files with pagination
  const fetchGcodeFiles = useCallback(
    async (params: {
      path: string;
      search: string;
      sortBy: string;
      sortOrder: 'asc' | 'desc';
      page: number;
      pageSize: number;
    }): Promise<FetchFilesResponse<any>> => {
      // Always use the hierarchical file browser endpoint to get complete data with all fields
      // This ensures library view (root path, no filters) gets the same fields as hierarchy view
      const data: GetGcodeFilesResponse = await apiClient.getGcodeFilesWithFilter({
        path: params.path,
        harvestId,
        printerId,
        sortBy: params.sortBy as any,
        sortOrder: params.sortOrder,
        search: params.search,
        page: params.page,
        pageSize: params.pageSize,
      });

      const files = (data.files || []).map(f => ({
        ...f,
        size: f.fileSize,
        modifiedDate: f.uploadedAt || new Date().toISOString(),
      }));

      return {
        files,
        totalFiles: data.totalFiles || 0,
        totalSize: data.totalSize || 0,
        page: data.page || params.page,
        totalPages: data.totalPages || 0,
      };
    },
    [harvestId, printerId]
  );

  // Handle file upload - uploads files sequentially and tracks per-file progress
  const handleUpload = useCallback(async (files: File[]) => {
    if (files.length === 0) {
      toast.error('No files selected');
      return;
    }

    try {
      // Upload files sequentially through the single-file endpoint
      // This allows per-file progress tracking via SignalR
      const response = await apiClient.uploadMultipleGcodeLibraryFiles(files);
      
      if (response.succeededCount > 0) {
        toast.success(`Successfully uploaded ${response.succeededCount} file${response.succeededCount > 1 ? 's' : ''}`);
      }
      
      if (response.failedCount > 0) {
        toast.error(`Failed to upload ${response.failedCount} file${response.failedCount > 1 ? 's' : ''}`);
      }
      
      // Close modal after upload completes
      setShowUploadModal(false);
    } catch (error) {
      toast.error(`Upload failed: ${error instanceof Error ? error.message : 'Unknown error'}`);
      setShowUploadModal(false);
    }
  }, []);

  // Create generic config
  const config: GenericFileBrowserConfig<any> = {
    fetchFiles: fetchGcodeFiles,
    gridViewComponent: GcodeGridView as React.ComponentType<any>,
    explorerViewComponent: ExplorerFileBrowser as React.ComponentType<any>,
    canDelete: true,
    onDelete: async (paths: string[]) => {
      // Delete files via API - throws on error
      await apiClient.deleteGcodeFiles(paths);
    },
    viewModePreferenceKey: 'printfarmer-gcode-viewmode',
    sortOptions: [
      { value: 'name', label: 'Name' },
      { value: 'size', label: 'Size' },
      { value: 'date', label: 'Date' },
    ],
    defaultSort: 'name',
    formatBytes,
    formatDate,
    // Upload button via extraToolbarButtons
    extraToolbarButtons: hasPermission('gcode_harvest', 'create') ? (
      <>
        <Button
          type="button"
          onClick={() => setShowUploadModal(true)}
          variant="secondary"
          size="sm"
          title="Upload files"
          iconCenter={<UploadIcon className="w-4 h-4" />}
        />
      </>
    ) : undefined,
  };

  return (
    <>
      <GenericFileBrowser
        config={config}
        viewMode={viewMode}
        onViewModeChange={onViewModeChange}
        initialPath="/"
        isModal={isModal}
      />
      <GcodeUploadModal
        isOpen={showUploadModal}
        onClose={() => setShowUploadModal(false)}
        onFilesSelected={handleUpload}
        harvestId={harvestId}
        printerId={printerId}
      />
    </>
  );
};
