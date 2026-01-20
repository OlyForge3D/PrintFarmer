import React, { useState, useCallback, useMemo } from 'react';
import { Button, Input, FormField, Textarea, Select } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { ManufacturerSelector } from '@/common/components/ManufacturerSelector';
import { ComponentModelCard, type ToolheadModelCardData } from '@/common/components/ComponentModelCard';
import { 
  useToolheadModels, 
  useCreateToolheadModel, 
  useUpdateToolheadModel, 
  useDeleteToolheadModel,
  useHotendModels,
  useExtruderModels,
  useNozzleModels,
} from '@/common/hooks/useApi';
import { CatalogContext, type ToolheadModelDefinition, type CreateToolheadModelDto, type UpdateToolheadModelDefDto } from '@/types/api';
import { PlusIcon } from '@/common/components/icons/MdiIcons';

/**
 * Converts a ToolheadModelDefinition to the card display format
 */
function toCardData(model: ToolheadModelDefinition): ToolheadModelCardData {
  return {
    type: 'toolhead',
    id: model.id,
    name: model.name,
    manufacturerId: model.manufacturerId,
    manufacturerName: model.manufacturerName,
    description: model.description,
    url: model.url,
  };
}

/**
 * Form state for add/edit toolhead modal
 */
interface ToolheadFormState {
  name: string;
  manufacturerId: string;
  manufacturerName?: string;
  description: string;
  url: string;
  defaultHotendId: string;
  defaultExtruderId: string;
  defaultNozzleId: string;
}

const emptyForm: ToolheadFormState = {
  name: '',
  manufacturerId: '',
  description: '',
  url: '',
  defaultHotendId: '',
  defaultExtruderId: '',
  defaultNozzleId: '',
};

/**
 * ToolheadsCatalog - Catalog tab for managing toolhead models
 * 
 * Features:
 * - Grid display of all toolhead models
 * - Add new toolhead with grouped manufacturer selector
 * - Edit existing toolheads with component associations
 * - Delete toolheads with confirmation
 */
export function ToolheadsCatalog() {
  // Data queries
  const { data: toolheadModels, isLoading, isError } = useToolheadModels();
  const { data: hotendModels } = useHotendModels();
  const { data: extruderModels } = useExtruderModels();
  const { data: nozzleModels } = useNozzleModels();

  // Build options for component selectors (grouped by manufacturer)
  const hotendOptions = useMemo(() => {
    if (!hotendModels) return [];
    const grouped = new Map<string, { id: string; name: string; mfgName: string }[]>();
    hotendModels.forEach(h => {
      const mfg = h.manufacturerName ?? 'Unknown';
      if (!grouped.has(mfg)) grouped.set(mfg, []);
      grouped.get(mfg)!.push({ id: h.id, name: h.name, mfgName: mfg });
    });
    return Array.from(grouped.entries()).sort(([a], [b]) => a.localeCompare(b));
  }, [hotendModels]);

  const extruderOptions = useMemo(() => {
    if (!extruderModels) return [];
    const grouped = new Map<string, { id: string; name: string; mfgName: string }[]>();
    extruderModels.forEach(e => {
      const mfg = e.manufacturerName ?? 'Unknown';
      if (!grouped.has(mfg)) grouped.set(mfg, []);
      grouped.get(mfg)!.push({ id: e.id, name: e.name, mfgName: mfg });
    });
    return Array.from(grouped.entries()).sort(([a], [b]) => a.localeCompare(b));
  }, [extruderModels]);

  const nozzleOptions = useMemo(() => {
    if (!nozzleModels) return [];
    const grouped = new Map<string, { id: string; name: string; mfgName: string }[]>();
    nozzleModels.forEach(n => {
      const mfg = n.manufacturerName ?? 'Unknown';
      if (!grouped.has(mfg)) grouped.set(mfg, []);
      grouped.get(mfg)!.push({ id: n.id, name: n.name, mfgName: mfg });
    });
    return Array.from(grouped.entries()).sort(([a], [b]) => a.localeCompare(b));
  }, [nozzleModels]);

  // Mutations
  const createMutation = useCreateToolheadModel();
  const updateMutation = useUpdateToolheadModel();
  const deleteMutation = useDeleteToolheadModel();

  // Modal state
  const [isAddModalOpen, setIsAddModalOpen] = useState(false);
  const [editingModel, setEditingModel] = useState<ToolheadModelDefinition | null>(null);
  const [deletingModel, setDeletingModel] = useState<ToolheadModelDefinition | null>(null);
  const [formState, setFormState] = useState<ToolheadFormState>(emptyForm);
  const [formErrors, setFormErrors] = useState<Partial<Record<keyof ToolheadFormState, string>>>({});

  // Open add modal
  const handleAddClick = useCallback(() => {
    setFormState(emptyForm);
    setFormErrors({});
    setIsAddModalOpen(true);
  }, []);

  // Open edit modal
  const handleEditClick = useCallback((card: ToolheadModelCardData) => {
    const model = toolheadModels?.find(m => m.id === card.id);
    if (!model) return;

    setFormState({
      name: model.name,
      manufacturerId: model.manufacturerId,
      manufacturerName: model.manufacturerName,
      description: model.description ?? '',
      url: model.url ?? '',
      defaultHotendId: model.defaultHotendId ?? '',
      defaultExtruderId: model.defaultExtruderId ?? '',
      defaultNozzleId: model.defaultNozzleId ?? '',
    });
    setFormErrors({});
    setEditingModel(model);
  }, [toolheadModels]);

  // Open delete confirmation
  const handleDeleteClick = useCallback((card: ToolheadModelCardData) => {
    const model = toolheadModels?.find(m => m.id === card.id);
    if (model) {
      setDeletingModel(model);
    }
  }, [toolheadModels]);

  // Close modals
  const handleCloseAddModal = useCallback(() => {
    setIsAddModalOpen(false);
    setFormState(emptyForm);
    setFormErrors({});
  }, []);

  const handleCloseEditModal = useCallback(() => {
    setEditingModel(null);
    setFormState(emptyForm);
    setFormErrors({});
  }, []);

  const handleCloseDeleteModal = useCallback(() => {
    setDeletingModel(null);
  }, []);

  // Form validation
  const validateForm = useCallback((): boolean => {
    const errors: Partial<Record<keyof ToolheadFormState, string>> = {};

    if (!formState.name.trim()) {
      errors.name = 'Name is required';
    }
    if (!formState.manufacturerId) {
      errors.manufacturerId = 'Manufacturer is required';
    }
    if (formState.url && !formState.url.startsWith('http')) {
      errors.url = 'URL must start with http:// or https://';
    }

    setFormErrors(errors);
    return Object.keys(errors).length === 0;
  }, [formState]);

  // Handle form submission for add
  const handleAdd = useCallback(async () => {
    if (!validateForm()) return;

    const dto: CreateToolheadModelDto = {
      name: formState.name.trim(),
      manufacturerId: formState.manufacturerId,
      description: formState.description.trim() || undefined,
      url: formState.url.trim() || undefined,
    };

    try {
      await createMutation.mutateAsync(dto);
      handleCloseAddModal();
    } catch {
      // Error handled by mutation
    }
  }, [formState, validateForm, createMutation, handleCloseAddModal]);

  // Handle form submission for edit
  const handleUpdate = useCallback(async () => {
    if (!editingModel || !validateForm()) return;

    const dto: UpdateToolheadModelDefDto = {
      name: formState.name.trim(),
      manufacturerId: formState.manufacturerId,
      description: formState.description.trim() || undefined,
      url: formState.url.trim() || undefined,
      // Send null to clear, undefined to leave unchanged, or the ID to set
      defaultHotendId: formState.defaultHotendId || null,
      defaultExtruderId: formState.defaultExtruderId || null,
      defaultNozzleId: formState.defaultNozzleId || null,
    };

    try {
      await updateMutation.mutateAsync({ id: editingModel.id, dto });
      handleCloseEditModal();
    } catch {
      // Error handled by mutation
    }
  }, [editingModel, formState, validateForm, updateMutation, handleCloseEditModal]);

  // Handle delete confirmation
  const handleDelete = useCallback(async () => {
    if (!deletingModel) return;

    try {
      await deleteMutation.mutateAsync(deletingModel.id);
      handleCloseDeleteModal();
    } catch {
      // Error handled by mutation
    }
  }, [deletingModel, deleteMutation, handleCloseDeleteModal]);

  // Handle form field changes
  const handleFieldChange = useCallback((field: keyof ToolheadFormState, value: string) => {
    setFormState(prev => ({ ...prev, [field]: value }));
    setFormErrors(prev => ({ ...prev, [field]: undefined }));
  }, []);

  // Handle manufacturer selection
  const handleManufacturerChange = useCallback((manufacturerId: string | undefined, manufacturerName?: string) => {
    setFormState(prev => ({
      ...prev,
      manufacturerId: manufacturerId ?? '',
      manufacturerName,
    }));
    setFormErrors(prev => ({ ...prev, manufacturerId: undefined }));
  }, []);

  // Render loading state
  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-pf-text-secondary">Loading toolhead models...</div>
      </div>
    );
  }

  // Render error state
  if (isError) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-red-500">Failed to load toolhead models</div>
      </div>
    );
  }

  const cards = (toolheadModels ?? []).map(toCardData);

  return (
    <div className="space-y-4">
      {/* Header with Add button */}
      <div className="flex justify-between items-center">
        <h2 className="text-lg font-semibold text-pf-text-primary">
          Toolhead Models ({cards.length})
        </h2>
        <Button 
          onClick={handleAddClick} 
          size="sm"
          title="Add new toolhead model"
          iconLeft={<PlusIcon className="w-4 h-4 mr-1" />}
        >
          Add
        </Button>
      </div>

      {/* Grid of toolhead cards */}
      {cards.length === 0 ? (
        <div className="text-center py-12 text-pf-text-secondary">
          <p>No toolhead models defined yet.</p>
          <p className="mt-2">Click "Add Toolhead" to create your first one.</p>
        </div>
      ) : (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
          {cards.map(card => (
            <ComponentModelCard
              key={card.id}
              model={card}
              onEdit={handleEditClick}
              onDelete={handleDeleteClick}
              isLoading={deleteMutation.isPending && deletingModel?.id === card.id}
            />
          ))}
        </div>
      )}

      {/* Add Toolhead Modal */}
      <Modal
        isOpen={isAddModalOpen}
        onClose={handleCloseAddModal}
        title="Add Toolhead"
        size="md"
        footer={
          <div className="flex justify-end gap-3">
            <Button variant="secondary" onClick={handleCloseAddModal}>
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={handleAdd}
              disabled={createMutation.isPending}
            >
              {createMutation.isPending ? 'Creating...' : 'Create Toolhead'}
            </Button>
          </div>
        }
      >
        <ToolheadForm
          formState={formState}
          formErrors={formErrors}
          onFieldChange={handleFieldChange}
          onManufacturerChange={handleManufacturerChange}
        />
      </Modal>

      {/* Edit Toolhead Modal */}
      <Modal
        isOpen={!!editingModel}
        onClose={handleCloseEditModal}
        title={`Edit Toolhead: ${editingModel?.name ?? ''}`}
        size="lg"
        footer={
          <div className="flex justify-end gap-3">
            <Button variant="secondary" onClick={handleCloseEditModal}>
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={handleUpdate}
              disabled={updateMutation.isPending}
            >
              {updateMutation.isPending ? 'Saving...' : 'Save Changes'}
            </Button>
          </div>
        }
      >
        <ToolheadForm
          formState={formState}
          formErrors={formErrors}
          onFieldChange={handleFieldChange}
          onManufacturerChange={handleManufacturerChange}
          showComponentAssociations
          hotendOptions={hotendOptions}
          extruderOptions={extruderOptions}
          nozzleOptions={nozzleOptions}
        />
      </Modal>

      {/* Delete Confirmation Modal */}
      <Modal
        isOpen={!!deletingModel}
        onClose={handleCloseDeleteModal}
        title="Delete Toolhead"
        size="sm"
      >
        <div className="space-y-4">
          <p className="text-pf-text-secondary">
            Are you sure you want to delete <strong>{deletingModel?.name}</strong>?
            This action cannot be undone.
          </p>
          <div className="flex justify-end gap-2">
            <Button variant="secondary" onClick={handleCloseDeleteModal}>
              Cancel
            </Button>
            <Button
              variant="danger"
              onClick={handleDelete}
              disabled={deleteMutation.isPending}
            >
              {deleteMutation.isPending ? 'Deleting...' : 'Delete'}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
}

/**
 * Reusable form for add/edit toolhead modal
 */
interface ToolheadFormProps {
  formState: ToolheadFormState;
  formErrors: Partial<Record<keyof ToolheadFormState, string>>;
  onFieldChange: (field: keyof ToolheadFormState, value: string) => void;
  onManufacturerChange: (manufacturerId: string | undefined, manufacturerName?: string) => void;
  /** If true, show component association fields (for edit mode) */
  showComponentAssociations?: boolean;
  /** Grouped hotend options: [manufacturerName, items[]][] */
  hotendOptions?: [string, { id: string; name: string; mfgName: string }[]][];
  /** Grouped extruder options: [manufacturerName, items[]][] */
  extruderOptions?: [string, { id: string; name: string; mfgName: string }[]][];
  /** Grouped nozzle options: [manufacturerName, items[]][] */
  nozzleOptions?: [string, { id: string; name: string; mfgName: string }[]][];
}

function ToolheadForm({
  formState,
  formErrors,
  onFieldChange,
  onManufacturerChange,
  showComponentAssociations = false,
  hotendOptions = [],
  extruderOptions = [],
  nozzleOptions = [],
}: ToolheadFormProps) {
  return (
    <div className="space-y-4">
      {/* Two-column grid for basic fields */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <FormField
          label="Manufacturer"
          required
          error={formErrors.manufacturerId}
        >
          <ManufacturerSelector
            value={formState.manufacturerId || undefined}
            onChange={onManufacturerChange}
            context={CatalogContext.Toolheads}
            required
            placeholder="Select manufacturer..."
          />
        </FormField>

        <FormField
          label="Name"
          required
          error={formErrors.name}
        >
          <Input
            value={formState.name}
            onChange={(e) => onFieldChange('name', e.target.value)}
            placeholder="e.g., Stealthburner"
          />
        </FormField>
      </div>

      <FormField
        label="Description"
        error={formErrors.description}
      >
        <Textarea
          value={formState.description}
          onChange={(e) => onFieldChange('description', e.target.value)}
          placeholder="Optional description..."
          rows={2}
        />
      </FormField>

      <FormField
        label="Product URL"
        error={formErrors.url}
      >
        <Input
          type="url"
          value={formState.url}
          onChange={(e) => onFieldChange('url', e.target.value)}
          placeholder="https://..."
        />
      </FormField>

      {/* Component Associations - only shown in edit mode */}
      {showComponentAssociations && (
        <div className="border-t border-pf-border pt-4 mt-4">
          <h4 className="text-sm font-medium text-pf-text-primary mb-2">
            Default Components
          </h4>
          <p className="text-xs text-pf-text-secondary mb-4">
            Optionally associate default components with this toolhead. These will be used as defaults when creating printers with this toolhead.
          </p>

          {/* Three-column grid for component selects */}
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
            <FormField label="Default Hotend">
              <Select
                value={formState.defaultHotendId}
                onChange={(e) => onFieldChange('defaultHotendId', e.target.value)}
              >
                <option value="">— None —</option>
                {hotendOptions.map(([mfgName, items]) => (
                  <optgroup key={mfgName} label={mfgName}>
                    {items.map(item => (
                      <option key={item.id} value={item.id}>
                        {item.name}
                      </option>
                    ))}
                  </optgroup>
                ))}
              </Select>
            </FormField>

            <FormField label="Default Extruder">
              <Select
                value={formState.defaultExtruderId}
                onChange={(e) => onFieldChange('defaultExtruderId', e.target.value)}
              >
                <option value="">— None —</option>
                {extruderOptions.map(([mfgName, items]) => (
                  <optgroup key={mfgName} label={mfgName}>
                    {items.map(item => (
                      <option key={item.id} value={item.id}>
                        {item.name}
                      </option>
                    ))}
                  </optgroup>
                ))}
              </Select>
            </FormField>

            <FormField label="Default Nozzle">
              <Select
                value={formState.defaultNozzleId}
                onChange={(e) => onFieldChange('defaultNozzleId', e.target.value)}
              >
                <option value="">— None —</option>
                {nozzleOptions.map(([mfgName, items]) => (
                  <optgroup key={mfgName} label={mfgName}>
                    {items.map(item => (
                      <option key={item.id} value={item.id}>
                        {item.name}
                      </option>
                    ))}
                  </optgroup>
                ))}
              </Select>
            </FormField>
          </div>
        </div>
      )}
    </div>
  );
}

export default ToolheadsCatalog;
