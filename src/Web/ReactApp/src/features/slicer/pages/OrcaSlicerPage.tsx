/**
 * OrcaSlicer-Style Slice Job Page
 * A full-screen slicer interface matching OrcaSlicer's layout
 */
import React, { useState, useEffect, useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { sliceJobService, type SubmitSliceJobRequest, SlicerEngine } from '@/services/sliceJobService';
import { slicerProfilesService } from '@/services/slicerProfilesService';
import { workersService } from '@/services/workersService';
import { slicerRegistry } from '@/services/slicerRegistry';
import { assetService } from '@/services/assetService';
import { apiClient } from '@/services/api';
import { WorkerResponse } from '@/types/worker';
import { hasRequiredCapabilities } from '@/types/worker';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import { SlicerWorkspace, type LoadedModel, type BedConfig } from '@/features/slicer/components/viewer';
import { SlicerSettingsPanel, type OrcaProcessSettings } from '@/features/slicer/components/settings';
import { PrinterSelectorModal } from '@/features/printers/components/PrinterSelectorModal';
import { ProfileSelector } from '@/features/slicer/components/ProfileSelector';
import type { MaterialType, MaterialPreset } from '@/types/slicer';
import type { ModelListItem } from '@/types/models';
import { Button, Alert, Select } from '@/common/components/ui';
import { useAuth } from '@/features/auth/hooks/useAuth';

const MATERIAL_PRESETS: Record<MaterialType, MaterialPreset> = {
  'PLA': { name: 'PLA', nozzleTemp: 210, bedTemp: 60 },
  'PETG': { name: 'PETG', nozzleTemp: 240, bedTemp: 80 },
  'ABS': { name: 'ABS', nozzleTemp: 245, bedTemp: 100 },
  'TPU': { name: 'TPU', nozzleTemp: 225, bedTemp: 60 },
  'Nylon': { name: 'Nylon', nozzleTemp: 260, bedTemp: 80 },
  'Carbon': { name: 'Carbon', nozzleTemp: 250, bedTemp: 90 },
  'Other': { name: 'Other', nozzleTemp: 220, bedTemp: 60 }
};

// Default bed config when no printer is selected
const DEFAULT_BED_CONFIG: BedConfig = {
  width: 400,
  depth: 400,
  height: 400,
};

export const OrcaSlicerPage: React.FC = () => {
  const { user } = useAuth(); // Verify user is authenticated and get user info
  const qc = useQueryClient();
  const [searchParams] = useSearchParams();
  const modelIdFromUrl = searchParams.get('modelId') || '';

  // === State ===
  const [selectedSlicerId, setSelectedSlicerId] = useState<number>(2); // Default to OrcaSlicer
  const [selectedPrinterId, setSelectedPrinterId] = useState<string>('');
  const [selectedFilamentMaterial, setSelectedFilamentMaterial] = useState<MaterialType>('PLA');
  const [selectedProcessPresetId, setSelectedProcessPresetId] = useState<string>('');
  const [slicerSettings, setSlicerSettings] = useState<OrcaProcessSettings>({} as OrcaProcessSettings);
  const originalProcessSettings = useMemo<Record<string, unknown>>(() => ({}), []);
  const [loadedModels, setLoadedModels] = useState<LoadedModel[]>([]);
  const [selectedLoadedModelId, setSelectedLoadedModelId] = useState<string | null>(null);
  const [showSettingsPanel, setShowSettingsPanel] = useState(false);
  const [isPrinterSelectorOpen, setIsPrinterSelectorOpen] = useState(false);
  const [priority, setPriority] = useState(1);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [slicing, setSlicing] = useState(false);

  // === Queries ===
  const { data: availableWorkers = [] } = useQuery<WorkerResponse[], Error>({
    queryKey: ['workers-available'],
    queryFn: () => workersService.getAvailableWorkers(),
    staleTime: 10_000,
    refetchInterval: 15_000,
  });

  const { data: availableSlicers = [] } = useQuery({
    queryKey: ['slicers-available'],
    queryFn: () => slicerRegistry.getSlicers(),
    staleTime: 10_000,
  });

  const { data: printers = [] } = useQuery({
    queryKey: ['printers'],
    queryFn: async () => {
      const printerList = await apiClient.getPrinters();
      return printerList as Array<{ 
        id: string; 
        name: string; 
        modelId?: string; 
        manufacturerName?: string; 
        modelName?: string;
      }>;
    },
    staleTime: 30_000
  });

  const { data: selectedPrinterDetails } = useQuery({
    queryKey: ['printerDetails', selectedPrinterId],
    queryFn: async () => {
      if (!selectedPrinterId) return null;
      const details = await apiClient.getPrinterDetails(selectedPrinterId);
      return details as {
        id: string;
        name: string;
        manufacturerName?: string;
        modelName?: string;
        modelMaxX?: number;
        modelMaxY?: number;
        modelMaxZ?: number;
      };
    },
    enabled: !!selectedPrinterId,
    staleTime: 30_000
  });

  const { data: models = [] } = useQuery<ModelListItem[], Error>({
    queryKey: ['modelsListBasic'],
    queryFn: async () => {
      const response = await apiClient.get<unknown[]>('/3d-models');
      return response.data.map(obj => {
        const m = obj as { id: string; fileName?: string; displayName?: string; originalFileName?: string; fileFormat?: number; uploadedAt?: string };
        return {
          id: m.id,
          fileName: m.fileName || m.displayName || m.originalFileName || 'model',
          originalFileName: m.originalFileName || m.fileName || 'model',
          fileFormat: m.fileFormat ?? 0,
          uploadedAt: m.uploadedAt ?? ''
        } as ModelListItem;
      });
    },
    staleTime: 20_000
  });

  const { data: hierarchyProfiles } = useQuery({
    queryKey: ['slicerProfilesHierarchy'],
    queryFn: () => slicerProfilesService.listHierarchical(),
    staleTime: 15_000
  });

  // === Derived values ===
  const selectedPrinter = useMemo(() => {
    return printers.find(p => p.id === selectedPrinterId);
  }, [printers, selectedPrinterId]);

  const bedConfig: BedConfig = useMemo(() => {
    if (selectedPrinterDetails?.modelMaxX && selectedPrinterDetails?.modelMaxY) {
      const config: BedConfig = {
        width: selectedPrinterDetails.modelMaxX,
        depth: selectedPrinterDetails.modelMaxY,
        height: selectedPrinterDetails.modelMaxZ || 300,
      };

      // Get bed texture if available
      if (selectedPrinterDetails.manufacturerName && selectedPrinterDetails.modelName) {
        const asset = assetService.getAsset(selectedPrinterDetails.manufacturerName, selectedPrinterDetails.modelName);
        if (asset?.bedTexture) {
          config.textureUrl = asset.bedTexture;
          config.textureFormat = asset.bedTextureFormat as 'svg' | 'png' | undefined;
        }
        if (asset?.bedModel) {
          config.bedModelUrl = asset.bedModel;
        }
      }

      return config;
    }
    return DEFAULT_BED_CONFIG;
  }, [selectedPrinterDetails]);

  const engineOptions = useMemo(() => {
    return availableSlicers.map(slicer => ({
      label: `${slicer.name || slicer.slicerType || 'Unknown'} v${slicer.version || '?'}`,
      value: slicer.slicerType === 'PrusaSlicer' ? 1 : slicer.slicerType === 'OrcaSlicer' ? 2 : 0
    }));
  }, [availableSlicers]);

  // Auto-select compatible worker
  const selectedWorkerId = useMemo(() => {
    const slicerType = selectedSlicerId === 1 ? 'prusaslicer' : 'orcaslicer';
    const compatible = availableWorkers.find(w => 
      hasRequiredCapabilities(w, [slicerType])
    );
    return compatible?.id || availableWorkers[0]?.id || '';
  }, [availableWorkers, selectedSlicerId]);

  // === Effects ===
  // Load model when modelIdFromUrl changes
  useEffect(() => {
    if (modelIdFromUrl && models.length > 0) {
      const model = models.find(m => m.id === modelIdFromUrl);
      if (model) {
        const apiBase = getApiBaseUrl();
        const ext = (model.originalFileName || model.fileName).split('.').pop()?.toLowerCase() || 'stl';
        const newLoadedModel: LoadedModel = {
          id: model.id,
          url: `${apiBase}/3d-models/file/${model.id}`,
          fileName: model.originalFileName || model.fileName,
          fileType: ext === 'ply' ? 'ply' : ext === '3mf' ? '3mf' : 'stl',
          position: [0, 0, 0],
          rotation: [0, 0, 0],
          scale: [1, 1, 1],
        };
        queueMicrotask(() => {
          setLoadedModels([newLoadedModel]);
          setSelectedLoadedModelId(model.id);
        });
      }
    }
  }, [modelIdFromUrl, models]);

  // === Mutations ===
  const submitMutation = useMutation({
    mutationFn: (req: SubmitSliceJobRequest) => sliceJobService.submitJob(req),
    onMutate: () => {
      setSlicing(true);
      setError(null);
      setMessage(null);
    },
    onSuccess: (data) => {
      setSlicing(false);
      setMessage(`Slice job submitted successfully! Job ID: ${data.jobId}`);
      qc.invalidateQueries({ queryKey: ['slice-jobs'] });
    },
    onError: () => {
      setSlicing(false);
      setError('Failed to submit slice job');
    },
  });

  // === Handlers ===
  const handleSlicerSettingsChange = useCallback((newSettings: OrcaProcessSettings) => {
    setSlicerSettings(newSettings);
  }, []);

  const handleAddModel = useCallback(() => {
    // Open model picker (could be a modal or navigate to models page)
    // For now, just toggle a simple file input
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.stl,.3mf,.ply,.obj';
    input.onchange = async (e) => {
      const file = (e.target as HTMLInputElement).files?.[0];
      if (file) {
        // Upload file to server
        const formData = new FormData();
        formData.append('file', file);
        try {
          const response = await apiClient.post<{ id: string; fileName: string }>('/3d-models/upload', formData);
          const uploaded = response.data;
          const apiBase = getApiBaseUrl();
          
          const ext = file.name.split('.').pop()?.toLowerCase() || 'stl';
          const newModel: LoadedModel = {
            id: uploaded.id,
            url: `${apiBase}/3d-models/file/${uploaded.id}`,
            fileName: file.name,
            fileType: ext === 'ply' ? 'ply' : ext === '3mf' ? '3mf' : 'stl',
            position: [0, 0, 0],
            rotation: [0, 0, 0],
            scale: [1, 1, 1],
          };
          setLoadedModels(prev => [...prev, newModel]);
          setSelectedLoadedModelId(uploaded.id);
          qc.invalidateQueries({ queryKey: ['modelsListBasic'] });
        } catch {
          setError('Failed to upload model');
        }
      }
    };
    input.click();
  }, [qc]);

  const handleModelSelect = useCallback((modelId: string | null) => {
    setSelectedLoadedModelId(modelId);
  }, []);

  const handleModelTransform = useCallback(
    (modelId: string, newPosition: [number, number, number], newRotation: [number, number, number], newScale: [number, number, number]) => {
      setLoadedModels(prev =>
        prev.map(model =>
          model.id === modelId
            ? { ...model, position: newPosition, rotation: newRotation, scale: newScale }
            : model
        )
      );
    },
    []
  );

  const handleSettingsProfiles = useCallback(() => {
    setShowSettingsPanel(prev => !prev);
  }, []);

  const handleSlice = useCallback(() => {
    if (loadedModels.length === 0) {
      setError('Please add a model to slice');
      return;
    }
    if (!user?.id) {
      setError('User not authenticated');
      return;
    }

    const model = loadedModels[0]; // For now, slice first model
    const slicerEngine = selectedSlicerId === 1 ? SlicerEngine.PrusaSlicer : SlicerEngine.OrcaSlicer;
    const capabilities = [selectedSlicerId === 1 ? 'prusaslicer' : 'orcaslicer'];
    
    const request: SubmitSliceJobRequest = {
      userId: user.id,
      printerId: selectedPrinterId || undefined,
      modelFileUrl: model.url,
      modelFileName: model.fileName,
      slicerEngine,
      slicerProfileJson: JSON.stringify(slicerSettings),
      slicerProfileId: selectedProcessPresetId || undefined,
      requiredCapabilitiesJson: JSON.stringify(capabilities),
      priority,
    };
    
    submitMutation.mutate(request);
  }, [loadedModels, user, selectedSlicerId, selectedPrinterId, slicerSettings, selectedProcessPresetId, priority, submitMutation]);

  const canSlice = loadedModels.length > 0 && !!selectedWorkerId;

  return (
    <div className="h-screen flex flex-col bg-pf-bg-0">
      {/* Main Workspace */}
      <div className="flex-1 flex overflow-hidden">
        {/* Settings panel (slide out from right) */}
        <div 
          className={`
            fixed right-0 top-0 h-full w-96 bg-pf-panel border-l border-pf-border shadow-xl z-50
            transform transition-transform duration-300 ease-in-out overflow-y-auto
            ${showSettingsPanel ? 'translate-x-0' : 'translate-x-full'}
          `}
        >
          <div className="p-4 border-b border-pf-border flex items-center justify-between">
            <h2 className="text-lg font-semibold text-pf-text-primary">Settings & Profiles</h2>
            <Button 
              variant="subtle"
              onClick={() => setShowSettingsPanel(false)}
              className="p-2"
            >
              <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </Button>
          </div>
          
          <div className="p-4 space-y-4">
            {/* Slicer Selection */}
            <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
              <label className="block text-sm font-semibold text-pf-text-primary mb-2">Slicer Engine</label>
              <Select
                value={selectedSlicerId}
                onChange={e => setSelectedSlicerId(Number(e.target.value))}
                className="w-full"
              >
                {engineOptions.map(opt => (
                  <option key={opt.value} value={opt.value}>{opt.label}</option>
                ))}
              </Select>
            </div>

            {/* Printer Selection */}
            <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
              <label className="block text-sm font-semibold text-pf-text-primary mb-2">Printer</label>
              {selectedPrinter ? (
                <div className="space-y-2">
                  <div className="p-3 bg-pf-bg-0 rounded-sm border border-pf-border">
                    <p className="font-medium text-pf-text-primary">{selectedPrinter.name}</p>
                    {selectedPrinter.modelName && (
                      <p className="text-sm text-pf-text-muted">
                        {selectedPrinter.manufacturerName && `${selectedPrinter.manufacturerName} • `}
                        {selectedPrinter.modelName}
                      </p>
                    )}
                  </div>
                  <Button
                    type="button"
                    onClick={() => setIsPrinterSelectorOpen(true)}
                    variant="secondary"
                    size="sm"
                    className="w-full"
                  >
                    Change Printer
                  </Button>
                </div>
              ) : (
                <Button
                  type="button"
                  onClick={() => setIsPrinterSelectorOpen(true)}
                  variant="primary"
                  size="sm"
                  className="w-full"
                >
                  Select Printer
                </Button>
              )}
            </div>

            {/* Filament Selection */}
            <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
              <label className="block text-sm font-semibold text-pf-text-primary mb-2">Filament</label>
              <Select
                value={selectedFilamentMaterial}
                onChange={e => setSelectedFilamentMaterial(e.target.value as MaterialType)}
                className="w-full"
              >
                {Object.keys(MATERIAL_PRESETS).map(m => (
                  <option key={m} value={m}>{m}</option>
                ))}
              </Select>
              <div className="text-xs text-pf-text-muted mt-2">
                {MATERIAL_PRESETS[selectedFilamentMaterial].nozzleTemp}°C nozzle, {MATERIAL_PRESETS[selectedFilamentMaterial].bedTemp}°C bed
              </div>
            </div>

            {/* Process Profile */}
            <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
              <label className="block text-sm font-semibold text-pf-text-primary mb-2">Process Profile</label>
              {hierarchyProfiles ? (
                <ProfileSelector
                  hierarchyData={hierarchyProfiles}
                  selectedProfileId={selectedProcessPresetId}
                  onChange={setSelectedProcessPresetId}
                />
              ) : (
                <Select value={selectedProcessPresetId} onChange={e => setSelectedProcessPresetId(e.target.value)}>
                  <option value="">-- Select Profile --</option>
                </Select>
              )}
            </div>

            {/* Slicer Settings */}
            <div className="bg-pf-bg-1 border border-pf-border rounded-lg overflow-hidden">
              <SlicerSettingsPanel
                settings={slicerSettings}
                onChange={handleSlicerSettingsChange}
                initialViewMode="simple"
                originalSettings={originalProcessSettings}
              />
            </div>

            {/* Priority */}
            <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
              <label className="block text-sm font-semibold text-pf-text-primary mb-2">Priority</label>
              <Select value={priority} onChange={e => setPriority(Number(e.target.value))}>
                <option value={0}>Low</option>
                <option value={1}>Normal</option>
                <option value={2}>High</option>
                <option value={3}>Critical</option>
              </Select>
            </div>
          </div>
        </div>

        {/* Main 3D Workspace */}
        <div className="flex-1">
          <SlicerWorkspace
            bedConfig={bedConfig}
            models={loadedModels}
            selectedModelId={selectedLoadedModelId || undefined}
            onModelSelect={handleModelSelect}
            onModelTransform={handleModelTransform}
            onAddModel={handleAddModel}
            onSettingsProfiles={handleSettingsProfiles}
            onSlice={handleSlice}
            slicing={slicing}
            canSlice={canSlice}
            slicesRemaining={30}
            slicesTotal={30}
          />
        </div>
      </div>

      {/* Error/Success Messages (floating) */}
      {(error || message) && (
        <div className="fixed bottom-20 left-1/2 -translate-x-1/2 z-50 max-w-lg w-full px-4">
          {error && <Alert type="error" className="shadow-lg">{error}</Alert>}
          {message && <Alert type="success" className="shadow-lg">{message}</Alert>}
        </div>
      )}

      {/* Printer Selector Modal */}
      <PrinterSelectorModal
        isOpen={isPrinterSelectorOpen}
        printers={printers}
        selectedPrinterId={selectedPrinterId}
        onSelect={(printerId) => setSelectedPrinterId(printerId)}
        onClose={() => setIsPrinterSelectorOpen(false)}
      />
    </div>
  );
};

export default OrcaSlicerPage;
