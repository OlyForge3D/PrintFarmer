import React, { useState, useCallback } from 'react';
import { useQuery } from '@tanstack/react-query';
import { PageTemplate } from '@/components/PageTemplate';
import { Button, Alert } from '@/components/ui';
import { UploadIcon, DeleteIcon, CubeIcon, EyeIcon } from '@/components/icons/MdiIcons';
import { STLPreviewModal } from '@/components/3D/STLPreviewModal';
import { useSTLFile } from '@/hooks/useSTLFile';
import { validateSTLFile } from '@/utils/stlFileUtils';

interface Model3D {
  id: string;
  fileName: string;
  originalFileName: string;
  filePath: string;
  fileSize: number;
  uploadedAt: string;
}

/**
 * 3D Models Viewer Page
 * Browse, preview, and manage 3D model files (STL, 3MF, OBJ, etc.)
 */
export const Models3DViewerPage: React.FC = () => {
  const [selectedModel, setSelectedModel] = useState<Model3D | null>(null);
  const [isPreviewOpen, setIsPreviewOpen] = useState(false);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [uploadSuccess, setUploadSuccess] = useState<string | null>(null);
  const stlFile = useSTLFile();

  // Fetch models list
  const { data: models = [], isLoading, error } = useQuery<Model3D[], Error>({
    queryKey: ['models-3d-all'],
    queryFn: async () => {
      try {
        const response = await fetch('/api/models');
        if (!response.ok) throw new Error('Failed to fetch models');
        return response.json();
      } catch (err) {
        console.error('Error fetching models:', err);
        return [];
      }
    },
    staleTime: 30_000,
    refetchInterval: 60_000,
  });

  const handleFileSelect = useCallback(
    async (e: React.ChangeEvent<HTMLInputElement>) => {
      const file = e.target.files?.[0];
      if (!file) return;

      setUploadError(null);
      setUploadSuccess(null);

      // Validate STL file
      const validation = await validateSTLFile(file, { maxSizeMB: 100 });
      if (!validation.valid) {
        setUploadError(validation.errors.join(', '));
        return;
      }

      // Here you would upload to server
      // For now, just show success
      setUploadSuccess(`File "${file.name}" ready to upload (${(file.size / 1024 / 1024).toFixed(2)} MB)`);
      stlFile.selectFile(file);
    },
    [stlFile]
  );

  const handleDragAndDrop = useCallback(
    async (e: React.DragEvent<HTMLDivElement>) => {
      e.preventDefault();
      e.stopPropagation();

      const file = e.dataTransfer.files?.[0];
      if (!file) return;

      setUploadError(null);
      setUploadSuccess(null);

      const validation = await validateSTLFile(file, { maxSizeMB: 100 });
      if (!validation.valid) {
        setUploadError(validation.errors.join(', '));
        return;
      }

      setUploadSuccess(`File "${file.name}" ready to upload (${(file.size / 1024 / 1024).toFixed(2)} MB)`);
      stlFile.selectFile(file);
    },
    [stlFile]
  );

  const handlePreview = (model: Model3D) => {
    setSelectedModel(model);
    setIsPreviewOpen(true);
  };

  const formatFileSize = (bytes: number): string => {
    if (bytes === 0) return '0 Bytes';
    const k = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return Math.round((bytes / Math.pow(k, i)) * 100) / 100 + ' ' + sizes[i];
  };

  const formatDate = (dateString: string): string => {
    try {
      return new Date(dateString).toLocaleDateString('en-US', {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
      });
    } catch {
      return 'Unknown date';
    }
  };

  return (
    <PageTemplate
      title="3D Models Viewer"
      subtitle="Browse, preview, and manage your 3D model files"
      icon={CubeIcon}
      maxWidth="max-w-7xl"
    >
      <div className="space-y-6">
        {/* Upload Section */}
        <div className="bg-pf-panel border border-pf-border rounded-lg p-6">
          <h2 className="text-lg font-semibold text-pf-text mb-4">Upload Model</h2>

          {uploadError && (
            <Alert type="error" title="Upload Error" onClose={() => setUploadError(null)}>
              {uploadError}
            </Alert>
          )}

          {uploadSuccess && (
            <Alert type="success" title="File Ready" onClose={() => setUploadSuccess(null)}>
              {uploadSuccess}
            </Alert>
          )}

          {/* Drag and Drop Area */}
          <div
            onDragOver={(e) => e.preventDefault()}
            onDrop={handleDragAndDrop}
            className="border-2 border-dashed border-pf-border rounded-lg p-8 text-center hover:border-pf-accent transition-colors cursor-pointer bg-pf-bg-0 bg-opacity-50"
          >
            <UploadIcon className="w-12 h-12 mx-auto mb-3 text-pf-text-muted" />
            <p className="text-pf-text font-medium mb-2">Drag and drop your STL file here</p>
            <p className="text-sm text-pf-text-muted mb-4">or click to browse</p>

            <input
              type="file"
              accept=".stl,.3mf,.obj,.ply"
              onChange={handleFileSelect}
              className="hidden"
              id="file-input"
            />
            <label htmlFor="file-input">
              <Button variant="primary" size="sm">
                Select File
              </Button>
            </label>

            <p className="text-xs text-pf-text-muted mt-4">
              Supported formats: STL, 3MF, OBJ, PLY (Max 100 MB)
            </p>
          </div>
        </div>

        {/* Models Grid */}
        <div className="bg-pf-panel border border-pf-border rounded-lg p-6">
          <h2 className="text-lg font-semibold text-pf-text mb-4">Your Models</h2>

          {isLoading ? (
            <div className="flex items-center justify-center py-12">
              <div className="text-center">
                <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-pf-accent mx-auto"></div>
                <p className="text-pf-text-muted mt-4">Loading models...</p>
              </div>
            </div>
          ) : error ? (
            <Alert type="error" title="Load Error">
              Failed to load models: {error.message}
            </Alert>
          ) : models.length === 0 ? (
            <div className="text-center py-12">
              <CubeIcon className="w-12 h-12 mx-auto mb-3 text-pf-text-muted opacity-50" />
              <p className="text-pf-text-muted">No models uploaded yet</p>
              <p className="text-sm text-pf-text-muted mt-2">Upload a 3D model to get started</p>
            </div>
          ) : (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
              {models.map((model) => (
                <div
                  key={model.id}
                  className="bg-pf-bg-0 border border-pf-border rounded-lg p-4 hover:border-pf-accent transition-colors group"
                >
                  {/* Model Icon/Thumbnail */}
                  <div className="bg-gradient-to-br from-pf-accent to-blue-700 rounded-lg h-32 flex items-center justify-center mb-3 group-hover:shadow-lg transition-shadow">
                    <CubeIcon className="w-12 h-12 text-white opacity-75" />
                  </div>

                  {/* Model Info */}
                  <div className="space-y-2 mb-4">
                    <h3 className="font-semibold text-pf-text truncate" title={model.fileName}>
                      {model.fileName}
                    </h3>
                    <p className="text-xs text-pf-text-muted">
                      {formatFileSize(model.fileSize)}
                    </p>
                    <p className="text-xs text-pf-text-muted">
                      {formatDate(model.uploadedAt)}
                    </p>
                  </div>

                  {/* Actions */}
                  <div className="flex gap-2">
                    <Button
                      onClick={() => handlePreview(model)}
                      variant="secondary"
                      size="sm"
                      className="flex-1 flex items-center justify-center gap-2"
                    >
                      <EyeIcon className="w-4 h-4" />
                      Preview
                    </Button>
                    <Button
                      variant="danger"
                      size="sm"
                      className="flex items-center justify-center gap-2"
                      onClick={() => {
                        // Handle delete
                        console.log('Delete model:', model.id);
                      }}
                      title="Delete model"
                    >
                      <DeleteIcon className="w-4 h-4" />
                    </Button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>

      {/* STL Preview Modal */}
      {isPreviewOpen && selectedModel && (
        <STLPreviewModal
          isOpen={isPreviewOpen}
          fileUrl={selectedModel.filePath}
          fileName={selectedModel.fileName}
          onClose={() => {
            setIsPreviewOpen(false);
            setSelectedModel(null);
            stlFile.clearFile();
          }}
          onUseModel={() => {
            // Model is selected for use
            console.log('Using model:', selectedModel.id);
            setIsPreviewOpen(false);
          }}
        />
      )}
    </PageTemplate>
  );
};

export default Models3DViewerPage;
