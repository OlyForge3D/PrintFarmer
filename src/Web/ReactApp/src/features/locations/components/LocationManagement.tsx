import React, { useState, useEffect, useOptimistic, useTransition, useCallback, useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import clsx from 'clsx';
import type { Location, LocationTreeNode, CreateLocationRequest, UpdateLocationRequest } from '@/types/api';
import { locationService } from '@/services/locationService';
import { invalidateLocationDashboardQueries } from '@/features/locations/hooks/useLocationDashboard';
import { LocationTreePicker } from '@/features/locations/components/LocationTreePicker';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { Modal } from '@/common/components/modals/Modal';
import { PrinterLocationDragDrop } from '@/features/printers/components/PrinterLocationDragDrop';
import { Button, Input, Textarea, FormField } from '@/common/components/ui';

interface TreeRowViewProps {
  node: LocationTreeNode;
  depth: number;
  expandedIds: Set<string>;
  onToggle: (id: string) => void;
  onEdit: (location: LocationTreeNode) => void;
  onAddChild: (parentId: string) => void;
  onMove: (location: LocationTreeNode) => void;
  onDelete: (id: string) => void;
  deletingId: string | null;
}

const TreeRowView: React.FC<TreeRowViewProps> = ({
  node,
  depth,
  expandedIds,
  onToggle,
  onEdit,
  onAddChild,
  onMove,
  onDelete,
  deletingId,
}) => {
  const isExpanded = expandedIds.has(node.id);
  const hasChildren = node.children.length > 0;
  const isDeleting = deletingId === node.id;

  return (
    <>
      <tr
        className={clsx(
          'border-b border-pf-border transition-opacity',
          isDeleting && 'opacity-40',
        )}
      >
        <td className="px-6 py-3 whitespace-nowrap text-sm font-medium text-pf-text-primary">
          <div className="flex items-center" style={{ paddingLeft: `${depth * 24}px` }}>
            {hasChildren ? (
              <Button
                variant="unstyled"
                className="mr-2 w-5 h-5 flex items-center justify-center text-pf-text-secondary hover:text-pf-text-primary"
                onClick={() => onToggle(node.id)}
                aria-expanded={isExpanded}
                aria-label={`${isExpanded ? 'Collapse' : 'Expand'} ${node.name}`}
              >
                {isExpanded ? '▾' : '▸'}
              </Button>
            ) : (
              <span className="mr-2 w-5 h-5" />
            )}
            {node.name}
          </div>
        </td>
        <td className="px-6 py-3 text-sm text-pf-text-secondary">
          {node.description || '-'}
        </td>
        <td className="px-6 py-3 whitespace-nowrap text-sm">
          <span className="inline-flex items-center justify-center px-3 py-1 rounded-full text-sm font-medium bg-pf-accent-bg text-white">
            {node.printerCount}
          </span>
          {node.totalPrinterCount > node.printerCount && (
            <span className="ml-1 text-xs text-pf-text-tertiary" title="Total including children">
              ({node.totalPrinterCount} total)
            </span>
          )}
        </td>
        <td className="px-6 py-3 whitespace-nowrap text-sm text-pf-text-secondary">
          {node.path || node.name}
        </td>
        <td className="px-6 py-3 whitespace-nowrap text-sm font-medium space-x-2">
          <Button onClick={() => onEdit(node)} variant="subtle" size="sm">Edit</Button>
          <Button onClick={() => onAddChild(node.id)} variant="subtle" size="sm">+ Child</Button>
          <Button onClick={() => onMove(node)} variant="subtle" size="sm">Move</Button>
          <Button
            onClick={() => onDelete(node.id)}
            variant="subtle"
            size="sm"
            className="text-pf-error hover:opacity-80"
            disabled={hasChildren}
          >
            Delete
          </Button>
        </td>
      </tr>
      {hasChildren && isExpanded &&
        node.children.map((child) => (
          <TreeRowView
            key={child.id}
            node={child}
            depth={depth + 1}
            expandedIds={expandedIds}
            onToggle={onToggle}
            onEdit={onEdit}
            onAddChild={onAddChild}
            onMove={onMove}
            onDelete={onDelete}
            deletingId={deletingId}
          />
        ))}
    </>
  );
};

interface LocationManagementProps {
  embedded?: boolean;
  showAssignments?: boolean;
  autoOpenCreateToken?: number;
  initialParentId?: string | null;
}

/**
 * LocationManagement Component - Manage printer locations as a tree hierarchy
 */
export const LocationManagement: React.FC<LocationManagementProps> = ({
  embedded = false,
  showAssignments = true,
  autoOpenCreateToken = 0,
  initialParentId = null,
}) => {
  const queryClient = useQueryClient();
  const [locations, setLocations] = useState<Location[]>([]);
  const [tree, setTree] = useState<LocationTreeNode[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [formData, setFormData] = useState<CreateLocationRequest>({
    name: '',
    description: '',
    parentId: null,
  });
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set());
  const [movingLocation, setMovingLocation] = useState<LocationTreeNode | null>(null);
  const [moveParentId, setMoveParentId] = useState<string | null>(null);

  const [, startTransition] = useTransition();
  const [optimisticLocations, addOptimisticDelete] = useOptimistic<Location[], string>(
    locations,
    (state, deletedLocationId) => state.filter((loc) => loc.id !== deletedLocationId),
  );

  const [locationToDelete, setLocationToDelete] = useState<string | null>(null);
  const initialParentIdRef = useRef<string | null>(initialParentId);
  const previousAutoOpenCreateTokenRef = useRef(0);
  const nameError = error === 'Location name is required' ? error : undefined;

  useEffect(() => {
    initialParentIdRef.current = initialParentId;
  }, [initialParentId]);

  const loadData = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const [allLocations, treeData] = await Promise.all([
        locationService.getAllLocations(),
        locationService.getLocationTree(),
      ]);
      setLocations(allLocations);
      setTree(treeData);
      setExpandedIds((prev) => {
        if (prev.size === 0) return new Set(treeData.map((n) => n.id));
        return prev;
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load locations');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadData();
  }, [loadData]);

  useEffect(() => {
    if (autoOpenCreateToken <= 0) return;
    if (autoOpenCreateToken === previousAutoOpenCreateTokenRef.current) return;
    previousAutoOpenCreateTokenRef.current = autoOpenCreateToken;
    const parentId = initialParentIdRef.current;
    setEditingId(null);
    setFormData({ name: '', description: '', parentId: parentId ?? null });
    setShowForm(true);
    if (parentId) {
      setExpandedIds((prev) => new Set([...prev, parentId]));
    }
  }, [autoOpenCreateToken]);

  const handleToggle = useCallback((id: string) => {
    setExpandedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

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
        await locationService.updateLocation(editingId, formData as UpdateLocationRequest);
      } else {
        await locationService.createLocation(formData);
      }
      invalidateLocationDashboardQueries(queryClient);

      setFormData({ name: '', description: '', parentId: null });
      setEditingId(null);
      setShowForm(false);
      await loadData();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save location');
    } finally {
      setLoading(false);
    }
  };

  const handleEdit = (node: LocationTreeNode) => {
    setEditingId(node.id);
    setFormData({
      name: node.name,
      description: node.description,
      parentId: node.parentId,
    });
    setShowForm(true);
  };

  const handleAddChild = (parentId: string) => {
    setEditingId(null);
    setFormData({ name: '', description: '', parentId });
    setShowForm(true);
    // Expand parent so user sees the context
    setExpandedIds((prev) => new Set([...prev, parentId]));
  };

  const handleMove = (node: LocationTreeNode) => {
    setMovingLocation(node);
    setMoveParentId(node.parentId ?? null);
  };

  const handleDelete = (id: string) => {
    setLocationToDelete(id);
  };

  const confirmDelete = () => {
    if (!locationToDelete) return;

    const id = locationToDelete;
    setLocationToDelete(null);

    startTransition(async () => {
      try {
        addOptimisticDelete(id);
        setError(null);
        await locationService.deleteLocation(id);
        invalidateLocationDashboardQueries(queryClient);
        await loadData();
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to delete location');
      }
    });
  };

  const confirmMove = async () => {
    if (!movingLocation) return;

    try {
      setLoading(true);
      setError(null);
      await locationService.moveLocation(movingLocation.id, { newParentId: moveParentId });
      invalidateLocationDashboardQueries(queryClient);
      setMovingLocation(null);
      setMoveParentId(null);
      await loadData();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to move location');
    } finally {
      setLoading(false);
    }
  };

  const handleCancel = () => {
    setShowForm(false);
    setEditingId(null);
    setFormData({ name: '', description: '', parentId: null });
  };

  return (
    <div className="space-y-6">
      <div className="flex justify-between items-center">
        <h2 className={clsx(embedded ? 'text-xl' : 'text-3xl', 'font-bold text-pf-text-primary')}>Printer Locations</h2>
        {!showForm && (
          <Button onClick={() => { setFormData({ name: '', description: '', parentId: initialParentId ?? null }); setShowForm(true); }}>
            Add Location
          </Button>
        )}
      </div>

      {error && (
        <div role="alert" className="px-4 py-3 rounded-sm bg-pf-error-bg border border-pf-error text-pf-error">
          {error}
        </div>
      )}

      {showForm && (
        <div className="shadow-sm rounded-lg p-6 bg-pf-bg-1 border border-pf-border max-w-4xl">
          <h2 className="text-xl font-semibold mb-4 text-pf-text-primary">
            {editingId ? 'Edit Location' : 'Create New Location'}
          </h2>
          <form onSubmit={handleCreateOrUpdate} className="space-y-4" noValidate>
            <FormField label="Location Name" htmlFor="loc-name" required error={nameError}>
              <Input
                id="loc-name"
                type="text"
                value={formData.name}
                onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                placeholder="e.g., Warehouse A"
                required
                invalid={!!nameError}
                aria-invalid={!!nameError}
              />
            </FormField>

            {!editingId && (
              <LocationTreePicker
                value={formData.parentId}
                onChange={(parentId) => setFormData({ ...formData, parentId })}
                label="Parent Location"
                placeholder="None (top-level)"
                excludeId={editingId ?? undefined}
              />
            )}

            <FormField label="Description" htmlFor="loc-desc">
              <Textarea
                id="loc-desc"
                value={formData.description || ''}
                onChange={(e) => setFormData({ ...formData, description: e.target.value })}
                placeholder="e.g., Main warehouse, first floor"
                rows={3}
                className="w-full"
              />
            </FormField>

            <div className="flex gap-3 pt-4">
              <Button type="button" onClick={handleCancel} variant="secondary">Cancel</Button>
              <Button type="submit" disabled={loading}>
                {loading ? 'Saving...' : editingId ? 'Update' : 'Create'}
              </Button>
            </div>
          </form>
        </div>
      )}

      <div className="shadow-sm overflow-hidden rounded-lg bg-pf-bg-1 border border-pf-border">
        {loading && !showForm ? (
          <div className="p-6 text-center text-pf-text-primary">Loading locations...</div>
        ) : tree.length === 0 ? (
          <div className="p-6 text-center text-pf-text-secondary">
            No locations found. Create one to get started!
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full border-collapse">
              <thead>
                <tr className="border-b border-pf-border">
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider bg-pf-bg-0 text-pf-text-secondary">Name</th>
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider bg-pf-bg-0 text-pf-text-secondary">Description</th>
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider bg-pf-bg-0 text-pf-text-secondary">Printers</th>
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider bg-pf-bg-0 text-pf-text-secondary">Path</th>
                  <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider bg-pf-bg-0 text-pf-text-secondary">Actions</th>
                </tr>
              </thead>
              <tbody>
                {tree.map((node) => (
                  <TreeRowView
                    key={node.id}
                    node={node}
                    depth={0}
                    expandedIds={expandedIds}
                    onToggle={handleToggle}
                    onEdit={handleEdit}
                    onAddChild={handleAddChild}
                    onMove={handleMove}
                    onDelete={handleDelete}
                    deletingId={locationToDelete}
                  />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {showAssignments && optimisticLocations.length > 0 && (
        <div className="border-t pt-8 mt-8">
          <PrinterLocationDragDrop key={optimisticLocations.length} locations={optimisticLocations} />
        </div>
      )}

      <ConfirmationModal
        isOpen={!!locationToDelete}
        title="Delete Location?"
        message="Delete this location? Locations with children cannot be deleted."
        confirmButtonText="Delete"
        cancelButtonText="Cancel"
        isDangerous
        onConfirm={confirmDelete}
        onCancel={() => setLocationToDelete(null)}
      />

      <Modal
        isOpen={!!movingLocation}
        onClose={() => setMovingLocation(null)}
        title={movingLocation ? `Move ${movingLocation.name}` : 'Move location'}
        size="md"
        footer={
          <>
            <Button type="button" variant="secondary" onClick={() => setMovingLocation(null)} disabled={loading}>
              Cancel
            </Button>
            <Button type="button" variant="primary" onClick={confirmMove} disabled={loading}>
              {loading ? 'Moving...' : 'Move location'}
            </Button>
          </>
        }
      >
        {/* TODO(a11y): Shared Modal needs focus trap and focus restore support. */}
        <div className="space-y-4">
          <p className="text-sm text-pf-text-secondary">
            Choose a new parent location. Select none to move this location to the top level.
          </p>
          <LocationTreePicker
            value={moveParentId}
            onChange={setMoveParentId}
            label="New parent location"
            placeholder="None (top-level)"
            excludeId={movingLocation?.id}
          />
        </div>
      </Modal>
    </div>
  );
};

export default LocationManagement;
