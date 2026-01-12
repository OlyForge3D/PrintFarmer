import { useState, useEffect } from 'react';
import { apiClient } from '@/services/api';
import { CloseIcon, EditIcon, DeleteIcon, SaveIcon, DatabaseIcon, ImageIcon, PlusIcon } from '@/common/components/icons/MdiIcons';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import type { ManufacturerDto, PrinterModelDto, MotionTypeString } from '@/types/api';
import { EditModelModal } from '@/features/models3d/components/EditModelModal';
import { PageTemplate } from '@/common/components/PageTemplate';
import { assetService } from '@/services/assetService';
import { Card } from '@/common/components/ui/Card';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { Alert } from '@/common/components/ui/Alert';
import { MasterDetailLayout } from '@/common/components/layout/MasterDetailLayout';
import { useKeyboardShortcuts } from '@/common/hooks/useKeyboardShortcuts';

export function CatalogPage() {
  const [manufacturers, setManufacturers] = useState<ManufacturerDto[]>([]);
  const [models, setModels] = useState<PrinterModelDto[]>([]);
  const [selectedManufacturer, setSelectedManufacturer] = useState<ManufacturerDto | null>(null);
  const [newManufacturer, setNewManufacturer] = useState('');
  const [editingManufacturer, setEditingManufacturer] = useState<{ id: string; name: string } | null>(null);
  const [editingModel, setEditingModel] = useState<{ id: string; name: string } | null>(null);
  const [editModelModalOpen, setEditModelModalOpen] = useState(false);
  const [modelToEdit, setModelToEdit] = useState<PrinterModelDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showDeleteConfirmation, setShowDeleteConfirmation] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<{ type: 'manufacturer' | 'model'; id: string; name: string } | null>(null);

  useEffect(() => {
    loadData();
  }, []);

  // Keyboard shortcuts for catalog actions
  useKeyboardShortcuts([
    {
      key: 'n',
      handler: () => {
        // Focus on the manufacturer input field
        const input = document.querySelector('[placeholder="Manufacturer name"]') as HTMLInputElement;
        input?.focus();
      },
      description: 'Add new manufacturer'
    },
    {
      key: 'm',
      handler: () => {
        if (selectedManufacturer) {
          handleAddModelClick();
        }
      },
      description: 'Add new model (requires manufacturer selected)'
    }
  ]);

  const loadData = async () => {
    try {
      setLoading(true);
      const [manufacturersData, modelsData] = await Promise.all([
        apiClient.getManufacturers(),
        apiClient.getModels()
      ]);
      setManufacturers(manufacturersData);
      setModels(modelsData);
      setError(null);
    } catch (err) {
      setError('Failed to load catalog data');
      console.error('Error loading catalog:', err);
    } finally {
      setLoading(false);
    }
  };

  const getModelCount = (manufacturerId: string): number => {
    return models.filter(m => m.manufacturerId === manufacturerId).length;
  };

  const getFilteredModels = (): PrinterModelDto[] => {
    if (!selectedManufacturer) return models;
    return models.filter(m => m.manufacturerId === selectedManufacturer.id);
  };

  const getMotionTypeDisplayName = (type?: MotionTypeString): string => {
    switch (type) {
      case 'Cartesian':
        return 'Cartesian';
      case 'CoreXY':
        return 'CoreXY';
      case 'Delta':
        return 'Delta';
      case 'Unknown':
        return 'Unknown';
      default:
        return 'Unknown';
    }
  };

  const getCoverImageUrl = (manufacturerId: string, modelId: string): string | undefined => {
    const manufacturer = manufacturers.find(m => m.id === manufacturerId);
    if (!manufacturer) return undefined;
    return assetService.getCoverImageUrl(manufacturer.name, modelId);
  };

  const addManufacturer = async () => {
    if (!newManufacturer.trim()) return;

    try {
      const response = await apiClient.createManufacturer(newManufacturer.trim());
      setManufacturers([...manufacturers, response]);
      setNewManufacturer('');
    } catch (err) {
      setError('Failed to add manufacturer');
      console.error('Error adding manufacturer:', err);
    }
  };

  const updateManufacturer = async (id: string, name: string) => {
    try {
      await apiClient.updateManufacturer(id, name);
      setManufacturers(manufacturers.map(m => m.id === id ? { ...m, name } : m));
      setEditingManufacturer(null);
    } catch (err) {
      setError('Failed to update manufacturer');
      console.error('Error updating manufacturer:', err);
    }
  };

  const updateModel = async (id: string, name: string) => {
    try {
      await apiClient.updateModelName(id, name);
      await loadData();
      setEditingModel(null);
    } catch (err) {
      console.error('Error updating model:', err);
    }
  };

  const handleAddModelClick = () => {
    if (!selectedManufacturer) return;

    // Create a temporary model object for adding a new model
    const tempModel: PrinterModelDto = {
      id: `temp-${Date.now()}` as string, // Temporary ID for add mode
      name: '',
      manufacturerId: selectedManufacturer.id,
      motionType: undefined,
      maxX: undefined,
      maxY: undefined,
      maxZ: undefined,
      defaultBackend: undefined,
      supportedFilamentTypes: [],
    };
    
    setModelToEdit(tempModel);
    setEditModelModalOpen(true);
  };

  const openEditModal = (model: PrinterModelDto) => {
    setModelToEdit(model);
    setEditModelModalOpen(true);
  };

  const closeEditModal = () => {
    setModelToEdit(null);
    setEditModelModalOpen(false);
  };

  const deleteManufacturer = (id: string, name: string) => {
    setDeleteTarget({ type: 'manufacturer', id, name });
    setShowDeleteConfirmation(true);
  };

  const deleteModel = (id: string, name: string) => {
    setDeleteTarget({ type: 'model', id, name });
    setShowDeleteConfirmation(true);
  };

  const handleConfirmDelete = async () => {
    if (!deleteTarget) return;

    try {
      if (deleteTarget.type === 'manufacturer') {
        await apiClient.deleteManufacturer(deleteTarget.id);
        setManufacturers(manufacturers.filter(m => m.id !== deleteTarget.id));
        setModels(models.filter(m => m.manufacturerId !== deleteTarget.id));
        if (selectedManufacturer?.id === deleteTarget.id) {
          setSelectedManufacturer(null);
        }
      } else {
        await apiClient.deleteModel(deleteTarget.id);
        setModels(models.filter(m => m.id !== deleteTarget.id));
      }
      setShowDeleteConfirmation(false);
      setDeleteTarget(null);
    } catch (err) {
      setError('Failed to delete');
      console.error('Error deleting:', err);
      setShowDeleteConfirmation(false);
      setDeleteTarget(null);
    }
  };
  if (loading) {
    return (
      <PageTemplate
        title="Catalog"
        subtitle="Manage printer manufacturers, models, and filament types"
        icon={DatabaseIcon}
      >
        <div className="flex items-center justify-center h-64">
          <div className="text-pf-text-secondary">Loading catalog...</div>
        </div>
      </PageTemplate>
    );
  }

  // Master panel - Manufacturers list
  const masterPanel = (
    <Card>
      <Card.Header>
        <div className="flex justify-between items-center">
          <h2 className="text-xl font-semibold">Manufacturers</h2>
          <div className="flex gap-2">
            <Input
              value={newManufacturer}
              onChange={(e) => setNewManufacturer(e.target.value)}
              onKeyPress={(e) => e.key === 'Enter' && addManufacturer()}
              placeholder="Manufacturer name"
              className="text-sm"
            />
            <Button
              onClick={addManufacturer}
              disabled={!newManufacturer.trim()}
              size="sm"
            >
              <PlusIcon className="h-4 w-4" />
            </Button>
          </div>
        </div>
      </Card.Header>
      <Card.Body>
        <div className="space-y-2 max-h-96 overflow-y-auto">
          {manufacturers.map((manufacturer) => (
            <div
              key={manufacturer.id}
              className={`p-3 border border-pf-border rounded cursor-pointer hover:bg-pf-bg-2 transition-colors ${selectedManufacturer?.id === manufacturer.id ? 'bg-blue-900/30 border-blue-600' : ''
                }`}
              onClick={() => setSelectedManufacturer(manufacturer)}
            >
              <div className="flex justify-between items-center">
                <div>
                  {editingManufacturer?.id === manufacturer.id ? (
                    <div className="flex gap-2">
                      <Input
                        value={editingManufacturer.name}
                        onChange={(e) => setEditingManufacturer({ ...editingManufacturer, name: e.target.value })}
                        className="text-sm"
                        autoFocus
                        placeholder="Edit manufacturer name"
                      />
                      <Button
                        onClick={(e) => {
                          e.stopPropagation();
                          updateManufacturer(editingManufacturer.id, editingManufacturer.name);
                        }}
                        variant="subtle"
                        size="sm"
                        title="Save manufacturer name"
                      >
                        <SaveIcon className="h-4 w-4 text-green-400" />
                      </Button>
                      <Button
                        onClick={(e) => {
                          e.stopPropagation();
                          setEditingManufacturer(null);
                        }}
                        variant="subtle"
                        size="sm"
                        title="Cancel edit"
                      >
                        <CloseIcon className="h-4 w-4 text-red-400" />
                      </Button>
                    </div>
                  ) : (
                    <>
                      <div className="font-medium text-pf-text">{manufacturer.name}</div>
                      <div className="text-sm text-pf-text-secondary">{getModelCount(manufacturer.id)} models</div>
                    </>
                  )}
                </div>
                {editingManufacturer?.id !== manufacturer.id && (
                  <div className="flex gap-1">
                    <Button
                      onClick={(e) => {
                        e.stopPropagation();
                        setEditingManufacturer({ id: manufacturer.id, name: manufacturer.name });
                      }}
                      variant="subtle"
                      size="sm"
                      title="Edit manufacturer"
                    >
                      <EditIcon className="h-4 w-4 text-blue-400" />
                    </Button>
                    <Button
                      onClick={(e) => {
                        e.stopPropagation();
                        deleteManufacturer(manufacturer.id, manufacturer.name);
                      }}
                      variant="subtle"
                      size="sm"
                      title="Delete manufacturer"
                    >
                      <DeleteIcon className="h-4 w-4 text-red-400" />
                    </Button>
                  </div>
                )}
              </div>
            </div>
          ))}
          {manufacturers.length === 0 && (
            <div className="text-center py-8 text-pf-text-secondary">
              No manufacturers found. Add your first manufacturer above.
            </div>
          )}
        </div>
      </Card.Body>
    </Card>
  );

  // Detail panel - Models list
  const detailPanel = (
    <Card>
      <Card.Header>
        <div className="flex justify-between items-center">
          <h2 className="text-xl font-semibold">
            Models {selectedManufacturer && `— ${selectedManufacturer.name}`}
          </h2>
          {selectedManufacturer && (
            <Button
              onClick={handleAddModelClick}
              size="sm"
              title="Add new model"
            >
              <PlusIcon className="h-4 w-4" />
            </Button>
          )}
        </div>
      </Card.Header>
      <Card.Body>
        <div className="space-y-2 max-h-96 overflow-y-auto">
          {selectedManufacturer ? (
            getFilteredModels().map((model) => (
              <div key={model.id} className="space-y-2">
                <div className="p-3 border border-pf-border rounded hover:bg-pf-bg-2 transition-colors">
                  <div className="flex justify-between items-center">
                    <div className="flex-1">
                      {editingModel?.id === model.id ? (
                        <div className="flex gap-2">
                          <Input
                            value={editingModel.name}
                            onChange={(e) => setEditingModel({ ...editingModel, name: e.target.value })}
                            className="text-sm"
                            autoFocus
                            placeholder="Edit model name"
                          />
                          <Button
                            onClick={() => updateModel(editingModel.id, editingModel.name)}
                            variant="subtle"
                            size="sm"
                            title="Save model name"
                          >
                            <SaveIcon className="h-4 w-4 text-green-400" />
                          </Button>
                          <Button
                            onClick={() => setEditingModel(null)}
                            variant="subtle"
                            size="sm"
                            title="Cancel edit"
                          >
                            <CloseIcon className="h-4 w-4 text-red-400" />
                          </Button>
                        </div>
                      ) : (
                        <div>
                          <div className="font-medium text-pf-text">{model.name}</div>
                          {model.motionType !== undefined && (
                            <div className="text-sm text-pf-text-secondary">
                              Type: {getMotionTypeDisplayName(model.motionType)}
                            </div>
                          )}
                          {model.supportedFilamentTypes && model.supportedFilamentTypes.length > 0 && (
                            <div className="text-sm text-pf-text-secondary mt-1">
                              Filament types: {model.supportedFilamentTypes.join(', ')}
                            </div>
                          )}
                          {(() => {
                            const coverUrl = getCoverImageUrl(selectedManufacturer!.id, model.id);
                            if (coverUrl) {
                              return (
                                <div className="mt-2 flex items-center gap-1 text-xs text-pf-text-secondary">
                                  <ImageIcon className="h-3 w-3" />
                                  <span>Cover image available</span>
                                </div>
                              );
                            }
                            return null;
                          })()}
                        </div>
                      )}
                    </div>
                    {editingModel?.id !== model.id && (
                      <div className="flex gap-1">
                        <Button
                          onClick={() => openEditModal(model)}
                          variant="subtle"
                          size="sm"
                          title="Edit model capabilities"
                        >
                          <EditIcon className="h-4 w-4 text-blue-400" />
                        </Button>
                        <Button
                          onClick={() => deleteModel(model.id, model.name)}
                          variant="subtle"
                          size="sm"
                          title="Delete model"
                        >
                          <DeleteIcon className="h-4 w-4 text-red-400" />
                        </Button>
                      </div>
                    )}
                  </div>
                </div>
              </div>
            ))
          ) : (
            <div className="text-center py-8 text-pf-text-secondary">
              Select a manufacturer to view and manage models
            </div>
          )}
          {selectedManufacturer && getFilteredModels().length === 0 && (
            <div className="text-center py-8 text-pf-text-secondary">
              No models found for {selectedManufacturer.name}. Add your first model above.
            </div>
          )}
        </div>
      </Card.Body>
    </Card>
  );

  return (
    <PageTemplate
      title="Catalog"
      subtitle="Manage printer manufacturers, models, and filament types"
      icon={DatabaseIcon}
    >
      {error && (
        <Alert type="error">{error}</Alert>
      )}

      <MasterDetailLayout
        master={masterPanel}
        detail={detailPanel}
        hasDetail={!!selectedManufacturer}
        onCloseDetail={() => setSelectedManufacturer(null)}
        detailTitle={selectedManufacturer?.name}
        masterWidth="w-80"
        breakpoint="lg"
      />

      <EditModelModal
        model={modelToEdit}
        isOpen={editModelModalOpen}
        onClose={closeEditModal}
        onSuccess={() => {
          loadData();
          closeEditModal();
        }}
      />

      <ConfirmationModal
        isOpen={showDeleteConfirmation}
        title={deleteTarget?.type === 'manufacturer' ? 'Delete Manufacturer' : 'Delete Model'}
        message={
          deleteTarget?.type === 'manufacturer'
            ? `Are you sure? This will also delete all associated models for "${deleteTarget.name}".`
            : `Are you sure you want to delete this model "${deleteTarget?.name}"?`
        }
        confirmButtonText="Delete"
        cancelButtonText="Cancel"
        isDangerous={true}
        onConfirm={handleConfirmDelete}
        onCancel={() => {
          setShowDeleteConfirmation(false);
          setDeleteTarget(null);
        }}
      />
    </PageTemplate>
  );
}
