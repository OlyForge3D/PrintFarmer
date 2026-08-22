import { useState, useCallback, useMemo } from 'react';
import {
  Button,
  Input,
  FormField,
  Checkbox,
  Badge,
  DataTable,
  type DataTableColumn,
} from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { PlusIcon, EditIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import {
  useNozzleMaterials,
  useCreateNozzleMaterial,
  useUpdateNozzleMaterial,
  useDeleteNozzleMaterial,
} from '@/common/hooks/useApi';
import type { NozzleMaterialDto, CreateNozzleMaterialDto, UpdateNozzleMaterialDto } from '@/types/api';

/**
 * Form state for add/edit nozzle material modal
 */
interface MaterialFormState {
  name: string;
  isHardened: boolean;
  defaultMaxTemp: string;
  description: string;
}

const emptyForm: MaterialFormState = {
  name: '',
  isHardened: false,
  defaultMaxTemp: '',
  description: '',
};

/**
 * NozzleMaterialsCatalog - Materials sub-section of the Nozzles catalog tab
 *
 * Lets a farm administrator add, edit, and remove nozzle materials (name, hardened
 * flag, default max temperature) so they can be immediately selected on a nozzle
 * model without a code change or redeploy. Built-in materials (seeded from the
 * legacy `NozzleType` enum, see #1824) can be edited but not deleted; materials
 * that are in use by an existing nozzle model cannot be deleted either — see
 * `DeleteNozzleMaterialAsync` in `CatalogService.cs` for the authoritative guard.
 */
export function NozzleMaterialsCatalog() {
  const { data: materials, isLoading, isError } = useNozzleMaterials();

  const createMutation = useCreateNozzleMaterial();
  const updateMutation = useUpdateNozzleMaterial();
  const deleteMutation = useDeleteNozzleMaterial();

  const [isAddModalOpen, setIsAddModalOpen] = useState(false);
  const [editingMaterial, setEditingMaterial] = useState<NozzleMaterialDto | null>(null);
  const [deletingMaterial, setDeletingMaterial] = useState<NozzleMaterialDto | null>(null);
  const [formState, setFormState] = useState<MaterialFormState>(emptyForm);
  const [formErrors, setFormErrors] = useState<Partial<Record<keyof MaterialFormState, string>>>({});

  const columns = useMemo<DataTableColumn<NozzleMaterialDto>[]>(() => [
    {
      key: 'name',
      header: 'Name',
      sortable: true,
      sort: (a, b) => a.name.localeCompare(b.name),
      render: (item) => (
        <span className="font-medium inline-flex items-center gap-2">
          {item.name}
          {item.isBuiltIn && <Badge variant="default">Built-in</Badge>}
        </span>
      ),
    },
    {
      key: 'isHardened',
      header: 'Hardened',
      sortable: true,
      sort: (a, b) => Number(a.isHardened) - Number(b.isHardened),
      render: (item) => (item.isHardened ? <Badge variant="warning">Hardened</Badge> : <span className="text-pf-text-muted">No</span>),
    },
    {
      key: 'defaultMaxTemp',
      header: 'Default Max Temp (°C)',
      sortable: true,
      sort: (a, b) => (a.defaultMaxTemp ?? 0) - (b.defaultMaxTemp ?? 0),
      render: (item) => item.defaultMaxTemp != null ? `${item.defaultMaxTemp}°C` : '—',
    },
    {
      key: 'description',
      header: 'Description',
      render: (item) => item.description || <span className="text-pf-text-muted">—</span>,
    },
  ], []);

  const handleAddClick = useCallback(() => {
    setFormState(emptyForm);
    setFormErrors({});
    setIsAddModalOpen(true);
  }, []);

  const handleEditClick = useCallback((material: NozzleMaterialDto) => {
    setFormState({
      name: material.name,
      isHardened: material.isHardened,
      defaultMaxTemp: material.defaultMaxTemp?.toString() ?? '',
      description: material.description ?? '',
    });
    setFormErrors({});
    setEditingMaterial(material);
  }, []);

  const handleDeleteClick = useCallback((material: NozzleMaterialDto) => {
    setDeletingMaterial(material);
  }, []);

  const handleCloseAddModal = useCallback(() => {
    setIsAddModalOpen(false);
    setFormState(emptyForm);
    setFormErrors({});
  }, []);

  const handleCloseEditModal = useCallback(() => {
    setEditingMaterial(null);
    setFormState(emptyForm);
    setFormErrors({});
  }, []);

  const handleCloseDeleteModal = useCallback(() => {
    setDeletingMaterial(null);
  }, []);

  const validateForm = useCallback((): boolean => {
    const errors: Partial<Record<keyof MaterialFormState, string>> = {};

    if (!formState.name.trim()) {
      errors.name = 'Name is required';
    }
    if (
      formState.defaultMaxTemp &&
      (isNaN(Number(formState.defaultMaxTemp)) || Number(formState.defaultMaxTemp) < 0)
    ) {
      errors.defaultMaxTemp = 'Default max temperature must be a positive number';
    }

    setFormErrors(errors);
    return Object.keys(errors).length === 0;
  }, [formState]);

  const handleFieldChange = useCallback(<K extends keyof MaterialFormState>(field: K, value: MaterialFormState[K]) => {
    setFormState(prev => ({ ...prev, [field]: value }));
    setFormErrors(prev => ({ ...prev, [field]: undefined }));
  }, []);

  const handleAdd = useCallback(async () => {
    if (!validateForm()) return;

    const dto: CreateNozzleMaterialDto = {
      name: formState.name.trim(),
      isHardened: formState.isHardened,
      defaultMaxTemp: formState.defaultMaxTemp ? Number(formState.defaultMaxTemp) : undefined,
      description: formState.description.trim() || undefined,
    };

    try {
      await createMutation.mutateAsync(dto);
      handleCloseAddModal();
    } catch {
      // Error handled by mutation
    }
  }, [formState, validateForm, createMutation, handleCloseAddModal]);

  const handleUpdate = useCallback(async () => {
    if (!editingMaterial || !validateForm()) return;

    const dto: UpdateNozzleMaterialDto = {
      name: formState.name.trim(),
      isHardened: formState.isHardened,
      defaultMaxTemp: formState.defaultMaxTemp ? Number(formState.defaultMaxTemp) : undefined,
      description: formState.description.trim() || undefined,
    };

    try {
      await updateMutation.mutateAsync({ id: editingMaterial.id, dto });
      handleCloseEditModal();
    } catch {
      // Error handled by mutation
    }
  }, [editingMaterial, formState, validateForm, updateMutation, handleCloseEditModal]);

  const handleDelete = useCallback(async () => {
    if (!deletingMaterial) return;

    try {
      await deleteMutation.mutateAsync(deletingMaterial.id);
      handleCloseDeleteModal();
    } catch {
      // Error handled by mutation
    }
  }, [deletingMaterial, deleteMutation, handleCloseDeleteModal]);

  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-pf-text-secondary">Loading nozzle materials...</div>
      </div>
    );
  }

  if (isError) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-pf-error">Failed to load nozzle materials</div>
      </div>
    );
  }

  const items = materials ?? [];

  return (
    <div className="space-y-4">
      <div className="flex justify-between items-center">
        <h2 className="text-lg font-semibold text-pf-text-primary">
          Nozzle Materials ({items.length})
        </h2>
        <Button
          onClick={handleAddClick}
          size="sm"
          title="Add new nozzle material"
          iconLeft={<PlusIcon className="w-4 h-4 mr-1" />}
        >
          Add
        </Button>
      </div>

      {items.length === 0 ? (
        <div className="text-center py-12 text-pf-text-secondary">
          <p>No nozzle materials defined yet.</p>
          <p className="mt-2">Click "Add" to create your first one.</p>
        </div>
      ) : (
        <DataTable
          data={items}
          columns={columns}
          getRowKey={(item) => item.id}
          keyboardNavigation
          ariaLabel="Nozzle materials catalog"
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
                onClick={() => handleDeleteClick(item)}
                title={item.isBuiltIn ? `${item.name} is built-in and cannot be deleted` : `Delete ${item.name}`}
                disabled={item.isBuiltIn || (deleteMutation.isPending && deletingMaterial?.id === item.id)}
              >
                <DeleteIcon className="w-4 h-4 text-pf-error" />
              </Button>
            </div>
          )}
        />
      )}

      {/* Add Material Modal */}
      <Modal
        isOpen={isAddModalOpen}
        onClose={handleCloseAddModal}
        title="Add Nozzle Material"
        size="md"
      >
        <MaterialForm
          formState={formState}
          formErrors={formErrors}
          onFieldChange={handleFieldChange}
          onSubmit={handleAdd}
          onCancel={handleCloseAddModal}
          isSubmitting={createMutation.isPending}
          submitLabel="Create Material"
        />
      </Modal>

      {/* Edit Material Modal */}
      <Modal
        isOpen={!!editingMaterial}
        onClose={handleCloseEditModal}
        title={`Edit Material: ${editingMaterial?.name ?? ''}`}
        size="md"
      >
        <MaterialForm
          formState={formState}
          formErrors={formErrors}
          onFieldChange={handleFieldChange}
          onSubmit={handleUpdate}
          onCancel={handleCloseEditModal}
          isSubmitting={updateMutation.isPending}
          submitLabel="Save Changes"
        />
      </Modal>

      {/* Delete Confirmation Modal */}
      <Modal
        isOpen={!!deletingMaterial}
        onClose={handleCloseDeleteModal}
        title="Delete Nozzle Material"
        size="sm"
      >
        <div className="space-y-4">
          <p className="text-pf-text-secondary">
            Are you sure you want to delete <strong>{deletingMaterial?.name}</strong>?
            This action cannot be undone. Materials in use by a nozzle model cannot be deleted.
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
 * Reusable form for add/edit nozzle material modal
 */
interface MaterialFormProps {
  formState: MaterialFormState;
  formErrors: Partial<Record<keyof MaterialFormState, string>>;
  onFieldChange: <K extends keyof MaterialFormState>(field: K, value: MaterialFormState[K]) => void;
  onSubmit: () => void;
  onCancel: () => void;
  isSubmitting: boolean;
  submitLabel: string;
}

function MaterialForm({
  formState,
  formErrors,
  onFieldChange,
  onSubmit,
  onCancel,
  isSubmitting,
  submitLabel,
}: MaterialFormProps) {
  return (
    <div className="space-y-4">
      <FormField label="Name" required error={formErrors.name}>
        <Input
          value={formState.name}
          onChange={(e) => onFieldChange('name', e.target.value)}
          placeholder="e.g., Nozzle X Alloy"
        />
      </FormField>

      <div className="grid grid-cols-2 gap-4">
        <FormField
          label="Default Max Temp (°C)"
          error={formErrors.defaultMaxTemp}
          helper="Used to pre-fill the nozzle model's max temp when this material is selected."
        >
          <Input
            type="number"
            value={formState.defaultMaxTemp}
            onChange={(e) => onFieldChange('defaultMaxTemp', e.target.value)}
            placeholder="e.g., 500"
          />
        </FormField>

        <FormField
          label="Hardened"
          helper="Abrasion-resistant materials that can safely print abrasive filaments."
        >
          <Checkbox
            checked={formState.isHardened}
            onChange={(e) => onFieldChange('isHardened', e.target.checked)}
            label="This material is hardened"
          />
        </FormField>
      </div>

      <FormField label="Description" error={formErrors.description}>
        <Input
          value={formState.description}
          onChange={(e) => onFieldChange('description', e.target.value)}
          placeholder="Optional notes about this material"
        />
      </FormField>

      <div className="flex justify-end gap-2 pt-2">
        <Button variant="secondary" onClick={onCancel} disabled={isSubmitting}>
          Cancel
        </Button>
        <Button onClick={onSubmit} disabled={isSubmitting}>
          {isSubmitting ? 'Saving...' : submitLabel}
        </Button>
      </div>
    </div>
  );
}
