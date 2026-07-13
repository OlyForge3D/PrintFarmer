/**
 * Filament fallback groups — client contract (issue #718 / F6 backend #711).
 *
 * Wire format is camelCase; the backend uses `JsonStringEnumConverter` so all
 * enum-like fields (materialType, toolheadType) travel as strings. This module
 * defines a narrow typed adapter that decodes raw JSON into strict domain
 * types so the UI layer never has to touch `unknown`.
 *
 * Chain-state derivation for the UI is intentionally kept in this file so the
 * whole client contract lives together. Backend remains the source of truth for
 * validation; the client only surfaces UX affordances (mixed-material warning,
 * chain-state labels) and never overrides a backend response.
 */

import type { ToolheadCoverage } from "@/features/filament-coverage/types";

// ── Wire DTOs (decoded) ──────────────────────────────────────────────────

export interface FilamentFallbackGroupMember {
  id: string;
  toolheadId: string;
  position: number;
  toolheadName: string | null;
  toolheadIndex: number;
  currentMaterial: string | null;
  currentSpoolId: number | null;
  materialMatches: boolean;
}

export interface FilamentFallbackGroup {
  id: string;
  printerId: string;
  name: string;
  materialType: string;
  displayOrder: number;
  createdAt: string;
  updatedAt: string;
  members: FilamentFallbackGroupMember[];
}

// ── Request payloads ────────────────────────────────────────────────────

export interface CreateFilamentFallbackGroupRequest {
  name: string;
  materialType: string;
  /** Optional — backend will append at end when omitted. */
  displayOrder?: number;
  /** Ordered — position 0 is the primary/active member. */
  toolheadIds: string[];
}

export type UpdateFilamentFallbackGroupRequest = CreateFilamentFallbackGroupRequest;

// ── SignalR event ────────────────────────────────────────────────────────

/**
 * Payload of the canonical lowercase `fallbackgroupsupdated` printer-hub event.
 * The payload is an invalidation cue only — subscribers must refetch canonical
 * queries and never treat it as truth. `printerId` is always present per the
 * backend controller broadcast, but the decoder tolerates absence defensively.
 */
export interface FallbackGroupsUpdatedEvent {
  printerId: string | null;
}

// ── Decoders ─────────────────────────────────────────────────────────────

function stringOrNull(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

function nullableNumber(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function numberOr(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

function stringOr(value: unknown, fallback: string): string {
  return typeof value === "string" ? value : fallback;
}

function boolOr(value: unknown, fallback: boolean): boolean {
  return typeof value === "boolean" ? value : fallback;
}

export function decodeFilamentFallbackGroupMember(raw: unknown): FilamentFallbackGroupMember {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    id: stringOr(r.id, ""),
    toolheadId: stringOr(r.toolheadId, ""),
    position: numberOr(r.position, 0),
    toolheadName: stringOrNull(r.toolheadName),
    toolheadIndex: numberOr(r.toolheadIndex, 0),
    currentMaterial: stringOrNull(r.currentMaterial),
    currentSpoolId: nullableNumber(r.currentSpoolId),
    materialMatches: boolOr(r.materialMatches, false),
  };
}

export function decodeFilamentFallbackGroup(raw: unknown): FilamentFallbackGroup {
  const r = (raw ?? {}) as Record<string, unknown>;
  const membersRaw = Array.isArray(r.members) ? (r.members as unknown[]) : [];
  return {
    id: stringOr(r.id, ""),
    printerId: stringOr(r.printerId, ""),
    name: stringOr(r.name, ""),
    materialType: stringOr(r.materialType, ""),
    displayOrder: numberOr(r.displayOrder, 0),
    createdAt: stringOr(r.createdAt, ""),
    updatedAt: stringOr(r.updatedAt, ""),
    members: membersRaw
      .map(decodeFilamentFallbackGroupMember)
      // Backend orders by position but never trust the wire — sort defensively.
      .sort((a, b) => a.position - b.position),
  };
}

export function decodeFilamentFallbackGroups(raw: unknown): FilamentFallbackGroup[] {
  if (!Array.isArray(raw)) return [];
  return raw
    .map(decodeFilamentFallbackGroup)
    .sort((a, b) => a.displayOrder - b.displayOrder);
}

export function decodeFallbackGroupsUpdatedEvent(raw: unknown): FallbackGroupsUpdatedEvent {
  const r = (raw ?? {}) as Record<string, unknown>;
  return { printerId: typeof r.printerId === "string" ? r.printerId : null };
}

// ── Chain-state derivation ──────────────────────────────────────────────

/**
 * Per-member chain state derived from the fallback group + optional coverage
 * signal (issue #709). Backend remains authoritative — this is UX only.
 *
 * - `active`: first member with a matching-material spool loaded whose coverage
 *   (when known) is not `runout`. Only one member per group can be `active`.
 * - `backup`: subsequent matching-material members with a spool loaded that
 *   can take over. Set only when there is already an `active` upstream.
 * - `exhausted`: coverage explicitly reports `runout`. Renders as a warning
 *   glyph plus text so color is never the only signal.
 * - `empty`: no spool currently bound to the member's toolhead.
 * - `mismatch`: spool loaded but material differs from the group's material.
 */
export type FallbackMemberState =
  | "active"
  | "backup"
  | "exhausted"
  | "empty"
  | "mismatch";

export interface FallbackMemberChainState {
  member: FilamentFallbackGroupMember;
  state: FallbackMemberState;
}

export interface FallbackGroupChainState {
  group: FilamentFallbackGroup;
  /** True when at least one loaded member's material doesn't match the group. */
  mixedMaterialWarning: boolean;
  members: FallbackMemberChainState[];
}

interface CoverageLookup {
  /** Returns the coverage row for a given toolhead index, if any. */
  byIndex(index: number): ToolheadCoverage | undefined;
}

/**
 * Build a coverage lookup from an array of toolhead coverage rows.
 * Rows without a numeric index are ignored.
 */
export function buildCoverageLookup(rows: ToolheadCoverage[] | null | undefined): CoverageLookup {
  const map = new Map<number, ToolheadCoverage>();
  (rows ?? []).forEach((row) => {
    if (typeof row.toolheadIndex === "number") map.set(row.toolheadIndex, row);
  });
  return {
    byIndex(index) {
      return map.get(index);
    },
  };
}

function isMaterialMatch(member: FilamentFallbackGroupMember, materialType: string): boolean {
  // The backend already sets `materialMatches`; treat it as the truth when
  // present. Fall back to a case-insensitive comparison so the derivation
  // still works on a freshly-created group before the server has projected
  // the materialMatches flag.
  if (member.materialMatches) return true;
  if (member.currentMaterial == null) return false;
  return member.currentMaterial.trim().toLowerCase() === materialType.trim().toLowerCase();
}

/**
 * Derive per-member chain state for a single group, optionally enriched with
 * per-toolhead coverage (from `usePrinterFilamentCoverage`). Coverage is
 * optional; when omitted, `exhausted` cannot be determined and members with a
 * matching-material spool cascade as `active` → `backup` on subsequent slots.
 */
export function deriveFallbackGroupChainState(
  group: FilamentFallbackGroup,
  coverage?: CoverageLookup,
): FallbackGroupChainState {
  let sawActive = false;
  let mixedMaterialWarning = false;

  const members = group.members.map<FallbackMemberChainState>((member) => {
    // Empty slot: no spool loaded to that toolhead.
    if (member.currentSpoolId == null) {
      return { member, state: "empty" };
    }

    // Coverage-informed exhausted state trumps material check because a spool
    // that ran out cannot serve regardless of its material.
    const cov = coverage?.byIndex(member.toolheadIndex);
    if (cov?.status === "runout") {
      return { member, state: "exhausted" };
    }

    // Loaded spool but wrong material → mismatch. Warn on the group.
    if (!isMaterialMatch(member, group.materialType)) {
      mixedMaterialWarning = true;
      return { member, state: "mismatch" };
    }

    // Material matches: first eligible becomes active; the rest are backups.
    if (!sawActive) {
      sawActive = true;
      return { member, state: "active" };
    }
    return { member, state: "backup" };
  });

  return { group, mixedMaterialWarning, members };
}

// ── Client-side validation mirrors backend rules (issue #711) ────────────

export interface FallbackGroupValidationError {
  field: "name" | "materialType" | "toolheadIds";
  message: string;
}

/**
 * Validate a fallback-group draft the same way the backend does. Backend
 * remains authoritative — this exists only so the client can render inline
 * errors before a round-trip.
 */
export function validateFallbackGroupDraft(
  draft: CreateFilamentFallbackGroupRequest,
  existing: readonly FilamentFallbackGroup[],
  /** Set of physical toolhead ids available on the printer. */
  physicalToolheadIds: ReadonlySet<string>,
  /** When editing, exclude the group being edited from the uniqueness check. */
  editingGroupId?: string,
): FallbackGroupValidationError[] {
  const errors: FallbackGroupValidationError[] = [];

  const trimmedName = draft.name.trim();
  if (trimmedName.length === 0) {
    errors.push({ field: "name", message: "Name is required." });
  } else {
    const nameLower = trimmedName.toLowerCase();
    const collision = existing.some(
      (g) => g.id !== editingGroupId && g.name.trim().toLowerCase() === nameLower,
    );
    if (collision) {
      errors.push({ field: "name", message: `A group named "${trimmedName}" already exists.` });
    }
  }

  if (draft.materialType.trim().length === 0) {
    errors.push({ field: "materialType", message: "Material type is required." });
  }

  if (draft.toolheadIds.length === 0) {
    errors.push({
      field: "toolheadIds",
      message: "Add at least one physical toolhead to the chain.",
    });
  } else {
    const seen = new Set<string>();
    for (const id of draft.toolheadIds) {
      if (!physicalToolheadIds.has(id)) {
        errors.push({
          field: "toolheadIds",
          message: "Only physical toolheads can participate in a fallback chain.",
        });
        break;
      }
      if (seen.has(id)) {
        errors.push({
          field: "toolheadIds",
          message: "Each toolhead can appear at most once in a chain.",
        });
        break;
      }
      seen.add(id);
    }
  }

  return errors;
}
