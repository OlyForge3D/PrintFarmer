import { useCallback, useState } from "react";
import { Button } from "@/common/components/ui/Button";

export interface DateRange {
  from: Date | null;
  to: Date | null;
}

interface Preset {
  label: string;
  hours: number | null;
}

const PRESETS: Preset[] = [
  { label: "24h", hours: 24 },
  { label: "7d", hours: 24 * 7 },
  { label: "30d", hours: 24 * 30 },
  { label: "90d", hours: 24 * 90 },
  { label: "All", hours: null },
];

function buildRange(hours: number | null): DateRange {
  if (hours === null) return { from: null, to: null };
  const to = new Date();
  const from = new Date(to.getTime() - hours * 60 * 60 * 1000);
  return { from, to };
}

/** Returns which preset label matches the current range, or null if custom. */
function detectActivePreset(from: Date | null, to: Date | null): string | null {
  if (from === null && to === null) return "All";
  if (!from || !to) return null;
  const diffHours = (to.getTime() - from.getTime()) / (1000 * 60 * 60);
  for (const preset of PRESETS) {
    if (preset.hours !== null && Math.abs(diffHours - preset.hours) < 1) {
      return preset.label;
    }
  }
  return null;
}

function toInputValue(date: Date | null): string {
  if (!date) return "";
  return date.toISOString().split("T")[0];
}

function formatDisplay(date: Date | null): string {
  if (!date) return "—";
  return date.toLocaleDateString(undefined, { month: "short", day: "numeric", year: "numeric" });
}

export interface QueueDateRangeBarProps {
  dateFrom: Date | null;
  dateTo: Date | null;
  onChange: (range: DateRange) => void;
}

/**
 * Compact date range bar shown above all queue tabs.
 * Provides quick presets (24h / 7d / 30d / 90d / All) and
 * an expandable custom From / To date picker.
 */
export default function QueueDateRangeBar({ dateFrom, dateTo, onChange }: QueueDateRangeBarProps) {
  const [expanded, setExpanded] = useState(false);

  const activePreset = detectActivePreset(dateFrom, dateTo);

  const handlePreset = useCallback(
    (hours: number | null) => {
      onChange(buildRange(hours));
      setExpanded(false);
    },
    [onChange]
  );

  const handleFromChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const val = e.target.value;
      onChange({ from: val ? new Date(val + "T00:00:00") : null, to: dateTo });
    },
    [dateTo, onChange]
  );

  const handleToChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const val = e.target.value;
      onChange({ from: dateFrom, to: val ? new Date(val + "T23:59:59") : null });
    },
    [dateFrom, onChange]
  );

  const rangeSummary =
    dateFrom || dateTo
      ? `${formatDisplay(dateFrom)} – ${formatDisplay(dateTo)}`
      : "All time";

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-lg mb-4">
      {/* Always-visible row */}
      <div className="flex items-center gap-3 px-3 py-2 flex-wrap">
        {/* Calendar icon + label */}
        <span className="text-xs font-medium text-pf-text-secondary shrink-0 select-none">
          📅 Date range
        </span>

        {/* Preset buttons */}
        <div className="flex gap-1" role="group" aria-label="Date range presets">
          {PRESETS.map((preset) => (
            <Button
              key={preset.label}
              onClick={() => handlePreset(preset.hours)}
              variant="ghost"
              size="sm"
              aria-pressed={activePreset === preset.label}
              className={`px-2.5 py-1 text-xs font-medium rounded transition-colors ${
                activePreset === preset.label
                  ? "bg-pf-accent text-white hover:bg-pf-accent"
                  : "bg-pf-bg-0 border border-pf-border text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary"
              }`}
            >
              {preset.label}
            </Button>
          ))}
        </div>

        {/* Separator */}
        <span className="text-pf-border hidden sm:inline select-none">|</span>

        {/* Current range summary / custom toggle */}
        <Button
          onClick={() => setExpanded((v) => !v)}
          variant="ghost"
          size="sm"
          aria-expanded={expanded}
          className={`px-2 py-1 text-xs flex items-center gap-1.5 ${
            activePreset === null
              ? "text-pf-accent font-medium"
              : "text-pf-text-secondary"
          } hover:text-pf-text-primary hover:bg-pf-bg-2`}
        >
          <span>{activePreset === null ? rangeSummary : "Custom…"}</span>
          <span
            className="transition-transform duration-150"
            style={{ transform: expanded ? "rotate(180deg)" : "rotate(0deg)" }}
          >
            ▾
          </span>
        </Button>
      </div>

      {/* Expandable custom date inputs */}
      {expanded && (
        <div className="px-3 pb-3 pt-0 border-t border-pf-border">
          <div className="grid grid-cols-2 gap-3 pt-3 max-w-xs">
            <div>
              <label className="block text-xs font-medium text-pf-text-secondary mb-1">
                From
              </label>
              <input
                type="date"
                value={toInputValue(dateFrom)}
                onChange={handleFromChange}
                className="w-full px-2 py-1 text-sm border border-pf-border rounded-sm bg-pf-bg-0 text-pf-text-primary focus:outline-none focus:ring-1 focus:ring-pf-accent"
              />
            </div>
            <div>
              <label className="block text-xs font-medium text-pf-text-secondary mb-1">
                To
              </label>
              <input
                type="date"
                value={toInputValue(dateTo)}
                onChange={handleToChange}
                className="w-full px-2 py-1 text-sm border border-pf-border rounded-sm bg-pf-bg-0 text-pf-text-primary focus:outline-none focus:ring-1 focus:ring-pf-accent"
              />
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

/** Returns the default date range (last 7 days). */
export function defaultDateRange(): DateRange {
  return buildRange(24 * 7);
}
