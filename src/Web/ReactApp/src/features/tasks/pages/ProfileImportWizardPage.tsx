import React, { useState, useMemo, useEffect } from 'react';
import { useSearchParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { PageTemplate } from '@/common/components/PageTemplate';
import { LayersIcon, CheckCircleIcon } from '@/common/components/icons/MdiIcons';
import { Check, Package, Printer, Palette, FolderOpen } from 'lucide-react';
import { apiClient } from '@/services/api';
import { tasksApi } from '@/services/tasksApi';
import { officialProfilesService } from '@/services/officialProfilesService';
import { toast } from 'sonner';

// Import wizard step components
import {
  PrinterModelSelectionStep,
  MachineProfileStep,
  FilamentProfileStep,
  ReviewStep,
  ImportProgressModal,
  type WizardStep,
  type MachineProfileDto,
  type ProcessProfileDto,
  type PrinterModelDto,
  type ManufacturerDto,
} from '../components/profile-wizard';

/**
 * Profile Import Wizard Shell
 *
 * 4-step flow (when no modelId provided) or 3-step flow (when modelId provided):
 * 0. Select printer model (only shown when no modelId)
 * 1. Select machine profile(s)
 * 2. Select filament profiles (4-column OrcaSlicer-style filter)
 * 3. Review and import (process profiles are auto-imported)
 */
export const ProfileImportWizardPage: React.FC = () => {
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const modelIdFromUrl = searchParams.get('modelId');
  const taskId = searchParams.get('taskId');

  // Track if the wizard was started without a modelId (4-step mode)
  // This is set once on mount and doesn't change when URL updates
  const [startedWithoutModelId] = useState(() => !searchParams.get('modelId'));

  // Local state for selected model (when user selects from step 0)
  const [selectedModelInfo, setSelectedModelInfo] = useState<{
    modelId: string;
    modelName: string;
    manufacturerName: string;
  } | null>(null);

  // Use URL modelId if available, otherwise use selected model
  const modelId = modelIdFromUrl || selectedModelInfo?.modelId || null;

  // Wizard state - start at 'model-select' if no modelId
  const [currentStep, setCurrentStep] = useState<WizardStep>(modelIdFromUrl ? 'machine' : 'model-select');
  const [selectedMachines, setSelectedMachines] = useState<Set<string>>(new Set());
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

  // Get manufacturer name from query or from selected model info
  const manufacturerName = manufacturer?.name || selectedModelInfo?.manufacturerName || null;

  // Fetch machine profiles for the model
  const {
    data: machineProfiles = [],
    isLoading: machinesLoading,
    error: machinesError,
  } = useQuery({
    queryKey: ['machine-profiles-for-model', modelId],
    queryFn: async () => {
      if (!modelId) return [];
      const res = await apiClient.get<MachineProfileDto[]>(`/slicer/profiles/machine/for-model/${modelId}`);
      return res.data;
    },
    enabled: !!modelId,
    staleTime: 60_000,
  });

  // Fetch already-imported profile names for pre-selection
  const { data: importedNames } = useQuery({
    queryKey: ['imported-profile-names', modelId],
    queryFn: async () => {
      if (!modelId) return null;
      return await officialProfilesService.getImportedProfileNamesForModel(modelId);
    },
    enabled: !!modelId,
    staleTime: 30_000, // Refresh more frequently to catch new imports
  });

  // Pre-select already-imported profiles when data loads
  useEffect(() => {
    if (importedNames && machineProfiles.length > 0 && selectedMachines.size === 0) {
      // Pre-select machine profiles that are already imported
      const importedMachineSet = new Set(importedNames.machineProfileNames);
      const preSelected = machineProfiles
        .filter((m) => importedMachineSet.has(m.name))
        .map((m) => m.name);
      if (preSelected.length > 0) {
        queueMicrotask(() => setSelectedMachines(new Set(preSelected)));
      }
    }
  }, [importedNames, machineProfiles, selectedMachines.size]);

  // Fetch process profiles for selected machines (auto-imported)
  const selectedMachineNames = useMemo(() => Array.from(selectedMachines), [selectedMachines]);

  const { data: processProfiles = [] } = useQuery({
    queryKey: ['process-profiles-for-machines', selectedMachineNames],
    queryFn: async () => {
      if (selectedMachineNames.length === 0) return [];
      const res = await apiClient.post<ProcessProfileDto[]>('/slicer/profiles/process/for-machines', {
        machineNames: selectedMachineNames,
      });
      return res.data;
    },
    enabled: selectedMachineNames.length > 0,
    staleTime: 60_000,
  });

  // Import mutation
  const importMutation = useMutation({
    mutationFn: async () => {
      if (selectedMachines.size === 0) {
        throw new Error('Please select at least one machine profile');
      }
      if (!modelId) {
        throw new Error('No printer model specified');
      }
      if (!manufacturerName) {
        throw new Error('Manufacturer information not available');
      }

      // Auto-import ALL process profiles for selected machines
      const allProcessProfileNames = processProfiles.map((p) => p.name);

      const result = await officialProfilesService.importSelectedProfilesForModel(modelId, {
        manufacturerName: manufacturerName,
        selectedMachineProfiles: Array.from(selectedMachines),
        selectedProcessProfiles: allProcessProfileNames,
        selectedFilamentProfiles: Array.from(selectedFilaments),
      });

      if (result.error) {
        throw new Error(result.error);
      }
      return result;
    },
    onSuccess: async (result) => {
      toast.success(
        `Profiles imported: ${result.machineProfilesImported} machine(s), ${result.processProfilesImported} process(s), ${result.filamentProfilesImported} filament(s)` +
          (result.skipped > 0 ? ` (${result.skipped} skipped as duplicates)` : ''),
        { duration: 5000 }
      );

      if (taskId) {
        try {
          await tasksApi.completeTask(taskId);
          await queryClient.invalidateQueries({ queryKey: ['tasks'] });
          toast.success('Task completed', { duration: 2000 });
        } catch (e) {
          console.error('Failed to complete task:', e);
          toast.error('Failed to mark task as complete');
        }
      }

      navigate('/');
    },
    onError: (error: Error) => {
      toast.error(`Failed to import profiles: ${error.message}`);
    },
  });

  // Machine selection handlers
  const toggleMachine = (name: string) => {
    const newSelected = new Set(selectedMachines);
    if (newSelected.has(name)) {
      newSelected.delete(name);
    } else {
      newSelected.add(name);
    }
    setSelectedMachines(newSelected);
    setSelectedFilaments(new Set()); // Clear filaments when machines change
  };

  const selectAllMachines = () => setSelectedMachines(new Set(machineProfiles.map((m) => m.name)));
  const selectNoMachines = () => {
    setSelectedMachines(new Set());
    setSelectedFilaments(new Set());
  };

  // Filament selection handlers
  const toggleFilament = (name: string) => {
    const newSelected = new Set(selectedFilaments);
    if (newSelected.has(name)) {
      newSelected.delete(name);
    } else {
      newSelected.add(name);
    }
    setSelectedFilaments(newSelected);
  };

  // Step navigation
  const goToModelSelect = () => setCurrentStep('model-select');
  const goToMachine = () => setCurrentStep('machine');
  const goToFilaments = () => setCurrentStep('filaments');
  const goToReview = () => setCurrentStep('review');
  const goBackToFilaments = () => setCurrentStep('filaments');

  // Handler for model selection (step 0)
  const handleModelSelect = (newModelId: string, modelName: string, mfgName: string) => {
    setSelectedModelInfo({ modelId: newModelId, modelName, manufacturerName: mfgName });
    // Update URL with modelId so refreshing keeps context
    setSearchParams((prev) => {
      prev.set('modelId', newModelId);
      return prev;
    });
    setCurrentStep('machine');
  };

  // Step configuration - include model-select step when wizard started without modelId
  // Use startedWithoutModelId instead of modelIdFromUrl to keep 4 steps after model selection
  const showModelSelectStep = startedWithoutModelId;
  
  const steps: { key: WizardStep; label: string; icon: React.ReactNode }[] = showModelSelectStep
    ? [
        { key: 'model-select', label: 'Model', icon: <FolderOpen className="h-4 w-4" /> },
        { key: 'machine', label: 'Machine', icon: <Printer className="h-4 w-4" /> },
        { key: 'filaments', label: 'Filaments', icon: <Palette className="h-4 w-4" /> },
        { key: 'review', label: 'Review', icon: <CheckCircleIcon className="h-4 w-4" /> },
      ]
    : [
        { key: 'machine', label: 'Machine', icon: <Printer className="h-4 w-4" /> },
        { key: 'filaments', label: 'Filaments', icon: <Palette className="h-4 w-4" /> },
        { key: 'review', label: 'Review', icon: <CheckCircleIcon className="h-4 w-4" /> },
      ];

  const stepOrder: WizardStep[] = showModelSelectStep
    ? ['model-select', 'machine', 'filaments', 'review']
    : ['machine', 'filaments', 'review'];
  const currentStepIndex = stepOrder.indexOf(currentStep);

  // Get display name for header
  const displayModelName = printerModel?.name || selectedModelInfo?.modelName || '';
  const displayManufacturerName = manufacturerName || '';

  return (
    <PageTemplate
      title="Import Slicer Profiles"
      subtitle={displayModelName ? `${displayManufacturerName} ${displayModelName}` : 'Select a printer model'}
      icon={LayersIcon}
    >
      {/* Header with printer info - only show when model is selected */}
      {(modelId || selectedModelInfo) && (
        <div className="mb-6">
          <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-4">
            <div className="flex items-center gap-4">
              <div className="p-3 bg-pf-accent/10 rounded-lg">
                <Package className="h-6 w-6 text-pf-accent" />
              </div>
              <div>
                <h2 className="text-lg font-semibold text-pf-text-primary">
                  {displayManufacturerName} {displayModelName}
                </h2>
                <p className="text-sm text-pf-text-secondary">Configure slicer profiles for this printer</p>
              </div>
            </div>
          </div>
        </div>
      )}

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

      {/* Step content */}
      {currentStep === 'model-select' && (
        <PrinterModelSelectionStep onSelectModel={handleModelSelect} />
      )}

      {currentStep === 'machine' && (
        <MachineProfileStep
          machineProfiles={machineProfiles}
          selectedMachines={selectedMachines}
          onToggleMachine={toggleMachine}
          onSelectAll={selectAllMachines}
          onSelectNone={selectNoMachines}
          onNext={goToFilaments}
          onBack={showModelSelectStep ? goToModelSelect : undefined}
          isLoading={modelLoading || machinesLoading}
          error={machinesError as Error | null}
          printerModelName={printerModel?.name}
          importedMachineNames={importedNames?.machineProfileNames}
        />
      )}

      {currentStep === 'filaments' && (
        <FilamentProfileStep
          selectedMachineNames={selectedMachineNames}
          selectedFilaments={selectedFilaments}
          onToggleFilament={toggleFilament}
          onSetSelectedFilaments={setSelectedFilaments}
          onBack={goToMachine}
          onNext={goToReview}
          importedFilamentNames={importedNames?.filamentProfileNames}
        />
      )}

      {currentStep === 'review' && (
        <ReviewStep
          selectedMachines={selectedMachines}
          processProfiles={processProfiles}
          selectedFilaments={selectedFilaments}
          onBack={goBackToFilaments}
          onImport={() => importMutation.mutate()}
          isImporting={importMutation.isPending}
          importedMachineNames={importedNames?.machineProfileNames}
          importedProcessNames={importedNames?.processProfileNames}
          importedFilamentNames={importedNames?.filamentProfileNames}
        />
      )}

      {/* Import progress modal */}
      <ImportProgressModal
        isOpen={importMutation.isPending}
        machineCount={selectedMachines.size}
        processCount={processProfiles.length}
        filamentCount={selectedFilaments.size}
      />
    </PageTemplate>
  );
};

export default ProfileImportWizardPage;
