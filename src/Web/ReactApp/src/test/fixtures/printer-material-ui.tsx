import { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuthContext } from '@/common/contexts/auth-context';
import { MaterialLoadout, type FilamentMacroControl } from '@/features/printers/components/MaterialLoadout';
import { MmuControlBox } from '@/features/printers/components/MmuControlBox';
import { MmuProtocol } from '@/features/printers/constants/mmuProtocol';
import { apiClient } from '@/services/api';
import { MmuGateStatus, type MmuStatus, type ToolheadDto, type SpoolmanSpool } from '@/types/api';
import '@/index.css';

const inventory: SpoolmanSpool[] = [
  { id: 7, name: 'Depleted PLA', material: 'PLA', filamentName: 'Red', colorHex: '#ef4444', remainingWeightG: 0, inUse: false },
  { id: 8, name: 'Unknown PETG', material: 'PETG', filamentName: 'White', colorHex: '#f5f5f5', remainingWeightG: null, inUse: false },
  { id: 9, name: 'Purple ASA', material: 'ASA', filamentName: 'Purple', colorHex: '#8b5cf6', remainingWeightG: 250, inUse: false },
  { id: 10, name: 'Black PLA', material: 'PLA', filamentName: 'Black', colorHex: '#1f2937', remainingWeightG: 340, inUse: true },
  { id: 99, name: 'New PLA', filamentName: 'Fixture PLA', material: 'PLA', colorHex: '#22c55e', remainingWeightG: 800, inUse: false },
];

const qidiGates = [0, 1, 2, 3].map(index => ({
  index, spoolId: 0, material: ['PLA', 'PLA', undefined, 'PETG'][index],
  color: ['#1f2937', '#ef4444', undefined, '#f5f5f5'][index],
  status: index === 2 ? MmuGateStatus.Empty : MmuGateStatus.Available,
}));
const qidiMmu: MmuStatus = { enabled: true, isHomed: true, activeGate: 0, activeTool: 0,
  filamentState: 'Loaded', action: 'Idle', numGates: 4, hasBypass: true, endlessSpool: false,
  clogDetection: false, gates: qidiGates, mmuType: MmuProtocol.Qidibox } as MmuStatus;
const qidiTools: ToolheadDto[] = [0, 1, 2, 3, 4].map(index => ({
  id: `qidi-${index}`, index, name: index === 0 ? 'Toolhead 0' : `Gate ${index}`,
  toolheadType: index === 0 ? 'Physical' : 'MmuGate',
  currentSpoolId: [9, 10, 7, undefined, 8][index],
}) as ToolheadDto);

const u1Mmu: MmuStatus = { ...qidiMmu, mmuType: MmuProtocol.SnapmakerU1, hasBypass: false, activeGate: 1, activeTool: 1,
  gates: [0, 1, 2, 3].map(index => ({ index, spoolId: [10, 9, 0, 8][index], material: ['PLA', 'ASA', undefined, 'PETG'][index],
    color: ['#1f2937', '#8b5cf6', undefined, '#f5f5f5'][index], status: index === 2 ? MmuGateStatus.Empty : MmuGateStatus.Available })) } as MmuStatus;
const u1Tools: ToolheadDto[] = [0, 1, 2, 3].map(index => ({ id: `u1-${index}`, index, name: `T${index}`,
  toolheadType: 'Physical', currentSpoolId: [10, 9, undefined, 8][index] }) as ToolheadDto);

const ad5xTools: ToolheadDto[] = [0, 1, 2, 3, 4].map(index => ({
  id: `ad5x-${index}`, index, name: index === 0 ? 'Hotend' : `Gate ${index}`,
  toolheadType: index === 0 ? 'Physical' : 'MmuGate', currentSpoolId: [undefined, 10, 9, 7, undefined][index],
}) as ToolheadDto);

const WIDTHS = { card: 380, sidebar: 240 } as const;

/** Isolated real-components fixture. Every API operation is local or rejected; nothing reaches a printer. */
export function mountMaterials(element: HTMLElement) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
  queryClient.setQueryData(['filament-coverage', 'fleet'], { printers: [], evaluatedAtUtc: new Date().toISOString() });
  const ids = ['qidi', 'qidi-rack', 'u1', 'ad5x', 'single', 'single-none'].flatMap(id => [`${id}-card`, `${id}-sidebar`]);
  for (const id of ids) queryClient.setQueryData(['printer-spools', id], inventory);

  function Fixture() {
    const [write, setWrite] = useState('');
    useEffect(() => {
      const original = {
        getPrinterSpools: apiClient.getPrinterSpools, getSpools: apiClient.getSpools,
        getAvailableMaterials: apiClient.getAvailableMaterials, setToolheadSpool: apiClient.setToolheadSpool,
        clearToolheadSpool: apiClient.clearToolheadSpool, mmuGateAction: apiClient.mmuGateAction,
      };
      apiClient.getPrinterSpools = async () => inventory;
      apiClient.getSpools = async () => ({ items: inventory.filter(spool => spool.remainingWeightG !== 0), totalCount: 4 });
      apiClient.getAvailableMaterials = async () => ['PLA', 'PETG', 'ASA'];
      apiClient.setToolheadSpool = async (id, index, spoolId) => { setWrite(`Assign ${id} index ${index} spool ${spoolId}`); return 'fixture-2'; };
      apiClient.clearToolheadSpool = async (id, index) => { setWrite(`Unassign ${id} index ${index}`); return 'fixture-3'; };
      apiClient.mmuGateAction = async (id, request) => {
        setWrite(`Gate ${request.action} ${id} ${request.gateIndex ?? request.laneName}`);
        return { success: true, message: 'fixture' } as Awaited<ReturnType<typeof apiClient.mmuGateAction>>;
      };
      return () => { Object.assign(apiClient, original); };
    }, []);
    const macros = (id: string): FilamentMacroControl => ({ onAction: action => setWrite(`Macro ${action} ${id}`) });

    const devices = (surface: keyof typeof WIDTHS) => [
      ['QidiBox', <MmuControlBox key="q" printerId={`qidi-${surface}`} mmuStatus={qidiMmu} isOnline toolheads={qidiTools}
        modelName="Qidi Plus4" reviewedRowVersion="fixture-1" filamentControl={macros(`qidi-${surface}`)} />],
      ['QidiBox rack active, rack hidden', <MmuControlBox key="qr" printerId={`qidi-rack-${surface}`} mmuStatus={{ ...qidiMmu, activeGate: -2 }}
        isOnline toolheads={qidiTools} modelName="Qidi Plus4" reviewedRowVersion="fixture-1" />],
      ['U1', <MaterialLoadout key="u" printerId={`u1-${surface}`} mmuStatus={u1Mmu} toolheads={u1Tools} modelName="Snapmaker U1"
        reviewedRowVersion="fixture-1" compact={surface === 'card'} isOnline filamentControl={macros(`u1-${surface}`)} />],
      ['AD5X', <MaterialLoadout key="a" printerId={`ad5x-${surface}`} toolheads={ad5xTools} modelName="Flashforge AD5X"
        reviewedRowVersion="fixture-1" compact={surface === 'card'} isOnline filamentControl={macros(`ad5x-${surface}`)} />],
      ['Single toolhead with spool', <MaterialLoadout key="s" printerId={`single-${surface}`} toolheads={[]} currentSpoolId={10}
        reviewedRowVersion="fixture-1" compact={surface === 'card'} isOnline filamentControl={macros(`single-${surface}`)} />],
      ['Single toolhead without spool', <MaterialLoadout key="n" printerId={`single-none-${surface}`} toolheads={[]}
        reviewedRowVersion="fixture-1" compact={surface === 'card'} isOnline filamentControl={macros(`single-none-${surface}`)} />],
    ] as const;

    return <div className="flex flex-wrap items-start gap-10 p-4">
      {(Object.keys(WIDTHS) as (keyof typeof WIDTHS)[]).map(surface => (
        <div key={surface} className="space-y-6" style={{ width: WIDTHS[surface] }}>
          {devices(surface).map(([name, node]) => (
            <section key={name} aria-label={`${surface} ${name}`} data-fixture-surface={surface}
              className={surface === 'sidebar' ? 'bg-pf-sidebar' : 'bg-pf-bg-1'}>
              <p className="mb-1 text-[10px] uppercase text-pf-text-tertiary">{surface} · {name}</p>
              {node}
            </section>
          ))}
        </div>
      ))}
      <output data-testid="fixture-write">{write}</output>
    </div>;
  }
  createRoot(element).render(<QueryClientProvider client={queryClient}>
    <AuthContext value={{ user: null, isAuthenticated: true, isLoading: false, hasPermission: () => true,
      hasRole: () => false, error: null, login: async () => false, loginWithPasskey: async () => false,
      register: async () => false, logout: async () => {} }}><Fixture /></AuthContext>
  </QueryClientProvider>);
}
