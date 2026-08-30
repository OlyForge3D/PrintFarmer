import React, { useState, useEffect, useMemo, useCallback, useRef } from 'react';
import { useSearchParams, useNavigate } from 'react-router';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import {
  sliceJobService,
  SubmitSliceJobRequest,
  formatQueuePositionSuffix,
} from '@/services/sliceJobService';
import { slicerService, type SlicerEngineInfo } from '@/services/slicerService';
import { 
  slicerProfilesService,
  type CustomProfile,
  type CloneProfileFamilyResponse,
  type OrcaMachineProfile,
  type OrcaFilamentProfile,
  type OrcaProcessProfile
} from '@/services/slicerProfilesService';
import { slicerRegistry } from '@/services/slicerRegistry';
import { assetService } from '@/services/assetService';
import { apiClient } from '@/services/api';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import { getErrorMessage } from '@/common/utils/apiErrors';
import { createSlicerRegistryConnection } from '@/services/slicerRegistryHubConnection';
import { ProfileEditorModal, type ProfileType } from '@/features/slicer/components/ProfileEditorModal';
import { ProcessProfileEditorModal } from '@/features/slicer/components/ProcessProfileEditorModal';
import { MachineProfileSelectorModal, type MachineProfileChoice } from '@/features/slicer/components/job/MachineProfileSelectorModal';
import { CreateProfileFamilyModal } from '@/features/slicer/components/profile-family/CreateProfileFamilyModal';
import { buildMachineProfileLabels, isProcessProfileCoreOneVariantCompatible, resolveHighFlow } from '@/features/slicer/utils/machineProfileLabels';
import { buildSlicerProfileJson } from '@/features/slicer/utils/slicerProfilePayload';
import {
  SlicerSettingsPanel,
  type OrcaProcessSettings,
} from '@/features/slicer/components/settings';
import { resolveProcessSettingsBaseline } from '@/features/slicer/components/settings/processSettingsBaseline';
import { scrubSettingsForVersion } from '@/features/slicer/components/settings/orcaSettingsMetadataResolver';
import { PrinterSlicerSelector, SlicerSelector, type PrinterForSlicing, SlicerSettingsPanel as SimpleSlicerSettingsPanel, type SlicerSettings } from '../components/job';
import { orcaToSimpleSettings, simpleToOrcaSettings } from './simpleSlicerMappings';
import { FilamentProfileDropdown, FILTER_STORAGE_KEY, type FilamentFilterConfig } from '../components/CascadingMenuDropdown';
import { getPrimaryNozzleDiameter } from '../utils/profileMatcher';
import { isMultiToolhead, getPhysicalToolheads } from '../utils/profileMatcher';
import {
  classifyCustomProfileScope,
  legacyMachineProfileMatchesPrinter,
  legacyProcessProfileMatchesMachine,
} from '../utils/customProfileScoping';
import type { Model3DBasic } from '../components/job/types';
import type { ModelListItem } from '@/types/models';
import { SearchablePickerModal } from '@/common/components/SearchablePickerModal';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Alert, Card, Input, Select, ColorPicker, ProgressBar } from '@/common/components/ui';
import { LayersIcon, EditIcon, DownloadIcon, RefreshIcon, SaveIcon, MoreVerticalIcon, CopyIcon, FileImportIcon, SwapHorizontalIcon } from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useSTLFile } from '@/common/hooks/useSTLFile';
import { useSliceJobProgress } from '@/features/slicer/hooks/useSliceJobProgress';
import { useSlicerMode } from '@/features/slicer/hooks/useSlicerMode';
import { SliceProgressOverlay } from '@/features/slicer/components/SliceProgressOverlay';
import type { LoadedModel, BedConfig } from '@/features/slicer/components/viewer';
import type { BufferGeometry } from 'three';
import { sliceJobService as sliceJobSvc } from '@/services/sliceJobService';
import { buildSlicerViewerModelUrl, getSlicerViewerFileType } from '@/features/slicer/utils/model-file-utils';
import { loadModelArrayBuffer, isAuthenticatedModelUrl } from '@/common/utils/authenticatedModelUrl';
import { buildSlicePayloadModels, resolveModel3DId, modelTransformJson, diffProcessOverrides } from '@/features/slicer/utils/slicePayload';
import { validateOrcaPrintSettings } from '@/features/slicer/utils/slicerSettingsValidation';
import { readOrcaBundle } from '@/features/slicer/utils/orcaBundleLoader';
import { SlicerWorkspaceBoundary } from '@/features/slicer/components/viewer/SlicerWorkspaceBoundary';
import { X } from 'lucide-react';
import { STLPreviewModalBoundary } from '@/features/slicer/components/viewer/STLPreviewModalBoundary';

// Removed MATERIAL_PRESETS constant - now using API-driven filament profiles

const NOZZLE_MATCH_TOLERANCE = 0.01;
const NOZZLE_VALUE_DECIMALS = 3;

interface NozzleOption {
  value: string;
  diameter: number;
  label: string;
}

type MachineProfilesErrorCode = 'no_profiles_for_model' | 'alias_matched_no_profiles';

interface MachineProfilesErrorBody {
  code?: string;
  detail?: string;
}

interface MachineProfilesQueryError {
  message?: string;
  statusCode?: number;
  data?: MachineProfilesErrorBody;
}

function getMachineProfilesErrorCode(error: MachineProfilesQueryError | null): MachineProfilesErrorCode | undefined {
  const code = error?.data?.code;
  return code === 'no_profiles_for_model' || code === 'alias_matched_no_profiles'
    ? code
    : undefined;
}

/**
 * Helper function to convert OrcaProcessProfile to OrcaProcessSettings
 * Maps profile data to settings structure, using defaults for missing values
 */
function convertOrcaProcessProfileToSettings(profile: OrcaProcessProfile | undefined): OrcaProcessSettings {
  if (!profile) return {} as OrcaProcessSettings;

  // Parse settings from profile if available
  const profileSettings = (profile.settings ?? {}) as Record<string, unknown>;

  // Merge the explicit profile values (raw JSON + the four promoted fields) and
  // resolve a COMPLETE baseline: every metadata-known key gets a value (the
  // profile's when present, otherwise its metadata default), coerced to the
  // editor's native type. This ensures every editable field has an original
  // value so the reset/modified affordance works for all settings, not just the
  // ~50 a profile declares explicitly.
  const merged: Record<string, unknown> = {
    ...profileSettings,
    ...(profile.layerHeight !== undefined ? { layer_height: profile.layerHeight } : {}),
    ...(profile.infillPercentage !== undefined ? { sparse_infill_density: profile.infillPercentage } : {}),
    ...(profile.printSpeed !== undefined ? { outer_wall_speed: profile.printSpeed } : {}),
    ...(profile.supports !== undefined ? { enable_support: profile.supports } : {}),
  };

  return resolveProcessSettingsBaseline(merged) as unknown as OrcaProcessSettings;
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

const DEFAULT_FILAMENT_COLOUR = '888888';

/** Normalize a hex string (with/without '#', 3 or 6 digits) to a 6-digit hex without '#'. */
function normalizeHexNoHash(input: unknown): string | undefined {
  if (typeof input !== 'string') return undefined;
  const clean = input.replace(/^#/, '').trim();
  if (/^[0-9a-fA-F]{6}$/.test(clean)) return clean.toUpperCase();
  if (/^[0-9a-fA-F]{3}$/.test(clean)) {
    return `${clean[0]}${clean[0]}${clean[1]}${clean[1]}${clean[2]}${clean[2]}`.toUpperCase();
  }
  return undefined;
}

/**
 * Resolve a filament profile's display colour (hex, no '#'), reading the
 * OrcaSlicer `filament_colour` / `default_filament_colour` settings. Each may be
 * a string or a per-extruder string array. Falls back to a neutral grey.
 */
function getFilamentProfileColour(profile: OrcaFilamentProfile | undefined): string {
  const settings = profile?.settings;
  if (settings) {
    const candidates = [settings.filament_colour, settings.default_filament_colour];
    for (const candidate of candidates) {
      const raw = Array.isArray(candidate) ? candidate[0] : candidate;
      const hex = normalizeHexNoHash(raw);
      if (hex) return hex;
    }
  }
  return DEFAULT_FILAMENT_COLOUR;
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

const MODELS_LIST_BASIC_QUERY_KEY = ['modelsListBasic'];

async function fetchModelsListBasic(): Promise<ModelListItem[]> {
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
}

export const NewSliceJobPage: React.FC = () => {
  const STORAGE_KEYS = {
    printerId: 'sliceJob.selectedPrinterId',
    machineProfileId: 'sliceJob.selectedMachineProfileId',
    filamentProfileId: 'sliceJob.selectedFilamentProfileId',
    processProfileId: 'sliceJob.selectedProcessProfileId',
  } as const;

  const { user } = useAuth();
  const { mode: slicerMode, canToggle: canToggleSlicerMode, setMode: setSlicerMode } = useSlicerMode();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const modelIdFromUrl = searchParams.get('modelId') || '';
  const slicerModeOptions = ['Simple', 'Advanced'] as const;
  const slicerModeRefs = React.useRef<Record<(typeof slicerModeOptions)[number], HTMLButtonElement | null>>({
    Simple: null,
    Advanced: null,
  });

  // Check if ANY machine profiles exist in the system (for onboarding detection).
  // This only reflects profiles that have been imported into the database
  // (see slicerProfilesService.listExtended / ProfilesController.ListExtendedAsync)
  // and is NOT the same signal as "a healthy OrcaSlicer worker is available" —
  // the for-model machine profile lookup below queries the worker live and does
  // not require anything to have been imported first (issue #1760).
  const { data: profilesSummary, isLoading: isProfilesSummaryLoading } = useQuery({
    queryKey: ['slicerProfilesExtended'],
    queryFn: () => slicerProfilesService.listExtended(),
    staleTime: 300_000,
  });
  const hasAnyMachineProfiles = (profilesSummary?.machineProfiles?.length ?? 0) > 0;

  // === Main Sidebar Controls ===
  const [selectedSlicerId, setSelectedSlicerId] = useState<number>(1);
  /**
   * Issue #578 dual-engine: user-selected pin for the OrcaSlicer engine version.
   * Undefined = unpinned (server picks latest / any worker may claim). Only
   * shown in the UI when 2+ versions are registered.
   */
  const [selectedEngineVersion, setSelectedEngineVersion] = useState<string | undefined>(undefined);
  const { data: registeredEngines, isLoading: isRegisteredEnginesLoading } = useQuery<SlicerEngineInfo[]>({
    queryKey: ['slicer-engines-registry'],
    queryFn: () => slicerService.listEngines(),
    staleTime: 300_000,
  });
  // A healthy, registered OrcaSlicer/PrusaSlicer worker means live per-model
  // profile lookups (GET .../profiles/machine/for-model/{id}) can succeed even
  // when nothing has been imported into the database yet. Gating onboarding on
  // hasAnyMachineProfiles alone incorrectly showed the "Get started with
  // slicing" screen for a fully healthy worker whenever the currently
  // registered printer models simply hadn't had profiles imported (issue #1760).
  const hasRegisteredEngine = (registeredEngines?.length ?? 0) > 0;
  const engineName = selectedSlicerId === 1 ? 'OrcaSlicer' : 'PrusaSlicer';
  const engineInfo = useMemo(
    () => registeredEngines?.find(e => (e?.engine ?? '').toLowerCase() === engineName.toLowerCase()),
    [registeredEngines, engineName],
  );
  const versionEntriesForEngine = useMemo(() => engineInfo?.versionEntries ?? [], [engineInfo]);
  // Backend-computed "newest online-available" version. The backend returns
  // `null` in the legacy-single-worker case (no SlicerService rows registered
  // at all), which is the signal to LEAVE JOBS UNPINNED so the legacy worker's
  // generic "orcaslicer" capability can still claim them. Do NOT synthesize a
  // fallback from availableVersionsForEngine here — that would defeat the null
  // signal and force a pin that breaks legacy deployments (Vasquez R3).
  const latestAvailableForEngine = useMemo(
    () => engineInfo?.latest ?? undefined,
    [engineInfo],
  );
  useEffect(() => {
    // Reset the pin whenever engine changes so a stale pin doesn't survive.
    // Also cascade profile selections (issue #578): a v2.3.1 machine or filament
    // profile will not be valid for v2.4.0 and vice versa. Same treatment when
    // switching between Orca and Prusa. Printer selection stays intact because
    // it is orthogonal to the slicer engine. Multi-extruder mappings must clear
    // too — they hold profile NAMES that are equally version-bound.
    setSelectedEngineVersion(undefined);
    setSelectedMachineProfileId('');
    setSelectedNozzleFilter('');
    setSelectedFilamentProfileId('');
    setSelectedFilamentMaterial('');
    setExtruderFilamentProfileIds({});
    setExtruderFilamentColours({});
  }, [selectedSlicerId]);

  // Track first mount so the initial undefined→undefined render doesn't wipe
  // user selections, but any subsequent transition (including pinned→undefined
  // "back to Latest") still cascades a reset — the resolved effective version
  // has changed, so the profile set the user was staring at may no longer apply.
  const engineVersionInitialRenderRef = useRef(true);
  useEffect(() => {
    if (engineVersionInitialRenderRef.current) {
      engineVersionInitialRenderRef.current = false;
      return;
    }
    setSelectedMachineProfileId('');
    setSelectedNozzleFilter('');
    setSelectedFilamentProfileId('');
    setSelectedFilamentMaterial('');
    setExtruderFilamentProfileIds({});
    setExtruderFilamentColours({});
  }, [selectedEngineVersion]);

  // Version-scoped settings scrub (issue #578). When the pinned engine version
  // changes, drop keys that don't exist in the new version's metadata and
  // migrate renamed keys to their target-version equivalents across ALL
  // in-flight settings state: `advancedProcessSettings` (dynamic dict),
  // `slicerSettings` (typed OrcaProcessSettings) and `originalProcessSettings`
  // (baseline snapshot used by `diffProcessOverrides` at submit time). This
  // guarantees that added fields appear, removed fields disappear and are
  // omitted from the submit payload's `overrides`, and renamed fields only
  // submit under the new key regardless of which state object the user's
  // edits landed in.
  const effectiveEngineVersion = selectedEngineVersion ?? latestAvailableForEngine;
  useEffect(() => {
    const scrubDict = (prev: Record<string, unknown> | undefined): Record<string, unknown> | undefined => {
      if (!prev || Object.keys(prev).length === 0) return prev;
      const scrubbed = scrubSettingsForVersion(prev, 'process', effectiveEngineVersion);
      const prevKeys = Object.keys(prev);
      const nextKeys = Object.keys(scrubbed);
      if (prevKeys.length === nextKeys.length && prevKeys.every((k) => k in scrubbed && scrubbed[k] === prev[k])) {
        return prev;
      }
      return scrubbed;
    };
    setAdvancedProcessSettings((prev) => scrubDict(prev) ?? prev);
    setSlicerSettings((prev) => {
      const scrubbed = scrubDict(prev as unknown as Record<string, unknown>);
      return (scrubbed as unknown as OrcaProcessSettings) ?? prev;
    });
    setOriginalProcessSettings((prev) => scrubDict(prev) ?? prev);
  }, [effectiveEngineVersion]);

  const [selectedPrinterId, setSelectedPrinterId] = useState<string>(() => {
    try {
      return localStorage.getItem(STORAGE_KEYS.printerId) ?? '';
    } catch {
      return '';
    }
  });
  // Material type filter for filament profile selection
  const [selectedFilamentMaterial, setSelectedFilamentMaterial] = useState<string>('');
  const [selectedProcessPresetId, setSelectedProcessPresetId] = useState<string>('');

  // === Cascading Profile Selection (OrcaSlicer-style) ===
  // Flow: Manufacturer → Printer Model → Machine Profile → Filament/Process filtered by machine
  const [selectedManufacturer, setSelectedManufacturer] = useState<string>('');
  const [selectedPrinterModel, setSelectedPrinterModel] = useState<string>('');
  const [selectedMachineProfileId, setSelectedMachineProfileId] = useState<string>('');
  const [isMachinePickerOpen, setIsMachinePickerOpen] = useState(false);
  const [isCreateProfileFamilyOpen, setIsCreateProfileFamilyOpen] = useState(false);
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

  // Per-slice filament colour overrides (hex, no '#'). Keyed by extruder index;
  // index 0 doubles as the single-filament colour. Defaults from the selected
  // filament profile's filament_colour, user-overridable via the swatch.
  const [extruderFilamentColours, setExtruderFilamentColours] = useState<Record<number, string>>({});

  // Filament profile targeted by the per-row "edit" action (multi-extruder).
  const [filamentEditProfile, setFilamentEditProfile] = useState<OrcaFilamentProfile | null>(null);

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

  // Simple mode: derive SlicerSettings view from OrcaProcessSettings
  const simpleSlicerSettings = useMemo<SlicerSettings>(
    () => orcaToSimpleSettings(slicerSettings),
    [slicerSettings]
  );

  const handleSimpleSettingsChange = useCallback((settings: SlicerSettings) => {
    setSlicerSettings((prev) => simpleToOrcaSettings(settings, prev));
  }, []);

  // Simple-mode inline field validity (issue #2223): false while the Simple
  // settings panel is showing an uncommitted invalid perimeter/infill/shell
  // layer value. Gates submission so a rejected edit can never be silently
  // replaced by the last valid value at slice time.
  const [isSimpleSlicerSettingsValid, setIsSimpleSlicerSettingsValid] = useState(true);

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
          const bundleJson = await readOrcaBundle(buffer);
          if (bundleJson === null) {
            toast.error(`${file.name}: not a valid OrcaSlicer bundle`);
            failedCount++;
            continue;
          }
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
          const bundleJson = await readOrcaBundle(buffer);
          if (bundleJson === null) {
            toast.error(`${file.name}: not a valid OrcaSlicer bundle`);
            failedCount++;
            continue;
          }
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
          const bundleJson = await readOrcaBundle(buffer);
          if (bundleJson === null) {
            toast.error(`${file.name}: not a valid OrcaSlicer bundle`);
            failedCount++;
            continue;
          }
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
  // Bumped every time a library model is picked from the modal — including
  // re-picking the model already selected — so the bed-add effect below is
  // guaranteed to re-run and place a new instance even when `selectedModelId`
  // itself doesn't change. See issue #1771 (duplicate model placement no-op).
  const [modelPickNonce, setModelPickNonce] = useState(0);
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
  
  // Profile Editor Modal State
  const [profileEditorOpen, setProfileEditorOpen] = useState(false);
  const [profileEditorType, setProfileEditorType] = useState<ProfileType>('machine');
  const [processEditorOpen, setProcessEditorOpen] = useState(false);
  const [sidebarOpen, setSidebarOpen] = useState(true);

  // Post-submission progress tracking
  const [submittedJobId, setSubmittedJobId] = useState<string | null>(null);
  const jobProgress = useSliceJobProgress(submittedJobId);

  // Snapshot of cost/routing values captured at slice-submit time.
  // Keeps cost display and queue routing stable while sidebar stays editable.
  const [sliceSnapshot, setSliceSnapshot] = useState<{
    filamentCostPerKg: number | null;
    requiredPrinterModel: string | undefined;
    requiredMaterialType: string | undefined;
    requiredNozzleDiameter: number | undefined;
  } | null>(null);

  // Auto-clear submittedJobId (and snapshot) when the job completes successfully,
  // returning the form to a fresh "ready to slice" state. This intentionally does
  // NOT fire for 'Failed': a failed job's failure/retry UI must stay visible until
  // the user explicitly acts (Retry or New Job) via onRetry/onNewJob below — those
  // are the only paths that clear `message`, so auto-clearing 'Failed' here left
  // `message` still holding the original "Job queued (id ...)" text, which would
  // reappear as soon as submittedJobId went null (the `!submittedJobId && message`
  // Alert), making a terminal failure look like it silently reverted to a stale
  // "Job queued" state (issue #2214).
  useEffect(() => {
    if (jobProgress.status === 'Completed') {
      const timer = setTimeout(() => {
        setSubmittedJobId(prev => prev ? null : prev);
        setSliceSnapshot(null);
        setMessage(null);
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
    // Map the UI slicer id (1=Orca, 2=Prusa) to the SlicerEngineType enum value
    // (OrcaSlicer=0, PrusaSlicer=1) expected by the server. Issue #578 dual-engine
    // dispatch depends on the correct engine name to build capability tags.
    const engineEnum = selectedSlicerId === 1 ? 0 : 1;
    return {
      name: typeName,
      version: slicer?.version || 'Unknown',
      engine: engineEnum
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
  const {
    data: machineProfilesData = [],
    error: machineProfilesError,
    isLoading: isMachineProfilesLoading,
  } = useQuery<OrcaMachineProfile[], MachineProfilesQueryError>({
    queryKey: ['machineProfilesForModel', selectedPrinterModelId, selectedEngineVersion ?? latestAvailableForEngine ?? null],
    queryFn: () => slicerProfilesService.getMachineProfilesForModel(selectedPrinterModelId!, selectedEngineVersion ?? latestAvailableForEngine),
    enabled: !!selectedPrinterModelId,
    staleTime: 30_000
  });
  const machineProfilesErrorCode = getMachineProfilesErrorCode(machineProfilesError);

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
    queryKey: ['filamentProfilesForMachines', selectedMachineNames, selectedEngineVersion ?? latestAvailableForEngine ?? null],
    queryFn: () => slicerProfilesService.getFilamentProfilesForMachines(selectedMachineNames, selectedEngineVersion ?? latestAvailableForEngine),
    enabled: selectedMachineNames.length > 0,
    staleTime: 30_000
  });

  // Fetch process profiles compatible with selected machine
  const { data: processProfilesData = [], isLoading: isProcessProfilesLoading } = useQuery<OrcaProcessProfile[]>({
    queryKey: ['processProfilesForMachines', selectedMachineNames, selectedEngineVersion ?? latestAvailableForEngine ?? null],
    queryFn: () => slicerProfilesService.getProcessProfilesForMachines(selectedMachineNames, selectedEngineVersion ?? latestAvailableForEngine),
    enabled: selectedMachineNames.length > 0,
    staleTime: 30_000
  });

  // Filter custom profiles by type for each selector.
  // Machine profiles are scoped by the authoritative catalog PrinterModel
  // association (printerModelId). Legacy profiles without that association fall
  // back to fuzzy manufacturer/model matching so they remain usable.
  const customMachineProfiles = useMemo(() => {
    const allCustomMachine = customProfilesData?.profiles?.filter(p => p.profileType === 'machine') ?? [];
    if (allCustomMachine.length === 0) return allCustomMachine;
    return allCustomMachine.filter(p => {
      const scope = classifyCustomProfileScope(p, selectedPrinterModelId);
      if (scope === 'match') return true;
      if (scope === 'mismatch') return false;
      // Unscoped legacy profile — fall back to fuzzy printer matching.
      return legacyMachineProfileMatchesPrinter(
        p,
        selectedPrinterForSlicing?.manufacturerName,
        selectedPrinterForSlicing?.modelName,
      );
    });
  }, [customProfilesData, selectedPrinterForSlicing, selectedPrinterModelId]);

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

  // Process profiles are scoped by the authoritative catalog PrinterModel
  // association (printerModelId). Legacy profiles without that association fall
  // back to compatible_printers matching against the selected machine profile.
  const customProcessProfiles = useMemo(() => {
    const allCustomProcess = customProfilesData?.profiles?.filter(p => p.profileType === 'process') ?? [];
    if (allCustomProcess.length === 0) return allCustomProcess;
    return allCustomProcess.filter(p => {
      const scope = classifyCustomProfileScope(p, selectedPrinterModelId);
      if (scope === 'match') return true;
      if (scope === 'mismatch') return false;
      // Unscoped legacy profile — fall back to compatible_printers matching.
      return legacyProcessProfileMatchesMachine(p, selectedMachineProfileId);
    });
  }, [customProfilesData, selectedPrinterModelId, selectedMachineProfileId]);


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

  /**
   * Every machine profile for this printer, unfiltered by nozzle.
   *
   * The picker does its own nozzle filtering, so it must receive the complete
   * set — otherwise the current nozzle filter would hide the very profiles the
   * user opened the picker to find.
   */
  const machineProfileChoices = useMemo<MachineProfileChoice[]>(() => {
    const system = (machineProfilesData ?? []).map((profile) => ({
      name: profile.name,
      nozzleDiameter: getMachineProfileNozzleDiameter(profile),
      isSystem: true,
      isHighFlowNozzle: profile.isHighFlowNozzle,
    }));
    const custom = customMachineProfiles.map((profile) => ({
      name: profile.name,
      nozzleDiameter: getMachineProfileNozzleDiameter(profile),
      isSystem: false,
    }));
    return [...custom, ...system];
  }, [machineProfilesData, customMachineProfiles]);

  /**
   * Display label for the currently selected profile, nozzle token trimmed.
   *
   * Scoped to the profiles sharing the selected nozzle — the same grouping the
   * picker uses. Building labels across the whole set would collide for any
   * multi-nozzle printer and fall back to raw names, defeating the trim.
   */
  const selectedMachineProfileLabel = useMemo(() => {
    if (!selectedMachineProfileId) return '';
    const selected = machineProfileChoices.find((c) => c.name === selectedMachineProfileId);
    if (!selected) return selectedMachineProfileId;
    const sameNozzle = machineProfileChoices.filter(
      (c) => c.nozzleDiameter === selected.nozzleDiameter && c.isSystem === selected.isSystem,
    );
    const labels = buildMachineProfileLabels(sameNozzle.map((c) => c.name));
    return labels.get(selectedMachineProfileId) ?? selectedMachineProfileId;
  }, [selectedMachineProfileId, machineProfileChoices]);

  const selectedMachineNozzleDiameter = useMemo(() => {
    return machineProfileChoices.find((c) => c.name === selectedMachineProfileId)?.nozzleDiameter;
  }, [machineProfileChoices, selectedMachineProfileId]);

  /**
   * Commits a profile chosen in the picker.
   *
   * The nozzle filter is synced to the chosen profile's diameter so the
   * nozzle-filtered lists that drive auto-selection and the "no profiles for
   * this nozzle" warnings stay consistent with what the user picked.
   */
  const handleMachineProfileSelect = useCallback((profileName: string) => {
    setSelectedMachineProfileId(profileName);
    const chosen = machineProfileChoices.find((c) => c.name === profileName);
    if (chosen?.nozzleDiameter !== undefined && chosen.nozzleDiameter > 0) {
      setSelectedNozzleFilter(formatNozzleDiameter(chosen.nozzleDiameter));
    }
  }, [machineProfileChoices]);

  const handleProfileFamilyCreated = useCallback((response: CloneProfileFamilyResponse) => {
    const variants = response.machineProfiles;
    if (variants.length === 0) {
      setIsCreateProfileFamilyOpen(false);
      return;
    }

    const preferredNozzle = selectedPrinterForSlicing?.nozzleDiameter;
    const preferred = variants.find((variant) => preferredNozzle !== undefined && Math.abs(variant.nozzleDiameter - preferredNozzle) <= NOZZLE_MATCH_TOLERANCE)
      ?? variants.find((variant) => Math.abs(variant.nozzleDiameter - 0.4) <= NOZZLE_MATCH_TOLERANCE)
      ?? variants[0];

    setSelectedMachineProfileId(preferred.name);
    setSelectedNozzleFilter(formatNozzleDiameter(preferred.nozzleDiameter));
    setIsCreateProfileFamilyOpen(false);
  }, [selectedPrinterForSlicing?.nozzleDiameter]);

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
    const selectedIsHighFlow = resolveHighFlow(selectedMachineProfile?.isHighFlowNozzle, selectedMachineName);

    const filtered = profiles.filter((profile) => {
      const compatiblePrinters = Array.isArray(profile.compatible_printers)
        ? profile.compatible_printers
        : [];

      // Guard 1: Compatible printer names must include the selected machine when provided.
      if (selectedMachineName && compatiblePrinters.length > 0) {
        const compatible = compatiblePrinters.some((printerName) => printerName === selectedMachineName);
        if (!compatible) {
          return false;
        }
      }

      // Guard 2: Avoid mixing HF and non-HF variants for the same machine family.
      // Apply this guard only when this is the same machine family (CORE One) and
      // the machine selection explicitly indicates HF/non-HF variant intent.
      // A profile whose `compatible_printers` explicitly lists BOTH variants is
      // dual-compatible and must pass regardless of selection — see
      // isProcessProfileCoreOneVariantCompatible for why this can't be decided
      // by joining the whole compatible_printers list into one string.
      if (selectedMachineLower.includes('core one')) {
        if (!isProcessProfileCoreOneVariantCompatible(profile.name, compatiblePrinters, selectedIsHighFlow)) {
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

  const defaultProcessPresetId = useMemo(() => {
    const firstSystemPreset = processProfilesBySource.user[0] ?? processProfilesBySource.system[0];
    if (firstSystemPreset) {
      return `system:${firstSystemPreset.name}`;
    }

    const firstCustomPreset = customProcessProfiles[0];
    if (firstCustomPreset) {
      return `custom:${firstCustomPreset.id}`;
    }

    return '';
  }, [customProcessProfiles, processProfilesBySource]);

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

  // Machine profiles for profile selection - use incremental machine profiles
  // Flat list of all available filament profiles for lookup
  const allFilamentProfiles = useMemo(() => {
    return filamentProfilesData ?? [];
  }, [filamentProfilesData]);

  // When a machine is selected, auto-fill filament/process defaults if they are empty.
  // This keeps printer switching ergonomic while preserving valid restored selections.
  useEffect(() => {
    if (!selectedMachineProfileId) {
      return;
    }

    queueMicrotask(() => {
      if (!selectedFilamentProfileId) {
        const nextSystemFilament = allFilamentProfiles[0];
        if (nextSystemFilament) {
          setSelectedFilamentProfileId(nextSystemFilament.name);
          setSelectedFilamentMaterial(nextSystemFilament.material || '');
        } else {
          const nextCustomFilament = customFilamentProfiles[0];
          if (nextCustomFilament) {
            setSelectedFilamentProfileId(nextCustomFilament.name);
            setSelectedFilamentMaterial('');
          }
        }
      }

      if (!selectedProcessPresetId && defaultProcessPresetId) {
        setSelectedProcessPresetId(defaultProcessPresetId);
      }
    });
  }, [
    allFilamentProfiles,
    customFilamentProfiles,
    defaultProcessPresetId,
    selectedFilamentProfileId,
    selectedMachineProfileId,
    selectedProcessPresetId,
  ]);

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
    queryKey: MODELS_LIST_BASIC_QUERY_KEY,
    queryFn: fetchModelsListBasic,
    staleTime: 20_000
  });

  // Connect to SlicerHub for real-time updates
  useEffect(() => {
    try {
      const { connection: hubConnection, dispose } =
        createSlicerRegistryConnection('slicer-registry-new-job-page');

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
        void dispose();
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

  // Identity of the bed-model instance created by the current pick — tracked
  // so this effect can tell "the library model list just finished loading for
  // the pick we already placed" (refresh that one instance's metadata) apart
  // from "the user picked again" (place a brand-new instance). Without this,
  // re-picking an already-placed library model would either not re-run this
  // effect at all, or match the existing instance by library id and silently
  // refresh it instead of adding a second one. See issue #1771.
  const lastModelPickRef = useRef<{ selectedModelId: string; nonce: number; instanceId: string } | null>(null);

  // Derive model file URL when selected and add to bed
  useEffect(() => {
    if (selectedModelId) {
      const apiBase = getApiBaseUrl();
      const mdl = models?.find(m => m.id === selectedModelId);
      const modelUrl = `${apiBase}/3d-models/file/${selectedModelId}`;
      const fileName = mdl?.originalFileName || mdl?.fileName || 'model.stl';
      const viewerUrl = buildSlicerViewerModelUrl(apiBase, selectedModelId, fileName);
      const fileType = getSlicerViewerFileType(fileName);

      const lastPick = lastModelPickRef.current;
      const isSamePick = lastPick?.selectedModelId === selectedModelId && lastPick?.nonce === modelPickNonce;
      // A fresh pick (new nonce) always gets its own instance id, even when
      // re-picking the same library model — this is what lets the same model
      // be placed on a second plate, or twice on the same plate.
      const instanceId = isSamePick ? lastPick!.instanceId : `${selectedModelId}-${modelPickNonce}`;
      lastModelPickRef.current = { selectedModelId, nonce: modelPickNonce, instanceId };

      queueMicrotask(() => {
        setModelFileUrl(modelUrl);
        if (mdl) {
          setModelFileName(fileName);
        }
        // Add a new bed-model instance for this pick, or refresh its metadata
        // once the model list loads (same pick, re-run via the `models` dep).
        setBedModels(prev => {
          const existingModel = prev.find((model) => model.id === instanceId);

          if (existingModel) {
            return prev.map((model) =>
              model.id === instanceId
                ? {
                    ...model,
                    url: modelUrl,
                    viewerUrl,
                    fileName,
                    fileType,
                  }
                : model,
            );
          }

          const offset = prev.length * 30; // offset each model so they don't overlap
          return [...prev, {
            id: instanceId,
            libraryModelId: selectedModelId,
            url: modelUrl,
            viewerUrl,
            fileName,
            fileType,
            position: [offset, 0, 0] as [number, number, number],
            rotation: [0, 0, 0] as [number, number, number],
            scale: [1, 1, 1] as [number, number, number],
          }];
        });
      });
    }
  }, [selectedModelId, modelPickNonce, models]);



  // Get selected filament profile details for display
  const selectedFilamentProfile = useMemo(() => {
    return allFilamentProfiles.find((p: OrcaFilamentProfile) => p.name === selectedFilamentProfileId);
  }, [allFilamentProfiles, selectedFilamentProfileId]);

  // Per-kg filament cost from the selected profile, for best-effort material cost.
  const filamentCostPerKg = useMemo(
    () => sliceJobService.parseOrcaNumeric(selectedFilamentProfile?.settings?.filament_cost),
    [selectedFilamentProfile],
  );

  // Derive queue routing requirements from current slice selections.
  const requiredPrinterModel = useMemo(
    () => selectedPrinterForSlicing?.modelName ?? undefined,
    [selectedPrinterForSlicing],
  );
  const requiredMaterialType = selectedFilamentMaterial || undefined;
  const requiredNozzleDiameter = useMemo(
    () => selectedPrinterForSlicing?.nozzleDiameter ?? undefined,
    [selectedPrinterForSlicing],
  );

  // When a job is in flight, freeze cost/routing to the snapshot captured at submit time.
  const isSubmitted = submittedJobId != null && sliceSnapshot != null;
  const effectiveFilamentCostPerKg = isSubmitted ? sliceSnapshot.filamentCostPerKg : filamentCostPerKg;
  const effectiveRequiredPrinterModel = isSubmitted ? sliceSnapshot.requiredPrinterModel : requiredPrinterModel;
  const effectiveRequiredMaterialType = isSubmitted ? sliceSnapshot.requiredMaterialType : requiredMaterialType;
  const effectiveRequiredNozzleDiameter = isSubmitted ? sliceSnapshot.requiredNozzleDiameter : requiredNozzleDiameter;

  // Slice-time cost is a best-effort estimate from the filament profile only.
  // Actual cost is computed later when the sliced G-code is submitted as a print job.
  const resolvedCostPerGram = useMemo((): number | null => {
    if (effectiveFilamentCostPerKg != null) {
      return effectiveFilamentCostPerKg / 1000;
    }
    return null;
  }, [effectiveFilamentCostPerKg]);

  const costSource = useMemo((): 'profile' | null => {
    return effectiveFilamentCostPerKg != null ? 'profile' : null;
  }, [effectiveFilamentCostPerKg]);

  const typedSlicerSettings = useMemo(() => {
    if (selectedProcessProfile) {
      return convertOrcaProcessProfileToSettings(selectedProcessProfile);
    }

    const customRawJson = selectedCustomProcessProfile?.rawJson;
    if (customRawJson) {
      try {
        const parsed = JSON.parse(customRawJson);
        if (parsed && typeof parsed === 'object') {
          return convertOrcaProcessProfileToSettings(parsed as OrcaProcessProfile);
        }
      } catch {
        // Ignore parse errors and fall through to the empty default.
      }
    }
    return {} as OrcaProcessSettings;
  }, [selectedCustomProcessProfile, selectedProcessProfile]);

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
      setSlicerSettings(typedSlicerSettings);
      setOriginalProcessSettings(typedSlicerSettings as unknown as Record<string, unknown>);
    });
  }, [typedSlicerSettings]);

  const submitMutation = useMutation({
    mutationFn: async (req: SubmitSliceJobRequest) => sliceJobService.submitJob(req),
    onSuccess: (res) => {
      setMessage(
        `Job queued (id ${res.jobId.substring(0, 8)})${formatQueuePositionSuffix(res.queuePosition)}`,
      );
      setError(null);
      setSubmittedJobId(res.jobId);
      qc.invalidateQueries({ queryKey: ['slice-jobs-my'] });
      qc.invalidateQueries({ queryKey: ['slice-jobs'] });
    },
    onError: (err: unknown) => {
      setError(getErrorMessage(err, 'Failed to submit job'));
    }
  });

  const submitSliceJob = useCallback((activeModelIds?: string[]) => {
    setError(null);

    // Issue #2223: block submission on invalid print settings instead of
    // letting a negative perimeter/infill count or a zero top/bottom shell
    // layer count reach the worker and fail generically later. The Simple
    // panel surfaces its own inline errors live (uncommitted invalid drafts),
    // so `isSimpleSlicerSettingsValid` catches those; `validateOrcaPrintSettings`
    // is a defense-in-depth check against the committed `slicerSettings` that
    // also covers Advanced mode and profile-import paths.
    if (slicerMode !== 'Advanced' && !isSimpleSlicerSettingsValid) {
      setError('Fix the highlighted print setting before slicing.');
      return;
    }
    const settingsErrors = validateOrcaPrintSettings(slicerSettings);
    if (settingsErrors.length > 0) {
      setError(settingsErrors[0].message);
      return;
    }

    // Issue #578 dual-engine (Hicks R3, refined R4): reject submission until
    // the engine registry has resolved. Otherwise Latest-mode would build
    // profile queries against `effectiveEngineVersion === undefined` and
    // dispatch an unpinned Orca job that any installed version could claim —
    // re-opening the profile/worker version mismatch H#3/H#R4 flagged.
    if (registeredEngines === undefined) {
      setError('Slicer registry not yet loaded. Please retry in a moment.');
      return;
    }

    // Backend returns latest=null in TWO shapes (Hicks R4 #3, Vasquez R4):
    //   1. Legacy / fresh-install: NO SlicerService rows — every
    //      versionEntry.available is true so we leave the job UNPINNED so
    //      a generic-capability legacy worker can claim it.
    //   2. All-offline: rows exist but none fresh+online — every
    //      versionEntry.available is false so the job would sit
    //      unclaimable. In both cases `engineInfo.latest` is null; only the
    //      per-entry availability signal distinguishes them.
    // Latest-mode guard: fires only when we can prove every version is
    // unavailable. A legacy submission (fresh install) is legitimately unpinned.
    const engineHasAnyAvailable = engineInfo
      ? versionEntriesForEngine.some(v => v.available)
      : true;
    if (
      selectedEngineVersion === undefined
      && engineInfo
      && engineInfo.versions.length > 0
      && !engineInfo.latest
      && !engineHasAnyAvailable
    ) {
      setError(`No online ${engineName} worker is available to accept this job.`);
      return;
    }

    // Pinned-mode guard (Vasquez): the check above is gated on the pin being
    // undefined, so an explicit pin was never validated at all. The picker hides
    // unpickable versions, but a pin can go stale AFTER selection — the registry
    // query has a 300s staleTime — and a job pinned to a version no worker
    // advertises sits in the queue unclaimable forever. UI warnings are not
    // enough while the dispatch path stays open, so hard-block here.
    //
    // NOTE: there is deliberately NO `engineInfo` guard and NO
    // `versionEntriesForEngine.length > 0` exemption here. Both were present in
    // earlier revisions and both let a pinned job escape:
    //
    //   * The length exemption was added on the theory that it protected the
    //     legacy/fresh-install shape. It does not. In that shape the backend
    //     marks EVERY entry available (`available = !anyServiceRows || ...`, see
    //     SlicersController.ListEnginesAsync), so the list is non-empty and a
    //     legitimate pin passes on the `.some(...)` clause below, not on any
    //     length check. The exemption only ever applied to an empty list.
    //   * Requiring `engineInfo` meant a registry refresh that drops the pinned
    //     engine entirely (engineInfo → undefined) skipped the guard and
    //     dispatched the stale pin (Hicks R3).
    //
    // Both cases dispatch a job carrying a version-specific capability tag that
    // no worker advertises, so it sits in the queue unclaimable forever. Since
    // `versionEntriesForEngine` is `engineInfo?.versionEntries ?? []` (keep that
    // `?? []` — it is what makes the single test below cover the unknown-engine
    // case, and dropping it is a compile error here and at the Latest-mode guard
    // rather than a silent behaviour change), the single `.some(...)` test covers
    // every case: unknown engine and empty list both yield `false`, which blocks.
    // Unverifiable is treated as unusable.
    //
    // Failing closed is recoverable. The picker always renders a Latest control
    // while a pin is held (SlicerSelector's `|| selectedVersion !== undefined`),
    // and clearing the pin either routes to the Latest-mode guard above (engine
    // known) or submits unpinned (engine unknown, where that guard short-circuits
    // on `engineInfo`) — no trap either way.
    if (
      selectedEngineVersion !== undefined
      && !versionEntriesForEngine.some(v => v.version === selectedEngineVersion && v.available)
    ) {
      setError(`${engineName} ${selectedEngineVersion} has no online worker to accept this job. Switch to Latest or start that worker.`);
      return;
    }

    // Legacy / fresh-install path is served naturally: `latestAvailableForEngine`
    // is undefined (backend `latest=null`), no user pin is set, so
    // `slicerEngineVersion` stays undefined below and a legacy single-worker
    // deployment can claim the job with its generic "orcaslicer" capability.

    // Plate-aware slicing: only the ACTIVE plate's models are sliced. The IDs
    // are passed synchronously from the workspace's Slice button so we never
    // depend on a (potentially stale) copy of plate state.
    const activePlateModels = activeModelIds
      ? bedModels.filter(m => activeModelIds.includes(m.id))
      : bedModels;
    const payloadModels = buildSlicePayloadModels(activePlateModels);
    const primaryModel = payloadModels.primary;

    // Guard: block when the active plate has no sliceable (server-hosted) models.
    // For the plate-aware path (activeModelIds provided) an empty active plate is
    // ALWAYS blocked — a stale root modelFileUrl must never slice a model that is
    // not on the active plate. The legacy path keeps the manual-URL escape hatch.
    const blockEmpty = activeModelIds
      ? payloadModels.sliceableCount === 0
      : payloadModels.sliceableCount === 0 && !modelFileUrl.trim();
    if (blockEmpty) {
      const msg = activeModelIds
        ? 'The active plate has no sliceable models. Add a model to this plate before slicing.'
        : 'Select a model or enter a URL';
      setError(msg);
      if (activeModelIds) toast.error(msg);
      return;
    }

    // Effective single-model source: prefer the active plate's first sliceable
    // model over root form state / bedModels[0].
    const effectiveModelFileUrl = primaryModel ? primaryModel.url : modelFileUrl;
    const effectiveModelFileName = primaryModel ? primaryModel.fileName : modelFileName;

    if (!selectedModelId && !effectiveModelFileUrl.trim()) {
      setError('Select a model or enter a URL');
      return;
    }
    if (!primaryModel && !selectedModelId && modelFileUrl.trim() && !modelFileName.trim()) {
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

    // Build per-slice filament colour overrides (hex, with '#'). Defaults come
    // from each selected profile's filament_colour and are user-overridable.
    const resolveExtruderColour = (idx: number, profileName: string): string => {
      const override = extruderFilamentColours[idx];
      const colour = override ?? getFilamentProfileColour(allFilamentProfiles.find(p => p.name === profileName));
      return `#${colour.replace(/^#/, '')}`;
    };
    const extruderFilamentColoursPayload = printerIsMultiToolhead
      ? physicalToolheads.map((_, i) => resolveExtruderColour(i, extruderFilamentProfileIds[i] ?? ''))
      : undefined;
    const singleFilamentColour = printerIsMultiToolhead
      ? undefined
      : resolveExtruderColour(0, selectedFilamentProfileId);

    // Process-setting overrides: send ONLY the keys the user actually changed in
    // the editor — never the full ~300-key metadata baseline. The named process
    // profile (and its `inherits:` chain) is resolved worker-side, so re-sending
    // unmodified defaults would clobber the profile's tuned/inherited values.
    const modifiedProcessOverrides = diffProcessOverrides(
      slicerSettings as unknown as Record<string, unknown>,
      originalProcessSettings,
    );

    const request: SubmitSliceJobRequest = {
      userId: user?.id || '',
      printerId: undefined,
      modelFileUrl: effectiveModelFileUrl,
      model3DId: resolveModel3DId(payloadModels, selectedModelId),
      modelFileName: effectiveModelFileName,
      slicerEngine: slicerInfo.engine,
      // Issue #578 dual-engine: when the user leaves the dropdown on "Latest"
      // (undefined pin), resolve to the backend-computed newest AVAILABLE
      // version so the job is deterministically routed to a worker that can
      // actually claim it. When nothing is registered/available, remain
      // unpinned so legacy single-worker deployments still work.
      slicerEngineVersion: selectedEngineVersion ?? latestAvailableForEngine,
      slicerProfileJson: buildSlicerProfileJson({
        // Canonical profile name, never the trimmed picker label.
        machineProfileName: selectedMachineProfileId,
        filamentProfileName: selectedFilamentProfileId,
        filamentProfileNames: extruderFilamentNames,
        filamentColours: extruderFilamentColoursPayload,
        filamentColour: singleFilamentColour,
        processPresetId: selectedProcessPresetId,
        overrides: scrubSettingsForVersion(
          {
            ...advancedProcessSettings,
            ...modifiedProcessOverrides,
          },
          'process',
          selectedEngineVersion ?? latestAvailableForEngine,
        ),
      }),
      slicerProfileId: selectedProcessPresetId.startsWith('custom:')
            ? selectedProcessPresetId.slice('custom:'.length)
            : undefined,
      requiredCapabilitiesJson: '[]',
      priority: 1,
      modelTransformJson: primaryModel
        ? modelTransformJson(primaryModel)
        : bedModels[0]
        ? JSON.stringify({ rotation: bedModels[0].rotation, scale: bedModels[0].scale, position: bedModels[0].position })
        : undefined,
      extruderFilamentProfileNames: extruderFilamentNames,
      extruderFilamentColours: extruderFilamentColoursPayload,
      // Multi-model support: only the ACTIVE plate's server-hosted models.
      modelFileUrls: payloadModels.modelFileUrls,
      // Per-model transforms aligned with modelFileUrls (active plate only).
      modelFileTransforms: payloadModels.modelFileTransforms,
    };

    // Freeze spool/cost/routing at submit time so the overlay and queue call
    // reflect what was actually sliced, regardless of later sidebar edits.
    setSliceSnapshot({
      filamentCostPerKg: filamentCostPerKg,
      requiredPrinterModel: requiredPrinterModel,
      requiredMaterialType: requiredMaterialType,
      requiredNozzleDiameter: requiredNozzleDiameter,
    });

    submitMutation.mutate(request);
  }, [
    advancedProcessSettings,
    allFilamentProfiles,
    bedModels,
    extruderFilamentColours,
    extruderFilamentProfileIds,
    filamentCostPerKg,
    modelFileName,
    modelFileUrl,
    originalProcessSettings,
    physicalToolheads,
    printerIsMultiToolhead,
    requiredMaterialType,
    requiredNozzleDiameter,
    requiredPrinterModel,
    selectedFilamentProfileId,
    selectedMachineProfileId,
    selectedProcessPresetId,
    selectedEngineVersion,
    latestAvailableForEngine,
    engineInfo,
    engineName,
    registeredEngines,
    versionEntriesForEngine,
    slicerInfo.engine,
    slicerSettings,
    slicerMode,
    isSimpleSlicerSettingsValid,
    submitMutation,
    user?.id,
    selectedModelId,
  ]);

  const onSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    // Slicing is driven exclusively by the workspace Slice button, which passes
    // the active plate's model IDs to submitSliceJob(). A bare form submit (e.g.
    // stray Enter) must NOT slice every plate's models, so it is intentionally a
    // no-op here to preserve the plate-aware invariant.
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

  // Handles the "Enter URL" tab of the model picker. The picker already
  // rejects malformed input client-side (see SearchablePickerModal), so by
  // the time this runs `url` is a well-formed http(s) URL or server-relative
  // path. Previously this just stashed the URL/name in state and did
  // nothing else — no request was ever sent, nothing was added to the bed,
  // and the plate silently stayed at 0 objects (issue #1910). Now it
  // actively verifies the URL is reachable before adding it to the bed, and
  // surfaces the outcome via toast either way.
  //
  // Reachability is checked via `loadModelArrayBuffer` rather than a bare
  // `fetch`: a server-relative `/api/3d-models/file/{id}` path is one of the
  // API's authenticated file endpoints (see `AuthenticatedModelSource` /
  // #1711), so an unauthenticated request would always 401 even though the
  // viewer can load it fine. `loadModelArrayBuffer` attaches the bearer
  // token for those endpoints and falls back to a plain fetch otherwise.
  const handleUrlModelSubmit = useCallback((url: string, fileName: string) => {
    // Relative server paths (e.g. "/api/3d-models/file/...") are resolved
    // against the configured API base the same way other server-relative
    // model URLs are (see handleWorkspaceModelsReplace); absolute http(s)
    // URLs are used as-is.
    const resolvedUrl = url.startsWith('/') ? `${getApiBaseUrl()}${url.replace(/^\/api/, '')}` : url;
    const toastId = toast.loading(`Fetching "${fileName}"…`);

    // When the URL points at a stored model by id (e.g.
    // "/3d-models/file/{id}") and the user didn't type a File Name,
    // SearchablePickerModal falls back to the last URL path segment (the
    // bare id, with no extension) as the file name. Detecting the viewer
    // file type from that extension-less name always defaults to STL (see
    // getSlicerViewerFileType), so a stored 3MF/PLY entered this way would
    // be loaded with the wrong loader. Prefer the model list's real file
    // name for type detection when we can match the id — via
    // `ensureQueryData` (not the `models` query result directly) so this
    // resolves correctly even if the "modelsListBasic" query is still in
    // flight (or hasn't been triggered yet) at submit time; it dedupes
    // against/reuses that query's cache, and runs concurrently with the
    // reachability check below rather than adding extra latency.
    //
    // Only attempt this id match for the API's own authenticated model
    // endpoint (`isAuthenticatedModelUrl`), not for an arbitrary absolute
    // URL. Otherwise a URL like "https://evil.example/3d-models/file/<real
    // GUID>" would spoof a match against the user's own model list and
    // attach that unrelated model's GUID as `model3DId` to a job whose
    // fetched geometry comes from a completely different, attacker-supplied
    // source — a data-integrity/model-substitution risk flagged in review of
    // issue #1973.
    const modelIdMatch = isAuthenticatedModelUrl(resolvedUrl)
      ? /\/3d-models\/file\/([^/?#]+)/.exec(resolvedUrl)
      : null;
    const matchedModelPromise = modelIdMatch
      ? qc.ensureQueryData({ queryKey: MODELS_LIST_BASIC_QUERY_KEY, queryFn: fetchModelsListBasic, staleTime: 20_000 })
        .then((list) => list.find((m) => m.id === modelIdMatch[1]))
        .catch(() => undefined)
      : Promise.resolve(undefined);

    void Promise.all([loadModelArrayBuffer(resolvedUrl), matchedModelPromise])
      .then(([, matchedModel]) => {
        const fileType = getSlicerViewerFileType(matchedModel?.originalFileName || matchedModel?.fileName || fileName);

        setSelectedModelId('');
        setModelFileUrl(resolvedUrl);
        setModelFileName(fileName);
        setBedModels((prev) => {
          const offset = prev.length * 30; // offset each model so they don't overlap
          const instance: LoadedModel = {
            id: `url-${Date.now()}`,
            // When the URL resolves to a persisted library model (matched via
            // `modelIdMatch` above), link the job back to it so `model3DId`
            // carries the real GUID instead of being omitted. URLs that don't
            // match a library model (e.g. an arbitrary external file) leave
            // this undefined, and `resolveModel3DId` correctly omits
            // `model3DId` rather than sending the synthetic `id` — sending it
            // is exactly what issue #1973 fixed.
            libraryModelId: matchedModel?.id,
            url: resolvedUrl,
            viewerUrl: resolvedUrl,
            fileName,
            fileType,
            position: [offset, 0, 0] as [number, number, number],
            rotation: [0, 0, 0] as [number, number, number],
            scale: [1, 1, 1] as [number, number, number],
          };
          return [...prev, instance];
        });
        toast.success(`Added "${fileName}" from URL.`, { id: toastId });
      })
      .catch((err: unknown) => {
        toast.error(`Could not load model from URL: ${getErrorMessage(err, 'the file could not be reached')}`, { id: toastId });
      });
  }, [qc]);

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
  const handleWorkspaceModelsReplace = useCallback((removedId: string, newModels: Array<{ url: string; fileName: string; geometry: BufferGeometry; position?: [number, number, number]; rotation?: [number, number, number]; scale?: [number, number, number] }>) => {
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

  // Remove one or more models from the workspace (e.g., when a non-empty plate
  // is deleted). Clears the active single-model slice source if it was removed.
  const handleWorkspaceDeleteModels = useCallback((modelIds: string[]) => {
    if (modelIds.length === 0) return;
    const removed = new Set(modelIds);
    setBedModels(prev => prev.filter(m => !removed.has(m.id)));
    setSelectedBedModelId(prev => (prev && removed.has(prev) ? null : prev));
  }, []);

  // Show onboarding banner only when the installation truly has no usable
  // profile source at all: nothing imported into the database AND no
  // registered OrcaSlicer/PrusaSlicer worker to fall back to. A healthy
  // worker can serve machine profiles live (per-model lookups below query the
  // worker directly), so it must never be treated the same as "unconfigured".
  // When a worker is registered but the *selected* printer's model has no
  // matching profile, that is a specific, actionable mismatch communicated
  // by the reason-specific empty state further down the page — not full
  // onboarding (issue #1760).
  if (!isProfilesSummaryLoading && !isRegisteredEnginesLoading && !hasAnyMachineProfiles && !hasRegisteredEngine) {
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

  return (
    <div className="min-h-full overflow-hidden bg-pf-bg-2 px-4 pt-4 pb-4">
      <form onSubmit={onSubmit} className="relative flex min-h-[70vh] flex-col gap-1.5 overflow-hidden lg:h-[calc(100dvh-5rem)] lg:min-h-0 lg:flex-row">
        {/* LEFT SIDEBAR: OrcaSlicer Menu — hidden on narrow viewports, toggled via hamburger.
             On lg+ screens: inline beside visualizer unless explicitly toggled off.
             On narrow screens: slides over as fixed-width panel when toggled open. */}
        <div
          data-testid="slicer-settings-sidebar"
          className={`${sidebarOpen ? 'absolute top-0 left-0 bottom-0 z-40 w-full lg:relative lg:inset-auto lg:z-auto' : 'hidden'} lg:w-96 space-y-1.5 shrink-0 lg:h-full lg:min-h-0 min-h-0 overflow-y-auto bg-pf-bg-2 shadow-xl lg:shadow-none`}
        >

          {/* Mobile-only close control: below lg this panel is an absolute overlay
               that covers the full viewport, including the workspace toolbar's
               hamburger toggle underneath it — so without a dedicated control here
               narrow-viewport users have no way to dismiss it and reach Add Model
               (issue #1867). Hidden at lg+, where the panel sits inline and never
               blocks anything. `sticky` keeps it reachable even if the settings
               list scrolls. */}
          <div className="sticky top-0 z-10 flex justify-end bg-pf-bg-2 lg:hidden">
            <Button
              type="button"
              variant="unstyled"
              onClick={() => setSidebarOpen(false)}
              aria-label="Hide settings"
              title="Hide settings"
              className="flex items-center justify-center p-2 m-1 rounded text-pf-text-muted hover:text-pf-text-primary hover:bg-pf-bg-1"
            >
              <X className="w-5 h-5" />
            </Button>
          </div>

          {/* SLICER ENGINE + VERSION — one panel, because a version only means
               anything relative to its engine (mirrors printer + machine profile).
               `versionEntriesForEngine` is passed RAW: SlicerSelector filters for
               display only, and the submit guard below still needs the unfiltered
               list to detect "engine registered but zero available workers". */}
          <SlicerSelector
            selectedSlicerId={selectedSlicerId}
            onSlicerChange={(slicerId) => {
              // Clear the pin in the SAME commit as the engine change. The
              // [selectedSlicerId] effect below also resets it, but effects flush
              // after commit, so relying on it alone renders one frame where the
              // new engine's versionEntries are paired with the old engine's pin
              // — a visible wrong-version flash on a control whose entire job is
              // version truth (Bishop). The effect stays as the safety net for
              // any other path that changes the engine.
              setSelectedSlicerId(slicerId);
              setSelectedEngineVersion(undefined);
            }}
            engineOptions={engineOptions}
            versionEntries={versionEntriesForEngine}
            latestVersion={latestAvailableForEngine}
            selectedVersion={selectedEngineVersion}
            onVersionChange={setSelectedEngineVersion}
            engineName={engineName}
          />

          {/* PRINTER + MACHINE SELECTION - one compact flow */}
          <div className="bg-pf-panel border border-pf-border rounded-lg p-2.5 space-y-2">
            <PrinterSlicerSelector
              printers={printers}
              isLoading={isPrintersLoading}
              selectedPrinterId={selectedPrinterId}
              onPrinterChange={(printerId) => {
                setSelectedPrinterId(printerId);
                // Cascade reset: printer change resets all profile selections
                setSelectedMachineProfileId('');
                setSelectedNozzleFilter('');
                setSelectedFilamentProfileId('');
                setSelectedFilamentMaterial('');
                setSelectedProcessPresetId('');
                setExtruderFilamentProfileIds({});
                setExtruderFilamentColours({});
                // Machine profile auto-select will happen via the effect
              }}
              className=""
            />

            <div className="border-t border-pf-border/70 pt-2 space-y-2">
              {/* The kebab (Edit / Import / Manage) lives OUTSIDE the printer
                  conditional: machine-profile management must stay reachable even
                  before a printer is chosen. Only the picker trigger is gated. */}
              <div className="space-y-1">
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
                    {/* Visible caption only. NOT a <label htmlFor>: a button is a
                        labelable element, so an associated label would override the
                        button's content and the accessible name would lose the
                        selected profile. The sr-only prefix inside the button carries
                        the same wording for assistive tech. */}
                    <span aria-hidden="true" className="block text-xs text-pf-text-muted">
                      Machine profile
                    </span>
                    <div className="flex items-center gap-1">
                      {!selectedPrinterId ? (
                        <p className="min-w-0 flex-1 text-xs text-pf-warning">
                          Select a printer above to choose a machine profile
                        </p>
                      ) : (
                      /* Single machine-profile control. Replaces the old paired
                          machine-profile + nozzle dropdowns: in OrcaSlicer a machine
                          profile IS (printer model x nozzle), and for Prusa CORE One two
                          profiles share one nozzle diameter (standard vs HF), so nozzle
                          alone can never identify a profile. Resolving both inside the
                          picker also removes the old behaviour where changing the nozzle
                          silently cleared the machine profile. */
                      <Button
                        type="button"
                        variant="unstyled"
                        id="machine-profile-select"
                        aria-haspopup="dialog"
                        aria-expanded={isMachinePickerOpen}
                        onClick={() => setIsMachinePickerOpen(true)}
                        disabled={machineProfileChoices.length === 0 || isMachineProfilesLoading}
                        // Keeps the button focusable while unavailable so the
                        // explanation below is actually reachable; a `title` on a
                        // natively disabled button is announced to nobody.
                        explainedDisabled={machineProfileChoices.length === 0 && !isMachineProfilesLoading}
                        title={machineProfileChoices.length === 0 && !isMachineProfilesLoading
                          ? 'No machine profiles for this printer — use the options menu to import one'
                          : undefined}
                        className={`group flex min-w-0 flex-1 items-center gap-2 rounded-md border border-pf-border bg-pf-bg-1 px-2.5 py-1.5 text-left transition-colors hover:border-pf-border-strong disabled:cursor-not-allowed disabled:opacity-60 ${isMachineProfilesLoading ? 'opacity-50' : ''}`}
                        iconRight={(
                          <span data-testid="machine-profile-change-affordance" className="shrink-0">
                            <SwapHorizontalIcon className="h-4 w-4 text-pf-text-muted" />
                          </span>
                        )}
                      >
                        {/* The accessible name must carry the SELECTION, so it is built
                            from content rather than an aria-label (which would override
                            it and announce only "Machine profile"). */}
                        <span className="sr-only">Machine profile: </span>
                        <span className="min-w-0 flex-1 truncate text-sm text-pf-text-primary">
                          {isMachineProfilesLoading
                            ? 'Loading...'
                            : selectedMachineProfileLabel || 'Select machine...'}
                        </span>
                        {selectedMachineProfileId && resolveHighFlow(selectedMachineProfile?.isHighFlowNozzle, selectedMachineProfileId) && (
                          <span
                            data-pf-radius="full"
                            className="shrink-0 rounded-full bg-pf-info/15 px-1.5 py-0.5 text-[10px] font-semibold text-pf-info"
                          >
                            HF
                          </span>
                        )}
                        {selectedMachineNozzleDiameter !== undefined && selectedMachineNozzleDiameter > 0 && (
                          <span
                            data-pf-radius="full"
                            className="shrink-0 rounded-full bg-pf-accent/12 px-2 py-0.5 text-[11px] font-semibold text-pf-accent tabular-nums"
                          >
                            {formatNozzleDiameter(selectedMachineNozzleDiameter)}mm
                          </span>
                        )}
                      </Button>
                      )}
                      <div className="relative shrink-0" ref={machineMenuRef}>
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
                              onClick={() => { setMachineMenuOpen(false); navigate('/admin/settings?tab=slicing&sub=profiles'); }}
                              iconLeft={<EditIcon className="w-3.5 h-3.5" />}
                            >
                              Manage profiles
                            </Button>
                          </div>
                        )}
                      </div>
                    </div>
                  </div>

            {selectedPrinterId && machineProfilesData.length > 0 && !hasVisibleMachineProfiles && selectedNozzleDiameter !== undefined && (
              <p className="text-xs text-pf-warning mt-1">
                No machine profiles available for the current nozzle. Open the machine profile picker to choose another.
              </p>
            )}
            {selectedPrinterId
              && machineProfilesData.length === 0
              && !hasVisibleMachineProfiles
              && !isMachineProfilesLoading
              && !isCustomProfilesLoading
              && selectedManufacturer
              && selectedPrinterModel
              && (
              <section
                className="mt-3"
                aria-labelledby="machine-profiles-empty-heading"
                aria-live="polite"
              >
                <Card className="border-pf-warning/40 bg-pf-warning/5">
                  <Card.Body className="space-y-3">
                    <div className="space-y-1">
                      <h3 id="machine-profiles-empty-heading" className="text-sm font-semibold text-pf-text-primary">
                        {machineProfilesErrorCode === 'no_profiles_for_model'
                          ? `No OrcaSlicer profiles for ${selectedPrinterModel}`
                          : machineProfilesErrorCode === 'alias_matched_no_profiles'
                            ? `No matching OrcaSlicer profiles for ${selectedPrinterModel}`
                            : `Machine profiles unavailable for ${selectedPrinterModel}`}
                      </h3>
                      {machineProfilesErrorCode === 'no_profiles_for_model' ? (
                        <p className="text-sm text-pf-text-secondary">
                          OrcaSlicer doesn't ship profiles for this printer model. A profile family will let you
                          choose the closest supported machine and nozzle sizes, adjust shared settings such as
                          build volume once, and generate matching machine, process, and filament profiles.
                        </p>
                      ) : machineProfilesErrorCode === 'alias_matched_no_profiles' ? (
                        <p className="text-sm text-pf-text-secondary">
                          PrintFarmer found an OrcaSlicer alias for this model, but the slicer returned no matching
                          profiles. This is likely a profile-coverage or slicer-engine-version issue.
                        </p>
                      ) : (
                        <p className="text-sm text-pf-text-secondary">
                          {machineProfilesError
                            ? 'PrintFarmer could not load machine profiles for this printer model. Check the slicer worker and try again.'
                            : 'No machine profiles were returned for this printer model. Check the selected slicer engine version or manage profiles from the options menu.'}
                        </p>
                      )}
                    </div>

                    {machineProfilesErrorCode === 'no_profiles_for_model' && (
                      <div className="flex flex-wrap items-center gap-2">
                        <Button
                          type="button"
                          variant="primary"
                          onClick={() => setIsCreateProfileFamilyOpen(true)}
                          aria-label={`Create profile family for ${selectedPrinterModel}`}
                        >
                          Create profile family
                        </Button>
                      </div>
                    )}
                  </Card.Body>
                </Card>
              </section>
            )}
            {selectedPrinterId && !selectedManufacturer && (
              <p className="text-xs text-pf-warning mt-1">
                No matching slicer profiles found for this printer's manufacturer
              </p>
            )}
            </div>
          </div>

          {/* Machine profile picker — owns both the nozzle facet and the profile choice */}
          <MachineProfileSelectorModal
            isOpen={isMachinePickerOpen}
            profiles={machineProfileChoices}
            selectedProfileName={selectedMachineProfileId}
            onSelect={handleMachineProfileSelect}
            onClose={() => setIsMachinePickerOpen(false)}
            printerLabel={selectedPrinterForSlicing?.modelName}
          />

          {selectedPrinterModelId && selectedPrinterModel && (
            <CreateProfileFamilyModal
              isOpen={isCreateProfileFamilyOpen}
              onClose={() => setIsCreateProfileFamilyOpen(false)}
              targetPrinterModelId={selectedPrinterModelId}
              targetPrinterModelName={selectedPrinterModel}
              defaultNozzleDiameter={selectedPrinterForSlicing?.nozzleDiameter}
              slicerEngineVersion={selectedEngineVersion ?? latestAvailableForEngine}
              onSuccess={handleProfileFamilyCreated}
            />
          )}

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
                        onClick={() => { setFilamentMenuOpen(false); navigate('/admin/settings?tab=slicing&sub=profiles'); }}
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
                <div className="grid grid-cols-2 gap-1.5">
                  {physicalToolheads.map((toolhead, idx) => {
                    const selectedName = extruderFilamentProfileIds[idx] ?? '';
                    const selectedProfile = allFilamentProfiles.find(p => p.name === selectedName);
                    const swatchColour = extruderFilamentColours[idx] ?? getFilamentProfileColour(selectedProfile);

                    return (
                      <div
                        key={toolhead.id ?? idx}
                        className="flex items-center gap-1 px-0.5 py-0"
                        data-testid={`extruder-filament-${idx}`}
                      >
                        <ColorPicker
                          swatchOnly
                          swatchClassName="w-5 h-5 text-[11px] font-semibold"
                          swatchContent={idx + 1}
                          value={swatchColour}
                          onChange={(hex) => setExtruderFilamentColours(prev => ({ ...prev, [idx]: hex }))}
                          aria-label={`Extruder ${idx + 1} filament colour`}
                        />
                        <div className="flex-1 min-w-0">
                          <FilamentProfileDropdown
                            profiles={allFilamentProfiles}
                            customProfiles={customFilamentProfiles.map(p => ({ id: p.id, name: p.name }))}
                            selectedProfileName={selectedName}
                            disabled={allFilamentProfiles.length === 0 && customFilamentProfiles.length === 0}
                            filterConfig={filamentFilterConfig}
                            onFilterConfigChange={handleFilamentFilterChange}
                            className="px-2 py-1 text-xs whitespace-nowrap"
                            onSelect={(name, source) => {
                              setExtruderFilamentProfileIds(prev => ({ ...prev, [idx]: name }));
                              // Default the colour swatch from the newly selected profile.
                              const sp = allFilamentProfiles.find(p => p.name === name);
                              setExtruderFilamentColours(prev => ({ ...prev, [idx]: getFilamentProfileColour(sp) }));
                              // Also keep the primary filament in sync (first extruder = primary)
                              if (idx === 0) {
                                setSelectedFilamentProfileId(name);
                                if (source === 'system') {
                                  setSelectedFilamentMaterial(sp?.material || '');
                                } else {
                                  setSelectedFilamentMaterial('');
                                }
                              }
                            }}
                          />
                        </div>
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          className="p-1 h-auto shrink-0"
                          onClick={() => {
                            setFilamentEditProfile(selectedProfile ?? null);
                            setProfileEditorType('filament');
                            setProfileEditorOpen(true);
                          }}
                          disabled={!selectedProfile}
                          title={selectedProfile ? 'Edit filament settings' : 'Select a filament first'}
                          aria-label={`Edit Extruder ${idx + 1} filament settings`}
                        >
                          <EditIcon className="w-3.5 h-3.5" />
                        </Button>
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
                        setFilamentEditProfile(null);
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
                      onClick={() => { setFilamentMenuOpen(false); navigate('/admin/settings?tab=slicing&sub=profiles'); }}
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
                <div className="flex items-center gap-1.5">
                  <ColorPicker
                    swatchOnly
                    swatchClassName="w-7 h-7"
                    value={extruderFilamentColours[0] ?? getFilamentProfileColour(selectedFilamentProfile)}
                    onChange={(hex) => setExtruderFilamentColours(prev => ({ ...prev, 0: hex }))}
                    aria-label="Filament colour"
                  />
                  <div className="flex-1 min-w-0">
                    <FilamentProfileDropdown
                      profiles={allFilamentProfiles}
                      customProfiles={customFilamentProfiles.map(p => ({ id: p.id, name: p.name }))}
                      selectedProfileName={selectedFilamentProfileId}
                      disabled={allFilamentProfiles.length === 0 && customFilamentProfiles.length === 0}
                      filterConfig={filamentFilterConfig}
                      onFilterConfigChange={handleFilamentFilterChange}
                      onSelect={(name, source) => {
                        setSelectedFilamentProfileId(name);
                        const sp = allFilamentProfiles.find(p => p.name === name);
                        setExtruderFilamentColours(prev => ({ ...prev, 0: getFilamentProfileColour(sp) }));
                        if (source === 'system') {
                          setSelectedFilamentMaterial(sp?.material || '');
                        } else {
                          setSelectedFilamentMaterial('');
                        }
                      }}
                    />
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
                      onClick={() => { setProfileMenuOpen(false); navigate('/admin/settings?tab=slicing&sub=profiles'); }}
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
            {(processProfilesBySource.user.length > 0 || processProfilesBySource.system.length > 0 || customProcessProfiles.length > 0) ? (
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
                 availableProcessProfiles.length > 0 ? (
                   <span className="italic">
                     No process profiles are compatible with this machine variant. Try the other CORE One / CORE One HF profile, or import a compatible process profile.
                   </span>
                 ) :
                 <span className="italic">No process profiles available</span>}
              </div>
            )}
          </div>

          {/* PER-USER MODE TOGGLE — compact and colocated with the settings it controls */}
          {canToggleSlicerMode && slicerMode && (
            <div
              className="flex items-center gap-1 rounded-md border border-pf-border bg-pf-panel p-1"
              role="radiogroup"
              aria-label="Slicer mode"
            >
              {slicerModeOptions.map((m, index) => {
                const active = slicerMode === m;
                return (
                  <Button
                    key={m}
                    ref={(element) => {
                      slicerModeRefs.current[m] = element;
                    }}
                    type="button"
                    variant="unstyled"
                    role="radio"
                    aria-checked={active}
                    tabIndex={active ? 0 : -1}
                    onKeyDown={(event) => {
                      if (event.key === 'ArrowRight' || event.key === 'ArrowDown') {
                        event.preventDefault();
                        const next = slicerModeOptions[(index + 1) % slicerModeOptions.length];
                        setSlicerMode(next);
                        slicerModeRefs.current[next]?.focus();
                        return;
                      }

                      if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') {
                        event.preventDefault();
                        const next = slicerModeOptions[(index - 1 + slicerModeOptions.length) % slicerModeOptions.length];
                        setSlicerMode(next);
                        slicerModeRefs.current[next]?.focus();
                        return;
                      }

                      if (event.key === 'Enter' || event.key === ' ') {
                        event.preventDefault();
                        setSlicerMode(m);
                      }
                    }}
                    onClick={() => setSlicerMode(m)}
                    className={`flex-1 rounded px-2.5 py-1 text-xs font-semibold transition-colors ${
                      active
                        ? 'bg-pf-accent text-pf-bg-1'
                        : 'text-pf-text-secondary hover:text-pf-text-primary'
                    }`}
                  >
                    {m}
                  </Button>
                );
              })}
            </div>
          )}

          {/* SIMPLE MODE: supports + bed adhesion overrides (layer height and infill hidden) */}
          {slicerMode !== 'Advanced' && (
            <SimpleSlicerSettingsPanel
              settings={simpleSlicerSettings}
              onSettingsChange={handleSimpleSettingsChange}
              onValidationChange={setIsSimpleSlicerSettingsValid}
              simpleMode
            />
          )}

          {/* ADVANCED MODE: full OrcaSlicer parameter editor behind collapsible disclosure */}
          {slicerMode === 'Advanced' && (
            <div className="bg-pf-panel border border-pf-border rounded-lg overflow-hidden">
              <SlicerSettingsPanel
                settings={slicerSettings}
                onChange={handleSlicerSettingsChange}
                advancedSettings={advancedProcessSettings}
                onAdvancedSettingsChange={setAdvancedProcessSettings}
                originalSettings={originalProcessSettings}
                engineVersion={effectiveEngineVersion}
              />
            </div>
          )}

          {/* Model picker modal — opened by workspace "+" button */}
          <SearchablePickerModal<Model3DBasic>
            isOpen={modelPickerOpen}
            onClose={() => setModelPickerOpen(false)}
            onSelect={(model) => {
              setSelectedModelId(model.id);
              setModelPickNonce((n) => n + 1);
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
            onUrlSubmit={handleUrlModelSubmit}
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
                setSliceSnapshot(null);
                setMessage(null);
                setModelFileUrl('');
                setModelFileName('');
                setBedModels([]);
              }}
              onRetry={() => {
                setSubmittedJobId(null);
                setSliceSnapshot(null);
                setError(null);
                setMessage(null);
              }}
            />
          )}

        </div>

        {/* RIGHT SIDE: 3D Workspace
             `min-w-0` is required here: without it this flex-row item falls back to its
             default `min-width: auto`, which is the max-content width of its descendants
             (toolbar buttons, transform panels, etc). At the tablet (lg) breakpoint the
             fixed-width left sidebar plus that unconstrained max-content width exceeds the
             viewport, pushing the model viewer and the Slice Plate action past the
             `overflow-hidden` form and off-screen (issue #1868). */}
        <div data-testid="slicer-workspace-panel" className="min-w-0 flex-1 flex flex-col min-h-0">
          <div className="relative bg-pf-panel border border-pf-border rounded-lg flex-1 overflow-hidden flex flex-col min-h-0">
            {selectedPrinterId ? (
              <SlicerWorkspaceBoundary
                bedConfig={workspaceBedConfig}
                models={workspaceModels}
                selectedModelId={selectedBedModelId ?? undefined}
                onModelSelect={handleWorkspaceModelSelect}
                onModelTransform={handleWorkspaceModelTransform}
                onAddModel={handleWorkspaceAddModel}
                onSlice={submitSliceJob}
                slicing={submitMutation.isPending}
                canSlice={!submittedJobId && workspaceModels.length > 0 && (!!selectedModelId || !!modelFileUrl.trim()) && !!selectedMachineProfileId && (printerIsMultiToolhead ? physicalToolheads.every((_, i) => !!extruderFilamentProfileIds[i]) : !!selectedFilamentProfileId) && !!selectedProcessPresetId}
                onToggleSidebar={() => setSidebarOpen(v => !v)}
                sidebarOpen={sidebarOpen}
                onModelsReplace={handleWorkspaceModelsReplace}
                onDeleteModels={handleWorkspaceDeleteModels}
                simpleMode={slicerMode !== 'Advanced'}
                className="h-full"
              />
            ) : (
              <div className="h-full w-full flex items-center justify-center text-pf-text-muted bg-pf-bg-0">
                <div className="text-center">
                  <p className="text-sm">Select a printer to open the slicer workspace</p>
                </div>
              </div>
            )}

            {/* Slicing progress overlay — covers 3D workspace */}
            {submittedJobId && (
              <SliceProgressOverlay
                jobId={submittedJobId}
                progress={jobProgress}
                filamentCostPerKg={effectiveFilamentCostPerKg}
                resolvedCostPerGram={resolvedCostPerGram}
                costSource={costSource}
                requiredPrinterModel={effectiveRequiredPrinterModel}
                requiredMaterialType={effectiveRequiredMaterialType}
                requiredNozzleDiameter={effectiveRequiredNozzleDiameter}
                onNewJob={() => {
                  setSubmittedJobId(null);
                  setSliceSnapshot(null);
                  setMessage(null);
                  setModelFileUrl('');
                  setModelFileName('');
                  setBedModels([]);
                }}
                onRetry={() => {
                  setSubmittedJobId(null);
                  setSliceSnapshot(null);
                  setError(null);
                  setMessage(null);
                }}
              />
            )}
          </div>
        </div>
      </form>

      {/* STL Preview Modal */}
      {isSTLPreviewOpen && (
        <STLPreviewModalBoundary
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
      
      {/* Profile Editor Modal - for editing selected profile settings */}
      <ProfileEditorModal
        isOpen={profileEditorOpen}
        onClose={() => { setProfileEditorOpen(false); setFilamentEditProfile(null); }}
        profileType={profileEditorType}
        originalProfile={
          profileEditorType === 'machine' ? (selectedMachineProfile ?? null) :
          (filamentEditProfile ?? selectedFilamentProfile ?? null)
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
  const progressFillClassName = isFailed
    ? 'bg-pf-error'
    : isCompleted
      ? 'bg-pf-success'
      : undefined;

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
          <ProgressBar
            value={isCompleted ? 100 : percent}
            ariaLabel="Slice progress"
            showPercent={false}
            className="flex-1"
            fillClassName={progressFillClassName}
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
              onClick={() => window.open(`${getApiBaseUrl()}/artifacts/job/${jobId}`, '_blank')}
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
