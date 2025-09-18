import React, { useState, useEffect } from 'react';
import { Settings, ChevronsRight, ChevronsLeft } from 'lucide-react';
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
  className = ''
}: FilamentTypeSelectorProps) {
  const [showSelector, setShowSelector] = useState(false);
  const [available, setAvailable] = useState<FilamentTypeDto[]>([]);
  const [selected, setSelected] = useState<FilamentTypeDto[]>([]);

  // Update internal state when props change
  useEffect(() => {
    if (!availableFilamentTypes.length) return;

    const selectedSet = new Set(selectedFilamentTypes);
    const selectedTypes = availableFilamentTypes.filter(ft => selectedSet.has(ft.name));
    const availableTypes = availableFilamentTypes.filter(ft => !selectedSet.has(ft.name));

    setSelected(selectedTypes);
    setAvailable(availableTypes);
  }, [availableFilamentTypes, selectedFilamentTypes]);

  const handleMoveToSelected = (filamentType: FilamentTypeDto) => {
    setAvailable(prev => prev.filter(ft => ft.id !== filamentType.id));
    setSelected(prev => [...prev, filamentType]);
    
    const newSelection = [...selectedFilamentTypes, filamentType.name];
    onSelectionChange(newSelection);
  };

  const handleMoveToAvailable = (filamentType: FilamentTypeDto) => {
    setSelected(prev => prev.filter(ft => ft.id !== filamentType.id));
    setAvailable(prev => [...prev, filamentType].sort((a, b) => a.name.localeCompare(b.name)));
    
    const newSelection = selectedFilamentTypes.filter(name => name !== filamentType.name);
    onSelectionChange(newSelection);
  };

  const handleMoveAllToSelected = () => {
    const allSelected = [...selected, ...available].sort((a, b) => a.name.localeCompare(b.name));
    setSelected(allSelected);
    setAvailable([]);
    onSelectionChange(allSelected.map(ft => ft.name));
  };

  const handleMoveAllToAvailable = () => {
    const allAvailable = [...available, ...selected].sort((a, b) => a.name.localeCompare(b.name));
    setAvailable(allAvailable);
    setSelected([]);
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
      <div className={className}>
        <div className="flex items-center space-x-2">
          <div className="flex-1">
            <div className="px-3 py-2 rounded-lg bg-pf-panel border border-pf-border text-pf-text-primary min-h-[40px] flex items-center">
              {displayText}
            </div>
          </div>
          <button
            type="button"
            onClick={() => setShowSelector(true)}
            className="px-3 py-2 rounded-lg bg-pf-accent hover:bg-pf-success-hover text-white flex items-center text-sm transition-colors"
            title="Configure supported materials"
          >
            <Settings className="w-4 h-4 mr-1" />
            Configure
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className={className}>
      <div className="space-y-4">
        <div className="flex items-center justify-between">
          <h5 className="text-sm font-medium text-pf-text-secondary">Configure Supported Materials</h5>
          <button
            type="button"
            onClick={() => setShowSelector(false)}
            className="text-sm text-pf-text-tertiary hover:text-pf-text-primary transition-colors"
          >
            Done
          </button>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          {/* Available Materials */}
          <div className="space-y-2">
            <div className="flex items-center justify-between">
              <h6 className="text-xs font-medium text-pf-text-secondary uppercase tracking-wide">
                Available Materials
              </h6>
              <button
                type="button"
                onClick={handleMoveAllToSelected}
                disabled={available.length === 0}
                className="text-xs text-pf-accent hover:text-pf-success-hover disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                title="Add all materials"
              >
                Add All
              </button>
            </div>
            <div className="border border-pf-border rounded-lg bg-pf-panel h-48 overflow-y-auto">
              {available.length === 0 ? (
                <div className="p-4 text-center text-pf-text-tertiary text-sm">
                  All materials are selected
                </div>
              ) : (
                <div className="p-2">
                  {available.map((filamentType) => (
                    <button
                      key={filamentType.id}
                      type="button"
                      onClick={() => handleMoveToSelected(filamentType)}
                      className="w-full text-left px-3 py-2 rounded-lg hover:bg-pf-bg-2 text-pf-text-primary text-sm transition-colors border-l-2 border-transparent hover:border-pf-accent"
                      title={`Add ${filamentType.name} (${filamentType.defaultTemperatures.hotend}°C / ${filamentType.defaultTemperatures.bed}°C)`}
                    >
                      <div className="flex items-center justify-between">
                        <span className="font-medium">{filamentType.name}</span>
                        <span className="text-xs text-pf-text-tertiary">
                          {filamentType.defaultTemperatures.hotend}°C / {filamentType.defaultTemperatures.bed}°C
                        </span>
                      </div>
                    </button>
                  ))}
                </div>
              )}
            </div>
          </div>

          {/* Transfer Controls */}
          <div className="flex flex-col justify-center items-center space-y-4">
            <button
              type="button"
              onClick={handleMoveAllToSelected}
              disabled={available.length === 0}
              className="w-full px-3 py-2 bg-pf-accent hover:bg-pf-success-hover disabled:opacity-50 disabled:cursor-not-allowed text-white rounded-lg flex items-center justify-center text-sm transition-colors"
              title="Add all materials"
            >
              <ChevronsRight className="w-5 h-5 stroke-[3.5] mr-2" />
            </button>
            <button
              type="button"
              onClick={handleMoveAllToAvailable}
              disabled={selected.length === 0}
              className="w-full px-3 py-2 bg-pf-accent hover:bg-pf-success-hover disabled:opacity-50 disabled:cursor-not-allowed text-white rounded-lg flex items-center justify-center text-sm transition-colors"
              title="Remove all materials"
            >
              <ChevronsLeft className="w-5 h-5 stroke-[3.5] mr-2" />
            </button>
          </div>

          {/* Selected Materials */}
          <div className="space-y-2">
            <div className="flex items-center justify-between">
              <h6 className="text-xs font-medium text-pf-text-secondary uppercase tracking-wide">
                Supported Materials
              </h6>
              <button
                type="button"
                onClick={handleMoveAllToAvailable}
                disabled={selected.length === 0}
                className="text-xs text-pf-accent hover:text-pf-success-hover disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                title="Remove all materials"
              >
                Remove All
              </button>
            </div>
            <div className="border border-pf-border rounded-lg bg-pf-panel h-48 overflow-y-auto">
              {selected.length === 0 ? (
                <div className="p-4 text-center text-pf-text-tertiary text-sm">
                  No materials selected
                </div>
              ) : (
                <div className="p-2">
                  {selected.map((filamentType) => (
                    <button
                      key={filamentType.id}
                      type="button"
                      onClick={() => handleMoveToAvailable(filamentType)}
                      className="w-full text-left px-3 py-2 rounded-lg hover:bg-pf-bg-2 text-pf-text-primary text-sm transition-colors border-r-2 border-transparent hover:border-pf-accent"
                      title={`Remove ${filamentType.name} (${filamentType.defaultTemperatures.hotend}°C / ${filamentType.defaultTemperatures.bed}°C)`}
                    >
                      <div className="flex items-center justify-between">
                        <span className="font-medium">{filamentType.name}</span>
                        <span className="text-xs text-pf-text-tertiary">
                          {filamentType.defaultTemperatures.hotend}°C / {filamentType.defaultTemperatures.bed}°C
                        </span>
                      </div>
                    </button>
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