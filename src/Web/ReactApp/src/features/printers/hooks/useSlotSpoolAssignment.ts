import { useEffect, useRef, useState } from 'react';
import { toast } from 'sonner';
import {
  useSetToolheadSpool,
  useClearToolheadSpool,
  usePrinterDetails,
} from '@/common/hooks/useApi';
import type { LoadoutSlot } from '@/features/printers/utils/materialLoadout';

export interface SlotSpoolAssignmentOptions {
  printerId: string;
  /** Printer revision required by the optimistic-concurrency spool endpoints. */
  reviewedRowVersion?: string | null;
  /** Whether every slot's API index is unambiguous. See `MaterialLoadout.hasResolvedTopology`. */
  hasResolvedTopology: boolean;
  /** Saved topology still loading; see `MaterialLoadout.topologyPending`. */
  topologyPending?: boolean;
  onSpoolChange?: () => void;
}

export interface SlotSpoolAssignment {
  selectedKey: string | null;
  /** Select (or deselect with `null`) a slot and anchor the revision the user is reviewing. */
  selectSlot: (key: string | null) => void;
  canMutate: boolean;
  blockedReason?: string;
  busy: boolean;
  /** Binds a spool; resolves `true` on success. */
  assign: (slot: LoadoutSlot, spoolId: number) => Promise<boolean>;
  /** Releases the slot's spool; resolves `true` on success. */
  clear: (slot: LoadoutSlot) => Promise<boolean>;
}

export const DISABLED_SLOT_REASON = 'Disabled on the device — cannot take a spool';
export const TOPOLOGY_LOADING_REASON = 'Loading saved gate layout…';
export const TOPOLOGY_MISMATCH_REASON =
  'Saved gate layout does not match the attached hardware — re-check the printer configuration to assign spools';

/**
 * Spool bind/clear for a resolved material slot, shared by every surface that
 * offers assignment so they cannot drift on concurrency or topology safety.
 *
 * The revision is captured when the user opens a slot rather than read at
 * dispatch time: if a SignalR `printerupdated` lands meanwhile, the decision
 * was made against the older state, so the write must still be validated
 * against that revision (412) instead of silently overwriting. After each
 * successful mutation it is re-anchored to the response revision so a second
 * action on the same slot does not spuriously 412 against its own write.
 */
export function useSlotSpoolAssignment({
  printerId,
  reviewedRowVersion,
  hasResolvedTopology,
  topologyPending = false,
  onSpoolChange,
}: SlotSpoolAssignmentOptions): SlotSpoolAssignment {
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [lockedRevision, setLockedRevision] = useState<string | null>(null);
  const [capturedFallbackRevision, setCapturedFallbackRevision] = useState<string | null>(null);
  // The detail query can resolve after a slot opens without a card revision.
  // The reactive snapshot enables the action; this ref carries that exact
  // immutable token into handlers after cache or live updates arrive.
  const initialFallbackRevisionRef = useRef<string | null>(null);
  const setSpoolMutation = useSetToolheadSpool();
  const clearSpoolMutation = useClearToolheadSpool();
  // The compact printer DTO can arrive before its concurrency token. Fetch the
  // detail DTO only in that case.
  const { data: revisionSource } = usePrinterDetails(printerId, {
    enabled: !reviewedRowVersion,
  });
  const fallbackRevision = revisionSource?.rowVersion ?? null;
  const effectiveRowVersion = reviewedRowVersion ?? fallbackRevision;
  // Revision returned by our own last write, held until the printer revision we
  // were given moves past the one current at write time. Deselecting or opening
  // another slot before that refresh would otherwise fall back to the pre-write
  // revision and 412 against our own write.
  const [ownWrite, setOwnWrite] = useState<{ revision: string; baseline: string | null } | null>(null);
  if (ownWrite && ownWrite.baseline !== effectiveRowVersion) {
    setOwnWrite(null);
  }
  const ownWriteRevision = ownWrite && ownWrite.baseline === effectiveRowVersion ? ownWrite.revision : null;
  if (selectedKey && !lockedRevision && !capturedFallbackRevision && fallbackRevision) {
    setCapturedFallbackRevision(fallbackRevision);
  }
  const activeRevision = lockedRevision ?? ownWriteRevision ?? capturedFallbackRevision ?? effectiveRowVersion;

  useEffect(() => {
    if (
      selectedKey &&
      !lockedRevision &&
      !initialFallbackRevisionRef.current &&
      capturedFallbackRevision
    ) {
      initialFallbackRevisionRef.current = capturedFallbackRevision;
    }
  }, [capturedFallbackRevision, lockedRevision, selectedKey]);

  // Without persisted topology the live-gate → `Toolhead.Index` mapping is a
  // guess and could write a G1 assignment to the physical hotend at index 0
  // (#1585), so mutation stays blocked.
  const canMutate = !!activeRevision && hasResolvedTopology;
  const blockedReason = !activeRevision
    ? 'Printer revision unavailable — refresh to assign spools'
    : !hasResolvedTopology
      ? topologyPending
        ? TOPOLOGY_LOADING_REASON
        : TOPOLOGY_MISMATCH_REASON
      : undefined;

  const selectSlot = (key: string | null) => {
    initialFallbackRevisionRef.current = null;
    setCapturedFallbackRevision(null);
    setSelectedKey(key);
    setLockedRevision(key ? ownWriteRevision ?? effectiveRowVersion : null);
  };

  const recordWrite = (newRevision: string) => {
    setLockedRevision(newRevision);
    setOwnWrite({ revision: newRevision, baseline: effectiveRowVersion });
  };

  const requireRevision = (): string | null => {
    const revision = lockedRevision ?? ownWriteRevision ?? initialFallbackRevisionRef.current ?? activeRevision;
    if (!revision) {
      toast.error('Printer revision unavailable. Refresh and review again.');
      return null;
    }
    if (!hasResolvedTopology) {
      toast.error(topologyPending
        ? 'Saved gate layout is still loading. Try again in a moment.'
        : 'Saved gate layout does not match the attached hardware. Refresh and review again.');
      return null;
    }
    return revision;
  };

  const assign = async (slot: LoadoutSlot, spoolId: number): Promise<boolean> => {
    // A disabled gate cannot feed filament, so binding a spool to it would
    // record material the printer can never draw.
    if (slot.disabled) {
      toast.error(`${slot.label} is disabled on the device and cannot take a spool.`);
      return false;
    }
    const revision = requireRevision();
    if (!revision) return false;
    try {
      const newRevision = await setSpoolMutation.mutateAsync({
        printerId,
        toolheadIndex: slot.apiIndex,
        spoolId,
        reviewedRowVersion: revision,
      });
      recordWrite(newRevision);
      onSpoolChange?.();
      return true;
    } catch {
      // The mutation's onError toast already reported the failure; swallow so
      // React Query doesn't report an unhandled rejection.
      return false;
    }
  };

  const clear = async (slot: LoadoutSlot): Promise<boolean> => {
    const revision = requireRevision();
    if (!revision) return false;
    try {
      const newRevision = await clearSpoolMutation.mutateAsync({
        printerId,
        toolheadIndex: slot.apiIndex,
        reviewedRowVersion: revision,
      });
      recordWrite(newRevision);
      onSpoolChange?.();
      return true;
    } catch {
      return false;
    }
  };

  return {
    selectedKey,
    selectSlot,
    canMutate,
    blockedReason,
    busy: setSpoolMutation.isPending || clearSpoolMutation.isPending,
    assign,
    clear,
  };
}
