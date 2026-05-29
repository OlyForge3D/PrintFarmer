import React, { useState, Suspense } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useViewModePreference } from '@/common/hooks/useViewModePreference';
import { useKeyboardShortcuts } from '@/common/hooks/useKeyboardShortcuts';
import { PageTemplate } from '@/common/components/PageTemplate';
import { CloseIcon, PlusIcon } from '@/common/components/icons/MdiIcons';
import { BulkTagAssignmentModal } from '@/common/components/modals/BulkTagAssignmentModal';
import { FloatingActionButton } from '@/common/components/FloatingActionButton';
import { TaggingModal } from '@/components/TaggingModal';
import { Button } from '@/common/components/ui';
import TagInput from '@/components/TagInput';
import { apiClient } from '@/services/api';
import { lazyWithPreload } from '@/common/utils/lazyWithPreload';
import { ModelsFileBrowser } from '@/features/models3d/components/ModelsFileBrowser';
import type { ModelViewerProps } from '@/features/models3d/components/3d/ModelViewer3D';
import type { GCodeViewerProps } from '@/features/models3d/components/3d/GCodeViewer3D';
const ModelViewer = lazyWithPreload<ModelViewerProps, React.FC<ModelViewerProps>>(
  () => import('@/features/models3d/components/3d/ModelViewer3D').then(m => ({ default: m.ModelViewer }))
);
const GCodeViewer = lazyWithPreload<GCodeViewerProps, React.FC<GCodeViewerProps>>(
  () => import('@/features/models3d/components/3d/GCodeViewer3D').then(m => ({ default: m.GCodeViewer }))
);
import { ViewerSkeleton } from '@/features/models3d/components/3d/ViewerSkeleton';
import type { Model, ModelTag } from '@/types/models';
import type { GCodeFile } from '@/types/gcode';

export const ModelsPage: React.FC = () => {
  const { viewMode, setViewMode } = useViewModePreference('printfarmer-models-viewmode');
  const [viewerModel, setViewerModel] = useState<Model | null>(null);
  const [gcodeViewer, setGcodeViewer] = useState<GCodeFile | null>(null);
  const [selectedTags, setSelectedTags] = useState<string[]>([]);
  const [showFiltersPanel, setShowFiltersPanel] = useState(false);
  const [showBulkTagModal, setShowBulkTagModal] = useState(false);
  const [selectedModelForTagging, setSelectedModelForTagging] = useState<Model | null>(null);
  const [selectedModelIds, setSelectedModelIds] = useState<string[]>([]);

  const { data: allTags = [] } = useQuery<ModelTag[]>({
    queryKey: ['model-tags'],
    queryFn: async () => {
      const result = await apiClient.getTags();
      return (result as unknown as ModelTag[]) || [];
    },
    staleTime: 5 * 60 * 1000,
    gcTime: 10 * 60 * 1000
  });

  // Keyboard shortcuts for models page actions
  useKeyboardShortcuts([
    {
      key: 'u',
      handler: () => {
        // Upload is handled by ModelsFileBrowser
        const uploadButton = document.querySelector('[title="Upload models"]') as HTMLButtonElement;
        uploadButton?.click();
      },
      description: 'Upload new model'
    },
    {
      key: 'v',
      handler: () => {
        const viewModes: Array<'grid' | 'explorer'> = ['grid', 'explorer'];
        const currentIndex = viewModes.indexOf(viewMode as 'grid' | 'explorer');
        const nextIndex = (currentIndex + 1) % viewModes.length;
        setViewMode(viewModes[nextIndex]);
      },
      description: 'Cycle view mode (Grid → Explorer)'
    },
    {
      key: 'f',
      handler: () => setShowFiltersPanel(!showFiltersPanel),
      description: 'Toggle filters'
    },
    {
      key: 't',
      handler: () => {
        if (selectedModelIds.length > 0) {
          setShowBulkTagModal(true);
        }
      },
      description: 'Tag selected models'
    }
  ]);

  return (
    <PageTemplate
      title="Models"
      subtitle="Manage your 3D models"
      showHeader={false}
      padding="px-4"
      backgroundColor="bg-pf-bg-2"
    >
      <div className="space-y-4 h-full flex flex-col">
        {/* Tag Filter Panel */}
        {showFiltersPanel && (
          <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-4 space-y-3">
            <div className="flex items-center justify-between mb-2">
              <h3 className="text-sm font-medium text-pf-text-primary">Filter by Tags</h3>
              <div className="flex items-center gap-2">
                {selectedTags.length > 0 && (
                  <Button onClick={() => setSelectedTags([])} variant="secondary" size="sm" className="text-xs">
                    Clear ({selectedTags.length})
                  </Button>
                )}
                <Button
                  onClick={() => setShowFiltersPanel(false)}
                  variant="secondary"
                  size="sm"
                  title="Close filters"
                >
                  <CloseIcon className="w-4 h-4" />
                </Button>
              </div>
            </div>
            <TagInput
              selectedTags={allTags && Array.isArray(allTags) ? allTags.filter((t: ModelTag) => selectedTags.includes((t as unknown as { id: string }).id)) : []}
              onChange={(tags) => setSelectedTags(tags.map(t => t.id))}
              placeholder="Select tags to filter models..."
              maxTags={undefined}
            />
          </div>
        )}

        {/* Content */}
        <div className="flex-1 min-h-0">
          <ModelsFileBrowser
            viewMode={viewMode as 'grid' | 'explorer'}
            onViewModeChange={setViewMode}
            selectedTags={selectedTags}
            selectedModelIds={selectedModelIds}
            onSelectionChange={setSelectedModelIds}
            onOpenModel={setViewerModel}
            onSliceModel={(model) => {
              window.location.assign(`/slicer?modelId=${model.id}`);
            }}
            onShowTagModal={() => setShowBulkTagModal(true)}
            onShowSingleTagModal={(model) => {
              setSelectedModelForTagging(model);
            }}
            onToggleTagFilterPanel={() => setShowFiltersPanel(!showFiltersPanel)}
          />
        </div>

        {/* Model Viewer Modal */}
        {viewerModel && (
          <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
            <div className="bg-pf-bg-1 rounded-lg shadow-xl border border-pf-border flex flex-col max-w-4xl w-full max-h-[90vh]">
              <div className="flex items-center justify-between p-4 border-b border-pf-border shrink-0">
                <h3 className="font-medium text-lg text-pf-text-primary">{viewerModel.name}</h3>
                <Button
                  onClick={() => setViewerModel(null)}
                  variant="subtle"
                  size="sm"
                >
                  <CloseIcon className="w-5 h-5" />
                </Button>
              </div>
              <div className="p-4 flex-1 overflow-y-auto">
                <Suspense
                  fallback={
                    <ViewerSkeleton
                      variant="model"
                      className="h-128 w-full"
                    />
                  }
                >
                  {(viewerModel.url || viewerModel.id) && viewerModel.fileType && (
                    <ModelViewer
                      modelUrl={viewerModel.url || `/api/3d-models/file/${viewerModel.id}`}
                      fileType={viewerModel.fileType}
                      showGrid={true}
                      className="h-128 w-full"
                    />
                  )}
                </Suspense>
              </div>
            </div>
          </div>
        )}

        {/* G-code Viewer Modal */}
        {gcodeViewer && (
          <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
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
                  {gcodeViewer.url && <GCodeViewer gcodeUrl={gcodeViewer.url} />}
                </Suspense>
              </div>
            </div>
          </div>
        )}

        {/* Bulk Tag Assignment Modal */}
        <BulkTagAssignmentModal
          isOpen={showBulkTagModal}
          onClose={() => setShowBulkTagModal(false)}
          initialSelectedModelIds={selectedModelIds}
        />

        {/* Individual Tagging Modal */}
        {selectedModelForTagging && (
          <TaggingModal
            isOpen={Boolean(selectedModelForTagging)}
            onClose={() => {
              setSelectedModelForTagging(null);
            }}
            objectId={selectedModelForTagging.id}
            objectType="Model3D"
            initialTags={selectedModelForTagging.tags || []}
          />
        )}

        {/* Floating Action Button for Upload */}
        <FloatingActionButton
          icon={PlusIcon}
          onClick={() => {
            const uploadButton = document.querySelector('[title="Upload models"]') as HTMLButtonElement;
            uploadButton?.click();
          }}
          label="Upload Model"
          position="bottom-right"
          variant="primary"
        />
      </div>
    </PageTemplate>
  );
};
