import { useCallback, useState } from "react";
import { Button } from "@/common/components/ui/Button";
import { Select } from "@/common/components/ui/Select";
import { Toolbar, FilterButton, RefreshButton, ViewToggle, ControlIcon } from "@/common/components/ui";
import { mdiCheck, mdiClose, mdiCircleOutline } from '@mdi/js';

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
      <Toolbar aria-label="History controls" className="p-3">
        <FilterButton expanded={isExpanded} count={selectedStatuses.length === 3 ? 0 : selectedStatuses.length}
          onClick={() => setIsExpanded(!isExpanded)}
          variant="ghost"
          className="flex items-center gap-2 text-left flex-[1_1_14rem] min-w-0 justify-start px-0 hover:bg-transparent"
          aria-controls="filter-content"
        >
          <span className="inline-flex items-center gap-2 min-w-0">
            <span className="text-sm font-medium text-pf-text-primary">Filters</span>
            <span className="text-xs text-pf-text-secondary truncate">{getFilterSummary()}</span>
          </span>
        </FilterButton>
          <Select
            value={sortBy}
            onChange={(e: React.ChangeEvent<HTMLSelectElement>) =>
              onSortChange(e.target.value as "newest" | "oldest" | "duration" | "model")
            }
            containerClassName="w-auto"
            aria-label="Sort by"
          >
            <option value="newest">Newest</option>
            <option value="oldest">Oldest</option>
            <option value="duration">Duration</option>
            <option value="model">Model</option>
          </Select>
          <Toolbar.Spacer />
          <ViewToggle value={viewMode} onChange={onViewModeChange} size="md" className="pf-view-toggle--desktop"
            ariaLabel="History view mode" options={[
              { mode: 'cards', icon: 'grid', title: 'Card view' },
              { mode: 'table', icon: 'table', title: 'Table view' },
            ]} />
          <RefreshButton onClick={onRefresh} busy={isLoading} />
      </Toolbar>

      {/* Expandable Content */}
      {isExpanded && (
        <div id="filter-content" className="pf-inline-form px-3 pb-3 pt-0 border-t border-pf-border space-y-3">
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
                    aria-pressed={selectedStatuses.includes(status)}
                    aria-label={
                      status === "completed"
                        ? "Done"
                        : status === "failed"
                          ? "Failed"
                          : "Cancelled"
                    }
                    className={`px-2 py-1 text-xs font-medium ${
                      selectedStatuses.includes(status)
                        ? status === "completed"
                          ? "bg-pf-success text-[var(--pf-text-inverse)] enabled:hover:ring-1 enabled:hover:ring-inset enabled:hover:ring-[var(--pf-text-inverse)]"
                          : status === "failed"
                          ? "bg-pf-error text-[var(--pf-text-inverse)] enabled:hover:ring-1 enabled:hover:ring-inset enabled:hover:ring-[var(--pf-text-inverse)]"
                          : "bg-pf-warning text-[var(--pf-text-inverse)] enabled:hover:ring-1 enabled:hover:ring-inset enabled:hover:ring-[var(--pf-text-inverse)]"
                        : "bg-pf-bg-0 border border-pf-border text-pf-text-secondary hover:bg-pf-bg-2"
                    } ${selectedStatuses.includes(status) ? "enabled:hover:scale-105 enabled:hover:shadow-sm" : ""}`}
                  >
                    {status === "completed" && (
                      <>
                        <span className="inline-flex items-center gap-1"><ControlIcon path={mdiCheck} /> Done</span>
                      </>
                    )}
                    {status === "failed" && (
                      <>
                        <span className="inline-flex items-center gap-1"><ControlIcon path={mdiClose} /> Failed</span>
                      </>
                    )}
                    {status === "cancelled" && (
                      <>
                        <span className="inline-flex items-center gap-1"><ControlIcon path={mdiCircleOutline} /> Cancelled</span>
                      </>
                    )}
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
