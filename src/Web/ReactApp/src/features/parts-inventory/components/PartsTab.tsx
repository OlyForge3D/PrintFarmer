import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { Badge, Button, EmptyState, Spinner, Toggle, HeaderActions, SearchField, Toolbar, FiltersDisclosure, TableHeaderCell } from '@/common/components/ui';
import { useTableSort } from '@/common/hooks/useTableSort';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import {
  PlusIcon,
  EditIcon,
  DeleteIcon,
  MinusIcon,
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
  const { headerProps, sortDataByValues } = useTableSort<'sku' | 'name' | 'bin' | 'onHand' | 'reorderPoint' | 'status'>();

  const filtered = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    if (!needle) return parts;
    return parts.filter((part) => {
      const haystack = [part.sku, part.name, part.description ?? ''].join(' ').toLowerCase();
      return haystack.includes(needle);
    });
  }, [parts, filter]);
  const sortedParts = sortDataByValues(filtered, {
    sku: part => part.sku,
    name: part => part.name,
    bin: part => part.defaultBinCode,
    onHand: part => part.onHand,
    reorderPoint: part => part.reorderPoint,
    status: part => [part.needsReorder ? 'Reorder' : '', part.isActive ? '' : 'Inactive'].filter(Boolean).join(' '),
  });

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
      <HeaderActions>
        <Button
          control
          variant="primary"
          iconLeft={<PlusIcon className="w-4 h-4" ariaLabel="Add" />}
          onClick={() => setIsCreating(true)}
          className="rounded-[2px]"
        >
          Add SKU
        </Button>
      </HeaderActions>
      <Toolbar aria-label="SKU inventory filters">
        <FiltersDisclosure activeCount={includeInactive ? 1 : 0} onReset={() => setIncludeInactive(false)}
          controls={trigger => (
            <Toolbar.Controls>
              <SearchField
                id="parts-filter"
                value={filter}
                onChange={(event) => setFilter(event.target.value)}
                onClear={() => setFilter('')}
                label="Search SKUs"
                placeholder="Filter by SKU, name, or description"
              />
              {trigger}
              <Toolbar.Spacer />
            </Toolbar.Controls>
          )}
        >
          {/* Enlarge both clickable labels, not the switch track or thumb. */}
          <div data-filter-hit-target="inactive-skus" className="[&_label]:min-h-[var(--pf-control-h-md)] [&_label]:inline-flex [&_label]:items-center [&_label>div]:relative">
            <Toggle
              checked={includeInactive}
              onChange={(event) => setIncludeInactive(event.target.checked)}
              label="Show inactive"
            />
          </div>
        </FiltersDisclosure>
      </Toolbar>

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
                <TableHeaderCell {...headerProps('sku')} className="px-3 py-2 font-medium">SKU</TableHeaderCell>
                <TableHeaderCell {...headerProps('name')} className="px-3 py-2 font-medium">Name</TableHeaderCell>
                <TableHeaderCell {...headerProps('bin')} className="px-3 py-2 font-medium">Default bin</TableHeaderCell>
                <TableHeaderCell {...headerProps('onHand')} align="right" className="px-3 py-2 font-medium">On-hand</TableHeaderCell>
                <TableHeaderCell {...headerProps('reorderPoint')} align="right" className="px-3 py-2 font-medium">Reorder pt</TableHeaderCell>
                <TableHeaderCell {...headerProps('status')} className="px-3 py-2 font-medium">Status</TableHeaderCell>
                <th scope="col" className="px-3 py-2 text-right font-medium">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-pf-border">
              {sortedParts.map((part) => (
                <tr key={part.id} className={part.isActive ? '' : 'opacity-60'}>
                  <td className="px-3 py-2 font-mono text-pf-text-primary">{part.sku}</td>
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
