import { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuthContext } from '@/common/contexts/auth-context';
import { MaterialLoadout } from '@/features/printers/components/MaterialLoadout';
import { MmuControlBox } from '@/features/printers/components/MmuControlBox';
import { apiClient } from '@/services/api';
import { MmuGateStatus, type MmuStatus, type ToolheadDto, type SpoolmanSpool } from '@/types/api';
import '@/index.css';

const inventory: SpoolmanSpool[] = [
  { id: 7, name: 'Depleted PLA', material: 'PLA', colorHex: '#ef4444', remainingWeightG: 0, inUse: false },
  { id: 8, name: 'Unknown PETG', material: 'PETG', colorHex: '#ffffff', remainingWeightG: null, inUse: false },
  { id: 9, name: 'Purple ASA', material: 'ASA', colorHex: '#8b5cf6', remainingWeightG: 250, inUse: false },
  { id: 99, name: 'New PLA', filamentName: 'Fixture PLA', material: 'PLA', colorHex: '#22c55e', remainingWeightG: 800, inUse: false },
];
const gates = Array.from({ length: 4 }, (_, index) => ({
  index, spoolId: 0, material: ['PLA', 'PLA', 'PETG', 'ASA'][index],
  color: ['#f59e0b', '#ef4444', '#ffffff', '#8b5cf6'][index],
  status: index === 3 ? MmuGateStatus.Empty : MmuGateStatus.Available,
}));
const mmu: MmuStatus = { enabled: true, isHomed: true, activeGate: 0, activeTool: 0,
  filamentState: 'Loaded', action: 'Idle', numGates: 4, hasBypass: true, endlessSpool: false,
  clogDetection: false, gates, mmuType: 'Qidibox' };
const qidiTools: ToolheadDto[] = Array.from({ length: 5 }, (_, index) => ({
  id: `qidi-${index}`, index, name: index === 0 ? 'Rack' : `Gate ${index}`,
  toolheadType: index === 0 ? 'Physical' : 'MmuGate',
  currentSpoolId: [9, undefined, 7, 8, 9][index],
}) as ToolheadDto);

/** Isolated real-components fixture. Every API operation is local or rejected. */
export function mountMaterials(element: HTMLElement) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
  queryClient.setQueryData(['filament-coverage', 'fleet'], { printers: [], evaluatedAtUtc: new Date().toISOString() });
  for (const id of ['qidi', 'mmu', 'multi', 'single']) queryClient.setQueryData(['printer-spools', id], inventory);

  function Fixture() {
    const [tools, setTools] = useState(qidiTools);
    const [revision, setRevision] = useState('fixture-1');
    const [write, setWrite] = useState('');
    useEffect(() => {
      const original = {
        getPrinterSpools: apiClient.getPrinterSpools, getSpools: apiClient.getSpools,
        getAvailableMaterials: apiClient.getAvailableMaterials, setToolheadSpool: apiClient.setToolheadSpool,
        clearToolheadSpool: apiClient.clearToolheadSpool, mmuGateAction: apiClient.mmuGateAction,
      };
      apiClient.getPrinterSpools = async () => inventory;
      apiClient.getSpools = async () => ({ items: inventory.filter(spool => spool.remainingWeightG !== 0), totalCount: 3 });
      apiClient.getAvailableMaterials = async () => ['PLA', 'PETG', 'ASA'];
      apiClient.setToolheadSpool = async (id, index, spoolId) => {
        setWrite(`Assign ${id} index ${index} spool ${spoolId}`);
        if (id === 'qidi') setTools(previous => previous.map(tool => tool.index === index ? { ...tool, currentSpoolId: spoolId } : tool));
        setRevision('fixture-2');
        return 'fixture-2';
      };
      apiClient.clearToolheadSpool = async (id, index) => {
        setWrite(`Unassign ${id} index ${index}`);
        if (id === 'qidi') setTools(previous => previous.map(tool => tool.index === index ? { ...tool, currentSpoolId: undefined } : tool));
        setRevision('fixture-3');
        return 'fixture-3';
      };
      apiClient.mmuGateAction = async () => { throw new Error('Physical commands are forbidden in this fixture'); };
      return () => { Object.assign(apiClient, original); };
    }, []);
    return <div className="flex flex-wrap items-start gap-6 p-3">
      <section aria-label="Sidebar material module" style={{ width: 340, maxWidth: '100%' }}>
        <MmuControlBox printerId="qidi" mmuStatus={mmu} isOnline toolheads={tools} reviewedRowVersion={revision} />
      </section>
      <section aria-label="Detail card material modules" className="space-y-4" style={{ width: 480, maxWidth: '100%' }}>
        <MaterialLoadout printerId="mmu" mmuStatus={{ ...mmu, mmuType: 'HappyHare', gates: gates.map((gate, i) => ({ ...gate, spoolId: [0, 7, 8, 9][i] })) }} toolheads={qidiTools} reviewedRowVersion="fixture-1" compact />
        <MaterialLoadout printerId="multi" toolheads={[{ id: 't0', index: 0, name: 'Left', toolheadType: 'Physical', currentSpoolId: 8 }, { id: 't1', index: 1, name: 'Right', toolheadType: 'Physical', currentSpoolId: 9 }] as ToolheadDto[]} reviewedRowVersion="fixture-1" compact />
        <MaterialLoadout printerId="single" toolheads={[]} reviewedRowVersion="fixture-1" />
      </section>
      <output data-testid="fixture-write">{write}</output>
    </div>;
  }
  createRoot(element).render(<QueryClientProvider client={queryClient}>
    <AuthContext value={{ user: null, isAuthenticated: true, isLoading: false, hasPermission: () => true,
      hasRole: () => false, error: null, login: async () => false, loginWithPasskey: async () => false,
      register: async () => false, logout: async () => {} }}><Fixture /></AuthContext>
  </QueryClientProvider>);
}
