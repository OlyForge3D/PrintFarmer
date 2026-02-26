/**
 * TaskComponentManager Component
 *
 * Manages the link between a maintenance task and inventory parts (components).
 * Shown as a modal from TaskCatalogTab when editing a task's required parts.
 * Uses the catalog task-component endpoints (POST/DELETE on /api/maintenance/tasks/{id}/components).
 */

import React, { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { Badge, Button } from '@/common/components/ui';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import { Modal } from '@/common/components/modals/Modal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import {
  PlusIcon,
  DeleteIcon,
  SearchIcon,
  GearIcon,
} from '@/common/components/icons/MdiIcons';
import {
  useAddCatalogTaskComponent,
  useRemoveCatalogTaskComponent,
} from '../hooks/useTaskCatalog';
import { useMaintenanceComponents } from '../hooks/useMaintenanceComponents';
import type {
  MaintenanceTaskDto,
  MaintenanceTaskComponentDto,
  MaintenanceComponentDto,
} from '@/types/maintenance';

// ──────────────────────── Props ────────────────────────

interface TaskComponentManagerProps {
  isOpen: boolean;
  task: MaintenanceTaskDto;
  onClose: () => void;
}

// ──────────────────────── Add Part Picker ────────────────────────

interface AddPartPickerProps {
  taskId: string;
  existingComponentIds: Set<string>;
  components: MaintenanceComponentDto[];
}

function AddPartPicker({ taskId, existingComponentIds, components }: AddPartPickerProps) {
  const addComponent = useAddCatalogTaskComponent();
  const [search, setSearch] = useState('');
  const [selectedId, setSelectedId] = useState('');
  const [quantity, setQuantity] = useState('1');
  const [notes, setNotes] = useState('');
  const [isAdding, setIsAdding] = useState(false);

  const available = useMemo(() => {
    const q = search.toLowerCase();
    return components.filter(c => {
      if (existingComponentIds.has(c.id)) return false;
      if (q && !c.name.toLowerCase().includes(q) && !c.category.toLowerCase().includes(q) && !(c.sku?.toLowerCase().includes(q))) return false;
      return true;
    });
  }, [components, existingComponentIds, search]);

  const handleAdd = async () => {
    if (!selectedId) return;
    setIsAdding(true);
    try {
      await addComponent.mutateAsync({
        taskId,
        data: {
          componentId: selectedId,
          quantity: Math.max(1, Number(quantity) || 1),
          notes: notes.trim() || null,
        },
      });
      const part = components.find(c => c.id === selectedId);
      toast.success(`Added "${part?.name ?? 'part'}"`);
      setSelectedId('');
      setQuantity('1');
      setNotes('');
      setSearch('');
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to add part');
    } finally {
      setIsAdding(false);
    }
  };

  return (
    <div className="space-y-3 border border-pf-border rounded-lg p-3 bg-pf-bg-3">
      <h4 className="text-sm font-medium text-pf-text-secondary flex items-center gap-1.5">
        <PlusIcon className="h-4 w-4" aria-hidden="true" />
        Add Part from Inventory
      </h4>

      {/* Search + Select */}
      <div className="relative">
        <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-pf-text-muted" aria-hidden="true" />
        <Input
          className="pl-9"
          placeholder="Search parts…"
          value={search}
          onChange={e => setSearch(e.target.value)}
          aria-label="Search inventory parts"
        />
      </div>

      {available.length === 0 ? (
        <p className="text-xs text-pf-text-muted py-2 text-center">
          {components.length === 0
            ? 'No parts in inventory yet. Add parts in the Parts Inventory tab first.'
            : 'No matching parts available (all may already be linked).'}
        </p>
      ) : (
        <>
          <Select
            value={selectedId}
            onChange={e => setSelectedId(e.target.value)}
            aria-label="Select part to add"
          >
            <option value="">Select a part…</option>
            {available.map(c => (
              <option key={c.id} value={c.id}>
                {c.name} ({c.category}){c.sku ? ` [${c.sku}]` : ''}{c.inStock < c.minimumStock ? ' ⚠ Low' : ''}
              </option>
            ))}
          </Select>

          {selectedId && (
            <div className="flex items-end gap-2">
              <div className="w-20">
                <label htmlFor="tcm-qty" className="block text-xs text-pf-text-muted mb-0.5">Quantity</label>
                <Input
                  id="tcm-qty"
                  type="number"
                  min="1"
                  value={quantity}
                  onChange={e => setQuantity(e.target.value)}
                />
              </div>
              <div className="flex-1">
                <label htmlFor="tcm-notes" className="block text-xs text-pf-text-muted mb-0.5">Notes (optional)</label>
                <Input
                  id="tcm-notes"
                  value={notes}
                  onChange={e => setNotes(e.target.value)}
                  placeholder="e.g. use PTFE-coated variant"
                  maxLength={500}
                />
              </div>
              <Button
                variant="primary"
                size="sm"
                onClick={handleAdd}
                disabled={isAdding}
                className="shrink-0"
              >
                {isAdding ? 'Adding…' : 'Add'}
              </Button>
            </div>
          )}
        </>
      )}
    </div>
  );
}

// ──────────────────────── Linked Part Row ────────────────────────

interface LinkedPartRowProps {
  tc: MaintenanceTaskComponentDto;
  component?: MaintenanceComponentDto;
  onRemoveClick: (tc: MaintenanceTaskComponentDto) => void;
}

function LinkedPartRow({ tc, component, onRemoveClick }: LinkedPartRowProps) {
  const isLow = component ? component.inStock < component.minimumStock : false;

  return (
    <div className="flex items-center gap-3 p-3 hover:bg-pf-bg-3/50 transition-colors">
      <GearIcon className="h-4 w-4 text-pf-text-muted shrink-0" aria-hidden="true" />
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="font-medium text-pf-text text-sm truncate">
            {tc.componentName ?? component?.name ?? 'Unknown part'}
          </span>
          {component && (
            <Badge variant="default" className="text-[10px]">{component.category}</Badge>
          )}
          {isLow && (
            <Badge variant="warning" className="text-[10px]">Low Stock</Badge>
          )}
        </div>
        <div className="flex items-center gap-3 text-xs text-pf-text-muted mt-0.5">
          <span>Qty: {tc.quantity}</span>
          {component?.sku && <span>SKU: {component.sku}</span>}
          {component && <span>{component.inStock} in stock</span>}
          {tc.notes && <span className="italic">{tc.notes}</span>}
        </div>
      </div>
      <Button
        variant="ghost"
        size="sm"
        onClick={() => onRemoveClick(tc)}
        aria-label={`Remove ${tc.componentName ?? 'part'}`}
        className="text-red-400 hover:text-red-300 shrink-0"
      >
        <DeleteIcon className="h-4 w-4" aria-hidden="true" />
      </Button>
    </div>
  );
}

// ──────────────────────── Main Component ────────────────────────

export function TaskComponentManager({ isOpen, task, onClose }: TaskComponentManagerProps) {
  const { data: allComponents = [] } = useMaintenanceComponents();
  const removeComponent = useRemoveCatalogTaskComponent();

  const [removingTc, setRemovingTc] = useState<MaintenanceTaskComponentDto | null>(null);

  // Map component IDs for quick lookup
  const componentMap = useMemo(() => {
    const map = new Map<string, MaintenanceComponentDto>();
    for (const c of allComponents) map.set(c.id, c);
    return map;
  }, [allComponents]);

  const existingComponentIds = useMemo(
    () => new Set(task.taskComponents.map(tc => tc.maintenanceComponentId)),
    [task.taskComponents]
  );

  const handleRemoveConfirm = async () => {
    if (!removingTc) return;
    try {
      await removeComponent.mutateAsync({
        taskId: task.id,
        componentId: removingTc.maintenanceComponentId,
      });
      toast.success(`Removed "${removingTc.componentName ?? 'part'}"`);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to remove part');
    } finally {
      setRemovingTc(null);
    }
  };

  return (
    <>
      <Modal isOpen={isOpen} onClose={onClose} title={`Parts for "${task.taskName}"`} size="lg">
        <div className="space-y-4">
          {/* Summary */}
          <p className="text-sm text-pf-text-muted">
            {task.taskComponents.length === 0
              ? 'No parts linked to this task yet. Add parts from your inventory below.'
              : `${task.taskComponents.length} part${task.taskComponents.length !== 1 ? 's' : ''} required for this task.`}
          </p>

          {/* Linked parts list */}
          {task.taskComponents.length > 0 && (
            <div className="divide-y divide-pf-border rounded-lg border border-pf-border bg-pf-bg-2">
              {task.taskComponents.map(tc => (
                <LinkedPartRow
                  key={tc.id}
                  tc={tc}
                  component={componentMap.get(tc.maintenanceComponentId)}
                  onRemoveClick={setRemovingTc}
                />
              ))}
            </div>
          )}

          {/* Add part picker */}
          <AddPartPicker
            taskId={task.id}
            existingComponentIds={existingComponentIds}
            components={allComponents}
          />

          {/* Close button */}
          <div className="flex justify-end pt-2">
            <Button variant="secondary" size="sm" onClick={onClose}>
              Done
            </Button>
          </div>
        </div>
      </Modal>

      {/* Remove confirmation */}
      <ConfirmationModal
        isOpen={!!removingTc}
        title="Remove Part"
        message={`Remove "${removingTc?.componentName ?? 'this part'}" from task "${task.taskName}"? The part remains in your inventory.`}
        confirmButtonText="Remove"
        isDangerous
        onConfirm={handleRemoveConfirm}
        onCancel={() => setRemovingTc(null)}
      />
    </>
  );
}
