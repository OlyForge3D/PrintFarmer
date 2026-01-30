import React, { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Button, Alert } from '@/common/components/ui';
import { AlertCircleIcon, SearchIcon } from '@/common/components/icons/MdiIcons';
import { ChevronRight, Package } from 'lucide-react';
import { apiClient } from '@/services/api';
import type { PrinterModelDto, ManufacturerDto } from './types';

interface PrinterModelSelectionStepProps {
  /** Callback when a model is selected and user clicks Next */
  onSelectModel: (modelId: string, modelName: string, manufacturerName: string) => void;
}

interface ManufacturerWithModels {
  manufacturer: ManufacturerDto;
  models: PrinterModelDto[];
}

/**
 * Step 0: Select which printer model to import profiles for.
 * Shows when wizard is accessed without a modelId parameter.
 */
export const PrinterModelSelectionStep: React.FC<PrinterModelSelectionStepProps> = ({
  onSelectModel,
}) => {
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedModelId, setSelectedModelId] = useState<string | null>(null);

  // Fetch all manufacturers
  const { data: manufacturers = [], isLoading: manufacturersLoading } = useQuery({
    queryKey: ['catalog-manufacturers'],
    queryFn: async () => {
      const res = await apiClient.get<ManufacturerDto[]>('/catalog/manufacturers');
      return res.data;
    },
    staleTime: 60_000,
  });

  // Fetch all printer models
  const { data: allModels = [], isLoading: modelsLoading, error: modelsError } = useQuery({
    queryKey: ['catalog-printer-models-all'],
    queryFn: async () => {
      const res = await apiClient.get<PrinterModelDto[]>('/catalog/printer-models');
      return res.data;
    },
    staleTime: 60_000,
  });

  // Group models by manufacturer
  const manufacturerData = useMemo((): ManufacturerWithModels[] => {
    const manufacturerMap = new Map<string, ManufacturerDto>();
    for (const m of manufacturers) {
      manufacturerMap.set(m.id, m);
    }

    const grouped = new Map<string, ManufacturerWithModels>();
    for (const model of allModels) {
      const manufacturer = manufacturerMap.get(model.manufacturerId);
      if (!manufacturer) continue;

      // Skip "Unknown" manufacturer and models
      if (manufacturer.name.toLowerCase() === 'unknown' || manufacturer.name.toLowerCase() === 'unknown manufacturer') continue;
      if (model.name.toLowerCase() === 'unknown' || model.name.toLowerCase() === 'unknown model') continue;

      if (!grouped.has(manufacturer.id)) {
        grouped.set(manufacturer.id, { manufacturer, models: [] });
      }
      grouped.get(manufacturer.id)!.models.push(model);
    }

    // Sort manufacturers and models
    return Array.from(grouped.values())
      .map((g) => ({
        ...g,
        models: g.models.sort((a, b) => a.name.localeCompare(b.name)),
      }))
      .sort((a, b) => a.manufacturer.name.localeCompare(b.manufacturer.name));
  }, [manufacturers, allModels]);

  // Filter based on search query
  const filteredData = useMemo(() => {
    if (!searchQuery.trim()) return manufacturerData;

    const query = searchQuery.toLowerCase();
    return manufacturerData
      .map((group) => ({
        ...group,
        models: group.models.filter(
          (model) =>
            model.name.toLowerCase().includes(query) ||
            group.manufacturer.name.toLowerCase().includes(query)
        ),
      }))
      .filter((group) => group.models.length > 0);
  }, [manufacturerData, searchQuery]);

  // Get selected model and manufacturer info
  const selectedModel = useMemo(() => {
    if (!selectedModelId) return null;
    for (const group of manufacturerData) {
      const model = group.models.find((m) => m.id === selectedModelId);
      if (model) {
        return { model, manufacturerName: group.manufacturer.name };
      }
    }
    return null;
  }, [selectedModelId, manufacturerData]);

  const isLoading = manufacturersLoading || modelsLoading;

  const handleNext = () => {
    if (selectedModel) {
      onSelectModel(selectedModel.model.id, selectedModel.model.name, selectedModel.manufacturerName);
    }
  };

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
      <div className="flex items-center justify-between mb-4">
        <div>
          <h3 className="text-lg font-semibold text-pf-text-primary">Select Printer Model</h3>
          <p className="text-sm text-pf-text-secondary">
            Choose which printer model you want to import profiles for
          </p>
        </div>
      </div>

      {/* Search box */}
      <div className="mb-4 relative">
        <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-pf-text-tertiary" />
        <input
          type="text"
          placeholder="Search manufacturers or models..."
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          className="w-full pl-10 pr-4 py-2 bg-pf-bg-2 border border-pf-border rounded-lg focus:ring-2 focus:ring-pf-accent focus:border-transparent text-sm"
        />
      </div>

      {isLoading ? (
        <div className="flex items-center justify-center py-12">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent" />
        </div>
      ) : modelsError ? (
        <Alert className="mb-4">
          <AlertCircleIcon className="h-5 w-5" />
          <span>Failed to load printer models.</span>
        </Alert>
      ) : filteredData.length === 0 ? (
        <div className="text-center py-8 text-pf-text-secondary">
          {searchQuery ? 'No printer models match your search.' : 'No printer models found in catalog.'}
        </div>
      ) : (
        <div className="max-h-[450px] overflow-y-auto border border-pf-border rounded-lg">
          {filteredData.map((group) => (
            <div key={group.manufacturer.id}>
              {/* Manufacturer header */}
              <div className="sticky top-0 bg-pf-bg-2 px-4 py-2 font-medium text-pf-text-primary text-sm border-b border-pf-border">
                {group.manufacturer.name}
              </div>
              {/* Models */}
              {group.models.map((model) => {
                const isSelected = selectedModelId === model.id;
                return (
                  <Button
                    key={model.id}
                    variant="subtle"
                    onClick={() => setSelectedModelId(model.id)}
                    className={`w-full justify-start rounded-none px-4 py-3 h-auto font-normal border-b border-pf-border/50 ${
                      isSelected
                        ? 'bg-pf-accent/10 border-l-2 border-l-pf-accent'
                        : 'hover:bg-pf-bg-hover'
                    }`}
                  >
                    <Package className={`h-5 w-5 flex-shrink-0 ${isSelected ? 'text-pf-accent' : 'text-pf-text-tertiary'}`} />
                    <span className={`flex-1 text-left ${isSelected ? 'text-pf-accent font-medium' : 'text-pf-text-primary'}`}>
                      {model.name}
                    </span>
                  </Button>
                );
              })}
            </div>
          ))}
        </div>
      )}

      <div className="mt-6 flex justify-between items-center">
        <div className="text-sm text-pf-text-secondary">
          {selectedModel ? (
            <span className="text-pf-accent font-medium">
              Selected: {selectedModel.manufacturerName} {selectedModel.model.name}
            </span>
          ) : (
            'Select a printer model to continue'
          )}
        </div>
        <Button
          onClick={handleNext}
          disabled={!selectedModelId}
          iconRight={<ChevronRight className="h-4 w-4" />}
        >
          Next
        </Button>
      </div>
    </div>
  );
};
