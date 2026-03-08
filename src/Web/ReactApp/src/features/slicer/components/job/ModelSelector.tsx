import React from 'react';
import { Button, Input, Select } from '@/common/components/ui';
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
  /** Optional CSS class name */
  className?: string;
}

/**
 * Model selection component.
 * Supports both library picker and manual URL input modes.
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
  className
}) => {
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
          {isLoadingModels ? (
            <Select disabled className="bg-pf-disabled">
              <option>Loading models...</option>
            </Select>
          ) : modelsError ? (
            <p className="text-sm text-pf-error">{modelsError.message}</p>
          ) : models && models.length > 0 ? (
            <Select 
              value={selectedModelId} 
              onChange={e => onModelIdChange(e.target.value)}
            >
              <option value="">-- Select model --</option>
              {models.map(m => (
                <option key={m.id} value={m.id}>{m.originalFileName}</option>
              ))}
            </Select>
          ) : (
            <Select disabled className="bg-pf-disabled">
              <option>-- No models available --</option>
            </Select>
          )}
        </div>
      ) : (
        <div className="space-y-3">
          <div>
            <label className="block text-xs text-pf-text-muted mb-1">File URL</label>
            <Input
              type="url"
              value={fileUrl}
              onChange={e => onFileUrlChange(e.target.value)}
              placeholder="https://... or /storage/..."
            />
          </div>
          <div>
            <label className="block text-xs text-pf-text-muted mb-1">File Name</label>
            <Input
              type="text"
              value={fileName}
              onChange={e => onFileNameChange(e.target.value)}
              placeholder="model.stl"
            />
          </div>
        </div>
      )}
    </div>
  );
};

export default ModelSelector;
