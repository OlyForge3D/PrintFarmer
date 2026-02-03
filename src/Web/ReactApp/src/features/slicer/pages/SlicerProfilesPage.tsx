import React, { useState, useMemo, useEffect } from 'react';
import { useNavigate } from 'react-router';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { SelectableRow } from '@/common/components/Table/SelectableRow';
import * as signalR from '@microsoft/signalr';
import { getHubUrl } from '@/common/utils/apiUrlHelpers';
import {
  slicerProfilesService,
  SlicerProfileListItem,
  HierarchicalProfilesResponse,
  PrinterModelProfilesDto,
  ProcessProfileListItem,
  FilamentProfileListItem,
  MachineProfileListItem,
  ImportSlicerProfileRequest,
  SlicerProfileExtended,
  SlicerProfileExportDto,
  BulkDeleteResultDto,
  CloneSingleProfileRequest,
  CloneSingleProfileResponse,
  UploadProfileRequest,
  CustomProfile,
  CustomProfilesListResponse,
  UpdateCustomProfileRequest
} from '@/services/slicerProfilesService';
import { orcaProfilesService } from '@/features/slicer/orca';
import { slicerRegistry } from '@/services/slicerRegistry';
import { FilterIcon, GearIcon, UploadIcon, SearchIcon, CheckCircleIcon, AlertCircleIcon, TimerSandIcon, CopyIcon } from '@/common/components/icons/MdiIcons';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button } from '@/common/components/ui/Button';
import { Alert } from '@/common/components/ui/Alert';
import { FormField } from '@/common/components/ui/FormField';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { Textarea } from '@/common/components/ui/Textarea';
import { Modal } from '@/common/components/modals/Modal';

export const SlicerProfilesPage: React.FC = () => {
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
  const [uploadError, setUploadError] = useState<string | null>(null);

  // Edit custom profile modal state
  const [isEditModalOpen, setIsEditModalOpen] = useState(false);
  const [editingProfile, setEditingProfile] = useState<CustomProfile | null>(null);
  const [editName, setEditName] = useState('');
  const [editDescription, setEditDescription] = useState('');
  const [editError, setEditError] = useState<string | null>(null);

  // Tab state - 'machines', 'filaments', 'processes', 'custom'
  const [activeTab, setActiveTab] = useState<'machines' | 'filaments' | 'processes' | 'custom'>('machines');

  // Filtering and search state
  const [searchQuery, setSearchQuery] = useState('');
  const [filterManufacturer, setFilterManufacturer] = useState<string>('all');
  const [selectedMachineProfileId, setSelectedMachineProfileId] = useState<string>('');
  const [selectedFilamentProfileId, setSelectedFilamentProfileId] = useState<string>('');
  const [selectedProcessProfileId, setSelectedProcessProfileId] = useState<string>('');
  const [filterEngine, setFilterEngine] = useState<string>('all');
  const [filterSource, setFilterSource] = useState<string>('all');
  const [showFilters, setShowFilters] = useState(false);

  // Paging state
  const [pageSize, setPageSize] = useState<number>(25);
  const [pageNumber, setPageNumber] = useState<number>(1);

  // UI state
  const [importError, setImportError] = useState<string | null>(null);
  const [exportingId, setExportingId] = useState<string | null>(null);
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

  // Main hierarchy query - loads all profiles for browsing
  const { data: profilesData, isLoading, error } = useQuery<HierarchicalProfilesResponse, Error>({
    queryKey: ['slicerProfilesHierarchy'],
    queryFn: async () => slicerProfilesService.listHierarchical(),
    staleTime: 10_000
  });

  // Filtered query - loads profiles filtered by selected machine (for CompatiblePrinters filtering)
  const { data: filteredProfilesData } = useQuery<HierarchicalProfilesResponse, Error>({
    queryKey: ['slicerProfilesHierarchyFiltered', selectedMachineProfileId],
    queryFn: async () => slicerProfilesService.listHierarchical(selectedMachineProfileId),
    enabled: !!selectedMachineProfileId,
    staleTime: 10_000
  });

  // Custom profiles query - loads user-owned custom profiles
  const { data: customProfilesData, isLoading: customProfilesLoading } = useQuery<CustomProfilesListResponse, Error>({
    queryKey: ['customProfiles'],
    queryFn: async () => slicerProfilesService.listCustomProfiles(),
    staleTime: 10_000
  });

  const allMachineProfiles = useMemo<MachineProfileListItem[]>(() => {
    // Try hierarchical data first, fallback to flat machineProfiles
    if (profilesData?.byHierarchy && Object.keys(profilesData.byHierarchy).length > 0) {
      const out: MachineProfileListItem[] = [];
      for (const mfgData of Object.values(profilesData.byHierarchy)) {
        for (const modelData of Object.values(mfgData.models)) {
          out.push(...(modelData.machineProfiles ?? []));
        }
      }
      return out;
    }
    // Fallback: use flat machineProfiles grouped by manufacturer
    if (profilesData?.machineProfiles) {
      const out: MachineProfileListItem[] = [];
      for (const profiles of Object.values(profilesData.machineProfiles)) {
        out.push(...profiles);
      }
      return out;
    }
    return [];
  }, [profilesData]);

  const allFilamentProfiles = useMemo<FilamentProfileListItem[]>(() => {
    // Try hierarchical data first, fallback to flat filamentProfiles
    if (profilesData?.byHierarchy && Object.keys(profilesData.byHierarchy).length > 0) {
      const out: FilamentProfileListItem[] = [];
      for (const mfgData of Object.values(profilesData.byHierarchy)) {
        for (const modelData of Object.values(mfgData.models)) {
          out.push(...(modelData.filamentProfiles ?? []));
        }
      }
      return out;
    }
    // Fallback: use flat filamentProfiles grouped by key
    if (profilesData?.filamentProfiles) {
      const out: FilamentProfileListItem[] = [];
      for (const profiles of Object.values(profilesData.filamentProfiles)) {
        out.push(...profiles);
      }
      return out;
    }
    return [];
  }, [profilesData]);

  const allProcessProfiles = useMemo<ProcessProfileListItem[]>(() => {
    // Try hierarchical data first, fallback to flat processProfiles
    if (profilesData?.byHierarchy && Object.keys(profilesData.byHierarchy).length > 0) {
      const out: ProcessProfileListItem[] = [];
      for (const mfgData of Object.values(profilesData.byHierarchy)) {
        for (const modelData of Object.values(mfgData.models)) {
          out.push(...(modelData.processProfiles ?? []));
        }
      }
      return out;
    }
    // Fallback: use flat processProfiles grouped by key
    if (profilesData?.processProfiles) {
      const out: ProcessProfileListItem[] = [];
      for (const profiles of Object.values(profilesData.processProfiles)) {
        out.push(...profiles);
      }
      return out;
    }
    return [];
  }, [profilesData]);

  type MachineProfileContext = {
    manufacturer: string;
    modelName: string;
    modelData: PrinterModelProfilesDto;
  };

  const machineContextById = useMemo<Map<string, MachineProfileContext>>(() => {
    const map = new Map<string, MachineProfileContext>();
    if (!profilesData?.byHierarchy) return map;

    for (const [manufacturer, mfgData] of Object.entries(profilesData.byHierarchy)) {
      for (const modelData of Object.values(mfgData.models)) {
        for (const machine of modelData.machineProfiles ?? []) {
          map.set(machine.id, {
            manufacturer,
            modelName: modelData.name,
            modelData,
          });
        }
      }
    }
    return map;
  }, [profilesData]);

  const selectedMachineContext = useMemo<MachineProfileContext | undefined>(() => {
    if (!selectedMachineProfileId) return undefined;
    return machineContextById.get(selectedMachineProfileId);
  }, [machineContextById, selectedMachineProfileId]);

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
      qc.invalidateQueries({ queryKey: ['slicerProfilesHierarchy'] });
    },
    onError: (err) => {
      setImportError(err.message);
    }
  });

  const setDefaultMutation = useMutation<void, Error, string>({
    mutationFn: async (id) => slicerProfilesService.setDefault(id),
    onSuccess: () => {
      setMessage('Default profile updated.');
      qc.invalidateQueries({ queryKey: ['slicerProfilesHierarchy'] });
    },
    onError: (err) => setMessage(`Failed to set default: ${err.message}`)
  });

  const bulkDeleteMutation = useMutation<BulkDeleteResultDto, Error, string[]>({
    mutationFn: async (ids) => slicerProfilesService.bulkDelete(ids),
    onSuccess: (result) => {
      setMessage(`Deleted ${result.totalDeleted} profiles (${result.machineProfilesDeleted} machine, ${result.processProfilesDeleted} process, ${result.filamentProfilesDeleted} filament)${result.notFound > 0 ? ` - ${result.notFound} not found` : ''}`);
      setSelectedProfileIds(new Set());
      qc.invalidateQueries({ queryKey: ['slicerProfilesHierarchy'] });
      qc.invalidateQueries({ queryKey: ['customProfiles'] });
    },
    onError: (err) => setMessage(`Failed to delete profiles: ${err.message}`)
  });

  // Clone profile mutation - creates a custom copy of a system profile
  const cloneProfileMutation = useMutation<CloneSingleProfileResponse, Error, CloneSingleProfileRequest>({
    mutationFn: async (request) => slicerProfilesService.cloneProfile(request),
    onSuccess: (result) => {
      setMessage(`Created custom profile: ${result.name}`);
      qc.invalidateQueries({ queryKey: ['slicerProfilesHierarchy'] });
      qc.invalidateQueries({ queryKey: ['customProfiles'] });
    },
    onError: (err) => setMessage(`Failed to clone profile: ${err.message}`)
  });

  // Upload custom profile mutation - creates a new custom profile from raw JSON
  const uploadProfileMutation = useMutation<CustomProfile, Error, UploadProfileRequest>({
    mutationFn: async (request) => slicerProfilesService.uploadProfile(request),
    onSuccess: (result) => {
      setMessage(`Created custom profile: ${result.name}`);
      setUploadRawJson('');
      setUploadName('');
      setUploadError(null);
      setIsUploadModalOpen(false);
      qc.invalidateQueries({ queryKey: ['slicerProfilesHierarchy'] });
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
      qc.invalidateQueries({ queryKey: ['slicerProfilesHierarchy'] });
      qc.invalidateQueries({ queryKey: ['customProfiles'] });
    },
    onError: (err) => setEditError(err.message)
  });

  // Delete single custom profile mutation
  const deleteProfileMutation = useMutation<void, Error, string>({
    mutationFn: async (id) => slicerProfilesService.deleteCustomProfile(id),
    onSuccess: () => {
      setMessage('Profile deleted');
      qc.invalidateQueries({ queryKey: ['slicerProfilesHierarchy'] });
      qc.invalidateQueries({ queryKey: ['customProfiles'] });
    },
    onError: (err) => setMessage(`Failed to delete profile: ${err.message}`)
  });

  // Helper to open edit modal for a custom profile
  const openEditModal = (profile: CustomProfile) => {
    setEditingProfile(profile);
    setEditName(profile.name);
    setEditDescription(profile.description || '');
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
    updateProfileMutation.mutate({
      id: editingProfile.id,
      request: {
        name: editName,
        description: editDescription || undefined
      }
    });
  };

  const onUploadCustomProfile = (e: React.FormEvent) => {
    e.preventDefault();
    if (!uploadRawJson.trim()) {
      setUploadError('Raw JSON is required');
      return;
    }
    uploadProfileMutation.mutate({
      rawJson: uploadRawJson,
      profileType: uploadProfileType,
      name: uploadName || undefined
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

  const exportProfile = async (id: string) => {
    setExportingId(id);
    try {
      const data: SlicerProfileExportDto = await slicerProfilesService.exportProfile(id);
      const blob = new Blob([data.rawJson], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `${data.name.replace(/\s+/g, '_')}_${data.hash.substring(0, 8)}.json`;
      a.click();
      URL.revokeObjectURL(url);
      setMessage('Profile exported.');
    } catch (e) {
      setMessage(e instanceof Error ? e.message : 'Export failed');
    } finally {
      setExportingId(null);
    }
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

  // Apply filters to machine profiles
  const filteredMachineProfiles = useMemo(() => {
    return allMachineProfiles.filter((p) => {
      const ctx = machineContextById.get(p.id);
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

      // Engine filter
      if (filterEngine !== 'all' && p.slicerType !== filterEngine) return false;

      // Source filter
      if (filterSource !== 'all' && filterSource !== '') {
        if (filterSource === 'default' && !p.isDefault) return false;
        if (filterSource === 'system' && !p.isSystem) return false;
        if (filterSource === 'public' && !p.isPublic) return false;
        if (filterSource === 'imported' && p.isSystem) return false;
      }

      return true;
    });
  }, [
    allMachineProfiles,
    filterEngine,
    filterManufacturer,
    filterSource,
    machineContextById,
    searchQuery,
  ]);

  // Filament/process profiles are shown only after selecting a machine profile
  // Use filteredProfilesData when available (contains CompatiblePrinters-filtered results)
  const filteredFilamentProfiles = useMemo(() => {
    if (!selectedMachineContext) return [];
    
    // Use filtered data from API when available (CompatiblePrinters filtering)
    let sourceProfiles = selectedMachineContext.modelData.filamentProfiles ?? [];
    if (filteredProfilesData?.filamentProfiles) {
      // Flatten filtered profiles from all manufacturers
      const filtered: FilamentProfileListItem[] = [];
      for (const profiles of Object.values(filteredProfilesData.filamentProfiles)) {
        filtered.push(...profiles);
      }
      if (filtered.length > 0) {
        sourceProfiles = filtered;
      }
    }
    
    return sourceProfiles.filter((p) => {
      if (searchQuery) {
        const query = searchQuery.toLowerCase();
        if (!p.name.toLowerCase().includes(query) && !p.material.toLowerCase().includes(query)) {
          return false;
        }
      }
      if (filterEngine !== 'all' && p.slicerType !== filterEngine) return false;

      if (selectedFilamentProfileId && p.id !== selectedFilamentProfileId) return false;

      if (filterSource !== 'all' && filterSource !== '') {
        if (filterSource === 'default' && !p.isDefault) return false;
        if (filterSource === 'system' && !p.isSystem) return false;
        if (filterSource === 'public' && !p.isPublic) return false;
        if (filterSource === 'imported' && p.isSystem) return false;
      }
      return true;
    });
  }, [filterEngine, filterSource, filteredProfilesData, searchQuery, selectedFilamentProfileId, selectedMachineContext]);

  const filteredProcessProfiles = useMemo(() => {
    if (!selectedMachineContext) return [];
    
    // Use filtered data from API when available (CompatiblePrinters filtering)
    let sourceProfiles = selectedMachineContext.modelData.processProfiles ?? [];
    if (filteredProfilesData?.processProfiles) {
      // Flatten filtered profiles from all manufacturers
      const filtered: ProcessProfileListItem[] = [];
      for (const profiles of Object.values(filteredProfilesData.processProfiles)) {
        filtered.push(...profiles);
      }
      if (filtered.length > 0) {
        sourceProfiles = filtered;
      }
    }
    
    return sourceProfiles.filter((p) => {
      if (searchQuery) {
        const query = searchQuery.toLowerCase();
        if (!p.name.toLowerCase().includes(query) && !p.quality.toLowerCase().includes(query)) {
          return false;
        }
      }
      if (filterEngine !== 'all' && p.slicerType !== filterEngine) return false;

      if (selectedProcessProfileId && p.id !== selectedProcessProfileId) return false;

      if (filterSource !== 'all' && filterSource !== '') {
        if (filterSource === 'default' && !p.isDefault) return false;
        if (filterSource === 'system' && !p.isSystem) return false;
        if (filterSource === 'public' && !p.isPublic) return false;
        if (filterSource === 'imported' && p.isSystem) return false;
      }
      return true;
    });
  }, [filterEngine, filterSource, filteredProfilesData, searchQuery, selectedMachineContext, selectedProcessProfileId]);

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

  const renderProfileRow = (p: SlicerProfileListItem) => (
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
      <td className="p-2">{p.slicerType}</td>
      <td className="p-2">{p.profileType === 'filament' ? (p as FilamentProfileListItem).material : p.profileType === 'machine' ? (p as MachineProfileListItem).manufacturer : '-'}</td>
      <td className="p-2">{p.profileType === 'process' ? (p as ProcessProfileListItem).quality : '-'}</td>
      <td className="p-2">{p.profileType === 'process' ? (p as ProcessProfileListItem).layerHeight.toFixed(2) + 'mm' : (p.profileType === 'machine' && (p as MachineProfileListItem).nozzleDiameter) ? (p as MachineProfileListItem).nozzleDiameter + 'mm' : '-'}</td>
      <td className="p-2">{p.profileType === 'process' ? (p as ProcessProfileListItem).infillPercentage + '%' : '-'}</td>
      <td className="p-2">
        <div className="flex flex-col text-xs gap-1">
          {p.isDefault && <span className="px-2 py-0.5 bg-pf-accent-bg text-pf-text-primary rounded-sm">Default</span>}
          {p.isSystem && <span className="px-2 py-0.5 bg-pf-bg-2 text-pf-text-primary rounded-sm">System</span>}
          {p.isPublic && <span className="px-2 py-0.5 bg-pf-success-bg text-pf-text-primary rounded-sm">Public</span>}
        </div>
      </td>
      <td className="p-2">
        <div className="flex gap-2">
          <Button
            onClick={() => setDefaultMutation.mutate(p.id)}
            loading={setDefaultMutation.isPending}
            size="sm"
            variant="primary"
          >Set Default</Button>
          <Button
            onClick={() => exportProfile(p.id)}
            loading={exportingId === p.id}
            size="sm"
            variant="secondary"
          >{exportingId === p.id ? 'Exporting...' : 'Export'}</Button>
          <Button
            onClick={() => cloneProfileMutation.mutate({
              sourceProfileId: p.id,
              profileType: p.profileType
            })}
            loading={cloneProfileMutation.isPending}
            size="sm"
            variant="secondary"
            title="Clone to My Profiles"
          >
            <CopyIcon className="w-4 h-4" />
          </Button>
        </div>
      </td>
    </SelectableRow>
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
  const getTotalCount = () => {
    return allMachineProfiles.length + allFilamentProfiles.length + allProcessProfiles.length + (customProfilesData?.profiles?.length ?? 0);
  };

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
    return filteredMachineProfiles
      .map(m => m)
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [filterManufacturer, filteredMachineProfiles]);

  // Dropdown options use CompatiblePrinters-filtered data when available
  const filamentOptions = useMemo(() => {
    if (!selectedMachineContext) return [];
    
    // Use filtered data from API when available (CompatiblePrinters filtering)
    let sourceProfiles = selectedMachineContext.modelData.filamentProfiles ?? [];
    if (filteredProfilesData?.filamentProfiles) {
      const filtered: FilamentProfileListItem[] = [];
      for (const profiles of Object.values(filteredProfilesData.filamentProfiles)) {
        filtered.push(...profiles);
      }
      if (filtered.length > 0) {
        sourceProfiles = filtered;
      }
    }
    
    return sourceProfiles
      .slice()
      .sort((a, b) => (a.material + a.name).localeCompare(b.material + b.name));
  }, [filteredProfilesData, selectedMachineContext]);

  const processOptions = useMemo(() => {
    if (!selectedMachineContext) return [];
    
    // Use filtered data from API when available (CompatiblePrinters filtering)
    let sourceProfiles = selectedMachineContext.modelData.processProfiles ?? [];
    if (filteredProfilesData?.processProfiles) {
      const filtered: ProcessProfileListItem[] = [];
      for (const profiles of Object.values(filteredProfilesData.processProfiles)) {
        filtered.push(...profiles);
      }
      if (filtered.length > 0) {
        sourceProfiles = filtered;
      }
    }
    
    return sourceProfiles
      .slice()
      .sort((a, b) => (a.quality + a.name).localeCompare(b.quality + b.name));
  }, [filteredProfilesData, selectedMachineContext]);

  const visibleProfiles = useMemo<SlicerProfileListItem[]>(() => {
    if (activeTab === 'machines') return filteredMachineProfiles;
    if (activeTab === 'filaments') return filteredFilamentProfiles;
    if (activeTab === 'processes') return filteredProcessProfiles;
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

  // Bulk selection handlers (must be after pagedProfiles is defined)
  const handleSelectAll = () => {
    if (selectedProfileIds.size === pagedProfiles.length && pagedProfiles.every(p => selectedProfileIds.has(p.id))) {
      // Deselect all on current page
      setSelectedProfileIds(new Set());
    } else {
      // Select all on current page
      setSelectedProfileIds(new Set(pagedProfiles.map(p => p.id)));
    }
  };

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
  }, [activeTab, filterEngine, filterManufacturer, filterSource, pageSize, searchQuery, selectedMachineProfileId, selectedFilamentProfileId, selectedProcessProfileId]);

  React.useEffect(() => {
    // When manufacturer changes, clear dependent selections
    setSelectedMachineProfileId('');
    setSelectedFilamentProfileId('');
    setSelectedProcessProfileId('');
  }, [filterManufacturer]);

  React.useEffect(() => {
    // When machine model changes, clear dependent selections
    setSelectedFilamentProfileId('');
    setSelectedProcessProfileId('');
  }, [selectedMachineProfileId]);

  React.useEffect(() => {
    if (!selectedMachineProfileId) return;
    const stillVisible = filteredMachineProfiles.some(m => m.id === selectedMachineProfileId);
    if (!stillVisible) {
      setSelectedMachineProfileId('');
    }
  }, [filteredMachineProfiles, selectedMachineProfileId]);

  // SignalR event listener for profile import progress
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
          qc.invalidateQueries({ queryKey: ['slicerProfilesHierarchy'] });
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
        hubConnection.stop();
      };
    } catch {
      return;
    }
  }, [reseedModalOpen, qc]);

  return (
    <PageTemplate
      title="Slicer Profiles"
      subtitle="Manage imported slicer profiles (OrcaSlicer / PrusaSlicer)"
      icon={GearIcon}
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
                <input
                  type="text"
                  placeholder="Search profiles by name, material, or manufacturer..."
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  className="w-full pl-10 pr-4 py-2 bg-pf-background border border-pf-border rounded-lg focus:ring-2 focus:ring-pf-primary focus:border-transparent"
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
            <div className="grid grid-cols-1 md:grid-cols-4 gap-3 p-3 bg-pf-background rounded-lg">
              <div>
                <label className="block text-sm font-medium mb-1">Manufacturer</label>
                <Select
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
                <label className="block text-sm font-medium mb-1">Machine Model</label>
                <Select
                  value={selectedMachineProfileId}
                  onChange={(e) => setSelectedMachineProfileId(e.target.value)}
                  aria-label="Select machine model"
                  disabled={filterManufacturer === 'all'}
                >
                  <option value="">Select a machine model</option>
                  {machineModelOptions.map(m => (
                    <option key={m.id} value={m.id}>{m.name}{m.nozzleDiameter ? ` (${m.nozzleDiameter}mm nozzle)` : ''}</option>
                  ))}
                </Select>
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">Process</label>
                <Select
                  value={selectedProcessProfileId}
                  onChange={(e) => setSelectedProcessProfileId(e.target.value)}
                  aria-label="Select process profile"
                  disabled={!selectedMachineProfileId}
                >
                  <option value="">Select a process profile</option>
                  {processOptions.map(p => (
                    <option key={p.id} value={p.id}>{p.quality}  {p.name}</option>
                  ))}
                </Select>
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">Filament</label>
                <Select
                  value={selectedFilamentProfileId}
                  onChange={(e) => setSelectedFilamentProfileId(e.target.value)}
                  aria-label="Select filament profile"
                  disabled={!selectedMachineProfileId}
                >
                  <option value="">Select a filament profile</option>
                  {filamentOptions.map(f => (
                    <option key={f.id} value={f.id}>{f.material}  {f.name}</option>
                  ))}
                </Select>
              </div>
            </div>

            {/* Advanced filters */}
            {showFilters && (
              <div className="grid grid-cols-1 md:grid-cols-2 gap-3 mt-3 p-3 bg-pf-background rounded-lg">
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
                <Button variant="secondary" size="sm" onClick={() => qc.invalidateQueries({ queryKey: ['slicerProfilesHierarchy'] })}>Refresh</Button>
              </div>
              {(searchQuery || filterManufacturer !== 'all' || filterEngine !== 'all' || filterSource !== 'all' || selectedMachineProfileId || selectedFilamentProfileId || selectedProcessProfileId) && (
                <Button
                  type="button"
                  onClick={() => {
                    setSearchQuery('');
                    setFilterManufacturer('all');
                    setFilterEngine('all');
                    setFilterSource('all');
                    setSelectedMachineProfileId('');
                    setSelectedFilamentProfileId('');
                    setSelectedProcessProfileId('');
                  }}
                  variant="subtle"
                  size="sm"
                >
                  Clear filters
                </Button>
              )}
            </div>

            {/* Profile Type Tabs */}
            <div className="flex gap-2 mt-4 border-b border-pf-border">
              <Button
                type="button"
                onClick={() => setActiveTab('machines')}
                variant="tab"
                size="sm"
                className={activeTab === 'machines' ? 'border-b-2 border-pf-primary text-pf-text-primary' : ''}
              >
                Machines ({filteredMachineProfiles.length})
              </Button>
              <Button
                type="button"
                onClick={() => setActiveTab('processes')}
                variant="tab"
                size="sm"
                className={activeTab === 'processes' ? 'border-b-2 border-pf-primary text-pf-text-primary' : ''}
              >
                Processes ({selectedMachineProfileId ? filteredProcessProfiles.length : 0})
              </Button>
              <Button
                type="button"
                onClick={() => setActiveTab('filaments')}
                variant="tab"
                size="sm"
                className={activeTab === 'filaments' ? 'border-b-2 border-pf-primary text-pf-text-primary' : ''}
              >
                Filaments ({selectedMachineProfileId ? filteredFilamentProfiles.length : 0})
              </Button>
              <Button
                type="button"
                onClick={() => setActiveTab('custom')}
                variant="tab"
                size="sm"
                className={activeTab === 'custom' ? 'border-b-2 border-pf-primary text-pf-text-primary' : ''}
              >
                My Profiles ({filteredCustomProfiles.length})
              </Button>
            </div>
          </div>

          {/* Profiles Table */}
          <div className="p-4">
            {error && <Alert type="error">{error.message}</Alert>}
            {isLoading && <div>Loading profiles...</div>}
            {!isLoading && getTotalCount() === 0 && <div className="text-pf-text-muted text-sm">No profiles imported yet.</div>}
            {!isLoading && getTotalCount() > 0 && getFilteredCount() === 0 && (
              <div className="text-pf-text-muted text-sm">No profiles match your filters.</div>
            )}
            {!isLoading && (activeTab === 'filaments' || activeTab === 'processes') && !selectedMachineProfileId && (
              <div className="text-pf-text-muted text-sm">Select a machine model to view filament and process profiles.</div>
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
                )}
              </>
            )}

            {/* Regular Profiles Tabs Content (Machines, Filaments, Processes) */}
            {activeTab !== 'custom' && (
              <>
            {/* Bulk actions bar */}
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
            {!isLoading && getFilteredCount() > 0 && (
              <div className="overflow-x-auto">
                <table className="min-w-full text-sm">
                  <thead>
                    <tr className="bg-pf-bg-1 text-left">
                      <th className="p-2 w-10">
                        <Checkbox
                          checked={pagedProfiles.length > 0 && pagedProfiles.every(p => selectedProfileIds.has(p.id))}
                          onChange={handleSelectAll}
                          label=""
                          aria-label="Select all profiles on this page"
                        />
                      </th>
                      <th className="p-2">Name</th>
                      <th className="p-2">Engine</th>
                      <th className="p-2">Material/Manufacturer</th>
                      <th className="p-2">Quality/Type</th>
                      <th className="p-2">Layer/Nozzle</th>
                      <th className="p-2">Infill</th>
                      <th className="p-2">Flags</th>
                      <th className="p-2">Actions</th>
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
              <TimerSandIcon className="h-12 w-12 text-pf-primary" />
            </div>
          )}
          {reseedStatus === 'success' && (
            <CheckCircleIcon className="h-12 w-12 text-green-500" />
          )}
          {reseedStatus === 'error' && (
            <AlertCircleIcon className="h-12 w-12 text-red-500" />
          )}

          {/* Status Message */}
          <p className={`text-center text-sm ${reseedStatus === 'loading' ? 'text-pf-text-secondary' :
            reseedStatus === 'success' ? 'text-green-600 dark:text-green-400' :
              'text-red-600 dark:text-red-400'
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

              {editError && <Alert type="error">{editError}</Alert>}
            </>
          )}
        </form>
      </Modal>
    </PageTemplate>
  );
};
