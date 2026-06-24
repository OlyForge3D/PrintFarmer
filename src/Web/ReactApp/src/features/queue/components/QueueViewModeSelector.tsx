import { Button } from "@/common/components/ui";
import { GridViewIcon, ListViewIcon, TableIcon } from "@/common/components/icons/MdiIcons";
import type { ReactNode } from "react";

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
    <div className="inline-flex rounded-lg border border-pf-border overflow-hidden" role="group" aria-label="Queue view mode">
      {modes.map((mode) => {
        const isActive = value === mode.id;
        return (
          <Button
            key={mode.id}
            onClick={() => onChange(mode.id)}
            variant="ghost"
            size="sm"
            className={`rounded-none border-0 ${isActive ? "bg-pf-accent text-white hover:bg-pf-accent" : "bg-pf-bg-0 text-pf-text-secondary hover:bg-pf-bg-2"}`}
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
