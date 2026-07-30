import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { Badge, Button, EmptyState, Input, Select, Spinner } from '@/common/components/ui';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import {
  PlusIcon,
  DeleteIcon,
  SearchIcon,
  LayersIcon,
} from '@/common/components/icons/MdiIcons';
import {
  useDeleteMapping,
  useMappings,
  useParts,
} from '../hooks/usePartsInventory';
import type { PartOutputMappingDto } from '@/types/partsInventory';
import { getErrorMessage } from '../utils/problemDetails';
import { MappingFormModal } from './MappingFormModal';

interface GroupedMapping {
  key: string;
  kind: 'gcode' | 'project';
  sourceId: string;
  mappings: PartOutputMappingDto[];
}

/**
 * MappingsTab — lists project/G-code → SKU mappings.
 *
 * Multi-SKU plates are represented as multiple mappings sharing the
 * same source file. The list is grouped by source so operators can
 * visually recognise plates and easily add another SKU to one.
 */
export function MappingsTab() {
  const [filter, setFilter] = useState('');
  const [skuFilter, setSkuFilter] = useState('');
  const [isCreating, setIsCreating] = useState(false);
  const [presetSource, setPresetSource] = useState<
    { kind: 'gcode' | 'project'; id: string; label: string } | undefined
  >(undefined);
  const [confirmDelete, setConfirmDelete] = useState<PartOutputMappingDto | null>(null);

  const { data: mappings = [], isLoading, error } = useMappings(skuFilter || undefined);
  const { data: parts = [] } = useParts({ includeInactive: true });
  const deleteMapping = useDeleteMapping();

  const filtered = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    if (!needle) return mappings;
    return mappings.filter((mapping) => {
      const haystack = [
        mapping.sku,
        mapping.gcodeFileId ?? '',
        mapping.printProjectFileId ?? '',
      ]
        .join(' ')
        .toLowerCase();
      return haystack.includes(needle);
    });
  }, [mappings, filter]);

  const grouped = useMemo<GroupedMapping[]>(() => {
    const map = new Map<string, GroupedMapping>();
    for (const mapping of filtered) {
      const kind: 'gcode' | 'project' = mapping.gcodeFileId ? 'gcode' : 'project';
      const sourceId = mapping.gcodeFileId ?? mapping.printProjectFileId ?? 'unknown';
      const key = `${kind}:${sourceId}`;
      const existing = map.get(key);
      if (existing) {
        existing.mappings.push(mapping);
      } else {
        map.set(key, { key, kind, sourceId, mappings: [mapping] });
      }
    }
    return Array.from(map.values()).sort((a, b) => a.sourceId.localeCompare(b.sourceId));
  }, [filtered]);

  const handleDelete = async () => {
    if (!confirmDelete) return;
    try {
      await deleteMapping.mutateAsync(confirmDelete.id);
      toast.success('Mapping removed');
      setConfirmDelete(null);
    } catch (err) {
      toast.error(getErrorMessage(err, 'Failed to remove mapping'));
    }
  };

  const openCreate = () => {
    setPresetSource(undefined);
    setIsCreating(true);
  };

  const openAddSku = (group: GroupedMapping) => {
    setPresetSource({
      kind: group.kind,
      id: group.sourceId,
      label: `${group.kind === 'gcode' ? 'G-code' : 'Project'} ${group.sourceId}`,
    });
    setIsCreating(true);
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-col sm:flex-row items-stretch sm:items-center gap-2">
        <div className="relative flex-1">
          <label htmlFor="mappings-filter" className="sr-only">
            Search mappings
          </label>
          <SearchIcon
            className="w-4 h-4 absolute left-2 top-1/2 -translate-y-1/2 text-pf-text-secondary pointer-events-none"
            ariaLabel="Search"
          />
          <Input
            id="mappings-filter"
            value={filter}
            onChange={(event) => setFilter(event.target.value)}
            placeholder="Filter by SKU or file ID"
            className="pl-8"
          />
        </div>
        <div>
          <label htmlFor="mappings-sku-filter" className="sr-only">
            Filter by SKU
          </label>
          <Select
            id="mappings-sku-filter"
            value={skuFilter}
            onChange={(event) => setSkuFilter(event.target.value)}
            aria-label="Filter by SKU"
          >
            <option value="">All SKUs</option>
            {parts.map((part) => (
              <option key={part.sku} value={part.sku}>
                {part.sku} — {part.name}
              </option>
            ))}
          </Select>
        </div>
        <Button
          variant="primary"
          size="sm"
          iconLeft={<PlusIcon className="w-4 h-4" ariaLabel="Add" />}
          onClick={openCreate}
        >
          Add mapping
        </Button>
      </div>

      {isLoading && (
        <div className="flex items-center gap-2 py-8 justify-center text-pf-text-secondary">
          <Spinner size="md" />
          <span>Loading mappings…</span>
        </div>
      )}

      {error && (
        <div className="p-3 border border-pf-error-border bg-pf-error-bg rounded-sm text-pf-error-text text-sm" role="alert">
          Failed to load mappings.
        </div>
      )}

      {!isLoading && !error && grouped.length === 0 && (
        <EmptyState
          icon={<LayersIcon className="w-8 h-8 text-pf-text-secondary" ariaLabel="Empty" />}
          title="No output mappings"
          description="Map a G-code or project file to a SKU so harvested prints know which stock to credit."
          action={
            <Button variant="primary" size="sm" onClick={openCreate}>
              Add first mapping
            </Button>
          }
        />
      )}

      {grouped.length > 0 && (
        <div className="space-y-3">
          {grouped.map((group) => (
            <div key={group.key} className="border border-pf-border rounded-sm bg-pf-bg-1">
              <div className="flex flex-wrap items-center justify-between gap-2 px-3 py-2 border-b border-pf-border">
                <div className="flex items-center gap-2 min-w-0">
                  <Badge variant={group.kind === 'gcode' ? 'info' : 'primary'} size="sm">
                    <span className="inline-flex items-center gap-1">
                      <LayersIcon className="w-3 h-3" ariaLabel="" />
                      {group.kind === 'gcode' ? 'G-code' : 'Project'}
                    </span>
                  </Badge>
                  <span className="font-mono text-xs text-pf-text-secondary truncate" title={group.sourceId}>
                    {group.sourceId}
                  </span>
                  {group.mappings.length > 1 && (
                    <Badge variant="warning" size="sm">
                      Multi-SKU plate ({group.mappings.length})
                    </Badge>
                  )}
                </div>
                <Button
                  variant="ghost"
                  size="sm"
                  iconLeft={<PlusIcon className="w-4 h-4" ariaLabel="Add" />}
                  onClick={() => openAddSku(group)}
                >
                  Add SKU
                </Button>
              </div>
              <table className="min-w-full text-sm">
                <thead className="text-pf-text-secondary">
                  <tr>
                    <th scope="col" className="px-3 py-1 text-left font-medium">SKU</th>
                    <th scope="col" className="px-3 py-1 text-right font-medium">Quantity</th>
                    <th scope="col" className="px-3 py-1 text-right font-medium">Actions</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-pf-border">
                  {group.mappings.map((mapping) => (
                    <tr key={mapping.id}>
                      <td className="px-3 py-1.5 font-mono">{mapping.sku}</td>
                      <td className="px-3 py-1.5 text-right font-mono">{mapping.quantity}</td>
                      <td className="px-3 py-1.5 text-right">
                        <Button
                          variant="ghost"
                          size="sm"
                          iconCenter={
                            <DeleteIcon
                              className="w-4 h-4"
                              ariaLabel={`Remove mapping for ${mapping.sku}`}
                            />
                          }
                          onClick={() => setConfirmDelete(mapping)}
                          aria-label={`Remove mapping for ${mapping.sku}`}
                        />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ))}
        </div>
      )}

      <MappingFormModal
        isOpen={isCreating}
        onClose={() => setIsCreating(false)}
        parts={parts}
        presetSource={presetSource}
      />
      <ConfirmationModal
        isOpen={Boolean(confirmDelete)}
        title="Remove mapping"
        message={
          confirmDelete
            ? `Remove the mapping from this source to ${confirmDelete.sku}? Historical ledger entries are unaffected.`
            : ''
        }
        confirmButtonText="Remove"
        isDangerous
        isConfirming={deleteMapping.isPending}
        onCancel={() => setConfirmDelete(null)}
        onConfirm={handleDelete}
      />
    </div>
  );
}
