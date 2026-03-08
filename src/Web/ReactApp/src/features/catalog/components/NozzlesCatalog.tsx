import React, { useState, useCallback, useMemo } from 'react';
import { 
  Button, 
  Input, 
  FormField, 
  Select, 
  Textarea,
  Badge,
  DataTable,
  type DataTableColumn,
  ViewToggle,
  gridTableOptions,
} from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { ManufacturerSelector } from '@/common/components/ManufacturerSelector';
import { ComponentModelCard, type NozzleModelCardData } from '@/common/components/ComponentModelCard';
import { useNozzleModels, useCreateNozzleModel, useUpdateNozzleModel, useDeleteNozzleModel } from '@/common/hooks/useApi';
import { CatalogContext, type NozzleModelDefinition, type CreateNozzleModelDto, type UpdateNozzleModelDto, NozzleTypeStringLabels } from '@/types/api';
import { PlusIcon, EditIcon, DeleteIcon, CopyIcon } from '@/common/components/icons/MdiIcons';
import { useCatalogViewMode } from '@/common/hooks/useCatalogViewMode';

/**
 * Converts a NozzleModelDefinition to the card display format
 */
function toCardData(model: NozzleModelDefinition): NozzleModelCardData {
  return {
    type: 'nozzle',
    id: model.id,
    name: model.name,
    manufacturerId: model.manufacturerId,
    manufacturerName: model.manufacturerName,
    description: model.description,
    url: model.url,
    maxTemp: model.maxTemp,
    isHardened: model.isHardened,
  };
}

/**
 * Form state for add/edit nozzle modal
 */
interface NozzleFormState {
  name: string;
  manufacturerId: string;
  manufacturerName?: string;
  diameter: string;
  maxTemp: string;
  nozzleType: string;
  description: string;
  url: string;
}

// Common nozzle diameters in mm
const COMMON_NOZZLE_DIAMETERS = ['0.2', '0.25', '0.3', '0.4', '0.5', '0.6', '0.8', '1.0', '1.2'];

// Default max temperatures by nozzle type (°C)
const DEFAULT_TEMPS_BY_TYPE: Record<string, number> = {
  'Brass': 300,
  'HardenedSteel': 500,
  'Steel': 450,
  'Copper': 350,
  'Ruby': 500,
  'Tungsten': 550,
  'PlatedCopper': 400,
  'Other': 300,
};

const emptyForm: NozzleFormState = {
  name: '',
  manufacturerId: '',
  diameter: '0.4',
  maxTemp: '300',
  nozzleType: 'Brass',
  description: '',
  url: '',
};

/**
 * NozzlesCatalog - Catalog tab for managing nozzle models
 * 
 * Features:
 * - Grid display of all nozzle models
 * - Add new nozzle with grouped manufacturer selector
 * - Edit existing nozzles
 * - Delete nozzles with confirmation
 */
export function NozzlesCatalog() {
  // Data queries
  const { data: nozzleModels, isLoading, isError } = useNozzleModels();

  // Mutations
  const createMutation = useCreateNozzleModel();
  const updateMutation = useUpdateNozzleModel();
  const deleteMutation = useDeleteNozzleModel();

  // Modal state
  const [isAddModalOpen, setIsAddModalOpen] = useState(false);
  const [editingModel, setEditingModel] = useState<NozzleModelDefinition | null>(null);
  const [deletingModel, setDeletingModel] = useState<NozzleModelDefinition | null>(null);
  const [formState, setFormState] = useState<NozzleFormState>(emptyForm);
  const [formErrors, setFormErrors] = useState<Partial<Record<keyof NozzleFormState, string>>>({});

  // View toggle state (grid vs table) - persisted per tab
  const [view, setView] = useCatalogViewMode('nozzles');

  // Define columns for DataTable with built-in sorting
  const columns = useMemo<DataTableColumn<NozzleModelCardData>[]>(() => [
    {
      key: 'name',
      header: 'Name',
      sortable: true,
      sort: (a, b) => a.name.localeCompare(b.name),
      render: (item) => <span className="font-medium">{item.name}</span>,
    },
    {
      key: 'manufacturer',
      header: 'Manufacturer',
      sortable: true,
      sort: (a, b) => (a.manufacturerName ?? '').localeCompare(b.manufacturerName ?? ''),
      render: (item) => item.manufacturerName ?? '—',
    },
    {
      key: 'maxTemp',
      header: 'Max Temp',
      sortable: true,
      sort: (a, b) => (a.maxTemp ?? 0) - (b.maxTemp ?? 0),
      render: (item) => item.maxTemp != null ? `${item.maxTemp}°C` : '—',
    },
    {
      key: 'material',
      header: 'Material',
      sortable: true,
      sort: (a, b) => (a.isHardened ? 1 : 0) - (b.isHardened ? 1 : 0),
      render: (item) => item.isHardened 
        ? <Badge variant="warning" size="sm">Hardened</Badge>
        : <span className="text-sm text-pf-text-muted">Standard</span>,
    },
  ], []);

  // Open add modal
  const handleAddClick = useCallback(() => {
    setFormState(emptyForm);
    setFormErrors({});
    setIsAddModalOpen(true);
  }, []);

  // Open edit modal
  const handleEditClick = useCallback((card: NozzleModelCardData) => {
    const model = nozzleModels?.find(m => m.id === card.id);
    if (!model) return;

    setFormState({
      name: model.name,
      manufacturerId: model.manufacturerId,
      manufacturerName: model.manufacturerName,
      diameter: model.diameter?.toString() ?? '0.4',
      maxTemp: model.maxTemp?.toString() ?? '',
      nozzleType: typeof model.nozzleType === 'string' ? model.nozzleType : 'Brass',
      description: model.description ?? '',
      url: model.url ?? '',
    });
    setFormErrors({});
    setEditingModel(model);
  }, [nozzleModels]);

  // Clone an existing nozzle model
  const handleCloneClick = useCallback((card: NozzleModelCardData) => {
    const model = nozzleModels?.find(m => m.id === card.id);
    if (!model) return;

    setFormState({
      name: `${model.name} - Copy`,
      manufacturerId: model.manufacturerId,
      manufacturerName: model.manufacturerName,
      diameter: model.diameter?.toString() ?? '0.4',
      maxTemp: model.maxTemp?.toString() ?? '',
      nozzleType: typeof model.nozzleType === 'string' ? model.nozzleType : 'Brass',
      description: model.description ?? '',
      url: model.url ?? '',
    });
    setFormErrors({});
    setIsAddModalOpen(true); // Use add modal since we're creating a new record
  }, [nozzleModels]);

  // Open delete confirmation
  const handleDeleteClick = useCallback((card: NozzleModelCardData) => {
    const model = nozzleModels?.find(m => m.id === card.id);
    if (model) {
      setDeletingModel(model);
    }
  }, [nozzleModels]);

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
    const errors: Partial<Record<keyof NozzleFormState, string>> = {};

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

  // Handle nozzle type change - update default temp
  const handleNozzleTypeChange = useCallback((newType: string) => {
    setFormState(prev => ({
      ...prev,
      nozzleType: newType,
      maxTemp: DEFAULT_TEMPS_BY_TYPE[newType]?.toString() || prev.maxTemp,
    }));
  }, []);

  // Handle form submission for add
  const handleAdd = useCallback(async () => {
    if (!validateForm()) return;

    const dto: CreateNozzleModelDto = {
      name: formState.name.trim(),
      manufacturerId: formState.manufacturerId,
      diameter: formState.diameter ? Number(formState.diameter) : 0.4,
      maxTemp: formState.maxTemp ? Number(formState.maxTemp) : undefined,
      nozzleType: formState.nozzleType,
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

    const dto: UpdateNozzleModelDto = {
      name: formState.name.trim(),
      manufacturerId: formState.manufacturerId,
      diameter: formState.diameter ? Number(formState.diameter) : undefined,
      maxTemp: formState.maxTemp ? Number(formState.maxTemp) : undefined,
      nozzleType: formState.nozzleType,
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
  const handleFieldChange = useCallback((field: keyof NozzleFormState, value: string) => {
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
        <div className="text-pf-text-secondary">Loading nozzle models...</div>
      </div>
    );
  }

  // Render error state
  if (isError) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-pf-error">Failed to load nozzle models</div>
      </div>
    );
  }

  const cards = (nozzleModels ?? []).map(toCardData);

  return (
    <div className="space-y-4">
      {/* Header with Add button */}
      <div className="flex justify-between items-center">
        <h2 className="text-lg font-semibold text-pf-text-primary">
          Nozzle Models ({cards.length})
        </h2>
        <div className="flex items-center gap-2">
          <Button 
            onClick={handleAddClick} 
            size="sm"
            title="Add new nozzle model"
            iconLeft={<PlusIcon className="w-4 h-4 mr-1" />}
          >
            Add
          </Button>
          <ViewToggle value={view} onChange={setView} options={gridTableOptions} />
        </div>
      </div>

      {/* Grid or Table view of nozzle cards */}
      {cards.length === 0 ? (
        <div className="text-center py-12 text-pf-text-secondary">
          <p>No nozzle models defined yet.</p>
          <p className="mt-2">Click "Add Nozzle" to create your first one.</p>
        </div>
      ) : view === 'grid' ? (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
          {cards.map(card => (
            <ComponentModelCard
              key={card.id}
              model={card}
              onEdit={handleEditClick}
              onClone={handleCloneClick}
              onDelete={handleDeleteClick}
              isLoading={deleteMutation.isPending && deletingModel?.id === card.id}
            />
          ))}
        </div>
      ) : (
        <DataTable
          data={cards}
          columns={columns}
          getRowKey={(item) => item.id}
          keyboardNavigation
          defaultSortColumn="name"
          renderActions={(item) => (
            <div className="flex gap-1">
              <Button
                variant="subtle"
                size="sm"
                onClick={() => handleEditClick(item)}
                title={`Edit ${item.name}`}
              >
                <EditIcon className="w-4 h-4" />
              </Button>
              <Button
                variant="subtle"
                size="sm"
                onClick={() => handleCloneClick(item)}
                title={`Clone ${item.name}`}
              >
                <CopyIcon className="w-4 h-4" />
              </Button>
              <Button
                variant="subtle"
                size="sm"
                onClick={() => handleDeleteClick(item)}
                title={`Delete ${item.name}`}
                disabled={deleteMutation.isPending && deletingModel?.id === item.id}
              >
                <DeleteIcon className="w-4 h-4 text-pf-error" />
              </Button>
            </div>
          )}
        />
      )}

      {/* Add Nozzle Modal */}
      <Modal
        isOpen={isAddModalOpen}
        onClose={handleCloseAddModal}
        title="Add Nozzle"
        size="md"
      >
        <NozzleForm
          formState={formState}
          formErrors={formErrors}
          onFieldChange={handleFieldChange}
          onManufacturerChange={handleManufacturerChange}
          onNozzleTypeChange={handleNozzleTypeChange}
          onSubmit={handleAdd}
          onCancel={handleCloseAddModal}
          isSubmitting={createMutation.isPending}
          submitLabel="Create Nozzle"
        />
      </Modal>

      {/* Edit Nozzle Modal */}
      <Modal
        isOpen={!!editingModel}
        onClose={handleCloseEditModal}
        title={`Edit Nozzle: ${editingModel?.name ?? ''}`}
        size="md"
      >
        <NozzleForm
          formState={formState}
          formErrors={formErrors}
          onFieldChange={handleFieldChange}
          onManufacturerChange={handleManufacturerChange}
          onNozzleTypeChange={handleNozzleTypeChange}
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
        title="Delete Nozzle"
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
 * Reusable form for add/edit nozzle modal
 */
interface NozzleFormProps {
  formState: NozzleFormState;
  formErrors: Partial<Record<keyof NozzleFormState, string>>;
  onFieldChange: (field: keyof NozzleFormState, value: string) => void;
  onManufacturerChange: (manufacturerId: string | undefined, manufacturerName?: string) => void;
  onNozzleTypeChange: (nozzleType: string) => void;
  onSubmit: () => void;
  onCancel: () => void;
  isSubmitting: boolean;
  submitLabel: string;
}

function NozzleForm({
  formState,
  formErrors,
  onFieldChange,
  onManufacturerChange,
  onNozzleTypeChange,
  onSubmit,
  onCancel,
  isSubmitting,
  submitLabel,
}: NozzleFormProps) {
  const [customDiameter, setCustomDiameter] = useState('');
  const [showCustomDiameter, setShowCustomDiameter] = useState(false);

  // Handle diameter selection or custom input
  const handleDiameterChange = (value: string) => {
    if (value === 'custom') {
      setShowCustomDiameter(true);
    } else {
      setShowCustomDiameter(false);
      onFieldChange('diameter', value);
    }
  };

  const handleCustomDiameterSubmit = () => {
    const num = parseFloat(customDiameter);
    if (!isNaN(num) && num > 0 && num <= 3) {
      onFieldChange('diameter', num.toString());
      setShowCustomDiameter(false);
      setCustomDiameter('');
    }
  };

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
          context={CatalogContext.Nozzles}
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
          placeholder="e.g., Volcano Hardened Steel 0.4mm"
        />
      </FormField>

      <div className="grid grid-cols-3 gap-4">
        <FormField label="Diameter (mm)" required error={formErrors.diameter}>
          {showCustomDiameter ? (
            <div className="flex gap-2">
              <Input
                type="number"
                step="0.01"
                min="0.1"
                max="3"
                value={customDiameter}
                onChange={(e) => setCustomDiameter(e.target.value)}
                placeholder="e.g., 0.35"
                className="flex-1"
              />
              <Button
                variant="primary"
                size="sm"
                onClick={handleCustomDiameterSubmit}
                disabled={!customDiameter}
              >
                Set
              </Button>
              <Button
                variant="secondary"
                size="sm"
                onClick={() => {
                  setShowCustomDiameter(false);
                  setCustomDiameter('');
                }}
              >
                ✕
              </Button>
            </div>
          ) : (
            <Select
              value={formState.diameter}
              onChange={(e) => handleDiameterChange(e.target.value)}
            >
              {COMMON_NOZZLE_DIAMETERS.map((d) => (
                <option key={d} value={d}>
                  {d}mm
                </option>
              ))}
              <option value="custom">Custom...</option>
            </Select>
          )}
        </FormField>

        <FormField label="Nozzle Type">
          <Select
            value={formState.nozzleType}
            onChange={(e) => {
              const newType = e.target.value;
              onFieldChange('nozzleType', newType);
              onNozzleTypeChange(newType);
            }}
          >
            {Object.entries(NozzleTypeStringLabels).map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </Select>
        </FormField>

        <FormField
          label="Max Temp (°C)"
          error={formErrors.maxTemp}
        >
          <Input
            type="number"
            value={formState.maxTemp}
            onChange={(e) => onFieldChange('maxTemp', e.target.value)}
            placeholder="e.g., 500"
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

export default NozzlesCatalog;
