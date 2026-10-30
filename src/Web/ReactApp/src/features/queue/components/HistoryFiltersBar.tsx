import { useCallback } from "react";
import { Button } from "@/common/components/ui/Button";
import { Select } from "@/common/components/ui/Select";
import { Toolbar, FiltersDisclosure, RefreshButton, ViewToggle, ControlIcon } from "@/common/components/ui";
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

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-lg">
      {/* Collapsed Header - Always Visible */}
      <Toolbar aria-label="History controls" className="p-3">
          <Select
            value={sortBy}
            onChange={(e: React.ChangeEvent<HTMLSelectElement>) =>
              onSortChange(e.target.value as "newest" | "oldest" | "duration" | "model")
            }
            containerClassName="w-auto"
            className="pf-control py-0"
            data-role="field"
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
              { mode: 'cards', kind: 'cards', title: 'Card view' },
              { mode: 'table', kind: 'table', title: 'Table view' },
            ]} />
          <RefreshButton onClick={onRefresh} busy={isLoading} />
      </Toolbar>

      <Toolbar aria-label="History filters">
        <FiltersDisclosure
          activeCount={selectedStatuses.length === 3 ? 0 : selectedStatuses.length}
          onReset={() => onStatusChange(["completed", "failed", "cancelled"])}
        >
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
                    control
                    aria-pressed={selectedStatuses.includes(status)}
                    aria-label={
                      status === "completed"
                        ? "Done"
                        : status === "failed"
                          ? "Failed"
                          : "Cancelled"
                    }
                    className={`px-2 text-xs font-medium ${
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
        </FiltersDisclosure>
      </Toolbar>
    </div>
  );
}
