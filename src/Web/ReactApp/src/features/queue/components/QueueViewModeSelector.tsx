import { Button } from "@/common/components/ui";
import { GridViewIcon, ListViewIcon, TableIcon } from "@/common/components/icons/MdiIcons";
import type { ReactNode } from "react";
import clsx from "clsx";

export type QueueViewMode = "table" | "list" | "cards";

interface QueueViewModeSelectorProps {
  value: QueueViewMode;
  onChange: (mode: QueueViewMode) => void;
}

export function QueueViewModeSelector({ value, onChange }: QueueViewModeSelectorProps) {
  const modes: Array<{
    id: QueueViewMode;
    label: string;
    short: string;
    icon: ReactNode;
  }> = [
    { id: "table", label: "Table view", short: "Table", icon: <TableIcon className="w-4 h-4" ariaLabel="Table view" /> },
    { id: "list", label: "List view", short: "List", icon: <ListViewIcon className="w-4 h-4" ariaLabel="List view" /> },
    { id: "cards", label: "Cards view", short: "Cards", icon: <GridViewIcon className="w-4 h-4" ariaLabel="Cards view" /> },
  ];

  return (
    <div
      className="inline-flex rounded-xl border border-pf-border/80 overflow-hidden bg-pf-bg-0/70 backdrop-blur-sm shadow-[0_8px_24px_rgba(0,0,0,0.18)]"
      role="group"
      aria-label="Queue view mode"
    >
      {modes.map((mode) => {
        const isActive = value === mode.id;
        return (
          <Button
            key={mode.id}
            onClick={() => onChange(mode.id)}
            variant="ghost"
            size="sm"
            className={clsx(
              "rounded-none border-0 transition-all duration-200 ease-out",
              "focus-visible:relative focus-visible:z-10",
              "motion-safe:hover:-translate-y-px",
              isActive
                ? "bg-gradient-to-br from-pf-accent to-pf-accent-dark text-white shadow-[inset_0_0_0_1px_rgba(255,255,255,0.16)] hover:from-pf-accent hover:to-pf-accent-dark"
                : "bg-pf-bg-0/60 text-pf-text-secondary hover:bg-pf-bg-2",
            )}
            aria-pressed={isActive}
            aria-label={mode.label}
            title={mode.label}
          >
            <span className="inline-flex items-center gap-1.5">
              <span aria-hidden="true">{mode.icon}</span>
              <span className="hidden md:inline">{mode.short}</span>
            </span>
          </Button>
        );
      })}
    </div>
  );
}
