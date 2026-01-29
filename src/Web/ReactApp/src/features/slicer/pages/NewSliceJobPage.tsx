import React, { useState, useEffect, Suspense, useMemo, useCallback } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { sliceJobService, SubmitSliceJobRequest } from '@/services/sliceJobService';
import { 
  slicerProfilesService,
  type OrcaMachineProfile,
  type OrcaFilamentProfile,
  type OrcaProcessProfile
} from '@/services/slicerProfilesService';
import { slicerRegistry } from '@/services/slicerRegistry';
import { assetService } from '@/services/assetService';
import { apiClient } from '@/services/api';
import * as signalR from '@microsoft/signalr';
import { getHubUrl, getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import { ViewerSkeleton } from '@/features/models3d/components/3d/ViewerSkeleton';
import { CloneProfilesModal } from '@/features/slicer/components/CloneProfilesModal';
import { SlicerSettingsPanel, DEFAULT_BASIC_SETTINGS, type BasicSlicerSettings } from '@/features/slicer/components/settings';
import { PrinterSlicerSelector, type PrinterForSlicing } from '../components/job';
import { getPrimaryNozzleDiameter } from '../utils/profileMatcher';
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

// Removed MATERIAL_PRESETS constant - now using API-driven filament profiles

export const NewSliceJobPage: React.FC = () => {
  const { user } = useAuth();
  const qc = useQueryClient();
  const [searchParams] = useSearchParams();
  const modelIdFromUrl = searchParams.get('modelId') || '';

  // === Main Sidebar Controls ===
  const [selectedSlicerId, setSelectedSlicerId] = useState<number>(1);
  const [selectedPrinterId, setSelectedPrinterId] = useState<string>('');
  // Material type filter for filament profile selection
  const [selectedFilamentMaterial, setSelectedFilamentMaterial] = useState<string>('');
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

  // === INCREMENTAL PROFILE LOADING (Phase 1) ===
  // Instead of loading all 3000+ profiles upfront, we load incrementally:
  // 1. Machine profiles loaded when printer is selected (using printer's modelId)
  // 2. Filament/process profiles loaded when machine profile is selected

  // Get selected printer's model ID for profile queries
  const selectedPrinterModelId = useMemo(() => {
    return selectedPrinter?.modelId || null;
  }, [selectedPrinter]);

  // Fetch machine profiles for the selected printer's model
  const { data: machineProfilesData = [], isLoading: isMachineProfilesLoading } = useQuery<OrcaMachineProfile[]>({
    queryKey: ['machineProfilesForModel', selectedPrinterModelId],
    queryFn: () => slicerProfilesService.getMachineProfilesForModel(selectedPrinterModelId!),
    enabled: !!selectedPrinterModelId,
    staleTime: 30_000
  });

  // Get the selected machine profile object
  const selectedMachineProfile = useMemo(() => {
    if (!selectedMachineProfileId || !machineProfilesData?.length) return null;
    return machineProfilesData.find(p => p.name === selectedMachineProfileId) || null;
  }, [selectedMachineProfileId, machineProfilesData]);

  // Machine names for filament/process queries (just the selected machine)
  const selectedMachineNames = useMemo(() => {
    if (!selectedMachineProfile?.name) return [];
    return [selectedMachineProfile.name];
  }, [selectedMachineProfile]);

  // Fetch filament profiles compatible with selected machine
  const { data: filamentProfilesData = [], isLoading: isFilamentProfilesLoading } = useQuery<OrcaFilamentProfile[]>({
    queryKey: ['filamentProfilesForMachines', selectedMachineNames],
    queryFn: () => slicerProfilesService.getFilamentProfilesForMachines(selectedMachineNames),
    enabled: selectedMachineNames.length > 0,
    staleTime: 30_000
  });

  // Fetch process profiles compatible with selected machine
  const { data: processProfilesData = [], isLoading: isProcessProfilesLoading } = useQuery<OrcaProcessProfile[]>({
    queryKey: ['processProfilesForMachines', selectedMachineNames],
    queryFn: () => slicerProfilesService.getProcessProfilesForMachines(selectedMachineNames),
    enabled: selectedMachineNames.length > 0,
    staleTime: 30_000
  });

  // === CUSTOM PROFILES (Hybrid Architecture) ===
  // Fetch user's custom profiles to merge with system profiles
  const { data: customProfilesData } = useQuery({
    queryKey: ['customProfiles'],
    queryFn: () => slicerProfilesService.listCustomProfiles(),
    staleTime: 30_000
  });

  // Filter custom profiles by type for each selector
  const customMachineProfiles = useMemo(() => {
    return customProfilesData?.profiles?.filter(p => p.profileType === 'machine') ?? [];
  }, [customProfilesData]);

  const customFilamentProfiles = useMemo(() => {
    return customProfilesData?.profiles?.filter(p => p.profileType === 'filament') ?? [];
  }, [customProfilesData]);

  const customProcessProfiles = useMemo(() => {
    return customProfilesData?.profiles?.filter(p => p.profileType === 'process') ?? [];
  }, [customProfilesData]);

  // Combined loading state for profile queries
  // Combined loading state for profile queries
  const isProfilesLoading = isMachineProfilesLoading || isFilamentProfilesLoading || isProcessProfilesLoading;

  // === Profile Selection Computed Values (Incremental Loading) ===
  // Machine profiles are loaded when printer is selected (via modelId query)
  // Filament/Process profiles are loaded when machine profile is selected

  // Machine profiles for the selected printer (from incremental query)
  const availableMachineProfiles = useMemo(() => {
    return machineProfilesData ?? [];
  }, [machineProfilesData]);

  // Process profiles for the selected machine (from incremental query)
  const availableProcessProfiles = useMemo(() => {
    return processProfilesData ?? [];
  }, [processProfilesData]);

  // Auto-select machine profile when printer is selected and machine profiles are loaded
  // This effect uses nozzle diameter matching when available
  useEffect(() => {
    if (!selectedPrinterForSlicing || !machineProfilesData?.length) return;

    // Set manufacturer/model from printer for display purposes
    const mfgName = selectedPrinterForSlicing.manufacturerName;
    const modelName = selectedPrinterForSlicing.modelName;
    setSelectedManufacturer(mfgName || '');
    setSelectedPrinterModel(modelName || '');
    
    // Get nozzle diameter from printer's primary toolhead
    const nozzle = getPrimaryNozzleDiameter(selectedPrinterForSlicing);
    
    if (!nozzle) {
      // No nozzle info, select first available machine profile
      if (machineProfilesData[0]) {
        setSelectedMachineProfileId(machineProfilesData[0].name);
      }
      return;
    }
    
    // Find profile with matching nozzle diameter (within tolerance)
    const nozzleTolerance = 0.01;
    const matchedProfile = machineProfilesData.find((p: OrcaMachineProfile) =>
      p.nozzleDiameter && Math.abs(p.nozzleDiameter - nozzle) < nozzleTolerance
    );
    
    if (matchedProfile) {
      setSelectedMachineProfileId(matchedProfile.name);
    } else if (machineProfilesData[0]) {
      // Default to first profile if no nozzle match
      setSelectedMachineProfileId(machineProfilesData[0].name);
    }
  }, [selectedPrinterForSlicing, machineProfilesData]);

  // Filter profiles for the selected printer - use incremental process profiles
  const printerProcessProfiles = useMemo(() => {
    return processProfilesData ?? [];
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

  // Machine profiles for profile selection - use incremental machine profiles
  const machineProfiles = useMemo(() => {
    return machineProfilesData ?? [];
  }, [machineProfilesData]);

  // Filament profiles grouped by material type for display
  const filamentProfilesByMaterial = useMemo(() => {
    // Use filament profiles from incremental query (already filtered by machine)
    const profiles = filamentProfilesData ?? [];
    
    // Group profiles by material type
    const grouped: Record<string, OrcaFilamentProfile[]> = {};
    for (const profile of profiles) {
      const mat = profile.material || 'Other';
      if (!grouped[mat]) grouped[mat] = [];
      grouped[mat].push(profile);
    }
    return grouped;
  }, [filamentProfilesData]);

  // Available material types from the profiles (sorted alphabetically)
  const availableMaterialTypes = useMemo(() => {
    return Object.keys(filamentProfilesByMaterial).sort();
  }, [filamentProfilesByMaterial]);

  // Filament profiles filtered by selected material type
  const filteredFilamentProfiles = useMemo(() => {
    if (!selectedFilamentMaterial) return [];
    return filamentProfilesByMaterial[selectedFilamentMaterial] ?? [];
  }, [filamentProfilesByMaterial, selectedFilamentMaterial]);

  // Flat list of all available filament profiles for lookup
  const allFilamentProfiles = useMemo(() => {
    return filamentProfilesData ?? [];
  }, [filamentProfilesData]);

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

  // Get selected filament profile details for display
  const selectedFilamentProfile = useMemo(() => {
    return allFilamentProfiles.find((p: OrcaFilamentProfile) => p.name === selectedFilamentProfileId);
  }, [allFilamentProfiles, selectedFilamentProfileId]);

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
            <label className="block text-sm font-semibold text-pf-text">
              Machine Profile
              {isProfilesLoading && <span className="ml-2 text-xs text-pf-text-muted">(Loading...)</span>}
            </label>
            
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

            {/* Machine Profile Selection (nozzle variants) - Custom profiles first, then system presets */}
            <Select
              value={selectedMachineProfileId}
              onChange={e => setSelectedMachineProfileId(e.target.value)}
              disabled={!selectedPrinterId || (availableMachineProfiles.length === 0 && customMachineProfiles.length === 0) || isMachineProfilesLoading}
              className={`w-full ${!selectedPrinterId || isMachineProfilesLoading ? 'opacity-50' : ''}`}
            >
              <option value="">{isMachineProfilesLoading ? '-- Loading... --' : '-- Select Machine Profile --'}</option>
              {/* Custom profiles first with ★ indicator */}
              {customMachineProfiles.length > 0 && (
                <option disabled className="text-pf-text-muted">── My Profiles ──</option>
              )}
              {customMachineProfiles.map(profile => (
                <option key={`custom-${profile.id}`} value={profile.name}>
                  ★ {profile.name}
                </option>
              ))}
              {/* System presets divider - only show if there are system profiles */}
              {availableMachineProfiles.length > 0 && (
                <option disabled className="text-pf-text-muted">── System Presets ──</option>
              )}
              {/* System profiles */}
              {availableMachineProfiles.map(profile => (
                <option key={profile.name} value={profile.name}>
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

          {/* FILAMENT PROFILE - two-step selection: material type then profile */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-4 space-y-3">
            <label className="block text-sm font-semibold text-pf-text">Filament Profile</label>
            
            {allFilamentProfiles.length > 0 ? (
              <>
                {/* Side-by-side: Material Type + Profile Selection */}
                <div className="flex gap-2">
                  {/* Material Type Selection */}
                  <div className="w-1/3">
                    <label className="block text-xs text-pf-text-muted mb-1">Material</label>
                    <Select
                      value={selectedFilamentMaterial}
                      onChange={e => {
                        setSelectedFilamentMaterial(e.target.value);
                        setSelectedFilamentProfileId(''); // Reset profile when material changes
                      }}
                      className="w-full"
                    >
                      <option value="">--</option>
                      {availableMaterialTypes.map(mat => (
                        <option key={mat} value={mat}>{mat}</option>
                      ))}
                    </Select>
                  </div>

                  {/* Filament Profile Selection (filtered by material) - Custom profiles first, then system presets */}
                  <div className="flex-1">
                    <label className="block text-xs text-pf-text-muted mb-1">Profile</label>
                    <Select
                      value={selectedFilamentProfileId}
                      onChange={e => setSelectedFilamentProfileId(e.target.value)}
                      disabled={!selectedFilamentMaterial && customFilamentProfiles.length === 0}
                      className={`w-full ${!selectedFilamentMaterial && customFilamentProfiles.length === 0 ? 'opacity-50' : ''}`}
                    >
                      <option value="">-- Select Profile --</option>
                      {/* Custom profiles first with ★ indicator */}
                      {customFilamentProfiles.length > 0 && (
                        <option disabled className="text-pf-text-muted">── My Profiles ──</option>
                      )}
                      {customFilamentProfiles.map(profile => (
                        <option key={`custom-${profile.id}`} value={profile.name}>
                          ★ {profile.name}
                        </option>
                      ))}
                      {/* System presets divider - only show if there are system profiles */}
                      {filteredFilamentProfiles.length > 0 && (
                        <option disabled className="text-pf-text-muted">── System Presets ──</option>
                      )}
                      {/* System profiles */}
                      {filteredFilamentProfiles.map(profile => (
                        <option key={profile.name} value={profile.name}>
                          {profile.name}
                        </option>
                      ))}
                    </Select>
                  </div>
                </div>

                {/* Show selected profile's temperature info */}
                {selectedFilamentProfile && (
                  <div className="text-xs text-pf-text-muted">
                    {selectedFilamentProfile.nozzleTemperature ?? 210}°C nozzle, {selectedFilamentProfile.bedTemperature ?? 60}°C bed
                  </div>
                )}
              </>
            ) : (
              <div className="text-sm text-pf-text-muted italic">
                {isMachineProfilesLoading ? 'Loading machine profiles...' : 
                 selectedMachineProfileId && isFilamentProfilesLoading ? 'Loading filament profiles...' :
                 !selectedMachineProfileId ? 'Select a machine profile to see filament options' :
                 'No filament profiles available'}
              </div>
            )}
          </div>

          {/* PROCESS PROFILE - Custom profiles first, then system presets */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-4">
            <label className="block text-sm font-semibold text-pf-text mb-2">Process Profile</label>
            {(availableProcessProfiles.length > 0 || customProcessProfiles.length > 0) ? (
              <Select
                value={selectedProcessPresetId}
                onChange={e => setSelectedProcessPresetId(e.target.value)}
                className="w-full"
              >
                <option value="">-- Select Process Profile --</option>
                {/* Custom profiles first with ★ indicator */}
                {customProcessProfiles.length > 0 && (
                  <option disabled className="text-pf-text-muted">── My Profiles ──</option>
                )}
                {customProcessProfiles.map(profile => (
                  <option key={`custom-${profile.id}`} value={profile.name}>
                    ★ {profile.name}
                  </option>
                ))}
                {/* System presets divider - only show if there are system profiles */}
                {availableProcessProfiles.length > 0 && (
                  <option disabled className="text-pf-text-muted">── System Presets ──</option>
                )}
                {/* System profiles */}
                {availableProcessProfiles.map(profile => (
                  <option key={profile.name} value={profile.name}>
                    {profile.name} - {profile.quality} ({profile.layerHeight}mm)
                  </option>
                ))}
              </Select>
            ) : (
              <div className="text-sm text-pf-text-muted italic">
                {isMachineProfilesLoading ? 'Loading machine profiles...' : 
                 selectedMachineProfileId && isProcessProfilesLoading ? 'Loading process profiles...' :
                 !selectedMachineProfileId ? 'Select a machine profile to see process options' :
                 'No process profiles available'}
              </div>
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
                        <option key={p.name} value={p.name}>{p.name} ({p.manufacturer})</option>
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
