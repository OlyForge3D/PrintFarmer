import type { MmuGate, ToolheadDto } from '@/types/api';

/**
 * Convert MMU gate data (live SignalR status) to ToolheadDto format
 * so ToolheadSpoolPicker can display and manage spools for MMU printers.
 */
export function mmuGatesToToolheads(gates: MmuGate[]): ToolheadDto[] {
  return gates.map((gate) => ({
    id: `mmu-gate-${gate.index}`,
    index: gate.index,
    toolheadType: 'MmuGate',
    currentSpoolId: gate.spoolId > 0 ? gate.spoolId : undefined,
    currentMaterial: gate.material,
    currentFilamentColor: gate.color,
    isPrimary: gate.index === 0,
    name: gate.name ?? `Gate ${gate.index}`,
  }));
}
