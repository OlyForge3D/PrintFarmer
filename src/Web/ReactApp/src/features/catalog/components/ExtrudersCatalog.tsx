import React, { useState, useCallback } from 'react';
import { Modal, Button, Input, FormField, Checkbox, Textarea } from '@/common/components/ui';
import { ManufacturerSelector } from '@/common/components/ManufacturerSelector';
import { ComponentModelCard, type ExtruderModelCardData } from '@/common/components/ComponentModelCard';
import { useExtruderModels, useCreateExtruderModel, useUpdateExtruderModel, useDeleteExtruderModel } from '@/common/hooks/useApi';
import { CatalogContext, type ExtruderModelDefinition, type CreateExtruderModelDto, type UpdateExtruderModelDto } from '@/types/api';
import { PlusIcon } from '@/common/components/icons/MdiIcons';

/**
 * Converts an ExtruderModelDefinition to the card display format
 */
function toCardData(model: ExtruderModelDefinition): ExtruderModelCardData {
  return {
    type: 'extruder',
    id: model.id,
    name: model.name,
    manufacturerId: model.manufacturerId,
    manufacturerName: model.manufacturerName,
    description: model.description,
    url: model.url,
    gearRatio: model.gearRatio,
    isDirectDrive: model.isDirectDrive,
  };
}

/**
 * Form state for add/edit extruder modal
 */
interface ExtruderFormState {
  name: string;
  manufacturerId: string;
  manufacturerName?: string;
  gearRatio: string;
  isDirectDrive: boolean;
  description: string;
  url: string;
}

const emptyForm: ExtruderFormState = {
  name: '',
  manufacturerId: '',
  gearRatio: '',
  isDirectDrive: true,
  description: '',
  url: '',
};

/**
 * ExtrudersCatalog - Catalog tab for managing extruder models
 * 
 * Features:
 * - Grid display of all extruder models
 * - Add new extruder with grouped manufacturer selector
 * - Edit existing extruders
 * - Delete extruders with confirmation
 */
export function ExtrudersCatalog() {
  // Data queries
  const { data: extruderModels, isLoading, isError } = useExtruderModels();

  // Mutations
  const createMutation = useCreateExtruderModel();
  const updateMutation = useUpdateExtruderModel();
  const deleteMutation = useDeleteExtruderModel();

  // Modal state
  const [isAddModalOpen, setIsAddModalOpen] = useState(false);
  const [editingModel, setEditingModel] = useState<ExtruderModelDefinition | null>(null);
  const [deletingModel, setDeletingModel] = useState<ExtruderModelDefinition | null>(null);
  const [formState, setFormState] = useState<ExtruderFormState>(emptyForm);
  const [formErrors, setFormErrors] = useState<Partial<Record<keyof ExtruderFormState, string>>>({});

  // Open add modal
  const handleAddClick = useCallback(() => {
    setFormState(emptyForm);
    setFormErrors({});
    setIsAddModalOpen(true);
  }, []);

  // Open edit modal
  const handleEditClick = useCallback((card: ExtruderModelCardData) => {
    const model = extruderModels?.find(m => m.id === card.id);
    if (!model) return;

    setFormState({
      name: model.name,
      manufacturerId: model.manufacturerId,
      manufacturerName: model.manufacturerName,
      gearRatio: model.gearRatio ?? '',
      isDirectDrive: model.isDirectDrive,
      description: model.description ?? '',
      url: model.url ?? '',
    });
    setFormErrors({});
    setEditingModel(model);
  }, [extruderModels]);

  // Open delete confirmation
  const handleDeleteClick = useCallback((card: ExtruderModelCardData) => {
    const model = extruderModels?.find(m => m.id === card.id);
    if (model) {
      setDeletingModel(model);
    }
  }, [extruderModels]);

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
    const errors: Partial<Record<keyof ExtruderFormState, string>> = {};

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

    const dto: CreateExtruderModelDto = {
      name: formState.name.trim(),
      manufacturerId: formState.manufacturerId,
      gearRatio: formState.gearRatio.trim() || undefined,
      isDirectDrive: formState.isDirectDrive,
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

    const dto: UpdateExtruderModelDto = {
      name: formState.name.trim(),
      manufacturerId: formState.manufacturerId,
      gearRatio: formState.gearRatio.trim() || undefined,
      isDirectDrive: formState.isDirectDrive,
      description: formState.description.trim() || undefined,
      url: formState.url.trim() || undefined,
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
  const handleFieldChange = useCallback((field: keyof ExtruderFormState, value: string | boolean) => {
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
        <div className="text-pf-text-secondary">Loading extruder models...</div>
      </div>
    );
  }

  // Render error state
  if (isError) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-red-500">Failed to load extruder models</div>
      </div>
    );
  }

  const cards = (extruderModels ?? []).map(toCardData);

  return (
    <div className="space-y-4">
      {/* Header with Add button */}
      <div className="flex justify-between items-center">
        <h2 className="text-lg font-semibold text-pf-text-primary">
          Extruder Models ({cards.length})
        </h2>
        <Button onClick={handleAddClick} size="sm">
          <PlusIcon className="w-4 h-4 mr-1" />
          Add Extruder
        </Button>
      </div>

      {/* Grid of extruder cards */}
      {cards.length === 0 ? (
        <div className="text-center py-12 text-pf-text-secondary">
          <p>No extruder models defined yet.</p>
          <p className="mt-2">Click "Add Extruder" to create your first one.</p>
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

      {/* Add Extruder Modal */}
      <Modal
        isOpen={isAddModalOpen}
        onClose={handleCloseAddModal}
        title="Add Extruder"
        size="md"
      >
        <ExtruderForm
          formState={formState}
          formErrors={formErrors}
          onFieldChange={handleFieldChange}
          onManufacturerChange={handleManufacturerChange}
          onSubmit={handleAdd}
          onCancel={handleCloseAddModal}
          isSubmitting={createMutation.isPending}
          submitLabel="Create Extruder"
        />
      </Modal>

      {/* Edit Extruder Modal */}
      <Modal
        isOpen={!!editingModel}
        onClose={handleCloseEditModal}
        title={`Edit Extruder: ${editingModel?.name ?? ''}`}
        size="md"
      >
        <ExtruderForm
          formState={formState}
          formErrors={formErrors}
          onFieldChange={handleFieldChange}
          onManufacturerChange={handleManufacturerChange}
          onSubmit={handleUpdate}
          onCancel={handleCloseEditModal}
          isSubmitting={updateMutation.isPending}
          submitLabel="Save Changes"
        />
      </Modal>

      {/* Delete Confirmation Modal */}
      <Modal
        isOpen={!!deletingModel}
        onClose={handleCloseDeleteModal}
        title="Delete Extruder"
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
 * Reusable form for add/edit extruder modal
 */
interface ExtruderFormProps {
  formState: ExtruderFormState;
  formErrors: Partial<Record<keyof ExtruderFormState, string>>;
  onFieldChange: (field: keyof ExtruderFormState, value: string | boolean) => void;
  onManufacturerChange: (manufacturerId: string | undefined, manufacturerName?: string) => void;
  onSubmit: () => void;
  onCancel: () => void;
  isSubmitting: boolean;
  submitLabel: string;
}

function ExtruderForm({
  formState,
  formErrors,
  onFieldChange,
  onManufacturerChange,
  onSubmit,
  onCancel,
  isSubmitting,
  submitLabel,
}: ExtruderFormProps) {
  return (
    <div className="space-y-4">
      <FormField
        label="Manufacturer"
        required
        error={formErrors.manufacturerId}
      >
        <ManufacturerSelector
          value={formState.manufacturerId || undefined}
          onChange={onManufacturerChange}
          context={CatalogContext.Extruders}
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
          placeholder="e.g., Orbiter 2.0"
        />
      </FormField>

      <div className="grid grid-cols-2 gap-4">
        <FormField
          label="Gear Ratio"
          error={formErrors.gearRatio}
        >
          <Input
            value={formState.gearRatio}
            onChange={(e) => onFieldChange('gearRatio', e.target.value)}
            placeholder="e.g., 7.5:1"
          />
        </FormField>

        <FormField label="">
          <div className="flex items-center h-10">
            <Checkbox
              checked={formState.isDirectDrive}
              onChange={(e) => onFieldChange('isDirectDrive', e.target.checked)}
              label="Direct Drive"
            />
          </div>
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

      <div className="flex justify-end gap-2 pt-2">
        <Button variant="secondary" onClick={onCancel}>
          Cancel
        </Button>
        <Button
          variant="primary"
          onClick={onSubmit}
          disabled={isSubmitting}
        >
          {isSubmitting ? 'Saving...' : submitLabel}
        </Button>
      </div>
    </div>
  );
}

export default ExtrudersCatalog;
