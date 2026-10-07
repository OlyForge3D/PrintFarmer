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
  /**
   * True while live MMU gates are known but the persisted toolhead topology has
   * not loaded yet, so an unresolved topology means "wait", not "mismatch".
   */
  topologyPending: boolean;
}

/**
 * The backend only materializes missing MMU gates up to `Math.Max(4, index)`
 * (PrintersService gap-fill), so a smaller unit would gain phantom gates that
 * then exceed its live gate count. Gap-filling is only safe at or above this.
 */
const MIN_GAP_FILL_LIVE_GATES = 4;

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

function unitLabelFor(kind: LoadoutKind, mmuType?: string, slotCount?: number): string {
  if (kind === 'tool') return slotCount === 1 ? 'Toolhead' : 'Toolheads';
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
    new Set(persistedGates.map((gate) => gate.index)).size !== persistedGates.length ||
    sortedLiveGates.some((gate, position) => gate.index !== position)
  ) {
    return null;
  }

  if (persistedGates.length === sortedLiveGates.length) {
    return new Map(
      sortedLiveGates.map((gate, position) => [gate.index, persistedGates[position].index]),
    );
  }

  // The config database can lag the hardware (e.g. 3 persisted gates behind a
  // four-slot QidiBox). When the persisted gates are exactly the canonical
  // prefix `1..M` the backend creates (Toolhead.Index = live gate + 1, see
  // ToolheadIndexMapper), the mapping for the remaining live gates is not a
  // guess: binding index M+1..N makes the backend create exactly the missing
  // gate without renumbering existing ones (#1588). The backend declines to
  // create gates when more than one physical toolhead is persisted, and pads to
  // at least four gates, so both shapes stay blocked here.
  const physicalToolheadCount = (toolheads ?? []).filter((toolhead) => !isMmuGate(toolhead)).length;
  const isCanonicalPrefix = persistedGates.length > 0
    && sortedLiveGates.length >= MIN_GAP_FILL_LIVE_GATES
    && physicalToolheadCount <= 1
    && persistedGates.length < sortedLiveGates.length
    && persistedGates.every((gate, position) => gate.index === position + 1)
    // Never let a live-only gate land on a persisted physical toolhead index.
    && !(toolheads ?? []).some((toolhead) =>
      !isMmuGate(toolhead)
      && toolhead.index > persistedGates.length
      && toolhead.index <= sortedLiveGates.length);
  if (!isCanonicalPrefix) {
    return null;
  }

  return new Map(sortedLiveGates.map((gate) => [gate.index, gate.index + 1]));
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
 * A QidiBox printer also has an external spool holder ("Rack", firmware
 * `filament_slot16`) that feeds the hotend directly and is not one of the box's
 * slots. Its spool binding lives on the persisted physical toolhead, never on a
 * gate, so it is surfaced from that toolhead's own index. With more than one
 * physical toolhead the holder's identity is ambiguous, so nothing is surfaced.
 * The holder is only surfaced when the device reports it (`hasBypass`).
 */
export function resolveQidiRackSlot(
  mmuStatus: MmuStatus | null | undefined,
  toolheads: ToolheadDto[] | undefined,
): LoadoutSlot | null {
  if (mmuStatus?.mmuType !== MmuProtocol.Qidibox || !mmuStatus.hasBypass) return null;
  const physical = (toolheads ?? []).filter((toolhead) => !isMmuGate(toolhead));
  if (physical.length !== 1) return null;
  return { ...externalSlotFromToolhead(physical[0]), label: 'Rack' };
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
  currentSpoolId?: number | null,
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
    const persistedByIndex = new Map((toolheads ?? []).filter(isMmuGate).map((t) => [t.index, t]));
    return {
      kind,
      unitLabel: unitLabelFor(kind, mmuStatus?.mmuType, sorted.length),
      slots: sorted.map((gate, position) => {
        const apiIndex = kind === 'tool'
          ? gate.index
          : persistedGateIndices?.get(gate.index) ?? gate.index;
        const slot = slotFromGate(gate, position, kind, apiIndex);
        // Many units (e.g. QidiBox) do not report a Spoolman id per gate, so the
        // persisted binding is the only record of which spool sits in the slot.
        const persisted = persistedGateIndices ? persistedByIndex.get(apiIndex) : undefined;
        if (slot.spoolId == null && persisted?.currentSpoolId != null && persisted.currentSpoolId > 0) {
          slot.spoolId = persisted.currentSpoolId;
          slot.material ??= persisted.currentMaterial;
          slot.color ??= persisted.currentFilamentColor;
        }
        return slot;
      }),
      hasResolvedTopology,
      topologyPending: kind === 'gate' && toolheads === undefined,
    };
  }

  // Single-toolhead (or zero-toolhead) printers: render a one-slot rail rather
  // than falling through to the legacy Spool section.
  if (!toolheads || toolheads.length <= 1) {
    const single = toolheads?.[0];
    if (single) {
      return {
        kind: 'tool',
        unitLabel: unitLabelFor('tool', undefined, 1),
        slots: [slotFromToolhead(single, 0, 'tool')],
        hasResolvedTopology: true,
        topologyPending: false,
      };
    }
    // No persisted toolheads at all — synthesize a slot from printer-level spool
    // if one exists (Hazard 6: zero toolheads with currentSpoolId still needs a rail).
    if (currentSpoolId != null && currentSpoolId > 0) {
      return {
        kind: 'tool',
        unitLabel: unitLabelFor('tool', undefined, 1),
        slots: [{
          key: 'toolhead-0',
          apiIndex: 0,
          gcodeIndex: 0,
          label: 'T0',
          material: undefined,
          color: undefined,
          spoolId: currentSpoolId,
          source: 'tool',
        }],
        hasResolvedTopology: true,
        topologyPending: false,
      };
    }
    return null;
  }

  const gates = toolheads.filter(isMmuGate).sort((a, b) => a.index - b.index);
  const physical = toolheads.filter((t) => !isMmuGate(t)).sort((a, b) => a.index - b.index);

  if (gates.length === 0) {
    return {
      kind: 'tool',
      unitLabel: unitLabelFor('tool', undefined, physical.length),
      slots: physical.map((t, position) => slotFromToolhead(t, position, 'tool')),
      hasResolvedTopology: true,
      topologyPending: false,
    };
  }

  const externals = physical
    .filter((t) => t.currentSpoolId != null || t.currentMaterial != null)
    .map((t) => externalSlotFromToolhead(t));

  // Persisted-only gate fallback (no live MMU status). Slots key gcodeIndex by
  // position, which coincides with activeGate only when persisted gates are
  // 0-based and contiguous. In practice activeGate is -1/-2 on this path (no
  // live MMU data), so the active indicator is intentionally suppressed here.
  return {
    kind: 'gate',
    unitLabel: unitLabelFor('gate', undefined, gates.length),
    slots: [
      ...gates.map((t, position) => slotFromToolhead(t, position, 'gate')),
      ...externals,
    ],
    hasResolvedTopology: true,
    topologyPending: false,
  };
}

/**
 * The filament-loaded state on the active slot. `loaded` means filament is
 * physically engaged (not merely selected); `selected` means the gate/tool is
 * active but filament may not be engaged; `none` means no slot is active.
 */
export type ActiveSlotState = 'loaded' | 'selected' | 'none';

export interface ActiveSlotInfo {
  /** The live g-code index of the active gate/tool. */
  gcodeIndex: number;
  state: ActiveSlotState;
}

/**
 * Resolve which slot is active (and whether filament is loaded) from MMU status.
 *
 * Hazards addressed:
 * - Sentinels -1 (none) and -2 (unknown) → returns null.
 * - External slots must never match → caller checks `slot.source !== 'external'`.
 * - Uses `filamentState` to distinguish loaded vs merely selected.
 * - For toolchangers, `activeTool` is used; for MMU/AMS, `activeGate`.
 */
export function resolveActiveSlot(
  mmuStatus: MmuStatus | undefined,
  kind: LoadoutKind,
): ActiveSlotInfo | null {
  if (!mmuStatus) return null;

  const rawIndex = kind === 'tool' ? mmuStatus.activeTool : mmuStatus.activeGate;

  // Hazard 4: sentinels -1 (none) and -2 (unknown) must not map to slot 0.
  if (rawIndex == null || rawIndex < 0) return null;

  const filament = mmuStatus.filamentState?.toLowerCase();
  const state: ActiveSlotState = filament === 'loaded' ? 'loaded' : 'selected';

  return { gcodeIndex: rawIndex, state };
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
