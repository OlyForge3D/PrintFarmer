import { useCallback } from "react";
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
}

/**
 * HistoryFiltersBar Component
 *
 * Provides filtering controls for job history:
 * - Status filter (Completed, Failed, Cancelled)
 * - Date range picker (Start/End dates)
 * - Sort order selector
 * - Refresh button
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

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4 space-y-4">
      {/* Status Filter */}
      <div>
        <label className="block text-sm font-medium text-pf-text-primary mb-2">
          Status
        </label>
        <div className="flex gap-2 flex-wrap">
          {["completed", "failed", "cancelled"].map((status) => (
            <Button
              key={status}
              onClick={() => handleStatusToggle(status)}
              className={`px-3 py-1.5 rounded text-sm font-medium transition-colors ${
                selectedStatuses.includes(status)
                  ? status === "completed"
                    ? "bg-pf-success text-white"
                    : status === "failed"
                    ? "bg-pf-error text-white"
                    : "bg-pf-warning text-white"
                  : "bg-pf-bg-0 border border-pf-border text-pf-text-secondary hover:bg-pf-bg-2"
              }`}
              variant="subtle"
            >
              {status === "completed" && "✓ Completed"}
              {status === "failed" && "✗ Failed"}
              {status === "cancelled" && "◯ Cancelled"}
            </Button>
          ))}
        </div>
      </div>

      {/* Date Range Filter */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-2">
            From Date
          </label>
          <input
            type="date"
            value={formatDateForInput(dateStart)}
            onChange={(e) =>
              onDateStartChange(
                e.target.value ? new Date(e.target.value + "T00:00:00Z") : null
              )
            }
            className="w-full px-3 py-2 border border-pf-border rounded bg-pf-bg-0 text-pf-text-primary"
          />
        </div>
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-2">
            To Date
          </label>
          <input
            type="date"
            value={formatDateForInput(dateEnd)}
            onChange={(e) =>
              onDateEndChange(
                e.target.value ? new Date(e.target.value + "T23:59:59Z") : null
              )
            }
            className="w-full px-3 py-2 border border-pf-border rounded bg-pf-bg-0 text-pf-text-primary"
          />
        </div>
      </div>

      {/* Quick Date Range Buttons */}
      <div>
        <label className="block text-sm font-medium text-pf-text-primary mb-2">
          Quick Range
        </label>
        <div className="flex gap-2 flex-wrap">
          {[
            { label: "7 Days", days: 7 },
            { label: "30 Days", days: 30 },
            { label: "90 Days", days: 90 },
            { label: "All Time", days: null },
          ].map((range) => (
            <Button
              key={range.label}
              onClick={() => handleQuickDateRange(range.days)}
              className="px-3 py-1.5 rounded text-sm font-medium"
              variant="secondary"
            >
              {range.label}
            </Button>
          ))}
        </div>
      </div>

      {/* Sort and Refresh */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-2">
            Sort By
          </label>
          <Select
            value={sortBy}
            onChange={(e: React.ChangeEvent<HTMLSelectElement>) =>
              onSortChange(e.target.value as "newest" | "oldest" | "duration" | "model")
            }
          >
            <option value="newest">Newest First</option>
            <option value="oldest">Oldest First</option>
            <option value="duration">Duration (Long First)</option>
            <option value="model">Model Name</option>
          </Select>
        </div>
        <div className="flex items-end">
          <Button
            onClick={onRefresh}
            disabled={isLoading}
            variant="secondary"
            size="sm"
            className="w-full"
          >
            {isLoading ? "Refreshing..." : "Refresh"}
          </Button>
        </div>
      </div>
    </div>
  );
}
