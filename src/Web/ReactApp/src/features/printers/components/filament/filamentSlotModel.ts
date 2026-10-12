import type { ReactNode } from 'react';
import type { LoadoutSlot } from '@/features/printers/utils/materialLoadout';
import { materialSlotState } from '@/features/printers/utils/materialSlotState';
import type { SpoolmanSpool } from '@/types/api';

/** Inventory stock of a slot: a live spool, a known-depleted spool, or nothing assigned. */
export type SlotStock = 'assigned' | 'depleted' | 'none';

/** Presentation model shared by every filament surface (MMU gates, toolheads, single feed). */
export interface FilamentSlotView {
  key: string;
  /** Short visible id ("1A", "T1", "Rack"); empty for a single-toolhead printer. */
  id: string;
  /** Short material for the rail ("PLA"). */
  material?: string;
  /** Material plus filament name for the summary and panel ("PLA Black"). */
  displayName?: string;
  color?: string;
  /** Known remaining grams; `undefined` when unknown (never rendered as 0). */
  grams?: number;
  spoolId?: number;
  stock: SlotStock;
  /**
   * The assigned spool is depleted and the printer reports no filament in the
   * slot, so it really is empty. A depleted spool alone only means inventory is
   * at 0 g; the slot may still hold live filament with a reported color.
   */
  empty?: boolean;
  active?: boolean;
  disabled?: boolean;
  atRisk?: boolean;
  /** Filament-coverage status exposed as `data-status` for diagnostics and tests. */
  coverageStatus?: string;
  testId?: string;
  dataSource?: string;
  /** The printer has this feed selected but reports no filament loaded through it. */
  selectedFeed?: boolean;
  /** Full device-reported slot name when it says more than the short id. */
  deviceName?: string;
}

export type SlotActionKind = 'change' | 'unassign' | 'assign' | 'load' | 'unload' | 'eject';

export interface SlotAction {
  kind: SlotActionKind;
  label: string;
  onClick: () => void;
  /** When set the action is aria-disabled and this reason is its accessible description and tooltip. */
  disabledReason?: string;
  /** A transient write is in flight: natively disabled, no explanation needed. */
  busy?: boolean;
}

export interface SlotPanelContent {
  /** Region label, e.g. "Slot 1B", "T1" or "Filament". */
  title: string;
  actions: SlotAction[];
  /** Optional secondary facts (coverage, warnings) rendered under the header. */
  extra?: ReactNode;
}

/** Visible noun used to build action labels for a slot. */
export type SlotTargetKind = 'gate' | 'tool' | 'single' | 'rack';

export function slotActionLabel(kind: SlotActionKind, target: SlotTargetKind, id: string): string {
  if (target === 'single') {
    return {
      change: 'Change spool',
      unassign: 'Unassign spool',
      assign: 'Assign spool',
      load: 'Load filament',
      unload: 'Unload filament',
      eject: 'Eject filament',
    }[kind];
  }
  const noun = target === 'gate' ? `slot ${id}` : target === 'rack' ? 'the rack' : id;
  return {
    change: `Change spool in ${noun}`,
    unassign: `Unassign spool from ${noun}`,
    assign: `Assign spool to ${noun}`,
    load: `Load ${noun}`,
    unload: `Unload ${noun}`,
    eject: `Eject ${noun}`,
  }[kind];
}

export function slotPanelTitle(target: SlotTargetKind, id: string): string {
  if (target === 'single') return 'Filament';
  if (target === 'gate') return `Slot ${id}`;
  return id;
}

/** "PLA" + "Matte Black" → "PLA Matte Black"; never repeats the material. */
export function filamentDisplayName(material?: string | null, filamentName?: string | null): string | undefined {
  const m = material?.trim();
  const f = filamentName?.trim();
  if (!f) return m || undefined;
  if (!m) return f;
  return f.toLowerCase().includes(m.toLowerCase()) ? f : `${m} ${f}`;
}

export function formatGrams(grams: number | undefined): string | undefined {
  if (grams == null || !Number.isFinite(grams) || grams < 0) return undefined;
  if (grams > 0 && grams < 1) return '<1 g';
  return `${Math.round(grams)} g`;
}

/** Build the shared view for a resolved loadout slot and its matching Spoolman record. */
// Persisted names like "Gate 1" or "T0" only restate the slot id already shown.
const GENERIC_POSITION_NAME = /^(gate|slot|lane|tool(head)?|hotend|t|g)\s*\d*$/i;

export function buildSlotView(
  slot: LoadoutSlot,
  spool: SpoolmanSpool | undefined,
  options: { id: string; active?: boolean; atRisk?: boolean; testId?: string },
): FilamentSlotView {
  const matched = spool != null && slot.spoolId != null && spool.id === slot.spoolId ? spool : undefined;
  const state = materialSlotState(slot, matched, 'unknown');
  const material = slot.material ?? matched?.material ?? undefined;
  const reportedColor = slot.color?.trim() || undefined;
  const matchedHex = matched?.colorHex?.trim().replace(/^#/, '');
  const color = reportedColor ?? (matchedHex ? `#${matchedHex}` : undefined);
  const stock: SlotStock = !state.assigned ? 'none' : state.depleted ? 'depleted' : 'assigned';
  const empty = stock === 'depleted' && !slot.material?.trim() && !reportedColor && slot.filamentPresence !== 'available';
  const weight = matched ? matched.remainingWeightG : state.assigned ? slot.remainingWeightG : undefined;
  return {
    key: slot.key,
    id: options.id,
    material,
    displayName: filamentDisplayName(material, matched?.filamentName ?? slot.filamentName),
    color,
    grams: weight != null && Number.isFinite(weight) && weight >= 0 ? weight : undefined,
    spoolId: state.assigned ? slot.spoolId : undefined,
    stock,
    empty: empty || undefined,
    active: options.active,
    disabled: slot.disabled,
    atRisk: options.atRisk,
    testId: options.testId,
    dataSource: slot.source,
    deviceName: slot.name && slot.name !== slot.label && !GENERIC_POSITION_NAME.test(slot.name.trim()) ? slot.name : undefined,
  };
}

export function slotAccessibleLabel(slot: FilamentSlotView): string {
  const prefix = slot.id ? `${slot.id}: ` : '';
  const parts: string[] = [];
  if (slot.stock === 'none') {
    parts.push(slot.displayName ? `${slot.displayName}, assign spool` : 'Assign spool');
  } else {
    parts.push(slot.displayName ?? 'Unknown material');
    const grams = formatGrams(slot.grams);
    if (slot.stock === 'depleted') parts.push(slot.empty ? 'empty' : '0 g remaining');
    else if (grams) parts.push(grams);
  }
  if (slot.active) parts.push('in use');
  else if (slot.selectedFeed) parts.push('selected, not loaded');
  if (slot.disabled) parts.push('disabled');
  if (slot.atRisk) parts.push('runout risk');
  return `${prefix}${parts.join(', ')}`;
}

/** Tooltip for the inventory warning on a slot whose assigned spool has no grams left. */
export const DEPLETED_SPOOL_HINT = 'Assigned spool has 0 g remaining';
