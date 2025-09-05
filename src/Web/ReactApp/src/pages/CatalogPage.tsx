import { useState, useEffect } from 'react';
import { apiClient } from '@/services/api';
import { Plus, Edit, Trash2, Save, X } from 'lucide-react';
import type { ManufacturerDto, ModelDto } from '@/types/api';

export function CatalogPage() {
  const [manufacturers, setManufacturers] = useState<ManufacturerDto[]>([]);
  const [models, setModels] = useState<ModelDto[]>([]);
  const [selectedManufacturer, setSelectedManufacturer] = useState<ManufacturerDto | null>(null);
  const [newManufacturer, setNewManufacturer] = useState('');
  const [newModel, setNewModel] = useState('');
  const [editingManufacturer, setEditingManufacturer] = useState<{ id: string; name: string } | null>(null);
  const [editingModel, setEditingModel] = useState<{ id: string; name: string } | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    loadData();
  }, []);

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

  const getFilteredModels = (): ModelDto[] => {
    if (!selectedManufacturer) return models;
    return models.filter(m => m.manufacturerId === selectedManufacturer.id);
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

  const addModel = async () => {
    if (!newModel.trim() || !selectedManufacturer) return;
    
    try {
      const response = await apiClient.createModel({
        name: newModel.trim(),
        manufacturerId: selectedManufacturer.id
      });
      setModels([...models, response]);
      setNewModel('');
    } catch (err) {
      setError('Failed to add model');
      console.error('Error adding model:', err);
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
      await apiClient.updateModel(id, name);
      setModels(models.map(m => m.id === id ? { ...m, name } : m));
      setEditingModel(null);
    } catch (err) {
      setError('Failed to update model');
      console.error('Error updating model:', err);
    }
  };

  const deleteManufacturer = async (id: string) => {
    if (!confirm('Are you sure? This will also delete all associated models.')) return;
    
    try {
      await apiClient.deleteManufacturer(id);
      setManufacturers(manufacturers.filter(m => m.id !== id));
      setModels(models.filter(m => m.manufacturerId !== id));
      if (selectedManufacturer?.id === id) {
        setSelectedManufacturer(null);
      }
    } catch (err) {
      setError('Failed to delete manufacturer');
      console.error('Error deleting manufacturer:', err);
    }
  };

  const deleteModel = async (id: string) => {
    if (!confirm('Are you sure you want to delete this model?')) return;
    
    try {
      await apiClient.deleteModel(id);
      setModels(models.filter(m => m.id !== id));
    } catch (err) {
      setError('Failed to delete model');
      console.error('Error deleting model:', err);
    }
  };

  if (loading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-pf-text-secondary">Loading catalog...</div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex justify-between items-center">
        <h1 className="text-3xl font-bold text-pf-text-primary font-bebas uppercase">Catalog</h1>
      </div>

      {error && (
        <div className="bg-red-900/50 border border-red-700 text-red-100 px-4 py-3 rounded">
          {error}
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Manufacturers Section */}
        <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
          <div className="flex justify-between items-center mb-4">
            <h2 className="text-xl font-semibold text-pf-text-primary">Manufacturers</h2>
            <div className="flex gap-2">
              <input
                value={newManufacturer}
                onChange={(e) => setNewManufacturer(e.target.value)}
                onKeyPress={(e) => e.key === 'Enter' && addManufacturer()}
                placeholder="Manufacturer name"
                className="px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary placeholder-pf-text-secondary text-sm"
              />
              <button
                onClick={addManufacturer}
                disabled={!newManufacturer.trim()}
                className="px-3 py-2 bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed flex items-center gap-2"
              >
                <Plus className="h-4 w-4" />
                Add
              </button>
            </div>
          </div>

          <div className="space-y-2 max-h-96 overflow-y-auto">
            {manufacturers.map((manufacturer) => (
              <div
                key={manufacturer.id}
                className={`p-3 border border-pf-border rounded cursor-pointer hover:bg-pf-bg-2 transition-colors ${
                  selectedManufacturer?.id === manufacturer.id ? 'bg-blue-900/30 border-blue-600' : ''
                }`}
                onClick={() => setSelectedManufacturer(manufacturer)}
              >
                <div className="flex justify-between items-center">
                  <div>
                    {editingManufacturer?.id === manufacturer.id ? (
                      <div className="flex gap-2">
                        <input
                          value={editingManufacturer.name}
                          onChange={(e) => setEditingManufacturer({ ...editingManufacturer, name: e.target.value })}
                          className="px-2 py-1 bg-pf-bg-0 border border-pf-border rounded text-sm"
                          autoFocus
                        />
                        <button
                          onClick={(e) => {
                            e.stopPropagation();
                            updateManufacturer(editingManufacturer.id, editingManufacturer.name);
                          }}
                          className="text-green-400 hover:text-green-300"
                        >
                          <Save className="h-4 w-4" />
                        </button>
                        <button
                          onClick={(e) => {
                            e.stopPropagation();
                            setEditingManufacturer(null);
                          }}
                          className="text-red-400 hover:text-red-300"
                        >
                          <X className="h-4 w-4" />
                        </button>
                      </div>
                    ) : (
                      <>
                        <div className="font-medium text-pf-text-primary">{manufacturer.name}</div>
                        <div className="text-sm text-pf-text-secondary">{getModelCount(manufacturer.id)} models</div>
                      </>
                    )}
                  </div>
                  {editingManufacturer?.id !== manufacturer.id && (
                    <div className="flex gap-1">
                      <button
                        onClick={(e) => {
                          e.stopPropagation();
                          setEditingManufacturer({ id: manufacturer.id, name: manufacturer.name });
                        }}
                        className="text-blue-400 hover:text-blue-300"
                      >
                        <Edit className="h-4 w-4" />
                      </button>
                      <button
                        onClick={(e) => {
                          e.stopPropagation();
                          deleteManufacturer(manufacturer.id);
                        }}
                        className="text-red-400 hover:text-red-300"
                      >
                        <Trash2 className="h-4 w-4" />
                      </button>
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
        </div>

        {/* Models Section */}
        <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
          <div className="flex justify-between items-center mb-4">
            <h2 className="text-xl font-semibold text-pf-text-primary">
              Models {selectedManufacturer && `— ${selectedManufacturer.name}`}
            </h2>
            {selectedManufacturer && (
              <div className="flex gap-2">
                <input
                  value={newModel}
                  onChange={(e) => setNewModel(e.target.value)}
                  onKeyPress={(e) => e.key === 'Enter' && addModel()}
                  placeholder="Model name"
                  className="px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary placeholder-pf-text-secondary text-sm"
                />
                <button
                  onClick={addModel}
                  disabled={!newModel.trim()}
                  className="px-3 py-2 bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed flex items-center gap-2"
                >
                  <Plus className="h-4 w-4" />
                  Add
                </button>
              </div>
            )}
          </div>

          <div className="space-y-2 max-h-96 overflow-y-auto">
            {selectedManufacturer ? (
              getFilteredModels().map((model) => (
                <div
                  key={model.id}
                  className="p-3 border border-pf-border rounded hover:bg-pf-bg-2 transition-colors"
                >
                  <div className="flex justify-between items-center">
                    <div>
                      {editingModel?.id === model.id ? (
                        <div className="flex gap-2">
                          <input
                            value={editingModel.name}
                            onChange={(e) => setEditingModel({ ...editingModel, name: e.target.value })}
                            className="px-2 py-1 bg-pf-bg-0 border border-pf-border rounded text-sm"
                            autoFocus
                          />
                          <button
                            onClick={() => updateModel(editingModel.id, editingModel.name)}
                            className="text-green-400 hover:text-green-300"
                          >
                            <Save className="h-4 w-4" />
                          </button>
                          <button
                            onClick={() => setEditingModel(null)}
                            className="text-red-400 hover:text-red-300"
                          >
                            <X className="h-4 w-4" />
                          </button>
                        </div>
                      ) : (
                        <div className="font-medium text-pf-text-primary">{model.name}</div>
                      )}
                    </div>
                    {editingModel?.id !== model.id && (
                      <div className="flex gap-1">
                        <button
                          onClick={() => setEditingModel({ id: model.id, name: model.name })}
                          className="text-blue-400 hover:text-blue-300"
                        >
                          <Edit className="h-4 w-4" />
                        </button>
                        <button
                          onClick={() => deleteModel(model.id)}
                          className="text-red-400 hover:text-red-300"
                        >
                          <Trash2 className="h-4 w-4" />
                        </button>
                      </div>
                    )}
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
        </div>
      </div>
    </div>
  );
}
