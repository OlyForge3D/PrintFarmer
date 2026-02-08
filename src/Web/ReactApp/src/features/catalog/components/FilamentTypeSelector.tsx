import React, { useState, useMemo } from 'react';
// No MdiIcons used in this component
import { ChevronsRightIcon, ChevronsLeftIcon, SettingsIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui/Button';
import type { FilamentTypeDto } from '@/types/api';

interface FilamentTypeSelectorProps {
  availableFilamentTypes?: FilamentTypeDto[];
  selectedFilamentTypes?: string[];
  onSelectionChange: (selectedTypes: string[]) => void;
  className?: string;
}

export function FilamentTypeSelector({ 
  availableFilamentTypes = [], 
  selectedFilamentTypes = [], 
  onSelectionChange,
  className
}: FilamentTypeSelectorProps) {
  const [showSelector, setShowSelector] = useState(false);

  // Derive available and selected lists from props (no internal state needed)
  const { available, selected } = useMemo(() => {
    if (!availableFilamentTypes.length) {
      return { available: [], selected: [] };
    }
    const selectedSet = new Set(selectedFilamentTypes);
    const selectedTypes = availableFilamentTypes.filter(ft => selectedSet.has(ft.name));
    const availableTypes = availableFilamentTypes.filter(ft => !selectedSet.has(ft.name));
    return { available: availableTypes, selected: selectedTypes };
  }, [availableFilamentTypes, selectedFilamentTypes]);

  const handleMoveToSelected = (filamentType: FilamentTypeDto) => {
    const newSelection = [...selectedFilamentTypes, filamentType.name];
    onSelectionChange(newSelection);
  };

  const handleMoveToAvailable = (filamentType: FilamentTypeDto) => {
    const newSelection = selectedFilamentTypes.filter(name => name !== filamentType.name);
    onSelectionChange(newSelection);
  };

  const handleMoveAllToSelected = () => {
    const allNames = availableFilamentTypes.map(ft => ft.name);
    onSelectionChange(allNames);
  };

  const handleMoveAllToAvailable = () => {
    onSelectionChange([]);
  };

  // Improved display text that shows actual matched filament type names
  const getDisplayText = () => {
    if (!availableFilamentTypes.length) {
      return selectedFilamentTypes.length > 0 
        ? `${selectedFilamentTypes.length} materials selected (loading details...)` 
        : 'No materials selected';
    }
    
    if (selectedFilamentTypes.length === 0) {
      return 'No materials selected';
    }
    
    // Find the actual FilamentTypeDto objects that match the selected names
    const selectedSet = new Set(selectedFilamentTypes);
    const matchedTypes = availableFilamentTypes.filter(ft => selectedSet.has(ft.name));
    
    if (matchedTypes.length === 0) {
      // Show the raw strings if no matches found (fallback)
      return `${selectedFilamentTypes.length} materials: ${selectedFilamentTypes.join(', ')}`;
    }
    
    // Show matched type names without temperature info for cleaner display
    if (matchedTypes.length <= 5) {
      return matchedTypes.map(ft => ft.name).join(', ');
    } else {
      // Show first few and count for many
      const first5 = matchedTypes.slice(0, 5).map(ft => ft.name).join(', ');
      return `${first5}, +${matchedTypes.length - 5} more`;
    }
  };

  const displayText = getDisplayText();

  if (!showSelector) {
    return (
      <div className={className ?? ''}>
        <div className="flex items-center gap-2">
          <div className="flex-1">
            <div className="px-3 py-2 rounded-lg bg-pf-panel border border-pf-border text-pf-text-primary min-h-[40px] flex items-center text-sm">
              {displayText}
            </div>
          </div>
          <Button
            size="sm"
            onClick={() => setShowSelector(true)}
            title="Configure supported materials"
            iconLeft={<SettingsIcon className="w-4 h-4" />}
          >
            Configure
          </Button>
        </div>
      </div>
    );
  }

  return (
    <div className={className}>
      <div className="space-y-4">
        <div className="flex items-center justify-between">
          <h5 className="text-sm font-medium text-pf-text-secondary">Configure Supported Materials</h5>
          <Button
            variant="subtle"
            size="sm"
            onClick={() => setShowSelector(false)}
          >
            Done
          </Button>
        </div>

        <div className="flex gap-4 items-stretch">
          {/* Available Materials */}
          <div className="flex-1 space-y-2">
            <h6 className="text-xs font-medium text-pf-text-secondary uppercase tracking-wide">
              Available Materials
            </h6>
            <div className="border border-pf-border rounded-lg bg-pf-panel h-48 overflow-y-auto">
              {available.length === 0 ? (
                <div className="p-4 text-center text-pf-text-tertiary text-sm">
                  All materials are selected
                </div>
              ) : (
                <div className="p-2">
                  {available.map((filamentType) => (
                    <Button
                      key={filamentType.id}
                      type="button"
                      onClick={() => handleMoveToSelected(filamentType)}
                      variant="subtle"
                      size="sm"
                      className="w-full justify-start border-l-2 border-transparent hover:border-pf-accent"
                      title={`Add ${filamentType.name}`}
                    >
                      {filamentType.name}
                    </Button>
                  ))}
                </div>
              )}
            </div>
          </div>

          {/* Transfer Controls */}
          <div className="flex flex-col justify-center items-center space-y-2 px-2">
            <Button
              onClick={handleMoveAllToSelected}
              disabled={available.length === 0}
              size="sm"
              title="Add all materials"
              iconCenter={<ChevronsRightIcon className="w-4 h-4" />}
            >
            </Button>
            <Button
              onClick={handleMoveAllToAvailable}
              disabled={selected.length === 0}
              size="sm"
              title="Remove all materials"
              iconCenter={<ChevronsLeftIcon className="w-4 h-4" />}
            >
            </Button>
          </div>

          {/* Selected Materials */}
          <div className="flex-1 space-y-2">
            <h6 className="text-xs font-medium text-pf-text-secondary uppercase tracking-wide">
              Supported Materials
            </h6>
            <div className="border border-pf-border rounded-lg bg-pf-panel h-48 overflow-y-auto">
              {selected.length === 0 ? (
                <div className="p-4 text-center text-pf-text-tertiary text-sm">
                  No materials selected
                </div>
              ) : (
                <div className="p-2">
                  {selected.map((filamentType) => (
                    <Button
                      key={filamentType.id}
                      type="button"
                      onClick={() => handleMoveToAvailable(filamentType)}
                      variant="subtle"
                      size="sm"
                      className="w-full justify-start border-r-2 border-transparent hover:border-pf-accent"
                      title={`Remove ${filamentType.name}`}
                    >
                      {filamentType.name}
                    </Button>
                  ))}
                </div>
              )}
            </div>
          </div>
        </div>

        <div className="text-xs text-pf-text-tertiary">
          Click materials to move between lists. Use transfer buttons to add/remove all materials at once.
        </div>
      </div>
    </div>
  );
}