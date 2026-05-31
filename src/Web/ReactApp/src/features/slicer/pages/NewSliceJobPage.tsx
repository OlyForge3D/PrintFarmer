import React, { useState, useEffect, useMemo, useCallback } from 'react';
import { useSearchParams, useNavigate } from 'react-router';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { sliceJobService, SubmitSliceJobRequest } from '@/services/sliceJobService';
import { 
  slicerProfilesService,
  type CustomProfile,
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
import { ProcessProfileEditorModal } from '@/features/slicer/components/ProcessProfileEditorModal';
import {
  SlicerSettingsPanel,
  BED_TYPE_OPTIONS,
  type OrcaProcessSettings,
} from '@/features/slicer/components/settings';
import { PrinterSlicerSelector, SlicerSelector, type PrinterForSlicing } from '../components/job';
import { FilamentProfileDropdown, FILTER_STORAGE_KEY, type FilamentFilterConfig } from '../components/CascadingMenuDropdown';
import { getPrimaryNozzleDiameter } from '../utils/profileMatcher';
import { isMultiToolhead, getPhysicalToolheads } from '../utils/profileMatcher';
import { isZipFile, extractOrcaBundle } from '@/features/slicer/orca/utils/orcaBundleExtractor';
import type { Model3DBasic } from '../components/job/types';
import type { ModelListItem } from '@/types/models';
import { SearchablePickerModal } from '@/common/components/SearchablePickerModal';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Alert, Input, Select } from '@/common/components/ui';
import { LayersIcon, EditIcon, DownloadIcon, RefreshIcon, SaveIcon, MoreVerticalIcon, CopyIcon, FileImportIcon } from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { NozzleTypeLabels, NozzleTypeStringLabels } from '@/types/api';
import { STLPreviewModal } from '@/features/models3d/components/3d/STLPreviewModal';
import { useSTLFile } from '@/common/hooks/useSTLFile';
import { useSliceJobProgress } from '@/features/slicer/hooks/useSliceJobProgress';
import { SlicerWorkspace, type LoadedModel, type BedConfig } from '@/features/slicer/components/viewer';
import * as THREE from 'three';
import { sliceJobService as sliceJobSvc } from '@/services/sliceJobService';

// Removed MATERIAL_PRESETS constant - now using API-driven filament profiles

const NOZZLE_MATCH_TOLERANCE = 0.01;
const NOZZLE_VALUE_DECIMALS = 3;

interface NozzleOption {
  value: string;
  diameter: number;
  label: string;
}

/**
 * Helper function to convert OrcaProcessProfile to OrcaProcessSettings
 * Maps profile data to settings structure, using defaults for missing values
 */
function convertOrcaProcessProfileToSettings(profile: OrcaProcessProfile | undefined): OrcaProcessSettings {
  if (!profile) return {} as OrcaProcessSettings;

  // Parse settings from profile if available
  const profileSettings = (profile.settings ?? {}) as Record<string, unknown>;

  return {
    layer_height: profile.layerHeight,
    sparse_infill_density: profile.infillPercentage,
    outer_wall_speed: profile.printSpeed,
    enable_support: profile.supports,
    // Spread any additional parsed settings from profile.settings
    ...profileSettings,
  } as OrcaProcessSettings;
}

function formatNozzleDiameter(diameter: number): string {
  return Number(diameter.toFixed(NOZZLE_VALUE_DECIMALS)).toString();
}

function parseNozzleDiameter(value: unknown): number | undefined {
  if (Array.isArray(value)) {
    return parseNozzleDiameter(value[0]);
  }

  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === 'string') {
    const parsed = Number.parseFloat(value);
    return Number.isFinite(parsed) ? parsed : undefined;
  }

  return undefined;
}

function getCustomProfileNozzleDiameter(profile: CustomProfile): number | undefined {
  if (!profile.rawJson) {
    return undefined;
  }

  try {
    const parsed = JSON.parse(profile.rawJson) as Record<string, unknown>;
    const settings = parsed.settings as Record<string, unknown> | undefined;
    return parseNozzleDiameter(parsed.nozzle_diameter)
      ?? parseNozzleDiameter(parsed.nozzleDiameter)
      ?? parseNozzleDiameter(settings?.nozzle_diameter)
      ?? parseNozzleDiameter(settings?.nozzleDiameter);
  } catch {
    return undefined;
  }
}

function getMachineProfileNozzleDiameter(profile: OrcaMachineProfile | CustomProfile): number | undefined {
  if ('nozzleDiameter' in profile) {
    return parseNozzleDiameter(profile.nozzleDiameter);
  }

  return getCustomProfileNozzleDiameter(profile);
}

function machineProfileMatchesNozzle(profile: OrcaMachineProfile | CustomProfile, selectedDiameter: number | undefined): boolean {
  if (selectedDiameter === undefined) {
    return true;
  }

  const profileNozzleDiameter = getMachineProfileNozzleDiameter(profile);
  if (profileNozzleDiameter === undefined) {
    return true;
  }

  return Math.abs(profileNozzleDiameter - selectedDiameter) < NOZZLE_MATCH_TOLERANCE;
}

function getPrimaryNozzleTypeLabel(printer: PrinterForSlicing | undefined): string | undefined {
  const toolhead = printer?.toolheads?.find((candidate) => candidate.isPrimary) ?? printer?.toolheads?.[0];
  const nozzleType = toolhead?.nozzleType;

  if (typeof nozzleType === 'number') {
    return NozzleTypeLabels[nozzleType as keyof typeof NozzleTypeLabels];
  }

  if (typeof nozzleType !== 'string' || !nozzleType) {
    return undefined;
  }

  const normalizedNozzleType = nozzleType.replace(/[^a-z0-9]/gi, '').toLowerCase();
  const matchingKey = Object.keys(NozzleTypeStringLabels).find((key) => {
    return key.replace(/[^a-z0-9]/gi, '').toLowerCase() === normalizedNozzleType;
  });

  return matchingKey ? NozzleTypeStringLabels[matchingKey] : nozzleType;
}

function profileMentionsHighFlow(text: string): boolean {
  return /\bhf\b/i.test(text);
}

function getProcessLayerHeight(profile: OrcaProcessProfile): number {
  if (Number.isFinite(profile.layerHeight)) {
    return profile.layerHeight;
  }

  const match = profile.name.match(/([0-9]+(?:\.[0-9]+)?)\s*mm/i);
  if (!match) {
    return Number.POSITIVE_INFINITY;
  }

  const parsed = Number.parseFloat(match[1]);
  return Number.isFinite(parsed) ? parsed : Number.POSITIVE_INFINITY;
}

export const NewSliceJobPage: React.FC = () => {
  const STORAGE_KEYS = {
    printerId: 'sliceJob.selectedPrinterId',
    machineProfileId: 'sliceJob.selectedMachineProfileId',
    filamentProfileId: 'sliceJob.selectedFilamentProfileId',
    processProfileId: 'sliceJob.selectedProcessProfileId',
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
  const [selectedNozzleFilter, setSelectedNozzleFilter] = useState<string>('');
  const [selectedFilamentProfileId, setSelectedFilamentProfileId] = useState<string>('');

  // Filament filter config (persisted in localStorage)
  const [filamentFilterConfig, setFilamentFilterConfig] = useState<FilamentFilterConfig>(() => {
    try {
      const stored = localStorage.getItem(FILTER_STORAGE_KEY);
      if (stored) return JSON.parse(stored) as FilamentFilterConfig;
    } catch { /* ignore */ }
    return { hiddenManufacturers: [], hiddenMaterials: [] };
  });

  const handleFilamentFilterChange = useCallback((config: FilamentFilterConfig) => {
    setFilamentFilterConfig(config);
    try { localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify(config)); } catch { /* ignore */ }
  }, []);

  // === Multi-extruder filament selection (for multi-toolhead printers) ===
  // Maps extruder index → filament profile name. Only used when printer has >1 physical toolhead.
  const [extruderFilamentProfileIds, setExtruderFilamentProfileIds] = useState<Record<number, string>>({});

  // === Bed Type Override ===
  const [selectedBedType, setSelectedBedType] = useState<string>('');

  // === OrcaSlicer-style Settings Panel ===
  const [slicerSettings, setSlicerSettings] = useState<OrcaProcessSettings>({} as OrcaProcessSettings);
  const [originalProcessSettings, setOriginalProcessSettings] = useState<Record<string, unknown>>({});
  const [advancedProcessSettings, setAdvancedProcessSettings] = useState<Record<string, unknown>>({});
  
  const [profileMenuOpen, setProfileMenuOpen] = useState(false);
  const profileMenuRef = React.useRef<HTMLDivElement>(null);
  const importFileRef = React.useRef<HTMLInputElement>(null);
  const [saveProfileState, setSaveProfileState] = useState<{ open: boolean; name: string }>({ open: false, name: '' });

  const [machineMenuOpen, setMachineMenuOpen] = useState(false);
  const machineMenuRef = React.useRef<HTMLDivElement>(null);
  const importMachineFileRef = React.useRef<HTMLInputElement>(null);

  const [filamentMenuOpen, setFilamentMenuOpen] = useState(false);
  const filamentMenuRef = React.useRef<HTMLDivElement>(null);
  const importFilamentFileRef = React.useRef<HTMLInputElement>(null);

  // Callback for settings panel changes
  const handleSlicerSettingsChange = useCallback((newSettings: OrcaProcessSettings) => {
    setSlicerSettings((prev) => ({ ...prev, ...newSettings }));
  }, []);

  // === Process Profile Management handlers ===
  const handleResetProcessProfile = useCallback(() => {
    setSlicerSettings({} as OrcaProcessSettings);
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
      qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
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
    const fileArray = Array.from(e.target.files ?? []);
    if (fileArray.length === 0) return;

    // Reset the input so re-importing the same file triggers onChange
    e.target.value = '';

    let importedCount = 0;
    let failedCount = 0;

    for (const file of fileArray) {
      const isBundle = file.name.endsWith('.orca_printer') || file.name.endsWith('.orca_filament');

      try {
        if (isBundle) {
          const buffer = await file.arrayBuffer();
          if (!isZipFile(buffer)) {
            toast.error(`${file.name}: not a valid OrcaSlicer bundle`);
            failedCount++;
            continue;
          }
          const bundleJson = await extractOrcaBundle(buffer);
          const bundle = JSON.parse(bundleJson) as { process?: Array<{ name?: string }> };
          const processProfiles = bundle.process ?? [];

          if (processProfiles.length === 0) {
            toast.info(`${file.name}: no process profiles found`);
            continue;
          }

          for (const profile of processProfiles) {
            await slicerProfilesService.uploadProfile({
              rawJson: JSON.stringify(profile),
              profileType: 'process',
              name: profile.name || 'Unnamed profile',
            });
          }

          importedCount += processProfiles.length;
        } else {
          const text = await file.text();
          const parsed = JSON.parse(text) as Record<string, unknown>;
          await slicerProfilesService.uploadProfile({
            rawJson: text,
            profileType: 'process',
            name: (parsed.name as string) || file.name.replace('.json', ''),
          });
          importedCount++;
        }
      } catch {
        toast.error(`Failed to import ${file.name}`);
        failedCount++;
      }
    }

    if (importedCount > 0) {
      toast.success(`Imported ${importedCount} process profile(s)${failedCount > 0 ? ` (${failedCount} failed)` : ''}`);
    }

    qc.invalidateQueries({ queryKey: ['customProfiles'] });
    qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
  }, [qc]);

  // === Machine Profile Import handlers ===
  const handleImportMachine = useCallback(() => {
    setMachineMenuOpen(false);
    importMachineFileRef.current?.click();
  }, []);

  const handleMachineFileImport = useCallback(async (e: React.ChangeEvent<HTMLInputElement>) => {
    const fileArray = Array.from(e.target.files ?? []);
    if (fileArray.length === 0) return;
    e.target.value = '';

    let importedCount = 0;
    let failedCount = 0;

    for (const file of fileArray) {
      const isBundle = file.name.endsWith('.orca_printer');

      try {
        if (isBundle) {
          const buffer = await file.arrayBuffer();
          if (!isZipFile(buffer)) {
            toast.error(`${file.name}: not a valid OrcaSlicer bundle`);
            failedCount++;
            continue;
          }
          const bundleJson = await extractOrcaBundle(buffer);
          const bundle = JSON.parse(bundleJson) as { printer?: Array<{ name?: string }> };
          const machineProfiles = bundle.printer ?? [];

          if (machineProfiles.length === 0) {
            toast.info(`${file.name}: no machine profiles found`);
            continue;
          }

          for (const profile of machineProfiles) {
            await slicerProfilesService.uploadProfile({
              rawJson: JSON.stringify(profile),
              profileType: 'machine',
              name: profile.name || 'Unnamed profile',
            });
          }

          importedCount += machineProfiles.length;
        } else {
          const text = await file.text();
          const parsed = JSON.parse(text) as Record<string, unknown>;
          await slicerProfilesService.uploadProfile({
            rawJson: text,
            profileType: 'machine',
            name: (parsed.name as string) || file.name.replace('.json', ''),
          });
          importedCount++;
        }
      } catch {
        toast.error(`Failed to import ${file.name}`);
        failedCount++;
      }
    }

    if (importedCount > 0) {
      toast.success(`Imported ${importedCount} machine profile(s)${failedCount > 0 ? ` (${failedCount} failed)` : ''}`);
    }

    qc.invalidateQueries({ queryKey: ['customProfiles'] });
    qc.invalidateQueries({ queryKey: ['machineProfilesForPrinter'] });
    qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
  }, [qc]);

  // === Filament Profile Import handlers ===
  const handleImportFilament = useCallback(() => {
    setFilamentMenuOpen(false);
    importFilamentFileRef.current?.click();
  }, []);

  const handleFilamentFileImport = useCallback(async (e: React.ChangeEvent<HTMLInputElement>) => {
    const fileArray = Array.from(e.target.files ?? []);
    if (fileArray.length === 0) return;
    e.target.value = '';

    let importedCount = 0;
    let failedCount = 0;

    for (const file of fileArray) {
      const isBundle = file.name.endsWith('.orca_filament');

      try {
        if (isBundle) {
          const buffer = await file.arrayBuffer();
          if (!isZipFile(buffer)) {
            toast.error(`${file.name}: not a valid OrcaSlicer bundle`);
            failedCount++;
            continue;
          }
          const bundleJson = await extractOrcaBundle(buffer);
          const bundle = JSON.parse(bundleJson) as { filament?: Array<{ name?: string }> };
          const filamentProfiles = bundle.filament ?? [];

          if (filamentProfiles.length === 0) {
            toast.info(`${file.name}: no filament profiles found`);
            continue;
          }

          for (const profile of filamentProfiles) {
            await slicerProfilesService.uploadProfile({
              rawJson: JSON.stringify(profile),
              profileType: 'filament',
              name: profile.name || 'Unnamed profile',
            });
          }

          importedCount += filamentProfiles.length;
        } else {
          const text = await file.text();
          const parsed = JSON.parse(text) as Record<string, unknown>;
          await slicerProfilesService.uploadProfile({
            rawJson: text,
            profileType: 'filament',
            name: (parsed.name as string) || file.name.replace('.json', ''),
          });
          importedCount++;
        }
      } catch {
        toast.error(`Failed to import ${file.name}`);
        failedCount++;
      }
    }

    if (importedCount > 0) {
      toast.success(`Imported ${importedCount} filament profile(s)${failedCount > 0 ? ` (${failedCount} failed)` : ''}`);
    }

    qc.invalidateQueries({ queryKey: ['customProfiles'] });
    qc.invalidateQueries({ queryKey: ['filamentProfilesAll'] });
    qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
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

  useEffect(() => {
    if (!machineMenuOpen) return;
    const handler = (e: MouseEvent) => {
      if (machineMenuRef.current && !machineMenuRef.current.contains(e.target as Node)) {
        setMachineMenuOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [machineMenuOpen]);

  useEffect(() => {
    if (!filamentMenuOpen) return;
    const handler = (e: MouseEvent) => {
      if (filamentMenuRef.current && !filamentMenuRef.current.contains(e.target as Node)) {
        setFilamentMenuOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [filamentMenuOpen]);

  // === Model Selection ===
  const [modelFileUrl, setModelFileUrl] = useState('');
  const [modelFileName, setModelFileName] = useState('');
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
  const [processEditorOpen, setProcessEditorOpen] = useState(false);
  const [sidebarOpen, setSidebarOpen] = useState(true);

  // Post-submission progress tracking
  const [submittedJobId, setSubmittedJobId] = useState<string | null>(null);
  const jobProgress = useSliceJobProgress(submittedJobId);

  // Auto-clear submittedJobId when the job reaches a terminal state
  useEffect(() => {
    if (jobProgress.status === 'completed' || jobProgress.status === 'failed') {
      const timer = setTimeout(() => {
        setSubmittedJobId(prev => prev ? null : prev);
      }, 3000);
      return () => clearTimeout(timer);
    }
  }, [jobProgress.status]);
  
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

  // Detect multi-toolhead printers (e.g., Bambu H2D, Prusa XL multi-tool)
  const printerIsMultiToolhead = useMemo(
    () => isMultiToolhead(selectedPrinterForSlicing),
    [selectedPrinterForSlicing]
  );
  const physicalToolheads = useMemo(
    () => getPhysicalToolheads(selectedPrinterForSlicing),
    [selectedPrinterForSlicing]
  );

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
      return { url: undefined, format: undefined, bedModelUrl: undefined };
    }

    // Look up asset by manufacturer and model name from local asset service
    const asset = assetService.getAsset(selectedPrinterWithDetails.manufacturerName, selectedPrinterWithDetails.modelName);

    if (asset?.bedTexture) {
      return {
        url: asset.bedTexture,
        format: asset.bedTextureFormat as 'svg' | 'png' | undefined,
        bedModelUrl: asset.bedModel,
      };
    }

    // If local asset service doesn't have it, return undefined
    // Don't use API fallback as it may return 404 and cause TextureLoader errors
    return { url: undefined, format: undefined, bedModelUrl: asset?.bedModel };
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

  // === CUSTOM PROFILES (Hybrid Architecture) ===
  // Fetch user's custom profiles to merge with system profiles
  // NOTE: Declared before selectedMachineProfile so custom machine profiles can be resolved
  const { data: customProfilesData, isLoading: isCustomProfilesLoading } = useQuery({
    queryKey: ['customProfiles'],
    queryFn: () => slicerProfilesService.listCustomProfiles(),
    staleTime: 30_000
  });

  // Get the selected machine profile object (system or custom/imported)
  const selectedMachineProfile = useMemo(() => {
    if (!selectedMachineProfileId) return null;
    if (machineProfilesData?.length) {
      const system = machineProfilesData.find(p => p.name === selectedMachineProfileId);
      if (system) return system;
    }
    // Check custom/imported machine profiles
    const customMachine = customProfilesData?.profiles?.filter(p => p.profileType === 'machine') ?? [];
    const custom = customMachine.find(p => p.name === selectedMachineProfileId);
    if (custom) {
      // Keep original profile name for compatibility queries; store printer_model separately for display
      let printerModel: string | undefined;
      if (custom.rawJson) {
        try {
          const parsed = JSON.parse(custom.rawJson) as Record<string, unknown>;
          if (typeof parsed.printer_model === 'string' && parsed.printer_model) {
            printerModel = parsed.printer_model;
          }
        } catch { /* ignore parse errors */ }
      }
      return { ...custom, printerModel } as OrcaMachineProfile;
    }
    return null;
  }, [selectedMachineProfileId, machineProfilesData, customProfilesData]);

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

  // Filter custom profiles by type for each selector
  // Machine profiles filtered by selected printer (name or rawJson metadata matching)
  const customMachineProfiles = useMemo(() => {
    const allCustomMachine = customProfilesData?.profiles?.filter(p => p.profileType === 'machine') ?? [];
    if (!selectedPrinterForSlicing?.manufacturerName && !selectedPrinterForSlicing?.modelName) {
      return allCustomMachine;
    }
    const mfr = selectedPrinterForSlicing.manufacturerName?.toLowerCase() ?? '';
    const model = selectedPrinterForSlicing.modelName?.toLowerCase() ?? '';
    return allCustomMachine.filter(p => {
      // Try to extract printer_model from rawJson
      if (p.rawJson) {
        try {
          const parsed = JSON.parse(p.rawJson) as Record<string, unknown>;
          const printerModel = (parsed.printer_model as string)?.toLowerCase();
          if (printerModel) {
            // Match if printer_model contains model name words or vice versa
            const modelWords = model.split(/[\s\-_]+/).filter(w => w.length > 2);
            if (modelWords.some(w => printerModel.includes(w))) return true;
            if (printerModel.split(/[\s\-_]+/).some(w => model.includes(w))) return true;
            return false;
          }
        } catch { /* fall through to name matching */ }
      }
      // Fall back to fuzzy name matching against manufacturer + model
      const nameLower = p.name.toLowerCase();
      const modelWords = model.split(/[\s\-_]+/).filter(w => w.length > 2);
      const mfrWords = mfr.split(/[\s\-_]+/).filter(w => w.length > 2);
      const matchesModel = modelWords.length > 0 && modelWords.some(w => nameLower.includes(w));
      const matchesMfr = mfrWords.length > 0 && mfrWords.some(w => nameLower.includes(w));
      // Show if name matches model, or if no matching info show it anyway
      return matchesModel || (matchesMfr && modelWords.length === 0);
    });
  }, [customProfilesData, selectedPrinterForSlicing]);

  // Filament profiles filtered by selected machine profile compatibility
  const customFilamentProfiles = useMemo(() => {
    const allCustomFilament = customProfilesData?.profiles?.filter(p => p.profileType === 'filament') ?? [];
    if (!selectedMachineProfileId) return allCustomFilament;
    return allCustomFilament.filter(p => {
      if (p.rawJson) {
        try {
          const parsed = JSON.parse(p.rawJson) as Record<string, unknown>;
          const compatible = parsed.compatible_printers as string[] | undefined;
          if (compatible && compatible.length > 0) {
            return compatible.some(c => c === selectedMachineProfileId);
          }
        } catch { /* hide profile if can't parse */ }
      }
      return false;
    });
  }, [customProfilesData, selectedMachineProfileId]);

  const customProcessProfiles = useMemo(() => {
    const allCustomProcess = customProfilesData?.profiles?.filter(p => p.profileType === 'process') ?? [];
    if (!selectedMachineProfileId) return allCustomProcess;
    return allCustomProcess.filter(p => {
      if (p.rawJson) {
        try {
          const parsed = JSON.parse(p.rawJson) as Record<string, unknown>;
          const compatible = parsed.compatible_printers as string[] | undefined;
          if (compatible && compatible.length > 0) {
            return compatible.some(c => c === selectedMachineProfileId);
          }
        } catch { /* hide profile if can't parse */ }
      }
      return false;
    });
  }, [customProfilesData, selectedMachineProfileId]);

  // Combined loading state for profile queries
  // Combined loading state for profile queries
  const isProfilesLoading = isMachineProfilesLoading || isFilamentProfilesLoading || isProcessProfilesLoading;

  // === Profile Selection Computed Values (Incremental Loading) ===
  // Machine profiles are loaded when printer is selected (via modelId query)
  // Filament/Process profiles are loaded when machine profile is selected

  const nozzleOptions = useMemo<NozzleOption[]>(() => {
    const nozzlesByValue = new Map<string, number>();
    const profiles = [...machineProfilesData, ...customMachineProfiles];

    profiles.forEach((profile) => {
      const nozzleDiameter = getMachineProfileNozzleDiameter(profile);
      if (nozzleDiameter === undefined || nozzleDiameter <= 0) {
        return;
      }

      nozzlesByValue.set(formatNozzleDiameter(nozzleDiameter), nozzleDiameter);
    });

    return Array.from(nozzlesByValue.entries())
      .sort(([, firstDiameter], [, secondDiameter]) => firstDiameter - secondDiameter)
      .map(([value, diameter]) => ({
        value,
        diameter,
        label: `${formatNozzleDiameter(diameter)} mm`,
      }));
  }, [machineProfilesData, customMachineProfiles]);

  const selectedNozzleDiameter = useMemo(() => {
    return parseNozzleDiameter(selectedNozzleFilter);
  }, [selectedNozzleFilter]);

  const filteredCustomMachineProfiles = useMemo(() => {
    return customMachineProfiles.filter((profile) => machineProfileMatchesNozzle(profile, selectedNozzleDiameter));
  }, [customMachineProfiles, selectedNozzleDiameter]);

  // Machine profiles for the selected printer (from incremental query), filtered by nozzle when selected.
  const availableMachineProfiles = useMemo(() => {
    return (machineProfilesData ?? []).filter((profile) => machineProfileMatchesNozzle(profile, selectedNozzleDiameter));
  }, [machineProfilesData, selectedNozzleDiameter]);

  const hasVisibleMachineProfiles = availableMachineProfiles.length > 0 || filteredCustomMachineProfiles.length > 0;

  // Process profiles for the selected machine (from incremental query)
  const availableProcessProfiles = useMemo(() => {
    return processProfilesData ?? [];
  }, [processProfilesData]);

  // Split process profiles into User presets (first) and System presets (second).
  // Apply machine compatibility guards and sort each group by layer height (smallest -> largest).
  const processProfilesBySource = useMemo(() => {
    const profiles = processProfilesData ?? [];
    const selectedMachineName = selectedMachineProfile?.name ?? '';
    const selectedMachineLower = selectedMachineName.toLowerCase();
    const selectedIsHighFlow = profileMentionsHighFlow(selectedMachineName);

    const filtered = profiles.filter((profile) => {
      // Guard 1: Compatible printer names must include the selected machine when provided.
      if (selectedMachineName && Array.isArray(profile.compatiblePrinters) && profile.compatiblePrinters.length > 0) {
        const compatible = profile.compatiblePrinters.some((printerName) => printerName === selectedMachineName);
        if (!compatible) {
          return false;
        }
      }

      // Guard 2: Avoid mixing HF and non-HF variants for the same machine family.
      // Use both profile name and compatible-printer text for variant detection.
      const candidateText = `${profile.name} ${(profile.compatiblePrinters ?? []).join(' ')}`.toLowerCase();
      const candidateIsHighFlow = profileMentionsHighFlow(candidateText);

      // Apply this guard only when this is the same machine family (CORE One) and
      // the machine selection explicitly indicates HF/non-HF variant intent.
      if (selectedMachineLower.includes('core one')) {
        if (selectedIsHighFlow && !candidateIsHighFlow) {
          return false;
        }

        if (!selectedIsHighFlow && candidateIsHighFlow) {
          return false;
        }
      }

      return true;
    });

    const user: typeof profiles = [];
    const system: typeof profiles = [];
    for (const profile of filtered) {
      // isSystem is the canonical flag from the slicer worker; treat missing as system
      // so bundle profiles never accidentally land in the user group.
      if ((profile as { isSystem?: boolean }).isSystem === false) {
        user.push(profile);
      } else {
        system.push(profile);
      }
    }

    const byLayerHeightThenName = (a: OrcaProcessProfile, b: OrcaProcessProfile) => {
      const layerDelta = getProcessLayerHeight(a) - getProcessLayerHeight(b);
      if (Math.abs(layerDelta) > 0.0001) {
        return layerDelta;
      }
      return a.name.localeCompare(b.name);
    };

    user.sort(byLayerHeightThenName);
    system.sort(byLayerHeightThenName);

    return { user, system };
  }, [processProfilesData, selectedMachineProfile]);

  useEffect(() => {
    const optionValues = nozzleOptions.map((option) => option.value);

    if (!selectedPrinterId) {
      if (selectedNozzleFilter) {
        setSelectedNozzleFilter('');
      }
      return;
    }

    if (!selectedPrinterForSlicing) {
      return;
    }

    if (optionValues.length === 0) {
      if (selectedNozzleFilter) {
        setSelectedNozzleFilter('');
      }
      return;
    }

    if (selectedNozzleFilter && optionValues.includes(selectedNozzleFilter)) {
      return;
    }

    const primaryNozzleDiameter = getPrimaryNozzleDiameter(selectedPrinterForSlicing);
    const primaryNozzleValue = primaryNozzleDiameter ? formatNozzleDiameter(primaryNozzleDiameter) : '';
    const nextNozzleValue = primaryNozzleValue && optionValues.includes(primaryNozzleValue)
      ? primaryNozzleValue
      : optionValues[0];

    setSelectedNozzleFilter(nextNozzleValue);
  }, [nozzleOptions, selectedNozzleFilter, selectedPrinterForSlicing, selectedPrinterId]);

  // Auto-select machine profile when printer is selected and machine profiles are loaded
  // Keep the current selection if it is still valid (restored from previous session).
  // This effect uses the selected nozzle filter when available.
  useEffect(() => {
    if (!selectedPrinterForSlicing) return;

    // Defer all setState calls to avoid synchronous updates in effect body
    queueMicrotask(() => {
      // Set manufacturer/model from printer for display purposes
      const mfgName = selectedPrinterForSlicing.manufacturerName;
      const modelName = selectedPrinterForSlicing.modelName;
      setSelectedManufacturer(mfgName || '');
      setSelectedPrinterModel(modelName || '');

      if ((selectedPrinterModelId && isMachineProfilesLoading) || isCustomProfilesLoading) {
        return;
      }

      // Keep current selection if valid — check both system and custom profiles
      const hasCurrent = !!selectedMachineProfileId && (
        availableMachineProfiles.some((profile) => profile.name === selectedMachineProfileId) ||
        filteredCustomMachineProfiles.some((profile) => profile.name === selectedMachineProfileId)
      );
      if (hasCurrent) {
        return;
      }

      const nextProfile = availableMachineProfiles[0] ?? filteredCustomMachineProfiles[0];
      if (nextProfile) {
        setSelectedMachineProfileId(nextProfile.name);
        return;
      }

      if (selectedMachineProfileId) {
        setSelectedMachineProfileId('');
      }
    });
  }, [selectedPrinterForSlicing, selectedMachineProfileId, selectedPrinterModelId, isMachineProfilesLoading, isCustomProfilesLoading, availableMachineProfiles, filteredCustomMachineProfiles]);

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
    // Don't suggest while custom profiles are still loading
    if (isCustomProfilesLoading) return false;
    // Don't suggest if any system or custom machine profile is currently selectable
    if (hasVisibleMachineProfiles) return false;
    // Suggest if machine profiles query completed but returned empty
    // (meaning OrcaSlicer has no profiles for this printer model)
    if (machineProfilesData.length === 0 && !isMachineProfilesLoading) return true;
    // Don't suggest if we have machine profiles (process profiles will load after machine selection)
    return false;
  }, [selectedPrinterId, selectedPrinterModelId, machineProfilesData.length, isMachineProfilesLoading, isCustomProfilesLoading, hasVisibleMachineProfiles, cloneProfilesDismissed]);

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

  // Unified resolved process profile for the modal editor — works for both system and custom presets
  const resolvedProcessProfile = useMemo((): OrcaProcessProfile | null => {
    if (selectedProcessProfile) return selectedProcessProfile;
    if (selectedCustomProcessProfile?.rawJson) {
      try {
        const parsed = JSON.parse(selectedCustomProcessProfile.rawJson) as Record<string, unknown>;
        return {
          name: selectedCustomProcessProfile.name,
          settings: parsed,
        } as OrcaProcessProfile;
      } catch {
        return null;
      }
    }
    return null;
  }, [selectedProcessProfile, selectedCustomProcessProfile]);

  // Fetch models for picker
  const { data: models = [], isLoading: isLoadingModels } = useQuery<ModelListItem[], Error>({
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
      const savedPrinterId = localStorage.getItem(STORAGE_KEYS.printerId);
      const savedMachineProfileId = localStorage.getItem(STORAGE_KEYS.machineProfileId);
      const savedFilamentProfileId = localStorage.getItem(STORAGE_KEYS.filamentProfileId);
      const savedProcessProfileId = localStorage.getItem(STORAGE_KEYS.processProfileId);

      queueMicrotask(() => {
        if (savedPrinterId) setSelectedPrinterId(savedPrinterId);
        if (savedMachineProfileId) setSelectedMachineProfileId(savedMachineProfileId);
        if (savedFilamentProfileId) setSelectedFilamentProfileId(savedFilamentProfileId);
        if (savedProcessProfileId) setSelectedProcessPresetId(savedProcessProfileId);
      });
    } catch { /* ignore */ }
  }, [STORAGE_KEYS.filamentProfileId, STORAGE_KEYS.machineProfileId, STORAGE_KEYS.printerId, STORAGE_KEYS.processProfileId]);

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
    if (selectedModelId) {
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
  }, [selectedModelId, models]);



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
      setOriginalProcessSettings(typedSettings as unknown as Record<string, unknown>);
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

    if (!selectedModelId && !modelFileUrl.trim()) {
      setError('Select a model or enter a URL');
      return;
    }
    if (!selectedModelId && modelFileUrl.trim() && !modelFileName.trim()) {
      setError('Model file name is required when using a URL');
      return;
    }

    if (!selectedProcessPresetId) {
      setError('Select a process profile');
      return;
    }

    // Multi-toolhead validation: ensure all extruders have filament assigned
    if (printerIsMultiToolhead) {
      const missingExtruders = physicalToolheads.filter(
        (_, i) => !extruderFilamentProfileIds[i]
      );
      if (missingExtruders.length > 0) {
        setError(`Assign a filament profile to all ${physicalToolheads.length} extruders`);
        return;
      }
    }

    // Build per-extruder filament profile name list for multi-toolhead
    const extruderFilamentNames = printerIsMultiToolhead
      ? physicalToolheads.map((_, i) => extruderFilamentProfileIds[i] ?? '')
      : undefined;

    const request: SubmitSliceJobRequest = {
      userId: user?.id || '',
      printerId: undefined,
      modelFileUrl: modelFileUrl,
      modelFileName: modelFileName,
      slicerEngine: slicerInfo.engine,
      slicerProfileJson: JSON.stringify({
            machineProfileName: selectedMachineProfileId,
            filamentProfileName: selectedFilamentProfileId,
            // Include per-extruder names in profile JSON so workers can access them
            ...(extruderFilamentNames ? { filamentProfileNames: extruderFilamentNames } : {}),
            processProfileName: selectedProcessPresetId.startsWith('system:')
              ? selectedProcessPresetId.slice('system:'.length)
              : selectedProcessPresetId.startsWith('custom:')
              ? selectedProcessPresetId.slice('custom:'.length)
              : selectedProcessPresetId,
            overrides: {
              ...slicerSettings,
              ...advancedProcessSettings,
              ...(selectedBedType ? { curr_bed_type: selectedBedType } : {}),
            },
          }),
      slicerProfileId: selectedProcessPresetId.startsWith('custom:')
            ? selectedProcessPresetId.slice('custom:'.length)
            : undefined,
      requiredCapabilitiesJson: '[]',
      priority: 1,
      modelTransformJson: bedModels[0]
        ? JSON.stringify({ rotation: bedModels[0].rotation, scale: bedModels[0].scale, position: bedModels[0].position })
        : undefined,
      extruderFilamentProfileNames: extruderFilamentNames,
      // Multi-model support: collect all bed model URLs that are server-hosted (non-blob)
      modelFileUrls: bedModels.length > 1
        ? bedModels.map(m => m.url).filter(u => u && !u.startsWith('blob:'))
        : undefined,
      // Per-model transforms: send each model's transform alongside its URL
      modelFileTransforms: bedModels.length > 1
        ? bedModels
            .filter(m => m.url && !m.url.startsWith('blob:'))
            .map(m => JSON.stringify({ rotation: m.rotation, scale: m.scale, position: m.position }))
        : undefined,
    };

    submitMutation.mutate(request);
  }, [
    advancedProcessSettings,
    bedModels,
    extruderFilamentProfileIds,
    modelFileName,
    modelFileUrl,
    physicalToolheads,
    printerIsMultiToolhead,
    selectedBedType,
    selectedFilamentProfileId,
    selectedMachineProfileId,
    selectedProcessPresetId,
    slicerInfo.engine,
    slicerSettings,
    submitMutation,
    user?.id,
    selectedModelId,
  ]);

  const onSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    submitSliceJob();
  };

  const workspaceBedConfig = useMemo<BedConfig>(() => ({
    width: bedDimensions?.width ?? 220,
    depth: bedDimensions?.depth ?? 220,
    height: bedDimensions?.height ?? 250,
    textureUrl: bedTextureInfo.url,
    textureFormat: bedTextureInfo.format,
    bedModelUrl: bedTextureInfo.bedModelUrl,
  }), [bedDimensions, bedTextureInfo.format, bedTextureInfo.url, bedTextureInfo.bedModelUrl]);

  // workspaceModels is the live bedModels state (multi-model accumulation)
  const workspaceModels = bedModels;

  const handleWorkspaceAddModel = useCallback(() => {
    setModelPickerOpen(true);
  }, []);

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

  // C1: Handle model replacement (e.g., after cut operation)
  const handleWorkspaceModelsReplace = useCallback((removedId: string, newModels: Array<{ url: string; fileName: string; geometry: THREE.BufferGeometry; position?: [number, number, number]; rotation?: [number, number, number]; scale?: [number, number, number] }>) => {
    setBedModels(prev => {
      const filtered = prev.filter(m => m.id !== removedId);
      const additions: LoadedModel[] = newModels.map((nm, i) => ({
        id: `${removedId}-cut-${i}-${Date.now()}`,
        url: nm.url,
        fileName: nm.fileName,
        fileType: 'stl' as const,
        position: nm.position ?? [0, 0, 0] as [number, number, number],
        rotation: nm.rotation ?? [0, 0, 0] as [number, number, number],
        scale: nm.scale ?? [1, 1, 1] as [number, number, number],
        geometry: nm.geometry,
      }));
      return [...filtered, ...additions];
    });
    setSelectedBedModelId(null);

    // Update submission source to use the first cut model with a non-blob server URL
    const serverModel = newModels.find(m => m.url && !m.url.startsWith('blob:'));
    if (serverModel) {
      // Normalize relative URLs to absolute, matching the pattern in the model selection useEffect
      const url = serverModel.url.startsWith('/') ? `${getApiBaseUrl()}${serverModel.url.replace(/^\/api/, '')}` : serverModel.url;
      setModelFileUrl(url);
      setModelFileName(serverModel.fileName || 'cut-piece.stl');
      setSelectedModelId('');
    } else {
      // All uploads failed (blob URLs only) — clear submission source so stale model can't be sliced
      setModelFileUrl('');
      setModelFileName('');
      setSelectedModelId('');
      toast.warning('Cut pieces could not be uploaded to server. Please retry or re-select a model to slice.');
    }
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
            Import printer profiles or create custom ones to configure your first slice job. Profiles define machine settings, filament parameters, and print quality presets.
          </p>
          <div className="flex gap-3">
            <Button
              variant="primary"
              onClick={() => navigate('/profiles/import')}
              data-testid="import-profiles-button"
            >
              Import Profiles
            </Button>
            <Button
              variant="secondary"
              onClick={() => navigate('/profiles')}
              data-testid="create-custom-profile-button"
            >
              Create Custom Profile
            </Button>
          </div>
        </div>
      </PageTemplate>
    );
  }

  const selectedNozzleTypeLabel = getPrimaryNozzleTypeLabel(selectedPrinterForSlicing);
  const nozzleFilterControl = selectedPrinterId && nozzleOptions.length > 0 ? (
    <div className="flex h-full min-h-19 flex-col justify-center rounded-md border border-pf-border bg-pf-bg-0 px-2 py-2">
      <label
        htmlFor="slicer-nozzle-filter"
        className="text-center text-xs font-semibold leading-tight text-pf-text-primary"
      >
        Nozzle
      </label>
      <Select
        id="slicer-nozzle-filter"
        value={selectedNozzleFilter}
        onChange={(event) => {
          setSelectedNozzleFilter(event.target.value);
          setSelectedMachineProfileId('');
        }}
        disabled={isMachineProfilesLoading || nozzleOptions.length <= 1}
        containerClassName="mt-1"
        className="h-8 rounded-sm py-1 pl-2 pr-6 text-center text-sm font-semibold"
      >
        {nozzleOptions.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </Select>
      <span
        className="mt-1 truncate text-center text-[11px] leading-tight text-pf-text-muted"
        title={selectedNozzleTypeLabel ?? undefined}
      >
        {selectedNozzleTypeLabel ?? 'Machine profiles'}
      </span>
    </div>
  ) : undefined;

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
            accessory={nozzleFilterControl}
            onPrinterChange={(printerId) => {
              setSelectedPrinterId(printerId);
              // Cascade reset: printer change resets all profile selections
              setSelectedMachineProfileId('');
              setSelectedNozzleFilter('');
              setSelectedFilamentProfileId('');
              setSelectedFilamentMaterial('');
              setSelectedProcessPresetId('');
              setExtruderFilamentProfileIds({});
              // Machine profile auto-select will happen via the effect
            }}
            className="bg-pf-panel border border-pf-border rounded-lg p-3"
          />

          {/* MACHINE PROFILE SELECTION - Filtered by selected printer */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-3 space-y-2">
            <div className="flex items-center justify-between">
              <label className="block text-sm font-semibold text-pf-text-primary">
                Machine
                {isProfilesLoading && <span className="ml-2 text-xs text-pf-text-muted">(Loading...)</span>}
              </label>
              <div className="relative" ref={machineMenuRef}>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  className="p-1 h-auto"
                  onClick={() => setMachineMenuOpen(v => !v)}
                  title="Machine profile options"
                  aria-label="Machine profile options menu"
                  aria-expanded={machineMenuOpen}
                  aria-haspopup="menu"
                >
                  <MoreVerticalIcon className="w-4 h-4" />
                </Button>
                {machineMenuOpen && (
                  <div className="absolute right-0 top-full mt-1 z-20 bg-pf-panel border border-pf-border rounded-lg shadow-lg min-w-40 py-1">
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      className="w-full justify-start px-3 py-1.5 text-sm rounded-none"
                      onClick={() => {
                        setMachineMenuOpen(false);
                        setProfileEditorType('machine');
                        setProfileEditorOpen(true);
                      }}
                      disabled={!selectedMachineProfileId}
                      iconLeft={<EditIcon className="w-3.5 h-3.5" />}
                    >
                      Edit settings
                    </Button>
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      className="w-full justify-start px-3 py-1.5 text-sm rounded-none"
                      onClick={handleImportMachine}
                      iconLeft={<FileImportIcon className="w-3.5 h-3.5" />}
                    >
                      Import profile
                    </Button>
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      className="w-full justify-start px-3 py-1.5 text-sm rounded-none"
                      onClick={() => { setMachineMenuOpen(false); navigate('/admin/slicer-profiles'); }}
                      iconLeft={<EditIcon className="w-3.5 h-3.5" />}
                    >
                      Manage profiles
                    </Button>
                  </div>
                )}
              </div>
            </div>
            {/* eslint-disable-next-line local/pf-no-raw-html-controls -- hidden file input requires native <input> for programmatic .click() trigger */}
            <input
              ref={importMachineFileRef}
              type="file"
              accept=".json,.orca_printer"
              multiple
              className="sr-only"
              onChange={handleMachineFileImport}
              aria-hidden="true"
              tabIndex={-1}
            />
            
            {/* Show printer info when selected */}
            {selectedPrinterForSlicing?.manufacturerName && selectedPrinterForSlicing?.modelName ? (
              <p className="text-xs text-pf-text-muted mb-2">
                Profiles for {selectedPrinterForSlicing.manufacturerName} {selectedPrinterForSlicing.modelName}
                {selectedPrinterForSlicing.nozzleDiameter && (
                  <span className="text-[11px]"> • {selectedPrinterForSlicing.nozzleDiameter}mm nozzle</span>
                )}
              </p>
            ) : (
              <p className="text-xs text-pf-warning mb-2">
                Select a printer above to see available machine profiles
              </p>
            )}

            {/* Machine Profile Selection (nozzle variants) - Custom profiles first, then system presets */}
            <Select
              label="Machine profile"
              value={selectedMachineProfileId}
              onChange={e => setSelectedMachineProfileId(e.target.value)}
              disabled={!selectedPrinterId || (availableMachineProfiles.length === 0 && filteredCustomMachineProfiles.length === 0) || isMachineProfilesLoading}
              className={`w-full ${!selectedPrinterId || isMachineProfilesLoading ? 'opacity-50' : ''}`}
            >
              <option value="">{isMachineProfilesLoading ? 'Loading...' : 'Select machine...'}</option>
              {/* Custom profiles first with ★ indicator */}
              {filteredCustomMachineProfiles.length > 0 && (
                <option disabled className="text-pf-text-muted">── My Profiles ──</option>
              )}
              {filteredCustomMachineProfiles.map(profile => (
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
            {selectedPrinterId && machineProfilesData.length > 0 && !hasVisibleMachineProfiles && selectedNozzleDiameter !== undefined && (
              <p className="text-xs text-pf-warning mt-1">No machine profiles available for the selected nozzle</p>
            )}
            {selectedPrinterId && machineProfilesData.length === 0 && !hasVisibleMachineProfiles && selectedManufacturer && selectedPrinterModel && (
              <p className="text-xs text-pf-warning mt-1">No machine profiles available for this printer model</p>
            )}
            {selectedPrinterId && !selectedManufacturer && (
              <p className="text-xs text-pf-warning mt-1">
                No matching slicer profiles found for this printer's manufacturer
              </p>
            )}
          </div>

          {/* FILAMENT PROFILE - cascading dropdown with manufacturer groups */}
          {/* Multi-toolhead: show per-extruder filament selectors */}
          {printerIsMultiToolhead ? (
            <div className="bg-pf-panel border border-pf-border rounded-lg p-3 space-y-2" data-testid="multi-extruder-filament-section">
              <div className="flex items-center justify-between">
                <label className="block text-sm font-semibold text-pf-text-primary">
                  Filament ({physicalToolheads.length} extruders)
                </label>
                <div className="relative" ref={filamentMenuRef}>
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    className="p-1 h-auto"
                    onClick={() => setFilamentMenuOpen(v => !v)}
                    title="Filament profile options"
                    aria-label="Filament profile options menu"
                    aria-expanded={filamentMenuOpen}
                    aria-haspopup="menu"
                  >
                    <MoreVerticalIcon className="w-4 h-4" />
                  </Button>
                  {filamentMenuOpen && (
                    <div className="absolute right-0 top-full mt-1 z-20 bg-pf-panel border border-pf-border rounded-lg shadow-lg min-w-40 py-1">
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        className="w-full justify-start px-3 py-1.5 text-sm rounded-none"
                        onClick={handleImportFilament}
                        iconLeft={<FileImportIcon className="w-3.5 h-3.5" />}
                      >
                        Import profile
                      </Button>
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        className="w-full justify-start px-3 py-1.5 text-sm rounded-none"
                        onClick={() => { setFilamentMenuOpen(false); navigate('/admin/slicer-profiles'); }}
                        iconLeft={<EditIcon className="w-3.5 h-3.5" />}
                      >
                        Manage profiles
                      </Button>
                    </div>
                  )}
                </div>
              </div>
              {/* eslint-disable-next-line local/pf-no-raw-html-controls -- hidden file input requires native <input> for programmatic .click() trigger */}
              <input
                ref={importFilamentFileRef}
                type="file"
                accept=".json,.orca_filament"
                multiple
                className="sr-only"
                onChange={handleFilamentFileImport}
                aria-hidden="true"
                tabIndex={-1}
              />

              {allFilamentProfiles.length > 0 || customFilamentProfiles.length > 0 ? (
                <div className="space-y-3">
                  {physicalToolheads.map((toolhead, idx) => {
                    const extruderLabel = toolhead.name || `Extruder ${idx + 1}`;
                    const nozzleInfo = toolhead.nozzleDiameter ? ` (${toolhead.nozzleDiameter}mm)` : '';
                    const currentMaterial = toolhead.currentMaterial ? ` • ${toolhead.currentMaterial}` : '';
                    const selectedName = extruderFilamentProfileIds[idx] ?? '';
                    const selectedProfile = allFilamentProfiles.find(p => p.name === selectedName);

                    return (
                      <div key={toolhead.id ?? idx} className="border border-pf-border/50 rounded-md p-2 space-y-1" data-testid={`extruder-filament-${idx}`}>
                        <div className="text-xs font-medium text-pf-text-secondary">
                          {extruderLabel}{nozzleInfo}{currentMaterial}
                        </div>
                        <FilamentProfileDropdown
                          profiles={allFilamentProfiles}
                          customProfiles={customFilamentProfiles.map(p => ({ id: p.id, name: p.name }))}
                          selectedProfileName={selectedName}
                          disabled={allFilamentProfiles.length === 0 && customFilamentProfiles.length === 0}
                          filterConfig={filamentFilterConfig}
                          onFilterConfigChange={handleFilamentFilterChange}
                          onSelect={(name, source) => {
                            setExtruderFilamentProfileIds(prev => ({ ...prev, [idx]: name }));
                            // Also keep the primary filament in sync (first extruder = primary)
                            if (idx === 0) {
                              setSelectedFilamentProfileId(name);
                              if (source === 'system') {
                                const sp = allFilamentProfiles.find(p => p.name === name);
                                setSelectedFilamentMaterial(sp?.material || '');
                              } else {
                                setSelectedFilamentMaterial('');
                              }
                            }
                          }}
                        />
                        {selectedProfile && (
                          <div className="text-xs text-pf-text-muted">
                            {selectedProfile.nozzleTemperature ?? 210}°C nozzle, {selectedProfile.bedTemperature ?? 60}°C bed
                          </div>
                        )}
                      </div>
                    );
                  })}
                </div>
              ) : (
                <div className="text-sm text-pf-text-muted p-2">
                  {isMachineProfilesLoading ? <span className="italic">Loading...</span> : 
                   selectedMachineProfileId && isFilamentProfilesLoading ? <span className="italic">Loading...</span> :
                   !selectedMachineProfileId ? 'Select a machine profile to see filament options' :
                   <span className="italic">No filament profiles available</span>}
                </div>
              )}
            </div>
          ) : (
          <div className="bg-pf-panel border border-pf-border rounded-lg p-3 space-y-2" data-testid="single-filament-section">
            <div className="flex items-center justify-between">
              <label className="block text-sm font-semibold text-pf-text-primary">Filament</label>
              <div className="relative" ref={filamentMenuRef}>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  className="p-1 h-auto"
                  onClick={() => setFilamentMenuOpen(v => !v)}
                  title="Filament profile options"
                  aria-label="Filament profile options menu"
                  aria-expanded={filamentMenuOpen}
                  aria-haspopup="menu"
                >
                  <MoreVerticalIcon className="w-4 h-4" />
                </Button>
                {filamentMenuOpen && (
                  <div className="absolute right-0 top-full mt-1 z-20 bg-pf-panel border border-pf-border rounded-lg shadow-lg min-w-40 py-1">
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      className="w-full justify-start px-3 py-1.5 text-sm rounded-none"
                      onClick={() => {
                        setFilamentMenuOpen(false);
                        setProfileEditorType('filament');
                        setProfileEditorOpen(true);
                      }}
                      disabled={!selectedFilamentProfileId}
                      iconLeft={<EditIcon className="w-3.5 h-3.5" />}
                    >
                      Edit settings
                    </Button>
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      className="w-full justify-start px-3 py-1.5 text-sm rounded-none"
                      onClick={handleImportFilament}
                      iconLeft={<FileImportIcon className="w-3.5 h-3.5" />}
                    >
                      Import profile
                    </Button>
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      className="w-full justify-start px-3 py-1.5 text-sm rounded-none"
                      onClick={() => { setFilamentMenuOpen(false); navigate('/admin/slicer-profiles'); }}
                      iconLeft={<EditIcon className="w-3.5 h-3.5" />}
                    >
                      Manage profiles
                    </Button>
                  </div>
                )}
              </div>
            </div>
            {/* eslint-disable-next-line local/pf-no-raw-html-controls -- hidden file input requires native <input> for programmatic .click() trigger */}
            <input
              ref={importFilamentFileRef}
              type="file"
              accept=".json,.orca_filament"
              multiple
              className="sr-only"
              onChange={handleFilamentFileImport}
              aria-hidden="true"
              tabIndex={-1}
            />
            
            {allFilamentProfiles.length > 0 || customFilamentProfiles.length > 0 ? (
              <>
                <FilamentProfileDropdown
                  profiles={allFilamentProfiles}
                  customProfiles={customFilamentProfiles.map(p => ({ id: p.id, name: p.name }))}
                  selectedProfileName={selectedFilamentProfileId}
                  disabled={allFilamentProfiles.length === 0 && customFilamentProfiles.length === 0}
                  filterConfig={filamentFilterConfig}
                  onFilterConfigChange={handleFilamentFilterChange}
                  onSelect={(name, source) => {
                    setSelectedFilamentProfileId(name);
                    if (source === 'system') {
                      const sp = allFilamentProfiles.find(p => p.name === name);
                      setSelectedFilamentMaterial(sp?.material || '');
                    } else {
                      setSelectedFilamentMaterial('');
                    }
                  }}
                />

                {/* Show selected profile's temperature info */}
                {selectedFilamentProfile && (
                  <div className="text-xs text-pf-text-muted">
                    {selectedFilamentProfile.nozzleTemperature ?? 210}°C nozzle, {selectedFilamentProfile.bedTemperature ?? 60}°C bed
                  </div>
                )}
              </>
            ) : (
              <div className="text-sm text-pf-text-muted p-2">
                {isMachineProfilesLoading ? <span className="italic">Loading...</span> : 
                 selectedMachineProfileId && isFilamentProfilesLoading ? <span className="italic">Loading...</span> :
                 !selectedMachineProfileId ? 'Select a machine profile to see filament options' :
                 <span className="italic">No filament profiles available</span>}
              </div>
            )}
          </div>
          )}

          {/* PROCESS PROFILE - with Reset, Save-as, and profile management menu */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-3 space-y-2">
            {/* Header: label + ⋮ options menu */}
            <div className="flex items-center justify-between mb-2">
              <label className="block text-sm font-semibold text-pf-text-primary">Process</label>
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
                      onClick={() => { setProfileMenuOpen(false); setProcessEditorOpen(true); }}
                      disabled={!selectedProcessPresetId}
                      iconLeft={<EditIcon className="w-3.5 h-3.5" />}
                    >
                      Edit in modal
                    </Button>
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
              accept=".json,.orca_printer,.orca_filament"
              multiple
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
                    <option value="">Select process...</option>
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
                    {/* System presets (bundled OrcaSlicer profiles) */}
                    {processProfilesBySource.user.length > 0 && (
                      <optgroup label="User presets">
                        {processProfilesBySource.user.map(profile => (
                          <option key={profile.name} value={`system:${profile.name}`}>
                            {profile.name} ({profile.layerHeight}mm)
                          </option>
                        ))}
                      </optgroup>
                    )}
                    {processProfilesBySource.system.length > 0 && (
                      <optgroup label="System presets">
                        {processProfilesBySource.system.map(profile => (
                          <option key={profile.name} value={`system:${profile.name}`}>
                            {profile.name} ({profile.layerHeight}mm)
                          </option>
                        ))}
                      </optgroup>
                    )}
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
              <div className="text-sm text-pf-text-muted p-2">
                {isMachineProfilesLoading ? <span className="italic">Loading...</span> :
                 selectedMachineProfileId && isProcessProfilesLoading ? <span className="italic">Loading...</span> :
                 !selectedMachineProfileId ? 'Select a machine profile to see process options' :
                 <span className="italic">No process profiles available</span>}
              </div>
            )}
          </div>

          {/* BED TYPE OVERRIDE */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-3 space-y-2">
            <label htmlFor="nsj-bed-type" className="block text-sm font-semibold text-pf-text-primary">
              Bed Type
            </label>
            <Select
              id="nsj-bed-type"
              value={selectedBedType}
              onChange={(e) => setSelectedBedType(e.target.value)}
            >
              <option value="">Inherit from profile</option>
              {BED_TYPE_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value}>{opt.label}</option>
              ))}
            </Select>
          </div>

          {/* ORCASLICER-STYLE SETTINGS PANEL */}
          <div className="bg-pf-panel border border-pf-border rounded-lg overflow-hidden">
            <SlicerSettingsPanel
              settings={slicerSettings}
              onChange={handleSlicerSettingsChange}
              advancedSettings={advancedProcessSettings}
              onAdvancedSettingsChange={setAdvancedProcessSettings}
              originalSettings={originalProcessSettings}
            />
          </div>

          {/* Model picker modal — opened by workspace "+" button */}
          <SearchablePickerModal<Model3DBasic>
            isOpen={modelPickerOpen}
            onClose={() => setModelPickerOpen(false)}
            onSelect={(model) => {
              setSelectedModelId(model.id);
              setModelFileUrl('');
              setModelFileName('');
            }}
            items={models ?? []}
            getItemId={(m) => m.id}
            getLabel={(m) => m.originalFileName}
            selectedId={selectedModelId}
            title="Select 3D Model"
            searchPlaceholder="Search models by filename..."
            emptyMessage="No models match your search."
            isLoading={isLoadingModels}
            onUrlSubmit={(url, name) => {
              setModelFileUrl(url);
              setModelFileName(name);
              setSelectedModelId('');
              setBedModels([]);
            }}
          />

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
                canSlice={!submittedJobId && workspaceModels.length > 0 && (!!selectedModelId || !!modelFileUrl.trim()) && !!selectedMachineProfileId && (printerIsMultiToolhead ? physicalToolheads.every((_, i) => !!extruderFilamentProfileIds[i]) : !!selectedFilamentProfileId) && !!selectedProcessPresetId}
                onToggleSidebar={() => setSidebarOpen(v => !v)}
                sidebarOpen={sidebarOpen}
                onModelsReplace={handleWorkspaceModelsReplace}
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
          printerName={selectedPrinterModel || selectedPrinter.name}
          onSuccess={() => {
            // Invalidate profiles cache to reload when modal closes
            qc.invalidateQueries({ queryKey: ['slicerProfiles'] });
            qc.invalidateQueries({ queryKey: ['slicerProfilesHierarchy'] });
            qc.invalidateQueries({ queryKey: ['machineProfilesForModel'] });
            qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
          }}
        />
      )}
      
      {/* Profile Editor Modal - for editing selected profile settings */}
      <ProfileEditorModal
        isOpen={profileEditorOpen}
        onClose={() => setProfileEditorOpen(false)}
        profileType={profileEditorType}
        originalProfile={
          profileEditorType === 'machine' ? (selectedMachineProfile ?? null) :
          (selectedFilamentProfile ?? null)
        }
        onSaveSuccess={(_profileId, profileName) => {
          qc.invalidateQueries({ queryKey: ['customProfiles'] });
          qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
          setMessage(`Custom profile "${profileName}" saved successfully`);
        }}
      />

      {/* Process Profile Editor Modal */}
      <ProcessProfileEditorModal
        isOpen={processEditorOpen}
        onClose={() => setProcessEditorOpen(false)}
        originalProfile={resolvedProcessProfile}
        currentSettings={advancedProcessSettings}
        onApply={(newSettings) => {
          setSlicerSettings((prev) => ({ ...prev, ...newSettings } as OrcaProcessSettings));
          // Keep advancedProcessSettings in sync so submit overrides don't revert modal edits
          setAdvancedProcessSettings((prev) => ({ ...prev, ...newSettings }));
        }}
        onSaveSuccess={(_profileId, profileName) => {
          qc.invalidateQueries({ queryKey: ['customProfiles'] });
          qc.invalidateQueries({ queryKey: ['processProfilesForMachines'] });
          qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
          setMessage(`Custom process profile "${profileName}" saved successfully`);
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
