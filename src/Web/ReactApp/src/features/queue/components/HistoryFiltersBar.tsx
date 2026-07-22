import { useCallback, useState } from "react";
import { Button } from "@/common/components/ui/Button";
import { Select } from "@/common/components/ui/Select";

interface HistoryFiltersBarProps {
  selectedStatuses: string[];
  onStatusChange: (statuses: string[]) => void;
  sortBy: "newest" | "oldest" | "duration" | "model";
  onSortChange: (sort: "newest" | "oldest" | "duration" | "model") => void;
  onRefresh: () => Promise<void>;
  isLoading: boolean;
  viewMode: "cards" | "table";
  onViewModeChange: (mode: "cards" | "table") => void;
}

/**
 * HistoryFiltersBar Component
 *
 * Provides filtering controls for job history:
 * - Status filter (Completed, Failed, Cancelled)
 * - Sort order selector
 * - View mode toggle (cards / table)
 * - Refresh button
 *
 * Date range is controlled globally by QueueDateRangeBar above the tab strip.
 */
export default function HistoryFiltersBar({
  selectedStatuses,
  onStatusChange,
  sortBy,
  onSortChange,
  onRefresh,
  isLoading,
  viewMode,
  onViewModeChange,
}: HistoryFiltersBarProps) {
  const [isExpanded, setIsExpanded] = useState(false);

  const handleStatusToggle = useCallback(
    (status: string) => {
      if (selectedStatuses.includes(status)) {
        onStatusChange(selectedStatuses.filter(s => s !== status));
      } else {
        onStatusChange([...selectedStatuses, status]);
      }
    },
    [selectedStatuses, onStatusChange]
  );

  // Build filter summary for collapsed view
  const getFilterSummary = () => {
    if (selectedStatuses.length === 3) return "All statuses";
    if (selectedStatuses.length > 0) {
      return selectedStatuses.map(s => s.charAt(0).toUpperCase() + s.slice(1)).join(", ");
    }
    return "No statuses selected";
  };

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-lg">
      {/* Collapsed Header - Always Visible */}
      <div className="p-3 flex items-center justify-between gap-4">
        <Button
          onClick={() => setIsExpanded(!isExpanded)}
          variant="ghost"
          className="flex items-center gap-2 text-left flex-1 min-w-0 justify-start px-0 hover:bg-transparent"
          aria-expanded={isExpanded}
          aria-controls="filter-content"
        >
          <span className="text-pf-text-secondary transition-transform duration-200" style={{ transform: isExpanded ? 'rotate(90deg)' : 'rotate(0deg)' }}>
            ▶
          </span>
          <span className="text-sm font-medium text-pf-text-primary">Filters</span>
          <span className="text-xs text-pf-text-secondary truncate">{getFilterSummary()}</span>
        </Button>
        
        {/* Quick actions always visible */}
        <div className="flex items-center gap-2 shrink-0">
          {/* View Toggle */}
          <div className="hidden sm:flex rounded-sm border border-pf-border overflow-hidden shrink-0">
            <Button
              onClick={() => onViewModeChange("cards")}
              variant="ghost"
              size="sm"
              className={`px-2 py-1 text-xs rounded-none ${
                viewMode === "cards"
                  ? "bg-pf-accent text-white hover:bg-pf-accent"
                  : "bg-pf-bg-0 text-pf-text-secondary hover:bg-pf-bg-2"
              }`}
              title="Card view"
            >
              ▦
            </Button>
            <Button
              onClick={() => onViewModeChange("table")}
              variant="ghost"
              size="sm"
              className={`px-2 py-1 text-xs rounded-none ${
                viewMode === "table"
                  ? "bg-pf-accent text-white hover:bg-pf-accent"
                  : "bg-pf-bg-0 text-pf-text-secondary hover:bg-pf-bg-2"
              }`}
              title="Table view"
            >
              ☰
            </Button>
          </div>
          <Select
            value={sortBy}
            onChange={(e: React.ChangeEvent<HTMLSelectElement>) =>
              onSortChange(e.target.value as "newest" | "oldest" | "duration" | "model")
            }
            className="text-xs py-1 px-2 w-auto shrink-0"
          >
            <option value="newest">Newest</option>
            <option value="oldest">Oldest</option>
            <option value="duration">Duration</option>
            <option value="model">Model</option>
          </Select>
          <Button
            onClick={onRefresh}
            disabled={isLoading}
            variant="secondary"
            size="sm"
            className="px-3 py-1 text-xs"
          >
            {isLoading ? "..." : "↻"}
          </Button>
        </div>
      </div>

      {/* Expandable Content */}
      {isExpanded && (
        <div id="filter-content" className="px-3 pb-3 pt-0 border-t border-pf-border space-y-3">
          <div className="pt-3">
            {/* Status Filter */}
            <div>
              <label className="block text-xs font-medium text-pf-text-secondary mb-1.5">
                Status
              </label>
              <div className="flex gap-1.5 flex-wrap">
                {["completed", "failed", "cancelled"].map((status) => (
                  <Button
                    key={status}
                    onClick={() => handleStatusToggle(status)}
                    variant="ghost"
                    size="sm"
                    className={`px-2 py-1 text-xs font-medium ${
                      selectedStatuses.includes(status)
                        ? status === "completed"
                          ? "bg-pf-success text-white hover:bg-pf-success"
                          : status === "failed"
                          ? "bg-pf-error text-white hover:bg-pf-error"
                          : "bg-pf-warning text-white hover:bg-pf-warning"
                        : "bg-pf-bg-0 border border-pf-border text-pf-text-secondary hover:bg-pf-bg-2"
                    }`}
                  >
                    {status === "completed" && "✓ Done"}
                    {status === "failed" && "✗ Failed"}
                    {status === "cancelled" && "◯ Cancelled"}
                  </Button>
                ))}
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
