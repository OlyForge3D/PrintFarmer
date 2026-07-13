import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { Badge, Button, EmptyState, Input, Spinner, Toggle } from '@/common/components/ui';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import {
  PlusIcon,
  EditIcon,
  DeleteIcon,
  MinusIcon,
  SearchIcon,
  AlertIcon,
  PackageIcon,
} from '@/common/components/icons/MdiIcons';
import {
  useBins,
  useDeletePart,
  useParts,
} from '../hooks/usePartsInventory';
import type { PartInventoryDto } from '@/types/partsInventory';
import { getErrorMessage } from '../utils/problemDetails';
import { PartFormModal } from './PartFormModal';
import { AdjustStockModal } from './AdjustStockModal';
import { AdjustmentHistoryDrawer } from './AdjustmentHistoryDrawer';

/**
 * PartsTab — CRUD list of printed-part SKUs.
 *
 * Provides:
 *   • Text filter over SKU / name / description.
 *   • "Show inactive" toggle to include soft-deactivated rows.
 *   • Inline actions for edit / adjust stock / view ledger / delete.
 *   • Low-stock (needsReorder) badges — icon + text, never colour-only.
 */
export function PartsTab() {
  const [includeInactive, setIncludeInactive] = useState(false);
  const [filter, setFilter] = useState('');
  const [editing, setEditing] = useState<PartInventoryDto | null>(null);
  const [isCreating, setIsCreating] = useState(false);
  const [adjustTarget, setAdjustTarget] = useState<PartInventoryDto | null>(null);
  const [historyTarget, setHistoryTarget] = useState<PartInventoryDto | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<PartInventoryDto | null>(null);

  const { data: parts = [], isLoading, error } = useParts({ includeInactive });
  const { data: bins = [] } = useBins({ includeInactive: true });
  const deletePart = useDeletePart();

  const filtered = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    if (!needle) return parts;
    return parts.filter((part) => {
      const haystack = [part.sku, part.name, part.description ?? ''].join(' ').toLowerCase();
      return haystack.includes(needle);
    });
  }, [parts, filter]);

  const handleDelete = async () => {
    if (!confirmDelete) return;
    try {
      await deletePart.mutateAsync(confirmDelete.sku);
      toast.success(`Deactivated ${confirmDelete.sku}`);
      setConfirmDelete(null);
    } catch (err) {
      toast.error(getErrorMessage(err, 'Failed to deactivate SKU'));
    }
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-col sm:flex-row items-stretch sm:items-center gap-2">
        <div className="relative flex-1">
          <label htmlFor="parts-filter" className="sr-only">
            Search SKUs
          </label>
          <SearchIcon
            className="w-4 h-4 absolute left-2 top-1/2 -translate-y-1/2 text-pf-text-secondary pointer-events-none"
            ariaLabel="Search"
          />
          <Input
            id="parts-filter"
            value={filter}
            onChange={(event) => setFilter(event.target.value)}
            placeholder="Filter by SKU, name, or description"
            className="pl-8"
          />
        </div>
        <div className="flex items-center gap-3">
          <Toggle
            checked={includeInactive}
            onChange={(event) => setIncludeInactive(event.target.checked)}
            label="Show inactive"
          />
          <Button
            variant="primary"
            size="sm"
            iconLeft={<PlusIcon className="w-4 h-4" ariaLabel="Add" />}
            onClick={() => setIsCreating(true)}
          >
            Add SKU
          </Button>
        </div>
      </div>

      {isLoading && (
        <div className="flex items-center gap-2 py-8 justify-center text-pf-text-secondary">
          <Spinner size="md" />
          <span>Loading SKUs…</span>
        </div>
      )}

      {error && (
        <div className="p-3 border border-pf-error-border bg-pf-error-bg rounded-sm text-pf-error-text text-sm" role="alert">
          Failed to load printed-part SKUs.
        </div>
      )}

      {!isLoading && !error && filtered.length === 0 && (
        <EmptyState
          icon={<PackageIcon className="w-8 h-8 text-pf-text-secondary" ariaLabel="Empty" />}
          title={filter ? 'No SKUs match your filter' : 'No printed-part SKUs yet'}
          description={
            filter
              ? 'Try a different search term.'
              : 'Add a SKU to start tracking printed-part inventory.'
          }
          action={
            !filter ? (
              <Button variant="primary" size="sm" onClick={() => setIsCreating(true)}>
                Add first SKU
              </Button>
            ) : undefined
          }
        />
      )}

      {filtered.length > 0 && (
        <div className="overflow-x-auto">
          <table className="min-w-full text-sm">
            <thead className="bg-pf-bg-1 text-pf-text-secondary">
              <tr>
                <th scope="col" className="px-3 py-2 text-left font-medium">SKU</th>
                <th scope="col" className="px-3 py-2 text-left font-medium">Name</th>
                <th scope="col" className="px-3 py-2 text-left font-medium">Default bin</th>
                <th scope="col" className="px-3 py-2 text-right font-medium">On-hand</th>
                <th scope="col" className="px-3 py-2 text-right font-medium">Reorder pt</th>
                <th scope="col" className="px-3 py-2 text-left font-medium">Status</th>
                <th scope="col" className="px-3 py-2 text-right font-medium">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-pf-border">
              {filtered.map((part) => (
                <tr key={part.id} className={part.isActive ? '' : 'opacity-60'}>
                  <td className="px-3 py-2 font-mono text-pf-text">{part.sku}</td>
                  <td className="px-3 py-2">
                    <div className="font-medium">{part.name}</div>
                    {part.description && (
                      <div className="text-xs text-pf-text-secondary">{part.description}</div>
                    )}
                  </td>
                  <td className="px-3 py-2">
                    {part.defaultBinCode ? (
                      <Badge variant="default" size="sm">{part.defaultBinCode}</Badge>
                    ) : (
                      <span className="text-pf-text-secondary">—</span>
                    )}
                  </td>
                  <td className="px-3 py-2 text-right font-mono">{part.onHand}</td>
                  <td className="px-3 py-2 text-right font-mono">{part.reorderPoint}</td>
                  <td className="px-3 py-2">
                    <div className="flex flex-wrap gap-1">
                      {part.needsReorder && (
                        <Badge variant="warning" size="sm">
                          <span className="inline-flex items-center gap-1">
                            <AlertIcon className="w-3 h-3" ariaLabel="" />
                            Reorder
                          </span>
                        </Badge>
                      )}
                      {!part.isActive && (
                        <Badge variant="default" size="sm">Inactive</Badge>
                      )}
                    </div>
                  </td>
                  <td className="px-3 py-2 text-right">
                    <div className="inline-flex gap-1" role="group" aria-label={`Actions for ${part.sku}`}>
                      <Button
                        variant="secondary"
                        size="sm"
                        iconLeft={<MinusIcon className="w-4 h-4" ariaLabel="Adjust" />}
                        onClick={() => setAdjustTarget(part)}
                        aria-label={`Adjust stock for ${part.sku}`}
                      >
                        Adjust
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => setHistoryTarget(part)}
                        aria-label={`View ledger for ${part.sku}`}
                      >
                        Ledger
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        iconCenter={<EditIcon className="w-4 h-4" ariaLabel={`Edit ${part.sku}`} />}
                        onClick={() => setEditing(part)}
                        aria-label={`Edit ${part.sku}`}
                      />
                      {part.isActive && (
                        <Button
                          variant="ghost"
                          size="sm"
                          iconCenter={
                            <DeleteIcon className="w-4 h-4" ariaLabel={`Deactivate ${part.sku}`} />
                          }
                          onClick={() => setConfirmDelete(part)}
                          aria-label={`Deactivate ${part.sku}`}
                        />
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <PartFormModal
        isOpen={isCreating || Boolean(editing)}
        onClose={() => {
          setIsCreating(false);
          setEditing(null);
        }}
        part={editing}
        bins={bins}
      />
      <AdjustStockModal
        isOpen={Boolean(adjustTarget)}
        onClose={() => setAdjustTarget(null)}
        part={adjustTarget}
        bins={bins}
      />
      <AdjustmentHistoryDrawer
        isOpen={Boolean(historyTarget)}
        onClose={() => setHistoryTarget(null)}
        part={historyTarget}
      />
      <ConfirmationModal
        isOpen={Boolean(confirmDelete)}
        title="Deactivate SKU"
        message={
          confirmDelete
            ? `Deactivate ${confirmDelete.sku}? Ledger and mappings are retained, and the SKU can be re-activated later.`
            : ''
        }
        confirmButtonText="Deactivate"
        isDangerous
        isConfirming={deletePart.isPending}
        onCancel={() => setConfirmDelete(null)}
        onConfirm={handleDelete}
      />
    </div>
  );
}
