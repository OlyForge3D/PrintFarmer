import React, { useState } from 'react';
import { useSearchParams } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { GcodeFileBrowser } from '@/features/gcode/components/GcodeFileBrowser';
import { useKeyboardShortcuts } from '@/common/hooks/useKeyboardShortcuts';
import { useViewModePreference } from '@/common/hooks/useViewModePreference';
import { usePageTour } from '@/common/hooks/usePageTour';
import { gcodeLibraryTour } from '@/features/gcode/tours/gcode-library.tour';
import { HelpButton } from '@/common/components/HelpButton';
import { PageTemplate } from '@/common/components/PageTemplate';
import { PlusIcon } from '@/common/components/icons/MdiIcons';
import { FloatingActionButton } from '@/common/components/FloatingActionButton';
import { TaggingModal } from '@/components/TaggingModal';
import { BulkTagAssignmentModal } from '@/common/components/modals/BulkTagAssignmentModal';
import { AddToProjectModal } from '@/features/projects/components/AddToProjectModal';
import { Select } from '@/common/components/ui';
import { FiltersDisclosure } from '@/common/components/ui/toolbar';
import TagInput from '@/components/TagInput';
import { apiClient } from '@/services/api';
import type { GcodeFile } from '@/types/api';
import type { ModelTag } from '@/types/models';

export const GcodeLibraryPage: React.FC = () => {
  const [searchParams] = useSearchParams();
  const { viewMode, setViewMode } = useViewModePreference('printfarmer-gcode-viewmode');
  const { startTour } = usePageTour({ tourId: 'gcode-library', steps: gcodeLibraryTour });
  const printerId = searchParams.get('printer') || undefined;
  const [selectedTags, setSelectedTags] = useState<string[]>([]);
  const [selectedPrinterModels, setSelectedPrinterModels] = useState<string[]>([]);
  const [availablePrinterModels, setAvailablePrinterModels] = useState<Array<{ id: string | null; name: string }>>([]);
  const [filtersExpanded, setFiltersExpanded] = useState(false);
  const [showBulkTagModal, setShowBulkTagModal] = useState(false);
  const [showAddToProjectModal, setShowAddToProjectModal] = useState(false);
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
      handler: () => setFiltersExpanded(expanded => !expanded),
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
      padding="px-4"
      backgroundColor="bg-pf-bg-2"
      titleActions={<HelpButton onClick={startTour} />}
    >
      <div className="space-y-4 h-full flex flex-col">
        {/* Content */}
        <div className="flex-1 min-h-0">
          <GcodeFileBrowser
            printerId={printerId}
            viewMode={viewMode as 'grid' | 'explorer'}
            onViewModeChange={setViewMode}
            selectedTags={selectedTags}
            filterActions={(controls) => (
              <FiltersDisclosure
                controls={controls}
                activeCount={selectedTags.length + selectedPrinterModels.length}
                expanded={filtersExpanded}
                onExpandedChange={setFiltersExpanded}
                onReset={() => {
                  setSelectedTags([]);
                  setSelectedPrinterModels([]);
                }}
                dataTour="gcode-filters"
              >
                <div className="flex flex-wrap gap-4 items-end">
                  <div className="flex-1 min-w-48 space-y-1">
                    <label className="text-xs font-medium text-pf-text-secondary">Tags</label>
                    <TagInput
                      selectedTags={Array.isArray(allTags) ? allTags.filter(tag => selectedTags.includes(tag.id)) : []}
                      onChange={(tags) => setSelectedTags(tags.map(tag => tag.id))}
                      placeholder="Select tags..."
                      maxTags={undefined}
                    />
                  </div>
                  <div className="min-w-48 space-y-1">
                    <label htmlFor="printer-model-filter" className="text-xs font-medium text-pf-text-secondary">Printer Model</label>
                    <Select
                      id="printer-model-filter"
                      value={selectedPrinterModels[0] ?? ''}
                      onChange={(event) => {
                        const value = event.target.value;
                        setSelectedPrinterModels(value ? [value] : []);
                      }}
                    >
                      <option value="">All models</option>
                      {availablePrinterModels.map((model) => (
                        <option key={model.name} value={model.name}>{model.name}</option>
                      ))}
                    </Select>
                  </div>
                </div>
              </FiltersDisclosure>
            )}
            selectedFileIds={selectedFileIds}
            onSelectionChange={setSelectedFileIds}
            onShowTagModal={() => setShowBulkTagModal(true)}
            onShowAddToProjectModal={() => setShowAddToProjectModal(true)}
            onShowSingleTagModal={(file) => setSelectedFileForTagging(file)}
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

        {/* Bulk Add to Project Modal */}
        <AddToProjectModal
          fileIds={selectedFileIds}
          isOpen={showAddToProjectModal}
          onClose={() => {
            setShowAddToProjectModal(false);
            setSelectedFileIds([]);
          }}
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
      <div data-tour="gcode-fab">
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
      </div>
    </PageTemplate>
  );
};