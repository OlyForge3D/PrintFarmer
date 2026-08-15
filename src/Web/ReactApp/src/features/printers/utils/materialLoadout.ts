import type { MmuGate, MmuStatus, ToolheadDto } from '@/types/api';
import { MmuGateStatus } from '@/types/api';
import { MmuProtocol } from '@/features/printers/constants/mmuProtocol';

/**
 * Whether a printer's filament slots are MMU/AMS gates fed into a shared hotend,
 * or genuine physical toolheads on a toolchanger. Drives every user-facing label
 * so a toolchanger is never described as an "AMS" with "gates".
 */
export type LoadoutKind = 'gate' | 'tool';

/**
 * Where a slot's filament comes from. A directly fed hotend that sits alongside
 * MMU gates (`external`) is neither an MMU gate nor part of the shared toolchanger
 * assembly, and would otherwise collide with the gate at G-code tool 0 on every
 * identity used to key the loadout — React key, coverage lookup and DOM test id.
 * Distinguishing it here keeps coverage rings, drawer state and testids stable per
 * source instead of clobbering the first gate's row.
 */
export type LoadoutSource = 'gate' | 'tool' | 'external';

export interface LoadoutSlot {
  /** Stable React key. */
  key: string;
  /**
   * Index accepted by the spool-binding APIs, i.e. the persisted `Toolhead.Index`.
   * Live MMU gate indices are mapped to persisted MMU gate records by validated
   * ordering rather than an inferred numeric offset.
   */
  apiIndex: number;
  /**
   * 0-based G-code tool index for MMU gates and physical toolheads. External
   * hotends alongside an MMU do not share this index space with gates, so this
   * field is undefined for them and the loadout keys their coverage separately.
   */
  gcodeIndex?: number;
  /** Short display label, e.g. `G1` for a gate or `T0` for a physical toolhead. */
  label: string;
  /** Full name reported by the device or the config database, when it has one. */
  name?: string;
  material?: string;
  color?: string;
  spoolId?: number;
  /** Where this slot's filament comes from. See {@link LoadoutSource}. */
  source: LoadoutSource;
  /** A physical hotend fed from an external spool alongside an MMU. */
  external?: boolean;
  /**
   * The device reports this gate as disabled ({@link MmuGateStatus.Disabled}), so
   * it cannot feed filament. It is still rendered — hiding it would renumber the
   * gates after it — but it must not be presented as assignable.
   */
  disabled?: boolean;
}

export interface MaterialLoadout {
  kind: LoadoutKind;
  /** Header label for the whole unit, e.g. `QidiBox` or `Toolheads`. */
  unitLabel: string;
  slots: LoadoutSlot[];
  /**
   * True when every live MMU gate has an unambiguous persisted MMU gate identity,
   * or when the device reports physical toolheads directly. False when the API
   * index is only a display fallback; spool mutation must remain blocked so a G1
   * assignment can never be posted to physical hotend index 0.
   */
  hasResolvedTopology: boolean;
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

function persistedGateIndicesByLiveIndex(
  liveGates: MmuGate[],
  toolheads: ToolheadDto[] | undefined,
): Map<number, number> | null {
  const persistedGates = (toolheads ?? [])
    .filter(isMmuGate)
    .sort((a, b) => a.index - b.index);
  const sortedLiveGates = [...liveGates].sort((a, b) => a.index - b.index);

  if (
    persistedGates.length !== sortedLiveGates.length ||
    new Set(persistedGates.map((gate) => gate.index)).size !== persistedGates.length ||
    sortedLiveGates.some((gate, position) => gate.index !== position)
  ) {
    return null;
  }

  return new Map(
    sortedLiveGates.map((gate, position) => [gate.index, persistedGates[position].index]),
  );
}

function slotFromGate(
  gate: MmuGate,
  position: number,
  kind: LoadoutKind,
  apiIndex: number,
): LoadoutSlot {
  const isTool = kind === 'tool';
  return {
    key: `gate-${gate.index}`,
    apiIndex,
    gcodeIndex: gate.index,
    label: isTool ? `T${gate.index}` : `G${position + 1}`,
    name: gate.name,
    material: gate.material,
    color: gate.color,
    spoolId: gate.spoolId > 0 ? gate.spoolId : undefined,
    source: isTool ? 'tool' : 'gate',
    // A toolchanger reports real toolheads over the MMU channel and has no
    // notion of a disabled gate, so only flag this for actual MMU gates.
    disabled: !isTool && gate.status === MmuGateStatus.Disabled,
  };
}

function slotFromToolhead(
  toolhead: ToolheadDto,
  position: number,
  kind: LoadoutKind,
): LoadoutSlot {
  const isTool = kind === 'tool';
  return {
    key: toolhead.id ?? `toolhead-${toolhead.index}`,
    apiIndex: toolhead.index,
    gcodeIndex: isTool ? toolhead.index : position,
    label: isTool ? `T${toolhead.index}` : `G${position + 1}`,
    name: toolhead.name,
    material: toolhead.currentMaterial,
    color: toolhead.currentFilamentColor,
    spoolId: toolhead.currentSpoolId ?? undefined,
    source: isTool ? 'tool' : 'gate',
  };
}

/**
 * Build an external-hotend slot alongside a set of MMU gates.
 *
 * The physical hotend index and the first gate's g-code index can both be `0`,
 * so keying externals with the same shape as gates collides on every identity:
 * the React key (`toolhead-0` vs the gate's `t.id`), the DOM `data-testid`
 * (`loadout-slot-0`) and the coverage lookup (`toolheadIndex === 0`). Coverage
 * is reported per g-code tool, and G1 gates already own G-code tool 0, so an
 * external hotend rendered in that same slot would inherit the gate's
 * remaining-material figures and the runout badge. This helper strips the
 * external slot's `gcodeIndex` so it never joins the shared coverage map, and
 * gives it an `external-*` React key that no gate can produce.
 */
function externalSlotFromToolhead(
  toolhead: ToolheadDto,
): LoadoutSlot {
  return {
    key: `external-${toolhead.index}`,
    apiIndex: toolhead.index,
    gcodeIndex: undefined,
    label: `T${toolhead.index}`,
    name: toolhead.name,
    material: toolhead.currentMaterial,
    color: toolhead.currentFilamentColor,
    spoolId: toolhead.currentSpoolId ?? undefined,
    source: 'external',
    external: true,
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
    const sorted = [...liveGates].sort((a, b) => a.index - b.index);
    const persistedGateIndices = kind === 'tool'
      ? null
      : persistedGateIndicesByLiveIndex(sorted, toolheads);
    // Toolchangers already report toolheads 0-based and identical to their API
    // index, so no persisted topology is needed to translate live indices safely.
    // For MMU gates the API-index offset can only be pinned down from the
    // persisted topology — without it, live G1 might land on physical hotend 0.
    const hasResolvedTopology = kind === 'tool' || persistedGateIndices !== null;
    return {
      kind,
      unitLabel: unitLabelFor(kind, mmuStatus?.mmuType),
      slots: sorted.map((gate, position) =>
        slotFromGate(
          gate,
          position,
          kind,
          kind === 'tool'
            ? gate.index
            : persistedGateIndices?.get(gate.index) ?? gate.index,
        )),
      hasResolvedTopology,
    };
  }

  if (!toolheads || toolheads.length <= 1) return null;

  const gates = toolheads.filter(isMmuGate).sort((a, b) => a.index - b.index);
  const physical = toolheads.filter((t) => !isMmuGate(t)).sort((a, b) => a.index - b.index);

  if (gates.length === 0) {
    return {
      kind: 'tool',
      unitLabel: unitLabelFor('tool'),
      slots: physical.map((t, position) => slotFromToolhead(t, position, 'tool')),
      hasResolvedTopology: true,
    };
  }

  const externals = physical
    .filter((t) => t.currentSpoolId != null || t.currentMaterial != null)
    .map((t) => externalSlotFromToolhead(t));

  return {
    kind: 'gate',
    unitLabel: unitLabelFor('gate'),
    slots: [
      ...gates.map((t, position) => slotFromToolhead(t, position, 'gate')),
      ...externals,
    ],
    hasResolvedTopology: true,
  };
}

/** Determine if a hex color is light enough to need a visible border. */
export function isLightColor(hex: string): boolean {
  const clean = hex.replace('#', '');
  // Accept the shorthand `#abc` form spec'd by CSS: expand `#abc` → `#aabbcc`
  // before the luminance check so a pale short-form swatch still shows a border.
  const normalized = clean.length === 3
    ? clean.split('').map((ch) => `${ch}${ch}`).join('')
    : clean;
  if (normalized.length < 6) return false;
  const r = parseInt(normalized.substring(0, 2), 16);
  const g = parseInt(normalized.substring(2, 4), 16);
  const b = parseInt(normalized.substring(4, 6), 16);
  if (Number.isNaN(r) || Number.isNaN(g) || Number.isNaN(b)) return false;
  const luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
  return luminance > 0.7;
}
