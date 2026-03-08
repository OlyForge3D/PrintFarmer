import { useState, useEffect, useCallback, useImperativeHandle, forwardRef } from 'react';
import { apiClient } from '@/services/api';
import { PlusIcon, DeleteIcon, LoadingIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { Alert } from '@/common/components/ui/Alert';
import { Select } from '@/common/components/ui';

// Supported slicer types - add new slicers here as they become supported
const SLICER_TYPES = [
  { value: 'OrcaSlicer', label: 'OrcaSlicer' },
  { value: 'PrusaSlicer', label: 'PrusaSlicer' },
  // Future slicers can be added here:
  // { value: 'Cura', label: 'Cura' },
] as const;

type SlicerType = typeof SLICER_TYPES[number]['value'];

// Local alias type for tracking pending changes (may not have ID yet)
interface LocalAlias {
  id: string;
  slicerModelName: string;
  slicerType: string;
  isNew?: boolean; // True if added locally but not yet persisted
}

interface ModelAliasEditorProps {
  modelId: string;
  onDirtyChange?: (isDirty: boolean) => void;
}

export interface ModelAliasEditorRef {
  saveChanges: () => Promise<void>;
  hasChanges: () => boolean;
}

export const ModelAliasEditor = forwardRef<ModelAliasEditorRef, ModelAliasEditorProps>(
  function ModelAliasEditor({ modelId, onDirtyChange }, ref) {
  const [aliases, setAliases] = useState<LocalAlias[]>([]);
  const [originalAliases, setOriginalAliases] = useState<LocalAlias[]>([]);
  const [newAliasName, setNewAliasName] = useState('');
  const [newSlicerType, setNewSlicerType] = useState<SlicerType>('OrcaSlicer');
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const loadAliases = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const data = await apiClient.getModelAliases(modelId);
      const mapped: LocalAlias[] = data.map(a => ({
        id: a.id,
        slicerModelName: a.slicerModelName,
        slicerType: a.slicerType || '',
      }));
      setAliases(mapped);
      setOriginalAliases(mapped);
    } catch (err) {
      setError('Failed to load aliases');
      console.error('Error loading aliases:', err);
    } finally {
      setLoading(false);
    }
  }, [modelId]);

  useEffect(() => {
    loadAliases();
  }, [loadAliases]);

  // Check if there are unsaved changes
  const hasChanges = useCallback(() => {
    if (aliases.length !== originalAliases.length) return true;
    const currentNames = aliases.map(a => `${a.slicerType}:${a.slicerModelName}`).sort();
    const originalNames = originalAliases.map(a => `${a.slicerType}:${a.slicerModelName}`).sort();
    return JSON.stringify(currentNames) !== JSON.stringify(originalNames);
  }, [aliases, originalAliases]);

  // Notify parent of dirty state changes
  useEffect(() => {
    onDirtyChange?.(hasChanges());
  }, [hasChanges, onDirtyChange]);

  const getAliasesBySlicer = (slicerType: string) => 
    aliases.filter(a => a.slicerType === slicerType);

  const handleAddAlias = () => {
    if (!newAliasName.trim()) return;
    
    // Check for duplicates within same slicer type
    const existingForSlicer = getAliasesBySlicer(newSlicerType);
    if (existingForSlicer.some(a => a.slicerModelName.toLowerCase() === newAliasName.trim().toLowerCase())) {
      setError(`This ${newSlicerType} alias already exists`);
      return;
    }

    const newAlias: LocalAlias = {
      id: `temp-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
      slicerModelName: newAliasName.trim(),
      slicerType: newSlicerType,
      isNew: true,
    };
    setAliases(prev => [...prev, newAlias]);
    setNewAliasName('');
    setError(null);
  };

  const handleDeleteAlias = (aliasId: string) => {
    setAliases(prev => prev.filter(a => a.id !== aliasId));
    setError(null);
  };

  // Save changes to backend
  const saveChanges = useCallback(async () => {
    if (!hasChanges()) return;

    try {
      setSaving(true);
      setError(null);
      
      const orcaNames = getAliasesBySlicer('OrcaSlicer').map(a => a.slicerModelName);
      const prusaNames = getAliasesBySlicer('PrusaSlicer').map(a => a.slicerModelName);

      const updated = await apiClient.updateModelAliases(modelId, {
        orcaSlicerNames: orcaNames,
        prusaSlicerNames: prusaNames,
      });

      // Update local state with server response
      const mapped: LocalAlias[] = updated.map(a => ({
        id: a.id,
        slicerModelName: a.slicerModelName,
        slicerType: a.slicerType || '',
      }));
      setAliases(mapped);
      setOriginalAliases(mapped);
    } catch (err) {
      setError('Failed to save aliases');
      console.error('Error saving aliases:', err);
      throw err; // Re-throw so parent knows save failed
    } finally {
      setSaving(false);
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [modelId, aliases, hasChanges]);

  // Expose methods to parent via ref
  useImperativeHandle(ref, () => ({
    saveChanges,
    hasChanges,
  }), [saveChanges, hasChanges]);

  if (loading) {
    return (
      <div className="flex items-center justify-center py-8">
        <LoadingIcon className="h-5 w-5 animate-spin text-pf-text-secondary" />
      </div>
    );
  }

  const isDirty = hasChanges();

  // Group aliases by slicer type for display
  const aliasesBySlicer = SLICER_TYPES.map(slicer => ({
    ...slicer,
    aliases: getAliasesBySlicer(slicer.value),
  }));

  return (
    <div className="space-y-4">
      {error && <Alert type="error">{error}</Alert>}
      
      {isDirty && (
        <Alert type="info">
          You have unsaved alias changes. Click "Save Changes" to persist them.
        </Alert>
      )}

      {/* Existing Aliases grouped by slicer */}
      <div className="space-y-3">
        {aliasesBySlicer.map(({ value: slicerType, label, aliases: slicerAliases }) => (
          slicerAliases.length > 0 && (
            <div key={slicerType} className="space-y-2">
              <div className="text-sm font-medium text-pf-text-secondary">{label}</div>
              <div className="space-y-1">
                {slicerAliases.map(alias => (
                  <div
                    key={alias.id}
                    className={`flex items-center justify-between gap-2 rounded px-3 py-2 ${
                      alias.isNew ? 'bg-green-500/10 border border-green-500/30' : 'bg-pf-bg-secondary'
                    }`}
                  >
                    <span className="text-sm text-pf-text-primary">
                      {alias.slicerModelName}
                      {alias.isNew && <span className="ml-2 text-xs text-green-500">(new)</span>}
                    </span>
                    <Button
                      onClick={() => handleDeleteAlias(alias.id)}
                      disabled={saving}
                      variant="subtle"
                      size="sm"
                      title="Delete alias"
                    >
                      <DeleteIcon className="h-4 w-4 text-red-400" />
                    </Button>
                  </div>
                ))}
              </div>
            </div>
          )
        ))}
      </div>

      {aliases.length === 0 && (
        <div className="text-center py-4 text-pf-text-secondary rounded-sm bg-pf-bg-secondary">
          <p className="text-sm">No aliases configured yet</p>
          <p className="text-xs mt-1">Add aliases to help map slicer-specific model names</p>
        </div>
      )}

      {/* Add New Alias */}
      <div className="border-t border-pf-border pt-4">
        <div className="text-sm font-medium text-pf-text-primary mb-2">Add New Alias</div>
        <div className="flex gap-2">
          <Select
            value={newSlicerType}
            onChange={e => setNewSlicerType(e.target.value as SlicerType)}
            disabled={saving}
            className="w-40"
          >
            {SLICER_TYPES.map(slicer => (
              <option key={slicer.value} value={slicer.value}>
                {slicer.label}
              </option>
            ))}
          </Select>
          <Input
            value={newAliasName}
            onChange={e => setNewAliasName(e.target.value)}
            onKeyDown={e => {
              if (e.key === 'Enter') {
                e.preventDefault();
                handleAddAlias();
              }
            }}
            placeholder="Model name in slicer..."
            disabled={saving}
            className="flex-1"
          />
          <Button
            onClick={handleAddAlias}
            disabled={saving || !newAliasName.trim()}
            variant="primary"
            size="sm"
            title="Add alias"
          >
            <PlusIcon className="h-4 w-4" />
          </Button>
        </div>
        <p className="text-xs text-pf-text-tertiary mt-2">
          Enter the model name exactly as it appears in the slicer profile
        </p>
      </div>
    </div>
  );
});
