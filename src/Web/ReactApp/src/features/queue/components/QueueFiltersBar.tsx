import { RefreshIcon, ClearFiltersIcon } from "@/common/components/icons/MdiIcons";
import { Button, Select, Input } from "@/common/components/ui";
import { useState, useCallback } from "react";

export interface TableFiltersBarProps {
  onStatusChange: (status: string | null) => void;
  onModelChange: (model: string | null) => void;
  onMaterialChange: (material: string | null) => void;
  onSortChange: (sortBy: "priority" | "deadline" | "deadline_desc") => void;
  onRefresh: () => void;
  isLoading?: boolean;
}

// Terminal states (Completed/Failed/Cancelled) are intentionally excluded here: the Print
// Queue tab shows active work only. Finished jobs live on the History tab, so exposing those
// filters here duplicated History and let the queue show terminal jobs.
const STATUS_OPTIONS = [
  { value: "Queued", label: "Queued" },
  { value: "Assigned", label: "Assigned" },
  { value: "Starting", label: "Starting" },
  { value: "Printing", label: "Printing" },
  { value: "Paused", label: "Paused" },
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
  onSortChange,
  onRefresh,
  isLoading = false,
}: TableFiltersBarProps) {
  const [selectedStatus, setSelectedStatus] = useState<string | null>(null);
  const [selectedMaterial, setSelectedMaterial] = useState<string | null>(null);
  const [selectedModel, setSelectedModel] = useState<string | null>(null);
  const [selectedSortBy, setSelectedSortBy] = useState<"priority" | "deadline" | "deadline_desc">("priority");

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
    setSelectedSortBy("priority");
    onStatusChange(null);
    onMaterialChange(null);
    onModelChange(null);
    onSortChange("priority");
  }, [onStatusChange, onMaterialChange, onModelChange, onSortChange]);

  const handleSortChange = useCallback(
    (e: React.ChangeEvent<HTMLSelectElement>) => {
      const value = (e.target.value || "priority") as "priority" | "deadline" | "deadline_desc";
      setSelectedSortBy(value);
      onSortChange(value);
    },
    [onSortChange]
  );

  const hasActiveFilters = selectedStatus || selectedMaterial || selectedModel || selectedSortBy !== "priority";

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

      <Select
        value={selectedSortBy}
        onChange={handleSortChange}
        className="w-40 text-sm"
        aria-label="Sort queue jobs"
      >
        <option value="priority">Sort: Priority</option>
        <option value="deadline">Sort: Deadline (Soonest)</option>
        <option value="deadline_desc">Sort: Deadline (Latest)</option>
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
