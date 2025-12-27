import React, { useState, useCallback, Suspense } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { DeleteIcon, CloseIcon, SettingsIcon, CubeIcon, EyeIcon, TagIcon, GridViewIcon, ListViewIcon, FileIcon, FolderIcon, UploadIcon, SearchIcon } from '@/components/icons/MdiIcons';
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
  const [showUploadPanel, setShowUploadPanel] = useState(false);
  const [showBrowser, setShowBrowser] = useState(false);
  const [viewerModel, setViewerModel] = useState<Model | null>(null);
  const [gcodeViewer, setGcodeViewer] = useState<GCodeFile | null>(null);
  // Slicing now redirects to NewSliceJobPage instead of using modal
  const [uploadProgress, setUploadProgress] = useState<Record<string, number>>({});
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedTags, setSelectedTags] = useState<string[]>([]);
  const [viewMode, setViewMode] = useState<'grid' | 'list'>('grid');
  const [showFiltersPanel, setShowFiltersPanel] = useState(true);
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
        icon={CubeIcon}
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
      icon={CubeIcon}
      maxWidth="max-w-7xl"
    >
      <div className="grid grid-cols-1 lg:grid-cols-4 gap-6 h-full">
        {/* ===== SIDEBAR ===== */}
        <div className="lg:col-span-1 space-y-4">
          {/* Upload Section */}
          <div className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-hidden">
            <button
              onClick={() => setShowUploadPanel(!showUploadPanel)}
              className="w-full px-4 py-3 flex items-center justify-between hover:bg-pf-bg-2 transition-colors"
            >
              <span className="font-semibold text-pf-text-primary flex items-center gap-2">
                <UploadIcon className="w-5 h-5" />
                Upload Models
              </span>
              <svg className={`w-5 h-5 transition-transform ${showUploadPanel ? 'rotate-180' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 14l-7 7m0 0l-7-7m7 7V3" />
              </svg>
            </button>

            {showUploadPanel && (
              <div className="border-t border-pf-border p-4 space-y-3">
                {/* Upload Area */}
                <div
                  className={`border-2 border-dashed rounded-lg p-6 text-center transition-colors cursor-pointer ${
                    dragOver ? 'border-pf-accent bg-pf-accent-bg bg-opacity-20' : 'border-pf-border hover:border-pf-accent'
                  }`}
                  onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
                  onDragLeave={() => setDragOver(false)}
                  onDrop={handleDrop}
                >
                  <div className="flex flex-col items-center space-y-2">
                    <CubeIcon className="w-8 h-8 text-pf-text-tertiary" />
                    <p className="text-xs font-medium text-pf-text-secondary">Drag files here</p>
                    <p className="text-xs text-pf-text-tertiary">or click to browse</p>
                  </div>
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
                    buttonText="Browse"
                    buttonVariant="secondary"
                    className="mt-3"
                  />
                </div>

                {/* Selected files list */}
                {selectedFiles.length > 0 && (
                  <div className="space-y-2">
                    <div className="flex items-center justify-between">
                      <h4 className="text-sm font-medium text-pf-text-primary">{selectedFiles.length} file(s) selected</h4>
                      <Button
                        onClick={() => setSelectedFiles([])}
                        variant="subtle"
                        size="sm"
                      >
                        <CloseIcon className="w-3 h-3" />
                      </Button>
                    </div>
                    <div className="max-h-32 overflow-y-auto space-y-1">
                      {selectedFiles.map((file, index) => (
                        <div key={index} className="flex items-center justify-between bg-pf-bg-2 p-2 rounded text-xs">
                          <span className="truncate text-pf-text-secondary">{file.name}</span>
                          <Button
                            onClick={() => removeFile(index)}
                            variant="subtle"
                            size="sm"
                          >
                            <DeleteIcon className="w-3 h-3" />
                          </Button>
                        </div>
                      ))}
                    </div>

                    {/* Upload progress */}
                    {Object.entries(uploadProgress).length > 0 && (
                      <div className="space-y-1">
                        {Object.entries(uploadProgress).map(([name, progress]) => (
                          <div key={name} className="text-xs">
                            <div className="flex justify-between mb-1">
                              <span className="truncate text-pf-text-secondary">{name}</span>
                              <span className="text-pf-text-tertiary">{progress}%</span>
                            </div>
                            <div className="h-1 bg-pf-bg-0 rounded-full border border-pf-border overflow-hidden">
                              <div className="bg-pf-accent h-full transition-all" style={{ width: `${progress}%` }} />
                            </div>
                          </div>
                        ))}
                      </div>
                    )}

                    {/* Upload button */}
                    <Button
                      onClick={uploadFiles}
                      disabled={uploadMutation.isPending}
                      variant="primary"
                      size="sm"
                      className="w-full mt-2"
                    >
                      <UploadIcon className="w-4 h-4 mr-1" />
                      {uploadMutation.isPending ? 'Uploading...' : 'Upload All'}
                    </Button>
                  </div>
                )}

                {selectedFiles.length === 0 && Object.entries(uploadProgress).length === 0 && (
                  <p className="text-xs text-pf-text-tertiary text-center py-2">
                    STL, 3MF, OBJ, PLY
                  </p>
                )}
              </div>
            )}
          </div>

          {/* Filters Section */}
          <div className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-hidden">
            <button
              onClick={() => setShowFiltersPanel(!showFiltersPanel)}
              className="w-full px-4 py-3 flex items-center justify-between hover:bg-pf-bg-2 transition-colors"
            >
              <span className="font-semibold text-pf-text-primary flex items-center gap-2">
                <TagIcon className="w-5 h-5" />
                Filters
              </span>
              <span className="text-xs bg-pf-accent text-white px-2 py-0.5 rounded-full">
                {selectedTags.length > 0 ? selectedTags.length : 0}
              </span>
            </button>

            {showFiltersPanel && (
              <div className="border-t border-pf-border p-4 space-y-3 max-h-96 overflow-y-auto">
                {/* Clear filters */}
                {selectedTags.length > 0 && (
                  <Button
                    onClick={() => setSelectedTags([])}
                    variant="secondary"
                    size="sm"
                    className="w-full text-xs"
                  >
                    Clear All Filters
                  </Button>
                )}

                {/* Tag list */}
                {allTags.length > 0 ? (
                  <div className="space-y-2">
                    {allTags.map(tag => (
                      <button
                        key={tag.id}
                        onClick={() => toggleTag(tag.id)}
                        className={`w-full flex items-center gap-2 px-3 py-2 rounded text-sm transition-colors ${
                          selectedTags.includes(tag.id)
                            ? 'bg-pf-accent text-white'
                            : 'bg-pf-bg-2 text-pf-text-primary hover:bg-pf-border'
                        }`}
                        title={tag.description}
                      >
                        <div
                          className="w-3 h-3 rounded-full flex-shrink-0"
                          style={{ backgroundColor: tag.color || '#6366f1' }}
                        />
                        <span className="flex-1 text-left truncate">{tag.name}</span>
                        {selectedTags.includes(tag.id) && (
                          <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 20 20">
                            <path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" />
                          </svg>
                        )}
                      </button>
                    ))}
                  </div>
                ) : (
                  <p className="text-xs text-pf-text-tertiary text-center py-3">No tags available</p>
                )}
              </div>
            )}
          </div>

          {/* Tools Section */}
          <div className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-hidden p-4 space-y-2">
            <h4 className="text-xs font-semibold text-pf-text-tertiary uppercase tracking-wide">Tools</h4>
            
            <Button
              onClick={() => setShowBrowser(!showBrowser)}
              variant={showBrowser ? 'primary' : 'secondary'}
              size="sm"
              className="w-full justify-start"
            >
              <FolderIcon className="w-4 h-4 mr-2" />
              {showBrowser ? 'Hide' : 'File'} Browser
            </Button>

            <Button
              onClick={() => setShowBulkTagModal(true)}
              variant="secondary"
              size="sm"
              className="w-full justify-start"
            >
              <TagIcon className="w-4 h-4 mr-2" />
              Bulk Tag
            </Button>
          </div>
        </div>

        {/* ===== MAIN CONTENT ===== */}
        <div className="lg:col-span-3 space-y-4">
          {/* Search Bar */}
          <div className="relative">
            <SearchIcon className="w-5 h-5 absolute left-3 top-1/2 -translate-y-1/2 text-pf-text-tertiary pointer-events-none" />
            <Input
              type="text"
              placeholder="Search models by name or description..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="pl-10 h-10"
            />
          </div>

          {/* Content Controls */}
          <div className="flex items-center justify-between">
            <p className="text-sm text-pf-text-secondary">
              {models.length} model{models.length !== 1 ? 's' : ''}
              {selectedTags.length > 0 && ` (filtered)`}
            </p>

            <div className="flex items-center gap-2">
              <div className="flex gap-1 bg-pf-bg-1 border border-pf-border rounded p-1">
                <Button
                  onClick={() => setViewMode('grid')}
                  variant={viewMode === 'grid' ? 'primary' : 'secondary'}
                  size="sm"
                  title="Grid view"
                  className="px-2"
                >
                  <GridViewIcon className="w-4 h-4" />
                </Button>
                <Button
                  onClick={() => setViewMode('list')}
                  variant={viewMode === 'list' ? 'primary' : 'secondary'}
                  size="sm"
                  title="List view"
                  className="px-2"
                >
                  <ListViewIcon className="w-4 h-4" />
                </Button>
              </div>
            </div>
          </div>

          {/* File Browser */}
          {showBrowser && (
            <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-4">
              <div className="flex items-center justify-between mb-4">
                <h3 className="text-sm font-semibold text-pf-text-primary">Browse by Folder</h3>
                <Button
                  variant="subtle"
                  size="sm"
                  onClick={() => setShowBrowser(false)}
                  title="Close browser"
                >
                  <CloseIcon className="w-4 h-4" />
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
          {models.length === 0 ? (
            <div className="bg-pf-bg-1 border border-pf-border rounded-lg py-12 text-center">
              <CubeIcon className="w-12 h-12 text-pf-text-tertiary mx-auto mb-3 opacity-50" />
              <p className="text-pf-text-secondary">No models found</p>
              <p className="text-xs text-pf-text-tertiary mt-1">
                {selectedTags.length > 0 ? 'Try adjusting your filters' : 'Upload your first model to get started'}
              </p>
            </div>
          ) : viewMode === 'grid' ? (
            // Grid View
            <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-3">
              {models.map((model: Model) => (
                <div key={model.id} className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-hidden hover:border-pf-accent hover:shadow-lg transition-all flex flex-col group">
                  {/* Model Preview */}
                  <div className="aspect-square bg-pf-bg-2 relative flex items-center justify-center min-h-32 overflow-hidden">
                    {model.thumbnailUrl ? (
                      <img
                        src={model.thumbnailUrl}
                        alt={model.name}
                        className="w-full h-full object-contain group-hover:scale-105 transition-transform"
                      />
                    ) : (
                      <CubeIcon className="w-12 h-12 text-pf-text-tertiary opacity-30" />
                    )}

                    {/* Quick View Button */}
                    <Button
                      onMouseEnter={() => (ModelViewer as typeof ModelViewer).preload?.()}
                      onFocus={() => (ModelViewer as typeof ModelViewer).preload?.()}
                      onClick={() => setViewerModel(model)}
                      variant="primary"
                      size="sm"
                      className="absolute inset-0 m-auto w-fit opacity-0 group-hover:opacity-100 transition-opacity"
                      title="View 3D Model"
                    >
                      <EyeIcon className="w-4 h-4 mr-1" />
                      View
                    </Button>
                  </div>

                  {/* Model Info */}
                  <div className="p-2.5 flex-1 flex flex-col">
                    <h3 className="font-semibold text-pf-text-primary line-clamp-2 mb-1.5 text-sm">{model.name}</h3>

                    {/* Tags */}
                    {model.tags && model.tags.length > 0 && (
                      <div className="flex flex-wrap gap-0.5 mb-2">
                        {model.tags.slice(0, 1).map(tag => (
                          <span
                            key={tag.id}
                            className="inline-block px-1.5 py-0.5 text-xs rounded text-white"
                            style={{ backgroundColor: tag.color || '#6366f1' }}
                          >
                            {tag.name}
                          </span>
                        ))}
                        {model.tags.length > 1 && (
                          <span className="inline-block px-1.5 py-0.5 text-xs rounded bg-pf-bg-2 text-pf-text-secondary">
                            +{model.tags.length - 1}
                          </span>
                        )}
                      </div>
                    )}

                    {/* Metadata */}
                    <div className="text-xs text-pf-text-secondary space-y-0.5 mb-2 flex-1">
                      {model.fileType && <div className="flex justify-between gap-1"><span>Type:</span> <span className="font-medium text-right">{model.fileType.toUpperCase()}</span></div>}
                      {typeof model.fileSize === 'number' && <div className="flex justify-between gap-1"><span>Size:</span> <span className="font-medium text-right">{formatFileSize(model.fileSize)}</span></div>}
                    </div>

                    {/* Actions */}
                    <div className="flex gap-1">
                      <Button
                        onClick={() => navigate(`/models/${model.id}`)}
                        variant="secondary"
                        size="sm"
                        className="flex-1 text-xs"
                        title="View Details"
                      >
                        Details
                      </Button>
                      <Button
                        onClick={() => navigate(`/jobs/new?modelId=${model.id}`)}
                        variant="primary"
                        size="sm"
                        className="flex-1 text-xs"
                        title="Slice this model"
                      >
                        Slice
                      </Button>
                      <Button
                        onClick={() => deleteMutation.mutate(model.id)}
                        disabled={deleteMutation.isPending}
                        variant="danger"
                        size="sm"
                        className="px-2"
                        title="Delete Model"
                      >
                        <DeleteIcon className="w-3 h-3" />
                      </Button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          ) : (
            // List View
            <div className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-pf-border bg-pf-bg-2">
                    <th className="px-4 py-3 text-left font-semibold text-pf-text-primary">Name</th>
                    <th className="px-4 py-3 text-left font-semibold text-pf-text-primary">Type</th>
                    <th className="px-4 py-3 text-left font-semibold text-pf-text-primary">Size</th>
                    <th className="px-4 py-3 text-left font-semibold text-pf-text-primary">Tags</th>
                    <th className="px-4 py-3 text-left font-semibold text-pf-text-primary">Uploaded</th>
                    <th className="px-4 py-3 text-right font-semibold text-pf-text-primary">Actions</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-pf-border">
                  {models.map((model: Model) => (
                    <tr key={model.id} className="hover:bg-pf-bg-2 transition-colors">
                      <td className="px-4 py-3">
                        <button
                          onClick={() => navigate(`/models/${model.id}`)}
                          className="font-medium text-pf-accent hover:underline text-left"
                        >
                          {model.name}
                        </button>
                      </td>
                      <td className="px-4 py-3 text-pf-text-secondary text-xs font-medium">
                        {model.fileType?.toUpperCase() || '—'}
                      </td>
                      <td className="px-4 py-3 text-pf-text-secondary text-xs">
                        {typeof model.fileSize === 'number' ? formatFileSize(model.fileSize) : '—'}
                      </td>
                      <td className="px-4 py-3">
                        {model.tags && model.tags.length > 0 ? (
                          <div className="flex flex-wrap gap-1">
                            {model.tags.slice(0, 2).map(tag => (
                              <span
                                key={tag.id}
                                className="inline-block px-2 py-0.5 text-xs rounded text-white"
                                style={{ backgroundColor: tag.color || '#6366f1' }}
                              >
                                {tag.name}
                              </span>
                            ))}
                            {model.tags.length > 2 && (
                              <span className="text-xs text-pf-text-secondary">+{model.tags.length - 2}</span>
                            )}
                          </div>
                        ) : (
                          <span className="text-pf-text-tertiary">—</span>
                        )}
                      </td>
                      <td className="px-4 py-3 text-pf-text-secondary text-xs">
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
                            <EyeIcon className="w-4 h-4" />
                          </Button>
                          <Button
                            onClick={() => navigate(`/models/${model.id}`)}
                            variant="subtle"
                            size="sm"
                            title="View Details"
                          >
                            <FileIcon className="w-4 h-4" />
                          </Button>
                          <Button
                            onClick={() => navigate(`/jobs/new?modelId=${model.id}`)}
                            variant="subtle"
                            size="sm"
                            title="Slice Model"
                          >
                            <SettingsIcon className="w-4 h-4" />
                          </Button>
                          <Button
                            onClick={() => deleteMutation.mutate(model.id)}
                            disabled={deleteMutation.isPending}
                            variant="danger"
                            size="sm"
                            title="Delete Model"
                          >
                            <DeleteIcon className="w-4 h-4" />
                          </Button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </div>

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
                <CloseIcon className="w-5 h-5" />
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
                <CloseIcon className="w-5 h-5" />
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
