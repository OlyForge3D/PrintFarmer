import React, { useState, useMemo, useEffect } from 'react';
import { useNavigate } from 'react-router';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { SelectableRow } from '@/common/components/Table/SelectableRow';
import { createSlicerRegistryConnection } from '@/services/slicerRegistryHubConnection';
import {
  slicerProfilesService,
  type OrcaProcessProfile,
  type OrcaFilamentProfile,
  type OrcaMachineProfile,
  type WorkerPrinterModelProfilesDto,
  type WorkerHierarchyResponse,
  ImportSlicerProfileRequest,
  SlicerProfileExtended,
  BulkDeleteResultDto,
  UploadProfileRequest,
  CustomProfile,
  CustomProfilesListResponse,
  UpdateCustomProfileRequest
} from '@/services/slicerProfilesService';
import { orcaProfilesService } from '@/features/slicer/orca';
import { slicerRegistry } from '@/services/slicerRegistry';
import { catalogService } from '@/services/catalogService';
import { FilterIcon, GearIcon, UploadIcon, SearchIcon, CheckCircleIcon, AlertCircleIcon, TimerSandIcon } from '@/common/components/icons/MdiIcons';
import { PageTemplate } from '@/common/components/PageTemplate';
import type { EmbeddablePageProps } from '@/common/components/EmbeddablePageProps';
import { Button } from '@/common/components/ui/Button';
import { Alert } from '@/common/components/ui/Alert';
import { Tabs } from '@/common/components/ui/Tabs';
import { FormField } from '@/common/components/ui/FormField';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { Textarea } from '@/common/components/ui/Textarea';
import { Modal } from '@/common/components/modals/Modal';
import { useMachineCompatibleProfiles } from '@/features/slicer/hooks/useMachineCompatibleProfiles';

type LibraryProfile =
  | (OrcaMachineProfile & { profileType: 'machine' })
  | (OrcaFilamentProfile & { profileType: 'filament' })
  | (OrcaProcessProfile & { profileType: 'process' });

const rowKey = (profile: LibraryProfile) => `${profile.profileType}:${profile.name}`;

export const SlicerProfilesPage: React.FC<EmbeddablePageProps> = ({ embedded = false }) => {
  const qc = useQueryClient();
  const navigate = useNavigate();

  // Form state
  const [rawJson, setRawJson] = useState('');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [slicerType, setSlicerType] = useState('');
  const [allowSystemOverride, setAllowSystemOverride] = useState(false);
  const [setDefault, setSetDefault] = useState(false);
  const [isPublic, setIsPublic] = useState(true);
  // import form visibility handled via modal state `isImportModalOpen`
  const [isImportModalOpen, setIsImportModalOpen] = useState(false);

  // Upload custom profile modal state
  const [isUploadModalOpen, setIsUploadModalOpen] = useState(false);
  const [uploadRawJson, setUploadRawJson] = useState('');
  const [uploadName, setUploadName] = useState('');
  const [uploadProfileType, setUploadProfileType] = useState<'machine' | 'filament' | 'process'>('process');
  // Catalog PrinterModel association for the uploaded profile (machine/process only).
  // Empty string means "auto-detect" (let the backend resolve from raw JSON via aliases).
  const [uploadPrinterModelId, setUploadPrinterModelId] = useState<string>('');
  // Compatible printer names for the uploaded profile (filament only). Empty array
  // means "auto-detect from raw JSON's compatible_printers".
  const [uploadCompatiblePrinters, setUploadCompatiblePrinters] = useState<string[]>([]);
  const [uploadError, setUploadError] = useState<string | null>(null);

  // Edit custom profile modal state
  const [isEditModalOpen, setIsEditModalOpen] = useState(false);
  const [editingProfile, setEditingProfile] = useState<CustomProfile | null>(null);
  const [editName, setEditName] = useState('');
  const [editDescription, setEditDescription] = useState('');
  // Catalog PrinterModel association for the edited profile (machine/process only).
  // Empty string means "clear association"; any GUID means "set to this model".
  const [editPrinterModelId, setEditPrinterModelId] = useState<string>('');
  // Compatible printer names for the edited profile (filament only).
  const [editCompatiblePrinters, setEditCompatiblePrinters] = useState<string[]>([]);
  const [editError, setEditError] = useState<string | null>(null);

  // Tab state - 'machines', 'filaments', 'processes', 'custom'
  const [activeTab, setActiveTab] = useState<'machines' | 'filaments' | 'processes' | 'custom'>('machines');

  // Filtering and search state
  const [searchQuery, setSearchQuery] = useState('');
  const [filterManufacturer, setFilterManufacturer] = useState<string>('all');
  const [selectedMachineModelId, setSelectedMachineModelId] = useState<string>('');
  const [selectedMachineProfileId, setSelectedMachineProfileId] = useState<string>('');
  const [filterEngine, setFilterEngine] = useState<string>('all');
  const [filterSource, setFilterSource] = useState<string>('all');
  const [showFilters, setShowFilters] = useState(false);

  // Paging state
  const [pageSize, setPageSize] = useState<number>(25);
  const [pageNumber, setPageNumber] = useState<number>(1);

  // UI state
  const [importError, setImportError] = useState<string | null>(null);
  const [exportingBundle, setExportingBundle] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [reseedModalOpen, setReseedModalOpen] = useState(false);
  const [reseedStatus, setReseedStatus] = useState<'loading' | 'success' | 'error'>('loading');
  const [reseedMessage, setReseedMessage] = useState<string>('Loading system profiles...');

  // Selection state for bulk delete
  const [selectedProfileIds, setSelectedProfileIds] = useState<Set<string>>(new Set());

  // Fetch available slicers
  const { data: availableSlicers = [] } = useQuery({
    queryKey: ['slicers-available'],
    queryFn: () => slicerRegistry.getSlicers(),
    staleTime: 10_000,
    refetchInterval: 15_000,
  });

  // Catalog printer models — used by the upload and edit dialogs to associate a
  // custom machine/process profile with a known printer model. Filament profiles
  // do not carry a PrinterModelId and the picker is hidden for that type.
  const { data: catalogPrinterModels = [] } = useQuery({
    queryKey: ['catalog-printer-models'],
    queryFn: () => catalogService.getModels(),
    staleTime: 60_000,
  });

  const sortedCatalogPrinterModels = useMemo(
    () => [...catalogPrinterModels].sort((a, b) => a.name.localeCompare(b.name)),
    [catalogPrinterModels]
  );

  // Extract slicer names for the dropdown
  const slicerNames = useMemo(() => {
    return availableSlicers
      .map(s => s.slicerType || s.name || '')
      .filter((v, i, arr) => v && arr.indexOf(v) === i)
      .sort();
  }, [availableSlicers]);

  // Set initial slicer type to first available
  React.useEffect(() => {
    if (!slicerType && slicerNames.length > 0) {
      setSlicerType(slicerNames[0]);
    }
  }, [slicerNames, slicerType]);

  const {
    data: profilesData,
    isLoading,
    error: libraryHierarchyError,
  } = useQuery<WorkerHierarchyResponse, Error>({
    queryKey: ['slicerProfilesLibraryHierarchy', 'all'],
    queryFn: () => slicerProfilesService.getLibraryHierarchy('all'),
    staleTime: 30_000,
  });

  // Custom profiles query - loads user-owned custom profiles
  const { data: customProfilesData, isLoading: customProfilesLoading } = useQuery<CustomProfilesListResponse, Error>({
    queryKey: ['customProfiles'],
    queryFn: async () => slicerProfilesService.listCustomProfiles(),
    staleTime: 10_000
  });

  type MachineProfileContext = {
    manufacturer: string;
    modelId: string;
    modelName: string;
    modelData: WorkerPrinterModelProfilesDto;
  };

  const machineContextByName = useMemo<Map<string, MachineProfileContext>>(() => {
    const map = new Map<string, MachineProfileContext>();
    if (!profilesData?.byHierarchy) return map;

    for (const [manufacturer, mfgData] of Object.entries(profilesData.byHierarchy)) {
      for (const [modelId, modelData] of Object.entries(mfgData.models)) {
        for (const machine of modelData.machineProfiles ?? []) {
          map.set(machine.name, {
            manufacturer,
            modelId,
            modelName: modelData.name,
            modelData,
          });
        }
      }
    }
    return map;
  }, [profilesData]);

  const selectedModelContext = useMemo<MachineProfileContext | undefined>(() => {
    if (!selectedMachineModelId || filterManufacturer === 'all') return undefined;
    const modelData = profilesData?.byHierarchy[filterManufacturer]?.models[selectedMachineModelId];
    if (!modelData) return undefined;
    return {
      manufacturer: filterManufacturer,
      modelId: selectedMachineModelId,
      modelName: modelData.name,
      modelData,
    };
  }, [filterManufacturer, profilesData, selectedMachineModelId]);

  const selectedMachineNames = useMemo(() => {
    if (!selectedModelContext) return [];
    if (selectedMachineProfileId) return [selectedMachineProfileId];
    return (selectedModelContext.modelData.machineProfiles ?? []).map((profile) => profile.name);
  }, [selectedMachineProfileId, selectedModelContext]);

  const {
    filamentProfilesQuery,
    processProfilesQuery,
  } = useMachineCompatibleProfiles(selectedMachineNames, {
    enabled: selectedMachineNames.length > 0,
    summary: true,
  });

  const importMutation = useMutation<SlicerProfileExtended, Error, ImportSlicerProfileRequest>({
    mutationFn: async (payload) => {
      return slicerProfilesService.importProfile(payload);
    },
    onSuccess: (res) => {
      setMessage(res ? (res.isDefault ? 'Imported and set as default.' : 'Profile imported.') : 'Imported.');
      setImportError(null);
      setRawJson('');
      setName('');
      setDescription('');
      setAllowSystemOverride(false);
      setSetDefault(false);
      qc.invalidateQueries({ queryKey: ['slicerProfilesLibraryHierarchy'] });
      qc.invalidateQueries({ queryKey: ['filamentProfilesForMachines'] });
      qc.invalidateQueries({ queryKey: ['processProfilesForMachines'] });
      qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
    },
    onError: (err) => {
      setImportError(err.message);
    }
  });

  const bulkDeleteMutation = useMutation<BulkDeleteResultDto, Error, string[]>({
    mutationFn: async (ids) => slicerProfilesService.bulkDelete(ids),
    onSuccess: (result) => {
      setMessage(`Deleted ${result.totalDeleted} profiles (${result.machineProfilesDeleted} machine, ${result.processProfilesDeleted} process, ${result.filamentProfilesDeleted} filament)${result.notFound > 0 ? ` - ${result.notFound} not found` : ''}`);
      setSelectedProfileIds(new Set());
      qc.invalidateQueries({ queryKey: ['slicerProfilesLibraryHierarchy'] });
      qc.invalidateQueries({ queryKey: ['filamentProfilesForMachines'] });
      qc.invalidateQueries({ queryKey: ['processProfilesForMachines'] });
      qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
      qc.invalidateQueries({ queryKey: ['customProfiles'] });
    },
    onError: (err) => setMessage(`Failed to delete profiles: ${err.message}`)
  });

  // Upload custom profile mutation - creates a new custom profile from raw JSON
  const uploadProfileMutation = useMutation<CustomProfile, Error, UploadProfileRequest>({
    mutationFn: async (request) => slicerProfilesService.uploadProfile(request),
    onSuccess: (result) => {
      setMessage(`Created custom profile: ${result.name}`);
      setUploadRawJson('');
      setUploadName('');
      setUploadPrinterModelId('');
      setUploadCompatiblePrinters([]);
      setUploadError(null);
      setIsUploadModalOpen(false);
      qc.invalidateQueries({ queryKey: ['slicerProfilesLibraryHierarchy'] });
      qc.invalidateQueries({ queryKey: ['filamentProfilesForMachines'] });
      qc.invalidateQueries({ queryKey: ['processProfilesForMachines'] });
      qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
      qc.invalidateQueries({ queryKey: ['customProfiles'] });
    },
    onError: (err) => setUploadError(err.message)
  });

  // Update custom profile mutation
  const updateProfileMutation = useMutation<CustomProfile, Error, { id: string; request: UpdateCustomProfileRequest }>({
    mutationFn: async ({ id, request }) => slicerProfilesService.updateCustomProfile(id, request),
    onSuccess: (result) => {
      setMessage(`Updated profile: ${result.name}`);
      setIsEditModalOpen(false);
      setEditingProfile(null);
      setEditError(null);
      qc.invalidateQueries({ queryKey: ['slicerProfilesLibraryHierarchy'] });
      qc.invalidateQueries({ queryKey: ['filamentProfilesForMachines'] });
      qc.invalidateQueries({ queryKey: ['processProfilesForMachines'] });
      qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
      qc.invalidateQueries({ queryKey: ['customProfiles'] });
    },
    onError: (err) => setEditError(err.message)
  });

  // Delete single custom profile mutation
  const deleteProfileMutation = useMutation<void, Error, string>({
    mutationFn: async (id) => slicerProfilesService.deleteCustomProfile(id),
    onSuccess: () => {
      setMessage('Profile deleted');
      qc.invalidateQueries({ queryKey: ['slicerProfilesLibraryHierarchy'] });
      qc.invalidateQueries({ queryKey: ['filamentProfilesForMachines'] });
      qc.invalidateQueries({ queryKey: ['processProfilesForMachines'] });
      qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
      qc.invalidateQueries({ queryKey: ['customProfiles'] });
    },
    onError: (err) => setMessage(`Failed to delete profile: ${err.message}`)
  });

  // Helper to open edit modal for a custom profile
  const openEditModal = (profile: CustomProfile) => {
    setEditingProfile(profile);
    setEditName(profile.name);
    setEditDescription(profile.description || '');
    // Seed the printer-model picker from the current association so the user can
    // see (and repair) it. Filament profiles always have null here and the picker
    // is hidden in the modal, so seeding is harmless.
    setEditPrinterModelId(profile.printerModelId || '');
    // Seed the compatible-printers picker (filament only). Defaults to [] for
    // non-filament profiles where the picker is hidden.
    setEditCompatiblePrinters(profile.compatiblePrinters ?? []);
    setEditError(null);
    setIsEditModalOpen(true);
  };

  const onEditProfile = (e: React.FormEvent) => {
    e.preventDefault();
    if (!editingProfile) return;
    if (!editName.trim()) {
      setEditError('Name is required');
      return;
    }
    // Only send printer-model fields for machine/process profiles. Filament does
    // not carry a PrinterModelId column and the backend ignores those fields, but
    // we omit them explicitly to keep the payload tidy.
    const isFilament = editingProfile.profileType === 'filament';
    const previousPrinterModelId = editingProfile.printerModelId || '';
    const printerModelChanged = !isFilament && editPrinterModelId !== previousPrinterModelId;

    // Compatible-printers diff (filament only). Order-insensitive comparison so
    // accidental reorders do not trigger an unnecessary write.
    const previousCompatible = editingProfile.compatiblePrinters ?? [];
    const compatibleChanged =
      isFilament &&
      (previousCompatible.length !== editCompatiblePrinters.length ||
        [...previousCompatible].sort().join('|') !== [...editCompatiblePrinters].sort().join('|'));
    const compatiblePayload = compatibleChanged
      ? editCompatiblePrinters.length > 0
        ? { compatiblePrinters: editCompatiblePrinters }
        : { clearCompatiblePrinters: true }
      : {};

    updateProfileMutation.mutate({
      id: editingProfile.id,
      request: {
        name: editName,
        description: editDescription || undefined,
        ...(printerModelChanged
          ? editPrinterModelId
            ? { printerModelId: editPrinterModelId }
            : { clearPrinterModelId: true }
          : {}),
        ...compatiblePayload
      }
    });
  };

  const onUploadCustomProfile = (e: React.FormEvent) => {
    e.preventDefault();
    if (!uploadRawJson.trim()) {
      setUploadError('Raw JSON is required');
      return;
    }
    const isFilament = uploadProfileType === 'filament';
    uploadProfileMutation.mutate({
      rawJson: uploadRawJson,
      profileType: uploadProfileType,
      name: uploadName || undefined,
      // Only attach a PrinterModelId for machine/process profiles. Empty string
      // means "auto-detect" — let the backend resolve from raw JSON via aliases.
      ...(!isFilament && uploadPrinterModelId
        ? { printerModelId: uploadPrinterModelId }
        : {}),
      // Only attach compatiblePrinters for filament profiles. Empty array means
      // "auto-detect" from the raw JSON's compatible_printers.
      ...(isFilament && uploadCompatiblePrinters.length > 0
        ? { compatiblePrinters: uploadCompatiblePrinters }
        : {})
    });
  };

  const handleToggleSelection = (id: string) => {
    setSelectedProfileIds(prev => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  };

  const exportOrcaBundle = async () => {
    setExportingBundle(true);
    try {
      const bundleJson = await orcaProfilesService.exportBundle({
        includeProcessProfiles: true,
        includeMetadata: true
      });

      const blob = new Blob([bundleJson], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      const timestamp = new Date().toISOString().split('T')[0];
      a.download = `printfarmer_orca_bundle_${timestamp}.json`;
      a.click();
      URL.revokeObjectURL(url);
      setMessage('OrcaSlicer bundle exported successfully.');
    } catch (e) {
      setMessage(e instanceof Error ? e.message : 'Bundle export failed');
    } finally {
      setExportingBundle(false);
    }
  };

  const allMachineProfiles = useMemo(
    () => Array.from(machineContextByName.entries())
      .map(([name, context]) => {
        const profile = context.modelData.machineProfiles?.find((item) => item.name === name);
        return profile ? { ...profile, profileType: 'machine' as const } : null;
      })
      .filter((profile): profile is OrcaMachineProfile & { profileType: 'machine' } => profile !== null),
    [machineContextByName],
  );

  const filteredMachineProfiles = useMemo(() => {
    return allMachineProfiles.filter((p) => {
      const ctx = machineContextByName.get(p.name);
      if (!ctx) return false;

      // Manufacturer filter
      if (filterManufacturer !== 'all' && ctx.manufacturer !== filterManufacturer) return false;

      // Search filter
      if (searchQuery) {
        const query = searchQuery.toLowerCase();
        if (!p.name.toLowerCase().includes(query) && !p.manufacturer.toLowerCase().includes(query)) {
          return false;
        }
      }

      if (filterEngine !== 'all' && filterEngine !== 'OrcaSlicer') return false;

      if (filterSource !== 'all' && filterSource !== '') {
        if (filterSource !== 'system') return false;
      }

      return true;
    });
  }, [
    allMachineProfiles,
    filterEngine,
    filterManufacturer,
    filterSource,
    machineContextByName,
    searchQuery,
  ]);

  const filteredFilamentProfiles = useMemo(() => {
    const sourceProfiles = filamentProfilesQuery.data ?? [];
    return sourceProfiles.filter((p) => {
      if (searchQuery) {
        const query = searchQuery.toLowerCase();
        if (!p.name.toLowerCase().includes(query) && !p.material.toLowerCase().includes(query)) {
          return false;
        }
      }
      if (filterEngine !== 'all' && filterEngine !== 'OrcaSlicer') return false;

      if (filterSource !== 'all' && filterSource !== '') {
        if (filterSource !== 'system') return false;
      }
      return true;
    });
  }, [filamentProfilesQuery.data, filterEngine, filterSource, searchQuery]);

  const filteredProcessProfiles = useMemo(() => {
    const sourceProfiles = processProfilesQuery.data ?? [];
    return sourceProfiles.filter((p) => {
      if (searchQuery) {
        const query = searchQuery.toLowerCase();
        if (!p.name.toLowerCase().includes(query) && !p.quality.toLowerCase().includes(query)) {
          return false;
        }
      }
      if (filterEngine !== 'all' && filterEngine !== 'OrcaSlicer') return false;

      if (filterSource !== 'all' && filterSource !== '') {
        if (filterSource !== 'system') return false;
      }
      return true;
    });
  }, [filterEngine, filterSource, processProfilesQuery.data, searchQuery]);

  // Filtered custom profiles for "My Profiles" tab
  const filteredCustomProfiles = useMemo<CustomProfile[]>(() => {
    if (!customProfilesData?.profiles) return [];
    
    return customProfilesData.profiles.filter((p) => {
      if (searchQuery) {
        const query = searchQuery.toLowerCase();
        if (!p.name.toLowerCase().includes(query)) {
          return false;
        }
      }
      return true;
    });
  }, [customProfilesData, searchQuery]);

  const onImport = (e: React.FormEvent) => {
    e.preventDefault();
    if (!rawJson.trim()) {
      setImportError('Raw profile JSON is required');
      return;
    }
    importMutation.mutate({
      rawJson: rawJson,
      name: name || undefined,
      description: description || undefined,
      slicerType,
      allowSystemOverride,
      setDefault,
      isPublic
    });
  };

  const renderProfileRow = (p: LibraryProfile) => (
    <tr key={rowKey(p)} className="border-t border-pf-border">
      <td className="p-2 font-medium">{p.name}</td>
      <td className="p-2">OrcaSlicer</td>
      <td className="p-2">{p.profileType === 'filament' ? p.material : p.profileType === 'machine' ? p.manufacturer : '-'}</td>
      <td className="p-2">{p.profileType === 'process' ? p.quality : '-'}</td>
      <td className="p-2">{p.profileType === 'process' ? `${p.layerHeight.toFixed(2)}mm` : p.profileType === 'machine' && p.nozzleDiameter ? `${p.nozzleDiameter}mm` : '-'}</td>
      <td className="p-2">{p.profileType === 'process' ? `${p.infillPercentage}%` : '-'}</td>
      <td className="p-2">
        <span className="px-2 py-0.5 bg-pf-bg-2 text-pf-text-primary rounded-sm text-xs">System</span>
      </td>
    </tr>
  );

  // Render a custom profile row for the "My Profiles" tab
  const renderCustomProfileRow = (p: CustomProfile) => (
    <SelectableRow key={p.id} className="border-t border-pf-border" isSelected={selectedProfileIds.has(p.id)}>
      <td className="p-2">
        <Checkbox
          checked={selectedProfileIds.has(p.id)}
          onChange={() => handleToggleSelection(p.id)}
          label=""
          aria-label={`Select ${p.name}`}
        />
      </td>
      <td className="p-2 font-medium">{p.name}</td>
      <td className="p-2 capitalize">{p.profileType}</td>
      <td className="p-2">{p.description || '-'}</td>
      <td className="p-2">
        {p.createdAt ? new Date(p.createdAt).toLocaleDateString() : '-'}
      </td>
      <td className="p-2">
        {p.updatedAt ? new Date(p.updatedAt).toLocaleDateString() : '-'}
      </td>
      <td className="p-2">
        <div className="flex gap-2">
          <Button
            onClick={() => openEditModal(p)}
            size="sm"
            variant="secondary"
          >Edit</Button>
          <Button
            onClick={() => {
              if (window.confirm(`Delete profile "${p.name}"?`)) {
                deleteProfileMutation.mutate(p.id);
              }
            }}
            loading={deleteProfileMutation.isPending}
            size="sm"
            variant="danger"
          >Delete</Button>
        </div>
      </td>
    </SelectableRow>
  );
  const getFilteredCount = () => {
    if (activeTab === 'machines') return filteredMachineProfiles.length;
    if (activeTab === 'filaments') return filteredFilamentProfiles.length;
    if (activeTab === 'processes') return filteredProcessProfiles.length;
    if (activeTab === 'custom') return filteredCustomProfiles.length;
    return 0;
  };

  const manufacturerOptions = useMemo(() => {
    return Object.keys(profilesData?.byHierarchy ?? {}).sort();
  }, [profilesData]);

  const machineModelOptions = useMemo(() => {
    if (!filterManufacturer || filterManufacturer === 'all') return [];
    const models = profilesData?.byHierarchy[filterManufacturer]?.models ?? {};
    return Object.entries(models)
      .map(([id, model]) => ({ id, name: model.name }))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [filterManufacturer, profilesData]);

  const machineProfileOptions = useMemo(
    () => [...(selectedModelContext?.modelData.machineProfiles ?? [])]
      .sort((a, b) => a.name.localeCompare(b.name)),
    [selectedModelContext],
  );

  const visibleProfiles = useMemo<LibraryProfile[]>(() => {
    if (activeTab === 'machines') return filteredMachineProfiles;
    if (activeTab === 'filaments') return filteredFilamentProfiles.map((profile) => ({ ...profile, profileType: 'filament' }));
    if (activeTab === 'processes') return filteredProcessProfiles.map((profile) => ({ ...profile, profileType: 'process' }));
    // 'custom' tab handles its own rendering
    return [];
  }, [activeTab, filteredFilamentProfiles, filteredMachineProfiles, filteredProcessProfiles]);

  const totalPages = useMemo(() => {
    return Math.max(1, Math.ceil(visibleProfiles.length / pageSize));
  }, [pageSize, visibleProfiles.length]);

  const safePageNumber = Math.min(Math.max(1, pageNumber), totalPages);

  const pagedProfiles = useMemo(() => {
    const start = (safePageNumber - 1) * pageSize;
    return visibleProfiles.slice(start, start + pageSize);
  }, [pageSize, safePageNumber, visibleProfiles]);

  const handleDeleteSelected = () => {
    if (selectedProfileIds.size === 0) return;
    if (!window.confirm(`Are you sure you want to delete ${selectedProfileIds.size} profile(s)?`)) return;
    bulkDeleteMutation.mutate(Array.from(selectedProfileIds));
  };

  // Clear selection when switching tabs or filtering
  React.useEffect(() => {
    setSelectedProfileIds(new Set());
  }, [activeTab, filterEngine, filterManufacturer, filterSource, searchQuery]);

  React.useEffect(() => {
    setPageNumber(1);
  }, [activeTab, filterEngine, filterManufacturer, filterSource, pageSize, searchQuery, selectedMachineModelId, selectedMachineProfileId]);

  React.useEffect(() => {
    // When manufacturer changes, clear dependent selections
    setSelectedMachineModelId('');
    setSelectedMachineProfileId('');
  }, [filterManufacturer]);

  React.useEffect(() => {
    // When machine model changes, clear dependent selections
    setSelectedMachineProfileId('');
  }, [selectedMachineModelId]);

  // SignalR event listener for profile import progress
  useEffect(() => {
    try {
      const { connection: hubConnection, dispose } =
        createSlicerRegistryConnection('slicer-registry-profiles-page');

      // Listen for profile import events
      hubConnection.on('profileimportstarted', (data: { message: string }) => {
        if (reseedModalOpen) {
          setReseedStatus('loading');
          setReseedMessage(data.message);
        }
      });

      hubConnection.on('profileimported', (data: { profileName: string; profileType: string; count: number }) => {
        if (reseedModalOpen) {
          setReseedMessage(`Imported: ${data.count} profiles... (${data.profileType}: ${data.profileName})`);
        }
      });

      hubConnection.on('profileimportcompleted', (data: { imported: number; skipped: number; deleted: number; message: string }) => {
        if (reseedModalOpen) {
          setReseedStatus('success');
          setReseedMessage(`✅ ${data.imported} profiles imported, ${data.skipped} skipped, ${data.deleted} deleted`);
          // Refresh the profiles list
          qc.invalidateQueries({ queryKey: ['slicerProfilesLibraryHierarchy'] });
          qc.invalidateQueries({ queryKey: ['filamentProfilesForMachines'] });
          qc.invalidateQueries({ queryKey: ['processProfilesForMachines'] });
          qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
        }
      });

      hubConnection.on('profileimporterror', (data: { error: string; profileName: string }) => {
        if (reseedModalOpen) {
          setReseedStatus('error');
          setReseedMessage(`❌ Error importing ${data.profileName}: ${data.error}`);
        }
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
  }, [reseedModalOpen, qc]);

  const libraryUnavailable = Boolean(
    libraryHierarchyError
    || processProfilesQuery.error
    || filamentProfilesQuery.error,
  );

  return (
    <PageTemplate
      title="Slicer Profiles"
      subtitle="Manage imported slicer profiles (OrcaSlicer / PrusaSlicer)"
      icon={GearIcon}
      embedded={embedded}
    >
      {/* OrcaSlicer Quick Actions */}
      <div className="flex flex-wrap gap-3 mb-4">
        <Button
          variant="secondary"
          onClick={() => navigate('/profiles/import')}
          className="flex items-center gap-2"
          iconLeft={<UploadIcon className="w-4 h-4" />}
        >
          Import Profiles...
        </Button>
        <Button
          variant="secondary"
          onClick={() => setIsImportModalOpen(true)}
          className="flex items-center gap-2"
          iconLeft={<UploadIcon className="w-4 h-4" />}
        >
          Import Profile
        </Button>
        <Button
          variant="secondary"
          onClick={() => exportOrcaBundle()}
          className="flex items-center gap-2"
          loading={exportingBundle}
          iconLeft={<UploadIcon className="w-4 h-4" />}
        >
          Export Orca Bundle
        </Button>
        <Button
          variant="primary"
          onClick={() => setIsUploadModalOpen(true)}
          className="flex items-center gap-2"
          iconLeft={<UploadIcon className="w-4 h-4" />}
        >
          Upload Custom Profile
        </Button>
      </div>

      <div className="space-y-4">
        {message && <Alert type="success">{message}</Alert>}
        <div className="bg-pf-panel rounded-sm shadow-sm">
          {/* Header with Search and Filters */}
          <div className="p-4 border-b border-pf-border">
            <div className="flex items-center gap-4 mb-4">
              <div className="flex-1 relative">
                <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-pf-text-muted" />
                <Input
                  type="text"
                  placeholder="Search profiles by name, material, or manufacturer..."
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  className="w-full pl-10 pr-4 py-2 bg-pf-bg-0 border border-pf-border rounded-lg focus:ring-2 focus:ring-pf-accent focus:border-transparent"
                />
              </div>
              <Button
                variant={showFilters ? 'primary' : 'secondary'}
                onClick={() => setShowFilters(!showFilters)}
                className="flex items-center gap-2"
                size="sm"
                iconLeft={<FilterIcon className="w-4 h-4" />}
              >
                Filters
              </Button>
            </div>

            {/* Primary selection flow (always visible): Manufacturer -> Machine Model -> Process/Filament */}
            <div className="grid grid-cols-1 md:grid-cols-3 gap-3 p-3 bg-pf-bg-0 rounded-lg">
              <div>
                <label htmlFor="profile-manufacturer" className="block text-sm font-medium mb-1">Manufacturer</label>
                <Select
                  id="profile-manufacturer"
                  value={filterManufacturer}
                  onChange={(e) => setFilterManufacturer(e.target.value)}
                  aria-label="Select manufacturer"
                >
                  <option value="all">Select a manufacturer</option>
                  {manufacturerOptions.map(m => (
                    <option key={m} value={m}>{m}</option>
                  ))}
                </Select>
              </div>
              <div>
                <label htmlFor="profile-machine-model" className="block text-sm font-medium mb-1">Machine Model</label>
                <Select
                  id="profile-machine-model"
                  value={selectedMachineModelId}
                  onChange={(e) => setSelectedMachineModelId(e.target.value)}
                  aria-label="Select machine model"
                  disabled={filterManufacturer === 'all'}
                >
                  <option value="">Select a machine model</option>
                  {machineModelOptions.map(m => (
                    <option key={m.id} value={m.id}>{m.name}</option>
                  ))}
                </Select>
              </div>
              <div>
                <label htmlFor="profile-machine-variant" className="block text-sm font-medium mb-1">Machine</label>
                <Select
                  id="profile-machine-variant"
                  value={selectedMachineProfileId}
                  onChange={(e) => setSelectedMachineProfileId(e.target.value)}
                  aria-label="Select machine"
                  disabled={!selectedMachineModelId}
                >
                  <option value="">All machines for this model</option>
                  {machineProfileOptions.map(machine => (
                    <option key={machine.name} value={machine.name}>{machine.name}</option>
                  ))}
                </Select>
              </div>
            </div>

            {/* Advanced filters */}
            {showFilters && (
              <div className="grid grid-cols-1 md:grid-cols-2 gap-3 mt-3 p-3 bg-pf-bg-0 rounded-lg">
                <div>
                  <label className="block text-sm font-medium mb-1">Engine</label>
                  <Select
                    value={filterEngine}
                    onChange={(e) => setFilterEngine(e.target.value)}
                    aria-label="Filter by engine"
                  >
                    <option value="all">All Engines</option>
                    {slicerNames.map(s => <option key={s} value={s}>{s}</option>)}
                  </Select>
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">Source</label>
                  <Select
                    value={filterSource}
                    onChange={(e) => setFilterSource(e.target.value)}
                    aria-label="Filter by source"
                  >
                    <option value="all">All Sources</option>
                    <option value="default">Default</option>
                    <option value="system">System</option>
                    <option value="public">Public</option>
                    <option value="imported">Imported</option>
                  </Select>
                </div>
              </div>
            )}

            {/* Active Filter Summary and Tabs */}
            <div className="flex items-center justify-between mt-3">
              <div className="flex items-center gap-4">
                <p className="text-sm text-pf-text-muted">
                  Showing {getFilteredCount()} profile(s)
                </p>
                <div className="flex items-center gap-2">
                  <span className="text-sm text-pf-text-muted">Rows</span>
                  <Select
                    value={String(pageSize)}
                    onChange={(e) => setPageSize(Number(e.target.value))}
                    aria-label="Rows per page"
                  >
                    <option value="10">10</option>
                    <option value="25">25</option>
                    <option value="50">50</option>
                    <option value="100">100</option>
                  </Select>
                </div>
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={() => {
                    qc.invalidateQueries({ queryKey: ['slicerProfilesLibraryHierarchy'] });
                    qc.invalidateQueries({ queryKey: ['filamentProfilesForMachines'] });
                    qc.invalidateQueries({ queryKey: ['processProfilesForMachines'] });
                  }}
                >Refresh</Button>
              </div>
              {(searchQuery || filterManufacturer !== 'all' || filterEngine !== 'all' || filterSource !== 'all' || selectedMachineModelId || selectedMachineProfileId) && (
                <Button
                  type="button"
                  onClick={() => {
                    setSearchQuery('');
                    setFilterManufacturer('all');
                    setFilterEngine('all');
                    setFilterSource('all');
                    setSelectedMachineModelId('');
                    setSelectedMachineProfileId('');
                  }}
                  variant="subtle"
                  size="sm"
                >
                  Clear filters
                </Button>
              )}
            </div>

            {/* Profile Type Tabs */}
            <Tabs activeTab={activeTab} onTabChange={(tabId) => setActiveTab(tabId as 'machines' | 'filaments' | 'processes' | 'custom')}>
              <Tabs.List className="border-b border-pf-border bg-transparent !p-0">
                <Tabs.Tab id="machines">
                  Machines ({filteredMachineProfiles.length})
                </Tabs.Tab>
                <Tabs.Tab id="processes">
                  Processes ({selectedMachineModelId ? filteredProcessProfiles.length : 0})
                </Tabs.Tab>
                <Tabs.Tab id="filaments">
                  Filaments ({selectedMachineModelId ? filteredFilamentProfiles.length : 0})
                </Tabs.Tab>
                <Tabs.Tab id="custom">
                  My Profiles ({filteredCustomProfiles.length})
                </Tabs.Tab>
              </Tabs.List>
            </Tabs>
          </div>

          {/* Profiles Table */}
          <div className="p-4">
            {libraryUnavailable && (
              <Alert type="warning" title="Profile library unavailable" className="mb-4">
                OrcaSlicer worker unavailable; the profile library cannot be listed. My Profiles is still available.
              </Alert>
            )}
            {isLoading && activeTab !== 'custom' && <div>Loading profiles...</div>}
            {!isLoading && !libraryUnavailable && getFilteredCount() === 0 && activeTab === 'machines' && (
              <div className="text-pf-text-muted text-sm">No profiles match your filters.</div>
            )}
            {!isLoading && activeTab === 'processes' && !selectedMachineModelId && (
              <div className="text-pf-text-muted text-sm">Select a printer model to see compatible processes.</div>
            )}
            {!isLoading && activeTab === 'filaments' && !selectedMachineModelId && (
              <div className="text-pf-text-muted text-sm">Select a printer model to see compatible filaments.</div>
            )}
            {activeTab === 'processes' && selectedMachineModelId && processProfilesQuery.isLoading && (
              <div className="text-pf-text-muted text-sm">Loading compatible processes...</div>
            )}
            {activeTab === 'filaments' && selectedMachineModelId && filamentProfilesQuery.isLoading && (
              <div className="text-pf-text-muted text-sm">Loading compatible filaments...</div>
            )}
            {activeTab === 'processes' && selectedMachineModelId && !processProfilesQuery.isLoading && !libraryUnavailable && filteredProcessProfiles.length === 0 && (
              <div className="text-pf-text-muted text-sm">No compatible process profiles found for the selected machines.</div>
            )}
            {activeTab === 'filaments' && selectedMachineModelId && !filamentProfilesQuery.isLoading && !libraryUnavailable && filteredFilamentProfiles.length === 0 && (
              <div className="text-pf-text-muted text-sm">No compatible filament profiles found for the selected machines.</div>
            )}
            {activeTab === 'filaments' && selectedMachineModelId && !libraryUnavailable && (
              <p className="text-pf-text-muted text-sm mb-3">Includes universal library filaments.</p>
            )}
            
            {/* Custom Profiles Tab Content */}
            {activeTab === 'custom' && (
              <>
                {customProfilesLoading && <div>Loading custom profiles...</div>}
                {!customProfilesLoading && filteredCustomProfiles.length === 0 && (
                  <div className="text-center py-8">
                    <p className="text-pf-text-muted mb-4">No custom profiles yet.</p>
                    <p className="text-sm text-pf-text-secondary mb-4">
                      Create custom profiles by cloning system profiles or uploading your own.
                    </p>
                    <Button
                      variant="primary"
                      onClick={() => setIsUploadModalOpen(true)}
                      iconLeft={<UploadIcon className="w-4 h-4" />}
                    >
                      Upload Custom Profile
                    </Button>
                  </div>
                )}
                {!customProfilesLoading && filteredCustomProfiles.length > 0 && (
                  <>
                    {selectedProfileIds.size > 0 && (
                      <div className="flex items-center gap-4 mb-4 p-2 bg-pf-bg-2 rounded-sm">
                        <span className="text-sm text-pf-text-secondary">{selectedProfileIds.size} profile(s) selected</span>
                        <Button
                          onClick={handleDeleteSelected}
                          loading={bulkDeleteMutation.isPending}
                          size="sm"
                          variant="danger"
                        >
                          Delete Selected
                        </Button>
                        <Button
                          onClick={() => setSelectedProfileIds(new Set())}
                          size="sm"
                          variant="subtle"
                        >
                          Clear Selection
                        </Button>
                      </div>
                    )}
                    <div className="overflow-x-auto">
                    <table className="min-w-full text-sm">
                      <thead>
                        <tr className="bg-pf-bg-1 text-left">
                          <th className="p-2 w-10">
                            <Checkbox
                              checked={filteredCustomProfiles.length > 0 && filteredCustomProfiles.every(p => selectedProfileIds.has(p.id))}
                              onChange={() => {
                                if (selectedProfileIds.size === filteredCustomProfiles.length && filteredCustomProfiles.every(p => selectedProfileIds.has(p.id))) {
                                  setSelectedProfileIds(new Set());
                                } else {
                                  setSelectedProfileIds(new Set(filteredCustomProfiles.map(p => p.id)));
                                }
                              }}
                              label=""
                              aria-label="Select all custom profiles"
                            />
                          </th>
                          <th className="p-2">Name</th>
                          <th className="p-2">Type</th>
                          <th className="p-2">Description</th>
                          <th className="p-2">Created</th>
                          <th className="p-2">Updated</th>
                          <th className="p-2">Actions</th>
                        </tr>
                      </thead>
                      <tbody>
                        {filteredCustomProfiles.map(p => renderCustomProfileRow(p))}
                      </tbody>
                    </table>
                    </div>
                  </>
                )}
              </>
            )}

            {/* Regular Profiles Tabs Content (Machines, Filaments, Processes) */}
            {activeTab !== 'custom' && (
              <>
            {!isLoading && getFilteredCount() > 0 && (
              <div className="overflow-x-auto">
                <table className="min-w-full text-sm">
                  <thead>
                    <tr className="bg-pf-bg-1 text-left">
                      <th className="p-2">Name</th>
                      <th className="p-2">Engine</th>
                      <th className="p-2">Material/Manufacturer</th>
                      <th className="p-2">Quality/Type</th>
                      <th className="p-2">Layer/Nozzle</th>
                      <th className="p-2">Infill</th>
                      <th className="p-2">Flags</th>
                    </tr>
                  </thead>
                  <tbody>
                    {pagedProfiles.map(p => renderProfileRow(p))}
                  </tbody>
                </table>
                <div className="flex items-center justify-between mt-3">
                  <p className="text-sm text-pf-text-muted">
                    Page {safePageNumber} of {totalPages}
                  </p>
                  <div className="flex items-center gap-2">
                    <Button
                      type="button"
                      variant="secondary"
                      size="sm"
                      disabled={safePageNumber <= 1}
                      onClick={() => setPageNumber((n) => Math.max(1, n - 1))}
                    >
                      Previous
                    </Button>
                    <Button
                      type="button"
                      variant="secondary"
                      size="sm"
                      disabled={safePageNumber >= totalPages}
                      onClick={() => setPageNumber((n) => Math.min(totalPages, n + 1))}
                    >
                      Next
                    </Button>
                  </div>
                </div>
              </div>
            )}
              </>
            )}
          </div>
        </div>
      </div>

      {/* Import Profile Modal */}
      <Modal
        isOpen={isImportModalOpen}
        onClose={() => setIsImportModalOpen(false)}
        title="Import Profile"
        isDisabled={importMutation.isPending}
        footer={
          <div className="flex justify-end gap-3 w-full">
            <Button
              variant="secondary"
              onClick={() => setIsImportModalOpen(false)}
              disabled={importMutation.isPending}
            >
              Cancel
            </Button>
            <Button
              form="import-profile-form"
              type="submit"
              loading={importMutation.isPending}
              variant="primary"
            >
              Import Profile
            </Button>
          </div>
        }
      >
        <form id="import-profile-form" onSubmit={onImport} className="space-y-4">
          <p className="text-sm text-pf-text-secondary mb-4">
            Paste raw slicer profile JSON exported from your slicer application. This is for advanced users only.
          </p>

          <FormField label="Raw Profile JSON" required helper="Paste raw slicer profile JSON exported from your slicer.">
            <Textarea
              placeholder={'{\n  "layer_height": 0.2, ...\n}'}
              value={rawJson}
              onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) => setRawJson(e.target.value)}
              rows={12}
            />
          </FormField>
          <FormField label="Name" helper="Optional; derived automatically if left blank.">
            <Input
              type="text"
              placeholder="Profile name"
              value={name}
              onChange={e => setName(e.target.value)}
            />
          </FormField>
          <FormField label="Description">
            <Input
              type="text"
              placeholder="Description"
              value={description}
              onChange={e => setDescription(e.target.value)}
            />
          </FormField>
          <FormField label="Slicer Engine" required>
            <Select
              aria-label="Slicer engine"
              value={slicerType}
              onChange={e => setSlicerType(e.target.value)}
            >
              {slicerNames.map(s => <option key={s}>{s}</option>)}
            </Select>
          </FormField>
          <div className="flex flex-col gap-2 text-sm">
            <label className="inline-flex items-center gap-2">
              <Checkbox checked={allowSystemOverride} onChange={e => setAllowSystemOverride(e.target.checked)} />
              <span>Allow system override</span>
            </label>
            <label className="inline-flex items-center gap-2">
              <Checkbox checked={setDefault} onChange={e => setSetDefault(e.target.checked)} />
              <span>Set as default after import</span>
            </label>
            <label className="inline-flex items-center gap-2">
              <Checkbox checked={isPublic} onChange={e => setIsPublic(e.target.checked)} />
              <span>Public (visible to other users)</span>
            </label>
          </div>
          {importError && <Alert type="error">{importError}</Alert>}
        </form>
      </Modal>

      {/* Upload Custom Profile Modal */}
      <Modal
        isOpen={isUploadModalOpen}
        onClose={() => {
          setIsUploadModalOpen(false);
          setUploadError(null);
        }}
        title="Upload Custom Profile"
        isDisabled={uploadProfileMutation.isPending}
        footer={
          <div className="flex justify-end gap-3 w-full">
            <Button
              variant="secondary"
              onClick={() => setIsUploadModalOpen(false)}
              disabled={uploadProfileMutation.isPending}
            >
              Cancel
            </Button>
            <Button
              form="upload-profile-form"
              type="submit"
              loading={uploadProfileMutation.isPending}
              variant="primary"
            >
              Upload Profile
            </Button>
          </div>
        }
      >
        <form id="upload-profile-form" onSubmit={onUploadCustomProfile} className="space-y-4">
          <p className="text-sm text-pf-text-secondary mb-4">
            Upload a custom profile from raw JSON. This creates a user-owned profile that you can edit or delete.
          </p>

          <FormField label="Profile Type" required>
            <Select
              aria-label="Profile type"
              value={uploadProfileType}
              onChange={e => setUploadProfileType(e.target.value as 'machine' | 'filament' | 'process')}
            >
              <option value="process">Process (Quality/Speed)</option>
              <option value="filament">Filament (Material)</option>
              <option value="machine">Machine (Printer)</option>
            </Select>
          </FormField>

          {uploadProfileType !== 'filament' && (
            <FormField
              label="Printer Model"
              helper="Optional. If left on Auto-detect, the server will try to match the model from the raw JSON via slicer aliases."
            >
              <Select
                aria-label="Printer model association"
                value={uploadPrinterModelId}
                onChange={e => setUploadPrinterModelId(e.target.value)}
              >
                <option value="">Auto-detect from JSON</option>
                {sortedCatalogPrinterModels.map(model => (
                  <option key={model.id} value={model.id}>{model.name}</option>
                ))}
              </Select>
            </FormField>
          )}

          {uploadProfileType === 'filament' && (
            <FormField
              label="Compatible Printers"
              helper="Optional. Hold Ctrl/Cmd to select multiple. Leave empty to auto-detect from the raw JSON's compatible_printers array."
            >
              <Select
                multiple
                aria-label="Compatible printer names"
                size={6}
                value={uploadCompatiblePrinters}
                onChange={e => setUploadCompatiblePrinters(Array.from(e.target.selectedOptions, o => o.value))}
              >
                {sortedCatalogPrinterModels.map(model => (
                  <option key={model.id} value={model.name}>{model.name}</option>
                ))}
              </Select>
            </FormField>
          )}

          <FormField label="Raw Profile JSON" required helper="Paste OrcaSlicer profile JSON.">
            <Textarea
              placeholder={'{\n  "layer_height": 0.2,\n  "infill_density": "15%",\n  ...\n}'}
              value={uploadRawJson}
              onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) => setUploadRawJson(e.target.value)}
              rows={10}
            />
          </FormField>

          <FormField label="Name" helper="Optional; derived automatically from JSON if left blank.">
            <Input
              type="text"
              placeholder="Custom profile name"
              value={uploadName}
              onChange={e => setUploadName(e.target.value)}
            />
          </FormField>

          {uploadError && <Alert type="error">{uploadError}</Alert>}
        </form>
      </Modal>

      {/* Profile Reseed Status Modal */}
      <Modal
        isOpen={reseedModalOpen}
        onClose={() => {
          if (reseedStatus !== 'loading') {
            setReseedModalOpen(false);
          }
        }}
        title="Loading System Profiles"
        isDisabled={reseedStatus === 'loading'}
        footer={
          reseedStatus !== 'loading' && (
            <Button
              variant="primary"
              onClick={() => setReseedModalOpen(false)}
            >
              {reseedStatus === 'success' ? 'Done' : 'Close'}
            </Button>
          )
        }
        width="max-w-md"
      >
        <div className="flex flex-col items-center gap-4 py-4">
          {/* Status Icon */}
          {reseedStatus === 'loading' && (
            <div className="animate-spin">
              <TimerSandIcon className="h-12 w-12 text-pf-accent" />
            </div>
          )}
          {reseedStatus === 'success' && (
            <CheckCircleIcon className="h-12 w-12 text-pf-success" />
          )}
          {reseedStatus === 'error' && (
            <AlertCircleIcon className="h-12 w-12 text-pf-error" />
          )}

          {/* Status Message */}
          <p className={`text-center text-sm ${reseedStatus === 'loading' ? 'text-pf-text-secondary' :
            reseedStatus === 'success' ? 'text-pf-success' :
              'text-pf-error'
            }`}>
            {reseedMessage}
          </p>
        </div>
      </Modal>

      {/* Edit Custom Profile Modal */}
      <Modal
        isOpen={isEditModalOpen}
        onClose={() => {
          setIsEditModalOpen(false);
          setEditingProfile(null);
          setEditError(null);
        }}
        title="Edit Custom Profile"
        isDisabled={updateProfileMutation.isPending}
        footer={
          <div className="flex justify-end gap-3 w-full">
            <Button
              variant="secondary"
              onClick={() => setIsEditModalOpen(false)}
              disabled={updateProfileMutation.isPending}
            >
              Cancel
            </Button>
            <Button
              form="edit-profile-form"
              type="submit"
              loading={updateProfileMutation.isPending}
              variant="primary"
            >
              Save Changes
            </Button>
          </div>
        }
      >
        <form id="edit-profile-form" onSubmit={onEditProfile} className="space-y-4">
          {editingProfile && (
            <>
              <p className="text-sm text-pf-text-secondary">
                Editing <span className="font-medium">{editingProfile.profileType}</span> profile
              </p>

              <FormField label="Name" required>
                <Input
                  type="text"
                  placeholder="Profile name"
                  value={editName}
                  onChange={e => setEditName(e.target.value)}
                />
              </FormField>

              <FormField label="Description">
                <Textarea
                  placeholder="Optional description"
                  value={editDescription}
                  onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) => setEditDescription(e.target.value)}
                  rows={3}
                />
              </FormField>

              {editingProfile.profileType !== 'filament' && (
                <FormField
                  label="Printer Model"
                  helper="Repair the catalog Printer Model association for this custom profile. Choose 'No association' to detach."
                >
                  <Select
                    aria-label="Printer model association"
                    value={editPrinterModelId}
                    onChange={e => setEditPrinterModelId(e.target.value)}
                  >
                    <option value="">No association</option>
                    {sortedCatalogPrinterModels.map(model => (
                      <option key={model.id} value={model.id}>{model.name}</option>
                    ))}
                  </Select>
                </FormField>
              )}

              {editingProfile.profileType === 'filament' && (
                <FormField
                  label="Compatible Printers"
                  helper="Edit which printers this filament profile applies to. Hold Ctrl/Cmd to select multiple. Clear all selections to detach."
                >
                  <Select
                    multiple
                    aria-label="Compatible printer names"
                    size={6}
                    value={editCompatiblePrinters}
                    onChange={e => setEditCompatiblePrinters(Array.from(e.target.selectedOptions, o => o.value))}
                  >
                    {sortedCatalogPrinterModels.map(model => (
                      <option key={model.id} value={model.name}>{model.name}</option>
                    ))}
                  </Select>
                </FormField>
              )}

              {editError && <Alert type="error">{editError}</Alert>}
            </>
          )}
        </form>
      </Modal>
    </PageTemplate>
  );
};
