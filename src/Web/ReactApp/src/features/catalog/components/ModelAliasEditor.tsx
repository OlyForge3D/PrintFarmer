import { useState, useEffect, useCallback } from 'react';
import { apiClient } from '@/services/api';
import { PlusIcon, DeleteIcon, LoadingIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { Alert } from '@/common/components/ui/Alert';
import type { SlicerModelAliasDto } from '@/types/api';

interface ModelAliasEditorProps {
  modelId: string;
  onSuccess?: () => void;
}

export function ModelAliasEditor({ modelId, onSuccess }: ModelAliasEditorProps) {
  const [aliases, setAliases] = useState<SlicerModelAliasDto[]>([]);
  const [orcaInput, setOrcaInput] = useState('');
  const [prusaInput, setPrusaInput] = useState('');
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const loadAliases = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const data = await apiClient.getModelAliases(modelId);
      setAliases(data);
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

  const getOrcaAliases = () => aliases.filter(a => a.slicerType === 'OrcaSlicer');
  const getPrusaAliases = () => aliases.filter(a => a.slicerType === 'PrusaSlicer');

  const handleAddOrcaAlias = async () => {
    if (!orcaInput.trim()) return;

    try {
      setSaving(true);
      setError(null);
      const current = getPrusaAliases();
      const prusaNames = current.map(a => a.slicerModelName);
      const orcaNames = getOrcaAliases().map(a => a.slicerModelName);
      orcaNames.push(orcaInput.trim());

      const updated = await apiClient.updateModelAliases(modelId, {
        orcaSlicerNames: orcaNames,
        prusaSlicerNames: prusaNames,
      });
      setAliases(updated);
      setOrcaInput('');
      onSuccess?.();
    } catch (err) {
      setError('Failed to add OrcaSlicer alias');
      console.error('Error adding alias:', err);
    } finally {
      setSaving(false);
    }
  };

  const handleAddPrusaAlias = async () => {
    if (!prusaInput.trim()) return;

    try {
      setSaving(true);
      setError(null);
      const current = getOrcaAliases();
      const orcaNames = current.map(a => a.slicerModelName);
      const prusaNames = getPrusaAliases().map(a => a.slicerModelName);
      prusaNames.push(prusaInput.trim());

      const updated = await apiClient.updateModelAliases(modelId, {
        orcaSlicerNames: orcaNames,
        prusaSlicerNames: prusaNames,
      });
      setAliases(updated);
      setPrusaInput('');
      onSuccess?.();
    } catch (err) {
      setError('Failed to add PrusaSlicer alias');
      console.error('Error adding alias:', err);
    } finally {
      setSaving(false);
    }
  };

  const handleDeleteAlias = async (aliasId: string) => {
    try {
      setSaving(true);
      setError(null);
      const aliasToDelete = aliases.find(a => a.id === aliasId);
      if (!aliasToDelete) return;

      const remaining = aliases.filter(a => a.id !== aliasId);
      const orcaNames = remaining
        .filter(a => a.slicerType === 'OrcaSlicer')
        .map(a => a.slicerModelName);
      const prusaNames = remaining
        .filter(a => a.slicerType === 'PrusaSlicer')
        .map(a => a.slicerModelName);

      const updated = await apiClient.updateModelAliases(modelId, {
        orcaSlicerNames: orcaNames,
        prusaSlicerNames: prusaNames,
      });
      setAliases(updated);
      onSuccess?.();
    } catch (err) {
      setError('Failed to delete alias');
      console.error('Error deleting alias:', err);
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <div className="flex items-center justify-center py-8">
        <LoadingIcon className="h-5 w-5 animate-spin text-pf-text-secondary" />
      </div>
    );
  }

  const orcaAliases = getOrcaAliases();
  const prusaAliases = getPrusaAliases();

  return (
    <div className="space-y-4">
      {error && <Alert type="error">{error}</Alert>}

      {/* OrcaSlicer Aliases */}
      <div className="space-y-2">
        <div className="font-medium text-pf-text">OrcaSlicer Aliases</div>
        <p className="text-sm text-pf-text-secondary">
          Model names as they appear in OrcaSlicer (e.g., "Prusa MK4", "Phrozen Arco 2")
        </p>
        
        <div className="space-y-2">
          {orcaAliases.map(alias => (
            <div
              key={alias.id}
              className="flex items-center justify-between gap-2 bg-pf-bg-secondary rounded px-3 py-2"
            >
              <span className="text-sm text-pf-text">{alias.slicerModelName}</span>
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

        <div className="flex gap-2">
          <Input
            value={orcaInput}
            onChange={e => setOrcaInput(e.target.value)}
            onKeyDown={e => {
              if (e.key === 'Enter') {
                handleAddOrcaAlias();
              }
            }}
            placeholder="Add OrcaSlicer alias..."
            disabled={saving}
          />
          <Button
            onClick={handleAddOrcaAlias}
            disabled={saving || !orcaInput.trim()}
            variant="primary"
            size="sm"
          >
            <PlusIcon className="h-4 w-4" />
          </Button>
        </div>
      </div>

      {/* PrusaSlicer Aliases */}
      <div className="space-y-2 border-t border-pf-border pt-4">
        <div className="font-medium text-pf-text">PrusaSlicer Aliases</div>
        <p className="text-sm text-pf-text-secondary">
          Model names as they appear in PrusaSlicer (e.g., "MK4", "MK3S+")
        </p>

        <div className="space-y-2">
          {prusaAliases.map(alias => (
            <div
              key={alias.id}
              className="flex items-center justify-between gap-2 bg-pf-bg-secondary rounded px-3 py-2"
            >
              <span className="text-sm text-pf-text">{alias.slicerModelName}</span>
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

        <div className="flex gap-2">
          <Input
            value={prusaInput}
            onChange={e => setPrusaInput(e.target.value)}
            onKeyDown={e => {
              if (e.key === 'Enter') {
                handleAddPrusaAlias();
              }
            }}
            placeholder="Add PrusaSlicer alias..."
            disabled={saving}
          />
          <Button
            onClick={handleAddPrusaAlias}
            disabled={saving || !prusaInput.trim()}
            variant="primary"
            size="sm"
          >
            <PlusIcon className="h-4 w-4" />
          </Button>
        </div>
      </div>

      {orcaAliases.length === 0 && prusaAliases.length === 0 && (
        <div className="text-center py-6 text-pf-text-secondary rounded bg-pf-bg-secondary">
          <p className="text-sm">No aliases configured yet</p>
          <p className="text-xs mt-1">Add aliases to help map slicer-specific model names</p>
        </div>
      )}
    </div>
  );
}
