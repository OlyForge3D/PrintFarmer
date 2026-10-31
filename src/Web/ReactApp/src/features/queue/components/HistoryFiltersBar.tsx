import { useCallback } from "react";
import { Button } from "@/common/components/ui/Button";
import { Select } from "@/common/components/ui/Select";
import { Toolbar, FiltersDisclosure, RefreshButton, ViewToggle, ControlIcon } from "@/common/components/ui";
import { mdiCheck, mdiClose, mdiCircleOutline } from '@mdi/js';
import type { HistorySortMode } from "./history-sort";

interface HistoryFiltersBarProps {
  selectedStatuses: string[];
  onStatusChange: (statuses: string[]) => void;
  sortBy: HistorySortMode;
  onSortChange: (sort: HistorySortMode) => void;
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
 * Date range is controlled by QueueDateRangeBar inside the History panel.
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
              onSortChange(e.target.value as HistorySortMode)
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
            <option value="model_desc">Model (descending)</option>
            <option value="name">Job name</option>
            <option value="name_desc">Job name (descending)</option>
            <option value="printer">Printer</option>
            <option value="printer_desc">Printer (descending)</option>
            <option value="material">Material</option>
            <option value="material_desc">Material (descending)</option>
            <option value="status">Status</option>
            <option value="status_desc">Status (descending)</option>
            <option value="filament">Filament</option>
            <option value="filament_desc">Filament (descending)</option>
            <option value="cost">Cost</option>
            <option value="cost_desc">Cost (descending)</option>
            <option value="duration_asc">Duration (ascending)</option>
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
        activeCount={3 - selectedStatuses.length}
        onReset={() => onStatusChange(["completed", "failed", "cancelled"])}
          controls={trigger => (
            <Toolbar.Controls>
              {trigger}
            </Toolbar.Controls>
          )}
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
