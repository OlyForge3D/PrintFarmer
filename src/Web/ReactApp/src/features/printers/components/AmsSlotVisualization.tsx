import { useState, useRef, useEffect, useCallback } from 'react';
import clsx from 'clsx';
import { Badge, Tooltip } from '@/common/components/ui';
import type { ToolheadDto } from '@/types/api';
import { SlotPopover } from './SlotPopover';

// ── Constants ──

const BAMBU_AMS_UNIT_SIZE = 4;
const MAX_SINGLE_MMU_SIZE = 5;

// ── Types ──

interface AmsUnit {
  label: string;
  slots: ToolheadDto[];
}

export interface AmsSlotVisualizationProps {
  /** Toolhead data from printer details */
  toolheads: ToolheadDto[];
  /** Compact mode for card views (fewer details, smaller slots) */
  compact?: boolean;
  /** Printer ID — when provided, slots become clickable with assign/unassign popovers */
  printerId?: string;
}

// ── Helpers ──

function isMmuGate(toolhead: ToolheadDto): boolean {
  return String(toolhead.toolheadType) === 'MmuGate';
}

/** Determine if a hex color is light enough to need a visible border */
function isLightColor(hex: string): boolean {
  const clean = hex.replace('#', '');
  if (clean.length < 6) return false;
  const r = parseInt(clean.substring(0, 2), 16);
  const g = parseInt(clean.substring(2, 4), 16);
  const b = parseInt(clean.substring(4, 6), 16);
  // Perceived luminance formula
  const luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
  return luminance > 0.7;
}

/**
 * Group MmuGate toolheads into AMS/MMU units.
 * If total ≤ 5, treat as a single MMU unit.
 * Otherwise, group in batches of 4 (Bambu AMS convention).
 */
function groupIntoUnits(mmuGates: ToolheadDto[]): AmsUnit[] {
  const sorted = [...mmuGates].sort((a, b) => a.index - b.index);

  if (sorted.length <= MAX_SINGLE_MMU_SIZE) {
    return [{ label: 'AMS 1', slots: sorted }];
  }

  const units: AmsUnit[] = [];
  for (let i = 0; i < sorted.length; i += BAMBU_AMS_UNIT_SIZE) {
    const batch = sorted.slice(i, i + BAMBU_AMS_UNIT_SIZE);
    units.push({
      label: `AMS ${units.length + 1}`,
      slots: batch,
    });
  }
  return units;
}

// ── Hooks ──

/** Close a popover when clicking outside the container or pressing Escape. */
function useClickOutside(
  ref: { current: HTMLElement | null },
  isActive: boolean,
  onClose: (() => void) | undefined,
) {
  useEffect(() => {
    if (!isActive || !onClose) return;
    const handleMouseDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        onClose();
      }
    };
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('mousedown', handleMouseDown);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('mousedown', handleMouseDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [ref, isActive, onClose]);
}

// ── Sub-components ──

function SlotTooltipContent({ toolhead }: { toolhead: ToolheadDto }) {
  return (
    <div className="space-y-1 text-left min-w-[140px]">
      {toolhead.currentMaterial && (
        <div className="font-medium text-pf-text-primary">{toolhead.currentMaterial}</div>
      )}
      {toolhead.name && (
        <div className="text-pf-text-secondary">{toolhead.name}</div>
      )}
      {toolhead.currentFilamentColor && (
        <div className="flex items-center gap-1.5">
          <span
            className="inline-block w-3 h-3 rounded-full border border-pf-border"
            style={{ backgroundColor: toolhead.currentFilamentColor }}
          />
          <span className="text-pf-text-secondary">{toolhead.currentFilamentColor}</span>
        </div>
      )}
      {toolhead.currentSpoolId != null && (
        <div className="text-pf-text-tertiary">Spool #{toolhead.currentSpoolId}</div>
      )}
      {toolhead.nozzleDiameter != null && (
        <div className="text-pf-text-tertiary">{toolhead.nozzleDiameter}mm nozzle</div>
      )}
    </div>
  );
}

interface SlotProps {
  toolhead: ToolheadDto;
  slotNumber: number;
  compact?: boolean;
  isPopoverOpen?: boolean;
  onSlotClick?: () => void;
  onPopoverClose?: () => void;
  printerId?: string;
}

function Slot({ toolhead, slotNumber, compact, isPopoverOpen, onSlotClick, onPopoverClose, printerId }: SlotProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const interactive = !!printerId;
  const hasFilament = toolhead.currentFilamentColor != null || toolhead.currentMaterial != null;
  const color = toolhead.currentFilamentColor;
  const needsBorder = color ? isLightColor(color) : false;

  useClickOutside(containerRef, !!isPopoverOpen, onPopoverClose);

  const handleClick = useCallback(() => {
    if (interactive && onSlotClick) onSlotClick();
  }, [interactive, onSlotClick]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (interactive && (e.key === 'Enter' || e.key === ' ')) {
      e.preventDefault();
      handleClick();
    }
  }, [interactive, handleClick]);

  const slot = (
    <div
      className={clsx(
        'relative flex flex-col items-center justify-center rounded-lg transition-colors',
        compact ? 'w-10 h-12 gap-0.5' : 'w-14 h-16 gap-1',
        hasFilament
          ? 'border border-pf-border bg-pf-bg-2'
          : 'border border-dashed border-pf-border-light bg-pf-bg-1',
        interactive && 'cursor-pointer hover:border-pf-accent',
        isPopoverOpen && 'border-pf-accent ring-1 ring-pf-accent',
      )}
      data-testid={`ams-slot-${toolhead.index}`}
      {...(interactive ? {
        role: 'button',
        tabIndex: 0,
        'aria-expanded': isPopoverOpen,
        'aria-haspopup': 'dialog' as const,
        onClick: handleClick,
        onKeyDown: handleKeyDown,
      } : {})}
    >
      {/* Slot number */}
      <span className={clsx(
        'absolute font-mono font-bold text-pf-text-tertiary',
        compact ? 'top-0.5 right-1 text-[8px]' : 'top-1 right-1.5 text-[9px]',
      )}>
        {slotNumber}
      </span>

      {/* Color swatch or empty indicator */}
      {hasFilament && color ? (
        <span
          className={clsx(
            'rounded-full shrink-0',
            compact ? 'w-5 h-5' : 'w-6 h-6',
            needsBorder && 'border-2 border-pf-border',
          )}
          style={{ backgroundColor: color }}
          data-testid={`slot-color-${toolhead.index}`}
          aria-label={`Filament color: ${color}`}
        />
      ) : (
        <span className={clsx(
          'text-pf-text-tertiary',
          compact ? 'text-[8px]' : 'text-[10px]',
        )}>
          Empty
        </span>
      )}

      {/* Material label */}
      {!compact && hasFilament && toolhead.currentMaterial && (
        <span className="text-[9px] font-medium text-pf-text-secondary truncate max-w-[52px]">
          {toolhead.currentMaterial}
        </span>
      )}
    </div>
  );

  const wrapped = hasFilament && !isPopoverOpen ? (
    <Tooltip content={<SlotTooltipContent toolhead={toolhead} />} position="top">
      {slot}
    </Tooltip>
  ) : slot;

  return (
    <div ref={containerRef} className="relative">
      {wrapped}
      {isPopoverOpen && printerId && (
        <SlotPopover toolhead={toolhead} printerId={printerId} onClose={onPopoverClose ?? (() => {})} />
      )}
    </div>
  );
}

interface NozzleIndicatorProps {
  toolhead: ToolheadDto;
  compact?: boolean;
  isPopoverOpen?: boolean;
  onSlotClick?: () => void;
  onPopoverClose?: () => void;
  printerId?: string;
}

function NozzleIndicator({ toolhead, compact, isPopoverOpen, onSlotClick, onPopoverClose, printerId }: NozzleIndicatorProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const interactive = !!printerId;
  const hasFilament = toolhead.currentFilamentColor != null || toolhead.currentMaterial != null;
  const color = toolhead.currentFilamentColor;

  useClickOutside(containerRef, !!isPopoverOpen, onPopoverClose);

  const handleClick = useCallback(() => {
    if (interactive && onSlotClick) onSlotClick();
  }, [interactive, onSlotClick]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (interactive && (e.key === 'Enter' || e.key === ' ')) {
      e.preventDefault();
      handleClick();
    }
  }, [interactive, handleClick]);

  const indicator = (
    <div
      className={clsx(
        'flex items-center gap-2 rounded-lg border border-pf-border bg-pf-bg-2 px-2',
        compact ? 'py-1' : 'py-1.5',
        interactive && 'cursor-pointer hover:border-pf-accent',
        isPopoverOpen && 'border-pf-accent ring-1 ring-pf-accent',
      )}
      data-testid={`nozzle-indicator-${toolhead.index}`}
      {...(interactive ? {
        role: 'button',
        tabIndex: 0,
        'aria-expanded': isPopoverOpen,
        'aria-haspopup': 'dialog' as const,
        onClick: handleClick,
        onKeyDown: handleKeyDown,
      } : {})}
    >
      {/* Nozzle icon */}
      <svg
        width={compact ? 14 : 18}
        height={compact ? 14 : 18}
        viewBox="0 0 24 24"
        fill="currentColor"
        className="text-pf-text-secondary shrink-0"
        aria-hidden="true"
      >
        <path d="M7 2v2h10V2H7zm0 4v4l-2 2v2h4v6h2v2h2v-2h2v-6h4v-2l-2-2V6H7zm2 2h6v3.17l1 1V14h-2v4h-4v-4H8v-1.83l1-1V8z" />
      </svg>

      {/* Color dot */}
      {color && (
        <span
          className={clsx(
            'rounded-full shrink-0',
            compact ? 'w-3.5 h-3.5' : 'w-4 h-4',
            isLightColor(color) && 'border border-pf-border',
          )}
          style={{ backgroundColor: color }}
        />
      )}

      {/* Label */}
      <div className="flex flex-col min-w-0">
        <span className={clsx(
          'font-medium text-pf-text-primary',
          compact ? 'text-[10px]' : 'text-xs',
        )}>
          T{toolhead.index}
          {toolhead.nozzleDiameter != null && (
            <span className="text-pf-text-tertiary ml-1">{toolhead.nozzleDiameter}mm</span>
          )}
        </span>
        {!compact && hasFilament && (
          <span className="text-[10px] text-pf-text-secondary truncate">
            {toolhead.currentMaterial || 'Loaded'}
          </span>
        )}
      </div>
    </div>
  );

  if (!hasFilament && !interactive) return indicator;

  const wrapped = hasFilament && !isPopoverOpen ? (
    <Tooltip content={<SlotTooltipContent toolhead={toolhead} />} position="top">
      {indicator}
    </Tooltip>
  ) : indicator;

  return (
    <div ref={containerRef} className="relative">
      {wrapped}
      {isPopoverOpen && printerId && (
        <SlotPopover toolhead={toolhead} printerId={printerId} onClose={onPopoverClose ?? (() => {})} />
      )}
    </div>
  );
}

// ── Main Component ──

/**
 * Visual representation of AMS (Automatic Material System) units and MMU
 * (Multi-Material Unit) slots showing loaded filaments.
 *
 * Groups toolheads by type:
 * - Physical toolheads → nozzle indicators
 * - MmuGate toolheads → AMS/MMU slot grid (4 per unit for Bambu, ≤5 for single MMU)
 *
 * Purely presentational — receives toolheads as prop.
 */
export function AmsSlotVisualization({ toolheads, compact = false, printerId }: AmsSlotVisualizationProps) {
  const [activeSlotIndex, setActiveSlotIndex] = useState<number | null>(null);

  if (!toolheads || toolheads.length === 0) return null;

  const physicalHeads = toolheads.filter((t) => !isMmuGate(t));
  const mmuGates = toolheads.filter(isMmuGate);
  const units = mmuGates.length > 0 ? groupIntoUnits(mmuGates) : [];

  // If there are only physical toolheads with no MMU gates, show as nozzle indicators
  const hasOnlyPhysical = physicalHeads.length > 0 && mmuGates.length === 0;
  // External spool: physical toolheads with loaded filament alongside MMU gates
  const externalSpools = mmuGates.length > 0
    ? physicalHeads.filter((t) => t.currentSpoolId != null || t.currentMaterial != null)
    : [];
  // Physical heads shown in top section only when there are no MMU gates
  const topPhysicalHeads = mmuGates.length === 0 ? physicalHeads : [];

  return (
    <div className="space-y-2" data-testid="ams-slot-visualization">
      {/* Physical toolhead indicators (only when no MMU gates) */}
      {topPhysicalHeads.length > 0 && (
        <div className="flex items-center gap-2 flex-wrap">
          {topPhysicalHeads.map((toolhead) => (
            <NozzleIndicator
              key={toolhead.id ?? `phys-${toolhead.index}`}
              toolhead={toolhead}
              compact={compact}
              printerId={printerId}
              isPopoverOpen={activeSlotIndex === toolhead.index}
              onSlotClick={() => setActiveSlotIndex(activeSlotIndex === toolhead.index ? null : toolhead.index)}
              onPopoverClose={() => setActiveSlotIndex(null)}
            />
          ))}
          {hasOnlyPhysical && topPhysicalHeads.length === 1 && !topPhysicalHeads[0].currentMaterial && (
            <span className="text-xs text-pf-text-tertiary">Single extruder</span>
          )}
        </div>
      )}

      {/* AMS/MMU units */}
      {units.map((unit, unitIndex) => (
        <div key={unit.label} data-testid={`ams-unit-${unitIndex}`}>
          {/* Unit header */}
          <div className="flex items-center gap-2 mb-1.5">
            <Badge variant="default" size="sm">{unit.label}</Badge>
            <div className="flex-1 border-b border-pf-border" />
            <span className="text-[10px] text-pf-text-tertiary">
              {unit.slots.filter((s) => s.currentMaterial != null).length}/{unit.slots.length} loaded
            </span>
          </div>

          {/* Slot grid */}
          <div className="flex gap-1.5 flex-wrap">
            {unit.slots.map((slot, slotIdx) => (
              <Slot
                key={slot.id ?? `mmu-${slot.index}`}
                toolhead={slot}
                slotNumber={slotIdx + 1}
                compact={compact}
                printerId={printerId}
                isPopoverOpen={activeSlotIndex === slot.index}
                onSlotClick={() => setActiveSlotIndex(activeSlotIndex === slot.index ? null : slot.index)}
                onPopoverClose={() => setActiveSlotIndex(null)}
              />
            ))}
          </div>
        </div>
      ))}

      {/* External spool indicators */}
      {externalSpools.length > 0 && (
        <div data-testid="external-spool-section">
          <div className="flex items-center gap-2 mb-1.5">
            <Badge variant="default" size="sm">External</Badge>
            <div className="flex-1 border-b border-pf-border" />
          </div>
          <div className="flex items-center gap-2 flex-wrap">
            {externalSpools.map((toolhead) => (
              <NozzleIndicator
                key={toolhead.id ?? `ext-${toolhead.index}`}
                toolhead={toolhead}
                compact={compact}
                printerId={printerId}
                isPopoverOpen={activeSlotIndex === toolhead.index}
                onSlotClick={() => setActiveSlotIndex(activeSlotIndex === toolhead.index ? null : toolhead.index)}
                onPopoverClose={() => setActiveSlotIndex(null)}
              />
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
