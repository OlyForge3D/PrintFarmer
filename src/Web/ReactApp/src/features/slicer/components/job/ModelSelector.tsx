import React, { useState, useMemo, useCallback } from 'react';
import { Button, Input } from '@/common/components/ui';
import { CubeIcon } from '@/common/components/icons/MdiIcons';
import { SearchablePickerModal } from '@/common/components/SearchablePickerModal';
import type { Model3DBasic } from './types';

interface ModelSelectorProps {
  /** Whether to use the model picker (true) or manual URL input (false) */
  useModelPicker: boolean;
  /** Toggle handler */
  onToggleMode: () => void;
  /** Available models for selection */
  models: Model3DBasic[] | undefined;
  /** Whether models are loading */
  isLoadingModels?: boolean;
  /** Error loading models */
  modelsError?: Error | null;
  /** Selected model ID when using picker */
  selectedModelId: string;
  /** Callback when model ID changes */
  onModelIdChange: (modelId: string) => void;
  /** File URL when using manual input */
  fileUrl: string;
  /** Callback when file URL changes */
  onFileUrlChange: (url: string) => void;
  /** File name when using manual input */
  fileName: string;
  /** Callback when file name changes */
  onFileNameChange: (name: string) => void;
  /** External trigger to open the picker modal (set true to open, reset via onPickerOpenChange) */
  pickerOpen?: boolean;
  /** Callback when external picker state changes */
  onPickerOpenChange?: (open: boolean) => void;
  /** Optional CSS class name */
  className?: string;
}

const getModelId = (m: Model3DBasic) => m.id;
const getModelLabel = (m: Model3DBasic) => m.originalFileName;

/**
 * Model selection component.
 * Supports both library picker (searchable modal) and manual URL input modes.
 */
export const ModelSelector: React.FC<ModelSelectorProps> = ({
  useModelPicker,
  onToggleMode,
  models,
  isLoadingModels,
  modelsError,
  selectedModelId,
  onModelIdChange,
  fileUrl,
  onFileUrlChange,
  fileName,
  onFileNameChange,
  pickerOpen,
  onPickerOpenChange,
  className,
}) => {
  const [internalOpen, setInternalOpen] = useState(false);

  // Support both controlled (pickerOpen prop) and uncontrolled (internal) modes
  const isPickerOpen = pickerOpen ?? internalOpen;
  const setIsPickerOpen = useCallback((open: boolean) => {
    setInternalOpen(open);
    onPickerOpenChange?.(open);
  }, [onPickerOpenChange]);

  const selectedModel = useMemo(
    () => models?.find((m) => m.id === selectedModelId),
    [models, selectedModelId],
  );

  const handleSelect = useCallback(
    (model: Model3DBasic) => {
      onModelIdChange(model.id);
    },
    [onModelIdChange],
  );

  return (
    <div className={`bg-pf-panel border border-pf-border rounded-lg p-4 space-y-3 ${className ?? ''}`}>
      <div className="flex items-center justify-between">
        <label className="block text-sm font-semibold text-pf-text-primary">
          {useModelPicker ? 'Select Model from Library' : 'Model File URL'}
        </label>
        <Button
          variant="ghost"
          size="sm"
          onClick={onToggleMode}
          className="text-xs"
        >
          {useModelPicker ? 'Enter URL manually' : 'Pick from library'}
        </Button>
      </div>

      {useModelPicker ? (
        <div>
          {modelsError ? (
            <p className="text-sm text-pf-error">{modelsError.message}</p>
          ) : (
            <>
              <Button
                variant="secondary"
                onClick={() => setIsPickerOpen(true)}
                iconLeft={<CubeIcon className="w-4 h-4" />}
                className="w-full justify-start text-left"
              >
                <span className={selectedModel ? 'text-pf-text-primary' : 'text-pf-text-muted'}>
                  {selectedModel?.originalFileName ?? 'Select a model...'}
                </span>
              </Button>

              <SearchablePickerModal<Model3DBasic>
                isOpen={isPickerOpen}
                onClose={() => setIsPickerOpen(false)}
                onSelect={handleSelect}
                items={models ?? []}
                getItemId={getModelId}
                getLabel={getModelLabel}
                selectedId={selectedModelId}
                title="Select 3D Model"
                searchPlaceholder="Search models by filename..."
                emptyMessage="No models match your search."
                isLoading={isLoadingModels}
              />
            </>
          )}
        </div>
      ) : (
        <div className="space-y-3">
          <div>
            <label className="block text-xs text-pf-text-muted mb-1">File URL</label>
            <Input
              type="url"
              value={fileUrl}
              onChange={(e) => onFileUrlChange(e.target.value)}
              placeholder="https://... or /storage/..."
            />
          </div>
          <div>
            <label className="block text-xs text-pf-text-muted mb-1">File Name</label>
            <Input
              type="text"
              value={fileName}
              onChange={(e) => onFileNameChange(e.target.value)}
              placeholder="model.stl"
            />
          </div>
        </div>
      )}
    </div>
  );
};

export default ModelSelector;
