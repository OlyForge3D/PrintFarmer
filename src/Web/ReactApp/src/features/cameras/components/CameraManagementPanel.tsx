import React, { useEffect, useOptimistic, useState, useTransition } from 'react';
import { SelectableRow } from '@/common/components/Table/SelectableRow';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { Button, Textarea } from '@/common/components/ui';
import {
  CameraIcon,
  DeleteIcon,
  DownloadIcon,
  EditIcon,
  EyeIcon,
  EyeOffIcon,
  PlusIcon,
  PrinterIcon
} from '@/common/components/icons/MdiIcons';
import { cameraService } from '@/services/cameraService';
import { apiClient } from '@/services/api';
import type { CameraDto, CreateCameraDto, Printer, UpdateCameraDto } from '@/types/api';

interface CameraManagementPanelProps {
  onCamerasChanged?: () => void;
}

/**
 * CameraManagementPanel - Manage standalone webcams
 *
 * Intended to be embedded inside the unified Cameras page.
 */
export function CameraManagementPanel({ onCamerasChanged }: CameraManagementPanelProps) {
  const [cameras, setCameras] = useState<CameraDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [formData, setFormData] = useState<CreateCameraDto>({
    name: '',
    description: '',
    streamUrl: '',
    snapshotUrl: '',
    location: '',
    sortOrder: 0,
    isEnabled: true
  });

  // State for "Add from Printer" modal
  const [showPrinterModal, setShowPrinterModal] = useState(false);
  const [printersWithCameras, setPrintersWithCameras] = useState<Printer[]>([]);
  const [loadingPrinters, setLoadingPrinters] = useState(false);

  // React 19: useTransition for async operations
  const [, startTransition] = useTransition();

  // React 19: useOptimistic for optimistic camera deletion
  const [optimisticCameras, addOptimisticDelete] = useOptimistic<CameraDto[], string>(
    cameras,
    (state, deletedId) => state.filter((cam) => cam.id !== deletedId)
  );

  // State for delete confirmation modal
  const [cameraToDelete, setCameraToDelete] = useState<string | null>(null);

  useEffect(() => {
    loadCameras();
  }, []);

  const loadCameras = async () => {
    try {
      setLoading(true);
      setError(null);
      const data = await cameraService.getAllCameras();
      setCameras(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load cameras');
    } finally {
      setLoading(false);
    }
  };

  const handleCreateOrUpdate = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!formData.name.trim()) {
      setError('Camera name is required');
      return;
    }
    if (!formData.streamUrl?.trim() && !formData.snapshotUrl?.trim()) {
      setError('At least one URL (stream or snapshot) is required');
      return;
    }

    try {
      setLoading(true);
      setError(null);

      if (editingId) {
        await cameraService.updateCamera(editingId, formData as UpdateCameraDto);
      } else {
        await cameraService.createCamera(formData);
      }

      setFormData({
        name: '',
        description: '',
        streamUrl: '',
        snapshotUrl: '',
        location: '',
        sortOrder: 0,
        isEnabled: true
      });
      setEditingId(null);
      setShowForm(false);
      await loadCameras();
      onCamerasChanged?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save camera');
    } finally {
      setLoading(false);
    }
  };

  const handleEdit = (camera: CameraDto) => {
    setEditingId(camera.id);
    setFormData({
      name: camera.name,
      description: camera.description || '',
      streamUrl: camera.streamUrl || '',
      snapshotUrl: camera.snapshotUrl || '',
      location: camera.location || '',
      sortOrder: camera.sortOrder,
      isEnabled: camera.isEnabled
    });
    setShowForm(true);
  };

  const handleDelete = (id: string) => {
    setCameraToDelete(id);
  };

  const confirmDelete = () => {
    if (!cameraToDelete) return;

    const id = cameraToDelete;
    setCameraToDelete(null);

    startTransition(async () => {
      try {
        addOptimisticDelete(id);
        setError(null);
        await cameraService.deleteCamera(id);
        await loadCameras();
        onCamerasChanged?.();
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to delete camera');
      }
    });
  };

  const handleToggle = async (camera: CameraDto) => {
    try {
      setError(null);
      await cameraService.toggleCamera(camera.id, !camera.isEnabled);
      await loadCameras();
      onCamerasChanged?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to toggle camera');
    }
  };

  const handleCancel = () => {
    setShowForm(false);
    setEditingId(null);
    setFormData({
      name: '',
      description: '',
      streamUrl: '',
      snapshotUrl: '',
      location: '',
      sortOrder: 0,
      isEnabled: true
    });
  };

  // Load printers that have camera URLs configured
  const loadPrintersWithCameras = async () => {
    try {
      setLoadingPrinters(true);
      setError(null);
      const printers = await apiClient.getPrinters();
      const withCameras = printers.filter((p) => p.cameraStreamUrl || p.cameraSnapshotUrl);
      setPrintersWithCameras(withCameras);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load printers');
    } finally {
      setLoadingPrinters(false);
    }
  };

  const handleOpenPrinterModal = () => {
    setShowPrinterModal(true);
    loadPrintersWithCameras();
  };

  const handleImportFromPrinter = async (printer: Printer) => {
    setFormData({
      name: `${printer.name} Camera`,
      description: `Camera from ${printer.name} printer`,
      streamUrl: printer.cameraStreamUrl || '',
      snapshotUrl: printer.cameraSnapshotUrl || '',
      location: '',
      sortOrder: cameras.length,
      isEnabled: true
    });
    setShowPrinterModal(false);
    setShowForm(true);
  };

  return (
    <div className="space-y-6">
      <div className="flex justify-between items-center">
        <h2 className="text-xl font-semibold text-pf-text-primary">Standalone Cameras</h2>
        {!showForm && (
          <div className="flex gap-2">
            <Button
              variant="secondary"
              onClick={handleOpenPrinterModal}
              iconLeft={<PrinterIcon className="w-4 h-4" />}
            >
              Add from Printer
            </Button>
            <Button onClick={() => setShowForm(true)} iconLeft={<PlusIcon className="w-4 h-4" />}>
              Add Camera
            </Button>
          </div>
        )}
      </div>

      {error && (
        <div className="px-4 py-3 rounded-sm bg-pf-error-bg border border-pf-error text-pf-error">
          {error}
        </div>
      )}

      {showForm && (
        <div className="shadow-sm rounded-lg p-6 bg-pf-bg-1 border border-pf-border max-w-4xl">
          <h3 className="text-lg font-semibold mb-4 text-pf-text-primary">
            {editingId ? 'Edit Camera' : 'Add New Camera'}
          </h3>
          <form onSubmit={handleCreateOrUpdate} className="space-y-4">
            <div>
              <label htmlFor="name" className="block text-sm font-medium text-pf-text-primary">
                Camera Name *
              </label>
              <input
                id="name"
                type="text"
                value={formData.name}
                onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                placeholder="e.g., Workshop Camera 1"
                className="mt-1 block w-full rounded-md shadow-xs py-2 px-3 focus:outline-hidden bg-pf-bg-0 text-pf-text-primary border border-pf-border"
                required
              />
            </div>

            <div>
              <label htmlFor="streamUrl" className="block text-sm font-medium text-pf-text-primary">
                Stream URL (MJPEG/HLS)
              </label>
              <input
                id="streamUrl"
                type="url"
                value={formData.streamUrl || ''}
                onChange={(e) => setFormData({ ...formData, streamUrl: e.target.value })}
                placeholder="e.g., http://192.168.1.100:8080/stream"
                className="mt-1 block w-full rounded-md shadow-xs py-2 px-3 focus:outline-hidden bg-pf-bg-0 text-pf-text-primary border border-pf-border"
              />
              <p className="mt-1 text-xs text-pf-text-tertiary">
                MJPEG or HLS stream URL. For RTSP cameras, use a transcoding service like go2rtc.
              </p>
            </div>

            <div>
              <label htmlFor="snapshotUrl" className="block text-sm font-medium text-pf-text-primary">
                Snapshot URL
              </label>
              <input
                id="snapshotUrl"
                type="url"
                value={formData.snapshotUrl || ''}
                onChange={(e) => setFormData({ ...formData, snapshotUrl: e.target.value })}
                placeholder="e.g., http://192.168.1.100:8080/snapshot"
                className="mt-1 block w-full rounded-md shadow-xs py-2 px-3 focus:outline-hidden bg-pf-bg-0 text-pf-text-primary border border-pf-border"
              />
            </div>

            <div>
              <label htmlFor="location" className="block text-sm font-medium text-pf-text-primary">
                Location
              </label>
              <input
                id="location"
                type="text"
                value={formData.location || ''}
                onChange={(e) => setFormData({ ...formData, location: e.target.value })}
                placeholder="e.g., Workshop, Main Room"
                className="mt-1 block w-full rounded-md shadow-xs py-2 px-3 focus:outline-hidden bg-pf-bg-0 text-pf-text-primary border border-pf-border"
              />
            </div>

            <div>
              <label htmlFor="description" className="block text-sm font-medium text-pf-text-primary">
                Description
              </label>
              <Textarea
                id="description"
                value={formData.description || ''}
                onChange={(e) => setFormData({ ...formData, description: e.target.value })}
                placeholder="Optional description"
                rows={2}
                className="w-full"
              />
            </div>

            <div>
              <label htmlFor="sortOrder" className="block text-sm font-medium text-pf-text-primary">
                Sort Order
              </label>
              <input
                id="sortOrder"
                type="number"
                value={formData.sortOrder || 0}
                onChange={(e) =>
                  setFormData({ ...formData, sortOrder: Number.parseInt(e.target.value) || 0 })
                }
                className="mt-1 block w-32 rounded-md shadow-xs py-2 px-3 focus:outline-hidden bg-pf-bg-0 text-pf-text-primary border border-pf-border"
              />
              <p className="mt-1 text-xs text-pf-text-tertiary">Lower numbers appear first in the camera view.</p>
            </div>

            <div className="flex gap-3 pt-4">
              <Button type="button" onClick={handleCancel} variant="secondary">
                Cancel
              </Button>
              <Button type="submit" disabled={loading}>
                {loading ? 'Saving...' : editingId ? 'Update Camera' : 'Add Camera'}
              </Button>
            </div>
          </form>
        </div>
      )}

      {loading && cameras.length === 0 ? (
        <div className="text-pf-text-secondary">Loading cameras...</div>
      ) : optimisticCameras.length === 0 ? (
        <div className="text-center py-12">
          <CameraIcon className="h-12 w-12 text-pf-text-tertiary mx-auto mb-4" />
          <h3 className="text-lg font-semibold text-pf-text-primary mb-2">No Standalone Cameras</h3>
          <p className="text-pf-text-secondary mb-4">
            Add webcams that are not attached to printers. They will appear in the Camera View.
          </p>
        </div>
      ) : (
        <div className="overflow-hidden rounded-lg border border-pf-border">
          <table className="min-w-full divide-y divide-pf-border">
            <thead className="bg-pf-bg-1">
              <tr>
                <th className="px-4 py-3 text-left text-xs font-medium text-pf-text-secondary uppercase tracking-wider">
                  Camera
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-pf-text-secondary uppercase tracking-wider">
                  URLs
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-pf-text-secondary uppercase tracking-wider">
                  Location
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-pf-text-secondary uppercase tracking-wider">
                  Status
                </th>
                <th className="px-4 py-3 text-right text-xs font-medium text-pf-text-secondary uppercase tracking-wider">
                  Actions
                </th>
              </tr>
            </thead>
            <tbody className="bg-pf-bg-0 divide-y divide-pf-border">
              {optimisticCameras.map((camera) => (
                <SelectableRow key={camera.id} isSelected={false}>
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-3">
                      <CameraIcon className="w-5 h-5 text-pf-text-tertiary" />
                      <div>
                        <div className="font-medium text-pf-text-primary">{camera.name}</div>
                        {camera.description && (
                          <div className="text-xs text-pf-text-tertiary">{camera.description}</div>
                        )}
                      </div>
                    </div>
                  </td>
                  <td className="px-4 py-3">
                    <div className="text-xs text-pf-text-secondary space-y-0.5">
                      {camera.streamUrl && <div title={camera.streamUrl}>Stream: ✓</div>}
                      {camera.snapshotUrl && <div title={camera.snapshotUrl}>Snapshot: ✓</div>}
                    </div>
                  </td>
                  <td className="px-4 py-3 text-sm text-pf-text-secondary">{camera.location || '-'}</td>
                  <td className="px-4 py-3">
                    <span
                      className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                        camera.isEnabled
                          ? 'bg-pf-status-online-bg text-pf-status-online-text'
                          : 'bg-pf-border-medium text-pf-text-secondary'
                      }`}
                    >
                      {camera.isEnabled ? 'Enabled' : 'Disabled'}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-right">
                    <div className="flex justify-end gap-1">
                      <Button
                        variant="subtle"
                        size="sm"
                        onClick={() => handleToggle(camera)}
                        title={camera.isEnabled ? 'Disable' : 'Enable'}
                        aria-label={camera.isEnabled ? 'Disable camera' : 'Enable camera'}
                        iconCenter={
                          camera.isEnabled ? (
                            <EyeIcon className="w-4 h-4 text-pf-status-online-text" />
                          ) : (
                            <EyeOffIcon className="w-4 h-4 text-pf-text-tertiary" />
                          )
                        }
                      />
                      <Button
                        variant="subtle"
                        size="sm"
                        onClick={() => handleEdit(camera)}
                        title="Edit"
                        aria-label="Edit camera"
                        iconCenter={<EditIcon className="w-4 h-4" />}
                      />
                      <Button
                        variant="subtle"
                        size="sm"
                        onClick={() => handleDelete(camera.id)}
                        title="Delete"
                        aria-label="Delete camera"
                        iconCenter={<DeleteIcon className="w-4 h-4 text-pf-error" />}
                      />
                    </div>
                  </td>
                </SelectableRow>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <ConfirmationModal
        isOpen={!!cameraToDelete}
        onCancel={() => setCameraToDelete(null)}
        onConfirm={confirmDelete}
        title="Delete Camera"
        message="Are you sure you want to delete this camera? This action cannot be undone."
        confirmButtonText="Delete"
        isDangerous
      />

      {showPrinterModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center">
          <div
            className="absolute inset-0 bg-black/50 backdrop-blur-xs"
            onClick={() => setShowPrinterModal(false)}
            onKeyDown={(e) => e.key === 'Escape' && setShowPrinterModal(false)}
            role="button"
            tabIndex={0}
            aria-label="Close modal"
          />
          <div className="relative bg-pf-bg-1 rounded-lg shadow-xl border border-pf-border max-w-2xl w-full mx-4 max-h-[80vh] overflow-hidden flex flex-col">
            <div className="p-4 border-b border-pf-border">
              <h3 className="text-lg font-semibold text-pf-text-primary">Add Camera from Printer</h3>
              <p className="text-sm text-pf-text-secondary mt-1">
                Select a printer to import its camera configuration
              </p>
            </div>
            <div className="p-4 overflow-y-auto flex-1">
              {loadingPrinters ? (
                <div className="text-center py-8 text-pf-text-secondary">Loading printers...</div>
              ) : printersWithCameras.length === 0 ? (
                <div className="text-center py-8">
                  <PrinterIcon className="w-12 h-12 text-pf-text-tertiary mx-auto mb-3" />
                  <p className="text-pf-text-secondary">No printers with camera configurations found.</p>
                  <p className="text-sm text-pf-text-tertiary mt-1">
                    Configure camera URLs in your printer settings first.
                  </p>
                </div>
              ) : (
                <div className="space-y-2">
                  {printersWithCameras.map((printer) => (
                    <Button
                      key={printer.id}
                      variant="unstyled"
                      onClick={() => handleImportFromPrinter(printer)}
                      className="w-full p-4 text-left rounded-lg border border-pf-border hover:border-pf-accent hover:bg-pf-bg-2 transition-colors"
                    >
                      <div className="flex items-center gap-3">
                        <PrinterIcon className="w-8 h-8 text-pf-text-tertiary shrink-0" />
                        <div className="flex-1 min-w-0">
                          <div className="font-medium text-pf-text-primary">{printer.name}</div>
                          <div className="text-xs text-pf-text-tertiary mt-1 space-y-0.5">
                            {printer.cameraStreamUrl && (
                              <div className="truncate" title={printer.cameraStreamUrl}>
                                Stream: {printer.cameraStreamUrl}
                              </div>
                            )}
                            {printer.cameraSnapshotUrl && (
                              <div className="truncate" title={printer.cameraSnapshotUrl}>
                                Snapshot: {printer.cameraSnapshotUrl}
                              </div>
                            )}
                          </div>
                        </div>
                        <DownloadIcon className="w-5 h-5 text-pf-accent shrink-0" />
                      </div>
                    </Button>
                  ))}
                </div>
              )}
            </div>
            <div className="p-4 border-t border-pf-border">
              <Button variant="secondary" onClick={() => setShowPrinterModal(false)}>
                Cancel
              </Button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
