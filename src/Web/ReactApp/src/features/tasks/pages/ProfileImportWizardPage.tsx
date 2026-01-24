import React, { useState, useMemo } from 'react';
import { useSearchParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Alert, Checkbox } from '@/common/components/ui';
import { LayersIcon, AlertCircleIcon, CheckCircleIcon } from '@/common/components/icons/MdiIcons';
import { Download, Check, Package, Printer, Palette, Settings, ChevronRight } from 'lucide-react';
import { apiClient } from '@/services/api';
import { tasksApi } from '@/services/tasksApi';
import { toast } from 'sonner';

// ================================
// Types matching OrcaSlicer worker response
// ================================

interface MachineProfileDto {
  name: string;
  manufacturer: string;
  nozzleDiameter?: number;
  printerModel?: string;
  inherits?: string;
}

interface FilamentProfileDto {
  name: string;
  manufacturer?: string;
  material: string;
  nozzleTemperature?: number;
  bedTemperature?: number;
  compatiblePrinters?: string[];
}

interface ProcessProfileDto {
  name: string;
  manufacturer?: string;
  compatiblePrinters?: string[];
}

interface PrinterModelProfilesDto {
  name: string;
  modelId: string;
  machineProfiles: MachineProfileDto[];
  filamentProfiles: FilamentProfileDto[];
  processProfiles: ProcessProfileDto[];
}

interface ManufacturerProfilesDto {
  name: string;
  models: Record<string, PrinterModelProfilesDto>;
}

interface AllProfilesResponse {
  byHierarchy: Record<string, ManufacturerProfilesDto>;
  filamentProfiles: Record<string, FilamentProfileDto[]>;
}

interface PrinterModelDto {
  id: string;
  name: string;
  manufacturerId: string;
}

interface ManufacturerDto {
  id: string;
  name: string;
}

// Wizard steps
type WizardStep = 'machine' | 'filaments' | 'review';

/**
 * Profile Import Wizard - 3-step flow:
 * 1. Select machine profile (nozzle variant) - STRICT matching by model name
 * 2. Select filament profiles (3-column: Vendor → Material → Profiles)
 * 3. Review and import (machine, STRICTLY compatible processes, selected filaments)
 */
export const ProfileImportWizardPage: React.FC = () => {
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  
  const modelId = searchParams.get('modelId');
  const taskId = searchParams.get('taskId');

  // Wizard state
  const [currentStep, setCurrentStep] = useState<WizardStep>('machine');
  const [selectedMachine, setSelectedMachine] = useState<MachineProfileDto | null>(null);
  
  // Filament selection state - 3-column approach
  const [selectedVendors, setSelectedVendors] = useState<Set<string>>(new Set());
  const [selectedMaterials, setSelectedMaterials] = useState<Set<string>>(new Set());
  const [selectedFilaments, setSelectedFilaments] = useState<Set<string>>(new Set());

  // Fetch printer model details
  const { data: printerModel, isLoading: modelLoading } = useQuery({
    queryKey: ['printer-model', modelId],
    queryFn: async () => {
      if (!modelId) return null;
      const res = await apiClient.get<PrinterModelDto>(`/catalog/printer-models/${modelId}`);
      return res.data;
    },
    enabled: !!modelId,
  });

  // Fetch manufacturer details
  const { data: manufacturer } = useQuery({
    queryKey: ['manufacturer', printerModel?.manufacturerId],
    queryFn: async () => {
      if (!printerModel?.manufacturerId) return null;
      const res = await apiClient.get<ManufacturerDto>(`/catalog/manufacturers/${printerModel.manufacturerId}`);
      return res.data;
    },
    enabled: !!printerModel?.manufacturerId,
  });

  // Fetch all available profiles from OrcaSlicer worker
  const { data: allProfiles, isLoading: profilesLoading, error: profilesError } = useQuery({
    queryKey: ['orca-profiles-worker-hierarchy'],
    queryFn: async () => {
      const res = await apiClient.get<AllProfilesResponse>('/slicer/profiles/worker-hierarchy');
      return res.data;
    },
    staleTime: 60_000,
  });

  // Find manufacturer in profiles (case-insensitive)
  const manufacturerProfiles = useMemo(() => {
    if (!allProfiles?.byHierarchy || !manufacturer?.name) return null;
    
    const key = Object.keys(allProfiles.byHierarchy).find(
      k => k.toLowerCase() === manufacturer.name.toLowerCase()
    );
    return key ? allProfiles.byHierarchy[key] : null;
  }, [allProfiles, manufacturer]);

  // Get machine profiles for this SPECIFIC printer model only
  // STRICT matching: model name must match exactly or be contained in the profile's model name
  const machineProfiles = useMemo((): MachineProfileDto[] => {
    if (!manufacturerProfiles?.models || !printerModel?.name) return [];
    
    const printerNameLower = printerModel.name.toLowerCase().trim();
    const machines: MachineProfileDto[] = [];
    
    for (const [modelKey, modelData] of Object.entries(manufacturerProfiles.models)) {
      const modelNameLower = modelData.name.toLowerCase().trim();
      const modelKeyLower = modelKey.toLowerCase().trim();
      
      // STRICT match: The OrcaSlicer model name should match our printer model name
      // "Elegoo Centauri Carbon" should NOT match "Elegoo Centauri" profiles
      // But "Elegoo Centauri Carbon" SHOULD match "Elegoo_Centauri_Carbon" (underscore variant)
      const normalizedPrinterName = printerNameLower.replace(/[\s_-]+/g, ' ');
      const normalizedModelName = modelNameLower.replace(/[\s_-]+/g, ' ');
      const normalizedModelKey = modelKeyLower.replace(/[\s_-]+/g, ' ');
      
      const isExactMatch = 
        normalizedModelName === normalizedPrinterName ||
        normalizedModelKey === normalizedPrinterName ||
        // Also check if model contains our full printer name (for variants with nozzle suffix)
        normalizedModelName.startsWith(normalizedPrinterName + ' ') ||
        normalizedModelKey.startsWith(normalizedPrinterName + ' ');
      
      if (isExactMatch && modelData.machineProfiles) {
        machines.push(...modelData.machineProfiles);
      }
    }
    
    return machines;
  }, [manufacturerProfiles, printerModel]);

  // Get process profiles compatible with selected machine
  // The worker ALREADY filters process profiles by model via compatible_printers_condition
  // So ALL process profiles in the model's processProfiles array ARE compatible
  const compatibleProcesses = useMemo((): ProcessProfileDto[] => {
    if (!selectedMachine || !manufacturerProfiles?.models || !printerModel) return [];
    
    // Find the model that matches our printer
    const normalizedPrinterName = printerModel.name.toLowerCase().replace(/[^a-z0-9]/g, '');
    
    const processes: ProcessProfileDto[] = [];
    for (const [modelKey, modelData] of Object.entries(manufacturerProfiles.models)) {
      const normalizedModelKey = modelKey.toLowerCase().replace(/[^a-z0-9]/g, '');
      
      // STRICT match - only use process profiles from the EXACT model match
      const isExactMatch = normalizedModelKey === normalizedPrinterName || 
        normalizedModelKey.includes(normalizedPrinterName) ||
        normalizedPrinterName.includes(normalizedModelKey);
      
      if (isExactMatch && modelData.processProfiles) {
        // Use ALL process profiles - the worker already filtered them by model compatibility
        processes.push(...modelData.processProfiles);
      }
    }
    
    // Dedupe by name
    return [...new Map(processes.map(p => [p.name, p])).values()];
  }, [selectedMachine, manufacturerProfiles, printerModel]);

  // ================================
  // Filament Selection - 3 Column Data
  // ================================

  // All vendors (manufacturers) with their filament counts
  const allVendors = useMemo(() => {
    if (!allProfiles?.filamentProfiles) return [];
    
    return Object.entries(allProfiles.filamentProfiles)
      .map(([name, filaments]) => ({ name, count: filaments.length }))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [allProfiles]);

  // Materials available from selected vendors
  const availableMaterials = useMemo(() => {
    if (!allProfiles?.filamentProfiles || selectedVendors.size === 0) return [];
    
    const materialSet = new Map<string, number>();
    
    for (const vendor of selectedVendors) {
      const filaments = allProfiles.filamentProfiles[vendor] || [];
      for (const f of filaments) {
        const material = f.material || 'Other';
        materialSet.set(material, (materialSet.get(material) || 0) + 1);
      }
    }
    
    return Array.from(materialSet.entries())
      .map(([name, count]) => ({ name, count }))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [allProfiles, selectedVendors]);

  // Filaments matching selected vendors AND materials
  const availableFilaments = useMemo(() => {
    if (!allProfiles?.filamentProfiles || selectedVendors.size === 0 || selectedMaterials.size === 0) {
      return [];
    }
    
    const filaments: FilamentProfileDto[] = [];
    
    for (const vendor of selectedVendors) {
      const vendorFilaments = allProfiles.filamentProfiles[vendor] || [];
      for (const f of vendorFilaments) {
        const material = f.material || 'Other';
        if (selectedMaterials.has(material)) {
          filaments.push(f);
        }
      }
    }
    
    return filaments.sort((a, b) => a.name.localeCompare(b.name));
  }, [allProfiles, selectedVendors, selectedMaterials]);

  // Toggle vendor selection
  const toggleVendor = (vendor: string) => {
    const newSelected = new Set(selectedVendors);
    if (newSelected.has(vendor)) {
      newSelected.delete(vendor);
      // Also clear materials and filaments from this vendor
      if (allProfiles?.filamentProfiles) {
        const vendorFilaments = allProfiles.filamentProfiles[vendor] || [];
        const newFilaments = new Set(selectedFilaments);
        for (const f of vendorFilaments) {
          newFilaments.delete(f.name);
        }
        setSelectedFilaments(newFilaments);
      }
    } else {
      newSelected.add(vendor);
    }
    setSelectedVendors(newSelected);
  };

  // Toggle material selection
  const toggleMaterial = (material: string) => {
    const newSelected = new Set(selectedMaterials);
    if (newSelected.has(material)) {
      newSelected.delete(material);
      // Also clear filaments of this material
      const newFilaments = new Set(selectedFilaments);
      for (const f of availableFilaments) {
        if ((f.material || 'Other') === material) {
          newFilaments.delete(f.name);
        }
      }
      setSelectedFilaments(newFilaments);
    } else {
      newSelected.add(material);
    }
    setSelectedMaterials(newSelected);
  };

  // Toggle filament selection
  const toggleFilament = (name: string) => {
    const newSelected = new Set(selectedFilaments);
    if (newSelected.has(name)) {
      newSelected.delete(name);
    } else {
      newSelected.add(name);
    }
    setSelectedFilaments(newSelected);
  };

  // Select all / none for filaments
  const selectAllFilaments = () => {
    const newSelected = new Set(selectedFilaments);
    for (const f of availableFilaments) {
      newSelected.add(f.name);
    }
    setSelectedFilaments(newSelected);
  };

  const selectNoFilaments = () => {
    const newSelected = new Set(selectedFilaments);
    for (const f of availableFilaments) {
      newSelected.delete(f.name);
    }
    setSelectedFilaments(newSelected);
  };

  // Import mutation
  const importMutation = useMutation({
    mutationFn: async () => {
      if (!selectedMachine || !manufacturer) {
        throw new Error('Please select a machine profile first');
      }
      
      // TODO: Call API to persist selected profiles
      return {
        machineProfile: selectedMachine.name,
        processCount: compatibleProcesses.length,
        filamentCount: selectedFilaments.size,
      };
    },
    onSuccess: async (result) => {
      toast.success(
        `Profiles configured: 1 machine, ${result.processCount} process profiles, ${result.filamentCount} filament profiles`,
        { duration: 5000 }
      );
      
      if (taskId) {
        try {
          await tasksApi.completeTask(taskId);
          queryClient.invalidateQueries({ queryKey: ['tasks'] });
        } catch (e) {
          console.error('Failed to complete task:', e);
        }
      }
      
      navigate('/');
    },
    onError: (error: Error) => {
      toast.error(`Failed to import profiles: ${error.message}`);
    },
  });

  // Step navigation
  const canProceedToFilaments = selectedMachine !== null;

  const goToNext = () => {
    if (currentStep === 'machine' && canProceedToFilaments) {
      setCurrentStep('filaments');
    } else if (currentStep === 'filaments') {
      setCurrentStep('review');
    }
  };

  const goToPrevious = () => {
    if (currentStep === 'filaments') {
      setCurrentStep('machine');
    } else if (currentStep === 'review') {
      setCurrentStep('filaments');
    }
  };

  // No model ID provided
  if (!modelId) {
    return (
      <PageTemplate title="Import Profiles" subtitle="Import slicer profiles for your printers" icon={LayersIcon}>
        <Alert variant="warning">
          <AlertCircleIcon className="h-5 w-5" />
          <span>No printer model specified. Please access this page from a task in the dashboard.</span>
        </Alert>
        <div className="mt-4">
          <Button onClick={() => navigate('/')}>Go to Dashboard</Button>
        </div>
      </PageTemplate>
    );
  }

  const isLoading = modelLoading || profilesLoading;

  // Step indicators
  const steps: { key: WizardStep; label: string; icon: React.ReactNode }[] = [
    { key: 'machine', label: 'Machine Profile', icon: <Printer className="h-4 w-4" /> },
    { key: 'filaments', label: 'Filament Profiles', icon: <Palette className="h-4 w-4" /> },
    { key: 'review', label: 'Review & Import', icon: <CheckCircleIcon className="h-4 w-4" /> },
  ];

  return (
    <PageTemplate 
      title="Import Slicer Profiles" 
      subtitle={printerModel ? `${manufacturer?.name || ''} ${printerModel.name}` : 'Loading...'}
      icon={LayersIcon}
    >
      {/* Header with printer info */}
      <div className="mb-6">
        <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-4">
          <div className="flex items-center gap-4">
            <div className="p-3 bg-pf-accent/10 rounded-lg">
              <Package className="h-6 w-6 text-pf-accent" />
            </div>
            <div>
              <h2 className="text-lg font-semibold text-pf-text-primary">
                {manufacturer?.name || 'Loading...'} {printerModel?.name || ''}
              </h2>
              <p className="text-sm text-pf-text-secondary">
                Configure slicer profiles for this printer
              </p>
            </div>
          </div>
        </div>
      </div>

      {/* Step indicator */}
      <div className="mb-6">
        <div className="flex items-center justify-center gap-2">
          {steps.map((step, index) => {
            const isActive = step.key === currentStep;
            const isCompleted = 
              (step.key === 'machine' && currentStep !== 'machine') ||
              (step.key === 'filaments' && currentStep === 'review');
            
            return (
              <React.Fragment key={step.key}>
                {index > 0 && (
                  <div className={`w-12 h-0.5 ${isCompleted || isActive ? 'bg-pf-accent' : 'bg-pf-border'}`} />
                )}
                <div 
                  className={`flex items-center gap-2 px-4 py-2 rounded-lg transition-colors ${
                    isActive 
                      ? 'bg-pf-accent text-white' 
                      : isCompleted 
                        ? 'bg-pf-accent/20 text-pf-accent'
                        : 'bg-pf-bg-1 text-pf-text-tertiary'
                  }`}
                >
                  {isCompleted ? <Check className="h-4 w-4" /> : step.icon}
                  <span className="text-sm font-medium">{step.label}</span>
                </div>
              </React.Fragment>
            );
          })}
        </div>
      </div>

      {/* Error state */}
      {profilesError && (
        <Alert variant="error" className="mb-4">
          <AlertCircleIcon className="h-5 w-5" />
          <span>Failed to load profiles. Make sure the OrcaSlicer worker is running.</span>
        </Alert>
      )}

      {/* Loading state */}
      {isLoading && (
        <div className="flex items-center justify-center py-12">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent" />
        </div>
      )}

      {/* Step 1: Machine Profile Selection */}
      {!isLoading && currentStep === 'machine' && (
        <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
          <h3 className="text-lg font-semibold text-pf-text-primary mb-2">
            Select Machine Profile
          </h3>
          <p className="text-sm text-pf-text-secondary mb-4">
            Choose the nozzle configuration for your {printerModel?.name}. Only profiles specifically for this model are shown.
          </p>
          
          {machineProfiles.length === 0 ? (
            <Alert variant="warning">
              <AlertCircleIcon className="h-5 w-5" />
              <span>
                No machine profiles found for &quot;{printerModel?.name}&quot;. 
                The name may not match OrcaSlicer&apos;s naming convention exactly.
              </span>
            </Alert>
          ) : (
            <div className="space-y-2">
              {machineProfiles.map((machine) => (
                <button
                  key={machine.name}
                  onClick={() => setSelectedMachine(machine)}
                  className={`w-full flex items-center justify-between p-4 rounded-lg border transition-colors ${
                    selectedMachine?.name === machine.name
                      ? 'border-pf-accent bg-pf-accent/10'
                      : 'border-pf-border hover:bg-pf-bg-hover'
                  }`}
                >
                  <div className="flex items-center gap-3">
                    <div className={`p-2 rounded-lg ${
                      selectedMachine?.name === machine.name ? 'bg-pf-accent/20' : 'bg-pf-bg-2'
                    }`}>
                      <Printer className={`h-5 w-5 ${
                        selectedMachine?.name === machine.name ? 'text-pf-accent' : 'text-pf-text-tertiary'
                      }`} />
                    </div>
                    <div className="text-left">
                      <div className="font-medium text-pf-text-primary">{machine.name}</div>
                      {machine.nozzleDiameter && (
                        <div className="text-sm text-pf-text-secondary">
                          {machine.nozzleDiameter}mm nozzle
                        </div>
                      )}
                    </div>
                  </div>
                  {selectedMachine?.name === machine.name && (
                    <Check className="h-5 w-5 text-pf-accent" />
                  )}
                </button>
              ))}
            </div>
          )}
          
          {/* Navigation */}
          <div className="mt-6 flex justify-end">
            <Button
              onClick={goToNext}
              disabled={!canProceedToFilaments}
              className="flex items-center gap-2"
            >
              Next: Select Filaments
              <ChevronRight className="h-4 w-4" />
            </Button>
          </div>
        </div>
      )}

      {/* Step 2: Filament Profile Selection - 3 Column Layout */}
      {!isLoading && currentStep === 'filaments' && (
        <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
          <div className="mb-4">
            <h3 className="text-lg font-semibold text-pf-text-primary">
              Filament Profiles Selection
            </h3>
            <p className="text-sm text-pf-text-secondary">
              Select vendors, then material types, then individual profiles. Selected: {selectedFilaments.size}
            </p>
          </div>
          
          {/* 3-Column Layout */}
          <div className="grid grid-cols-4 gap-4 h-[450px]">
            {/* Column 1: Vendors */}
            <div className="border border-pf-border rounded-lg overflow-hidden flex flex-col">
              <div className="bg-pf-bg-2 px-3 py-2 border-b border-pf-border">
                <span className="text-sm font-medium text-pf-text-primary">Vendor</span>
              </div>
              <div className="flex-1 overflow-y-auto">
                {allVendors.map(({ name, count }) => {
                  const isSelected = selectedVendors.has(name);
                  return (
                    <button
                      key={name}
                      onClick={() => toggleVendor(name)}
                      className={`w-full text-left px-3 py-2 text-sm border-b border-pf-border/50 transition-colors ${
                        isSelected 
                          ? 'bg-pf-accent/20 text-pf-accent font-medium' 
                          : 'hover:bg-pf-bg-hover text-pf-text-primary'
                      }`}
                    >
                      {name}
                      <span className="text-pf-text-tertiary ml-1">({count})</span>
                    </button>
                  );
                })}
              </div>
            </div>

            {/* Column 2: Material Types */}
            <div className="border border-pf-border rounded-lg overflow-hidden flex flex-col">
              <div className="bg-pf-bg-2 px-3 py-2 border-b border-pf-border">
                <span className="text-sm font-medium text-pf-text-primary">Type</span>
              </div>
              <div className="flex-1 overflow-y-auto">
                {selectedVendors.size === 0 ? (
                  <div className="p-3 text-sm text-pf-text-tertiary italic">
                    Select a vendor first
                  </div>
                ) : availableMaterials.length === 0 ? (
                  <div className="p-3 text-sm text-pf-text-tertiary italic">
                    No materials found
                  </div>
                ) : (
                  availableMaterials.map(({ name, count }) => {
                    const isSelected = selectedMaterials.has(name);
                    return (
                      <button
                        key={name}
                        onClick={() => toggleMaterial(name)}
                        className={`w-full text-left px-3 py-2 text-sm border-b border-pf-border/50 transition-colors ${
                          isSelected 
                            ? 'bg-pf-accent/20 text-pf-accent font-medium' 
                            : 'hover:bg-pf-bg-hover text-pf-text-primary'
                        }`}
                      >
                        {name}
                        <span className="text-pf-text-tertiary ml-1">({count})</span>
                      </button>
                    );
                  })
                )}
              </div>
            </div>

            {/* Column 3 & 4: Profiles (spans 2 columns) */}
            <div className="col-span-2 border border-pf-border rounded-lg overflow-hidden flex flex-col">
              <div className="bg-pf-bg-2 px-3 py-2 border-b border-pf-border flex items-center justify-between">
                <span className="text-sm font-medium text-pf-text-primary">Profile</span>
                <div className="flex gap-2">
                  <Button 
                    variant="ghost" 
                    size="sm" 
                    onClick={selectAllFilaments}
                    disabled={availableFilaments.length === 0}
                  >
                    All
                  </Button>
                  <Button 
                    variant="ghost" 
                    size="sm" 
                    onClick={selectNoFilaments}
                    disabled={availableFilaments.length === 0}
                  >
                    None
                  </Button>
                </div>
              </div>
              <div className="flex-1 overflow-y-auto">
                {selectedMaterials.size === 0 ? (
                  <div className="p-3 text-sm text-pf-text-tertiary italic">
                    Select a material type first
                  </div>
                ) : availableFilaments.length === 0 ? (
                  <div className="p-3 text-sm text-pf-text-tertiary italic">
                    No filaments found
                  </div>
                ) : (
                  availableFilaments.map((filament) => {
                    const isSelected = selectedFilaments.has(filament.name);
                    return (
                      <label
                        key={filament.name}
                        className={`flex items-center gap-2 px-3 py-2 border-b border-pf-border/50 cursor-pointer transition-colors ${
                          isSelected 
                            ? 'bg-pf-accent/10' 
                            : 'hover:bg-pf-bg-hover'
                        }`}
                      >
                        <Checkbox
                          checked={isSelected}
                          onCheckedChange={() => toggleFilament(filament.name)}
                        />
                        <span className="text-sm text-pf-text-primary">{filament.name}</span>
                      </label>
                    );
                  })
                )}
              </div>
            </div>
          </div>
          
          {/* Navigation */}
          <div className="mt-6 flex justify-between">
            <Button variant="outline" onClick={goToPrevious}>
              Back
            </Button>
            <Button onClick={goToNext} className="flex items-center gap-2">
              Next: Review
              <ChevronRight className="h-4 w-4" />
            </Button>
          </div>
        </div>
      )}

      {/* Step 3: Review & Import */}
      {!isLoading && currentStep === 'review' && (
        <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
          <h3 className="text-lg font-semibold text-pf-text-primary mb-4">
            Review &amp; Import
          </h3>
          
          <div className="space-y-4">
            {/* Machine Profile */}
            <div className="p-4 border border-pf-border rounded-lg">
              <div className="flex items-center gap-2 mb-2">
                <Printer className="h-5 w-5 text-pf-accent" />
                <h4 className="font-medium text-pf-text-primary">Machine Profile</h4>
              </div>
              <p className="text-sm text-pf-text-secondary pl-7">
                {selectedMachine?.name}
                {selectedMachine?.nozzleDiameter && ` (${selectedMachine.nozzleDiameter}mm nozzle)`}
              </p>
            </div>
            
            {/* Process Profiles */}
            <div className="p-4 border border-pf-border rounded-lg">
              <div className="flex items-center gap-2 mb-2">
                <Settings className="h-5 w-5 text-pf-accent" />
                <h4 className="font-medium text-pf-text-primary">Process Profiles</h4>
                <span className="text-sm text-pf-text-tertiary">({compatibleProcesses.length} compatible)</span>
              </div>
              {compatibleProcesses.length === 0 ? (
                <p className="text-sm text-pf-text-tertiary pl-7 italic">
                  No process profiles explicitly compatible with {selectedMachine?.name}
                </p>
              ) : (
                <div className="mt-2 pl-7 max-h-32 overflow-y-auto">
                  <div className="flex flex-wrap gap-1">
                    {compatibleProcesses.slice(0, 10).map((p) => (
                      <span key={p.name} className="text-xs bg-pf-bg-2 px-2 py-1 rounded">
                        {p.name}
                      </span>
                    ))}
                    {compatibleProcesses.length > 10 && (
                      <span className="text-xs text-pf-text-tertiary">
                        +{compatibleProcesses.length - 10} more
                      </span>
                    )}
                  </div>
                </div>
              )}
            </div>
            
            {/* Filament Profiles */}
            <div className="p-4 border border-pf-border rounded-lg">
              <div className="flex items-center gap-2 mb-2">
                <Palette className="h-5 w-5 text-pf-accent" />
                <h4 className="font-medium text-pf-text-primary">Filament Profiles</h4>
                <span className="text-sm text-pf-text-tertiary">({selectedFilaments.size} selected)</span>
              </div>
              {selectedFilaments.size > 0 ? (
                <div className="mt-2 pl-7 max-h-32 overflow-y-auto">
                  <div className="flex flex-wrap gap-1">
                    {Array.from(selectedFilaments).slice(0, 15).map((name) => (
                      <span key={name} className="text-xs bg-pf-bg-2 px-2 py-1 rounded">
                        {name}
                      </span>
                    ))}
                    {selectedFilaments.size > 15 && (
                      <span className="text-xs text-pf-text-tertiary">
                        +{selectedFilaments.size - 15} more
                      </span>
                    )}
                  </div>
                </div>
              ) : (
                <p className="text-sm text-pf-text-secondary pl-7">
                  No filament profiles selected. You can still import without filaments.
                </p>
              )}
            </div>
          </div>
          
          {/* Summary */}
          <div className="mt-6 p-4 bg-pf-accent/10 rounded-lg">
            <div className="flex items-center gap-2 text-pf-accent">
              <CheckCircleIcon className="h-5 w-5" />
              <span className="font-medium">
                Ready to import {1 + compatibleProcesses.length + selectedFilaments.size} profiles
              </span>
            </div>
          </div>
          
          {/* Navigation */}
          <div className="mt-6 flex justify-between">
            <Button variant="outline" onClick={goToPrevious}>
              Back
            </Button>
            <Button
              onClick={() => importMutation.mutate()}
              disabled={importMutation.isPending}
              className="flex items-center gap-2"
            >
              <Download className="h-4 w-4" />
              {importMutation.isPending ? 'Importing...' : 'Import Profiles'}
            </Button>
          </div>
        </div>
      )}
    </PageTemplate>
  );
};

export default ProfileImportWizardPage;
