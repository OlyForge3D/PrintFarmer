/**
 * Fleet-level gate for the shared failure-detection status poll (#1146 item
 * 3). `usePrinterFailureDetectionStatus` already shares one query key across
 * every card (`FAILURE_DETECTION_STATUS_QUERY_KEY`); the remaining problem
 * was that each card independently computed its OWN `enabled` flag from just
 * its own printer (`!!printer.obicoEnabled`), which is composition-dependent:
 * whether the shared query polls at all — and how many redundant
 * per-observer poll timers get scheduled — ends up depending on which mix of
 * printers happens to be mounted at once, rather than on a single fleet-wide
 * decision.
 *
 * This context lets a page compute "does ANY relevant printer have Obico
 * enabled" exactly once (e.g. in `PrintersPage`, from the full printer list)
 * and share that single boolean with every card, instead of prop-drilling it
 * through `CompactPrinterCard`/`DetailedPrinterCard`'s props. Cards outside
 * a provider (e.g. isolated tests/stories) get the default of `false`,
 * matching the previous "no explicit opt-in" behavior.
 */
import { useContext } from 'react';
import { FailureDetectionPollingContext } from '@/features/printers/hooks/FailureDetectionPollingContext';

/** Whether the fleet has at least one printer opted into any failure-detection provider. */
export function hasFailureDetectionEnabledPrinter(
  printers: readonly Pick<import('@/types/api').Printer, 'obicoEnabled' | 'printGuardEnabled'>[]
): boolean {
  return printers.some((printer) => !!printer.obicoEnabled || !!printer.printGuardEnabled);
}

/** Whether the fleet currently has at least one failure-detection-enabled printer. */
export function useFailureDetectionPollingEnabled(): boolean {
  return useContext(FailureDetectionPollingContext);
}