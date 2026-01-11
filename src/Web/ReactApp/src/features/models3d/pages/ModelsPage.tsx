import React, { useState, Suspense } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useViewModePreference } from '@/common/hooks/useViewModePreference';
import { CloseIcon, CubeIcon, TagIcon, UploadIcon, FilterIcon, ArrowUpIcon, ArrowDownIcon } from '@/common/components/icons/MdiIcons';
import { PageTemplate } from '@/common/components/PageTemplate';
import { FileBrowserViewModeToggle } from '@/common/components/FileBrowserViewModeToggle';
import { BulkTagAssignmentModal } from '@/common/components/modals/BulkTagAssignmentModal';
import { ModelUploadModal } from '@/common/components/modals/ModelUploadModal';
import { TaggingModal } from '@/components/TaggingModal';
import { Button, Input } from '@/common/components/ui';
import TagInput from '@/components/TagInput';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';
import { lazyWithPreload } from '@/common/utils/lazyWithPreload';
import { ModelGridView } from '@/features/models3d/components/ModelGridView';
import { ModelListView } from '@/features/models3d/components/ModelListView';
import { ExplorerModelListView } from '@/features/models3d/components/ExplorerModelListView';
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
import { ViewerSkeleton } from '@/features/models3d/components/3d/ViewerSkeleton';
import type { Model, ModelTag } from '@/types/models';
import type { GCodeFile } from '@/types/gcode';

export const ModelsPage: React.FC = () => {
  const { viewMode, setViewMode } = useViewModePreference('printfarmer-models-viewmode');
  const [showUploadModal, setShowUploadModal] = useState(false);
  const [viewerModel, setViewerModel] = useState<Model | null>(null);
  const [gcodeViewer, setGcodeViewer] = useState<GCodeFile | null>(null);
  const [isViewerMaximized, setIsViewerMaximized] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [debouncedSearchQuery, setDebouncedSearchQuery] = useState('');
  const [selectedTags, setSelectedTags] = useState<string[]>([]);
  const [showFiltersPanel, setShowFiltersPanel] = useState(false);
  const [showBulkTagModal, setShowBulkTagModal] = useState(false);
  const [selectedModelForTagging, setSelectedModelForTagging] = useState<Model | null>(null);
  const [isTaggingModalOpen, setIsTaggingModalOpen] = useState(false);

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
              <FileBrowserViewModeToggle 
                viewMode={viewMode}
                onViewModeChange={setViewMode}
              />

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
            <div className="border-t border-pf-border pt-3 space-y-3">
              <div className="flex items-center justify-between mb-2">
                <h3 className="text-sm font-medium text-pf-text-primary">Filter by Tags</h3>
                {selectedTags.length > 0 && (
                  <Button onClick={() => setSelectedTags([])} variant="secondary" size="sm" className="text-xs">
                    Clear Filters
                  </Button>
                )}
              </div>
              <TagInput
                selectedTags={allTags.filter(t => selectedTags.includes(t.id))}
                onChange={(tags) => setSelectedTags(tags.map(t => t.id))}
                placeholder="Select tags to filter models..."
                maxTags={undefined}
              />
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
            <ExplorerModelListView
              onTagModel={(file) => {
                setSelectedModelForTagging({
                  id: file.modelId || file.path,
                  name: file.name || file.fileName,
                  fileName: file.fileName,
                  fileSize: file.size,
                  fileType: (file.fileName?.split('.').pop() || 'stl') as 'stl' | '3mf' | 'obj' | 'ply',
                  uploadedAt: file.modifiedAt,
                  thumbnailPath: file.thumbnailPath,
                  tags: file.tags || []
                });
                setIsTaggingModalOpen(true);
              }}
            />
          ) : viewMode === 'grid' ? (
            <ModelGridView
              models={models}
              isLoading={isLoading}
              onViewerModel={setViewerModel}
              onTagModel={(model) => {
                setSelectedModelForTagging(model);
                setIsTaggingModalOpen(true);
              }}
              formatFileSize={formatFileSize}
            />
          ) : (
            <ModelListView
              models={models}
              isLoading={isLoading}
              onViewerModel={setViewerModel}
              onTagModel={(model) => {
                setSelectedModelForTagging(model);
                setIsTaggingModalOpen(true);
              }}
              formatFileSize={formatFileSize}
            />
          )}
        </div>
      </div>

      {/* Model Viewer Modal */}
      {viewerModel && (
        <div className={`fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 ${isViewerMaximized ? 'p-2' : 'p-4'}`}>
          <div className={`bg-pf-bg-1 rounded-lg shadow-xl border border-pf-border flex flex-col ${
            isViewerMaximized 
              ? 'w-full h-full max-w-none max-h-none' 
              : 'max-w-4xl w-full max-h-[90vh]'
          }`}>
            <div className="flex items-center justify-between p-4 border-b border-pf-border flex-shrink-0">
              <h3 className="font-medium text-lg text-pf-text-primary">{viewerModel.name}</h3>
              <div className="flex items-center gap-2">
                <Button
                  onClick={() => setIsViewerMaximized(!isViewerMaximized)}
                  variant="subtle"
                  size="sm"
                  title={isViewerMaximized ? 'Restore size' : 'Maximize viewer'}
                >
                  {isViewerMaximized ? (
                    <ArrowDownIcon className="w-5 h-5" />
                  ) : (
                    <ArrowUpIcon className="w-5 h-5" />
                  )}
                </Button>
                <Button
                  onClick={() => {
                    setViewerModel(null);
                    setIsViewerMaximized(false);
                  }}
                  variant="subtle"
                  size="sm"
                >
                  <CloseIcon className="w-5 h-5" />
                </Button>
              </div>
            </div>
            <div className={`p-4 flex-1 ${isViewerMaximized ? 'overflow-hidden' : 'overflow-y-auto'}`}>
              <Suspense fallback={
                <ViewerSkeleton 
                  variant="model" 
                  className={isViewerMaximized ? 'h-full w-full' : 'h-[32rem] w-full'}
                />
              }>
                {viewerModel.url && viewerModel.fileType && (
                  <ModelViewer
                    modelUrl={viewerModel.url}
                    fileType={viewerModel.fileType}
                    showGrid={false} // Hide grid on Models page
                    className={isViewerMaximized ? 'h-full w-full' : 'h-[32rem] w-full'}
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

      {/* Tagging Modal */}
      {selectedModelForTagging && (
        <TaggingModal
          isOpen={isTaggingModalOpen}
          onClose={() => setIsTaggingModalOpen(false)}
          objectId={selectedModelForTagging.id}
          objectType="Model3D"
          initialTags={selectedModelForTagging.tags || []}
        />
      )}
    </PageTemplate>
  );
};
