import React, { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Button, Alert, Checkbox } from '@/common/components/ui';
import { AlertCircleIcon } from '@/common/components/icons/MdiIcons';
import { ChevronRight, CheckCircle, ChevronLeft } from 'lucide-react';
import { apiClient } from '@/services/api';
import {
  type FilamentProfileDto,
  PRINTER_FILTER_ALL,
  PRINTER_FILTER_TEMPLATES,
  FILTER_ALL,
  getFilamentVendor,
} from './types';

interface FilamentProfileStepProps {
  /** Names of selected machine profiles */
  selectedMachineNames: string[];
  /** Currently selected filament profile names */
  selectedFilaments: Set<string>;
  /** Callback when a filament is toggled */
  onToggleFilament: (name: string) => void;
  /** Callback to update the entire selected filaments set */
  onSetSelectedFilaments: (filaments: Set<string>) => void;
  /** Go back to previous step */
  onBack: () => void;
  /** Proceed to next step */
  onNext: () => void;
  /** Names of filament profiles already imported (shown with badge) */
  importedFilamentNames?: string[];
}

/**
 * OrcaSlicer-style 4-column filter layout for filament selection:
 * Printer | Type | Vendor | Profile
 */
export const FilamentProfileStep: React.FC<FilamentProfileStepProps> = ({
  selectedMachineNames,
  selectedFilaments,
  onToggleFilament,
  onSetSelectedFilaments,
  onBack,
  onNext,
  importedFilamentNames,
}) => {
  // Filter state
  const [selectedPrinter, setSelectedPrinter] = useState<string>(PRINTER_FILTER_ALL);
  const [selectedMaterialType, setSelectedMaterialType] = useState<string>(FILTER_ALL);
  const [selectedVendor, setSelectedVendor] = useState<string>(FILTER_ALL);

  // Create a set for fast lookup of imported filament names
  const importedSet = useMemo(
    () => new Set(importedFilamentNames || []),
    [importedFilamentNames]
  );

  // Fetch filament profiles for selected machines
  const {
    data: machineFilamentProfiles = [],
    isLoading: machineFilamentsLoading,
    error: machineFilamentsError,
  } = useQuery({
    queryKey: ['filament-profiles-for-machines', selectedMachineNames],
    queryFn: async () => {
      if (selectedMachineNames.length === 0) return [];
      const res = await apiClient.post<FilamentProfileDto[]>('/slicer/profiles/filament/for-machines', {
        machineNames: selectedMachineNames,
      });
      return res.data;
    },
    enabled: selectedMachineNames.length > 0,
    staleTime: 60_000,
  });

  // Fetch template filaments from OrcaFilamentLibrary
  const {
    data: templateFilamentProfiles = [],
    isLoading: templateFilamentsLoading,
  } = useQuery({
    queryKey: ['filament-profiles-templates'],
    queryFn: async () => {
      const res = await apiClient.get<FilamentProfileDto[]>('/slicer/profiles/filament/templates');
      return res.data;
    },
    staleTime: 60_000,
  });

  // Build the printer options list: (All), (Templates), then individual machine names
  const printerOptions = useMemo(() => {
    return [PRINTER_FILTER_ALL, PRINTER_FILTER_TEMPLATES, ...selectedMachineNames];
  }, [selectedMachineNames]);

  // Determine which filaments to show based on printer selection
  const baseFilaments = useMemo(() => {
    if (selectedPrinter === PRINTER_FILTER_TEMPLATES) {
      return templateFilamentProfiles;
    } else if (selectedPrinter === PRINTER_FILTER_ALL) {
      // Combine machine-specific and templates, dedupe by name
      const combined = new Map<string, FilamentProfileDto>();
      for (const f of machineFilamentProfiles) {
        combined.set(f.name, f);
      }
      for (const f of templateFilamentProfiles) {
        if (!combined.has(f.name)) {
          combined.set(f.name, f);
        }
      }
      return Array.from(combined.values());
    } else {
      // Filter to specific machine's compatible filaments (exclude templates)
      // Only show filaments that explicitly list this machine in compatiblePrinters
      return machineFilamentProfiles.filter(
        (f) => f.compatiblePrinters?.includes(selectedPrinter) === true
      );
    }
  }, [selectedPrinter, machineFilamentProfiles, templateFilamentProfiles]);

  // Extract unique material types from current base filaments
  const materialTypes = useMemo(() => {
    const types = new Set<string>();
    for (const f of baseFilaments) {
      if (f.material) types.add(f.material);
    }
    return [FILTER_ALL, ...Array.from(types).sort()];
  }, [baseFilaments]);

  // Extract unique vendors from current base filaments
  const vendors = useMemo(() => {
    const vendorSet = new Set<string>();
    for (const f of baseFilaments) {
      vendorSet.add(getFilamentVendor(f));
    }
    return [FILTER_ALL, ...Array.from(vendorSet).sort()];
  }, [baseFilaments]);

  // Filter filaments based on selected type and vendor
  const filteredFilaments = useMemo(() => {
    return baseFilaments.filter((f) => {
      const matchesType = selectedMaterialType === FILTER_ALL || f.material === selectedMaterialType;
      const matchesVendor = selectedVendor === FILTER_ALL || getFilamentVendor(f) === selectedVendor;
      return matchesType && matchesVendor;
    });
  }, [baseFilaments, selectedMaterialType, selectedVendor]);

  // Reset Type and Vendor filters when Printer changes
  const handlePrinterChange = (printer: string) => {
    setSelectedPrinter(printer);
    setSelectedMaterialType(FILTER_ALL);
    setSelectedVendor(FILTER_ALL);
  };

  // Select/deselect all filtered filaments
  const selectAllFilteredFilaments = () => {
    const newSelected = new Set(selectedFilaments);
    for (const f of filteredFilaments) {
      newSelected.add(f.name);
    }
    onSetSelectedFilaments(newSelected);
  };

  const selectNoFilteredFilaments = () => {
    const newSelected = new Set(selectedFilaments);
    for (const f of filteredFilaments) {
      newSelected.delete(f.name);
    }
    onSetSelectedFilaments(newSelected);
  };

  const isLoading = machineFilamentsLoading || templateFilamentsLoading;

  // Count statistics
  const totalFilaments = machineFilamentProfiles.length + templateFilamentProfiles.length;
  const filteredSelectedCount = filteredFilaments.filter((f) => selectedFilaments.has(f.name)).length;

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
      <div className="flex items-center justify-between mb-4">
        <div>
          <h3 className="text-lg font-semibold text-pf-text-primary">Select Filament Profiles</h3>
          <p className="text-sm text-pf-text-secondary">
            Filter by printer, material type, and vendor to find profiles
          </p>
        </div>
      </div>

      {isLoading ? (
        <div className="flex items-center justify-center py-12">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent" />
        </div>
      ) : machineFilamentsError ? (
        <Alert className="mb-4">
          <AlertCircleIcon className="h-5 w-5" />
          <span>Failed to load filament profiles.</span>
        </Alert>
      ) : totalFilaments === 0 ? (
        <div className="text-center py-8 text-pf-text-secondary">
          No filament profiles found for the selected machine(s).
          <br />
          You can continue without filament profiles.
        </div>
      ) : (
        /* OrcaSlicer-style 4-column layout */
        <div className="grid grid-cols-4 gap-3 h-[450px]">
          {/* Column 1: Printer */}
          <FilterColumn
            title="Printer"
            options={printerOptions}
            selectedOption={selectedPrinter}
            onSelectOption={handlePrinterChange}
          />

          {/* Column 2: Material Type */}
          <FilterColumn
            title="Type"
            options={materialTypes}
            selectedOption={selectedMaterialType}
            onSelectOption={setSelectedMaterialType}
          />

          {/* Column 3: Vendor */}
          <FilterColumn
            title="Vendor"
            options={vendors}
            selectedOption={selectedVendor}
            onSelectOption={setSelectedVendor}
          />

          {/* Column 4: Profile list with checkboxes */}
          <div className="flex flex-col border border-pf-border rounded-lg overflow-hidden">
            <div className="bg-pf-bg-2 px-3 py-2 font-medium text-pf-text-primary text-sm border-b border-pf-border flex items-center justify-between">
              <span>Profile</span>
              <div className="flex gap-1">
                <Button
                  size="sm"
                  onClick={selectAllFilteredFilaments}
                  className="text-xs px-2 py-1 h-auto"
                >
                  All
                </Button>
                <Button
                  size="sm"
                  onClick={selectNoFilteredFilaments}
                  className="text-xs px-2 py-1 h-auto"
                >
                  None
                </Button>
              </div>
            </div>
            <div className="flex-1 overflow-y-auto">
              {filteredFilaments.length === 0 ? (
                <div className="p-4 text-sm text-pf-text-tertiary text-center">
                  No profiles match the selected filters
                </div>
              ) : (
                filteredFilaments.map((filament) => {
                  const isSelected = selectedFilaments.has(filament.name);
                  const isImported = importedSet.has(filament.name);
                  return (
                    <label
                      key={filament.name}
                      className={`flex items-center gap-2 px-3 py-2 cursor-pointer transition-colors ${
                        isSelected ? 'bg-pf-accent/10' : 'hover:bg-pf-bg-hover'
                      }`}
                    >
                      <Checkbox checked={isSelected} onChange={() => onToggleFilament(filament.name)} />
                      <span
                        className="text-sm text-pf-text-primary truncate flex-1"
                        title={filament.name}
                      >
                        {filament.name}
                      </span>
                      {isImported && (
                        <span title="Already imported">
                          <CheckCircle className="h-3.5 w-3.5 text-green-500 shrink-0" />
                        </span>
                      )}
                    </label>
                  );
                })
              )}
            </div>
          </div>
        </div>
      )}

      <div className="mt-6 flex justify-between items-center">
        <Button 
          onClick={onBack}
          iconLeft={<ChevronLeft className="h-4 w-4 rotate-180" />}
        >Back</Button>
        <div className="text-sm text-pf-text-secondary">
          {selectedFilaments.size} selected
          {filteredFilaments.length < totalFilaments && (
            <span className="text-pf-text-tertiary"> ({filteredSelectedCount} shown)</span>
          )}
          {importedSet.size > 0 && (
            <span className="text-green-600 dark:text-green-400 ml-2">
              ({importedSet.size} already imported)
            </span>
          )}
        </div>
        <Button
          onClick={onNext}
          iconRight={<ChevronRight className="h-4 w-4" />}>
          Next
        </Button>
      </div>
    </div>
  );
};

// ================================
// Filter Column Sub-component
// ================================

interface FilterColumnProps {
  title: string;
  options: string[];
  selectedOption: string;
  onSelectOption: (option: string) => void;
}

const FilterColumn: React.FC<FilterColumnProps> = ({
  title,
  options,
  selectedOption,
  onSelectOption,
}) => {
  return (
    <div className="flex flex-col border border-pf-border rounded-lg overflow-hidden">
      <div className="bg-pf-bg-2 px-3 py-2 font-medium text-pf-text-primary text-sm border-b border-pf-border">
        {title}
      </div>
      <div className="flex-1 overflow-y-auto">
        {options.map((option) => {
          const isSelected = selectedOption === option;
          return (
            <Button
              key={option}
              variant="subtle"
              onClick={() => onSelectOption(option)}
              className={`w-full justify-start rounded-none px-3 py-2 text-sm h-auto font-normal transition-colors ${
                isSelected ? 'bg-pf-accent text-white hover:bg-pf-accent/90 hover:text-white' : 'text-pf-text-primary hover:bg-pf-bg-hover'
              }`}
            >
              {option}
            </Button>
          );
        })}
      </div>
    </div>
  );
};
