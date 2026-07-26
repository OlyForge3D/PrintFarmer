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

/** Every known subsystem status, in the order the server treats as "worse". */
export const KNOWN_SUBSYSTEM_STATUSES: readonly KnownSubsystemStatus[] = [
  'Healthy',
  'Degraded',
  'Unhealthy',
  'Unknown',
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
  /** Optional call-to-action label (only present alongside `actionRoute`). */
  actionLabel?: string | null;
  /** Optional client-side route to navigate to. */
  actionRoute?: string | null;
}

export interface AdminOverviewDto {
  /** UTC ISO-8601 timestamp when the snapshot was generated. */
  checkedAt: string;
  /** Subsystem tiles in stable display order. */
  subsystems: SubsystemHealthDto[];
  /** Attention items pre-sorted Error → Warning → Info by the server. */
  attention: AttentionItemDto[];
}
