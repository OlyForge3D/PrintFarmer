import React, { useState, useCallback, useMemo } from 'react';
import { 
  Button, 
  FormField, 
  Alert,
  Badge,
  DataTable,
  type DataTableColumn,
  ViewToggle,
  gridTableOptions,
} from '@/common/components/ui';
import { ManufacturerSelector } from '@/common/components/ManufacturerSelector';
import { PrinterModelCard, type PrinterModelCardData } from '@/common/components/PrinterModelCard';
import { useManufacturers, useModels } from '@/common/hooks/useApi';
import { EditModelModal } from '@/features/models3d/components/EditModelModal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { apiClient } from '@/services/api';
import { CatalogContext, type PrinterModelDto, type ManufacturerDto } from '@/types/api';
import { PlusIcon, EditIcon, DeleteIcon, CopyIcon } from '@/common/components/icons/MdiIcons';
import { useCatalogViewMode } from '@/common/hooks/useCatalogViewMode';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';

/**
 * Converts a PrinterModelDto to the card display format with manufacturer name
 */
function toCardData(model: PrinterModelDto, manufacturers: ManufacturerDto[]): PrinterModelCardData {
  const manufacturer = manufacturers.find(m => m.id === model.manufacturerId);
  return {
    ...model,
    manufacturerName: manufacturer?.name,
  };
}

/**
 * PrinterModelsCatalog - Catalog tab for managing printer models
 * 
 * Features:
 * - Card grid display of all printer models with rich information
 * - Filter by manufacturer using grouped dropdown selector
 * - Add new printer model with comprehensive modal form
 * - Edit existing printer models
 * - Clone printer models to create variations
 * - Delete printer models with confirmation
 * 
 * Displays:
 * - Motion type (Cartesian, CoreXY, Delta)
 * - Build volume dimensions
 * - Capability badges (heated bed, enclosure, multi-material, auto-leveling)
 * - Max temperatures and print speed
 * - Supported filament types
 * - Toolhead configurations
 */
export function PrinterModelsCatalog() {
  const queryClient = useQueryClient();

  // Data queries
  const { data: manufacturers = [], isLoading: manufacturersLoading } = useManufacturers();
  const { data: models = [], isLoading: modelsLoading, isError } = useModels();

  // Filter state
  const [selectedManufacturerId, setSelectedManufacturerId] = useState<string | undefined>(undefined);

  // Modal state
  const [editModelModalOpen, setEditModelModalOpen] = useState(false);
  const [modelToEdit, setModelToEdit] = useState<PrinterModelDto | null>(null);
  const [isCloneMode, setIsCloneMode] = useState(false);
  const [deletingModel, setDeletingModel] = useState<PrinterModelCardData | null>(null);

  // View toggle state (grid vs table) - persisted per tab
  const [view, setView] = useCatalogViewMode('printer-models');

  // Define columns for DataTable with built-in sorting
  const columns = useMemo<DataTableColumn<PrinterModelCardData>[]>(() => [
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
      key: 'motionType',
      header: 'Motion Type',
      sortable: true,
      sort: (a, b) => (a.motionType ?? '').localeCompare(b.motionType ?? ''),
      render: (item) => item.motionType ?? '—',
    },
    {
      key: 'buildVolume',
      header: 'Build Volume',
      sortable: true,
      sort: (a, b) => ((a.maxX ?? 0) * (a.maxY ?? 0) * (a.maxZ ?? 0)) - ((b.maxX ?? 0) * (b.maxY ?? 0) * (b.maxZ ?? 0)),
      render: (item) => item.maxX && item.maxY && item.maxZ 
        ? `${item.maxX}×${item.maxY}×${item.maxZ} mm` 
        : '—',
    },
    {
      key: 'features',
      header: 'Features',
      sortable: true,
      sort: (a, b) => {
        const aFeatures = (a.hasHeatedBed ? 1 : 0) + (a.hasEnclosure ? 1 : 0) + (a.multiMaterial ? 1 : 0) + (a.supportsAutoLeveling ? 1 : 0);
        const bFeatures = (b.hasHeatedBed ? 1 : 0) + (b.hasEnclosure ? 1 : 0) + (b.multiMaterial ? 1 : 0) + (b.supportsAutoLeveling ? 1 : 0);
        return aFeatures - bFeatures;
      },
      render: (item) => (
        <div className="flex flex-wrap gap-1">
          {item.hasHeatedBed && <Badge variant="info" size="sm">Heated Bed</Badge>}
          {item.hasEnclosure && <Badge variant="info" size="sm">Enclosure</Badge>}
          {item.multiMaterial && <Badge variant="success" size="sm">Multi-Material</Badge>}
          {item.supportsAutoLeveling && <Badge variant="success" size="sm">Auto-Level</Badge>}
          {!item.hasHeatedBed && !item.hasEnclosure && !item.multiMaterial && !item.supportsAutoLeveling && (
            <span className="text-sm text-pf-text-muted">Basic</span>
          )}
        </div>
      ),
    },
  ], []);

  // Delete mutation
  const deleteMutation = useMutation({
    mutationFn: (id: string) => apiClient.deleteModel(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['models'] });
      toast.success('Printer model deleted');
    },
    onError: (err) => {
      toast.error(err instanceof Error ? err.message : 'Failed to delete model');
    },
  });

  // Filter models by selected manufacturer
  const filteredModels = useMemo(() => {
    if (!selectedManufacturerId) {
      return models;
    }
    return models.filter(m => m.manufacturerId === selectedManufacturerId);
  }, [models, selectedManufacturerId]);

  // Convert to card data with manufacturer names
  const cards = useMemo(() => {
    return filteredModels.map(m => toCardData(m, manufacturers));
  }, [filteredModels, manufacturers]);

  // Handle add new model
  const handleAddClick = useCallback(() => {
    // Create a temporary model object for adding a new model
    // manufacturerId can be empty - user will select in modal
    const tempModel: PrinterModelDto = {
      id: `temp-${Date.now()}`,
      name: '',
      manufacturerId: selectedManufacturerId || '',
      motionType: undefined,
      maxX: undefined,
      maxY: undefined,
      maxZ: undefined,
      defaultBackend: undefined,
      supportedFilamentTypes: [],
    };

    setModelToEdit(tempModel);
    setIsCloneMode(false);
    setEditModelModalOpen(true);
  }, [selectedManufacturerId]);

  // Handle edit model
  const handleEditClick = useCallback((card: PrinterModelCardData) => {
    const model = models.find(m => m.id === card.id);
    if (!model) return;

    setModelToEdit(model);
    setIsCloneMode(false);
    setEditModelModalOpen(true);
  }, [models]);

  // Handle clone model
  const handleCloneClick = useCallback((card: PrinterModelCardData) => {
    const model = models.find(m => m.id === card.id);
    if (!model) return;

    const clonedModel: PrinterModelDto = {
      ...model,
      id: `temp-${Date.now()}`,
      name: `${model.name} - Copy`,
    };

    setModelToEdit(clonedModel);
    setIsCloneMode(true);
    setEditModelModalOpen(true);
  }, [models]);

  // Handle delete click
  const handleDeleteClick = useCallback((card: PrinterModelCardData) => {
    setDeletingModel(card);
  }, []);

  // Handle delete confirm
  const handleConfirmDelete = useCallback(async () => {
    if (!deletingModel) return;

    try {
      await deleteMutation.mutateAsync(deletingModel.id);
      setDeletingModel(null);
    } catch {
      // Error handled by mutation
    }
  }, [deletingModel, deleteMutation]);

  // Close modals
  const handleCloseEditModal = useCallback(() => {
    setEditModelModalOpen(false);
    setModelToEdit(null);
    setIsCloneMode(false);
  }, []);

  const handleCloseDeleteModal = useCallback(() => {
    setDeletingModel(null);
  }, []);

  // Handle manufacturer selection
  const handleManufacturerChange = useCallback((manufacturerId: string | undefined) => {
    setSelectedManufacturerId(manufacturerId);
  }, []);

  // Handle edit modal success - modal handles cache refresh
  const handleEditSuccess = useCallback(() => {
    handleCloseEditModal();
  }, [handleCloseEditModal]);

  // Render loading state
  const isLoading = manufacturersLoading || modelsLoading;
  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-pf-text-secondary">Loading printer models catalog...</div>
      </div>
    );
  }

  // Render error state
  if (isError) {
    return (
      <div className="flex items-center justify-center h-64">
        <Alert type="error">Failed to load printer models</Alert>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {/* Header with Filter and Add button */}
      <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4">
        <div className="flex items-center gap-4 w-full sm:w-auto">
          <FormField label="Filter by Manufacturer" className="flex-1 sm:flex-initial sm:min-w-[280px]">
            <ManufacturerSelector
              value={selectedManufacturerId}
              onChange={handleManufacturerChange}
              context={CatalogContext.Printers}
              placeholder="All Manufacturers"
              ariaLabel="Filter printer models by manufacturer"
            />
          </FormField>
        </div>

        <div className="flex items-center gap-2">
          <Button
            onClick={handleAddClick}
            title="Add new printer model"
            size="sm"
            iconLeft={<PlusIcon className="h-4 w-4 mr-2" />}
          >
            Add
          </Button>
          <ViewToggle value={view} onChange={setView} options={gridTableOptions} />
        </div>
      </div>

      {/* Helpful hint when no manufacturer selected */}
      {!selectedManufacturerId && (
        <p className="text-sm text-pf-text-secondary">
          Showing all {models.length} printer models. Select a manufacturer to filter.
        </p>
      )}

      {/* Grid or Table view of printer model cards */}
      {cards.length === 0 ? (
        <div className="text-center py-12 text-pf-text-secondary">
          {selectedManufacturerId ? (
            <div>
              <p>No printer models found for this manufacturer.</p>
              <p className="mt-2">Click "Add Printer Model" to create one.</p>
            </div>
          ) : (
            <p>No printer models in the catalog yet.</p>
          )}
        </div>
      ) : view === 'grid' ? (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
          {cards.map(card => (
            <PrinterModelCard
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
                <DeleteIcon className="w-4 h-4 text-red-500" />
              </Button>
            </div>
          )}
        />
      )}

      {/* Edit/Add Printer Model Modal */}
      <EditModelModal
        model={modelToEdit}
        isOpen={editModelModalOpen}
        onClose={handleCloseEditModal}
        onSuccess={handleEditSuccess}
        isCloneMode={isCloneMode}
      />

      {/* Delete Confirmation Modal */}
      <ConfirmationModal
        isOpen={!!deletingModel}
        title="Delete Printer Model"
        message={`Are you sure you want to delete "${deletingModel?.name}"? This action cannot be undone.`}
        confirmButtonText="Delete"
        cancelButtonText="Cancel"
        isDangerous={true}
        onConfirm={handleConfirmDelete}
        onCancel={handleCloseDeleteModal}
      />
    </div>
  );
}

export default PrinterModelsCatalog;
