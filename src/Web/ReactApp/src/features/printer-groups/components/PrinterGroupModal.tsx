import { useState, useMemo } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Input, Textarea, FormField, Badge, Select, Checkbox } from '@/common/components/ui';
import { SearchIcon } from '@/common/components/icons/MdiIcons';
import { toast } from 'sonner';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { usePrinters } from '@/common/hooks/useApi';
import { apiClient } from '@/services/api';
import type { PrinterGroup, CreatePrinterGroupRequest, UpdatePrinterGroupRequest, PrinterGroupPrinter } from '@/types/api';
import { PrinterBackend } from '@/types/api';

interface PrinterGroupModalProps {
  isOpen: boolean;
  onClose: () => void;
  editGroup?: PrinterGroup | null;
  /** Printers already in the group (edit mode only) */
  assignedPrinters?: PrinterGroupPrinter[];
}

function backendLabel(backend: PrinterBackend | number): string {
  if (typeof backend === 'number') return PrinterBackend[backend] || 'Unknown';
  return backend;
}

export function PrinterGroupModal({ isOpen, onClose, editGroup, assignedPrinters = [] }: PrinterGroupModalProps) {
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [nameError, setNameError] = useState('');
  const [selectedPrinterIds, setSelectedPrinterIds] = useState<Set<string>>(new Set());
  const [searchQuery, setSearchQuery] = useState('');
  const [modelFilter, setModelFilter] = useState<string>('all');

  const { data: allPrinters = [] } = usePrinters();

  // Determine which printers are already assigned (edit mode)
  const assignedIds = useMemo(
    () => new Set(assignedPrinters.map((p) => p.id)),
    [assignedPrinters]
  );

  // Determine the group's model constraint from already-assigned printers
  const groupModelId = useMemo(() => {
    if (assignedPrinters.length > 0) {
      return allPrinters.find((p) => p.id === assignedPrinters[0].id)?.modelId;
    }
    return undefined;
  }, [assignedPrinters, allPrinters]);

  // Compute the effective model constraint (from assigned printers OR from current selection)
  const effectiveModelId = useMemo(() => {
    if (groupModelId) return groupModelId;
    // If creating and user has selected at least one printer, lock to that model
    const firstSelected = allPrinters.find((p) => selectedPrinterIds.has(p.id));
    return firstSelected?.modelId;
  }, [groupModelId, selectedPrinterIds, allPrinters]);

  // Available printers: not already assigned, and matching model if constrained
  const availablePrinters = useMemo(() => {
    const lowerSearch = searchQuery.toLowerCase();
    return allPrinters.filter((p) => {
      if (assignedIds.has(p.id)) return false;
      if (effectiveModelId && p.modelId !== effectiveModelId) return false;
      if (modelFilter !== 'all' && p.modelId !== modelFilter) return false;
      if (lowerSearch && !p.name.toLowerCase().includes(lowerSearch)) return false;
      return true;
    });
  }, [allPrinters, assignedIds, effectiveModelId, modelFilter, searchQuery]);

  // Unique models for the filter dropdown (only from non-assigned, non-constrained printers)
  const uniqueModels = useMemo(() => {
    const models = new Map<string, string>();
    for (const p of allPrinters) {
      if (assignedIds.has(p.id)) continue;
      if (effectiveModelId && p.modelId !== effectiveModelId) continue;
      if (p.modelId && p.modelName) {
        const label = p.manufacturerName ? `${p.manufacturerName} ${p.modelName}` : p.modelName;
        models.set(p.modelId, label);
      }
    }
    return Array.from(models.entries()).sort((a, b) => a[1].localeCompare(b[1]));
  }, [allPrinters, assignedIds, effectiveModelId]);

  // Group available printers by model for display
  const modelName = useMemo(() => {
    if (!effectiveModelId) return undefined;
    const p = allPrinters.find((pr) => pr.modelId === effectiveModelId);
    return p ? `${p.manufacturerName ?? ''} ${p.modelName ?? ''}`.trim() : undefined;
  }, [effectiveModelId, allPrinters]);

  // Reset form when modal opens (React-recommended pattern for adjusting state on prop change)
  const [prevIsOpen, setPrevIsOpen] = useState(false);
  if (isOpen && !prevIsOpen) {
    if (editGroup) {
      setName(editGroup.name);
      setDescription(editGroup.description || '');
    } else {
      setName('');
      setDescription('');
    }
    setNameError('');
    setSelectedPrinterIds(new Set());
    setSearchQuery('');
    setModelFilter('all');
  }
  if (isOpen !== prevIsOpen) {
    setPrevIsOpen(isOpen);
  }

  const togglePrinter = (printerId: string) => {
    setSelectedPrinterIds((prev) => {
      const next = new Set(prev);
      if (next.has(printerId)) {
        next.delete(printerId);
      } else {
        next.add(printerId);
      }
      return next;
    });
  };

  const createMutation = useMutation({
    mutationFn: async (dto: CreatePrinterGroupRequest) => {
      const created = await apiClient.createPrinterGroup(dto);
      // Assign selected printers to the newly created group
      for (const printerId of selectedPrinterIds) {
        await apiClient.assignPrinterToGroup(created.id, printerId);
      }
      return created;
    },
    onSuccess: (created) => {
      queryClient.invalidateQueries({ queryKey: ['printer-groups'] });
      queryClient.invalidateQueries({ queryKey: ['printers'] });
      const count = selectedPrinterIds.size;
      const suffix = count > 0 ? ` with ${count} printer${count !== 1 ? 's' : ''}` : '';
      toast.success(`Group "${created.name}" created${suffix}`);
      onClose();
    },
    onError: (error: { message?: string; details?: string }) => {
      toast.error(`Failed to create group: ${error.details || error.message || 'Unknown error'}`);
    },
  });

  const updateMutation = useMutation({
    mutationFn: async ({ id, dto }: { id: string; dto: UpdatePrinterGroupRequest }) => {
      const updated = await apiClient.updatePrinterGroup(id, dto);
      // Assign any newly selected printers
      for (const printerId of selectedPrinterIds) {
        await apiClient.assignPrinterToGroup(id, printerId);
      }
      return updated;
    },
    onSuccess: (updated) => {
      queryClient.invalidateQueries({ queryKey: ['printer-groups'] });
      queryClient.invalidateQueries({ queryKey: ['printer-groups', editGroup?.id] });
      queryClient.invalidateQueries({ queryKey: ['printers'] });
      toast.success(`Group "${updated.name}" updated`);
      onClose();
    },
    onError: (error: { message?: string; details?: string }) => {
      toast.error(`Failed to update group: ${error.details || error.message || 'Unknown error'}`);
    },
  });

  const handleSubmit = () => {
    if (!name.trim()) {
      setNameError('Name is required');
      return;
    }
    setNameError('');

    const dto = {
      name: name.trim(),
      description: description.trim() || undefined,
    };

    if (editGroup) {
      updateMutation.mutate({ id: editGroup.id, dto });
    } else {
      createMutation.mutate(dto);
    }
  };

  const isPending = createMutation.isPending || updateMutation.isPending;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={editGroup ? 'Edit Printer Group' : 'Create Printer Group'}
      size="lg"
      footer={
        <div className="flex gap-3">
          <Button variant="secondary" onClick={onClose} disabled={isPending}>
            Cancel
          </Button>
          <Button variant="primary" onClick={handleSubmit} loading={isPending} disabled={isPending}>
            {editGroup ? 'Save' : 'Create Group'}
          </Button>
        </div>
      }
    >
      <div className="space-y-5">
        <FormField label="Name" htmlFor="group-name" required error={nameError}>
          <Input
            id="group-name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="e.g., MK4S Fleet"
            invalid={!!nameError}
            disabled={isPending}
          />
        </FormField>

        <FormField label="Description" htmlFor="group-description">
          <Textarea
            id="group-description"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Optional description"
            rows={2}
            disabled={isPending}
          />
        </FormField>

        {/* Printer selection */}
        <div>
          <div className="flex items-center justify-between mb-2">
            <label className="text-sm font-medium text-pf-text-primary">
              {editGroup ? 'Add Printers' : 'Assign Printers'}
            </label>
            {modelName && (
              <Badge variant="primary" size="sm">{modelName}</Badge>
            )}
          </div>
          <p className="text-xs text-pf-text-secondary mb-3">
            {effectiveModelId
              ? 'Only printers with the same model are shown (groups must be homogeneous).'
              : 'Select printers to add. All printers in a group must be the same model.'}
          </p>

          {/* Search and filter bar */}
          <div className="flex gap-2 mb-2">
            <div className="flex-1 relative">
              <SearchIcon className="absolute left-2.5 top-1/2 -translate-y-1/2 w-4 h-4 text-pf-text-tertiary pointer-events-none" />
              <Input
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                placeholder="Search printers..."
                className="pl-8"
                disabled={isPending}
              />
            </div>
            {!effectiveModelId && uniqueModels.length > 1 && (
              <Select
                value={modelFilter}
                onChange={(e) => setModelFilter(e.target.value)}
                containerClassName="w-48"
                disabled={isPending}
              >
                <option value="all">All models</option>
                {uniqueModels.map(([id, label]) => (
                  <option key={id} value={id}>{label}</option>
                ))}
              </Select>
            )}
          </div>

          {availablePrinters.length === 0 ? (
            <p className="text-sm text-pf-text-tertiary py-3 text-center border border-pf-border rounded-lg bg-pf-bg-2">
              {searchQuery
                ? 'No printers match your search'
                : effectiveModelId
                  ? 'No additional matching printers available'
                  : 'No printers available'}
            </p>
          ) : (
            <div className="max-h-48 overflow-y-auto border border-pf-border rounded-lg divide-y divide-pf-border/50">
              {availablePrinters.map((printer) => (
                <label
                  key={printer.id}
                  className="flex items-center gap-3 px-3 py-2 hover:bg-pf-bg-2/50 cursor-pointer"
                >
                  <Checkbox
                    checked={selectedPrinterIds.has(printer.id)}
                    onChange={() => togglePrinter(printer.id)}
                    disabled={isPending}
                  />
                  <span className="text-sm text-pf-text-primary flex-1">{printer.name}</span>
                  <span className="text-xs text-pf-text-secondary">
                    {printer.modelName ?? 'Unknown model'}
                  </span>
                  <Badge variant="default" size="sm">{backendLabel(printer.backend)}</Badge>
                </label>
              ))}
            </div>
          )}
          {selectedPrinterIds.size > 0 && (
            <p className="text-xs text-pf-text-secondary mt-2">
              {selectedPrinterIds.size} printer{selectedPrinterIds.size !== 1 ? 's' : ''} selected
            </p>
          )}
        </div>
      </div>
    </Modal>
  );
}

