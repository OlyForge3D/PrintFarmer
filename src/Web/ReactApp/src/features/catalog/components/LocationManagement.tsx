import React, { useState, useEffect } from 'react';
import { Location, CreateLocationRequest, UpdateLocationRequest, locationService } from '@/services/locationService';
import { PrinterLocationDragDrop } from '@/features/printers/components/PrinterLocationDragDrop';

/**
 * LocationManagement Component - Manage printer locations
 * 
 * Includes drag and drop functionality for assigning printers to locations via the PrinterLocationDragDrop component.
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
            className="bg-pf-accent-bg text-white font-bold py-2 px-4 rounded transition-opacity hover:opacity-90"
          >
            Add Location
          </button>
        )}
      </div>

      {/* Error message */}
      {error && (
        <div className="px-4 py-3 rounded bg-pf-error-bg border border-pf-error text-pf-error">
          {error}
        </div>
      )}

      {/* Create/Edit Form */}
      {showForm && (
        <div className="shadow rounded-lg p-6 bg-pf-bg-1 border border-pf-border">
          <h2 className="text-xl font-semibold mb-4 text-pf-text-primary">
            {editingId ? 'Edit Location' : 'Create New Location'}
          </h2>
          <form onSubmit={handleCreateOrUpdate} className="space-y-4">
            {/* Name field */}
            <div>
              <label htmlFor="name" className="block text-sm font-medium text-pf-text-primary">
                Location Name *
              </label>
              <input
                id="name"
                type="text"
                value={formData.name}
                onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                placeholder="e.g., RACK1-01"
                className="mt-1 block w-full rounded-md shadow-sm py-2 px-3 focus:outline-none bg-pf-bg-0 text-pf-text-primary border border-pf-border"
                required
              />
            </div>

            {/* Description field */}
            <div>
              <label htmlFor="description" className="block text-sm font-medium text-pf-text-primary">
                Description
              </label>
              <textarea
                id="description"
                value={formData.description || ''}
                onChange={(e) => setFormData({ ...formData, description: e.target.value })}
                placeholder="e.g., Main warehouse rack, first column"
                rows={3}
                className="mt-1 block w-full rounded-md shadow-sm py-2 px-3 focus:outline-none bg-pf-bg-0 text-pf-text-primary border border-pf-border"
              />
            </div>

            {/* Form actions */}
            <div className="flex gap-3 pt-4">
              <button
                type="submit"
                disabled={loading}
                className="bg-pf-accent-bg text-white font-bold py-2 px-4 rounded transition-opacity disabled:opacity-50 hover:opacity-90"
              >
                {loading ? 'Saving...' : editingId ? 'Update' : 'Create'}
              </button>
              <button
                type="button"
                onClick={handleCancel}
                className="bg-pf-border-medium text-pf-text-primary font-bold py-2 px-4 rounded transition-opacity hover:opacity-80"
              >
                Cancel
              </button>
            </div>
          </form>
        </div>
      )}

      {/* Locations List */}
      <div className="shadow overflow-hidden rounded-lg bg-pf-bg-1 border border-pf-border">
        {loading && !showForm ? (
          <div className="p-6 text-center text-pf-text-primary">Loading locations...</div>
        ) : locations.length === 0 ? (
          <div className="p-6 text-center text-pf-text-secondary">
            No locations found. Create one to get started!
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full border-collapse">
              <thead>
                <tr className="border-b border-pf-border">
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider bg-pf-bg-0 text-pf-text-secondary">
                    Name
                  </th>
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider bg-pf-bg-0 text-pf-text-secondary">
                    Description
                  </th>
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider bg-pf-bg-0 text-pf-text-secondary">
                    Printers
                  </th>
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider bg-pf-bg-0 text-pf-text-secondary">
                    Created
                  </th>
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider bg-pf-bg-0 text-pf-text-secondary">
                    Actions
                  </th>
                </tr>
              </thead>
              <tbody>
                {locations.map((location) => (
                  <tr key={location.id} className="border-b border-pf-border hover:bg-pf-bg-0">
                    <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-pf-text-primary">
                      {location.name}
                    </td>
                    <td className="px-6 py-4 text-sm text-pf-text-secondary">
                      {location.description || '-'}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm">
                      <span className="inline-flex items-center justify-center px-3 py-1 rounded-full text-sm font-medium bg-pf-accent-bg text-white">
                        {location.printerCount}
                      </span>
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-pf-text-secondary">
                      {new Date(location.createdAt).toLocaleDateString()}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm font-medium space-x-3">
                      <button
                        onClick={() => handleEdit(location)}
                        className="text-pf-accent hover:text-pf-accent-bg"
                      >
                        Edit
                      </button>
                      <button
                        onClick={() => handleDelete(location.id)}
                        style={{ color: 'var(--pf-error)' }}
                        className="hover:opacity-80"
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

      {/* Drag and Drop Printer Assignment */}
      <div className="border-t pt-8 mt-8">
        <PrinterLocationDragDrop />
      </div>
    </div>
  );
};

export default LocationManagement;
