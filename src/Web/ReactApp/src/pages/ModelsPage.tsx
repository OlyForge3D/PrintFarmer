import React, { useState, useCallback, Suspense } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { Upload, Box, Trash2, Eye, Settings, Search, Tag, Grid3x3, List, X, FileText } from 'lucide-react';
import { PageTemplate } from '@/components/PageTemplate';
import { BulkTagAssignmentModal } from '@/components/modals/BulkTagAssignmentModal';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';
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
  const { data: availablePrinters = [] } = useQuery({
    queryKey: ['printers-fast'],
    queryFn: async () => {
      const response = await fetch(`${getApiBaseUrl()}/printers/fast`, { headers: getAuthHeaders() });
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

  const handleFileSelect = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files) {
      const files = Array.from(e.target.files).filter(file =>
        ['stl', '3mf', 'obj', 'ply'].includes(file.name.split('.').pop()?.toLowerCase() || '')
      );
      setSelectedFiles(prev => [...prev, ...files]);
    }
  };

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
              <input
                id="file-upload"
                type="file"
                multiple
                accept=".stl,.3mf,.obj,.ply"
                onChange={handleFileSelect}
                className="hidden"
              />
            </div>
          </div>
        </div>

        {/* Selected files */}
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
                    <button
                      onClick={() => removeFile(index)}
                      className="p-1 hover:bg-pf-bg-1 rounded"
                      aria-label="Remove file"
                      title="Remove file"
                    >
                      <Trash2 className="w-4 h-4 text-pf-text-tertiary" />
                    </button>
                  </div>
                </div>
              ))}
            </div>
            <div className="mt-4 flex justify-end">
              <button
                onClick={uploadFiles}
                disabled={uploadMutation.isPending}
                className="px-4 py-2 bg-pf-accent text-white rounded hover:bg-pf-success-hover disabled:opacity-50"
              >
                <Upload className="w-4 h-4 inline mr-2" />
                {uploadMutation.isPending ? 'Uploading...' : 'Upload Files'}
              </button>
            </div>
          </div>
        )}
      </div>

      {/* Search and Filter Bar */}
      <div className="space-y-4 mt-6">
        <div className="flex flex-col md:flex-row gap-4 items-stretch md:items-center">
          {/* Search box */}
          <div className="flex-1 relative">
            <Search className="w-5 h-5 absolute left-3 top-3 text-pf-text-tertiary pointer-events-none" />
            <input
              type="text"
              placeholder="Search models by name or description..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="w-full pl-10 pr-4 py-2 bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary placeholder-pf-text-tertiary focus:outline-none focus:ring-2 focus:ring-pf-accent"
            />
          </div>

          {/* Tag filter button */}
          <button
            onClick={() => setShowTagFilter(!showTagFilter)}
            className={`px-4 py-2 rounded border flex items-center gap-2 transition-colors ${selectedTags.length > 0
              ? 'bg-pf-accent-bg text-pf-accent border-pf-accent'
              : 'bg-pf-bg-1 text-pf-text-primary border-pf-border hover:bg-pf-bg-2'
              }`}
          >
            <Tag className="w-4 h-4" />
            Tags {selectedTags.length > 0 && `(${selectedTags.length})`}
          </button>

          {/* Bulk tagging button */}
          <button
            onClick={() => setShowBulkTagModal(true)}
            className="flex items-center gap-2 px-4 py-2 bg-pf-bg-1 border border-pf-border rounded hover:bg-pf-bg-2 text-sm font-medium text-pf-text-primary"
            title="Assign tags to multiple models at once"
          >
            <Tag className="w-4 h-4" />
            Bulk Tag
          </button>

          {/* View mode toggle */}
          <div className="flex gap-2 border border-pf-border rounded p-1 bg-pf-bg-1">
            <button
              onClick={() => setViewMode('grid')}
              className={`p-2 rounded transition-colors ${viewMode === 'grid'
                ? 'bg-pf-accent text-white'
                : 'text-pf-text-tertiary hover:bg-pf-bg-2'
                }`}
              title="Grid view"
            >
              <Grid3x3 className="w-4 h-4" />
            </button>
            <button
              onClick={() => setViewMode('list')}
              className={`p-2 rounded transition-colors ${viewMode === 'list'
                ? 'bg-pf-accent text-white'
                : 'text-pf-text-tertiary hover:bg-pf-bg-2'
                }`}
              title="List view"
            >
              <List className="w-4 h-4" />
            </button>
          </div>
        </div>

        {/* Tag filter dropdown */}
        {showTagFilter && (
          <div className="bg-pf-bg-1 border border-pf-border rounded p-4">
            <div className="flex items-center justify-between mb-3">
              <h4 className="font-medium text-pf-text-primary">Filter by Tags</h4>
              <button
                onClick={() => {
                  setShowTagFilter(false);
                  setSelectedTags([]);
                }}
                className="text-xs text-pf-text-tertiary hover:text-pf-text-primary"
              >
                Clear All
              </button>
            </div>
            <div className="flex flex-wrap gap-2">
              {allTags.length > 0 ? (
                allTags.map(tag => (
                  <button
                    key={tag.id}
                    onClick={() => toggleTag(tag.id)}
                    className={`px-3 py-1 rounded-full text-sm font-medium transition-all ${selectedTags.includes(tag.id)
                      ? 'ring-2 ring-offset-2 ring-offset-pf-bg-0 ring-pf-accent'
                      : ''
                      }`}
                    style={{
                      backgroundColor: tag.color || '#6366f1',
                      color: 'white',
                      opacity: selectedTags.includes(tag.id) ? 1 : 0.6
                    }}
                    title={tag.description}
                  >
                    {tag.name}
                  </button>
                ))
              ) : (
                <p className="text-pf-text-secondary text-sm">No tags available</p>
              )}
            </div>
          </div>
        )}
      </div>

      {/* Models Display */}
      {models.length === 0 ? (
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
                  <button
                    onMouseEnter={() => (ModelViewer as typeof ModelViewer).preload?.()}
                    onFocus={() => (ModelViewer as typeof ModelViewer).preload?.()}
                    onClick={() => setViewerModel(model)}
                    className="p-2 bg-pf-bg-1 bg-opacity-80 hover:bg-pf-bg-1 rounded shadow border border-pf-border"
                    title="View 3D Model"
                  >
                    <Eye className="w-4 h-4 text-pf-text-primary" />
                  </button>
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
                  <button
                    onClick={() => navigate(`/models/${model.id}`)}
                    className="flex-1 px-3 py-2 bg-pf-bg-2 text-pf-text-primary rounded hover:bg-pf-bg-1 text-sm font-medium border border-pf-border"
                    title="View Details"
                  >
                    <FileText className="w-4 h-4 inline mr-1" />
                    Details
                  </button>
                  <button
                    onClick={() => navigate(`/jobs/new?modelId=${model.id}`)}
                    className="flex-1 px-3 py-2 bg-pf-accent-bg bg-opacity-20 text-pf-accent rounded hover:bg-pf-accent-bg hover:bg-opacity-30 text-sm font-medium border border-pf-accent"
                    title="Slice this model"
                  >
                    <Settings className="w-4 h-4 inline mr-1" />
                    Slice
                  </button>
                  <button
                    onClick={() => deleteMutation.mutate(model.id)}
                    disabled={deleteMutation.isPending}
                    className="px-3 py-2 bg-pf-error-bg text-pf-error-text rounded hover:bg-pf-error border border-pf-error-border"
                    title="Delete Model"
                  >
                    <Trash2 className="w-4 h-4" />
                  </button>
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
                      <button
                        onMouseEnter={() => (ModelViewer as typeof ModelViewer).preload?.()}
                        onClick={() => setViewerModel(model)}
                        className="p-2 hover:bg-pf-bg-2 rounded text-pf-text-primary"
                        title="View 3D Model"
                      >
                        <Eye className="w-4 h-4" />
                      </button>
                      <button
                        onClick={() => navigate(`/models/${model.id}`)}
                        className="p-2 hover:bg-pf-bg-2 rounded text-pf-text-primary"
                        title="View Details"
                      >
                        <FileText className="w-4 h-4" />
                      </button>
                      <button
                        onClick={() => navigate(`/jobs/new?modelId=${model.id}`)}
                        className="p-2 hover:bg-pf-bg-2 rounded text-pf-accent"
                        title="Slice Model"
                      >
                        <Settings className="w-4 h-4" />
                      </button>
                      <button
                        onClick={() => deleteMutation.mutate(model.id)}
                        disabled={deleteMutation.isPending}
                        className="p-2 hover:bg-pf-error hover:bg-opacity-20 rounded text-pf-error"
                        title="Delete Model"
                      >
                        <Trash2 className="w-4 h-4" />
                      </button>
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
              <button
                onClick={() => setViewerModel(null)}
                className="p-1 hover:bg-pf-bg-2 rounded text-pf-text-primary"
              >
                <X className="w-5 h-5" />
              </button>
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
              <button
                onClick={() => setGcodeViewer(null)}
                className="p-1 hover:bg-pf-bg-2 rounded text-pf-text-primary"
              >
                <X className="w-5 h-5" />
              </button>
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
