/**
 * Client-side mirror of `Farm.Infrastructure.Dtos.AdminOverviewDto` and friends.
 *
 * The server serializes both status enums as **strings** via `JsonStringEnumConverter`
 * (see `src/api/Program.cs` and `src/infra/Dtos/AdminOverviewDto.cs`). Consumers must
 * treat `status` and `severity` as strings, not numbers.
 *
 * Forward compatibility: the backend can add subsystems (e.g. `spoolman`) or ship a
 * newer enum member without a frontend release. The DTO fields are typed as `string`
 * on purpose so unknown values still type-check; discriminated unions below keep the
 * known members callable, and the UI degrades unknown values to the "Unknown" /
 * "Info" treatment via {@link isKnownSubsystemStatus} / {@link isKnownAttentionSeverity}.
 */

/** Known subsystem status values sent by the API. */
export type KnownSubsystemStatus = 'Healthy' | 'Degraded' | 'Unhealthy' | 'Unknown';

/** Every known subsystem status, in the order the server treats as "worse".
 * `Unhealthy` (a confirmed, actionable failure) outranks `Unknown` (an
 * unconfirmed probe timeout/parse failure) so a real failure in one subsystem
 * is never masked behind an "Unknown" reported by a different subsystem in
 * the same overview (see issue #2222). */
export const KNOWN_SUBSYSTEM_STATUSES: readonly KnownSubsystemStatus[] = [
  'Healthy',
  'Degraded',
  'Unknown',
  'Unhealthy',
];

/** Type guard: does the raw value from the API match a known status? */
export function isKnownSubsystemStatus(value: string): value is KnownSubsystemStatus {
  return (KNOWN_SUBSYSTEM_STATUSES as readonly string[]).includes(value);
}

/** Known attention severity values sent by the API. */
export type KnownAttentionSeverity = 'Info' | 'Warning' | 'Error';

/** Every known severity, most severe first (matches the server's sort order). */
export const KNOWN_ATTENTION_SEVERITIES: readonly KnownAttentionSeverity[] = [
  'Error',
  'Warning',
  'Info',
];

/** Type guard: does the raw value from the API match a known severity? */
export function isKnownAttentionSeverity(value: string): value is KnownAttentionSeverity {
  return (KNOWN_ATTENTION_SEVERITIES as readonly string[]).includes(value);
}

export interface SubsystemHealthDto {
  /** Stable machine key (e.g. `"database"`, `"signalr"`, `"spoolman"`). */
  key: string;
  /** Human-readable subsystem name. */
  name: string;
  /**
   * Current status. Typed as `string` because the backend may add new enum
   * members without a frontend release; use {@link isKnownSubsystemStatus}
   * to discriminate.
   */
  status: string;
  /** Optional short one-line detail. May be `null`. */
  detail?: string | null;
}

export interface AttentionItemDto {
  /** Stable identifier so the UI can key on it. */
  key: string;
  /**
   * Severity. Typed as `string` because the backend may add new enum members
   * without a frontend release; use {@link isKnownAttentionSeverity} to discriminate.
   */
  severity: string;
  /** Plain-language title of what is wrong. */
  title: string;
  /** Additional detail explaining the issue. */
  detail: string;
  /** Optional call-to-action label (only present alongside a destination id or route). */
  actionLabel?: string | null;
  /**
   * Preferred navigation target: the stable id of an entry in `ADMIN_DESTINATIONS`.
   * When present, the client resolves the id to the current canonical path. This keeps
   * route renames a frontend concern — the backend never has to know URLs it does not own.
   */
  actionDestinationId?: string | null;
  /**
   * Fallback client-side route to navigate to when {@link actionDestinationId} is not
   * provided or does not resolve (e.g. non-admin operational pages like `/printers`).
   */
  actionRoute?: string | null;
}

export interface AdminOverviewDto {
  /** UTC ISO-8601 timestamp when the snapshot was generated. */
  checkedAt: string;
  /**
   * The single worst status across `subsystems` (server-computed roll-up; see
   * `AdminOverviewService.ComputeOverallStatus`). Always render this for any
   * overall/summary status indicator instead of assuming "Healthy" — a degraded
   * or unhealthy subsystem must never be masked by a contradictory "all clear"
   * header (see issue #2222). Typed as `string` for the same forward-compatibility
   * reason as {@link SubsystemHealthDto.status}; use {@link isKnownSubsystemStatus}.
   */
  overallStatus: string;
  /** Subsystem tiles in stable display order. */
  subsystems: SubsystemHealthDto[];
  /** Attention items pre-sorted Error → Warning → Info by the server. */
  attention: AttentionItemDto[];
}
