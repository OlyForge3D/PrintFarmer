import React, { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { GcodeFileBrowser } from '@/features/gcode/components/GcodeFileBrowser';
import { useKeyboardShortcuts } from '@/common/hooks/useKeyboardShortcuts';
import { useViewModePreference } from '@/common/hooks/useViewModePreference';
import { PageTemplate } from '@/common/components/PageTemplate';
import { CloseIcon, PlusIcon } from '@/common/components/icons/MdiIcons';
import { FloatingActionButton } from '@/common/components/FloatingActionButton';
import { TaggingModal } from '@/components/TaggingModal';
import { BulkTagAssignmentModal } from '@/common/components/modals/BulkTagAssignmentModal';
import { Button } from '@/common/components/ui';
import TagInput from '@/components/TagInput';
import { apiClient } from '@/services/api';
import type { GcodeFile } from '@/types/api';
import type { ModelTag } from '@/types/models';

export const GcodeLibraryPage: React.FC = () => {
  const [searchParams] = useSearchParams();
  const { viewMode, setViewMode } = useViewModePreference('printfarmer-gcode-viewmode');
  const harvestId = searchParams.get('harvest') || undefined;
  const printerId = searchParams.get('printer') || undefined;
  const [selectedTags, setSelectedTags] = useState<string[]>([]);
  const [selectedPrinterModels, setSelectedPrinterModels] = useState<string[]>([]);
  const [availablePrinterModels, setAvailablePrinterModels] = useState<Array<{ id: string | null; name: string }>>([]);
  const [showFiltersPanel, setShowFiltersPanel] = useState(false);
  const [showBulkTagModal, setShowBulkTagModal] = useState(false);
  const [selectedFileForTagging, setSelectedFileForTagging] = useState<GcodeFile | null>(null);
  const [selectedFileIds, setSelectedFileIds] = useState<string[]>([]);

  const { data: allTags = [] } = useQuery<ModelTag[]>({
    queryKey: ['gcode-tags'],
    queryFn: async () => {
      const result = await apiClient.getTags();
      return (result as unknown as ModelTag[]) || [];
    },
    staleTime: 5 * 60 * 1000,
    gcTime: 10 * 60 * 1000
  });

  // Keyboard shortcuts for G-code library actions
  useKeyboardShortcuts([
    {
      key: 'u',
      handler: () => {
        // Upload is handled by GcodeFileBrowser
        const uploadButton = document.querySelector('[title="Upload files"]') as HTMLButtonElement;
        uploadButton?.click();
      },
      description: 'Upload new G-code file'
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
        if (selectedFileIds.length > 0) {
          setShowBulkTagModal(true);
        }
      },
      description: 'Tag selected files'
    }
  ]);

  return (
    <PageTemplate
      title="G-code Library"
      subtitle="Browse and manage your G-code files"
      showHeader={false}
      padding="px-4"
      backgroundColor="bg-pf-bg-2"
    >
      <div className="space-y-4 h-full flex flex-col">
        {/* Filter Panel */}
        {showFiltersPanel && (
          <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-4 space-y-4">
            <div className="flex items-center justify-between">
              <h3 className="text-sm font-medium text-pf-text-primary">Filters</h3>
              <div className="flex items-center gap-2">
                {(selectedTags.length > 0 || selectedPrinterModels.length > 0) && (
                  <Button
                    onClick={() => {
                      setSelectedTags([]);
                      setSelectedPrinterModels([]);
                    }}
                    variant="secondary"
                    size="sm"
                    className="text-xs"
                  >
                    Clear All
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

            {/* Tag Filter */}
            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <label className="text-xs font-medium text-pf-text-secondary">Tags</label>
                {selectedTags.length > 0 && (
                  <Button onClick={() => setSelectedTags([])} variant="secondary" size="sm" className="text-xs">
                    Clear ({selectedTags.length})
                  </Button>
                )}
              </div>
              <TagInput
                selectedTags={allTags && Array.isArray(allTags) ? allTags.filter((t: ModelTag) => selectedTags.includes((t as unknown as { id: string }).id)) : []}
                onChange={(tags) => setSelectedTags(tags.map(t => t.id))}
                placeholder="Select tags..."
                maxTags={undefined}
              />
            </div>

            {/* Printer Model Filter */}
            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <label className="text-xs font-medium text-pf-text-secondary">Printer Models</label>
                {selectedPrinterModels.length > 0 && (
                  <Button onClick={() => setSelectedPrinterModels([])} variant="secondary" size="sm" className="text-xs">
                    Clear ({selectedPrinterModels.length})
                  </Button>
                )}
              </div>
              <div className="flex flex-wrap gap-2">
                {availablePrinterModels.length === 0 ? (
                  <p className="text-xs text-pf-text-secondary">No printer models found in current files</p>
                ) : (
                  availablePrinterModels.map((model) => (
                    <Button
                      key={model.name}
                      type="button"
                      variant={selectedPrinterModels.includes(model.name) ? 'primary' : 'secondary'}
                      size="sm"
                      onClick={() => {
                        setSelectedPrinterModels(prev =>
                          prev.includes(model.name)
                            ? prev.filter(m => m !== model.name)
                            : [...prev, model.name]
                        );
                      }}
                      className="text-xs"
                    >
                      {model.name}
                    </Button>
                  ))
                )}
              </div>
            </div>
          </div>
        )}

        {/* Content */}
        <div className="flex-1 min-h-0">
          <GcodeFileBrowser
            harvestId={harvestId}
            printerId={printerId}
            viewMode={viewMode as 'grid' | 'explorer'}
            onViewModeChange={setViewMode}
            selectedTags={selectedTags}
            selectedFileIds={selectedFileIds}
            onSelectionChange={setSelectedFileIds}
            onShowTagModal={() => setShowBulkTagModal(true)}
            onShowSingleTagModal={(file) => setSelectedFileForTagging(file)}
            onToggleTagFilterPanel={() => setShowFiltersPanel(!showFiltersPanel)}
            selectedPrinterModels={selectedPrinterModels}
            onAvailablePrinterModelsChange={setAvailablePrinterModels}
          />
        </div>

        {/* Bulk Tag Assignment Modal */}
        <BulkTagAssignmentModal
          isOpen={showBulkTagModal}
          onClose={() => setShowBulkTagModal(false)}
          initialSelectedModelIds={selectedFileIds}
        />

        {/* Individual Tagging Modal */}
        {selectedFileForTagging && (
          <TaggingModal
            isOpen={Boolean(selectedFileForTagging)}
            onClose={() => setSelectedFileForTagging(null)}
            objectId={selectedFileForTagging.id}
            objectType="GcodeFile"
            initialTags={selectedFileForTagging.tags || []}
          />
        )}
      </div>

      {/* Floating Action Button for Upload */}
      <FloatingActionButton
        icon={PlusIcon}
        onClick={() => {
          const uploadButton = document.querySelector('[title="Upload files"]') as HTMLButtonElement;
          uploadButton?.click();
        }}
        label="Upload G-Code"
        position="bottom-right"
        variant="primary"
      />
    </PageTemplate>
  );
};