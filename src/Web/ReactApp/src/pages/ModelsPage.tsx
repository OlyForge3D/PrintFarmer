import React, { useState, useCallback, Suspense } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { UploadIcon, SettingsIcon, SearchIcon, CloseIcon } from '@/components/icons/MdiIcons';
import { Box, Trash2, Eye, Tag, Grid3x3, List, FileText, FolderOpen } from 'lucide-react';
import { PageTemplate } from '@/components/PageTemplate';
import { BulkTagAssignmentModal } from '@/components/modals/BulkTagAssignmentModal';
import { Button, Input, FileUpload } from '@/components/ui';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';
import { HierarchicalFileBrowser, FileEntry } from '@/components/files/HierarchicalFileBrowser';
// Lazy load heavy three.js based viewers with manual preload support
import { lazyWithPreload } from '@/utils/lazyWithPreload';
import type { ModelViewerProps } from '@/components/3d/ModelViewer3D';
import type { GCodeViewerProps } from '@/components/3d/GCodeViewer3D';
const ModelViewer = lazyWithPreload<ModelViewerProps, React.FC<ModelViewerProps>>(
  () => import('@/components/3d/ModelViewer3D').then(m => ({ default: m.ModelViewer }))
);
const GCodeViewer = lazyWithPreload<GCodeViewerProps, React.FC<GCodeViewerProps>>(
  () => import('@/components/3d/GCodeViewer3D').then(m => ({ default: m.GCodeViewer }))
);
// Slicing now redirects to NewSliceJobPage for better UX with 3D preview
// const SlicerConfigModal = lazyWithPreload<{...}>(...)
import { slicerService } from '@/services/slicerService';
import type { SlicedModelSummary } from '@/services/slicerService';
import { ViewerSkeleton } from '@/components/3d/ViewerSkeleton';

// Backend currently returns a SlicedModelSummary; we extend with optional UI enrichment fields.
type Model = SlicedModelSummary & {
  fileName?: string;
  fileSize?: number;
  fileType?: 'stl' | '3mf' | 'obj' | 'ply';
  uploadedAt?: string; // alias of createdAt
  url?: string;
  thumbnailUrl?: string;
  tags?: Array<{ id: string; name: string; color?: string }>;
};

type ModelTag = { id: string; name: string; color?: string; description?: string };

interface GCodeFile {
  id: string;
  name: string;
  url: string;
  printTime?: number;
  filamentUsed?: number;
  layerCount?: number;
}

export const ModelsPage: React.FC = () => {
  const navigate = useNavigate();
  const [dragOver, setDragOver] = useState(false);
  const [selectedFiles, setSelectedFiles] = useState<File[]>([]);
  const [showBrowser, setShowBrowser] = useState(false);
  const [viewerModel, setViewerModel] = useState<Model | null>(null);
  const [gcodeViewer, setGcodeViewer] = useState<GCodeFile | null>(null);
  // Slicing now redirects to NewSliceJobPage instead of using modal
  const [uploadProgress, setUploadProgress] = useState<Record<string, number>>({});
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedTags, setSelectedTags] = useState<string[]>([]);
  const [viewMode, setViewMode] = useState<'grid' | 'list'>('grid');
  const [showTagFilter, setShowTagFilter] = useState(false);
  const [showBulkTagModal, setShowBulkTagModal] = useState(false);

  const queryClient = useQueryClient();

  // Fetch tags
  const { data: allTags = [] } = useQuery<ModelTag[]>({
    queryKey: ['model-tags'],
    queryFn: async () => {
      const response = await fetch(`${getApiBaseUrl()}/3d-models/tags`, {
        headers: getAuthHeaders()
      });
      if (!response.ok) throw new Error('Failed to fetch tags');
      return response.json();
    },
    staleTime: 5 * 60 * 1000,
    gcTime: 10 * 60 * 1000
  });

  // Fetch models with search/filter
  const { data: searchResult, isLoading } = useQuery({
    queryKey: ['models-search', searchQuery, selectedTags],
    queryFn: async () => {
      const response = await fetch(`${getApiBaseUrl()}/3d-models/search`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...getAuthHeaders()
        },
        body: JSON.stringify({
          query: searchQuery || undefined,
          tagIds: selectedTags.length > 0 ? selectedTags : undefined,
          page: 1,
          pageSize: 100,
          sortBy: 'uploadedAt',
          descending: true
        })
      });
      if (!response.ok) throw new Error('Failed to search models');
      return response.json();
    },
    staleTime: 2 * 60 * 1000,
    gcTime: 5 * 60 * 1000
  });

  const models = searchResult?.models || [];

  // Fetch available printers for slicing (using fast endpoint without status checks)
  useQuery({
    queryKey: ['printers-fast'],
    queryFn: async () => {
      const response = await fetch(`${getApiBaseUrl()}/printers`, { headers: getAuthHeaders() });
      return response.json();
    },
    staleTime: 5 * 60 * 1000,
    gcTime: 10 * 60 * 1000
  });

  // Upload mutation
  const uploadMutation = useMutation({
    mutationFn: (file: File) => slicerService.uploadModel(file),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['models-search'] });
      setSelectedFiles([]);
      setUploadProgress({});
    }
  });

  // Delete mutation
  const deleteMutation = useMutation({
    mutationFn: (modelId: string) => slicerService.deleteModel(modelId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['models-search'] });
    }
  });

  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    setDragOver(false);

    const files = Array.from(e.dataTransfer.files).filter(file =>
      ['stl', '3mf', 'obj', 'ply'].includes(file.name.split('.').pop()?.toLowerCase() || '')
    );
    setSelectedFiles(prev => [...prev, ...files]);
  }, []);

  const uploadFiles = async () => {
    for (const file of selectedFiles) {
      try {
        setUploadProgress(prev => ({ ...prev, [file.name]: 0 }));

        const progressInterval = setInterval(() => {
          setUploadProgress(prev => {
            const current = prev[file.name] || 0;
            if (current < 90) {
              return { ...prev, [file.name]: current + 10 };
            }
            return prev;
          });
        }, 200);

        await uploadMutation.mutateAsync(file);

        clearInterval(progressInterval);
        setUploadProgress(prev => ({ ...prev, [file.name]: 100 }));
      } catch (error) {
        console.error('Upload failed:', error);
        setUploadProgress(prev => {
          const rest = { ...prev };
          delete rest[file.name];
          return rest;
        });
      }
    }
  };

  const removeFile = (index: number) => {
    setSelectedFiles(prev => prev.filter((_, i) => i !== index));
  };

  const toggleTag = (tagId: string) => {
    setSelectedTags(prev =>
      prev.includes(tagId)
        ? prev.filter(id => id !== tagId)
        : [...prev, tagId]
    );
  };

  const formatFileSize = (bytes: number) => {
    if (bytes === 0) return '0 Bytes';
    const k = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  };

  if (isLoading && models.length === 0) {
    return (
      <PageTemplate
        title="3D Models"
        subtitle="Upload and manage your 3D models for slicing and printing"
        icon={Box}
        maxWidth="max-w-7xl"
      >
        <div className="flex items-center justify-center h-64">
          <div className="pf-animate-spin rounded-full h-12 w-12 border-b-2 border-pf-accent"></div>
        </div>
      </PageTemplate>
    );
  }

  return (
    <PageTemplate
      title="3D Models"
      subtitle="Upload and manage your 3D models for slicing and printing"
      icon={Box}
      maxWidth="max-w-7xl"
    >
      {/* Upload Area */}
      <div className="bg-pf-bg-1 rounded-lg shadow-lg border border-pf-border">
        <div
          className={`border-2 border-dashed rounded-lg p-8 text-center transition-colors ${dragOver ? 'border-pf-accent bg-pf-accent-bg bg-opacity-20' : 'border-pf-border'
            }`}
          onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
          onDragLeave={() => setDragOver(false)}
          onDrop={handleDrop}
        >
          <div className="space-y-4">
            <div className="mx-auto w-16 h-16 bg-pf-bg-2 rounded-full flex items-center justify-center">
              <Box className="w-8 h-8 text-pf-text-tertiary" />
            </div>

            <div>
              <label htmlFor="file-upload" className="cursor-pointer">
              <span className="text-lg font-medium text-pf-text-primary">
                Drop 3D models here or click to select
              </span>
            </label>
            <p className="text-pf-text-secondary mt-1">
              Supports STL, 3MF, OBJ, and PLY files
            </p>
            <FileUpload
              id="file-upload"
              multiple
              accept=".stl,.3mf,.obj,.ply"
              onChange={(files) => {
                if (files) {
                  const filesToAdd = Array.from(files).filter(file =>
                    ['stl', '3mf', 'obj', 'ply'].includes(file.name.split('.').pop()?.toLowerCase() || '')
                  );
                  setSelectedFiles(prev => [...prev, ...filesToAdd]);
                }
              }}
              buttonText="Select Files or Drag and Drop"
              buttonVariant="secondary"
            />
          </div>
        </div>
      </div>        {/* Selected files */}
        {selectedFiles.length > 0 && (
          <div className="border-t border-pf-border p-4">
            <h4 className="font-medium mb-3 text-pf-text-primary">Selected Files</h4>
            <div className="space-y-2">
              {selectedFiles.map((file, index) => (
                <div key={index} className="flex items-center justify-between bg-pf-bg-2 p-3 rounded">
                  <div className="flex items-center space-x-3">
                    <Box className="w-5 h-5 text-pf-text-tertiary" />
                    <div>
                      <div className="font-medium text-sm text-pf-text-primary">{file.name}</div>
                      <div className="text-xs text-pf-text-secondary">{formatFileSize(file.size)}</div>
                    </div>
                  </div>
                  <div className="flex items-center space-x-2">
                    {uploadProgress[file.name] !== undefined && (
                      <div className="w-24">
                        <div className="text-xs text-pf-text-secondary mb-1">
                          {uploadProgress[file.name]}%
                        </div>
                        <div className="w-full bg-pf-bg-0 rounded-full h-1 border border-pf-border">
                          {(() => {
                            const pct = uploadProgress[file.name] ?? 0;
                            const bucket = Math.min(100, Math.max(0, Math.round(pct / 5) * 5));
                            const widthClass = `w-[${bucket}%]` as const;
                            return (
                              <div
                                className={`bg-pf-accent h-1 rounded-full transition-all duration-300 ${widthClass}`}
                                aria-label={`Upload progress ${pct} percent`}
                              />
                            );
                          })()}
                        </div>
                      </div>
                    )}
                    <Button
                      onClick={() => removeFile(index)}
                      variant="subtle"
                      aria-label="Remove file"
                      title="Remove file"
                    >
                      <Trash2 className="w-4 h-4" />
                    </Button>
                  </div>
                </div>
              ))}
            </div>
            <div className="mt-4 flex justify-end">
              <Button
                variant="primary"
                onClick={uploadFiles}
                disabled={uploadMutation.isPending}
              >
                <Upload className="w-4 h-4 mr-2" />
                {uploadMutation.isPending ? 'Uploading...' : 'Upload Files'}
              </Button>
            </div>
          </div>
        )}
      </div>

      {/* Search and Filter Bar */}
      <div className="space-y-4 mt-6">
        <div className="flex flex-col md:flex-row gap-4 items-stretch md:items-center">
          {/* Search box */}
          <div className="flex-1 relative">
            <Search className="w-5 h-5 absolute left-3 top-1/2 -translate-y-1/2 text-pf-text-tertiary pointer-events-none" />
            <Input
              type="text"
              placeholder="Search models by name or description..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="pl-10"
            />
          </div>

          {/* Tag filter button */}
          <Button
            variant={selectedTags.length > 0 ? 'primary' : 'secondary'}
            onClick={() => setShowTagFilter(!showTagFilter)}
            className="flex items-center gap-2"
          >
            <Tag className="w-4 h-4" />
            Tags {selectedTags.length > 0 && `(${selectedTags.length})`}
          </Button>

          {/* Bulk tagging button */}
          <Button
            variant="secondary"
            onClick={() => setShowBulkTagModal(true)}
            title="Assign tags to multiple models at once"
          >
            <Tag className="w-4 h-4 mr-1" />
            Bulk Tag
          </Button>

          {/* Browser toggle button */}
          <Button
            variant={showBrowser ? 'primary' : 'secondary'}
            onClick={() => setShowBrowser(!showBrowser)}
            title="Browse files by folder"
          >
            <FolderOpen className="w-4 h-4 mr-1" />
            {showBrowser ? 'Hide Browser' : 'File Browser'}
          </Button>

          {/* View mode toggle */}
          <div className="flex gap-2">
            <Button
              onClick={() => setViewMode('grid')}
              variant={viewMode === 'grid' ? 'primary' : 'secondary'}
              size="sm"
              title="Grid view"
            >
              <Grid3x3 className="w-4 h-4" />
            </Button>
            <Button
              onClick={() => setViewMode('list')}
              variant={viewMode === 'list' ? 'primary' : 'secondary'}
              size="sm"
              title="List view"
            >
              <List className="w-4 h-4" />
            </Button>
          </div>
        </div>

        {/* Tag filter dropdown */}
        {showTagFilter && (
          <div className="bg-pf-bg-1 border border-pf-border rounded p-4">
            <div className="flex items-center justify-between mb-3">
              <h4 className="font-medium text-pf-text-primary">Filter by Tags</h4>
              <Button
                onClick={() => {
                  setShowTagFilter(false);
                  setSelectedTags([]);
                }}
                variant="subtle"
                size="sm"
              >
                Clear All
              </Button>
            </div>
            <div className="flex flex-wrap gap-2">
              {allTags.length > 0 ? (
                allTags.map(tag => (
                  <Button
                    key={tag.id}
                    onClick={() => toggleTag(tag.id)}
                    variant={selectedTags.includes(tag.id) ? 'primary' : 'secondary'}
                    size="sm"
                    className="rounded-full"
                    style={{
                      backgroundColor: tag.color || '#6366f1',
                      color: 'white',
                      opacity: selectedTags.includes(tag.id) ? 1 : 0.6
                    }}
                    title={tag.description}
                  >
                    {tag.name}
                  </Button>
                ))
              ) : (
                <p className="text-pf-text-secondary text-sm">No tags available</p>
              )}
            </div>
          </div>
        )}
      </div>

      {/* Hierarchical File Browser */}
      {showBrowser && (
        <div className="bg-pf-bg-1 rounded-lg shadow-lg border border-pf-border p-6">
          <div className="flex items-center justify-between mb-4">
            <h3 className="text-lg font-semibold text-pf-text-primary flex items-center gap-2">
              <FolderOpen className="w-5 h-5" />
              Browse Models by Folder
            </h3>
            <Button
              variant="subtle"
              size="sm"
              onClick={() => setShowBrowser(false)}
              title="Close browser"
            >
              <X className="w-4 h-4" />
            </Button>
          </div>
          <HierarchicalFileBrowser
            endpoint="models"
            initialPath="/"
            showThumbnails={true}
            onFileSelect={(file: FileEntry) => {
              if (!file.isDirectory) {
                navigate(`/models?view=browser&path=${encodeURIComponent(file.path)}`);
              }
            }}
            onFileDelete={() => {
              queryClient.invalidateQueries({ queryKey: ['models-search'] });
            }}
          />
        </div>
      )}

      {/* Models Display */}
      {models.length === 0 && !showBrowser ? (
        <div className="text-center py-12">
          <Box className="w-12 h-12 text-pf-text-tertiary mx-auto mb-4 opacity-50" />
          <p className="text-pf-text-secondary">No models found</p>
        </div>
      ) : viewMode === 'grid' ? (
        // Grid View
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
          {models.map((model: Model) => (
            <div key={model.id} className="bg-pf-bg-1 rounded-lg shadow-lg border border-pf-border overflow-hidden flex flex-col">
              {/* Model Preview */}
              <div className="flex-1 aspect-square bg-pf-bg-2 relative flex items-center justify-center min-h-64">
                {model.thumbnailUrl ? (
                  <img
                    src={model.thumbnailUrl}
                    alt={model.name}
                    className="w-full h-full object-contain"
                  />
                ) : (
                  <div className="w-full h-full flex items-center justify-center">
                    <Box className="w-12 h-12 text-pf-text-tertiary" />
                  </div>
                )}

                {/* Quick actions overlay */}
                <div className="absolute top-2 right-2 flex space-x-1">
                  <Button
                    onMouseEnter={() => (ModelViewer as typeof ModelViewer).preload?.()}
                    onFocus={() => (ModelViewer as typeof ModelViewer).preload?.()}
                    onClick={() => setViewerModel(model)}
                    variant="secondary"
                    size="sm"
                    title="View 3D Model"
                  >
                    <Eye className="w-4 h-4" />
                  </Button>
                </div>
              </div>

              {/* Model Info */}
              <div className="p-4 flex-1 flex flex-col">
                <h3 className="font-medium text-lg mb-1 text-pf-text-primary">{model.name}</h3>

                {/* Tags */}
                {model.tags && model.tags.length > 0 && (
                  <div className="flex flex-wrap gap-1 mb-2">
                    {model.tags.map(tag => (
                      <span
                        key={tag.id}
                        className="inline-block px-2 py-1 text-xs rounded text-white"
                        style={{ backgroundColor: tag.color || '#6366f1' }}
                      >
                        {tag.name}
                      </span>
                    ))}
                  </div>
                )}

                <div className="text-sm text-pf-text-secondary space-y-1 mb-4 flex-1">
                  {model.fileType && <div>Type: {model.fileType.toUpperCase()}</div>}
                  {typeof model.fileSize === 'number' && <div>Size: {formatFileSize(model.fileSize)}</div>}
                  <div>Uploaded: {new Date(model.uploadedAt || (model as { createdAt?: string; updatedAt?: string }).createdAt || (model as { updatedAt?: string }).updatedAt || Date.now()).toLocaleDateString()}</div>
                </div>

                {/* Actions */}
                <div className="flex space-x-2">
                  <Button
                    onClick={() => navigate(`/models/${model.id}`)}
                    variant="secondary"
                    size="sm"
                    title="View Details"
                    className="flex-1"
                  >
                    <FileText className="w-4 h-4 mr-1" />
                    Details
                  </Button>
                  <Button
                    onClick={() => navigate(`/jobs/new?modelId=${model.id}`)}
                    variant="primary"
                    size="sm"
                    title="Slice this model"
                    className="flex-1"
                  >
                    <Settings className="w-4 h-4 mr-1" />
                    Slice
                  </Button>
                  <Button
                    onClick={() => deleteMutation.mutate(model.id)}
                    disabled={deleteMutation.isPending}
                    variant="danger"
                    size="sm"
                    title="Delete Model"
                  >
                    <Trash2 className="w-4 h-4" />
                  </Button>
                </div>
              </div>
            </div>
          ))}
        </div>
      ) : (
        // List View
        <div className="bg-pf-bg-1 rounded-lg shadow-lg border border-pf-border overflow-hidden">
          <table className="w-full">
            <thead>
              <tr className="border-b border-pf-border bg-pf-bg-2">
                <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary">Name</th>
                <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary">Type</th>
                <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary">Size</th>
                <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary">Tags</th>
                <th className="px-4 py-3 text-left text-sm font-medium text-pf-text-primary">Uploaded</th>
                <th className="px-4 py-3 text-right text-sm font-medium text-pf-text-primary">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-pf-border">
              {models.map((model: Model) => (
                <tr key={model.id} className="hover:bg-pf-bg-2 transition-colors">
                  <td className="px-4 py-3">
                    <div className="font-medium text-pf-text-primary">{model.name}</div>
                  </td>
                  <td className="px-4 py-3 text-pf-text-secondary">{model.fileType?.toUpperCase() || '-'}</td>
                  <td className="px-4 py-3 text-pf-text-secondary">{typeof model.fileSize === 'number' ? formatFileSize(model.fileSize) : '-'}</td>
                  <td className="px-4 py-3">
                    {model.tags && model.tags.length > 0 ? (
                      <div className="flex flex-wrap gap-1">
                        {model.tags.map(tag => (
                          <span
                            key={tag.id}
                            className="inline-block px-2 py-1 text-xs rounded text-white"
                            style={{ backgroundColor: tag.color || '#6366f1' }}
                          >
                            {tag.name}
                          </span>
                        ))}
                      </div>
                    ) : (
                      <span className="text-pf-text-tertiary">-</span>
                    )}
                  </td>
                  <td className="px-4 py-3 text-pf-text-secondary text-sm">
                    {new Date(model.uploadedAt || (model as { createdAt?: string }).createdAt || Date.now()).toLocaleDateString()}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <div className="flex justify-end gap-2">
                      <Button
                        onMouseEnter={() => (ModelViewer as typeof ModelViewer).preload?.()}
                        onClick={() => setViewerModel(model)}
                        variant="subtle"
                        size="sm"
                        title="View 3D Model"
                      >
                        <Eye className="w-4 h-4" />
                      </Button>
                      <Button
                        onClick={() => navigate(`/models/${model.id}`)}
                        variant="subtle"
                        size="sm"
                        title="View Details"
                      >
                        <FileText className="w-4 h-4" />
                      </Button>
                      <Button
                        onClick={() => navigate(`/jobs/new?modelId=${model.id}`)}
                        variant="subtle"
                        size="sm"
                        title="Slice Model"
                      >
                        <Settings className="w-4 h-4" />
                      </Button>
                      <Button
                        onClick={() => deleteMutation.mutate(model.id)}
                        disabled={deleteMutation.isPending}
                        variant="danger"
                        size="sm"
                        title="Delete Model"
                      >
                        <Trash2 className="w-4 h-4" />
                      </Button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* Model Viewer Modal */}
      {viewerModel && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
          <div className="bg-pf-bg-1 rounded-lg shadow-xl max-w-4xl w-full max-h-[90vh] overflow-y-auto border border-pf-border">
            <div className="flex items-center justify-between p-4 border-b border-pf-border">
              <h3 className="font-medium text-lg text-pf-text-primary">{viewerModel.name}</h3>
              <Button
                onClick={() => setViewerModel(null)}
                variant="subtle"
                size="sm"
              >
                <X className="w-5 h-5" />
              </Button>
            </div>
            <div className="p-4">
              <Suspense fallback={<ViewerSkeleton variant="model" />}>
                {viewerModel.url && viewerModel.fileType && (
                  <ModelViewer
                    modelUrl={viewerModel.url}
                    fileType={viewerModel.fileType}
                    className="h-96 w-full"
                  />
                )}
              </Suspense>
            </div>
          </div>
        </div>
      )}

      {/* G-code Viewer Modal */}
      {gcodeViewer && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
          <div className="bg-pf-bg-1 rounded-lg shadow-xl max-w-6xl w-full max-h-[90vh] overflow-y-auto border border-pf-border">
            <div className="flex items-center justify-between p-4 border-b border-pf-border">
              <h3 className="font-medium text-lg text-pf-text-primary">{gcodeViewer.name}</h3>
              <Button
                onClick={() => setGcodeViewer(null)}
                variant="subtle"
                size="sm"
              >
                <X className="w-5 h-5" />
              </Button>
            </div>
            <div className="p-4">
              <Suspense fallback={<ViewerSkeleton variant="gcode" />}>
                <GCodeViewer gcodeUrl={gcodeViewer.url} />
              </Suspense>
            </div>
          </div>
        </div>
      )}

      {/* Bulk Tag Assignment Modal */}
      <BulkTagAssignmentModal
        isOpen={showBulkTagModal}
        onClose={() => setShowBulkTagModal(false)}
      />
    </PageTemplate>
  );
};
