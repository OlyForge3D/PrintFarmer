import { RefreshIcon, ClearFiltersIcon } from "@/common/components/icons/MdiIcons";
import { Button, Select, Input } from "@/common/components/ui";
import { useState, useCallback } from "react";

export interface TableFiltersBarProps {
  onStatusChange: (status: string | null) => void;
  onModelChange: (model: string | null) => void;
  onMaterialChange: (material: string | null) => void;
  onRefresh: () => void;
  isLoading?: boolean;
}

const STATUS_OPTIONS = [
  { value: "Queued", label: "Queued" },
  { value: "Assigned", label: "Assigned" },
  { value: "Starting", label: "Starting" },
  { value: "Printing", label: "Printing" },
  { value: "Paused", label: "Paused" },
  { value: "Completed", label: "Completed" },
  { value: "Failed", label: "Failed" },
  { value: "Cancelled", label: "Cancelled" },
];

const MATERIAL_OPTIONS = [
  { value: "PLA", label: "PLA" },
  { value: "PETG", label: "PETG" },
  { value: "ABS", label: "ABS" },
  { value: "TPU", label: "TPU" },
  { value: "Nylon", label: "Nylon" },
];

export function TableFiltersBar({
  onStatusChange,
  onModelChange,
  onMaterialChange,
  onRefresh,
  isLoading = false,
}: TableFiltersBarProps) {
  const [selectedStatus, setSelectedStatus] = useState<string | null>(null);
  const [selectedMaterial, setSelectedMaterial] = useState<string | null>(null);
  const [selectedModel, setSelectedModel] = useState<string | null>(null);

  const handleStatusChange = useCallback(
    (e: React.ChangeEvent<HTMLSelectElement>) => {
      const value = e.target.value || null;
      setSelectedStatus(value);
      onStatusChange(value);
    },
    [onStatusChange]
  );

  const handleMaterialChange = useCallback(
    (e: React.ChangeEvent<HTMLSelectElement>) => {
      const value = e.target.value || null;
      setSelectedMaterial(value);
      onMaterialChange(value);
    },
    [onMaterialChange]
  );

  const handleModelChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const value = e.target.value || null;
      setSelectedModel(value);
      onModelChange(value);
    },
    [onModelChange]
  );

  const handleClearFilters = useCallback(() => {
    setSelectedStatus(null);
    setSelectedMaterial(null);
    setSelectedModel(null);
    onStatusChange(null);
    onMaterialChange(null);
    onModelChange(null);
  }, [onStatusChange, onMaterialChange, onModelChange]);

  const hasActiveFilters = selectedStatus || selectedMaterial || selectedModel;

  return (
    <div className="flex items-center gap-3 bg-pf-bg-1 border border-pf-border rounded-lg px-3 py-2">
      <Select
        value={selectedStatus || ""}
        onChange={handleStatusChange}
        className="w-36 text-sm"
        aria-label="Filter by status"
      >
        <option value="">Active Jobs</option>
        {STATUS_OPTIONS.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </Select>

      <Input
        type="text"
        value={selectedModel || ""}
        onChange={handleModelChange}
        placeholder="Printer model..."
        className="w-40 text-sm"
        aria-label="Filter by printer model"
      />

      <Select
        value={selectedMaterial || ""}
        onChange={handleMaterialChange}
        className="w-36 text-sm"
        aria-label="Filter by material"
      >
        <option value="">All Materials</option>
        {MATERIAL_OPTIONS.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </Select>

      <div className="flex items-center gap-1 ml-auto">
        <Button
          onClick={onRefresh}
          disabled={isLoading}
          variant="subtle"
          size="sm"
          iconCenter={<RefreshIcon />}
          title="Refresh data"
          aria-label="Refresh data"
        />
        {hasActiveFilters && (
          <Button
            onClick={handleClearFilters}
            variant="subtle"
            size="sm"
            iconCenter={<ClearFiltersIcon />}
            title="Reset all filters"
            aria-label="Reset all filters"
          />
        )}
      </div>
    </div>
  );
}
