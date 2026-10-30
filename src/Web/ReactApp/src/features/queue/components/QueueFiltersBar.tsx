import { Select, Input } from "@/common/components/ui";
import { FiltersDisclosure, Toolbar, RefreshButton } from "@/common/components/ui/toolbar";
import { useState, useCallback } from "react";
import type { ReactNode } from "react";

export interface TableFiltersBarProps {
  onStatusChange: (status: string | null) => void;
  onModelChange: (model: string | null) => void;
  onMaterialChange: (material: string | null) => void;
  onSortChange: (sortBy: "priority" | "deadline" | "deadline_desc") => void;
  onRefresh: () => void;
  isLoading?: boolean;
  viewControls?: ReactNode;
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
  viewControls,
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
    onStatusChange(null);
    onMaterialChange(null);
    onModelChange(null);
  }, [onStatusChange, onMaterialChange, onModelChange]);

  const handleSortChange = useCallback(
    (e: React.ChangeEvent<HTMLSelectElement>) => {
      const value = (e.target.value || "priority") as "priority" | "deadline" | "deadline_desc";
      setSelectedSortBy(value);
      onSortChange(value);
    },
    [onSortChange]
  );

  const activeFilterCount = [selectedStatus, selectedMaterial, selectedModel].filter(Boolean).length;

  return (
    <Toolbar aria-label="Queue controls" className="bg-pf-bg-1 border border-pf-border rounded-lg px-3 py-2">
      <FiltersDisclosure activeCount={activeFilterCount} onReset={handleClearFilters}>
        <div className="w-full sm:w-36 shrink-0">
          <Select
            value={selectedStatus || ""}
            onChange={handleStatusChange}
            className="pf-control w-full py-0 text-sm"
            data-role="field"
            aria-label="Filter by status"
          >
            <option value="">Active Jobs</option>
            {STATUS_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </Select>
        </div>

        <div className="w-full sm:w-40 shrink-0">
          <Input
            type="text"
            value={selectedModel || ""}
            onChange={handleModelChange}
            placeholder="Printer model..."
            className="pf-control w-full py-0 text-sm"
            data-role="field"
            aria-label="Filter by printer model"
          />
        </div>

        <div className="w-full sm:w-36 shrink-0">
          <Select
            value={selectedMaterial || ""}
            onChange={handleMaterialChange}
            className="pf-control w-full py-0 text-sm"
            data-role="field"
            aria-label="Filter by material"
          >
            <option value="">All Materials</option>
            {MATERIAL_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </Select>
        </div>
      </FiltersDisclosure>

      <div className="w-full sm:w-40 shrink-0">
        <Select
          value={selectedSortBy}
          onChange={handleSortChange}
          className="pf-control w-full py-0 text-sm"
          data-role="field"
          aria-label="Sort by"
        >
          <option value="priority">Sort: Priority</option>
          <option value="deadline">Sort: Deadline (Soonest)</option>
          <option value="deadline_desc">Sort: Deadline (Latest)</option>
        </Select>
      </div>

      <div className="flex flex-wrap items-center gap-1 w-full sm:w-auto sm:ml-auto justify-end shrink-0">
        {viewControls}
        <RefreshButton
          onClick={onRefresh}
          busy={isLoading}
          variant="subtle"
          title="Refresh data"
          aria-label="Refresh data"
        />
      </div>
    </Toolbar>
  );
}
