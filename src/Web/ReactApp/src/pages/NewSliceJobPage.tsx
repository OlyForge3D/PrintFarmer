import React, { useState, useEffect, Suspense, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { sliceJobService, SubmitSliceJobRequest } from '@/services/sliceJobService';
import slicerProfilesService, { SlicerProfileListItem } from '@/services/slicerProfilesService';
import workersService from '@/services/workersService';
import { slicerRegistry } from '@/services/slicerRegistry';
import { assetService } from '@/services/assetService';
import { WorkerResponse } from '@/types/worker';
import { hasRequiredCapabilities } from '@/types/worker';
import * as signalR from '@microsoft/signalr';
import { getHubUrl, getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';
import { ViewerSkeleton } from '@/components/3d/ViewerSkeleton';

// Lazy load the 3D model viewer for better performance
const ModelViewer3D = React.lazy(() =>
  import('@/components/3d/ModelViewer3D').then(mod => ({ default: mod.ModelViewer }))
);

// Lightweight model DTO interface for picker (subset of Model3DDto)
interface ModelListItem {
  id: string;
  fileName: string;
  originalFileName: string;
  fileFormat: number;
  uploadedAt: string;
  filePath?: string;
}

import { PageTemplate } from '@/components/PageTemplate';
import { Button } from '@/components/ui/Button';
import { Alert } from '@/components/ui/Alert';
import { FormField } from '@/components/ui/FormField';
import { Input } from '@/components/ui/Input';
import { Select } from '@/components/ui/Select';
import { Layers } from 'lucide-react';
import { useAuth } from '@/contexts/AuthHooks';

// Material/Filament type and temperature presets
type MaterialType = 'PLA' | 'PETG' | 'ABS' | 'TPU' | 'Nylon' | 'Carbon' | 'Other';
interface MaterialPreset {
  name: MaterialType;
  nozzleTemp: number;
  bedTemp: number;
}

const MATERIAL_PRESETS: Record<MaterialType, MaterialPreset> = {
  'PLA': { name: 'PLA', nozzleTemp: 210, bedTemp: 60 },
  'PETG': { name: 'PETG', nozzleTemp: 240, bedTemp: 80 },
  'ABS': { name: 'ABS', nozzleTemp: 245, bedTemp: 100 },
  'TPU': { name: 'TPU', nozzleTemp: 225, bedTemp: 60 },
  'Nylon': { name: 'Nylon', nozzleTemp: 260, bedTemp: 80 },
  'Carbon': { name: 'Carbon', nozzleTemp: 250, bedTemp: 90 },
  'Other': { name: 'Other', nozzleTemp: 220, bedTemp: 60 }
};

export const NewSliceJobPage: React.FC = () => {
  const { user } = useAuth();
  const qc = useQueryClient();
  const [searchParams] = useSearchParams();
  const modelIdFromUrl = searchParams.get('modelId') || '';

  // Initialize asset service on component mount
  useEffect(() => {
    assetService.initialize().catch(err => console.error('Failed to initialize asset service:', err));
  }, []);

  // === Main Sidebar Controls ===
  const [selectedSlicerId, setSelectedSlicerId] = useState<number>(1);
  const [selectedPrinterId, setSelectedPrinterId] = useState<string>('');
  const [printerSearchText, setPrinterSearchText] = useState('');
  const [selectedFilamentMaterial, setSelectedFilamentMaterial] = useState<MaterialType>('PLA');
  const [selectedProcessPresetId, setSelectedProcessPresetId] = useState<string>('');
  const [showAdvancedSettings, setShowAdvancedSettings] = useState(false);
  const [activeSettingsTab, setActiveSettingsTab] = useState<'quality' | 'strength' | 'speed' | 'support' | 'material' | 'other'>('quality');

  // === Custom Settings ===
  const [customSettings, setCustomSettings] = useState({
    layerHeight: 0.2,
    infill: 20,
    printSpeed: 120,
    wallThickness: 1.2,
    nozzleTemp: MATERIAL_PRESETS.PLA.nozzleTemp,
    bedTemp: MATERIAL_PRESETS.PLA.bedTemp,
    enableSupports: false,
    supportDensity: 15,
    supportPattern: 'linear',
    topLayerCount: 4,
    bottomLayerCount: 4,
    travelSpeed: 150,
    topSurfaceFinish: 'standard',
  });

  // === Model Selection ===
  const [modelFileUrl, setModelFileUrl] = useState('');
  const [modelFileName, setModelFileName] = useState('');
  const [useModelPicker, setUseModelPicker] = useState(true);
  const [selectedModelId, setSelectedModelId] = useState<string>(modelIdFromUrl);
  const [useProfile, setUseProfile] = useState(true);
  const [selectedProfileId, setSelectedProfileId] = useState<string>('');
  const [rawProfileJson, setRawProfileJson] = useState('');
  const [requiredCapabilitiesJson, setRequiredCapabilitiesJson] = useState('[]');
  const [capabilitiesError, setCapabilitiesError] = useState<string | null>(null);
  const [parsedCapabilities, setParsedCapabilities] = useState<string[]>([]);
  const [priority, setPriority] = useState(1);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

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
    refetchInterval: 15_000,
  });

  // Slicer info with version
  const slicerInfo = useMemo(() => {
    const slicer = availableSlicers.find(s => s.slicerType === (selectedSlicerId === 1 ? 'PrusaSlicer' : 'OrcaSlicer'));
    return {
      name: slicer?.name || (selectedSlicerId === 1 ? 'PrusaSlicer' : 'OrcaSlicer'),
      version: slicer?.version || 'Unknown',
      engine: selectedSlicerId
    };
  }, [selectedSlicerId, availableSlicers]);

  const engineOptions = useMemo(() => {
    return availableSlicers.map(slicer => ({
      label: `${slicer.name || slicer.slicerType || 'Unknown'} v${slicer.version || '?'}`,
      value: slicer.slicerType === 'PrusaSlicer' ? 1 : slicer.slicerType === 'OrcaSlicer' ? 2 : 0
    }));
  }, [availableSlicers]);

  // Auto-select first available worker based on capabilities (for system use)
  // selectedWorkerId is auto-selected by the backend based on capabilities
  useMemo(() => {
    if (parsedCapabilities.length === 0) {
      return availableWorkers[0]?.id;
    }
    const compatible = availableWorkers.find(w => hasRequiredCapabilities(w, parsedCapabilities));
    return compatible?.id || availableWorkers[0]?.id;
  }, [availableWorkers, parsedCapabilities]);

  // Fetch printers for dropdown
  const { data: printers = [] } = useQuery({
    queryKey: ['printers'],
    queryFn: async () => {
      const baseUrl = getApiBaseUrl();
      const res = await fetch(`${baseUrl}/printers`, { headers: getAuthHeaders() });
      if (!res.ok) throw new Error('Failed to load printers');
      return res.json() as Promise<Array<{ id: string; name: string; model?: string; modelId?: string; modelMaxX?: number; modelMaxY?: number; modelMaxZ?: number }>>;
    },
    staleTime: 30_000
  });

  // Get selected printer's bed dimensions
  const selectedPrinter = useMemo(() => {
    return printers.find(p => p.id === selectedPrinterId);
  }, [printers, selectedPrinterId]);

  const bedDimensions = useMemo(() => {
    if (!selectedPrinter?.modelMaxX || !selectedPrinter?.modelMaxY) {
      return undefined;
    }
    return {
      width: selectedPrinter.modelMaxX,
      depth: selectedPrinter.modelMaxY,
      height: selectedPrinter.modelMaxZ || 0.5
    };
  }, [selectedPrinter]);

  // Get bed texture for the selected printer
  const bedTextureInfo = useMemo(() => {
    if (!selectedPrinter?.model) {
      return { url: undefined, format: undefined };
    }

    // Try to find asset by printer model name
    // First try to parse manufacturer from printer data if available
    const asset = assetService.searchPrinters(selectedPrinter.model)[0];

    if (asset?.bedTexture) {
      return {
        url: asset.bedTexture,
        format: asset.bedTextureFormat as 'svg' | 'png' | undefined
      };
    }

    return { url: undefined, format: undefined };
  }, [selectedPrinter?.model]);

  // Filter printers by search text
  const filteredPrinters = useMemo(() => {
    if (!printerSearchText.trim()) return printers;
    const search = printerSearchText.toLowerCase();
    return printers.filter(p => p.name.toLowerCase().includes(search) || p.model?.toLowerCase().includes(search));
  }, [printers, printerSearchText]);

  // Fetch process profiles - filter by selected printer
  const { data: profiles = [] } = useQuery<SlicerProfileListItem[], Error>({
    queryKey: ['slicerProfilesExtended', selectedPrinterId],
    queryFn: () => slicerProfilesService.listExtended(),
    staleTime: 15_000
  });

  // Filter profiles for the selected printer
  const printerProcessProfiles = useMemo(() => {
    // Return all profiles - filtering can be done based on availability
    return profiles;
  }, [profiles]);

  // Filament profiles - combination of slicer profiles + custom for printer
  const filamentProfiles = useMemo(() => {
    return MATERIAL_PRESETS;
  }, []);

  // Fetch models for picker
  const { data: models = [], error: modelsError } = useQuery<ModelListItem[], Error>({
    queryKey: ['modelsListBasic'],
    queryFn: async () => {
      const baseUrl = import.meta.env.VITE_API_BASE_URL as string | undefined;
      const apiBase = !baseUrl || baseUrl.trim() === '' ? '/api' : baseUrl;
      const token = localStorage.getItem('auth-token');
      const headers: HeadersInit = { 'Content-Type': 'application/json' };
      if (token) headers['Authorization'] = `Bearer ${token}`;

      const res = await fetch(`${apiBase}/3d-models`, { headers });
      if (!res.ok) throw new Error(await res.text() || 'Failed to load models');
      const json = await res.json();
      return (json as unknown[]).map(obj => {
        const m = obj as { id: string; fileName?: string; displayName?: string; originalFileName?: string; fileFormat?: number; uploadedAt?: string; uploadedAtUtc?: string };
        return {
          id: m.id,
          fileName: m.fileName || m.displayName || m.originalFileName || 'model',
          originalFileName: m.originalFileName || m.fileName || m.displayName || 'model',
          fileFormat: m.fileFormat ?? 0,
          uploadedAt: m.uploadedAt ?? m.uploadedAtUtc ?? ''
        } as ModelListItem;
      });
    },
    staleTime: 20_000
  });

  // Connect to SlicerHub for real-time updates
  useEffect(() => {
    try {
      if (!signalR || typeof signalR.HubConnectionBuilder !== 'function') {
        return;
      }

      const builder = new signalR.HubConnectionBuilder();
      if (!builder || typeof builder.withUrl !== 'function') {
        return;
      }

      const hubConnection = builder
        .withUrl(getHubUrl('/hubs/slicer-registry'))
        .withAutomaticReconnect()
        .build();

      hubConnection.on('SlicerRegistered', () => {
        qc.invalidateQueries({ queryKey: ['workers-available'] });
      });

      hubConnection.on('SlicerHeartbeat', () => {
        qc.invalidateQueries({ queryKey: ['workers-available'] });
      });

      hubConnection.on('SlicerDeregistered', () => {
        qc.invalidateQueries({ queryKey: ['workers-available'] });
      });

      hubConnection
        .start()
        .catch(err => {
          console.error('Failed to connect to SlicerHub:', err);
        });

      return () => {
        hubConnection.stop();
      };
    } catch {
      return;
    }
  }, [qc]);

  // Persist selections
  useEffect(() => {
    try {
      const savedCaps = localStorage.getItem('sliceJob.requiredCapabilities');
      if (savedCaps) setRequiredCapabilitiesJson(savedCaps);
      const savedProfileId = localStorage.getItem('sliceJob.selectedProfileId');
      if (savedProfileId) setSelectedProfileId(savedProfileId);
    } catch { /* ignore */ }
  }, []);

  useEffect(() => {
    try { localStorage.setItem('sliceJob.requiredCapabilities', requiredCapabilitiesJson); } catch { /* ignore */ }
  }, [requiredCapabilitiesJson]);

  useEffect(() => {
    try {
      if (selectedProfileId) localStorage.setItem('sliceJob.selectedProfileId', selectedProfileId);
      else localStorage.removeItem('sliceJob.selectedProfileId');
    } catch { /* ignore */ }
  }, [selectedProfileId]);

  // Derive model file URL when selected
  useEffect(() => {
    if (useModelPicker && selectedModelId) {
      const baseUrl = import.meta.env.VITE_API_BASE_URL as string | undefined;
      const apiBase = !baseUrl || baseUrl.trim() === '' ? '/api' : baseUrl;
      setModelFileUrl(`${apiBase}/3d-models/${selectedModelId}/file`);
      const mdl = models?.find(m => m.id === selectedModelId);
      if (mdl) {
        setModelFileName(mdl.fileName || mdl.originalFileName);
      }
    }
  }, [useModelPicker, selectedModelId, models]);

  // Capabilities JSON validation
  useEffect(() => {
    const text = requiredCapabilitiesJson.trim();
    if (!text) {
      setParsedCapabilities([]);
      setCapabilitiesError(null);
      return;
    }
    try {
      const parsed = JSON.parse(text);
      if (!Array.isArray(parsed)) {
        setCapabilitiesError('Capabilities JSON must be an array');
        setParsedCapabilities([]);
      } else if (!parsed.every(x => typeof x === 'string')) {
        setCapabilitiesError('All capability entries must be strings');
        setParsedCapabilities([]);
      } else {
        setCapabilitiesError(null);
        setParsedCapabilities(parsed as string[]);
      }
    } catch {
      setCapabilitiesError('Invalid JSON syntax');
      setParsedCapabilities([]);
    }
  }, [requiredCapabilitiesJson]);

  // Update temps when filament changes
  const applyFilamentMaterial = (material: MaterialType) => {
    setSelectedFilamentMaterial(material);
    setCustomSettings(prev => ({
      ...prev,
      nozzleTemp: MATERIAL_PRESETS[material].nozzleTemp,
      bedTemp: MATERIAL_PRESETS[material].bedTemp,
    }));
  };

  const submitMutation = useMutation({
    mutationFn: async (req: SubmitSliceJobRequest) => sliceJobService.submitJob(req),
    onSuccess: (res) => {
      setMessage(`Job queued (id ${res.jobId.substring(0, 8)}) position ${res.queuePosition}`);
      setError(null);
      setModelFileUrl('');
      setModelFileName('');
      setRawProfileJson('');
      setSelectedProfileId('');
      qc.invalidateQueries({ queryKey: ['slice-jobs-my'] });
    },
    onError: (err: unknown) => {
      setError(err instanceof Error ? err.message : 'Failed to submit job');
    }
  });

  const onSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    if (useModelPicker) {
      if (!selectedModelId) {
        setError('Select a model or switch to manual URL mode');
        return;
      }
    } else {
      if (!modelFileUrl.trim()) {
        setError('Model file URL is required');
        return;
      }
      if (!modelFileName.trim()) {
        setError('Model file name is required');
        return;
      }
    }

    if (useProfile && !selectedProfileId) {
      setError('Select a profile or switch to raw JSON mode');
      return;
    }
    if (!useProfile && !rawProfileJson.trim()) {
      setError('Provide raw profile JSON or switch to profile mode');
      return;
    }

    if (capabilitiesError) {
      setError('Fix capabilities JSON errors before submitting');
      return;
    }

    const capabilities = parsedCapabilities.length > 0 ? JSON.stringify(parsedCapabilities) : '[]';

    const request: SubmitSliceJobRequest = {
      userId: user?.id || '',
      printerId: undefined,
      modelFileUrl: modelFileUrl,
      modelFileName: modelFileName,
      slicerEngine: slicerInfo.engine,
      slicerProfileJson: useProfile ? '{}' : rawProfileJson,
      slicerProfileId: useProfile ? selectedProfileId : undefined,
      requiredCapabilitiesJson: capabilities,
      priority
    };

    submitMutation.mutate(request);
  };

  const getFileType = (): 'stl' | '3mf' | 'obj' | 'ply' => {
    if (modelFileName) {
      const ext = modelFileName.split('.').pop()?.toLowerCase();
      if (ext === '3mf') return '3mf';
      if (ext === 'obj') return 'obj';
      if (ext === 'ply') return 'ply';
    }
    return 'stl';
  };

  return (
    <PageTemplate
      title="New Slice Job"
      subtitle="OrcaSlicer-style distributed slicing"
      icon={Layers}
      maxWidth="max-w-7xl"
    >
      <form onSubmit={onSubmit} className="flex flex-col lg:flex-row gap-6 h-full">
        {/* LEFT SIDEBAR: OrcaSlicer Menu */}
        <div className="w-full lg:w-96 space-y-4 flex-shrink-0 pb-4 max-h-screen overflow-y-auto">

          {/* SLICER SELECTION - Shows name and version */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-4">
            <label className="block text-sm font-semibold text-pf-text mb-2">Slicer</label>
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

          {/* PRINTER SELECTION with Search */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-4">
            <label className="block text-sm font-semibold text-pf-text mb-2">Printer</label>
            <Input
              type="text"
              placeholder="Search printers..."
              value={printerSearchText}
              onChange={e => setPrinterSearchText(e.target.value)}
              className="mb-2 text-sm"
            />
            <Select
              value={selectedPrinterId}
              onChange={e => setSelectedPrinterId(e.target.value)}
              className="w-full"
            >
              <option value="">-- Select Printer --</option>
              {filteredPrinters.map(p => (
                <option key={p.id} value={p.id}>{p.name} {p.model ? `(${p.model})` : ''}</option>
              ))}
            </Select>
          </div>

          {/* FILAMENT / MATERIAL PROFILE - Shows slicer + custom profiles */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-4">
            <label className="block text-sm font-semibold text-pf-text mb-2">Filament</label>
            <Select
              value={selectedFilamentMaterial}
              onChange={e => applyFilamentMaterial(e.target.value as MaterialType)}
              className="w-full"
            >
              {Object.keys(filamentProfiles).map(m => (
                <option key={m} value={m}>{m}</option>
              ))}
            </Select>
            <div className="text-xs text-pf-text-muted mt-2">
              {MATERIAL_PRESETS[selectedFilamentMaterial].nozzleTemp}°C nozzle, {MATERIAL_PRESETS[selectedFilamentMaterial].bedTemp}°C bed
            </div>
          </div>

          {/* PROCESS PRESETS - Only for selected printer, with Advanced toggle */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-4">
            <div className="flex items-center justify-between mb-3">
              <label className="block text-sm font-semibold text-pf-text">Process</label>
              <div className="flex items-center gap-2">
                <span className="text-xs font-medium text-pf-text">Advanced</span>
                <button
                  type="button"
                  onClick={() => setShowAdvancedSettings(!showAdvancedSettings)}
                  className={`w-10 h-6 rounded-full transition-colors flex items-center ${showAdvancedSettings
                    ? 'bg-pf-accent'
                    : 'bg-pf-border'
                    }`}
                  title="Toggle advanced settings"
                >
                  <div className={`w-5 h-5 rounded-full bg-white transition-transform ${showAdvancedSettings
                    ? 'translate-x-4'
                    : 'translate-x-0.5'
                    }`} />
                </button>
              </div>
            </div>

            {/* Process Presets Dropdown - Filtered by printer */}
            <Select
              value={selectedProcessPresetId}
              onChange={e => setSelectedProcessPresetId(e.target.value)}
              className="w-full mb-3"
            >
              <option value="">-- Select Process Profile --</option>
              {printerProcessProfiles.map(p => (
                <option key={p.id} value={p.id}>{p.name}</option>
              ))}
            </Select>

            {/* Advanced Settings - Only shown if Advanced toggle is ON */}
            {showAdvancedSettings && (
              <>
                {/* Settings Tabs */}
                <div className="flex gap-1 border-b border-pf-border mb-3 text-xs">
                  {(['quality', 'strength', 'speed', 'support', 'material', 'other'] as const).map(tab => (
                    <button
                      key={tab}
                      type="button"
                      onClick={() => setActiveSettingsTab(tab)}
                      className={`pb-2 px-2 transition-colors capitalize ${activeSettingsTab === tab
                        ? 'border-b-2 border-pf-accent text-pf-accent font-medium'
                        : 'text-pf-text-muted hover:text-pf-text'
                        }`}
                    >
                      {tab}
                    </button>
                  ))}
                </div>

                {/* Settings Panel Content */}
                <div className="space-y-3 text-sm">
                  {activeSettingsTab === 'quality' && (
                    <>
                      <div>
                        <label className="block text-xs font-medium text-pf-text mb-1">
                          Layer Height: {customSettings.layerHeight.toFixed(2)}mm
                        </label>
                        <input
                          type="range"
                          min="0.08"
                          max="0.4"
                          step="0.04"
                          value={customSettings.layerHeight}
                          onChange={e => setCustomSettings(prev => ({ ...prev, layerHeight: parseFloat(e.target.value) }))}
                          className="w-full h-2 bg-pf-border rounded cursor-pointer"
                          title="Layer Height"
                        />
                      </div>
                      <div>
                        <label className="block text-xs font-medium text-pf-text mb-1">
                          Wall Thickness: {customSettings.wallThickness.toFixed(1)}mm
                        </label>
                        <input
                          type="range"
                          min="0.8"
                          max="2.4"
                          step="0.2"
                          value={customSettings.wallThickness}
                          onChange={e => setCustomSettings(prev => ({ ...prev, wallThickness: parseFloat(e.target.value) }))}
                          className="w-full h-2 bg-pf-border rounded cursor-pointer"
                          title="Wall Thickness"
                        />
                      </div>
                    </>
                  )}

                  {activeSettingsTab === 'strength' && (
                    <>
                      <div>
                        <label className="block text-xs font-medium text-pf-text mb-1">
                          Infill: {customSettings.infill}%
                        </label>
                        <input
                          type="range"
                          min="0"
                          max="100"
                          step="5"
                          value={customSettings.infill}
                          onChange={e => setCustomSettings(prev => ({ ...prev, infill: parseInt(e.target.value) }))}
                          className="w-full h-2 bg-pf-border rounded cursor-pointer"
                          title="Infill Percentage"
                        />
                      </div>
                    </>
                  )}

                  {activeSettingsTab === 'speed' && (
                    <>
                      <div>
                        <label className="block text-xs font-medium text-pf-text mb-1">
                          Print Speed: {customSettings.printSpeed}mm/s
                        </label>
                        <input
                          type="range"
                          min="20"
                          max="200"
                          step="10"
                          value={customSettings.printSpeed}
                          onChange={e => setCustomSettings(prev => ({ ...prev, printSpeed: parseInt(e.target.value) }))}
                          className="w-full h-2 bg-pf-border rounded cursor-pointer"
                          title="Print Speed"
                        />
                      </div>
                      <div>
                        <label className="block text-xs font-medium text-pf-text mb-1">
                          Travel Speed: {customSettings.travelSpeed}mm/s
                        </label>
                        <input
                          type="range"
                          min="100"
                          max="300"
                          step="10"
                          value={customSettings.travelSpeed}
                          onChange={e => setCustomSettings(prev => ({ ...prev, travelSpeed: parseInt(e.target.value) }))}
                          className="w-full h-2 bg-pf-border rounded cursor-pointer"
                          title="Travel Speed"
                        />
                      </div>
                    </>
                  )}

                  {activeSettingsTab === 'support' && (
                    <>
                      <label className="flex items-center gap-2 text-sm">
                        <input
                          type="checkbox"
                          checked={customSettings.enableSupports}
                          onChange={e => setCustomSettings(prev => ({ ...prev, enableSupports: e.target.checked }))}
                          title="Enable Supports"
                        />
                        <span>Enable Supports</span>
                      </label>
                      {customSettings.enableSupports && (
                        <>
                          <div>
                            <label className="block text-xs font-medium text-pf-text mb-1">
                              Density: {customSettings.supportDensity}%
                            </label>
                            <input
                              type="range"
                              min="5"
                              max="50"
                              step="5"
                              value={customSettings.supportDensity}
                              onChange={e => setCustomSettings(prev => ({ ...prev, supportDensity: parseInt(e.target.value) }))}
                              className="w-full h-2 bg-pf-border rounded cursor-pointer"
                              title="Support Density"
                            />
                          </div>
                          <div>
                            <label className="block text-xs font-medium text-pf-text mb-1">Pattern</label>
                            <Select
                              value={customSettings.supportPattern}
                              onChange={e => {
                                const value = e.target.value;
                                if (value === 'linear' || value === 'grid' || value === 'honeycomb') {
                                  setCustomSettings(prev => ({ ...prev, supportPattern: value }));
                                }
                              }}
                              className="w-full text-xs"
                            >
                              <option value="linear">Linear</option>
                              <option value="grid">Grid</option>
                              <option value="honeycomb">Honeycomb</option>
                            </Select>
                          </div>
                        </>
                      )}
                    </>
                  )}

                  {activeSettingsTab === 'material' && (
                    <>
                      <div>
                        <label className="block text-xs font-medium text-pf-text mb-1">
                          Nozzle: {customSettings.nozzleTemp}°C
                        </label>
                        <input
                          type="range"
                          min="190"
                          max="280"
                          step="5"
                          value={customSettings.nozzleTemp}
                          onChange={e => setCustomSettings(prev => ({ ...prev, nozzleTemp: parseInt(e.target.value) }))}
                          className="w-full h-2 bg-pf-border rounded cursor-pointer"
                          title="Nozzle Temperature"
                        />
                      </div>
                      <div>
                        <label className="block text-xs font-medium text-pf-text mb-1">
                          Bed: {customSettings.bedTemp}°C
                        </label>
                        <input
                          type="range"
                          min="20"
                          max="120"
                          step="5"
                          value={customSettings.bedTemp}
                          onChange={e => setCustomSettings(prev => ({ ...prev, bedTemp: parseInt(e.target.value) }))}
                          className="w-full h-2 bg-pf-border rounded cursor-pointer"
                          title="Bed Temperature"
                        />
                      </div>
                    </>
                  )}

                  {activeSettingsTab === 'other' && (
                    <>
                      <div>
                        <label className="block text-xs font-medium text-pf-text mb-1">
                          Top Layers: {customSettings.topLayerCount}
                        </label>
                        <input
                          type="range"
                          min="1"
                          max="10"
                          step="1"
                          value={customSettings.topLayerCount}
                          onChange={e => setCustomSettings(prev => ({ ...prev, topLayerCount: parseInt(e.target.value) }))}
                          className="w-full h-2 bg-pf-border rounded cursor-pointer"
                          title="Top Layer Count"
                        />
                      </div>
                      <div>
                        <label className="block text-xs font-medium text-pf-text mb-1">
                          Bottom Layers: {customSettings.bottomLayerCount}
                        </label>
                        <input
                          type="range"
                          min="1"
                          max="10"
                          step="1"
                          value={customSettings.bottomLayerCount}
                          onChange={e => setCustomSettings(prev => ({ ...prev, bottomLayerCount: parseInt(e.target.value) }))}
                          className="w-full h-2 bg-pf-border rounded cursor-pointer"
                          title="Bottom Layer Count"
                        />
                      </div>
                    </>
                  )}
                </div>
              </>
            )}
          </div>

          {/* MODEL SELECTION - Inline, not collapsible */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-4 space-y-3">
            <label className="block text-sm font-semibold text-pf-text">Model</label>

            <FormField
              label="Use Model Picker"
              helper={useModelPicker ? 'Select from uploaded models' : 'Enter URL manually'}
              inline
            >
              <input
                id="useModelPicker"
                type="checkbox"
                checked={useModelPicker}
                onChange={() => {
                  setUseModelPicker(v => !v);
                  if (useModelPicker) {
                    setSelectedModelId('');
                    setModelFileUrl('');
                    setModelFileName('');
                  }
                }}
                title="Use Model Picker"
              />
            </FormField>

            {useModelPicker ? (
              <FormField label="Model" error={modelsError ? modelsError.message : undefined}>
                {models && models.length > 0 ? (
                  <Select value={selectedModelId} onChange={e => setSelectedModelId(e.target.value)}>
                    <option value="">-- Select model --</option>
                    {models.map(m => (
                      <option key={m.id} value={m.id}>{m.fileName}</option>
                    ))}
                  </Select>
                ) : (
                  <Select disabled className="bg-pf-disabled" title="No models available">
                    <option>-- No models --</option>
                  </Select>
                )}
              </FormField>
            ) : (
              <>
                <FormField label="File URL" required>
                  <Input
                    type="text"
                    value={modelFileUrl}
                    onChange={e => setModelFileUrl(e.target.value)}
                    placeholder="https://... or /storage/..."
                  />
                </FormField>
                <FormField label="File Name" required>
                  <Input
                    type="text"
                    value={modelFileName}
                    onChange={e => setModelFileName(e.target.value)}
                    placeholder="model.stl"
                  />
                </FormField>
              </>
            )}

            {/* Profile Selection */}
            <div className="border-t border-pf-border pt-3">
              <div className="flex gap-3 mb-2">
                <label className="inline-flex items-center gap-2 text-sm">
                  <input type="radio" name="mode" checked={useProfile} onChange={() => setUseProfile(true)} title="Use Profile Mode" />
                  <span>Profile</span>
                </label>
                <label className="inline-flex items-center gap-2 text-sm">
                  <input type="radio" name="mode" checked={!useProfile} onChange={() => setUseProfile(false)} title="Use JSON Mode" />
                  <span>JSON</span>
                </label>
              </div>

              {useProfile ? (
                <FormField label="Profile">
                  {profiles && profiles.length > 0 ? (
                    <Select value={selectedProfileId} onChange={e => setSelectedProfileId(e.target.value)}>
                      <option value="">-- Select --</option>
                      {profiles.map(p => (
                        <option key={p.id} value={p.id}>{p.name} ({p.slicerType})</option>
                      ))}
                    </Select>
                  ) : (
                    <Select disabled className="bg-pf-disabled" title="No profiles available">
                      <option>-- No profiles --</option>
                    </Select>
                  )}
                </FormField>
              ) : (
                <FormField label="Raw JSON">
                  <textarea
                    value={rawProfileJson}
                    onChange={e => setRawProfileJson(e.target.value)}
                    rows={4}
                    className="border rounded p-2 font-mono text-xs w-full bg-pf-panel text-pf-text"
                    placeholder='{"layer_height": 0.2}'
                  />
                </FormField>
              )}

              <FormField label="Priority">
                <Select value={priority} onChange={e => setPriority(Number(e.target.value))}>
                  <option value={0}>Low</option>
                  <option value={1}>Normal</option>
                  <option value={2}>High</option>
                  <option value={3}>Critical</option>
                </Select>
              </FormField>

              <FormField label="Capabilities" error={capabilitiesError || undefined}>
                <textarea
                  value={requiredCapabilitiesJson}
                  onChange={e => setRequiredCapabilitiesJson(e.target.value)}
                  rows={3}
                  className="border rounded p-2 font-mono text-xs w-full bg-pf-panel text-pf-text"
                  placeholder='["orcaslicer"]'
                />
              </FormField>
            </div>
          </div>

          {/* STATUS MESSAGES */}
          {error && <Alert type="error">{error}</Alert>}
          {message && <Alert type="success">{message}</Alert>}

          {/* ACTION BUTTONS */}
          <div className="flex flex-col gap-2 sticky bottom-0 bg-pf-background pt-2 border-t border-pf-border">
            <Button type="submit" loading={submitMutation.isPending} variant="primary" className="w-full">
              Submit Job
            </Button>
            <Button
              type="button"
              variant="secondary"
              className="w-full"
              onClick={() => {
                setModelFileUrl('');
                setModelFileName('');
                setRawProfileJson('');
                setSelectedProfileId('');
                setError(null);
                setMessage(null);
              }}
            >
              Reset
            </Button>
          </div>
        </div>

        {/* RIGHT SIDE: 3D Model Preview */}
        <div className="flex-1 hidden lg:flex flex-col gap-4 min-h-96">
          <div className="card bg-pf-panel border border-pf-border flex-1 overflow-hidden">
            <div className="card-header">
              <h3 className="font-semibold text-pf-text">
                {modelFileName ? `Preview: ${modelFileName}` : 'Model Preview'}
              </h3>
            </div>
            <div className="card-body p-0 flex-1">
              {modelFileUrl ? (
                <Suspense fallback={<ViewerSkeleton variant="model" className="h-full w-full" />}>
                  <ModelViewer3D
                    modelUrl={modelFileUrl}
                    fileType={getFileType()}
                    showGrid={true}
                    showAxes={true}
                    autoRotate={false}
                    className="h-full w-full"
                    bedDimensions={bedDimensions}
                    bedTextureUrl={bedTextureInfo.url}
                    bedTextureFormat={bedTextureInfo.format}
                  />
                </Suspense>
              ) : (
                <div className="h-full w-full flex items-center justify-center text-pf-text-muted">
                  <div className="text-center">
                    <p className="text-sm">Select a model to view 3D preview</p>
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
      </form>
    </PageTemplate>
  );
};

export default NewSliceJobPage;
