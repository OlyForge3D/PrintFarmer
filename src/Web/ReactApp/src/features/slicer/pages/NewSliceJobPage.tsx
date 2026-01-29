import React, { useState, useEffect, Suspense, useMemo, useCallback } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { sliceJobService, SubmitSliceJobRequest } from '@/services/sliceJobService';
import { slicerProfilesService } from '@/services/slicerProfilesService';
import { slicerRegistry } from '@/services/slicerRegistry';
import { assetService } from '@/services/assetService';
import { apiClient } from '@/services/api';
import * as signalR from '@microsoft/signalr';
import { getHubUrl, getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import { ViewerSkeleton } from '@/features/models3d/components/3d/ViewerSkeleton';
import { ProfileSelector } from '@/features/slicer/components/ProfileSelector';
import { CloneProfilesModal } from '@/features/slicer/components/CloneProfilesModal';
import { SlicerSettingsPanel, DEFAULT_BASIC_SETTINGS, type BasicSlicerSettings } from '@/features/slicer/components/settings';
import { PrinterSlicerSelector, type PrinterForSlicing } from '../components/job';
import {
  findHierarchyManufacturer,
  findHierarchyModel,
  getPrimaryNozzleDiameter
} from '../utils/profileMatcher';
import type { MaterialType, MaterialPreset } from '@/types/slicer';
import type { ModelListItem } from '@/types/models';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Alert, FormField, Input, Select, Checkbox, Radio, Textarea } from '@/common/components/ui';
import { LayersIcon, EyeIcon } from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { STLPreviewModal } from '@/features/models3d/components/3d/STLPreviewModal';
import { useSTLFile } from '@/common/hooks/useSTLFile';

// Lazy load the 3D model viewer for better performance
const ModelViewer3D = React.lazy(() =>
  import('@/features/models3d/components/3d/ModelViewer3D').then(mod => ({ default: mod.ModelViewer }))
);

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

  // === Main Sidebar Controls ===
  const [selectedSlicerId, setSelectedSlicerId] = useState<number>(1);
  const [selectedPrinterId, setSelectedPrinterId] = useState<string>('');
  const [selectedFilamentMaterial, setSelectedFilamentMaterial] = useState<MaterialType>('PLA');
  const [selectedProcessPresetId, setSelectedProcessPresetId] = useState<string>('');

  // === Cascading Profile Selection (OrcaSlicer-style) ===
  // Flow: Manufacturer → Printer Model → Machine Profile → Filament/Process filtered by machine
  const [selectedManufacturer, setSelectedManufacturer] = useState<string>('');
  const [selectedPrinterModel, setSelectedPrinterModel] = useState<string>('');
  const [selectedMachineProfileId, setSelectedMachineProfileId] = useState<string>('');
  const [selectedFilamentProfileId, setSelectedFilamentProfileId] = useState<string>('');

  // === OrcaSlicer-style Settings Panel ===
  const [slicerSettings, setSlicerSettings] = useState<BasicSlicerSettings>(DEFAULT_BASIC_SETTINGS);

  // Callback for settings panel changes
  const handleSlicerSettingsChange = useCallback((newSettings: BasicSlicerSettings) => {
    setSlicerSettings(newSettings);
  }, []);

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
  const [isSTLPreviewOpen, setIsSTLPreviewOpen] = useState(false);
  const [isCloneProfilesModalOpen, setIsCloneProfilesModalOpen] = useState(false);
  const stlFile = useSTLFile();

  // === Queries ===
  const { data: availableSlicers = [] } = useQuery({
    queryKey: ['slicers-available'],
    queryFn: () => slicerRegistry.getSlicers(),
    staleTime: 10_000,
    refetchInterval: 15_000,
  });

  // Map slicer type enum to display names
  // API returns slicerType as number: 0=Unknown, 1=OrcaSlicer, 2=PrusaSlicer
  const getSlicerTypeName = useCallback((slicerType: string | number | undefined): string => {
    if (slicerType === 1 || slicerType === 'OrcaSlicer') return 'OrcaSlicer';
    if (slicerType === 2 || slicerType === 'PrusaSlicer') return 'PrusaSlicer';
    return 'Unknown';
  }, []);

  // Slicer info with version - shows slicer type name, not worker name
  const slicerInfo = useMemo(() => {
    const slicer = availableSlicers.find(s => {
      const typeName = getSlicerTypeName(s.slicerType);
      return (selectedSlicerId === 1 && typeName === 'OrcaSlicer') ||
             (selectedSlicerId === 2 && typeName === 'PrusaSlicer');
    });
    const typeName = selectedSlicerId === 1 ? 'OrcaSlicer' : 'PrusaSlicer';
    return {
      name: typeName,
      version: slicer?.version || 'Unknown',
      engine: selectedSlicerId
    };
  }, [selectedSlicerId, availableSlicers, getSlicerTypeName]);

  // Deduplicate slicers by type and show type name (not worker name)
  const engineOptions = useMemo(() => {
    const seenTypes = new Set<string>();
    const options: { label: string; value: number }[] = [];
    
    for (const slicer of availableSlicers) {
      const typeName = getSlicerTypeName(slicer.slicerType);
      if (typeName !== 'Unknown' && !seenTypes.has(typeName)) {
        seenTypes.add(typeName);
        options.push({
          label: `${typeName} ${slicer.version || ''}`.trim(),
          value: typeName === 'OrcaSlicer' ? 1 : 2
        });
      }
    }
    
    // If no slicers available, show defaults
    if (options.length === 0) {
      return [
        { label: 'OrcaSlicer', value: 1 },
        { label: 'PrusaSlicer', value: 2 }
      ];
    }
    
    return options;
  }, [availableSlicers, getSlicerTypeName]);

  // Fetch printers for dropdown - includes data needed for profile matching
  const { data: printers = [], isLoading: isPrintersLoading } = useQuery({
    queryKey: ['printers'],
    queryFn: async () => {
      const printerList = await apiClient.getPrinters();
      // Map to PrinterForSlicing format (basic Printer type - no nozzle/toolhead data)
      return printerList.map(p => ({
        id: p.id,
        name: p.name,
        manufacturerId: p.manufacturerId,
        manufacturerName: p.manufacturerName,
        modelId: p.modelId,
        modelName: p.modelName,
        thumbnailUrl: p.thumbnailUrl,
        isOnline: p.isOnline
      })) as PrinterForSlicing[];
    },
    staleTime: 30_000
  });

  // Fetch full printer details including bed dimensions and toolheads when a printer is selected
  const { data: selectedPrinterDetails } = useQuery({
    queryKey: ['printerDetails', selectedPrinterId],
    queryFn: async () => {
      if (!selectedPrinterId) return null;
      const details = await apiClient.getPrinterDetails(selectedPrinterId);
      return details;
    },
    enabled: !!selectedPrinterId,
    staleTime: 30_000
  });

  // Merge basic printer info with detailed info including toolheads
  const selectedPrinterForSlicing = useMemo((): PrinterForSlicing | undefined => {
    const basic = printers.find(p => p.id === selectedPrinterId);
    if (!basic) return undefined;
    
    // Merge with details to get toolheads and nozzle info
    return {
      ...basic,
      toolheads: selectedPrinterDetails?.toolheads,
      // Get nozzle from primary toolhead if available
      nozzleDiameter: selectedPrinterDetails?.toolheads?.[0]?.nozzleDiameter
    };
  }, [printers, selectedPrinterId, selectedPrinterDetails]);

  // Get selected printer basic info from list
  const selectedPrinter = useMemo(() => {
    return printers.find(p => p.id === selectedPrinterId);
  }, [printers, selectedPrinterId]);

  // Use detailed info if available, fall back to basic
  const selectedPrinterWithDetails = useMemo(() => {
    return selectedPrinterDetails || selectedPrinter;
  }, [selectedPrinterDetails, selectedPrinter]);

  const bedDimensions = useMemo(() => {
    if (!selectedPrinterWithDetails || !('modelMaxX' in selectedPrinterWithDetails) || !selectedPrinterWithDetails.modelMaxX || !selectedPrinterWithDetails.modelMaxY) {
      return undefined;
    }
    const detailedPrinter = selectedPrinterWithDetails as { modelMaxX: number; modelMaxY: number; modelMaxZ?: number };
    return {
      width: detailedPrinter.modelMaxX,
      depth: detailedPrinter.modelMaxY,
      height: detailedPrinter.modelMaxZ || 0.5
    };
  }, [selectedPrinterWithDetails]);
  // Get bed texture for the selected printer
  const bedTextureInfo = useMemo(() => {
    if (!selectedPrinterWithDetails?.manufacturerName || !selectedPrinterWithDetails?.modelName) {
      return { url: undefined, format: undefined };
    }

    // Look up asset by manufacturer and model name from local asset service
    const asset = assetService.getAsset(selectedPrinterWithDetails.manufacturerName, selectedPrinterWithDetails.modelName);

    if (asset?.bedTexture) {
      return {
        url: asset.bedTexture,
        format: asset.bedTextureFormat as 'svg' | 'png' | undefined
      };
    }

    // If local asset service doesn't have it, return undefined
    // Don't use API fallback as it may return 404 and cause TextureLoader errors
    return { url: undefined, format: undefined };
  }, [selectedPrinterWithDetails?.manufacturerName, selectedPrinterWithDetails?.modelName]);

  // Fetch process profiles using React Query
  const { data: processProfilesData } = useQuery({
    queryKey: ['slicerProfiles'],
    queryFn: () => slicerProfilesService.listExtended(),
    staleTime: 15_000
  });

  // Fetch hierarchical profiles for ProfileSelector component
  const { data: hierarchyProfiles } = useQuery({
    queryKey: ['slicerProfilesHierarchy'],
    queryFn: () => slicerProfilesService.listHierarchical(),
    staleTime: 15_000
  });

  // === Cascading Profile Selection Computed Values ===
  // Note: Manufacturer and Model are now derived from selected printer via auto-matching effect

  // Get machine profiles for selected printer model
  const availableMachineProfiles = useMemo(() => {
    if (!hierarchyProfiles?.byHierarchy || !selectedManufacturer || !selectedPrinterModel) return [];
    const mfgData = hierarchyProfiles.byHierarchy[selectedManufacturer];
    const modelData = mfgData?.models?.[selectedPrinterModel];
    return modelData?.machineProfiles ?? [];
  }, [hierarchyProfiles, selectedManufacturer, selectedPrinterModel]);

  // Get filament profiles for selected printer model (filtered by compatiblePrinters)
  const availableFilamentProfiles = useMemo(() => {
    if (!hierarchyProfiles?.byHierarchy || !selectedManufacturer || !selectedPrinterModel) return [];
    const mfgData = hierarchyProfiles.byHierarchy[selectedManufacturer];
    const modelData = mfgData?.models?.[selectedPrinterModel];
    return modelData?.filamentProfiles ?? [];
  }, [hierarchyProfiles, selectedManufacturer, selectedPrinterModel]);

  // Get process profiles for selected printer model (filtered by compatiblePrinters)
  const availableProcessProfiles = useMemo(() => {
    if (!hierarchyProfiles?.byHierarchy || !selectedManufacturer || !selectedPrinterModel) return [];
    const mfgData = hierarchyProfiles.byHierarchy[selectedManufacturer];
    const modelData = mfgData?.models?.[selectedPrinterModel];
    return modelData?.processProfiles ?? [];
  }, [hierarchyProfiles, selectedManufacturer, selectedPrinterModel]);

  // Note: Previous cascading reset effects were removed because they conflicted
  // with the printer-first flow. The auto-match effect now handles setting all
  // values atomically when a printer is selected, so we don't need to reset
  // child selections when parent changes.

  // Auto-match slicer profiles when a printer is selected (printer-first flow)
  // This effect sets manufacturer, model, and machine profile all at once based on the selected printer
  useEffect(() => {
    if (!selectedPrinterForSlicing || !hierarchyProfiles?.byHierarchy) return;
    
    const mfgName = selectedPrinterForSlicing.manufacturerName;
    const modelName = selectedPrinterForSlicing.modelName;
    
    if (!mfgName || !modelName) {
      // Clear selections if printer has no manufacturer/model info
      setSelectedManufacturer('');
      setSelectedPrinterModel('');
      setSelectedMachineProfileId('');
      return;
    }
    
    // Find matching manufacturer in hierarchy
    const hierarchyMfrs = Object.keys(hierarchyProfiles.byHierarchy);
    const matchedMfr = findHierarchyManufacturer(mfgName, hierarchyMfrs);
    
    if (!matchedMfr) {
      // No matching manufacturer in slicer profiles
      setSelectedManufacturer('');
      setSelectedPrinterModel('');
      setSelectedMachineProfileId('');
      return;
    }
    
    // Set manufacturer
    setSelectedManufacturer(matchedMfr);
    
    // Find matching model in hierarchy
    // Note: models are keyed by GUID, but have a 'name' property with the actual model name
    const mfgData = hierarchyProfiles.byHierarchy[matchedMfr];
    const matchedModel = findHierarchyModel(modelName, mfgData?.models);
    
    if (!matchedModel) {
      // No matching model in slicer profiles
      setSelectedPrinterModel('');
      setSelectedMachineProfileId('');
      return;
    }
    
    // Set model
    setSelectedPrinterModel(matchedModel);
    
    // Auto-match machine profile by nozzle diameter
    const machineProfiles = mfgData?.models?.[matchedModel]?.machineProfiles ?? [];
    const nozzle = getPrimaryNozzleDiameter(selectedPrinterForSlicing);
    
    if (machineProfiles.length === 0) {
      setSelectedMachineProfileId('');
      return;
    }
    
    // Find profile with matching nozzle diameter
    if (nozzle) {
      const nozzleTolerance = 0.01;
      const matchedProfile = machineProfiles.find(p => 
        p.nozzleDiameter && Math.abs(p.nozzleDiameter - nozzle) < nozzleTolerance
      );
      if (matchedProfile) {
        setSelectedMachineProfileId(matchedProfile.id);
        return;
      }
    }
    
    // Default to first profile if no nozzle match
    setSelectedMachineProfileId(machineProfiles[0].id);
  }, [selectedPrinterForSlicing, hierarchyProfiles]);

  // Filter profiles for the selected printer
  const printerProcessProfiles = useMemo(() => {
    // Return all process profiles from the extended response
    return processProfilesData?.processProfiles ?? [];
  }, [processProfilesData]);

  // Check if printer has no profiles - show clone suggestion
  const shouldSuggestCloneProfiles = useMemo(() => {
    return selectedPrinterId && printerProcessProfiles.length === 0;
  }, [selectedPrinterId, printerProcessProfiles.length]);

  // Auto-open clone profiles modal if printer selected but has no profiles
  useEffect(() => {
    if (shouldSuggestCloneProfiles && !isCloneProfilesModalOpen) {
      const timer = setTimeout(() => {
        setIsCloneProfilesModalOpen(true);
      }, 300);
      return () => clearTimeout(timer);
    }
  }, [shouldSuggestCloneProfiles, isCloneProfilesModalOpen]);

  // Machine profiles for profile selection
  const machineProfiles = useMemo(() => {
    return processProfilesData?.machineProfiles ?? [];
  }, [processProfilesData]);

  // Filament profiles - combination of slicer profiles + custom for printer
  const filamentProfiles = useMemo(() => {
    return MATERIAL_PRESETS;
  }, []);

  // Fetch models for picker
  const { data: models = [], error: modelsError } = useQuery<ModelListItem[], Error>({
    queryKey: ['modelsListBasic'],
    queryFn: async () => {
      const response = await apiClient.get<unknown[]>('/3d-models');
      return response.data.map(obj => {
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
      const apiBase = getApiBaseUrl();
      setModelFileUrl(`${apiBase}/3d-models/file/${selectedModelId}`);
      const mdl = models?.find(m => m.id === selectedModelId);
      if (mdl) {
        setModelFileName(mdl.originalFileName || mdl.fileName);
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
    setSlicerSettings(prev => ({
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
      icon={LayersIcon}
      showHeader={false}
      padding="p-2"
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

          {/* PRINTER SELECTION - Select from registered printers first */}
          <PrinterSlicerSelector
            printers={printers}
            isLoading={isPrintersLoading}
            selectedPrinterId={selectedPrinterId}
            onPrinterChange={(printerId) => {
              setSelectedPrinterId(printerId);
              // Auto-match will happen via the effect above
            }}
            className="bg-pf-panel border border-pf-border rounded-lg p-4"
          />

          {/* MACHINE PROFILE SELECTION - Filtered by selected printer */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-4 space-y-3">
            <label className="block text-sm font-semibold text-pf-text">Machine Profile</label>
            
            {/* Show printer info when selected */}
            {selectedPrinterForSlicing?.manufacturerName && selectedPrinterForSlicing?.modelName ? (
              <p className="text-xs text-pf-text-muted mb-2">
                Profiles for {selectedPrinterForSlicing.manufacturerName} {selectedPrinterForSlicing.modelName}
                {selectedPrinterForSlicing.nozzleDiameter && ` • ${selectedPrinterForSlicing.nozzleDiameter}mm nozzle`}
              </p>
            ) : (
              <p className="text-xs text-amber-500 mb-2">
                Select a printer above to see available machine profiles
              </p>
            )}

            {/* Machine Profile Selection (nozzle variants) */}
            <Select
              value={selectedMachineProfileId}
              onChange={e => setSelectedMachineProfileId(e.target.value)}
              disabled={!selectedPrinterId || availableMachineProfiles.length === 0}
              className={`w-full ${!selectedPrinterId ? 'opacity-50' : ''}`}
            >
              <option value="">-- Select Machine Profile --</option>
              {availableMachineProfiles.map(profile => (
                <option key={profile.id} value={profile.id}>
                  {profile.name}
                  {profile.nozzleDiameter ? ` (${profile.nozzleDiameter}mm)` : ''}
                </option>
              ))}
            </Select>
            {selectedPrinterId && availableMachineProfiles.length === 0 && selectedManufacturer && selectedPrinterModel && (
              <p className="text-xs text-amber-500 mt-1">No machine profiles available for this printer model</p>
            )}
            {selectedPrinterId && !selectedManufacturer && (
              <p className="text-xs text-amber-500 mt-1">
                No matching slicer profiles found for this printer's manufacturer
              </p>
            )}
          </div>

          {/* FILAMENT PROFILE - from slicer profiles */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-4">
            <label className="block text-sm font-semibold text-pf-text mb-2">Filament Profile</label>
            {availableFilamentProfiles.length > 0 ? (
              <Select
                value={selectedFilamentProfileId}
                onChange={e => setSelectedFilamentProfileId(e.target.value)}
                className="w-full"
              >
                <option value="">-- Select Filament Profile --</option>
                {availableFilamentProfiles.map(profile => (
                  <option key={profile.id} value={profile.id}>
                    {profile.name} ({profile.material})
                  </option>
                ))}
              </Select>
            ) : (
              <>
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
                {selectedPrinterModel && (
                  <p className="text-xs text-amber-500 mt-1">No filament profiles for this model - using presets</p>
                )}
              </>
            )}
          </div>

          {/* PROCESS PROFILE - filtered by selected machine */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-4">
            <label className="block text-sm font-semibold text-pf-text mb-2">Process Profile</label>
            {availableProcessProfiles.length > 0 ? (
              <Select
                value={selectedProcessPresetId}
                onChange={e => setSelectedProcessPresetId(e.target.value)}
                className="w-full"
              >
                <option value="">-- Select Process Profile --</option>
                {availableProcessProfiles.map(profile => (
                  <option key={profile.id} value={profile.id}>
                    {profile.name} - {profile.quality} ({profile.layerHeight}mm)
                  </option>
                ))}
              </Select>
            ) : hierarchyProfiles ? (
              <ProfileSelector
                hierarchyData={hierarchyProfiles}
                selectedProfileId={selectedProcessPresetId}
                onChange={setSelectedProcessPresetId}
              />
            ) : (
              <Select
                value={selectedProcessPresetId}
                onChange={e => setSelectedProcessPresetId(e.target.value)}
                className="w-full"
              >
                <option value="">-- Select Process Profile --</option>
                {printerProcessProfiles.map(p => (
                  <option key={p.id} value={p.id}>{p.name}</option>
                ))}
              </Select>
            )}
          </div>

          {/* ORCASLICER-STYLE SETTINGS PANEL */}
          <div className="bg-pf-panel border border-pf-border rounded-lg overflow-hidden">
            <SlicerSettingsPanel
              settings={slicerSettings}
              onChange={handleSlicerSettingsChange}
              initialViewMode="basic"
            />
          </div>

          {/* MODEL SELECTION - Inline, not collapsible */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-4 space-y-3">
            <label className="block text-sm font-semibold text-pf-text">Model</label>

            <FormField
              label="Use Model Picker"
              helper={useModelPicker ? 'Select from uploaded models' : 'Enter URL manually'}
              inline
            >
              <Checkbox
                id="useModelPicker"
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
                      <option key={m.id} value={m.id}>{m.originalFileName}</option>
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

            {/* STL Preview Button */}
            {(selectedModelId || modelFileUrl) && (
              <Button
                type="button"
                onClick={() => setIsSTLPreviewOpen(true)}
                variant="secondary"
                size="sm"
                className="w-full flex items-center justify-center gap-2"
              >
                <EyeIcon className="w-4 h-4" />
                Preview 3D Model
              </Button>
            )}

            {/* Profile Selection */}
            <div className="border-t border-pf-border pt-3">
              <div className="flex gap-3 mb-2">
                <label className="inline-flex items-center gap-2 text-sm">
                  <Radio
                    name="mode"
                    checked={useProfile}
                    onChange={() => setUseProfile(true)}
                    title="Use Profile Mode"
                  />
                  <span>Profile</span>
                </label>
                <label className="inline-flex items-center gap-2 text-sm">
                  <Radio
                    name="mode"
                    checked={!useProfile}
                    onChange={() => setUseProfile(false)}
                    title="Use JSON Mode"
                  />
                  <span>JSON</span>
                </label>
              </div>

              {useProfile ? (
                <FormField label="Profile">
                  {machineProfiles && machineProfiles.length > 0 ? (
                    <Select value={selectedProfileId} onChange={e => setSelectedProfileId(e.target.value)}>
                      <option value="">-- Select --</option>
                      {machineProfiles.map(p => (
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
                  <Textarea
                    value={rawProfileJson}
                    onChange={e => setRawProfileJson(e.target.value)}
                    rows={4}
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
                <Textarea
                  value={requiredCapabilitiesJson}
                  onChange={e => setRequiredCapabilitiesJson(e.target.value)}
                  rows={3}
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
        <div className="flex-1 hidden lg:flex flex-col gap-4 min-h-screen">
          <div className="card bg-pf-panel border border-pf-border flex-1 overflow-hidden flex flex-col">
            <div className="card-header flex-shrink-0">
              <h3 className="font-semibold text-pf-text">
                {modelFileName ? `Preview: ${modelFileName}` : 'Model Preview'}
              </h3>
            </div>
            <div className="card-body p-0 flex-1 overflow-hidden">
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

      {/* STL Preview Modal */}
      {isSTLPreviewOpen && (
        <STLPreviewModal
          isOpen={isSTLPreviewOpen}
          fileUrl={modelFileUrl}
          fileName={modelFileName}
          onClose={() => {
            setIsSTLPreviewOpen(false);
            stlFile.clearFile();
          }}
          onUseModel={() => {
            // Model is already selected, just close the modal
            setIsSTLPreviewOpen(false);
          }}
        />
      )}
      
      {/* Clone Profiles Modal - shown when printer selected but has no profiles */}
      {selectedPrinter && (
        <CloneProfilesModal
          isOpen={isCloneProfilesModalOpen}
          onClose={() => setIsCloneProfilesModalOpen(false)}
          printerId={selectedPrinterId}
          printerName={selectedPrinter.name}
          onSuccess={() => {
            // Invalidate profiles cache to reload when modal closes
            qc.invalidateQueries({ queryKey: ['slicerProfiles'] });
            qc.invalidateQueries({ queryKey: ['slicerProfilesHierarchy'] });
          }}
        />
      )}
    </PageTemplate>
  );
};

export default NewSliceJobPage;
