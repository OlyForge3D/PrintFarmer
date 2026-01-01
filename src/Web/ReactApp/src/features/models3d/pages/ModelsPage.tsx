import React, { useState, Suspense } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate, Link } from 'react-router-dom';
import { useViewModePreference } from '@/common/hooks/useViewModePreference';
import { DeleteIcon, CloseIcon, LayersTripleOutlineIcon, CubeIcon, EyeIcon, TagIcon, GridViewIcon, ListViewIcon, FileIcon, FolderIcon, UploadIcon, FilterIcon } from '@/common/components/icons/MdiIcons';
import { PageTemplate } from '@/common/components/PageTemplate';
import { BulkTagAssignmentModal } from '@/common/components/modals/BulkTagAssignmentModal';
import { ModelUploadModal } from '@/common/components/modals/ModelUploadModal';
import { Button, Input } from '@/common/components/ui';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';
import { ExplorerFileBrowser } from '@/features/gcode/components/ExplorerFileBrowser';
// Lazy load heavy three.js based viewers with manual preload support
import { lazyWithPreload } from '@/common/utils/lazyWithPreload';
import type { ModelViewerProps } from '@/features/models3d/components/3d/ModelViewer3D';
import type { GCodeViewerProps } from '@/features/models3d/components/3d/GCodeViewer3D';
const ModelViewer = lazyWithPreload<ModelViewerProps, React.FC<ModelViewerProps>>(
  () => import('@/features/models3d/components/3d/ModelViewer3D').then(m => ({ default: m.ModelViewer }))
);
const GCodeViewer = lazyWithPreload<GCodeViewerProps, React.FC<GCodeViewerProps>>(
  () => import('@/features/models3d/components/3d/GCodeViewer3D').then(m => ({ default: m.GCodeViewer }))
);
// Slicing now redirects to NewSliceJobPage for better UX with 3D preview
// const SlicerConfigModal = lazyWithPreload<{...}>(...)
import { slicerService } from '@/services/slicerService';
import type { SlicedModelSummary } from '@/services/slicerService';
import { ViewerSkeleton } from '@/features/models3d/components/3d/ViewerSkeleton';
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
  const { viewMode, setViewMode } = useViewModePreference('printfarmer-models-viewmode');
  const [showUploadModal, setShowUploadModal] = useState(false);
  const [viewerModel, setViewerModel] = useState<Model | null>(null);
  const [gcodeViewer, setGcodeViewer] = useState<GCodeFile | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [debouncedSearchQuery, setDebouncedSearchQuery] = useState('');
  const [selectedTags, setSelectedTags] = useState<string[]>([]);
  const [showFiltersPanel, setShowFiltersPanel] = useState(false);
  const [showBulkTagModal, setShowBulkTagModal] = useState(false);

  const queryClient = useQueryClient();

  // Debounce search query
  React.useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedSearchQuery(searchQuery);
    }, 300);
    return () => clearTimeout(timer);
  }, [searchQuery]);

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
    queryKey: ['models-search', debouncedSearchQuery, selectedTags],
    queryFn: async () => {
      const response = await fetch(`${getApiBaseUrl()}/3d-models/search`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...getAuthHeaders()
        },
        body: JSON.stringify({
          query: debouncedSearchQuery || undefined,
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

  // Delete mutation
  const deleteMutation = useMutation({
    mutationFn: (modelId: string) => slicerService.deleteModel(modelId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['models-search'] });
    }
  });

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
      <div className="space-y-4 h-full flex flex-col">
        {/* Toolbar */}
        <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-4 space-y-3">
          {/* Top Row: Search and Controls */}
          <div className="flex flex-col sm:flex-row items-stretch sm:items-center gap-3">
            <Input
              type="text"
              placeholder="Search models..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="flex-1 min-w-0"
              autoComplete="off"
            />
            
            <div className="flex items-center gap-2 flex-wrap">
              <div className="flex gap-1 bg-pf-bg-0 border border-pf-border rounded p-1">
                <Button
                  onClick={() => setViewMode('explorer')}
                  variant={viewMode === 'explorer' ? 'primary' : 'secondary'}
                  size="sm"
                  title="Explorer view"
                  className="px-2"
                >
                  <FolderIcon className="w-4 h-4" />
                </Button>
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

              <Button
                onClick={() => setShowUploadModal(true)}
                variant="secondary"
                size="sm"
                className="whitespace-nowrap"
              >
                <UploadIcon className="w-4 h-4 mr-1" />
              </Button>

              <Button
                onClick={() => setShowFiltersPanel(!showFiltersPanel)}
                variant="secondary"
                size="sm"
                className="whitespace-nowrap"
              >
                <FilterIcon className="w-4 h-4 mr-1" />
                {selectedTags.length > 0 && (
                  <span className="ml-1.5 px-1.5 py-0.5 text-xs bg-pf-accent text-white rounded">
                    {selectedTags.length}
                  </span>
                )}
              </Button>

              {(selectedTags.length > 0 || models.length > 0) && (
                <Button
                  onClick={() => setShowBulkTagModal(true)}
                  variant="secondary"
                  size="sm"
                  className="whitespace-nowrap"
                >
                  <TagIcon className="w-4 h-4 mr-1" />
                </Button>
              )}
            </div>
          </div>

          {/* Filters Panel */}
          {showFiltersPanel && (
            <div className="border-t border-pf-border pt-3 space-y-2">
              {selectedTags.length > 0 && (
                <Button onClick={() => setSelectedTags([])} variant="secondary" size="sm" className="w-full text-xs">
                  Clear All Filters
                </Button>
              )}
              <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-2">
                {allTags.length > 0 ? (
                  allTags.map(tag => (
                    <Button
                      key={tag.id}
                      onClick={() => toggleTag(tag.id)}
                      variant={selectedTags.includes(tag.id) ? "primary" : "secondary"}
                      size="sm"
                      className="flex items-center gap-1.5 text-xs"
                      title={tag.description}
                    >
                      <div
                        className="w-2 h-2 rounded-full flex-shrink-0"
                        style={{ backgroundColor: tag.color || 'var(--pf-accent)' }}
                      />
                      <span className="truncate">{tag.name}</span>
                    </Button>
                  ))
                ) : (
                  <p className="text-xs text-pf-text-tertiary col-span-full text-center py-2">No tags available</p>
                )}
              </div>
            </div>
          )}
        </div>

        {/* Content Area - Full Width */}
        <div className="flex-1 min-h-0 flex flex-col">
          {models.length === 0 && viewMode !== 'explorer' ? (
            <div className="bg-pf-bg-1 border border-pf-border rounded-lg py-12 text-center">
              <CubeIcon className="w-12 h-12 text-pf-text-tertiary mx-auto mb-3 opacity-50" />
              <p className="text-pf-text-secondary">No models found</p>
              <p className="text-xs text-pf-text-tertiary mt-1">
                {selectedTags.length > 0 ? 'Try adjusting your filters' : 'Upload your first model to get started'}
              </p>
            </div>
          ) : viewMode === 'explorer' ? (
            <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-4 h-full">
              <ExplorerFileBrowser endpoint="models" />
            </div>
          ) : viewMode === 'grid' ? (
            <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-3 overflow-y-auto">
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
                            style={{ backgroundColor: tag.color || 'var(--pf-accent)' }}
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
                    <div className="flex gap-2">
                      {model.fileType !== '3mf' && (
                        <Button
                          onMouseEnter={() => (ModelViewer as typeof ModelViewer).preload?.()}
                          onClick={() => setViewerModel(model)}
                          variant="secondary"
                          size="sm"
                          className="flex-1"
                          title="View 3D Model"
                        >
                          <EyeIcon className="w-4 h-4" />
                        </Button>
                      )}
                      <Button
                        onClick={() => navigate(`/models/${model.id}`)}
                        variant="secondary"
                        size="sm"
                        className="flex-1"
                        title="View Details"
                      >
                        <FileIcon className="w-4 h-4" />
                      </Button>
                      <Button
                        onClick={() => navigate(`/jobs/new?modelId=${model.id}`)}
                        variant="primary"
                        size="sm"
                        className="flex-1"
                        title="Slice Model"
                      >
                        <LayersTripleOutlineIcon className="w-4 h-4" />
                      </Button>
                      <Button
                        onClick={() => deleteMutation.mutate(model.id)}
                        disabled={deleteMutation.isPending}
                        variant="danger"
                        size="sm"
                        className="px-2"
                        title="Delete Model"
                      >
                        <DeleteIcon className="w-4 h-4" />
                      </Button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          ) : (
            // List View
            <div className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-x-auto flex-1">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-pf-border bg-pf-bg-2 sticky top-0">
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
                        <div className="flex items-center gap-3 min-w-0">
                          {/* Thumbnail */}
                          <div className="w-12 h-12 flex-shrink-0 rounded bg-pf-bg-2 flex items-center justify-center border border-pf-border overflow-hidden">
                            {model.thumbnailUrl ? (
                              <img
                                src={model.thumbnailUrl}
                                alt={model.name}
                                className="w-full h-full object-contain"
                              />
                            ) : (
                              <CubeIcon className="w-6 h-6 text-pf-text-tertiary opacity-50" />
                            )}
                          </div>
                          {/* Filename as link, not button */}
                          <Link
                            to={`/models/${model.id}`}
                            className="font-medium text-pf-accent hover:underline text-left min-w-0 truncate"
                          >
                            {model.name}
                          </Link>
                        </div>
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
                                style={{ backgroundColor: tag.color || 'var(--pf-accent)' }}
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
                          {model.fileType !== '3mf' && (
                            <Button
                              onMouseEnter={() => (ModelViewer as typeof ModelViewer).preload?.()}
                              onClick={() => setViewerModel(model)}
                              variant="subtle"
                              size="sm"
                              title="View 3D Model"
                            >
                              <EyeIcon className="w-4 h-4" />
                            </Button>
                          )}
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
                            <LayersTripleOutlineIcon className="w-4 h-4" />
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

      {/* Model Upload Modal */}
      <ModelUploadModal
        isOpen={showUploadModal}
        onClose={() => setShowUploadModal(false)}
      />

      {/* Bulk Tag Assignment Modal */}
      <BulkTagAssignmentModal
        isOpen={showBulkTagModal}
        onClose={() => setShowBulkTagModal(false)}
      />
    </PageTemplate>
  );
};
