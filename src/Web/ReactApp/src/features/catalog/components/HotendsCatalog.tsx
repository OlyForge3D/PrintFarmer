import React, { useState, useCallback } from 'react';
import { Button, Input, FormField, Checkbox, Textarea } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { ManufacturerSelector } from '@/common/components/ManufacturerSelector';
import { ComponentModelCard, type HotendModelCardData } from '@/common/components/ComponentModelCard';
import { useHotendModels, useCreateHotendModel, useUpdateHotendModel, useDeleteHotendModel } from '@/common/hooks/useApi';
import { CatalogContext, type HotendModelDefinition, type CreateHotendModelDto, type UpdateHotendModelDto } from '@/types/api';
import { PlusIcon } from '@/common/components/icons/MdiIcons';

/**
 * Converts a HotendModelDefinition to the card display format
 */
function toCardData(model: HotendModelDefinition): HotendModelCardData {
  return {
    type: 'hotend',
    id: model.id,
    name: model.name,
    manufacturerId: model.manufacturerId,
    manufacturerName: model.manufacturerName,
    description: model.description,
    url: model.url,
    maxTemp: model.maxTemp,
    isHighFlow: model.isHighFlow,
  };
}

/**
 * Form state for add/edit hotend modal
 */
interface HotendFormState {
  name: string;
  manufacturerId: string;
  manufacturerName?: string;
  maxTemp: string;
  isHighFlow: boolean;
  description: string;
  url: string;
}

const emptyForm: HotendFormState = {
  name: '',
  manufacturerId: '',
  maxTemp: '',
  isHighFlow: false,
  description: '',
  url: '',
};

/**
 * HotendsCatalog - Catalog tab for managing hotend models
 * 
 * Features:
 * - Grid display of all hotend models
 * - Add new hotend with grouped manufacturer selector
 * - Edit existing hotends
 * - Delete hotends with confirmation
 */
export function HotendsCatalog() {
  // Data queries
  const { data: hotendModels, isLoading, isError } = useHotendModels();

  // Mutations
  const createMutation = useCreateHotendModel();
  const updateMutation = useUpdateHotendModel();
  const deleteMutation = useDeleteHotendModel();

  // Modal state
  const [isAddModalOpen, setIsAddModalOpen] = useState(false);
  const [editingModel, setEditingModel] = useState<HotendModelDefinition | null>(null);
  const [deletingModel, setDeletingModel] = useState<HotendModelDefinition | null>(null);
  const [formState, setFormState] = useState<HotendFormState>(emptyForm);
  const [formErrors, setFormErrors] = useState<Partial<Record<keyof HotendFormState, string>>>({});

  // Open add modal
  const handleAddClick = useCallback(() => {
    setFormState(emptyForm);
    setFormErrors({});
    setIsAddModalOpen(true);
  }, []);

  // Open edit modal
  const handleEditClick = useCallback((card: HotendModelCardData) => {
    const model = hotendModels?.find(m => m.id === card.id);
    if (!model) return;

    setFormState({
      name: model.name,
      manufacturerId: model.manufacturerId,
      manufacturerName: model.manufacturerName,
      maxTemp: model.maxTemp?.toString() ?? '',
      isHighFlow: model.isHighFlow,
      description: model.description ?? '',
      url: model.url ?? '',
    });
    setFormErrors({});
    setEditingModel(model);
  }, [hotendModels]);

  // Open delete confirmation
  const handleDeleteClick = useCallback((card: HotendModelCardData) => {
    const model = hotendModels?.find(m => m.id === card.id);
    if (model) {
      setDeletingModel(model);
    }
  }, [hotendModels]);

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
    const errors: Partial<Record<keyof HotendFormState, string>> = {};

    if (!formState.name.trim()) {
      errors.name = 'Name is required';
    }
    if (!formState.manufacturerId) {
      errors.manufacturerId = 'Manufacturer is required';
    }
    if (formState.maxTemp && (isNaN(Number(formState.maxTemp)) || Number(formState.maxTemp) < 0)) {
      errors.maxTemp = 'Max temperature must be a positive number';
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

    const dto: CreateHotendModelDto = {
      name: formState.name.trim(),
      manufacturerId: formState.manufacturerId,
      maxTemp: formState.maxTemp ? Number(formState.maxTemp) : undefined,
      isHighFlow: formState.isHighFlow,
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

    const dto: UpdateHotendModelDto = {
      name: formState.name.trim(),
      manufacturerId: formState.manufacturerId,
      maxTemp: formState.maxTemp ? Number(formState.maxTemp) : undefined,
      isHighFlow: formState.isHighFlow,
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
  const handleFieldChange = useCallback((field: keyof HotendFormState, value: string | boolean) => {
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
        <div className="text-pf-text-secondary">Loading hotend models...</div>
      </div>
    );
  }

  // Render error state
  if (isError) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-red-500">Failed to load hotend models</div>
      </div>
    );
  }

  const cards = (hotendModels ?? []).map(toCardData);

  return (
    <div className="space-y-4">
      {/* Header with Add button */}
      <div className="flex justify-between items-center">
        <h2 className="text-lg font-semibold text-pf-text-primary">
          Hotend Models ({cards.length})
        </h2>
        <Button 
          onClick={handleAddClick}
          title="Add new hotend model"
          size="sm"
          iconLeft={<PlusIcon className="w-4 h-4 mr-1" />}
        >
          Add
        </Button>
      </div>

      {/* Grid of hotend cards */}
      {cards.length === 0 ? (
        <div className="text-center py-12 text-pf-text-secondary">
          <p>No hotend models defined yet.</p>
          <p className="mt-2">Click "Add Hotend" to create your first one.</p>
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

      {/* Add Hotend Modal */}
      <Modal
        isOpen={isAddModalOpen}
        onClose={handleCloseAddModal}
        title="Add Hotend"
        size="md"
      >
        <HotendForm
          formState={formState}
          formErrors={formErrors}
          onFieldChange={handleFieldChange}
          onManufacturerChange={handleManufacturerChange}
          onSubmit={handleAdd}
          onCancel={handleCloseAddModal}
          isSubmitting={createMutation.isPending}
          submitLabel="Create Hotend"
        />
      </Modal>

      {/* Edit Hotend Modal */}
      <Modal
        isOpen={!!editingModel}
        onClose={handleCloseEditModal}
        title={`Edit Hotend: ${editingModel?.name ?? ''}`}
        size="md"
      >
        <HotendForm
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
        title="Delete Hotend"
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
 * Reusable form for add/edit hotend modal
 */
interface HotendFormProps {
  formState: HotendFormState;
  formErrors: Partial<Record<keyof HotendFormState, string>>;
  onFieldChange: (field: keyof HotendFormState, value: string | boolean) => void;
  onManufacturerChange: (manufacturerId: string | undefined, manufacturerName?: string) => void;
  onSubmit: () => void;
  onCancel: () => void;
  isSubmitting: boolean;
  submitLabel: string;
}

function HotendForm({
  formState,
  formErrors,
  onFieldChange,
  onManufacturerChange,
  onSubmit,
  onCancel,
  isSubmitting,
  submitLabel,
}: HotendFormProps) {
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
          context={CatalogContext.Hotends}
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
          placeholder="e.g., Dragon HF"
        />
      </FormField>

      <div className="grid grid-cols-2 gap-4">
        <FormField
          label="Max Temperature (°C)"
          error={formErrors.maxTemp}
        >
          <Input
            type="number"
            value={formState.maxTemp}
            onChange={(e) => onFieldChange('maxTemp', e.target.value)}
            placeholder="e.g., 500"
          />
        </FormField>

        <FormField label="">
          <div className="flex items-center h-10">
            <Checkbox
              checked={formState.isHighFlow}
              onChange={(e) => onFieldChange('isHighFlow', e.target.checked)}
              label="High Flow"
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

export default HotendsCatalog;
