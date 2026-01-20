import { useState, useCallback } from 'react';
import { Button, Input, FormField, Checkbox } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { PlusIcon, DownloadIcon } from '@/common/components/icons/MdiIcons';
import { 
  useFilamentTypes, 
  useCreateFilamentType, 
  useUpdateFilamentType, 
  useDeleteFilamentType,
  useImportFilamentTypesFromSpoolman
} from '@/common/hooks/useApi';
import type { FilamentTypeDto, CreateFilamentTypeRequest, UpdateFilamentTypeRequest, TempTargets } from '@/types/api';

/**
 * Card display for filament type
 */
interface FilamentTypeCardProps {
  filament: FilamentTypeDto;
  onEdit: (filament: FilamentTypeDto) => void;
  onDelete: (filament: FilamentTypeDto) => void;
  isDeleting?: boolean;
}

function FilamentTypeCard({ filament, onEdit, onDelete, isDeleting }: FilamentTypeCardProps) {
  return (
    <div className="bg-pf-card border border-pf-border rounded-lg p-4 hover:border-pf-primary/50 transition-colors">
      <div className="flex justify-between items-start mb-3">
        <h3 className="font-semibold text-pf-text-primary text-lg">{filament.name}</h3>
        <div className="flex gap-1">
          <Button
            variant="ghost"
            size="xs"
            onClick={() => onEdit(filament)}
            title="Edit filament type"
          >
            Edit
          </Button>
          <Button
            variant="ghost"
            size="xs"
            onClick={() => onDelete(filament)}
            disabled={isDeleting}
            className="text-red-400 hover:text-red-300"
            title="Delete filament type"
          >
            {isDeleting ? '...' : 'Delete'}
          </Button>
        </div>
      </div>
      
      <div className="space-y-2 text-sm">
        {/* Temperature info */}
        <div className="flex gap-4">
          <div className="text-pf-text-secondary">
            <span className="text-pf-text-muted">Hotend:</span>{' '}
            <span className="text-orange-400">{filament.defaultTemperatures?.hotend ?? '—'}°C</span>
          </div>
          <div className="text-pf-text-secondary">
            <span className="text-pf-text-muted">Bed:</span>{' '}
            <span className="text-blue-400">{filament.defaultTemperatures?.bed ?? '—'}°C</span>
          </div>
        </div>
        
        {/* Properties */}
        <div className="flex flex-wrap gap-2">
          {filament.isAbrasive && (
            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs bg-amber-500/20 text-amber-400 border border-amber-500/30">
              Abrasive
            </span>
          )}
          {filament.needsEnclosure && (
            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs bg-purple-500/20 text-purple-400 border border-purple-500/30">
              Needs Enclosure
            </span>
          )}
          {!filament.isAbrasive && !filament.needsEnclosure && (
            <span className="text-pf-text-muted text-xs">Standard</span>
          )}
        </div>
      </div>
    </div>
  );
}

/**
 * Form state for add/edit filament modal
 */
interface FilamentFormState {
  name: string;
  hotendTemp: string;
  bedTemp: string;
  isAbrasive: boolean;
  needsEnclosure: boolean;
}

const emptyForm: FilamentFormState = {
  name: '',
  hotendTemp: '',
  bedTemp: '',
  isAbrasive: false,
  needsEnclosure: false,
};

/**
 * FilamentsCatalog - Catalog tab for managing filament types
 * 
 * Features:
 * - Grid display of all filament types
 * - Add new filament type with temperature settings
 * - Edit existing filament types
 * - Delete filament types with confirmation
 * - Import filament types from Spoolman
 */
export function FilamentsCatalog() {
  // Data queries
  const { data: filamentTypes, isLoading, isError } = useFilamentTypes();

  // Mutations
  const createMutation = useCreateFilamentType();
  const updateMutation = useUpdateFilamentType();
  const deleteMutation = useDeleteFilamentType();
  const importMutation = useImportFilamentTypesFromSpoolman();

  // Modal state
  const [isAddModalOpen, setIsAddModalOpen] = useState(false);
  const [editingFilament, setEditingFilament] = useState<FilamentTypeDto | null>(null);
  const [deletingFilament, setDeletingFilament] = useState<FilamentTypeDto | null>(null);
  const [formState, setFormState] = useState<FilamentFormState>(emptyForm);
  const [formErrors, setFormErrors] = useState<Partial<Record<keyof FilamentFormState, string>>>({});

  // Open add modal
  const handleAddClick = useCallback(() => {
    setFormState(emptyForm);
    setFormErrors({});
    setIsAddModalOpen(true);
  }, []);

  // Open edit modal
  const handleEditClick = useCallback((filament: FilamentTypeDto) => {
    setFormState({
      name: filament.name,
      hotendTemp: filament.defaultTemperatures?.hotend?.toString() ?? '',
      bedTemp: filament.defaultTemperatures?.bed?.toString() ?? '',
      isAbrasive: filament.isAbrasive,
      needsEnclosure: filament.needsEnclosure,
    });
    setFormErrors({});
    setEditingFilament(filament);
  }, []);

  // Open delete confirmation
  const handleDeleteClick = useCallback((filament: FilamentTypeDto) => {
    setDeletingFilament(filament);
  }, []);

  // Close modals
  const handleCloseAddModal = useCallback(() => {
    setIsAddModalOpen(false);
    setFormState(emptyForm);
    setFormErrors({});
  }, []);

  const handleCloseEditModal = useCallback(() => {
    setEditingFilament(null);
    setFormState(emptyForm);
    setFormErrors({});
  }, []);

  const handleCloseDeleteModal = useCallback(() => {
    setDeletingFilament(null);
  }, []);

  // Form validation
  const validateForm = useCallback((): boolean => {
    const errors: Partial<Record<keyof FilamentFormState, string>> = {};

    if (!formState.name.trim()) {
      errors.name = 'Name is required';
    }
    if (formState.hotendTemp && (isNaN(Number(formState.hotendTemp)) || Number(formState.hotendTemp) < 0)) {
      errors.hotendTemp = 'Hotend temperature must be a positive number';
    }
    if (formState.bedTemp && (isNaN(Number(formState.bedTemp)) || Number(formState.bedTemp) < 0)) {
      errors.bedTemp = 'Bed temperature must be a positive number';
    }

    setFormErrors(errors);
    return Object.keys(errors).length === 0;
  }, [formState]);

  // Build temperature targets from form
  const buildTempTargets = useCallback((): TempTargets => {
    return {
      hotend: formState.hotendTemp ? Number(formState.hotendTemp) : 200,
      bed: formState.bedTemp ? Number(formState.bedTemp) : 60,
    };
  }, [formState.hotendTemp, formState.bedTemp]);

  // Handle form submission for add
  const handleAdd = useCallback(async () => {
    if (!validateForm()) return;

    const dto: CreateFilamentTypeRequest = {
      name: formState.name.trim(),
      defaultTemperatures: buildTempTargets(),
      isAbrasive: formState.isAbrasive,
      needsEnclosure: formState.needsEnclosure,
    };

    try {
      await createMutation.mutateAsync(dto);
      handleCloseAddModal();
    } catch {
      // Error handled by mutation
    }
  }, [formState, validateForm, buildTempTargets, createMutation, handleCloseAddModal]);

  // Handle form submission for edit
  const handleUpdate = useCallback(async () => {
    if (!editingFilament || !validateForm()) return;

    const dto: UpdateFilamentTypeRequest = {
      name: formState.name.trim(),
      defaultTemperatures: buildTempTargets(),
      isAbrasive: formState.isAbrasive,
      needsEnclosure: formState.needsEnclosure,
    };

    try {
      await updateMutation.mutateAsync({ id: editingFilament.id, dto });
      handleCloseEditModal();
    } catch {
      // Error handled by mutation
    }
  }, [editingFilament, formState, validateForm, buildTempTargets, updateMutation, handleCloseEditModal]);

  // Handle delete confirmation
  const handleDelete = useCallback(async () => {
    if (!deletingFilament) return;

    try {
      await deleteMutation.mutateAsync(deletingFilament.id);
      handleCloseDeleteModal();
    } catch {
      // Error handled by mutation
    }
  }, [deletingFilament, deleteMutation, handleCloseDeleteModal]);

  // Handle Spoolman import
  const handleImportFromSpoolman = useCallback(async () => {
    try {
      await importMutation.mutateAsync();
      // Mutation success automatically invalidates the query and refreshes the list
    } catch {
      // Error handled by mutation
    }
  }, [importMutation]);

  // Handle form field changes
  const handleFieldChange = useCallback((field: keyof FilamentFormState, value: string | boolean) => {
    setFormState(prev => ({ ...prev, [field]: value }));
    setFormErrors(prev => ({ ...prev, [field]: undefined }));
  }, []);

  // Render loading state
  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-pf-text-secondary">Loading filament types...</div>
      </div>
    );
  }

  // Render error state
  if (isError) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-red-500">Failed to load filament types</div>
      </div>
    );
  }

  const sortedFilaments = [...(filamentTypes ?? [])].sort((a, b) => a.name.localeCompare(b.name));

  return (
    <div className="space-y-4">
      {/* Header with Add and Import buttons */}
      <div className="flex justify-between items-center">
        <h2 className="text-lg font-semibold text-pf-text-primary">
          Filament Types ({sortedFilaments.length})
        </h2>
        <div className="flex gap-2">
          <Button 
            onClick={handleImportFromSpoolman}
            title="Import filament types from Spoolman"
            size="sm"
            variant="secondary"
            disabled={importMutation.isPending}
            iconLeft={<DownloadIcon className="w-4 h-4 mr-1" />}
          >
            {importMutation.isPending ? 'Importing...' : 'Import from Spoolman'}
          </Button>
          <Button 
            onClick={handleAddClick}
            title="Add new filament type"
            size="sm"
            iconLeft={<PlusIcon className="w-4 h-4 mr-1" />}
          >
            Add
          </Button>
        </div>
      </div>

      {/* Grid of filament cards */}
      {sortedFilaments.length === 0 ? (
        <div className="text-center py-12 text-pf-text-secondary">
          <p>No filament types defined yet.</p>
          <p className="mt-2">Click "Add" to create your first one, or "Import from Spoolman" to import existing types.</p>
        </div>
      ) : (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
          {sortedFilaments.map(filament => (
            <FilamentTypeCard
              key={filament.id}
              filament={filament}
              onEdit={handleEditClick}
              onDelete={handleDeleteClick}
              isDeleting={deleteMutation.isPending && deletingFilament?.id === filament.id}
            />
          ))}
        </div>
      )}

      {/* Add Filament Modal */}
      <Modal
        isOpen={isAddModalOpen}
        onClose={handleCloseAddModal}
        title="Add Filament Type"
        size="md"
      >
        <FilamentForm
          formState={formState}
          formErrors={formErrors}
          onFieldChange={handleFieldChange}
          onSubmit={handleAdd}
          onCancel={handleCloseAddModal}
          isSubmitting={createMutation.isPending}
          submitLabel="Create Filament"
        />
      </Modal>

      {/* Edit Filament Modal */}
      <Modal
        isOpen={!!editingFilament}
        onClose={handleCloseEditModal}
        title={`Edit Filament: ${editingFilament?.name ?? ''}`}
        size="md"
      >
        <FilamentForm
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
        isOpen={!!deletingFilament}
        onClose={handleCloseDeleteModal}
        title="Delete Filament Type"
        size="sm"
      >
        <div className="space-y-4">
          <p className="text-pf-text-secondary">
            Are you sure you want to delete <strong>{deletingFilament?.name}</strong>?
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
 * Reusable form for add/edit filament modal
 */
interface FilamentFormProps {
  formState: FilamentFormState;
  formErrors: Partial<Record<keyof FilamentFormState, string>>;
  onFieldChange: (field: keyof FilamentFormState, value: string | boolean) => void;
  onSubmit: () => void;
  onCancel: () => void;
  isSubmitting: boolean;
  submitLabel: string;
}

function FilamentForm({
  formState,
  formErrors,
  onFieldChange,
  onSubmit,
  onCancel,
  isSubmitting,
  submitLabel,
}: FilamentFormProps) {
  return (
    <div className="space-y-4">
      <FormField
        label="Name"
        required
        error={formErrors.name}
      >
        <Input
          value={formState.name}
          onChange={(e) => onFieldChange('name', e.target.value)}
          placeholder="e.g., PLA, PETG, ABS"
        />
      </FormField>

      <div className="grid grid-cols-2 gap-4">
        <FormField
          label="Hotend Temperature (°C)"
          error={formErrors.hotendTemp}
        >
          <Input
            type="number"
            value={formState.hotendTemp}
            onChange={(e) => onFieldChange('hotendTemp', e.target.value)}
            placeholder="e.g., 210"
          />
        </FormField>

        <FormField
          label="Bed Temperature (°C)"
          error={formErrors.bedTemp}
        >
          <Input
            type="number"
            value={formState.bedTemp}
            onChange={(e) => onFieldChange('bedTemp', e.target.value)}
            placeholder="e.g., 60"
          />
        </FormField>
      </div>

      <div className="space-y-2">
        <Checkbox
          checked={formState.isAbrasive}
          onChange={(e) => onFieldChange('isAbrasive', e.target.checked)}
          label="Abrasive (requires hardened nozzle)"
        />
        <p className="text-xs text-pf-text-muted ml-6">
          e.g., Carbon fiber, glass fiber, or metal-filled filaments
        </p>
      </div>

      <div className="space-y-2">
        <Checkbox
          checked={formState.needsEnclosure}
          onChange={(e) => onFieldChange('needsEnclosure', e.target.checked)}
          label="Requires enclosure"
        />
        <p className="text-xs text-pf-text-muted ml-6">
          e.g., ABS, ASA, Nylon, or other materials that warp without enclosure
        </p>
      </div>

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

export default FilamentsCatalog;
