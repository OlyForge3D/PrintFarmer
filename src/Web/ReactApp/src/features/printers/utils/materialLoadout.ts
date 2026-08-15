import type { MmuGate, MmuStatus, ToolheadDto } from '@/types/api';
import { MmuProtocol } from '@/features/printers/constants/mmuProtocol';

/**
 * Whether a printer's filament slots are MMU/AMS gates fed into a shared hotend,
 * or genuine physical toolheads on a toolchanger. Drives every user-facing label
 * so a toolchanger is never described as an "AMS" with "gates".
 */
export type LoadoutKind = 'gate' | 'tool';

export interface LoadoutSlot {
  /** Stable React key. */
  key: string;
  /**
   * Index accepted by the spool-binding APIs, i.e. the persisted `Toolhead.Index`.
   * MMU gates are persisted at 1..N because the shared hotend occupies index 0,
   * while live MMU status reports the same gates 0-based — this field carries the
   * translated value so an assignment lands on the slot the user clicked.
   */
  apiIndex: number;
  /**
   * 0-based G-code tool index. Filament coverage rows are keyed by this, so it is
   * the join key between a slot and its remaining/demand figures.
   */
  gcodeIndex: number;
  /** Short display label, e.g. `G1` for a gate or `T0` for a physical toolhead. */
  label: string;
  /** Full name reported by the device or the config database, when it has one. */
  name?: string;
  material?: string;
  color?: string;
  spoolId?: number;
  /** A physical hotend fed from an external spool alongside an MMU. */
  external?: boolean;
}

export interface MaterialLoadout {
  kind: LoadoutKind;
  /** Header label for the whole unit, e.g. `QidiBox` or `Toolheads`. */
  unitLabel: string;
  slots: LoadoutSlot[];
}

function isMmuGate(toolhead: ToolheadDto): boolean {
  return String(toolhead.toolheadType) === 'MmuGate';
}

/**
 * A Snapmaker U1 reports its physical toolheads over the MMU status channel.
 * They are real toolheads, so they must never be labelled as AMS gates.
 */
function isToolchangerProtocol(mmuType?: string): boolean {
  return mmuType === MmuProtocol.SnapmakerU1;
}

function unitLabelFor(kind: LoadoutKind, mmuType?: string): string {
  if (kind === 'tool') return 'Toolheads';
  switch (mmuType) {
    case MmuProtocol.Qidibox:
      return 'QidiBox';
    case MmuProtocol.Afc:
      return 'AFC';
    case MmuProtocol.HappyHare:
      return 'MMU';
    default:
      return 'AMS';
  }
}

/**
 * Live MMU gates are 0-based, but the backend persists MMU gates at 1..N. Rather
 * than assume one convention globally, read the offset off the persisted topology:
 * gates starting at 1 mean live gate `n` is persisted toolhead `n + 1`. When no
 * gates are persisted yet the indices are passed through unchanged, which matches
 * the backend's own auto-materialization path.
 */
function persistedGateOffset(toolheads: ToolheadDto[] | undefined): number {
  const gates = toolheads?.filter(isMmuGate) ?? [];
  if (gates.length === 0) return 0;
  return Math.min(...gates.map((gate) => gate.index)) > 0 ? 1 : 0;
}

function slotFromGate(
  gate: MmuGate,
  position: number,
  kind: LoadoutKind,
  apiOffset: number,
): LoadoutSlot {
  const isTool = kind === 'tool';
  return {
    key: `gate-${gate.index}`,
    apiIndex: isTool ? gate.index : gate.index + apiOffset,
    gcodeIndex: gate.index,
    label: isTool ? `T${gate.index}` : `G${position + 1}`,
    name: gate.name,
    material: gate.material,
    color: gate.color,
    spoolId: gate.spoolId > 0 ? gate.spoolId : undefined,
  };
}

function slotFromToolhead(
  toolhead: ToolheadDto,
  position: number,
  kind: LoadoutKind,
  gcodeOffset: number,
): LoadoutSlot {
  const isTool = kind === 'tool';
  return {
    key: toolhead.id ?? `toolhead-${toolhead.index}`,
    apiIndex: toolhead.index,
    gcodeIndex: isTool ? toolhead.index : toolhead.index - gcodeOffset,
    label: isTool ? `T${toolhead.index}` : `G${position + 1}`,
    name: toolhead.name,
    material: toolhead.currentMaterial,
    color: toolhead.currentFilamentColor,
    spoolId: toolhead.currentSpoolId ?? undefined,
  };
}

/**
 * Collapse the live MMU status and the persisted toolhead topology into a single
 * ordered list of filament slots.
 *
 * Live status wins on slot *count* because it reflects the hardware actually
 * attached, which is why a four-slot QidiBox no longer renders as three gates when
 * the config database has fallen behind. Persisted toolheads are still consulted to
 * translate indices, so the slot a user clicks is the slot the API writes to.
 *
 * Returns `null` when the printer has nothing multi-slot worth rendering.
 */
export function resolveMaterialLoadout(
  mmuStatus: MmuStatus | undefined,
  toolheads: ToolheadDto[] | undefined,
): MaterialLoadout | null {
  const liveGates = mmuStatus?.gates;

  if (liveGates && liveGates.length > 0) {
    const kind: LoadoutKind = isToolchangerProtocol(mmuStatus?.mmuType) ? 'tool' : 'gate';
    const apiOffset = kind === 'tool' ? 0 : persistedGateOffset(toolheads);
    const sorted = [...liveGates].sort((a, b) => a.index - b.index);
    return {
      kind,
      unitLabel: unitLabelFor(kind, mmuStatus?.mmuType),
      slots: sorted.map((gate, position) => slotFromGate(gate, position, kind, apiOffset)),
    };
  }

  if (!toolheads || toolheads.length <= 1) return null;

  const gates = toolheads.filter(isMmuGate).sort((a, b) => a.index - b.index);
  const physical = toolheads.filter((t) => !isMmuGate(t)).sort((a, b) => a.index - b.index);

  if (gates.length === 0) {
    return {
      kind: 'tool',
      unitLabel: unitLabelFor('tool'),
      slots: physical.map((t, position) => slotFromToolhead(t, position, 'tool', 0)),
    };
  }

  const gcodeOffset = gates[0].index > 0 ? 1 : 0;
  const externals = physical
    .filter((t) => t.currentSpoolId != null || t.currentMaterial != null)
    .map((t, position) => ({ ...slotFromToolhead(t, position, 'tool', 0), external: true }));

  return {
    kind: 'gate',
    unitLabel: unitLabelFor('gate'),
    slots: [
      ...gates.map((t, position) => slotFromToolhead(t, position, 'gate', gcodeOffset)),
      ...externals,
    ],
  };
}

/** Determine if a hex color is light enough to need a visible border. */
export function isLightColor(hex: string): boolean {
  const clean = hex.replace('#', '');
  if (clean.length < 6) return false;
  const r = parseInt(clean.substring(0, 2), 16);
  const g = parseInt(clean.substring(2, 4), 16);
  const b = parseInt(clean.substring(4, 6), 16);
  const luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
  return luminance > 0.7;
}
