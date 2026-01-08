import { Button } from "@/common/components/ui/Button";
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

  return (
    <div className="bg-white border border-gray-200 rounded-lg p-4 mb-4">
      <div className="grid grid-cols-1 md:grid-cols-4 gap-4 items-end">
        {/* Status Filter */}
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-2">
            Status
          </label>
          {/* TODO: Replace with FormField + Select component */}
          <select
            value={selectedStatus || ""}
            onChange={handleStatusChange}
            className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-blue-500 focus:border-blue-500"
          >
            <option value="">All Statuses</option>
            {STATUS_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </div>

        {/* Model Filter */}
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-2">
            Printer Model
          </label>
          <input
            type="text"
            value={selectedModel || ""}
            onChange={handleModelChange}
            placeholder="Search by model..."
            className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-blue-500 focus:border-blue-500"
          />
        </div>

        {/* Material Filter */}
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-2">
            Material
          </label>
          {/* TODO: Replace with FormField + Select component */}
          <select
            value={selectedMaterial || ""}
            onChange={handleMaterialChange}
            className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-blue-500 focus:border-blue-500"
          >
            <option value="">All Materials</option>
            {MATERIAL_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </div>

        {/* Action Buttons */}
        <div className="flex gap-2">
          <Button
            onClick={onRefresh}
            disabled={isLoading}
            variant="secondary"
            className="flex-1"
          >
            {isLoading ? "Loading..." : "Refresh"}
          </Button>
          <Button
            onClick={handleClearFilters}
            variant="secondary"
            className="flex-1"
          >
            Clear
          </Button>
        </div>
      </div>
    </div>
  );
}
