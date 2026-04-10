import React, { useState, useEffect, useMemo, useCallback } from 'react';
import { useSearchParams, useNavigate } from 'react-router';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
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
import { CloneProfilesModal } from '@/features/slicer/components/CloneProfilesModal';
import { ProfileEditorModal, type ProfileType } from '@/features/slicer/components/ProfileEditorModal';
import {
  SlicerSettingsPanel,
  DEFAULT_ADVANCED_SETTINGS,
  type SimpleSlicerSettings,
  type AdvancedSlicerSettings,
} from '@/features/slicer/components/settings';
import { PrinterSlicerSelector, SlicerSelector, type PrinterForSlicing } from '../components/job';
import { ModelSelector } from '../components/job/ModelSelector';
import { getPrimaryNozzleDiameter } from '../utils/profileMatcher';
import type { ModelListItem } from '@/types/models';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Alert, Input, Select } from '@/common/components/ui';
import { LayersIcon, EyeIcon, EditIcon, DownloadIcon, RefreshIcon, SaveIcon, MoreVerticalIcon, CopyIcon, FileImportIcon } from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { STLPreviewModal } from '@/features/models3d/components/3d/STLPreviewModal';
import { useSTLFile } from '@/common/hooks/useSTLFile';
import { useSliceJobProgress } from '@/features/slicer/hooks/useSliceJobProgress';
import { SlicerWorkspace, type LoadedModel, type BedConfig } from '@/features/slicer/components/viewer';
import { sliceJobService as sliceJobSvc } from '@/services/sliceJobService';

// Removed MATERIAL_PRESETS constant - now using API-driven filament profiles

/**
 * Helper function to convert OrcaProcessProfile to AdvancedSlicerSettings
 * Maps profile data to settings structure, using defaults for missing values
 */
function convertOrcaProcessProfileToSettings(profile: OrcaProcessProfile | undefined): AdvancedSlicerSettings {
  if (!profile) return DEFAULT_ADVANCED_SETTINGS;

  // Parse settings from profile if available
  const profileSettings = (profile.settings ?? {}) as Record<string, unknown>;

  return {
    ...DEFAULT_ADVANCED_SETTINGS,
    layerHeight: profile.layerHeight ?? DEFAULT_ADVANCED_SETTINGS.layerHeight,
    infillDensity: profile.infillPercentage ?? DEFAULT_ADVANCED_SETTINGS.infillDensity,
    printSpeed: profile.printSpeed ?? DEFAULT_ADVANCED_SETTINGS.printSpeed,
    enableSupports: profile.supports ?? DEFAULT_ADVANCED_SETTINGS.enableSupports,
    // Spread any additional parsed settings from profile.settings
    ...profileSettings,
  };
}

export const NewSliceJobPage: React.FC = () => {
  const STORAGE_KEYS = {
    printerId: 'sliceJob.selectedPrinterId',
    machineProfileId: 'sliceJob.selectedMachineProfileId',
    filamentProfileId: 'sliceJob.selectedFilamentProfileId',
    processProfileId: 'sliceJob.selectedProcessProfileId',
    requiredCapabilities: 'sliceJob.requiredCapabilities',
    selectedProfileId: 'sliceJob.selectedProfileId',
  } as const;

  const { user } = useAuth();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const modelIdFromUrl = searchParams.get('modelId') || '';

  // Check if ANY machine profiles exist in the system (for onboarding detection)
  const { data: profilesSummary, isLoading: isProfilesSummaryLoading } = useQuery({
    queryKey: ['slicerProfilesExtended'],
    queryFn: () => slicerProfilesService.listExtended(),
    staleTime: 300_000,
  });
  const hasAnyMachineProfiles = (profilesSummary?.machineProfiles?.length ?? 0) > 0;

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
  const [slicerSettings, setSlicerSettings] = useState<AdvancedSlicerSettings>(DEFAULT_ADVANCED_SETTINGS);
  const [advancedProcessSettings, setAdvancedProcessSettings] = useState<Record<string, unknown>>({});
  
  const [profileMenuOpen, setProfileMenuOpen] = useState(false);
  const profileMenuRef = React.useRef<HTMLDivElement>(null);
  const importFileRef = React.useRef<HTMLInputElement>(null);
  const [saveProfileState, setSaveProfileState] = useState<{ open: boolean; name: string }>({ open: false, name: '' });

  // Callback for settings panel changes
  const handleSlicerSettingsChange = useCallback((newSettings: SimpleSlicerSettings | AdvancedSlicerSettings) => {
    setSlicerSettings((prev) => ({ ...prev, ...newSettings }));
  }, []);

  // === Process Profile Management handlers ===
  const handleResetProcessProfile = useCallback(() => {
    setSlicerSettings(DEFAULT_ADVANCED_SETTINGS);
    setAdvancedProcessSettings({});
  }, []);

  const handleCopyProcess = useCallback(() => {
    if (!selectedProcessPresetId) return;
    setProfileMenuOpen(false);
    // Derive a display name from the preset ID for the default copy name
    const displayName = selectedProcessPresetId.startsWith('system:')
      ? selectedProcessPresetId.slice('system:'.length)
      : selectedProcessPresetId.startsWith('custom:')
      ? selectedProcessPresetId.slice('custom:'.length)
      : selectedProcessPresetId;
    setSaveProfileState({ open: true, name: `${displayName} (Copy)` });
  }, [selectedProcessPresetId]);

  const handleConfirmSaveProfile = useCallback(async () => {
    if (!saveProfileState.name.trim() || !selectedProcessPresetId) return;
    const sourceId = selectedProcessPresetId.startsWith('system:')
      ? selectedProcessPresetId.slice('system:'.length)
      : selectedProcessPresetId.startsWith('custom:')
      ? selectedProcessPresetId.slice('custom:'.length)
      : selectedProcessPresetId;
    try {
      await slicerProfilesService.cloneProfile({
        sourceProfileId: sourceId,
        profileType: 'process',
        name: saveProfileState.name.trim(),
      });
      toast.success(`Profile "${saveProfileState.name.trim()}" saved`);
      qc.invalidateQueries({ queryKey: ['customProfiles'] });
      setSaveProfileState({ open: false, name: '' });
    } catch {
      toast.error('Failed to save profile');
    }
  }, [saveProfileState.name, selectedProcessPresetId, qc]);

  const handleImportProcess = useCallback(() => {
    setProfileMenuOpen(false);
    importFileRef.current?.click();
  }, []);

  const handleProfileFileImport = useCallback(async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    try {
      const text = await file.text();
      const parsed = JSON.parse(text) as Record<string, unknown>;
      await slicerProfilesService.uploadProfile({
        rawJson: text,
        profileType: 'process',
        name: (parsed.name as string) || file.name.replace('.json', ''),
      });
      toast.success('Profile imported successfully');
      qc.invalidateQueries({ queryKey: ['customProfiles'] });
    } catch {
      toast.error('Failed to import profile — make sure it is valid OrcaSlicer process JSON');
    }
  }, [qc]);

  // Close profile menu when clicking outside
  useEffect(() => {
    if (!profileMenuOpen) return;
    const handler = (e: MouseEvent) => {
      if (profileMenuRef.current && !profileMenuRef.current.contains(e.target as Node)) {
        setProfileMenuOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [profileMenuOpen]);

  // === Model Selection ===
  const [modelFileUrl, setModelFileUrl] = useState('');
  const [modelFileName, setModelFileName] = useState('');
  const [useModelPicker, setUseModelPicker] = useState(true);
  const [selectedModelId, setSelectedModelId] = useState<string>(modelIdFromUrl);
  const [modelPickerOpen, setModelPickerOpen] = useState(false);
  // Multi-model bed state — accumulates models added via the "+" button
  const [bedModels, setBedModels] = useState<LoadedModel[]>([]);

  // Track which model is selected on the 3D bed (for TransformControls)
  const [selectedBedModelId, setSelectedBedModelId] = useState<string | null>(null);

  // ESC key deselects the model on the bed
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && selectedBedModelId != null) {
        setSelectedBedModelId(null);
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [selectedBedModelId]);

  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [isSTLPreviewOpen, setIsSTLPreviewOpen] = useState(false);
  const [isCloneProfilesModalOpen, setIsCloneProfilesModalOpen] = useState(false);
  const [cloneProfilesDismissed, setCloneProfilesDismissed] = useState(false);
  
  // Profile Editor Modal State
  const [profileEditorOpen, setProfileEditorOpen] = useState(false);
  const [profileEditorType, setProfileEditorType] = useState<ProfileType>('machine');
  const [sidebarOpen, setSidebarOpen] = useState(true);

  // Post-submission progress tracking
  const [submittedJobId, setSubmittedJobId] = useState<string | null>(null);
  const jobProgress = useSliceJobProgress(submittedJobId);
  
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
        isOnline: p.isOnline,
        motionType: p.motionType
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
  }, [selectedPrinterWithDetails]);

  // === INCREMENTAL PROFILE LOADING (Phase 1) ===
  // Instead of loading all 3000+ profiles upfront, we load incrementally:
  // 1. Machine profiles loaded when printer is selected (using printer's modelId)
  // 2. Filament/process profiles loaded when machine profile is selected

  // Get selected printer's model ID for profile queries
  // NOTE: Use selectedPrinterDetails (from /details endpoint) because the basic
  // /api/printers list doesn't include modelId - only the details endpoint does
  const selectedPrinterModelId = useMemo(() => {
    return selectedPrinterDetails?.modelId || null;
  }, [selectedPrinterDetails]);

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

  // Group process profiles by quality level for better UX
  const processProfilesByQuality = useMemo(() => {
    const profiles = processProfilesData ?? [];
    const qualityOrder = ['fine', 'standard', 'draft', 'speed'];
    const grouped: Record<string, typeof profiles> = {};
    
    for (const profile of profiles) {
      const quality = (profile.quality ?? 'other').toLowerCase();
      if (!grouped[quality]) {
        grouped[quality] = [];
      }
      grouped[quality].push(profile);
    }
    
    // Sort groups by quality order, with unknown qualities at the end
    const sortedEntries = Object.entries(grouped).sort(([a], [b]) => {
      const indexA = qualityOrder.indexOf(a);
      const indexB = qualityOrder.indexOf(b);
      // If not in order list, put at end
      const posA = indexA === -1 ? 999 : indexA;
      const posB = indexB === -1 ? 999 : indexB;
      return posA - posB;
    });
    
    return sortedEntries;
  }, [processProfilesData]);

  // Auto-select machine profile when printer is selected and machine profiles are loaded
  // Keep the current selection if it is still valid (restored from previous session).
  // This effect uses nozzle diameter matching when available
  useEffect(() => {
    if (!selectedPrinterForSlicing || !machineProfilesData?.length) return;

    // Defer all setState calls to avoid synchronous updates in effect body
    queueMicrotask(() => {
      // Set manufacturer/model from printer for display purposes
      const mfgName = selectedPrinterForSlicing.manufacturerName;
      const modelName = selectedPrinterForSlicing.modelName;
      setSelectedManufacturer(mfgName || '');
      setSelectedPrinterModel(modelName || '');

      // Keep current selection if valid for this printer model
      const hasCurrent = !!selectedMachineProfileId && machineProfilesData.some((p) => p.name === selectedMachineProfileId);
      if (hasCurrent) {
        return;
      }
      
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
    });
  }, [selectedPrinterForSlicing, machineProfilesData, selectedMachineProfileId]);

  // When machine profile changes, keep compatible selections and clear invalid ones.
  // This lets us restore last-used filament/process values on re-entry.
  useEffect(() => {
    queueMicrotask(() => {
      if (!selectedMachineProfileId) {
        setSelectedFilamentProfileId('');
        setSelectedFilamentMaterial('');
        setSelectedProcessPresetId('');
        return;
      }

      if (selectedFilamentProfileId) {
        const selectedFilament = (filamentProfilesData ?? []).find((p) => p.name === selectedFilamentProfileId);
        const customFilamentExists = customFilamentProfiles.some((p) => p.name === selectedFilamentProfileId);

        if (selectedFilament) {
          setSelectedFilamentMaterial(selectedFilament.material || '');
        } else if (customFilamentExists) {
          // Keep custom selection; material may not be derivable from custom profile metadata.
          if (!selectedFilamentMaterial) {
            setSelectedFilamentMaterial('');
          }
        } else {
          setSelectedFilamentProfileId('');
          setSelectedFilamentMaterial('');
        }
      }

      if (selectedProcessPresetId) {
        const processIsValid = selectedProcessPresetId.startsWith('system:')
          ? (processProfilesData ?? []).some((p) => `system:${p.name}` === selectedProcessPresetId)
          : selectedProcessPresetId.startsWith('custom:')
            ? customProcessProfiles.some((p) => `custom:${p.id}` === selectedProcessPresetId)
            : false;

        if (!processIsValid) {
          setSelectedProcessPresetId('');
        }
      }
    });
  }, [
    selectedMachineProfileId,
    selectedFilamentProfileId,
    selectedFilamentMaterial,
    selectedProcessPresetId,
    filamentProfilesData,
    customFilamentProfiles,
    processProfilesData,
    customProcessProfiles,
  ]);

  // Check if printer has no profiles - show clone suggestion
  // IMPORTANT: Only suggest clone AFTER machine profiles have loaded and we know there are none
  // This prevents the modal from showing during loading states
  const shouldSuggestCloneProfiles = useMemo(() => {
    // Don't suggest if user already dismissed for this session
    if (cloneProfilesDismissed) return false;
    // Don't suggest if no printer selected
    if (!selectedPrinterId) return false;
    // Don't suggest if printer has no modelId (query won't run)
    if (!selectedPrinterModelId) return false;
    // Don't suggest while machine profiles are still loading
    if (isMachineProfilesLoading) return false;
    // Suggest if machine profiles query completed but returned empty
    // (meaning OrcaSlicer has no profiles for this printer model)
    if (machineProfilesData.length === 0 && !isMachineProfilesLoading) return true;
    // Don't suggest if we have machine profiles (process profiles will load after machine selection)
    return false;
  }, [selectedPrinterId, selectedPrinterModelId, machineProfilesData.length, isMachineProfilesLoading, cloneProfilesDismissed]);

  // Auto-open clone profiles modal if printer selected but has no profiles
  // Only opens once per printer selection - user dismissal is respected
  useEffect(() => {
    if (shouldSuggestCloneProfiles && !isCloneProfilesModalOpen) {
      const timer = setTimeout(() => {
        setIsCloneProfilesModalOpen(true);
      }, 500); // Increased delay to ensure profiles have time to load
      return () => clearTimeout(timer);
    }
  }, [shouldSuggestCloneProfiles, isCloneProfilesModalOpen]);

  // Reset dismissal state when printer changes
  useEffect(() => {
    queueMicrotask(() => setCloneProfilesDismissed(false));
  }, [selectedPrinterId]);

  // Machine profiles for profile selection - use incremental machine profiles
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

  // Selected process profile from Orca profile query
  const selectedProcessProfile = useMemo(() => {
    if (!selectedProcessPresetId || !selectedProcessPresetId.startsWith('system:')) {
      return null;
    }

    const processName = selectedProcessPresetId.slice('system:'.length);
    return (processProfilesData ?? []).find((p: OrcaProcessProfile) => p.name === processName) ?? null;
  }, [processProfilesData, selectedProcessPresetId]);

  const selectedCustomProcessProfile = useMemo(() => {
    if (!selectedProcessPresetId || !selectedProcessPresetId.startsWith('custom:')) {
      return null;
    }

    const customId = selectedProcessPresetId.slice('custom:'.length);
    return customProcessProfiles.find((p) => p.id === customId) ?? null;
  }, [customProcessProfiles, selectedProcessPresetId]);

  // Fetch models for picker
  const { data: models = [], error: modelsError } = useQuery<ModelListItem[], Error>({
    queryKey: ['modelsListBasic'],
    queryFn: async () => {
      const response = await apiClient.get<unknown[]>('/3d-models');
      return response.data.map(obj => {
        const m = obj as { id: string; name?: string; fileName?: string; displayName?: string; originalFileName?: string; fileFormat?: number; uploadedAt?: string; uploadedAtUtc?: string };
        const displayName = m.name || m.originalFileName || m.displayName || m.fileName || 'model';
        return {
          id: m.id,
          fileName: m.fileName || displayName,
          originalFileName: displayName,
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

  // Restore persisted selections
  useEffect(() => {
    try {
      const savedCaps = localStorage.getItem(STORAGE_KEYS.requiredCapabilities);
      const savedProfileId = localStorage.getItem(STORAGE_KEYS.selectedProfileId);
      const savedPrinterId = localStorage.getItem(STORAGE_KEYS.printerId);
      const savedMachineProfileId = localStorage.getItem(STORAGE_KEYS.machineProfileId);
      const savedFilamentProfileId = localStorage.getItem(STORAGE_KEYS.filamentProfileId);
      const savedProcessProfileId = localStorage.getItem(STORAGE_KEYS.processProfileId);

      queueMicrotask(() => {
        if (savedCaps) setRequiredCapabilitiesJson(savedCaps);
        if (savedProfileId) setSelectedProfileId(savedProfileId);
        if (savedPrinterId) setSelectedPrinterId(savedPrinterId);
        if (savedMachineProfileId) setSelectedMachineProfileId(savedMachineProfileId);
        if (savedFilamentProfileId) setSelectedFilamentProfileId(savedFilamentProfileId);
        if (savedProcessProfileId) setSelectedProcessPresetId(savedProcessProfileId);
      });
    } catch { /* ignore */ }
  }, [STORAGE_KEYS.filamentProfileId, STORAGE_KEYS.machineProfileId, STORAGE_KEYS.printerId, STORAGE_KEYS.processProfileId, STORAGE_KEYS.requiredCapabilities, STORAGE_KEYS.selectedProfileId]);

  // First use fallback: default to first available printer.
  useEffect(() => {
    if (!printers.length) return;

    queueMicrotask(() => {
      const hasSelectedPrinter = !!selectedPrinterId && printers.some((p) => p.id === selectedPrinterId);
      if (!hasSelectedPrinter) {
        setSelectedPrinterId(printers[0].id);
      }
    });
  }, [printers, selectedPrinterId]);

  useEffect(() => {
    try { localStorage.setItem(STORAGE_KEYS.requiredCapabilities, requiredCapabilitiesJson); } catch { /* ignore */ }
  }, [STORAGE_KEYS.requiredCapabilities, requiredCapabilitiesJson]);

  useEffect(() => {
    try {
      if (selectedProfileId) localStorage.setItem(STORAGE_KEYS.selectedProfileId, selectedProfileId);
      else localStorage.removeItem(STORAGE_KEYS.selectedProfileId);
    } catch { /* ignore */ }
  }, [STORAGE_KEYS.selectedProfileId, selectedProfileId]);

  useEffect(() => {
    try {
      if (selectedPrinterId) localStorage.setItem(STORAGE_KEYS.printerId, selectedPrinterId);
      else localStorage.removeItem(STORAGE_KEYS.printerId);
    } catch { /* ignore */ }
  }, [STORAGE_KEYS.printerId, selectedPrinterId]);

  useEffect(() => {
    try {
      if (selectedMachineProfileId) localStorage.setItem(STORAGE_KEYS.machineProfileId, selectedMachineProfileId);
      else localStorage.removeItem(STORAGE_KEYS.machineProfileId);
    } catch { /* ignore */ }
  }, [STORAGE_KEYS.machineProfileId, selectedMachineProfileId]);

  useEffect(() => {
    try {
      if (selectedFilamentProfileId) localStorage.setItem(STORAGE_KEYS.filamentProfileId, selectedFilamentProfileId);
      else localStorage.removeItem(STORAGE_KEYS.filamentProfileId);
    } catch { /* ignore */ }
  }, [STORAGE_KEYS.filamentProfileId, selectedFilamentProfileId]);

  useEffect(() => {
    try {
      if (selectedProcessPresetId) localStorage.setItem(STORAGE_KEYS.processProfileId, selectedProcessPresetId);
      else localStorage.removeItem(STORAGE_KEYS.processProfileId);
    } catch { /* ignore */ }
  }, [STORAGE_KEYS.processProfileId, selectedProcessPresetId]);

  // Derive model file URL when selected and add to bed
  useEffect(() => {
    if (useModelPicker && selectedModelId) {
      const apiBase = getApiBaseUrl();
      const mdl = models?.find(m => m.id === selectedModelId);
      const url = `${apiBase}/3d-models/file/${selectedModelId}`;
      const fileName = mdl?.originalFileName || mdl?.fileName || 'model.stl';
      queueMicrotask(() => {
        setModelFileUrl(url);
        if (mdl) {
          setModelFileName(fileName);
        }
        // Add to bed models if not already present
        setBedModels(prev => {
          if (prev.some(m => m.id === selectedModelId)) return prev;
          const offset = prev.length * 30; // offset each model so they don't overlap
          return [...prev, {
            id: selectedModelId,
            url,
            fileName,
            fileType: 'stl' as const,
            position: [offset, 0, 0] as [number, number, number],
            rotation: [0, 0, 0] as [number, number, number],
            scale: [1, 1, 1] as [number, number, number],
          }];
        });
      });
    }
  }, [useModelPicker, selectedModelId, models]);



  // Get selected filament profile details for display
  const selectedFilamentProfile = useMemo(() => {
    return allFilamentProfiles.find((p: OrcaFilamentProfile) => p.name === selectedFilamentProfileId);
  }, [allFilamentProfiles, selectedFilamentProfileId]);

  // Hydrate dynamic advanced settings from selected Orca process profile.
  useEffect(() => {
    queueMicrotask(() => {
      const rawSettings = selectedProcessProfile?.settings;
      if (rawSettings && typeof rawSettings === 'object') {
        setAdvancedProcessSettings({ ...rawSettings });
        return;
      }

      const customRawJson = selectedCustomProcessProfile?.rawJson;
      if (customRawJson) {
        try {
          const parsed = JSON.parse(customRawJson);
          if (parsed && typeof parsed === 'object') {
            setAdvancedProcessSettings(parsed as Record<string, unknown>);
            return;
          }
        } catch {
          // Ignore parse error and fall back to empty settings.
        }
      }

      setAdvancedProcessSettings({});
    });
  }, [selectedCustomProcessProfile, selectedProcessProfile]);

  // Sync typed slicer settings from selected Orca process profile.
  // Uses queueMicrotask to avoid the "setState in effect" lint warning.
  useEffect(() => {
    queueMicrotask(() => {
      const typedSettings = convertOrcaProcessProfileToSettings(selectedProcessProfile ?? undefined);
      setSlicerSettings(typedSettings);
    });
  }, [selectedProcessProfile]);

  const submitMutation = useMutation({
    mutationFn: async (req: SubmitSliceJobRequest) => sliceJobService.submitJob(req),
    onSuccess: (res) => {
      setMessage(`Job queued (id ${res.jobId.substring(0, 8)}) position ${res.queuePosition}`);
      setError(null);
      setSubmittedJobId(res.jobId);
      qc.invalidateQueries({ queryKey: ['slice-jobs-my'] });
      qc.invalidateQueries({ queryKey: ['slice-jobs'] });
    },
    onError: (err: unknown) => {
      setError(err instanceof Error ? err.message : 'Failed to submit job');
    }
  });

  const submitSliceJob = useCallback(() => {
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

    if (!selectedProcessPresetId) {
      setError('Select a process profile');
      return;
    }

    const request: SubmitSliceJobRequest = {
      userId: user?.id || '',
      printerId: undefined,
      modelFileUrl: modelFileUrl,
      modelFileName: modelFileName,
      slicerEngine: slicerInfo.engine,
      slicerProfileJson: JSON.stringify({
            machineProfileName: selectedMachineProfileId,
            filamentProfileName: selectedFilamentProfileId,
            processProfileName: selectedProcessPresetId.startsWith('system:')
              ? selectedProcessPresetId.slice('system:'.length)
              : selectedProcessPresetId.startsWith('custom:')
              ? selectedProcessPresetId.slice('custom:'.length)
              : selectedProcessPresetId,
            overrides: {
              ...slicerSettings,
              ...advancedProcessSettings,
            },
          }),
      slicerProfileId: selectedProcessPresetId.startsWith('custom:')
            ? selectedProcessPresetId.slice('custom:'.length)
            : undefined,
      requiredCapabilitiesJson: '[]',
      priority: 1
    };

    submitMutation.mutate(request);
  }, [
    advancedProcessSettings,
    modelFileName,
    modelFileUrl,
    selectedFilamentProfileId,
    selectedMachineProfileId,
    selectedProcessPresetId,
    slicerInfo.engine,
    slicerSettings,
    submitMutation,
    useModelPicker,
    user?.id,
    selectedModelId,
  ]);

  const onSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    submitSliceJob();
  };

  const workspaceBedConfig = useMemo<BedConfig>(() => ({
    width: bedDimensions?.width ?? 220,
    depth: bedDimensions?.depth ?? 220,
    height: bedDimensions?.height ?? 250,
    textureUrl: bedTextureInfo.url,
    textureFormat: bedTextureInfo.format,
  }), [bedDimensions, bedTextureInfo.format, bedTextureInfo.url]);

  // workspaceModels is the live bedModels state (multi-model accumulation)
  const workspaceModels = bedModels;

  const handleWorkspaceAddModel = useCallback(() => {
    if (!useModelPicker) {
      setUseModelPicker(true);
    }
    setModelPickerOpen(true);
  }, [useModelPicker]);

  const handleWorkspaceModelSelect = useCallback((modelId: string | null) => {
    setSelectedBedModelId(modelId);
  }, []);

  const handleWorkspaceModelTransform = useCallback((
    modelId: string,
    position: [number, number, number],
    rotation: [number, number, number],
    scale: [number, number, number],
  ) => {
    setBedModels((prev) => prev.map((model) =>
      model.id === modelId
        ? {
          ...model,
          position,
          rotation,
          scale,
        }
        : model,
    ));
  }, []);

  const handleWorkspaceSettingsProfiles = useCallback(() => {
    const settingsPanel = document.querySelector('[aria-label="Process profile options menu"]');
    if (settingsPanel instanceof HTMLElement) {
      settingsPanel.scrollIntoView({ behavior: 'smooth', block: 'center' });
      settingsPanel.focus();
      return;
    }

    window.scrollTo({ top: 0, behavior: 'smooth' });
  }, []);

  // Show onboarding banner when no machine profiles exist and loading is complete
  if (!isProfilesSummaryLoading && !hasAnyMachineProfiles) {
    return (
      <PageTemplate
        title="New Slice Job"
        subtitle="OrcaSlicer-style distributed slicing"
        icon={LayersIcon}
        showHeader={false}
        padding="p-2"
      >
        <div className="flex flex-col items-center justify-center py-16 text-center" data-testid="onboarding-banner">
          <div className="relative mb-8">
            <svg width="120" height="120" viewBox="0 0 120 120" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
              {/* Printer base */}
              <rect x="20" y="60" width="80" height="40" rx="4" className="fill-pf-bg-2 stroke-pf-border" strokeWidth="2" />
              {/* Build plate */}
              <rect x="30" y="70" width="60" height="4" rx="1" className="fill-pf-accent/30" />
              {/* Vertical rails */}
              <rect x="28" y="20" width="4" height="50" rx="2" className="fill-pf-border" />
              <rect x="88" y="20" width="4" height="50" rx="2" className="fill-pf-border" />
              {/* Crossbar / gantry */}
              <rect x="28" y="20" width="64" height="4" rx="2" className="fill-pf-border" />
              {/* Hotend */}
              <rect x="54" y="24" width="12" height="16" rx="2" className="fill-pf-accent" />
              <rect x="58" y="40" width="4" height="6" rx="1" className="fill-pf-accent" />
              {/* Slice layers (animated feel) */}
              <rect x="38" y="74" width="44" height="2" rx="1" className="fill-pf-accent/60" />
              <rect x="40" y="78" width="40" height="2" rx="1" className="fill-pf-accent/40" />
              <rect x="42" y="82" width="36" height="2" rx="1" className="fill-pf-accent/20" />
              {/* Status light */}
              <circle cx="90" cy="90" r="4" className="fill-pf-success" />
            </svg>
          </div>
          <h2 className="text-xl font-semibold text-pf-text-primary mb-2">
            Get started with slicing
          </h2>
          <p className="text-sm text-pf-text-secondary mb-6 max-w-md">
            Import printer profiles to configure your first slice job. Profiles define machine settings, filament parameters, and print quality presets.
          </p>
          <div className="flex gap-3">
            <Button
              variant="primary"
              onClick={() => navigate('/profiles/import')}
              data-testid="import-profiles-button"
            >
              Import Profiles
            </Button>
          </div>
        </div>
      </PageTemplate>
    );
  }

  return (
    <div className="overflow-hidden p-2 bg-pf-bg-2">
      <form onSubmit={onSubmit} className="relative flex lg:flex-row gap-2 h-[calc(100dvh-72px)] overflow-hidden">
        {/* LEFT SIDEBAR: OrcaSlicer Menu — hidden on narrow viewports, toggled via hamburger.
             On lg+ screens: inline beside visualizer unless explicitly toggled off.
             On narrow screens: slides over as fixed-width panel when toggled open. */}
        <div className={`${sidebarOpen ? 'absolute top-0 left-0 bottom-0 z-40 w-96 lg:relative lg:inset-auto lg:z-auto' : 'hidden'} lg:w-96 space-y-2 shrink-0 lg:h-full lg:min-h-0 min-h-0 overflow-y-auto bg-pf-bg-2 shadow-xl lg:shadow-none`}>

          {/* SLICER SELECTION - Card selector with OrcaSlicer logo */}
          <SlicerSelector
            selectedSlicerId={selectedSlicerId}
            onSlicerChange={setSelectedSlicerId}
            engineOptions={engineOptions}
          />

          {/* PRINTER SELECTION - Select from registered printers first */}
          <PrinterSlicerSelector
            printers={printers}
            isLoading={isPrintersLoading}
            selectedPrinterId={selectedPrinterId}
            onPrinterChange={(printerId) => {
              setSelectedPrinterId(printerId);
              // Cascade reset: printer change resets all profile selections
              setSelectedMachineProfileId('');
              setSelectedFilamentProfileId('');
              setSelectedFilamentMaterial('');
              setSelectedProcessPresetId('');
              // Machine profile auto-select will happen via the effect
            }}
            className="bg-pf-panel border border-pf-border rounded-lg p-3"
          />

          {/* MACHINE PROFILE SELECTION - Filtered by selected printer */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-3 space-y-2">
            <div className="flex items-center justify-between">
              <label className="block text-sm font-semibold text-pf-text-primary">
                Machine Profile
                {isProfilesLoading && <span className="ml-2 text-xs text-pf-text-muted">(Loading...)</span>}
              </label>
              {selectedMachineProfileId && (
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  onClick={() => {
                    setProfileEditorType('machine');
                    setProfileEditorOpen(true);
                  }}
                  className="p-1 h-auto"
                  title="Edit machine profile settings"
                >
                  <EditIcon className="w-4 h-4" />
                </Button>
              )}
            </div>
            
            {/* Show printer info when selected */}
            {selectedPrinterForSlicing?.manufacturerName && selectedPrinterForSlicing?.modelName ? (
              <p className="text-xs text-pf-text-muted mb-2">
                Profiles for {selectedPrinterForSlicing.manufacturerName} {selectedPrinterForSlicing.modelName}
                {selectedPrinterForSlicing.nozzleDiameter && ` • ${selectedPrinterForSlicing.nozzleDiameter}mm nozzle`}
              </p>
            ) : (
              <p className="text-xs text-pf-warning mb-2">
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
              <p className="text-xs text-pf-warning mt-1">No machine profiles available for this printer model</p>
            )}
            {selectedPrinterId && !selectedManufacturer && (
              <p className="text-xs text-pf-warning mt-1">
                No matching slicer profiles found for this printer's manufacturer
              </p>
            )}
          </div>

          {/* FILAMENT PROFILE - two-step selection: material type then profile */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-3 space-y-2">
            <div className="flex items-center justify-between">
              <label className="block text-sm font-semibold text-pf-text-primary">Filament Profile</label>
              {selectedFilamentProfileId && (
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  onClick={() => {
                    setProfileEditorType('filament');
                    setProfileEditorOpen(true);
                  }}
                  className="p-1 h-auto"
                  title="Edit filament profile settings"
                >
                  <EditIcon className="w-4 h-4" />
                </Button>
              )}
            </div>
            
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

          {/* PROCESS PROFILE - with Reset, Save-as, and profile management menu */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-3">
            {/* Header: label + ⋮ options menu */}
            <div className="flex items-center justify-between mb-2">
              <label className="block text-sm font-semibold text-pf-text-primary">Process Profile</label>
              <div className="relative" ref={profileMenuRef}>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  className="p-1 h-auto"
                  onClick={() => setProfileMenuOpen(v => !v)}
                  title="Profile options"
                  aria-label="Process profile options menu"
                  aria-expanded={profileMenuOpen}
                  aria-haspopup="menu"
                >
                  <MoreVerticalIcon className="w-4 h-4" />
                </Button>
                {profileMenuOpen && (
                  <div
                    className="absolute right-0 top-full mt-1 z-20 bg-pf-panel border border-pf-border rounded-lg shadow-lg min-w-40 py-1"
                  >
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      className="w-full justify-start px-3 py-1.5 text-sm rounded-none"
                      onClick={handleCopyProcess}
                      disabled={!selectedProcessPresetId}
                      iconLeft={<CopyIcon className="w-3.5 h-3.5" />}
                    >
                      Copy selected
                    </Button>
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      className="w-full justify-start px-3 py-1.5 text-sm rounded-none"
                      onClick={handleImportProcess}
                      iconLeft={<FileImportIcon className="w-3.5 h-3.5" />}
                    >
                      Import profile
                    </Button>
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      className="w-full justify-start px-3 py-1.5 text-sm rounded-none"
                      onClick={() => { setProfileMenuOpen(false); navigate('/admin/slicer-profiles'); }}
                      iconLeft={<EditIcon className="w-3.5 h-3.5" />}
                    >
                      Manage profiles
                    </Button>
                  </div>
                )}
              </div>
            </div>

            {/* Hidden file input for importing profiles */}
            {/* eslint-disable-next-line local/pf-no-raw-html-controls -- hidden file input requires native <input> for programmatic .click() trigger */}
            <input
              ref={importFileRef}
              type="file"
              accept=".json"
              className="sr-only"
              onChange={handleProfileFileImport}
              aria-hidden="true"
              tabIndex={-1}
            />

            {/* Select + Reset + Save row */}
            {(availableProcessProfiles.length > 0 || customProcessProfiles.length > 0) ? (
              <>
                <div className="flex gap-1">
                  <Select
                    value={selectedProcessPresetId}
                    onChange={e => setSelectedProcessPresetId(e.target.value)}
                    className="flex-1 min-w-0"
                  >
                    <option value="">-- Select Process Profile --</option>
                    {/* Custom profiles first with ★ indicator */}
                    {customProcessProfiles.length > 0 && (
                      <optgroup label="★ My Profiles">
                        {customProcessProfiles.map(profile => (
                          <option key={`custom-${profile.id}`} value={`custom:${profile.id}`}>
                            ★ {profile.name}
                          </option>
                        ))}
                      </optgroup>
                    )}
                    {/* System presets grouped by quality level */}
                    {processProfilesByQuality.map(([quality, profiles]) => (
                      <optgroup key={quality} label={quality.charAt(0).toUpperCase() + quality.slice(1)}>
                        {profiles.map(profile => (
                          <option key={profile.name} value={`system:${profile.name}`}>
                            {profile.name} ({profile.layerHeight}mm)
                          </option>
                        ))}
                      </optgroup>
                    ))}
                  </Select>
                  <Button
                    type="button"
                    variant="secondary"
                    size="sm"
                    className="px-2 shrink-0"
                    title="Reset settings to defaults"
                    aria-label="Reset settings to defaults"
                    onClick={handleResetProcessProfile}
                  >
                    <RefreshIcon className="w-4 h-4" />
                  </Button>
                  <Button
                    type="button"
                    variant="secondary"
                    size="sm"
                    className="px-2 shrink-0"
                    title="Save as custom profile"
                    aria-label="Save as custom profile"
                    disabled={!selectedProcessPresetId}
                    onClick={handleCopyProcess}
                  >
                    <SaveIcon className="w-4 h-4" />
                  </Button>
                </div>
                {/* Inline save-as name input */}
                {saveProfileState.open && (
                  <div className="mt-2 flex gap-1 items-center">
                    <Input
                      type="text"
                      value={saveProfileState.name}
                      onChange={(e: React.ChangeEvent<HTMLInputElement>) => setSaveProfileState(s => ({ ...s, name: e.target.value }))}
                      placeholder="Profile name..."
                      className="flex-1 text-sm"
                      autoFocus
                      onKeyDown={(e: React.KeyboardEvent<HTMLInputElement>) => {
                        if (e.key === 'Enter') { e.preventDefault(); void handleConfirmSaveProfile(); }
                        if (e.key === 'Escape') setSaveProfileState({ open: false, name: '' });
                      }}
                      aria-label="New profile name"
                    />
                    <Button type="button" size="sm" variant="primary" onClick={() => void handleConfirmSaveProfile()} disabled={!saveProfileState.name.trim()}>
                      Save
                    </Button>
                    <Button type="button" size="sm" variant="secondary" onClick={() => setSaveProfileState({ open: false, name: '' })}>
                      Cancel
                    </Button>
                  </div>
                )}
              </>
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
              advancedSettings={advancedProcessSettings}
              onAdvancedSettingsChange={setAdvancedProcessSettings}
            />
          </div>

          {/* MODEL SELECTION - Uses searchable picker modal */}
          <ModelSelector
            useModelPicker={useModelPicker}
            onToggleMode={() => {
              setUseModelPicker(v => !v);
              if (useModelPicker) {
                setSelectedModelId('');
                setModelFileUrl('');
                setModelFileName('');
                setBedModels([]);
              }
            }}
            models={models}
            modelsError={modelsError}
            selectedModelId={selectedModelId}
            onModelIdChange={setSelectedModelId}
            fileUrl={modelFileUrl}
            onFileUrlChange={setModelFileUrl}
            fileName={modelFileName}
            onFileNameChange={setModelFileName}
            pickerOpen={modelPickerOpen}
            onPickerOpenChange={setModelPickerOpen}
          />

          {/* STL Preview Button */}
          {(selectedModelId || modelFileUrl) && (
            <Button
              type="button"
              onClick={() => setIsSTLPreviewOpen(true)}
              variant="secondary"
              size="sm"
              className="w-full"
              iconLeft={<EyeIcon className="w-4 h-4" />}
            >
              Preview 3D Model
            </Button>
          )}

          {/* STATUS MESSAGES */}
          {error && <Alert type="error">{error}</Alert>}
          {!submittedJobId && message && <Alert type="success">{message}</Alert>}

          {/* REAL-TIME JOB PROGRESS */}
          {submittedJobId && (
            <SliceJobProgressPanel
              jobId={submittedJobId}
              progress={jobProgress}
              onNewJob={() => {
                setSubmittedJobId(null);
                setMessage(null);
                setModelFileUrl('');
                setModelFileName('');
                setBedModels([]);
              }}
              onRetry={() => {
                setSubmittedJobId(null);
                setError(null);
                setMessage(null);
              }}
            />
          )}

        </div>

        {/* RIGHT SIDE: 3D Workspace */}
        <div className="flex-1 flex flex-col min-h-0">
          <div className="bg-pf-panel border border-pf-border rounded-lg flex-1 overflow-hidden flex flex-col min-h-0">
            {selectedPrinterId ? (
              <SlicerWorkspace
                bedConfig={workspaceBedConfig}
                models={workspaceModels}
                selectedModelId={selectedBedModelId ?? undefined}
                onModelSelect={handleWorkspaceModelSelect}
                onModelTransform={handleWorkspaceModelTransform}
                onAddModel={handleWorkspaceAddModel}
                onSettingsProfiles={handleWorkspaceSettingsProfiles}
                onSlice={submitSliceJob}
                slicing={submitMutation.isPending}
                canSlice={!submittedJobId && workspaceModels.length > 0 && !!selectedMachineProfileId && !!selectedFilamentProfileId && !!selectedProcessPresetId}
                onToggleSidebar={() => setSidebarOpen(v => !v)}
                sidebarOpen={sidebarOpen}
                className="h-full"
              />
            ) : (
              <div className="h-full w-full flex items-center justify-center text-pf-text-muted bg-pf-bg-0">
                <div className="text-center">
                  <p className="text-sm">Select a printer to open the slicer workspace</p>
                </div>
              </div>
            )}
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
          onClose={() => {
            setIsCloneProfilesModalOpen(false);
            setCloneProfilesDismissed(true); // Prevent re-opening on cancel
          }}
          printerId={selectedPrinterId}
          printerName={selectedPrinter.name}
          onSuccess={() => {
            // Invalidate profiles cache to reload when modal closes
            qc.invalidateQueries({ queryKey: ['slicerProfiles'] });
            qc.invalidateQueries({ queryKey: ['slicerProfilesHierarchy'] });
            qc.invalidateQueries({ queryKey: ['machineProfilesForModel'] });
          }}
        />
      )}
      
      {/* Profile Editor Modal - for editing selected profile settings */}
      <ProfileEditorModal
        isOpen={profileEditorOpen}
        onClose={() => setProfileEditorOpen(false)}
        profileType={profileEditorType}
        initialViewMode={profileEditorType === 'machine' ? 'advanced' : 'simple'}
        originalProfile={
          profileEditorType === 'machine' ? (selectedMachineProfile ?? null) :
          (selectedFilamentProfile ?? null)
        }
        onSaveSuccess={(profileId, profileName) => {
          // Invalidate custom profiles cache
          qc.invalidateQueries({ queryKey: ['customProfiles'] });
          // Show success message
          setMessage(`Custom profile "${profileName}" saved successfully`);
        }}
      />
    </div>
  );
};

export default NewSliceJobPage;

/* ─── Inline progress panel shown after job submission ─── */

function SliceJobProgressPanel({
  jobId,
  progress,
  onNewJob,
  onRetry,
}: {
  jobId: string;
  progress: ReturnType<typeof useSliceJobProgress>;
  onNewJob: () => void;
  onRetry: () => void;
}) {
  const isCompleted = progress.status === 'Completed';
  const isFailed = progress.status === 'Failed';
  const isCancelled = progress.status === 'Cancelled';
  const isTerminal = isCompleted || isFailed || isCancelled;
  const percent = progress.progressPercent;

  return (
    <div className="rounded-lg border border-pf-border bg-pf-bg-1/50 p-4 space-y-3">
      <div className="flex items-center justify-between">
        <h4 className="text-sm font-semibold text-pf-text-primary">
          Job Progress
        </h4>
        <span className="font-mono text-xs text-pf-text-secondary" title={jobId}>
          {jobId.substring(0, 8)}…
        </span>
      </div>

      {/* Progress bar */}
      <div className="space-y-1">
        <div className="flex items-center gap-2">
          <progress
            value={Math.min(isCompleted ? 100 : percent, 100)}
            max={100}
            className={`flex-1 h-2 rounded-full overflow-hidden [&::-webkit-progress-bar]:bg-pf-bg-2 [&::-webkit-progress-value]:rounded-full [&::-moz-progress-bar]:rounded-full ${
              isFailed
                ? '[&::-webkit-progress-value]:bg-pf-error [&::-moz-progress-bar]:bg-pf-error'
                : isCompleted
                  ? '[&::-webkit-progress-value]:bg-pf-success [&::-moz-progress-bar]:bg-pf-success'
                  : '[&::-webkit-progress-value]:bg-pf-accent [&::-moz-progress-bar]:bg-pf-accent'
            }`}
          />
          <span className="text-xs font-mono text-pf-text-secondary whitespace-nowrap">
            {isCompleted ? '100' : percent}%
          </span>
        </div>
        {progress.progressMessage && (
          <p className="text-xs text-pf-text-tertiary">{progress.progressMessage}</p>
        )}
      </div>

      {/* Status + metadata */}
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-pf-text-secondary">
        {progress.status && (
          <span>Status: <strong className="text-pf-text-primary">{progress.status}</strong></span>
        )}
        {progress.estimatedPrintTimeSeconds != null && progress.estimatedPrintTimeSeconds > 0 && (
          <span>Est. print: {sliceJobSvc.formatPrintTime(progress.estimatedPrintTimeSeconds)}</span>
        )}
        {progress.filamentUsedGrams != null && progress.filamentUsedGrams > 0 && (
          <span>Filament: {sliceJobSvc.formatFilamentUsed(progress.filamentUsedGrams)}</span>
        )}
      </div>

      {/* Completion actions */}
      {isCompleted && (
        <div className="flex items-center gap-2 pt-1">
          {progress.resultFileUrl && (
            <Button
              variant="success"
              size="sm"
              iconLeft={<DownloadIcon className="w-3.5 h-3.5" />}
              onClick={() => window.open(`/api/artifacts/job/${jobId}`, '_blank')}
            >
              Download G-code
            </Button>
          )}
          <Button variant="secondary" size="sm" onClick={onNewJob}>
            New Job
          </Button>
        </div>
      )}

      {/* Failure actions */}
      {isFailed && (
        <div className="space-y-2 pt-1">
          {progress.error && (
            <p className="text-xs text-pf-error bg-pf-error/10 rounded px-2 py-1 wrap-break-word">
              {progress.error}
            </p>
          )}
          <div className="flex items-center gap-2">
            <Button variant="primary" size="sm" onClick={onRetry}>
              Retry
            </Button>
            <Button variant="secondary" size="sm" onClick={onNewJob}>
              New Job
            </Button>
          </div>
        </div>
      )}

      {/* Cancelled */}
      {isCancelled && (
        <div className="flex items-center gap-2 pt-1">
          <Button variant="primary" size="sm" onClick={onRetry}>
            Retry
          </Button>
          <Button variant="secondary" size="sm" onClick={onNewJob}>
            New Job
          </Button>
        </div>
      )}

      {/* Waiting / no SignalR connection yet */}
      {!isTerminal && !progress.status && (
        <p className="text-xs text-pf-text-tertiary italic">
          {progress.isConnected ? 'Waiting for updates…' : 'Connecting to real-time updates…'}
        </p>
      )}
    </div>
  );
}
