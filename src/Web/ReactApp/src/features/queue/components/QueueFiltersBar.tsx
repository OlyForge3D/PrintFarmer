import { RefreshIcon, ClearFiltersIcon, ChevronDownIcon, ChevronUpIcon } from "@/common/components/icons/MdiIcons";
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
  const [isExpanded, setIsExpanded] = useState(true);

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
    <div className="bg-pf-bg-1 border border-pf-border rounded-lg overflow-hidden">
      {/* Header with Collapse/Expand Toggle */}
      <Button
        onClick={() => setIsExpanded(!isExpanded)}
        className="w-full flex items-center justify-between p-4 bg-pf-bg-2 hover:bg-pf-bg-1 transition-colors"
        type="button"
        variant="ghost"
      >
        <h3 className="font-semibold text-pf-text-primary flex-1 text-left">FILTERS</h3>
        {isExpanded ? (
          <ChevronUpIcon className="w-5 h-5 text-pf-text-secondary" />
        ) : (
          <ChevronDownIcon className="w-5 h-5 text-pf-text-secondary" />
        )}
      </Button>

      {/* Collapsible Filter Content */}
      {isExpanded && (
        <div className="p-4 space-y-4 border-t border-pf-border">
          {/* Status Filter */}
          <div className="flex items-center justify-between gap-4">
            <label className="text-sm font-medium text-pf-text-primary w-32">Status</label>
            <Select
              value={selectedStatus || ""}
              onChange={handleStatusChange}
              className="flex-1"
            >
              <option value="">All Statuses</option>
              {STATUS_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </Select>
          </div>

          {/* Model Filter */}
          <div className="flex items-center justify-between gap-4">
            <label className="text-sm font-medium text-pf-text-primary w-32">Printer Model</label>
            <Input
              type="text"
              value={selectedModel || ""}
              onChange={handleModelChange}
              placeholder="Search by model..."
              className="flex-1"
            />
          </div>

          {/* Material Filter */}
          <div className="flex items-center justify-between gap-4">
            <label className="text-sm font-medium text-pf-text-primary w-32">Material</label>
            <Select
              value={selectedMaterial || ""}
              onChange={handleMaterialChange}
              className="flex-1"
            >
              <option value="">All Materials</option>
              {MATERIAL_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </Select>
          </div>

          {/* Action Buttons */}
          <div className="flex gap-2 pt-2 border-t border-pf-border">
            <Button
              onClick={onRefresh}
              disabled={isLoading}
              variant="secondary"
              className="flex-1"
              iconCenter={<RefreshIcon />}
              title="Refresh data"
            >
            </Button>
            <Button
              onClick={handleClearFilters}
              variant="secondary"
              className="flex-1"
              iconCenter={<ClearFiltersIcon />}
              title="Reset all filters"
            >
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
