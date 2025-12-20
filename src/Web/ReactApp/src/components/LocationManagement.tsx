import React, { useState, useEffect } from 'react';
import { Location, CreateLocationRequest, UpdateLocationRequest, locationService } from '../services/locationService';

/**
 * LocationManagement Component - Manage printer locations
 * 
 * For assigning printers to locations via drag and drop, see PrinterLocationDragDrop component:
 * import PrinterLocationDragDrop from './PrinterLocationDragDrop';
 * <PrinterLocationDragDrop />
 */
export const LocationManagement: React.FC = () => {
  const [locations, setLocations] = useState<Location[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [formData, setFormData] = useState<CreateLocationRequest>({
    name: '',
    description: '',
  });

  // Load locations on component mount
  useEffect(() => {
    loadLocations();
  }, []);

  const loadLocations = async () => {
    try {
      setLoading(true);
      setError(null);
      const data = await locationService.getAllLocations();
      setLocations(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load locations');
    } finally {
      setLoading(false);
    }
  };

  const handleCreateOrUpdate = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!formData.name.trim()) {
      setError('Location name is required');
      return;
    }

    try {
      setLoading(true);
      setError(null);

      if (editingId) {
        // Update existing location
        await locationService.updateLocation(editingId, formData as UpdateLocationRequest);
      } else {
        // Create new location
        await locationService.createLocation(formData);
      }

      // Reset form and reload
      setFormData({ name: '', description: '' });
      setEditingId(null);
      setShowForm(false);
      await loadLocations();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save location');
    } finally {
      setLoading(false);
    }
  };

  const handleEdit = (location: Location) => {
    setEditingId(location.id);
    setFormData({
      name: location.name,
      description: location.description,
    });
    setShowForm(true);
  };

  const handleDelete = async (id: string) => {
    if (!window.confirm('Are you sure you want to delete this location?')) {
      return;
    }

    try {
      setLoading(true);
      setError(null);
      await locationService.deleteLocation(id);
      await loadLocations();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to delete location');
    } finally {
      setLoading(false);
    }
  };

  const handleCancel = () => {
    setShowForm(false);
    setEditingId(null);
    setFormData({ name: '', description: '' });
  };

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex justify-between items-center">
        <h1 className="text-3xl font-bold">Printer Locations</h1>
        {!showForm && (
          <button
            onClick={() => setShowForm(true)}
            className="font-bold py-2 px-4 rounded transition-opacity"
            style={{
              backgroundColor: 'var(--pf-accent-bg)',
              color: 'white',
            }}
          >
            Add Location
          </button>
        )}
      </div>

      {/* Error message */}
      {error && (
        <div className="px-4 py-3 rounded" style={{
          backgroundColor: 'var(--pf-error-bg)',
          border: '1px solid var(--pf-error)',
          color: 'var(--pf-error)',
        }}>
          {error}
        </div>
      )}

      {/* Create/Edit Form */}
      {showForm && (
        <div className="shadow rounded-lg p-6" style={{
          backgroundColor: 'var(--pf-bg-1)',
          border: '1px solid var(--pf-border)',
        }}>
          <h2 className="text-xl font-semibold mb-4" style={{ color: 'var(--pf-text-primary)' }}>
            {editingId ? 'Edit Location' : 'Create New Location'}
          </h2>
          <form onSubmit={handleCreateOrUpdate} className="space-y-4">
            {/* Name field */}
            <div>
              <label htmlFor="name" className="block text-sm font-medium" style={{ color: 'var(--pf-text-primary)' }}>
                Location Name *
              </label>
              <input
                id="name"
                type="text"
                value={formData.name}
                onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                placeholder="e.g., RACK1-01"
                className="mt-1 block w-full rounded-md shadow-sm py-2 px-3 focus:outline-none"
                style={{
                  backgroundColor: 'var(--pf-bg-0)',
                  color: 'var(--pf-text-primary)',
                  border: '1px solid var(--pf-border)',
                }}
                required
              />
            </div>

            {/* Description field */}
            <div>
              <label htmlFor="description" className="block text-sm font-medium" style={{ color: 'var(--pf-text-primary)' }}>
                Description
              </label>
              <textarea
                id="description"
                value={formData.description || ''}
                onChange={(e) => setFormData({ ...formData, description: e.target.value })}
                placeholder="e.g., Main warehouse rack, first column"
                rows={3}
                className="mt-1 block w-full rounded-md shadow-sm py-2 px-3 focus:outline-none"
                style={{
                  backgroundColor: 'var(--pf-bg-0)',
                  color: 'var(--pf-text-primary)',
                  border: '1px solid var(--pf-border)',
                }}
              />
            </div>

            {/* Form actions */}
            <div className="flex gap-3 pt-4">
              <button
                type="submit"
                disabled={loading}
                className="font-bold py-2 px-4 rounded transition-opacity disabled:opacity-50"
                style={{
                  backgroundColor: 'var(--pf-accent-bg)',
                  color: 'white',
                }}
              >
                {loading ? 'Saving...' : editingId ? 'Update' : 'Create'}
              </button>
              <button
                type="button"
                onClick={handleCancel}
                className="font-bold py-2 px-4 rounded transition-opacity"
                style={{
                  backgroundColor: 'var(--pf-border-medium)',
                  color: 'var(--pf-text-primary)',
                }}
              >
                Cancel
              </button>
            </div>
          </form>
        </div>
      )}

      {/* Locations List */}
      <div className="shadow overflow-hidden rounded-lg" style={{
        backgroundColor: 'var(--pf-bg-1)',
        border: '1px solid var(--pf-border)',
      }}>
        {loading && !showForm ? (
          <div className="p-6 text-center" style={{ color: 'var(--pf-text-primary)' }}>Loading locations...</div>
        ) : locations.length === 0 ? (
          <div className="p-6 text-center" style={{ color: 'var(--pf-text-secondary)' }}>
            No locations found. Create one to get started!
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full" style={{ borderCollapse: 'collapse' }}>
              <thead>
                <tr style={{ borderBottom: '1px solid var(--pf-border)' }}>
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider" style={{
                    backgroundColor: 'var(--pf-bg-0)',
                    color: 'var(--pf-text-secondary)',
                  }}>
                    Name
                  </th>
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider" style={{
                    backgroundColor: 'var(--pf-bg-0)',
                    color: 'var(--pf-text-secondary)',
                  }}>
                    Description
                  </th>
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider" style={{
                    backgroundColor: 'var(--pf-bg-0)',
                    color: 'var(--pf-text-secondary)',
                  }}>
                    Printers
                  </th>
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider" style={{
                    backgroundColor: 'var(--pf-bg-0)',
                    color: 'var(--pf-text-secondary)',
                  }}>
                    Created
                  </th>
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider" style={{
                    backgroundColor: 'var(--pf-bg-0)',
                    color: 'var(--pf-text-secondary)',
                  }}>
                    Actions
                  </th>
                </tr>
              </thead>
              <tbody style={{ borderCollapse: 'collapse' }}>
                {locations.map((location) => (
                  <tr key={location.id} style={{
                    borderBottom: '1px solid var(--pf-border)',
                  }}
                  onMouseEnter={(e) => {
                    e.currentTarget.style.backgroundColor = 'var(--pf-bg-0)';
                  }}
                  onMouseLeave={(e) => {
                    e.currentTarget.style.backgroundColor = 'transparent';
                  }}>
                    <td className="px-6 py-4 whitespace-nowrap text-sm font-medium" style={{ color: 'var(--pf-text-primary)' }}>
                      {location.name}
                    </td>
                    <td className="px-6 py-4 text-sm" style={{ color: 'var(--pf-text-secondary)' }}>
                      {location.description || '-'}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm">
                      <span className="inline-flex items-center justify-center px-3 py-1 rounded-full text-sm font-medium" style={{
                        backgroundColor: 'var(--pf-accent-bg)',
                        color: 'white',
                      }}>
                        {location.printerCount}
                      </span>
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm" style={{ color: 'var(--pf-text-secondary)' }}>
                      {new Date(location.createdAt).toLocaleDateString()}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm font-medium space-x-3">
                      <button
                        onClick={() => handleEdit(location)}
                        className="text-blue-600 hover:text-blue-900"
                      >
                        Edit
                      </button>
                      <button
                        onClick={() => handleDelete(location.id)}
                        className="text-red-600 hover:text-red-900"
                      >
                        Delete
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
};

export default LocationManagement;
