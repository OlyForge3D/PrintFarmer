import { useState, useCallback, useMemo } from 'react';
import { 
  Button, 
  Input, 
  FormField, 
  Checkbox, 
  Card, 
  Badge,
  DataTable,
  type DataTableColumn,
  ViewToggle,
  gridTableOptions,
} from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { PlusIcon, DownloadIcon, EditIcon, DeleteIcon, CopyIcon } from '@/common/components/icons/MdiIcons';
import { useCatalogViewMode } from '@/common/hooks/useCatalogViewMode';
import { 
  useFilamentTypesPaged,
  useCreateFilamentType, 
  useUpdateFilamentType, 
  useDeleteFilamentType,
  useImportFilamentTypesFromSpoolman,
} from '@/common/hooks/useApi';
import type { FilamentTypeDto, CreateFilamentTypeRequest, UpdateFilamentTypeRequest, TempTargets } from '@/types/api';

const PAGE_SIZE = 50;


/**
 * Card display for filament type - uses consistent Card styling with other catalog tabs
 */
interface FilamentTypeCardProps {
  filament: FilamentTypeDto;
  onEdit: (filament: FilamentTypeDto) => void;
  onClone: (filament: FilamentTypeDto) => void;
  onDelete: (filament: FilamentTypeDto) => void;
  isDeleting?: boolean;
}

function FilamentTypeCard({ filament, onEdit, onClone, onDelete, isDeleting }: FilamentTypeCardProps) {
  return (
    <Card className="h-full flex flex-col">
      <div className="p-4 flex-1">
        {/* Header: Name and Actions */}
        <div className="flex justify-between items-start">
          <div className="flex-1 min-w-0">
            <h3 className="text-lg font-semibold text-gray-900 dark:text-white truncate">
              {filament.name}
            </h3>
          </div>

          {/* Action Buttons */}
          <div className="flex gap-1 ml-2 shrink-0">
            <Button
              variant="subtle"
              size="sm"
              onClick={() => onEdit(filament)}
              disabled={isDeleting}
              aria-label={`Edit ${filament.name}`}
            >
              <svg
                className="w-4 h-4"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={2}
                  d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z"
                />
              </svg>
            </Button>
            <Button
              variant="subtle"
              size="sm"
              onClick={() => onClone(filament)}
              disabled={isDeleting}
              aria-label={`Clone ${filament.name}`}
              title={`Clone ${filament.name}`}
            >
              <svg
                className="w-4 h-4"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={2}
                  d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z"
                />
              </svg>
            </Button>
            <Button
              variant="subtle"
              size="sm"
              onClick={() => onDelete(filament)}
              disabled={isDeleting}
              aria-label={`Delete ${filament.name}`}
              className="text-red-600 hover:text-red-700 hover:bg-red-50 dark:text-red-400 dark:hover:text-red-300 dark:hover:bg-red-900/20"
            >
              <svg
                className="w-4 h-4"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={2}
                  d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
                />
              </svg>
            </Button>
          </div>
        </div>

        {/* Temperature badges */}
        <div className="flex flex-wrap gap-2 mt-2">
          {filament.defaultTemperatures?.hotend && (
            <Badge variant="default" size="sm">
              Hotend {filament.defaultTemperatures.hotend}°C
            </Badge>
          )}
          {filament.defaultTemperatures?.bed && (
            <Badge variant="default" size="sm">
              Bed {filament.defaultTemperatures.bed}°C
            </Badge>
          )}
        </div>

        {/* Price & Density */}
        <div className="flex flex-wrap gap-2 mt-2">
          {filament.defaultPricePerKg != null && (
            <Badge variant="default" size="sm">
              ${filament.defaultPricePerKg}/kg
            </Badge>
          )}
          {filament.defaultDensity != null && (
            <Badge variant="default" size="sm">
              {filament.defaultDensity} g/cm³
            </Badge>
          )}
        </div>

        {/* Property badges */}
        <div className="flex flex-wrap gap-2 mt-2">
          {filament.isAbrasive && (
            <Badge variant="warning" size="sm">
              Abrasive
            </Badge>
          )}
          {filament.needsEnclosure && (
            <Badge variant="info" size="sm">
              Needs Enclosure
            </Badge>
          )}
          {!filament.isAbrasive && !filament.needsEnclosure && (
            <span className="text-sm text-gray-500 dark:text-gray-400">Standard</span>
          )}
        </div>
      </div>
    </Card>
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
  defaultPricePerKg: string;
  defaultDensity: string;
}

const emptyForm: FilamentFormState = {
  name: '',
  hotendTemp: '',
  bedTemp: '',
  isAbrasive: false,
  needsEnclosure: false,
  defaultPricePerKg: '',
  defaultDensity: '',
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
  // Pagination & search state
  const [page, setPage] = useState(1);
  const [searchInput, setSearchInput] = useState('');
  const [search, setSearch] = useState<string | undefined>(undefined);

  // Data queries — paged
  const { data: pagedResult, isLoading, isError } = useFilamentTypesPaged(page, PAGE_SIZE, search);

  // Extract items and paging info from response
  const filaments = useMemo(() => pagedResult?.items ?? [], [pagedResult]);
  const totalCount = pagedResult?.totalCount ?? filaments.length;
  const totalPages = pagedResult?.totalPages ?? 1;

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

  // View toggle state (grid vs table) - persisted per tab
  const [view, setView] = useCatalogViewMode('filaments');

  // Define columns for DataTable with built-in sorting
  const columns = useMemo<DataTableColumn<FilamentTypeDto>[]>(() => [
    {
      key: 'name',
      header: 'Name',
      sortable: true,
      sort: (a, b) => a.name.localeCompare(b.name),
      render: (item) => <span className="font-medium">{item.name}</span>,
    },
    {
      key: 'hotendTemp',
      header: 'Hotend Temp',
      sortable: true,
      sort: (a, b) => (a.defaultTemperatures?.hotend ?? 0) - (b.defaultTemperatures?.hotend ?? 0),
      render: (item) => item.defaultTemperatures?.hotend != null 
        ? `${item.defaultTemperatures.hotend}°C` 
        : '—',
    },
    {
      key: 'bedTemp',
      header: 'Bed Temp',
      sortable: true,
      sort: (a, b) => (a.defaultTemperatures?.bed ?? 0) - (b.defaultTemperatures?.bed ?? 0),
      render: (item) => item.defaultTemperatures?.bed != null 
        ? `${item.defaultTemperatures.bed}°C` 
        : '—',
    },
    {
      key: 'properties',
      header: 'Properties',
      sortable: true,
      sort: (a, b) => {
        const aProps = (a.isAbrasive ? 1 : 0) + (a.needsEnclosure ? 1 : 0);
        const bProps = (b.isAbrasive ? 1 : 0) + (b.needsEnclosure ? 1 : 0);
        return aProps - bProps;
      },
      render: (item) => (
        <div className="flex flex-wrap gap-1">
          {item.isAbrasive && <Badge variant="warning" size="sm">Abrasive</Badge>}
          {item.needsEnclosure && <Badge variant="info" size="sm">Needs Enclosure</Badge>}
          {!item.isAbrasive && !item.needsEnclosure && (
            <span className="text-sm text-pf-text-muted">Standard</span>
          )}
        </div>
      ),
    },
    {
      key: 'defaultPricePerKg',
      header: 'Price ($/kg)',
      sortable: true,
      sort: (a, b) => (a.defaultPricePerKg ?? 0) - (b.defaultPricePerKg ?? 0),
      render: (item) => item.defaultPricePerKg != null
        ? `$${item.defaultPricePerKg}`
        : '—',
    },
    {
      key: 'defaultDensity',
      header: 'Density (g/cm³)',
      sortable: true,
      sort: (a, b) => (a.defaultDensity ?? 0) - (b.defaultDensity ?? 0),
      render: (item) => item.defaultDensity != null
        ? `${item.defaultDensity}`
        : '—',
    },
  ], []);

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
      defaultPricePerKg: filament.defaultPricePerKg?.toString() ?? '',
      defaultDensity: filament.defaultDensity?.toString() ?? '',
    });
    setFormErrors({});
    setEditingFilament(filament);
  }, []);

  // Clone an existing filament type
  const handleCloneClick = useCallback((filament: FilamentTypeDto) => {
    setFormState({
      name: `${filament.name} - Copy`,
      hotendTemp: filament.defaultTemperatures?.hotend?.toString() ?? '',
      bedTemp: filament.defaultTemperatures?.bed?.toString() ?? '',
      isAbrasive: filament.isAbrasive,
      needsEnclosure: filament.needsEnclosure,
      defaultPricePerKg: filament.defaultPricePerKg?.toString() ?? '',
      defaultDensity: filament.defaultDensity?.toString() ?? '',
    });
    setFormErrors({});
    setIsAddModalOpen(true);
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
    if (formState.defaultPricePerKg && (isNaN(Number(formState.defaultPricePerKg)) || Number(formState.defaultPricePerKg) < 0)) {
      errors.defaultPricePerKg = 'Price must be a non-negative number';
    }
    if (formState.defaultDensity && (isNaN(Number(formState.defaultDensity)) || Number(formState.defaultDensity) < 0)) {
      errors.defaultDensity = 'Density must be a non-negative number';
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
      defaultPricePerKg: formState.defaultPricePerKg !== '' ? Number(formState.defaultPricePerKg) : undefined,
      defaultDensity: formState.defaultDensity !== '' ? Number(formState.defaultDensity) : undefined,
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
      defaultPricePerKg: formState.defaultPricePerKg !== '' ? Number(formState.defaultPricePerKg) : undefined,
      defaultDensity: formState.defaultDensity !== '' ? Number(formState.defaultDensity) : undefined,
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
    } catch {
      // Error handled by mutation
    }
  }, [importMutation]);

  // Handle form field changes
  const handleFieldChange = useCallback((field: keyof FilamentFormState, value: string | boolean) => {
    setFormState(prev => ({ ...prev, [field]: value }));
    setFormErrors(prev => ({ ...prev, [field]: undefined }));
  }, []);

  // Handle search submission
  const handleSearch = useCallback(() => {
    setSearch(searchInput.trim() || undefined);
    setPage(1);
  }, [searchInput]);

  // Render loading state
  if (isLoading && filaments.length === 0) {
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

  return (
    <div className="space-y-4">
      {/* Header with action buttons */}
      <div className="flex justify-between items-center flex-wrap gap-2">
        <h2 className="text-lg font-semibold text-pf-text-primary">
          Filament Types ({totalCount})
        </h2>
        <div className="flex items-center gap-2">
          <form
            className="flex gap-1"
            onSubmit={(e) => { e.preventDefault(); handleSearch(); }}
          >
            <Input
              value={searchInput}
              onChange={(e) => setSearchInput(e.target.value)}
              placeholder="Search by name…"
              className="w-44"
            />
            {search && (
              <Button
                type="button"
                variant="subtle"
                size="sm"
                onClick={() => { setSearchInput(''); setSearch(undefined); setPage(1); }}
                title="Clear search"
              >
                ✕
              </Button>
            )}
          </form>
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
          <ViewToggle value={view} onChange={setView} options={gridTableOptions} />
        </div>
      </div>

      {/* Grid or Table view of filament cards */}
      {filaments.length === 0 ? (
        <div className="text-center py-12 text-pf-text-secondary">
          <p>No filament types defined yet.</p>
          <p className="mt-2">Click "Add" to create your first one, or "Import from Spoolman" to import existing types.</p>
        </div>
      ) : view === 'grid' ? (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
          {filaments.map(filament => (
            <FilamentTypeCard
              key={filament.id}
              filament={filament}
              onEdit={handleEditClick}
              onClone={handleCloneClick}
              onDelete={handleDeleteClick}
              isDeleting={deleteMutation.isPending && deletingFilament?.id === filament.id}
            />
          ))}
        </div>
      ) : (
        <DataTable
          data={filaments}
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
                disabled={deleteMutation.isPending && deletingFilament?.id === item.id}
              >
                <DeleteIcon className="w-4 h-4 text-red-500" />
              </Button>
            </div>
          )}
        />
      )}

      {/* Pagination */}
      {totalPages > 1 && (
        <div className="flex items-center justify-between py-2">
          <div className="text-sm text-pf-text-secondary">
            Page {page} of {totalPages} &middot; {totalCount} total
          </div>
          <div className="flex gap-2">
            <Button
              onClick={() => setPage(p => Math.max(1, p - 1))}
              disabled={page <= 1 || isLoading}
              variant="secondary"
              size="sm"
            >
              ← Previous
            </Button>
            <Button
              onClick={() => setPage(p => p + 1)}
              disabled={page >= totalPages || isLoading}
              variant="secondary"
              size="sm"
            >
              Next →
            </Button>
          </div>
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

      <div className="grid grid-cols-2 gap-4">
        <FormField
          label="Default Price ($/kg)"
          error={formErrors.defaultPricePerKg}
        >
          <Input
            type="number"
            step="0.01"
            min="0"
            value={formState.defaultPricePerKg}
            onChange={(e) => onFieldChange('defaultPricePerKg', e.target.value)}
            placeholder="e.g., 16"
          />
        </FormField>

        <FormField
          label="Default Density (g/cm³)"
          error={formErrors.defaultDensity}
        >
          <Input
            type="number"
            step="0.01"
            min="0"
            value={formState.defaultDensity}
            onChange={(e) => onFieldChange('defaultDensity', e.target.value)}
            placeholder="e.g., 1.24"
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
