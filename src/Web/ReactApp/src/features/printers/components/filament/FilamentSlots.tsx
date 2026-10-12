import { useId, useRef, useState, type KeyboardEvent } from 'react';
import clsx from 'clsx';
import {
  ArrowDownToLine,
  ArrowLeftRight,
  ArrowUpFromLine,
  ChevronDown,
  Eject,
  Plus,
  Unlink,
  type LucideIcon,
} from 'lucide-react';
import { Button, Tooltip } from '@/common/components/ui';
import { SpoolIcon } from '@/common/components/icons/SpoolIcon';
import {
  DEPLETED_SPOOL_HINT,
  formatGrams,
  slotAccessibleLabel,
  type FilamentSlotView,
  type SlotAction,
  type SlotActionKind,
  type SlotPanelContent,
} from './filamentSlotModel';

const INVENTORY_KINDS: SlotActionKind[] = ['change', 'unassign', 'assign'];

const ACTION_ICONS: Record<SlotActionKind, LucideIcon> = {
  change: ArrowLeftRight,
  unassign: Unlink,
  assign: Plus,
  load: ArrowDownToLine,
  unload: ArrowUpFromLine,
  eject: Eject,
};

/** Spool glyph with the Option 4 stock states: filled, depleted (!) and unassigned (+). */
export function SlotSpool({ slot, size, className }: { slot: FilamentSlotView; size: number; className?: string }) {
  const badge = Math.max(10, Math.round(size * 0.44));
  const fill = slot.stock === 'depleted' && slot.empty
    ? null
    : slot.stock === 'none'
      ? slot.color ?? null
      : slot.color ?? 'var(--pf-border-strong)';
  return (
    <span className={clsx('relative inline-flex shrink-0', className)} aria-hidden="true" data-stock={slot.stock}>
      <SpoolIcon decorative size={size} frameColor="var(--pf-text-secondary)" fillColor={fill} />
      {slot.stock === 'depleted' && (
        <span
          className="absolute -right-1 -top-1 inline-flex aspect-square items-center justify-center rounded-full bg-pf-warning font-bold leading-none text-[var(--pf-warning-fg)]"
          style={{ width: badge, height: badge, fontSize: Math.round(badge * 0.72) }}
          data-testid="slot-badge-depleted"
        >
          !
        </span>
      )}
      {slot.stock === 'none' && (
        <span
          className="absolute -bottom-0.5 -right-0.5 inline-flex aspect-square items-center justify-center rounded-full border border-pf-border-strong bg-pf-bg-1 text-pf-text-primary"
          style={{ width: badge + 1, height: badge + 1 }}
          data-testid="slot-badge-assign"
        >
          <Plus style={{ width: badge - 4, height: badge - 4 }} strokeWidth={3} />
        </span>
      )}
    </span>
  );
}

function ActiveDot() {
  return (
    <span
      aria-hidden="true"
      className="inline-block size-1.5 shrink-0 rounded-full bg-pf-accent shadow-[0_0_0_2px_color-mix(in_srgb,var(--pf-accent)_22%,transparent)]"
    />
  );
}

function Sep() {
  return <span aria-hidden="true" className="shrink-0 text-pf-text-tertiary">·</span>;
}

/** Material, then "Empty", "0 g" for a depleted spool in a loaded slot, or grams, joined with middots; renders nothing when nothing is known. */
function FactsLine({ slot, nameTestId }: { slot: FilamentSlotView; nameTestId?: string }) {
  const grams = formatGrams(slot.grams);
  const tail = slot.stock === 'depleted'
    ? slot.empty
      ? <span className="shrink-0 text-pf-warning">Empty</span>
      : <span className="shrink-0 tabular-nums text-pf-warning" title={DEPLETED_SPOOL_HINT} data-testid="filament-grams">{grams ?? '0 g'}</span>
    : grams && <span className="shrink-0 tabular-nums text-pf-text-tertiary" data-testid="filament-grams">{grams}</span>;
  return (
    <>
      {slot.displayName && (
        <span className="min-w-0 truncate" data-testid={nameTestId}>{slot.displayName}</span>
      )}
      {slot.displayName && tail && <Sep />}
      {tail}
    </>
  );
}

function PanelButton({ action }: { action: SlotAction }) {
  const Icon = ACTION_ICONS[action.kind];
  const descriptionId = useId();
  const explained = action.disabledReason != null;
  const disabled = explained || !!action.busy;
  const primary = action.kind === 'assign' && !disabled;
  return (
    <>
    <Tooltip
      content={explained ? `${action.label} — ${action.disabledReason}` : action.label}
      // The reason is always exposed through the persistent description below,
      // so the visual tooltip never needs to be announced as well.
      ariaMode="hidden"
      position="top"
      delay={400}
      className="w-max max-w-56 whitespace-normal text-center"
    >
      <Button
        type="button"
        variant="unstyled"
        aria-label={action.label}
        aria-describedby={explained ? descriptionId : undefined}
        disabled={disabled}
        explainedDisabled={explained}
        onClick={action.onClick}
        data-testid={`slot-action-${action.kind}`}
        className={clsx(
          'inline-flex size-11 shrink-0 items-center justify-center rounded-md transition-colors',
          'focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-pf-accent',
          primary
            ? 'bg-[var(--pf-button-primary-bg)] text-[var(--pf-button-primary-text)] hover:bg-[var(--pf-button-primary-hover)]'
            : 'text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary',
          'data-[pf-disabled-explained]:cursor-not-allowed data-[pf-disabled-explained]:opacity-40 data-[pf-disabled-explained]:hover:bg-transparent data-[pf-disabled-explained]:hover:text-pf-text-secondary',
        )}
      >
        <Icon aria-hidden="true" className="size-5" strokeWidth={1.75} />
      </Button>
    </Tooltip>
    {explained && <span id={descriptionId} className="sr-only">{action.disabledReason}</span>}
    </>
  );
}

/** The one shared slot panel: header plus a single row of 44px icon buttons. */
export function SlotActionPanel({
  id,
  slot,
  showId,
  content,
  onKeyDown,
}: {
  id: string;
  slot: FilamentSlotView;
  showId: boolean;
  content: SlotPanelContent;
  onKeyDown?: (event: KeyboardEvent<HTMLDivElement>) => void;
}) {
  const inventory = content.actions.filter((a) => INVENTORY_KINDS.includes(a.kind));
  const physical = content.actions.filter((a) => !INVENTORY_KINDS.includes(a.kind));
  const grams = formatGrams(slot.grams);
  const showName = (showId && !!slot.id) || !!slot.displayName;
  const showSpoolLine = slot.spoolId != null || slot.disabled || !!slot.deviceName;
  return (
    <div
      id={id}
      role="region"
      aria-label={content.title}
      onKeyDown={onKeyDown}
      className="mt-2 rounded-md border border-pf-border-strong bg-pf-bg-1 px-1 pb-1 pt-2"
      data-testid="slot-panel"
    >
      {(showName || showSpoolLine || content.extra) && (
        <div className="min-w-0 px-1.5 pb-1.5">
          {showName && (
            <div className="flex min-w-0 items-center gap-1.5 text-[13px] text-pf-text-primary">
              {showId && slot.id && <b className="shrink-0 font-semibold">{slot.id}</b>}
              {showId && slot.id && slot.displayName && <Sep />}
              {slot.displayName && <span className="min-w-0 truncate">{slot.displayName}</span>}
              {slot.color && !slot.empty && (
                <span
                  aria-hidden="true"
                  className="inline-block size-2.5 shrink-0 rounded-full border border-pf-border"
                  style={{ backgroundColor: slot.color }}
                />
              )}
            </div>
          )}
          {showSpoolLine && (
            <div className="mt-0.5 flex flex-wrap items-center gap-x-1.5 text-xs tabular-nums text-pf-text-tertiary">
              {slot.deviceName && <span className="min-w-0 truncate" data-testid="slot-device-name">{slot.deviceName}</span>}
              {slot.deviceName && slot.spoolId != null && <Sep />}
              {slot.spoolId != null && <span>Spool #{slot.spoolId}</span>}
              {slot.spoolId != null && (slot.stock === 'depleted' || grams) && <Sep />}
              {slot.stock === 'depleted'
                ? slot.empty
                  ? <span className="text-pf-warning">Empty</span>
                  : <span className="text-pf-warning" title={DEPLETED_SPOOL_HINT}>{grams ?? '0 g'}</span>
                : slot.spoolId != null && grams && <span>{grams}</span>}
              {slot.disabled && (
                <>
                  {(slot.spoolId != null || slot.deviceName) && <Sep />}
                  <span className="text-pf-warning">Disabled on device</span>
                </>
              )}
            </div>
          )}
          {content.extra}
        </div>
      )}
      <div role="group" aria-label={`${content.title} actions`} className="flex flex-wrap items-center">
        {inventory.map((action) => <PanelButton key={action.kind} action={action} />)}
        {inventory.length > 0 && physical.length > 0 && (
          <span aria-hidden="true" className="mx-[3px] h-6 w-px shrink-0 bg-pf-border-strong" />
        )}
        {physical.map((action) => <PanelButton key={action.kind} action={action} />)}
      </div>
    </div>
  );
}

export interface FilamentSlotsProps {
  /** Slots shown in the rail, in display order. */
  slots: FilamentSlotView[];
  /**
   * The active feed for the resting line. It may be a slot that is not in the
   * rail (a hidden QidiBox rack). `null` falls back to the mini spool rail.
   */
  summarySlot: FilamentSlotView | null;
  /** `single` renders one spool button and no rail. */
  mode: 'single' | 'multi';
  unitWord: 'slots' | 'tools';
  selectedKey: string | null;
  onSelect: (key: string | null) => void;
  panelFor: (slot: FilamentSlotView) => SlotPanelContent;
  testId?: string;
}

/**
 * Option 4 filament module: a one-line active-feed summary that discloses a
 * compact spool rail, and one shared action panel for the selected slot.
 * Every device type renders through this so they share one design language.
 */
export function FilamentSlots({
  slots,
  summarySlot,
  mode,
  unitWord,
  selectedKey,
  onSelect,
  panelFor,
  testId = 'filament-slots',
}: FilamentSlotsProps) {
  const [expanded, setExpanded] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const baseId = useId();
  const railId = `${baseId}-rail`;
  const panelId = `${baseId}-panel`;
  const selected = slots.find((slot) => slot.key === selectedKey) ?? null;
  const railOpen = expanded || selected != null;

  const handlePanelKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'Escape' || !selected) return;
    event.stopPropagation();
    const key = selected.key;
    onSelect(null);
    requestAnimationFrame(() => {
      const target = Array.from(rootRef.current?.querySelectorAll<HTMLElement>('[data-slot-key]') ?? [])
        .find((el) => el.dataset.slotKey === key);
      target?.focus();
    });
  };

  if (slots.length === 0) return null;

  if (mode === 'single') {
    const slot = slots[0];
    const isOpen = selected?.key === slot.key;
    const hasFacts = !!slot.displayName || slot.stock === 'depleted' || !!formatGrams(slot.grams);
    return (
      <div ref={rootRef} data-testid={testId}>
        <Button
          type="button"
          variant="unstyled"
          data-slot-key={slot.key}
          data-testid={slot.testId}
          data-state={slot.stock}
          data-status={slot.coverageStatus}
          data-source={slot.dataSource}
          aria-expanded={isOpen}
          aria-controls={isOpen ? panelId : undefined}
          aria-label={slot.stock === 'none' && !slot.displayName ? 'Assign spool' : `Filament: ${slotAccessibleLabel(slot)}`}
          onClick={() => onSelect(isOpen ? null : slot.key)}
          className={clsx(
            // The shared Button wraps children in a span; `*:contents` lets them join this flex row.
            'inline-flex min-h-11 max-w-full items-center gap-2.5 rounded-md border p-1 text-left transition-colors *:contents',
            hasFacts && 'pr-3',
            'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent',
            isOpen ? 'border-pf-border-strong bg-pf-bg-2' : 'border-transparent hover:bg-pf-bg-2',
          )}
        >
          <SlotSpool slot={slot} size={40} />
          {hasFacts && (
            <span className="flex min-w-0 items-center gap-1.5 text-[13px] text-pf-text-primary">
              <FactsLine slot={slot} nameTestId="filament-summary-name" />
            </span>
          )}
        </Button>
        {isOpen && (
          <SlotActionPanel id={panelId} slot={slot} showId={false} content={panelFor(slot)} onKeyDown={handlePanelKeyDown} />
        )}
      </div>
    );
  }

  const emptyCount = slots.filter((slot) => slot.stock === 'depleted' && slot.empty).length;
  const zeroCount = slots.filter((slot) => slot.stock === 'depleted' && !slot.empty).length;
  const summaryLabel = [
    summarySlot ? slotAccessibleLabel(summarySlot) : `Filament ${unitWord}`,
    emptyCount > 0 ? `${emptyCount} empty` : null,
    zeroCount > 0 ? `${zeroCount} at 0 g` : null,
    `${railOpen ? 'Hide' : 'Show'} ${slots.length} ${unitWord}`,
  ].filter(Boolean).join(', ');

  return (
    <div ref={rootRef} className="@container" data-testid={testId}>
      <Button
        type="button"
        variant="unstyled"
        aria-expanded={railOpen}
        aria-controls={railId}
        aria-label={summaryLabel}
        onClick={() => {
          if (railOpen) {
            setExpanded(false);
            onSelect(null);
          } else {
            setExpanded(true);
          }
        }}
        data-testid="filament-summary"
        className={clsx(
          'flex min-h-11 w-full min-w-0 items-center gap-2 rounded-md p-1 text-left transition-colors hover:bg-pf-bg-2 *:contents',
          'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent',
        )}
      >
        {summarySlot ? (
          <>
            <SlotSpool slot={summarySlot} size={32} className="@max-[16.5rem]:[&_svg]:size-7" />
            <span className="flex min-w-0 flex-1 items-center gap-1.5 text-[13px] text-pf-text-primary" data-testid="filament-summary-line">
              {summarySlot.active && <ActiveDot />}
              {summarySlot.id && <b className="shrink-0 font-semibold">{summarySlot.id}</b>}
              {summarySlot.id && (summarySlot.displayName || summarySlot.stock === 'depleted' || formatGrams(summarySlot.grams)) && <Sep />}
              <FactsLine slot={summarySlot} nameTestId="filament-summary-name" />
            </span>
          </>
        ) : (
          <span className="flex min-w-0 flex-1 items-center gap-1.5 overflow-hidden" data-testid="filament-summary-mini">
            {slots.map((slot) => <SlotSpool key={slot.key} slot={slot} size={24} />)}
          </span>
        )}
        <span data-pf-radius="full" className="ml-auto inline-flex shrink-0 items-center gap-1 rounded-full border border-pf-border px-2 py-[3px] text-xs text-pf-text-secondary">
          {emptyCount + zeroCount > 0 && (
            <span aria-hidden="true" className="size-1.5 rounded-full bg-pf-warning" data-testid="filament-empty-dot" />
          )}
          <span className="tabular-nums">{slots.length}</span>
          <span className="@max-[16.5rem]:sr-only">&nbsp;{unitWord}</span>
          <ChevronDown aria-hidden="true" className={clsx('size-3 transition-transform', railOpen && 'rotate-180')} strokeWidth={2.5} />
        </span>
      </Button>

      <div id={railId} hidden={!railOpen} className="mt-2 border-t border-pf-border-divider pt-2">
        <div
          role="group"
          aria-label={`Filament ${unitWord}`}
          className="grid gap-1"
          style={{ gridTemplateColumns: `repeat(${Math.min(slots.length, 5)}, minmax(0, 1fr))` }}
        >
          {slots.map((slot) => {
            const isSelected = selected?.key === slot.key;
            return (
              <Button
                key={slot.key}
                type="button"
                variant="unstyled"
                data-slot-key={slot.key}
                data-testid={slot.testId}
                data-state={slot.stock}
                data-status={slot.coverageStatus}
                data-active={slot.active ? 'loaded' : slot.selectedFeed ? 'selected' : undefined}
                aria-current={slot.active ? 'true' : undefined}
                data-source={slot.dataSource}
                data-disabled={slot.disabled ? 'true' : undefined}
                aria-label={slotAccessibleLabel(slot)}
                aria-expanded={isSelected}
                aria-controls={isSelected ? panelId : undefined}
                onClick={() => onSelect(isSelected ? null : slot.key)}
                className={clsx(
                  'flex min-h-11 min-w-0 flex-col items-center gap-1 rounded-md border px-0.5 py-1 transition-colors *:contents',
                  'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent',
                  isSelected ? 'border-pf-border-strong bg-pf-bg-2' : 'border-transparent hover:bg-pf-bg-2',
                  slot.disabled && 'opacity-60',
                )}
              >
                <span className="flex items-center gap-1 text-[11px] font-semibold leading-none text-pf-text-primary">
                  {slot.active && <ActiveDot />}
                  {slot.id}
                </span>
                <SlotSpool slot={slot} size={32} />
                <span
                  className={clsx(
                    'min-h-[1lh] max-w-full truncate text-[11.5px] leading-tight',
                    slot.stock === 'depleted' || slot.atRisk ? 'text-pf-warning' : 'text-pf-text-secondary',
                  )}
                >
                  {slot.stock === 'depleted' && slot.empty ? 'Empty' : slot.material ?? ''}
                </span>
              </Button>
            );
          })}
        </div>
        {selected && (
          <SlotActionPanel id={panelId} slot={selected} showId content={panelFor(selected)} onKeyDown={handlePanelKeyDown} />
        )}
      </div>
    </div>
  );
}
