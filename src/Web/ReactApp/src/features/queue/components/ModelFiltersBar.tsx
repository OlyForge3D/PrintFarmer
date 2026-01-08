import { Button } from "@/common/components/ui/Button";
import { Select } from "@/common/components/ui/Select";
import { ChangeEvent } from "react";

type JobStatus = "queued" | "printing" | "paused" | "completed" | "failed";

interface ModelFiltersBarProps {
  models: string[];
  selectedModel: string | null;
  onModelChange: (model: string | null) => void;
  selectedStatuses: JobStatus[];
  onStatusChange: (statuses: JobStatus[]) => void;
  sortBy: "name" | "queue" | "waitTime" | "printing";
  onSortChange: (sort: "name" | "queue" | "waitTime" | "printing") => void;
  onRefresh: () => void;
  isLoading: boolean;
}

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
          <label className="block text-pf-text-secondary text-sm mb-1">
            Printer Model
          </label>
          <Select
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
          <label className="block text-pf-text-secondary text-sm mb-1">
            Sort By
          </label>
          <Select
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
        <div className="flex items-end">
          <Button
            onClick={onRefresh}
            disabled={isLoading}
            variant="secondary"
            className="w-full sm:w-auto"
          >
            {isLoading ? "Loading..." : "🔄 Refresh"}
          </Button>
        </div>
      </div>

      {/* Status Filter Toggle Buttons */}
      <div>
        <label className="block text-pf-text-secondary text-sm mb-2">
          Job Status
        </label>
        <div className="flex flex-wrap gap-2">
          {(["queued", "printing", "paused"] as JobStatus[]).map((status) => (
            <button
              key={status}
              onClick={() => handleStatusToggle(status)}
              disabled={isLoading}
              className={`px-3 py-1 rounded-full text-sm font-medium transition-colors ${
                selectedStatuses.includes(status)
                  ? status === "queued"
                    ? "bg-pf-info text-white"
                    : status === "printing"
                      ? "bg-pf-success text-white"
                      : "bg-pf-warning text-white"
                  : "bg-pf-bg-0 border border-pf-border text-pf-text-secondary hover:bg-pf-bg-1"
              } ${isLoading ? "opacity-50 cursor-not-allowed" : ""}`}
            >
              {status === "queued"
                ? `⏳ Queued (${selectedStatuses.includes("queued") ? "✓" : ""})`
                : status === "printing"
                  ? `⏱️ Printing (${selectedStatuses.includes("printing") ? "✓" : ""})`
                  : `⏸️ Paused (${selectedStatuses.includes("paused") ? "✓" : ""})`}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}
