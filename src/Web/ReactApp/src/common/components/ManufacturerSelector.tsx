import React, { useMemo, useState, useCallback } from 'react';
import { Select, Input, Button, Modal } from '@/common/components/ui';
import { CatalogContext, ManufacturerWithCount } from '@/types/api';
import { useManufacturersByContext, useCreateManufacturer, useManufacturers } from '@/common/hooks/useApi';

/**
 * Props for ManufacturerSelector component
 */
export interface ManufacturerSelectorProps {
  /**
   * The currently selected manufacturer ID
   */
  value: string | undefined;

  /**
   * Callback when manufacturer selection changes
   */
  onChange: (manufacturerId: string | undefined, manufacturerName?: string) => void;

  /**
   * The catalog context to use for grouping (Hotends, Extruders, etc.)
   * When provided, manufacturers are grouped by "With Items" vs "All Others"
   */
  context?: CatalogContext;

  /**
   * Whether the selector is required (no empty option)
   */
  required?: boolean;

  /**
   * Whether the selector is disabled
   */
  disabled?: boolean;

  /**
   * Placeholder text when no selection
   */
  placeholder?: string;

  /**
   * Additional CSS class
   */
  className?: string;

  /**
   * Accessibility label
   */
  ariaLabel?: string;

  /**
   * Whether to show the "Add New" option
   */
  showAddNew?: boolean;
}

/**
 * Special value used to trigger the "Add New Manufacturer" modal
 */
const ADD_NEW_VALUE = '__add_new__';

/**
 * ManufacturerSelector - A dropdown for selecting manufacturers with optional contextual grouping
 * 
 * Features:
 * - When context is provided: Groups manufacturers as "With Items" vs "All Others"
 * - Shows item counts next to manufacturer names in contextual mode
 * - Optional "Add New Manufacturer" option that opens an inline modal
 * - Falls back to simple flat list when no context is provided
 */
export function ManufacturerSelector({
  value,
  onChange,
  context,
  required = false,
  disabled = false,
  placeholder = 'Select manufacturer...',
  className,
  ariaLabel = 'Manufacturer',
  showAddNew = true,
}: ManufacturerSelectorProps) {
  // Modal state for adding new manufacturer
  const [isAddModalOpen, setIsAddModalOpen] = useState(false);
  const [newManufacturerName, setNewManufacturerName] = useState('');
  const [addError, setAddError] = useState<string | null>(null);

  // Fetch contextual manufacturers when context is provided
  const contextualQuery = useManufacturersByContext(context!, {
    enabled: !!context,
  });

  // Fetch all manufacturers when no context is provided
  const allManufacturersQuery = useManufacturers({
    enabled: !context,
  });

  // Mutation for creating new manufacturer
  const createMutation = useCreateManufacturer();

  // Determine loading/error states based on which query is active
  const isLoading = context ? contextualQuery.isLoading : allManufacturersQuery.isLoading;
  const isError = context ? contextualQuery.isError : allManufacturersQuery.isError;

  // Build the grouped or flat options list
  const { options, selectedName } = useMemo(() => {
    let opts: { value: string; label: string; group?: string }[] = [];
    let selName: string | undefined;

    if (context && contextualQuery.data) {
      // Contextual mode: group by "With Items" and "All Others"
      const { withItems, withoutItems } = contextualQuery.data;

      // Add "With Items" group
      if (withItems.length > 0) {
        withItems.forEach((m: ManufacturerWithCount) => {
          opts.push({
            value: m.id,
            label: `${m.name} (${m.itemCount})`,
            group: 'With Items',
          });
          if (m.id === value) selName = m.name;
        });
      }

      // Add "All Others" group
      if (withoutItems.length > 0) {
        withoutItems.forEach((m: ManufacturerWithCount) => {
          opts.push({
            value: m.id,
            label: m.name,
            group: 'All Others',
          });
          if (m.id === value) selName = m.name;
        });
      }
    } else if (!context && allManufacturersQuery.data) {
      // Non-contextual mode: flat list of all manufacturers
      allManufacturersQuery.data.forEach(m => {
        opts.push({
          value: m.id,
          label: m.name,
        });
        if (m.id === value) selName = m.name;
      });
    }

    return { options: opts, selectedName: selName };
  }, [context, contextualQuery.data, allManufacturersQuery.data, value]);

  // Handle selection change
  const handleChange = useCallback((e: React.ChangeEvent<HTMLSelectElement>) => {
    const newValue = e.target.value;
    
    if (newValue === ADD_NEW_VALUE) {
      // Open add new modal
      setIsAddModalOpen(true);
      setNewManufacturerName('');
      setAddError(null);
      return;
    }

    if (newValue === '') {
      onChange(undefined, undefined);
    } else {
      // Find the name for the selected ID
      const selectedOpt = options.find(o => o.value === newValue);
      // Strip item count from label if present
      const name = selectedOpt?.label.replace(/\s*\(\d+\)$/, '');
      onChange(newValue, name);
    }
  }, [onChange, options]);

  // Handle creating new manufacturer
  const handleCreateManufacturer = useCallback(async () => {
    if (!newManufacturerName.trim()) {
      setAddError('Please enter a manufacturer name');
      return;
    }

    try {
      const newManufacturer = await createMutation.mutateAsync(newManufacturerName.trim());

      // Select the newly created manufacturer
      onChange(newManufacturer.id, newManufacturer.name);
      
      // Close modal
      setIsAddModalOpen(false);
      setNewManufacturerName('');
      setAddError(null);
    } catch (err) {
      setAddError(err instanceof Error ? err.message : 'Failed to create manufacturer');
    }
  }, [newManufacturerName, createMutation, onChange]);

  // Cancel adding new manufacturer
  const handleCancelAdd = useCallback(() => {
    setIsAddModalOpen(false);
    setNewManufacturerName('');
    setAddError(null);
  }, []);

  // Group options by their group property
  const groupedOptions = useMemo(() => {
    const groups: Map<string | undefined, typeof options> = new Map();
    
    options.forEach(opt => {
      const group = opt.group;
      if (!groups.has(group)) {
        groups.set(group, []);
      }
      groups.get(group)!.push(opt);
    });

    return groups;
  }, [options]);

  // Render loading/error states
  if (isLoading) {
    return (
      <Select
        value=""
        onChange={() => {}}
        aria-label={ariaLabel}
        className={className}
        disabled
      >
        <option value="">Loading manufacturers...</option>
      </Select>
    );
  }

  if (isError) {
    return (
      <Select
        value=""
        onChange={() => {}}
        aria-label={ariaLabel}
        className={className}
        disabled
      >
        <option value="">Error loading manufacturers</option>
      </Select>
    );
  }

  return (
    <>
      <Select
        value={value ?? ''}
        onChange={handleChange}
        aria-label={ariaLabel}
        className={className}
        required={required}
        disabled={disabled}
      >
        {/* Placeholder option when not required */}
        {!required && <option value="">{placeholder}</option>}

        {/* Render grouped options if we have groups */}
        {context && groupedOptions.size > 0 ? (
          <>
            {Array.from(groupedOptions.entries()).map(([group, groupOpts]) => (
              <optgroup key={group || 'ungrouped'} label={group || 'Other'}>
                {groupOpts.map(opt => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </optgroup>
            ))}
          </>
        ) : (
          /* Flat list without groups */
          options.map(opt => (
            <option key={opt.value} value={opt.value}>
              {opt.label}
            </option>
          ))
        )}

        {/* Add New option */}
        {showAddNew && (
          <option value={ADD_NEW_VALUE}>+ Add New Manufacturer...</option>
        )}
      </Select>

      {/* Add New Manufacturer Modal */}
      <Modal
        isOpen={isAddModalOpen}
        onClose={handleCancelAdd}
        title="Add New Manufacturer"
        size="sm"
      >
        <div className="space-y-4">
          <div>
            <label htmlFor="new-manufacturer-name" className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
              Manufacturer Name
            </label>
            <Input
              id="new-manufacturer-name"
              type="text"
              value={newManufacturerName}
              onChange={(e) => setNewManufacturerName(e.target.value)}
              placeholder="Enter manufacturer name..."
              autoFocus
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault();
                  handleCreateManufacturer();
                }
              }}
            />
            {addError && (
              <p className="mt-1 text-sm text-red-600 dark:text-red-400">{addError}</p>
            )}
          </div>

          <div className="flex justify-end gap-2">
            <Button variant="secondary" onClick={handleCancelAdd}>
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={handleCreateManufacturer}
              disabled={createMutation.isPending}
            >
              {createMutation.isPending ? 'Creating...' : 'Create'}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
}

export default ManufacturerSelector;
