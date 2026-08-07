import { Button } from "@/common/components/ui/Button";
import { Select } from "@/common/components/ui/Select";
import { RefreshIcon, ClearFiltersIcon } from "@/common/components/icons/MdiIcons";
import { ChangeEvent } from "react";
import type { JobStatus } from "@/types/queue";
import type { ModelFiltersBarProps } from "@/types/components";

/**
 * ModelFiltersBar Component
 *
 * Provides filter controls for the "By Model" tab:
 * - Select printer model to view
 * - Filter by job status (queued, printing, paused)
 * - Sort by various criteria
 * - Refresh button to reload data
 */
export default function ModelFiltersBar({
  models,
  selectedModel,
  onModelChange,
  selectedStatuses,
  onStatusChange,
  sortBy,
  onSortChange,
  onRefresh,
  isLoading,
}: ModelFiltersBarProps) {
  // Toggle status filter
  const handleStatusToggle = (status: JobStatus) => {
    const newStatuses = selectedStatuses.includes(status)
      ? selectedStatuses.filter((s) => s !== status)
      : [...selectedStatuses, status];

    // Ensure at least one status is selected
    if (newStatuses.length > 0) {
      onStatusChange(newStatuses);
    }
  };

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4 space-y-3">
      {/* Top Row: Dropdowns */}
      <div className="flex flex-col sm:flex-row gap-3">
        {/* Model Select */}
        <div className="flex-1">
          <label htmlFor="model-filter" className="block text-pf-text-secondary text-sm mb-1">
            Printer Model
          </label>
          <Select
            id="model-filter"
            value={selectedModel || ""}
            onChange={(e: ChangeEvent<HTMLSelectElement>) => onModelChange(e.target.value || null)}
            disabled={isLoading}
            className="w-full"
          >
            <option value="">All Models ({models.length})</option>
            {models.map((model) => (
              <option key={model} value={model}>
                {model}
              </option>
            ))}
          </Select>
        </div>

        {/* Sort Select */}
        <div className="flex-1">
          <label htmlFor="model-sort" className="block text-pf-text-secondary text-sm mb-1">
            Sort By
          </label>
          <Select
            id="model-sort"
            value={sortBy}
            onChange={(e: ChangeEvent<HTMLSelectElement>) =>
              onSortChange(
                e.target.value as "name" | "queue" | "waitTime" | "printing"
              )
            }
            disabled={isLoading}
            className="w-full"
          >
            <option value="name">Model Name</option>
            <option value="queue">Queue Size (Largest First)</option>
            <option value="waitTime">Wait Time (Longest First)</option>
            <option value="printing">Printing Count (Most First)</option>
          </Select>
        </div>

        {/* Refresh Button */}
        <div className="flex items-end gap-2">
          <Button
            onClick={onRefresh}
            disabled={isLoading}
            variant="secondary"
            iconCenter={<RefreshIcon />}
            title="Refresh data"
          >
          </Button>
          <Button
            onClick={() => {
              onModelChange(null);
              onStatusChange(["queued", "printing", "paused"]);
              onSortChange("name");
            }}
            disabled={isLoading}
            variant="secondary"
            iconCenter={<ClearFiltersIcon />}
            title="Reset all filters"
          >
          </Button>
        </div>
      </div>

      {/* Status Filter Toggle Buttons */}
      <div>
        <span id="model-status-label" className="block text-pf-text-secondary text-sm mb-2">
          Job Status
        </span>
        <div className="flex flex-wrap gap-2" role="group" aria-labelledby="model-status-label">
          {(["queued", "printing", "paused"] as JobStatus[]).map((status) => (
            <Button
              key={status}
              onClick={() => handleStatusToggle(status)}
              disabled={isLoading}
              variant="subtle"
              aria-pressed={selectedStatuses.includes(status)}
              aria-label={status.charAt(0).toUpperCase() + status.slice(1)}
              className={`px-3 py-1 rounded-sm text-sm font-medium transition-colors ${
                selectedStatuses.includes(status)
                  ? `${
                      status === "queued"
                        ? "bg-pf-info"
                        : status === "printing"
                          ? "bg-pf-success"
                          : "bg-pf-warning"
                    } text-[var(--pf-text-inverse)] enabled:hover:ring-1 enabled:hover:ring-inset enabled:hover:ring-[var(--pf-text-inverse)]`
                  : "bg-pf-bg-0 border border-pf-border text-pf-text-secondary enabled:hover:bg-pf-bg-1"
              } ${selectedStatuses.includes(status) ? "enabled:hover:scale-105 enabled:hover:shadow-sm" : ""} ${isLoading ? "opacity-50 cursor-not-allowed" : ""}`}
            >
              {status === "queued"
                ? `⏳ Queued (${selectedStatuses.includes("queued") ? "✓" : ""})`
                : status === "printing"
                  ? `⏱️ Printing (${selectedStatuses.includes("printing") ? "✓" : ""})`
                  : `⏸️ Paused (${selectedStatuses.includes("paused") ? "✓" : ""})`}
            </Button>
          ))}
        </div>
      </div>
    </div>
  );
}
