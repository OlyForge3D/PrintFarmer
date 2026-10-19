import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuthContext } from '@/common/contexts/auth-context';
import { Button } from '@/common/components/ui';
import { CompactPrinterCard } from '@/features/printers/components/CompactPrinterCard';
import { DetailedPrinterCard } from '@/features/printers/components/DetailedPrinterCard';
import { PrinterCardGrid } from '@/features/printers/components/PrinterCardGrid';
import { PrinterBackend, type Printer } from '@/types/api';
import '@/index.css';

const STATES = ['Idle', 'Printing', 'Paused', 'Offline', 'Idle', 'Printing'] as const;

// Real cards and controls, synthetic printer states, and no live API or hardware.
export function mountPrinterCards(element: HTMLElement) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { enabled: false, retry: false, staleTime: Infinity } },
  });
  queryClient.setQueryData(['settings', 'user'], { printerControlMode: 'Expert' });
  const printers = Array.from({ length: 60 }, (_, index) => ({
    id: `density-${index}`,
    name: `Fixture ${index + 1}`,
    backend: PrinterBackend.Moonraker,
    backendUrl: 'http://emulator.invalid',
    state: STATES[index % STATES.length],
    isOnline: STATES[index % STATES.length] !== 'Offline',
    isEnabled: true,
    manufacturerName: 'A deliberately long printer manufacturer name',
    modelName: 'A deliberately long printer model designation',
    progress: 42,
    fileName: 'density-validation-benchy.gcode',
    printTimeLeftSeconds: STATES[index % STATES.length] === 'Printing' ? 7200 : undefined,
    hotendTemp: 210,
    hotendTarget: 210,
    bedTemp: 60,
    bedTarget: 60,
  } as Printer));

  function Fixture() {
    const [mode, setMode] = useState<'compact' | 'detailed'>('detailed');
    const [sidebar, setSidebar] = useState(true);
    const [virtualized, setVirtualized] = useState(false);
    return (
      <div className="flex h-screen">
        {sidebar && <aside aria-label="Fixture sidebar" className="w-64 shrink-0 max-sm:hidden bg-pf-sidebar" />}
        <main data-main-content className="min-w-0 flex-1 overflow-auto p-4">
          <div className="mb-4 flex flex-wrap gap-2">
            <Button onClick={() => setMode('detailed')}>Detailed</Button>
            <Button onClick={() => setMode('compact')}>Compact</Button>
            <Button onClick={() => setSidebar(value => !value)}>Toggle sidebar</Button>
            <Button onClick={() => setVirtualized(value => !value)}>Toggle virtualization</Button>
          </div>
          <PrinterCardGrid
            printers={virtualized ? printers : printers.slice(0, 6)}
            mode={mode}
            renderPrinter={printer => mode === 'detailed'
              ? <DetailedPrinterCard printer={printer} />
              : <CompactPrinterCard printer={printer} onExpand={() => {}} />}
          />
        </main>
      </div>
    );
  }

  createRoot(element).render(
    <MemoryRouter>
      <QueryClientProvider client={queryClient}>
        <AuthContext value={{
          user: null, isAuthenticated: true, isLoading: false,
          hasPermission: () => true, hasRole: () => false, error: null,
          login: async () => false, loginWithPasskey: async () => false,
          register: async () => false, logout: async () => {},
        }}>
          <Fixture />
        </AuthContext>
      </QueryClientProvider>
    </MemoryRouter>,
  );
}
