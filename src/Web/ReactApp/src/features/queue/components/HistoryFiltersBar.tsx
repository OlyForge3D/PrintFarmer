import { useCallback, useState } from "react";
import { Button } from "@/common/components/ui/Button";
import { Select } from "@/common/components/ui/Select";

interface HistoryFiltersBarProps {
  selectedStatuses: string[];
  onStatusChange: (statuses: string[]) => void;
  dateStart: Date | null;
  onDateStartChange: (date: Date | null) => void;
  dateEnd: Date | null;
  onDateEndChange: (date: Date | null) => void;
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
 * - Date range picker (Start/End dates)
 * - Sort order selector
 * - Refresh button
 * 
 * Features collapsible design to save vertical space.
 */
export default function HistoryFiltersBar({
  selectedStatuses,
  onStatusChange,
  dateStart,
  onDateStartChange,
  dateEnd,
  onDateEndChange,
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

  const handleQuickDateRange = useCallback(
    (days: number | null) => {
      if (days === null) {
        onDateStartChange(null);
        onDateEndChange(null);
      } else {
        const end = new Date();
        const start = new Date();
        start.setDate(start.getDate() - days);
        onDateStartChange(start);
        onDateEndChange(end);
      }
    },
    [onDateStartChange, onDateEndChange]
  );

  const formatDateForInput = (date: Date | null): string => {
    if (!date) return "";
    return date.toISOString().split("T")[0];
  };

  const formatDateDisplay = (date: Date | null): string => {
    if (!date) return "Any";
    return date.toLocaleDateString();
  };

  // Build filter summary for collapsed view
  const getFilterSummary = () => {
    const parts: string[] = [];
    
    if (selectedStatuses.length === 3) {
      parts.push("All statuses");
    } else if (selectedStatuses.length > 0) {
      parts.push(selectedStatuses.map(s => s.charAt(0).toUpperCase() + s.slice(1)).join(", "));
    }
    
    if (dateStart || dateEnd) {
      parts.push(`${formatDateDisplay(dateStart)} – ${formatDateDisplay(dateEnd)}`);
    } else {
      parts.push("All time");
    }
    
    return parts.join(" • ");
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
          <div className="hidden sm:flex gap-1">
            {[
              { label: "7d", days: 7 },
              { label: "30d", days: 30 },
              { label: "90d", days: 90 },
              { label: "All", days: null },
            ].map((range) => (
              <Button
                key={range.label}
                onClick={() => handleQuickDateRange(range.days)}
                variant="ghost"
                size="sm"
                className="px-2 py-1 text-xs bg-pf-bg-2 text-pf-text-secondary hover:bg-pf-bg-3 hover:text-pf-text-primary"
              >
                {range.label}
              </Button>
            ))}
          </div>
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
          {/* Status + Date Range in one row on larger screens */}
          <div className="grid grid-cols-1 lg:grid-cols-3 gap-3 pt-3">
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

            {/* Date Range */}
            <div>
              <label className="block text-xs font-medium text-pf-text-secondary mb-1.5">
                From
              </label>
              <input
                type="date"
                value={formatDateForInput(dateStart)}
                onChange={(e) =>
                  onDateStartChange(
                    e.target.value ? new Date(e.target.value + "T00:00:00Z") : null
                  )
                }
                className="w-full px-2 py-1 text-sm border border-pf-border rounded-sm bg-pf-bg-0 text-pf-text-primary"
              />
            </div>
            <div>
              <label className="block text-xs font-medium text-pf-text-secondary mb-1.5">
                To
              </label>
              <input
                type="date"
                value={formatDateForInput(dateEnd)}
                onChange={(e) =>
                  onDateEndChange(
                    e.target.value ? new Date(e.target.value + "T23:59:59Z") : null
                  )
                }
                className="w-full px-2 py-1 text-sm border border-pf-border rounded-sm bg-pf-bg-0 text-pf-text-primary"
              />
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
