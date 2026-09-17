import type { UpdateChannelSettings } from "@/types/api";

/**
 * Thrown by an `onSaveUpdateChannel` handler when the POST attempt could not
 * be confirmed as applied: an authoritative refetch succeeded, but the
 * settings it returned do not match what was requested.
 *
 * This is a confirmed rejection/unchanged state, not an unknown outcome. The
 * refetch is the source of truth regardless of whether the POST promise
 * itself resolved or rejected (a resolved POST with a mismatched refetch is
 * just as much a rejection as a rejected POST followed by an unchanged
 * refetch) — the UI must reconcile to these authoritative settings instead
 * of claiming success.
 */
export class UpdateChannelSaveRejectedError extends Error {
  constructor(public readonly authoritative: UpdateChannelSettings) {
    super(
      "UpdateChannel save was not applied; the authoritative settings do not match the request.",
    );
    this.name = "UpdateChannelSaveRejectedError";
  }
}