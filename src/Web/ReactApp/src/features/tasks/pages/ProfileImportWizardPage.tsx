import React, { useState, useMemo, useEffect } from 'react';
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
type WizardStep = 'machine' | 'process' | 'filaments' | 'review';

/**
 * Profile Import Wizard - 4-step lazy-loading flow:
 * 1. Select machine profile(s) - fetched for specific model only
 * 2. Select process profiles - fetched based on selected machines
 * 3. Select filament profiles - fetched based on selected machines  
 * 4. Review and import
 */
export const ProfileImportWizardPage: React.FC = () => {
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  
  const modelId = searchParams.get('modelId');
  const taskId = searchParams.get('taskId');

  // Wizard state
  const [currentStep, setCurrentStep] = useState<WizardStep>('machine');
  const [selectedMachines, setSelectedMachines] = useState<Set<string>>(new Set());
  const [selectedProcesses, setSelectedProcesses] = useState<Set<string>>(new Set());
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

  // STEP 1: Fetch machine profiles for specific model only
  const { data: machineProfiles = [], isLoading: machinesLoading, error: machinesError } = useQuery({
    queryKey: ['machine-profiles', manufacturer?.name, printerModel?.name],
    queryFn: async () => {
      if (!manufacturer?.name || !printerModel?.name) return [];
      // Use the specific endpoint for model
      const url = `/slicer/profiles/machine/${encodeURIComponent(manufacturer.name)}/${encodeURIComponent(printerModel.name)}`;
      const res = await apiClient.get<MachineProfileDto[]>(url);
      return res.data;
    },
    enabled: !!manufacturer?.name && !!printerModel?.name,
    staleTime: 60_000,
  });

  // STEP 2: Fetch process profiles for selected machines (lazy load)
  const selectedMachineNames = useMemo(() => Array.from(selectedMachines), [selectedMachines]);
  
  const { data: processProfiles = [], isLoading: processesLoading, error: processesError } = useQuery({
    queryKey: ['process-profiles-for-machines', selectedMachineNames],
    queryFn: async () => {
      if (selectedMachineNames.length === 0) return [];
      const res = await apiClient.post<ProcessProfileDto[]>('/slicer/profiles/process/for-machines', {
        machineNames: selectedMachineNames
      });
      return res.data;
    },
    enabled: selectedMachineNames.length > 0 && currentStep !== 'machine',
    staleTime: 60_000,
  });

  // STEP 3: Fetch filament profiles for selected machines (lazy load)
  const { data: filamentProfiles = [], isLoading: filamentsLoading, error: filamentsError } = useQuery({
    queryKey: ['filament-profiles-for-machines', selectedMachineNames],
    queryFn: async () => {
      if (selectedMachineNames.length === 0) return [];
      const res = await apiClient.post<FilamentProfileDto[]>('/slicer/profiles/filament/for-machines', {
        machineNames: selectedMachineNames
      });
      return res.data;
    },
    enabled: selectedMachineNames.length > 0 && (currentStep === 'filaments' || currentStep === 'review'),
    staleTime: 60_000,
  });

  // Auto-select all process profiles when they load
  useEffect(() => {
    if (processProfiles.length > 0 && selectedProcesses.size === 0 && currentStep === 'process') {
      setSelectedProcesses(new Set(processProfiles.map(p => p.name)));
    }
  }, [processProfiles, currentStep, selectedProcesses.size]);

  // Group filaments by manufacturer and material for display
  const groupedFilaments = useMemo(() => {
    const groups: Record<string, Record<string, FilamentProfileDto[]>> = {};
    
    for (const filament of filamentProfiles) {
      const vendor = filament.manufacturer || 'Unknown';
      const material = filament.material || 'Other';
      
      if (!groups[vendor]) groups[vendor] = {};
      if (!groups[vendor][material]) groups[vendor][material] = [];
      groups[vendor][material].push(filament);
    }
    
    return groups;
  }, [filamentProfiles]);

  // Toggle machine selection
  const toggleMachine = (name: string) => {
    const newSelected = new Set(selectedMachines);
    if (newSelected.has(name)) {
      newSelected.delete(name);
    } else {
      newSelected.add(name);
    }
    setSelectedMachines(newSelected);
    // Clear downstream selections when machines change
    setSelectedProcesses(new Set());
    setSelectedFilaments(new Set());
  };

  // Toggle process selection
  const toggleProcess = (name: string) => {
    const newSelected = new Set(selectedProcesses);
    if (newSelected.has(name)) {
      newSelected.delete(name);
    } else {
      newSelected.add(name);
    }
    setSelectedProcesses(newSelected);
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

  // Select all/none helpers
  const selectAllMachines = () => setSelectedMachines(new Set(machineProfiles.map(m => m.name)));
  const selectNoMachines = () => { setSelectedMachines(new Set()); setSelectedProcesses(new Set()); setSelectedFilaments(new Set()); };
  
  const selectAllProcesses = () => setSelectedProcesses(new Set(processProfiles.map(p => p.name)));
  const selectNoProcesses = () => setSelectedProcesses(new Set());
  
  const selectAllFilaments = () => setSelectedFilaments(new Set(filamentProfiles.map(f => f.name)));
  const selectNoFilaments = () => setSelectedFilaments(new Set());

  // Import mutation
  const importMutation = useMutation({
    mutationFn: async () => {
      if (selectedMachines.size === 0) {
        throw new Error('Please select at least one machine profile');
      }
      
      // TODO: Call API to persist selected profiles
      return {
        machineCount: selectedMachines.size,
        processCount: selectedProcesses.size,
        filamentCount: selectedFilaments.size,
      };
    },
    onSuccess: async (result) => {
      toast.success(
        `Profiles configured: ${result.machineCount} machine(s), ${result.processCount} process profile(s), ${result.filamentCount} filament profile(s)`,
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
  const canProceedToProcess = selectedMachines.size > 0;

  const goToNext = () => {
    if (currentStep === 'machine' && canProceedToProcess) {
      setCurrentStep('process');
    } else if (currentStep === 'process') {
      setCurrentStep('filaments');
    } else if (currentStep === 'filaments') {
      setCurrentStep('review');
    }
  };

  const goToPrevious = () => {
    if (currentStep === 'process') {
      setCurrentStep('machine');
    } else if (currentStep === 'filaments') {
      setCurrentStep('process');
    } else if (currentStep === 'review') {
      setCurrentStep('filaments');
    }
  };

  // No model ID provided
  if (!modelId) {
    return (
      <PageTemplate title="Import Profiles" subtitle="Import slicer profiles for your printers" icon={LayersIcon}>
        <Alert>
          <AlertCircleIcon className="h-5 w-5" />
          <span>No printer model specified. Please access this page from a task in the dashboard.</span>
        </Alert>
        <div className="mt-4">
          <Button onClick={() => navigate('/')}>Go to Dashboard</Button>
        </div>
      </PageTemplate>
    );
  }

  const isLoading = modelLoading || (currentStep === 'machine' && machinesLoading);

  // Step indicators
  const steps: { key: WizardStep; label: string; icon: React.ReactNode }[] = [
    { key: 'machine', label: 'Machine', icon: <Printer className="h-4 w-4" /> },
    { key: 'process', label: 'Process', icon: <Settings className="h-4 w-4" /> },
    { key: 'filaments', label: 'Filaments', icon: <Palette className="h-4 w-4" /> },
    { key: 'review', label: 'Review', icon: <CheckCircleIcon className="h-4 w-4" /> },
  ];

  const stepOrder: WizardStep[] = ['machine', 'process', 'filaments', 'review'];
  const currentStepIndex = stepOrder.indexOf(currentStep);

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
            const isCompleted = index < currentStepIndex;
            
            return (
              <React.Fragment key={step.key}>
                {index > 0 && (
                  <div className={`w-8 h-0.5 ${isCompleted || isActive ? 'bg-pf-accent' : 'bg-pf-border'}`} />
                )}
                <div 
                  className={`flex items-center gap-2 px-3 py-2 rounded-lg transition-colors ${
                    isActive 
                      ? 'bg-pf-accent text-white' 
                      : isCompleted 
                        ? 'bg-pf-accent/20 text-pf-accent'
                        : 'bg-pf-bg-1 text-pf-text-tertiary'
                  }`}
                >
                  {isCompleted ? <Check className="h-4 w-4" /> : step.icon}
                  <span className="text-sm font-medium hidden sm:inline">{step.label}</span>
                </div>
              </React.Fragment>
            );
          })}
        </div>
      </div>

      {/* Error state */}
      {machinesError && (
        <Alert className="mb-4">
          <AlertCircleIcon className="h-5 w-5" />
          <span>Failed to load machine profiles. Make sure the OrcaSlicer worker is running.</span>
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
          <div className="flex items-center justify-between mb-4">
            <div>
              <h3 className="text-lg font-semibold text-pf-text-primary">
                Select Machine Profile(s)
              </h3>
              <p className="text-sm text-pf-text-secondary">
                Choose the nozzle configuration(s) for your {printerModel?.name}
              </p>
            </div>
            <div className="flex gap-2">
              <Button size="sm" onClick={selectAllMachines}>All</Button>
              <Button size="sm" onClick={selectNoMachines}>None</Button>
            </div>
          </div>
          
          {machineProfiles.length === 0 ? (
            <Alert>
              <AlertCircleIcon className="h-5 w-5" />
              <span>
                No machine profiles found for &quot;{printerModel?.name}&quot;. 
                The name may not match OrcaSlicer&apos;s naming convention exactly.
              </span>
            </Alert>
          ) : (
            <div className="space-y-2 max-h-[400px] overflow-y-auto">
              {machineProfiles.map((machine) => {
                const isSelected = selectedMachines.has(machine.name);
                return (
                  <label
                    key={machine.name}
                    className={`flex items-center gap-3 p-4 rounded-lg border cursor-pointer transition-colors ${
                      isSelected
                        ? 'border-pf-accent bg-pf-accent/10'
                        : 'border-pf-border hover:bg-pf-bg-hover'
                    }`}
                  >
                    <Checkbox
                      checked={isSelected}
                      onChange={() => toggleMachine(machine.name)}
                    />
                    <div className="flex-1">
                      <div className="font-medium text-pf-text-primary">{machine.name}</div>
                      {machine.nozzleDiameter && (
                        <div className="text-sm text-pf-text-secondary">
                          {machine.nozzleDiameter}mm nozzle
                        </div>
                      )}
                    </div>
                    {isSelected && <Check className="h-5 w-5 text-pf-accent" />}
                  </label>
                );
              })}
            </div>
          )}
          
          <div className="mt-6 flex justify-between items-center">
            <div className="text-sm text-pf-text-secondary">
              {selectedMachines.size} of {machineProfiles.length} selected
            </div>
            <Button
              onClick={goToNext}
              disabled={!canProceedToProcess}
              className="flex items-center gap-2"
            >
              Next: Process Profiles
              <ChevronRight className="h-4 w-4" />
            </Button>
          </div>
        </div>
      )}

      {/* Step 2: Process Profile Selection */}
      {!isLoading && currentStep === 'process' && (
        <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
          <div className="flex items-center justify-between mb-4">
            <div>
              <h3 className="text-lg font-semibold text-pf-text-primary">
                Select Process Profiles
              </h3>
              <p className="text-sm text-pf-text-secondary">
                Print quality presets compatible with your selected machine(s)
              </p>
            </div>
            <div className="flex gap-2">
              <Button size="sm" onClick={selectAllProcesses} disabled={processProfiles.length === 0}>All</Button>
              <Button size="sm" onClick={selectNoProcesses} disabled={processProfiles.length === 0}>None</Button>
            </div>
          </div>
          
          {processesLoading ? (
            <div className="flex items-center justify-center py-12">
              <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent" />
            </div>
          ) : processesError ? (
            <Alert className="mb-4">
              <AlertCircleIcon className="h-5 w-5" />
              <span>Failed to load process profiles.</span>
            </Alert>
          ) : processProfiles.length === 0 ? (
            <div className="text-center py-8 text-pf-text-secondary">
              No process profiles found for the selected machine(s).
              <br />
              You can continue without process profiles.
            </div>
          ) : (
            <div className="space-y-2 max-h-[400px] overflow-y-auto">
              {processProfiles.map((process) => {
                const isSelected = selectedProcesses.has(process.name);
                return (
                  <label
                    key={process.name}
                    className={`flex items-center gap-3 p-3 rounded-lg border cursor-pointer transition-colors ${
                      isSelected
                        ? 'border-pf-accent bg-pf-accent/10'
                        : 'border-pf-border hover:bg-pf-bg-hover'
                    }`}
                  >
                    <Checkbox
                      checked={isSelected}
                      onChange={() => toggleProcess(process.name)}
                    />
                    <div className="flex-1">
                      <div className="text-sm text-pf-text-primary">{process.name}</div>
                    </div>
                  </label>
                );
              })}
            </div>
          )}
          
          <div className="mt-6 flex justify-between items-center">
            <Button onClick={goToPrevious}>Back</Button>
            <div className="text-sm text-pf-text-secondary">
              {selectedProcesses.size} of {processProfiles.length} selected
            </div>
            <Button onClick={goToNext} className="flex items-center gap-2">
              Next: Filaments
              <ChevronRight className="h-4 w-4" />
            </Button>
          </div>
        </div>
      )}

      {/* Step 3: Filament Profile Selection */}
      {!isLoading && currentStep === 'filaments' && (
        <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
          <div className="flex items-center justify-between mb-4">
            <div>
              <h3 className="text-lg font-semibold text-pf-text-primary">
                Select Filament Profiles
              </h3>
              <p className="text-sm text-pf-text-secondary">
                Material presets compatible with your selected machine(s)
              </p>
            </div>
            <div className="flex gap-2">
              <Button size="sm" onClick={selectAllFilaments} disabled={filamentProfiles.length === 0}>All</Button>
              <Button size="sm" onClick={selectNoFilaments} disabled={filamentProfiles.length === 0}>None</Button>
            </div>
          </div>
          
          {filamentsLoading ? (
            <div className="flex items-center justify-center py-12">
              <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent" />
            </div>
          ) : filamentsError ? (
            <Alert className="mb-4">
              <AlertCircleIcon className="h-5 w-5" />
              <span>Failed to load filament profiles.</span>
            </Alert>
          ) : filamentProfiles.length === 0 ? (
            <div className="text-center py-8 text-pf-text-secondary">
              No filament profiles found for the selected machine(s).
              <br />
              You can continue without filament profiles.
            </div>
          ) : (
            <div className="max-h-[400px] overflow-y-auto space-y-4">
              {Object.entries(groupedFilaments).sort(([a], [b]) => a.localeCompare(b)).map(([vendor, materials]) => (
                <div key={vendor} className="border border-pf-border rounded-lg">
                  <div className="bg-pf-bg-2 px-4 py-2 font-medium text-pf-text-primary border-b border-pf-border">
                    {vendor}
                  </div>
                  <div className="p-2">
                    {Object.entries(materials).sort(([a], [b]) => a.localeCompare(b)).map(([material, filaments]) => (
                      <div key={`${vendor}-${material}`} className="mb-2 last:mb-0">
                        <div className="text-xs font-medium text-pf-text-tertiary uppercase px-2 py-1">
                          {material} ({filaments.length})
                        </div>
                        <div className="space-y-1">
                          {filaments.map((filament) => {
                            const isSelected = selectedFilaments.has(filament.name);
                            return (
                              <label
                                key={filament.name}
                                className={`flex items-center gap-2 px-3 py-2 rounded cursor-pointer transition-colors ${
                                  isSelected
                                    ? 'bg-pf-accent/10'
                                    : 'hover:bg-pf-bg-hover'
                                }`}
                              >
                                <Checkbox
                                  checked={isSelected}
                                  onChange={() => toggleFilament(filament.name)}
                                />
                                <span className="text-sm text-pf-text-primary truncate">{filament.name}</span>
                              </label>
                            );
                          })}
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              ))}
            </div>
          )}
          
          <div className="mt-6 flex justify-between items-center">
            <Button onClick={goToPrevious}>Back</Button>
            <div className="text-sm text-pf-text-secondary">
              {selectedFilaments.size} of {filamentProfiles.length} selected
            </div>
            <Button onClick={goToNext} className="flex items-center gap-2">
              Next: Review
              <ChevronRight className="h-4 w-4" />
            </Button>
          </div>
        </div>
      )}

      {/* Step 4: Review & Import */}
      {!isLoading && currentStep === 'review' && (
        <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
          <h3 className="text-lg font-semibold text-pf-text-primary mb-4">
            Review &amp; Import
          </h3>
          
          <div className="space-y-4">
            {/* Machine Profiles */}
            <div className="p-4 border border-pf-border rounded-lg">
              <div className="flex items-center gap-2 mb-2">
                <Printer className="h-5 w-5 text-pf-accent" />
                <h4 className="font-medium text-pf-text-primary">Machine Profiles</h4>
                <span className="text-sm text-pf-text-tertiary">({selectedMachines.size} selected)</span>
              </div>
              <div className="pl-7 flex flex-wrap gap-1">
                {Array.from(selectedMachines).slice(0, 5).map((name) => (
                  <span key={name} className="text-xs bg-pf-bg-2 px-2 py-1 rounded">
                    {name}
                  </span>
                ))}
                {selectedMachines.size > 5 && (
                  <span className="text-xs text-pf-text-tertiary">
                    +{selectedMachines.size - 5} more
                  </span>
                )}
              </div>
            </div>
            
            {/* Process Profiles */}
            <div className="p-4 border border-pf-border rounded-lg">
              <div className="flex items-center gap-2 mb-2">
                <Settings className="h-5 w-5 text-pf-accent" />
                <h4 className="font-medium text-pf-text-primary">Process Profiles</h4>
                <span className="text-sm text-pf-text-tertiary">({selectedProcesses.size} selected)</span>
              </div>
              {selectedProcesses.size > 0 ? (
                <div className="pl-7 flex flex-wrap gap-1 max-h-24 overflow-y-auto">
                  {Array.from(selectedProcesses).slice(0, 10).map((name) => (
                    <span key={name} className="text-xs bg-pf-bg-2 px-2 py-1 rounded">
                      {name}
                    </span>
                  ))}
                  {selectedProcesses.size > 10 && (
                    <span className="text-xs text-pf-text-tertiary">
                      +{selectedProcesses.size - 10} more
                    </span>
                  )}
                </div>
              ) : (
                <p className="text-sm text-pf-text-tertiary pl-7 italic">No process profiles selected</p>
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
                <div className="pl-7 flex flex-wrap gap-1 max-h-24 overflow-y-auto">
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
              ) : (
                <p className="text-sm text-pf-text-tertiary pl-7 italic">No filament profiles selected</p>
              )}
            </div>
          </div>
          
          {/* Summary */}
          <div className="mt-6 p-4 bg-pf-accent/10 rounded-lg">
            <div className="flex items-center gap-2 text-pf-accent">
              <CheckCircleIcon className="h-5 w-5" />
              <span className="font-medium">
                Ready to import {selectedMachines.size + selectedProcesses.size + selectedFilaments.size} profiles
              </span>
            </div>
          </div>
          
          {/* Navigation */}
          <div className="mt-6 flex justify-between">
            <Button onClick={goToPrevious}>Back</Button>
            <Button
              onClick={() => importMutation.mutate()}
              disabled={importMutation.isPending || selectedMachines.size === 0}
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
