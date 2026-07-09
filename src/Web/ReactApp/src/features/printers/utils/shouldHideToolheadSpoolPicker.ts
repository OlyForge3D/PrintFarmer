import type { MmuGate, ToolheadDto } from '@/types/api';
import { MmuProtocol } from '@/features/printers/constants/mmuProtocol';

/**
 * Decide whether the lower "Spools / Assign spools to each toolhead" picker
 * should be hidden in PrinterDetailsSidebar to avoid duplicating AMS slot UI.
 *
 * Hide when:
 * - Live MMU gates are reported via SignalR (Klipper Happy-Hare path), except U1 physical lanes, OR
 * - Any persisted toolhead is an MmuGate (Bambu AMS, QidiBox path).
 *
 * Physical-only multi-toolhead printers (e.g., Snapmaker U1) must keep the picker.
 */
export function shouldHideToolheadSpoolPicker(
  liveMmuGates: MmuGate[] | undefined,
  toolheads: ToolheadDto[] | undefined,
  liveMmuProtocol?: string,
): boolean {
  if (liveMmuProtocol !== MmuProtocol.SnapmakerU1 && liveMmuGates && liveMmuGates.length > 0) return true;
  if (toolheads?.some(t => String(t.toolheadType) === 'MmuGate')) return true;
  return false;
}
