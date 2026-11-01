import { useState } from 'react';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PartsTab } from '@/features/parts-inventory/components/PartsTab';
import { BinsTab } from '@/features/parts-inventory/components/BinsTab';
import { partsInventoryKeys } from '@/features/parts-inventory/hooks/usePartsInventory';
import { SpoolsTab } from '@/features/filamentManagement/components/SpoolsTab';
import { apiClient } from '@/services/api';

// Isolated browser specimen only: reads stay in memory; no API/printer I/O.
export function InventoryToolbarFixture({ surface = 'parts' }: { surface?: 'parts' | 'bins' | 'spools' }) {
  const [client] = useState(() => {
    const client = new QueryClient({
      defaultOptions: { queries: { enabled: false, retry: false, staleTime: Infinity } },
    });
    for (const includeInactive of [false, true]) {
      client.setQueryData(partsInventoryKeys.partsList(includeInactive), []);
      client.setQueryData(partsInventoryKeys.binList(includeInactive), []);
    }
    apiClient.getSpoolFilterOptions = async () => ({ materials: [], vendors: [], locations: [] });
    apiClient.getSpoolmanHealth = async () => (
      { configured: true, success: true } as Awaited<ReturnType<typeof apiClient.getSpoolmanHealth>>
    );
    apiClient.getSpoolmanConfig = async () => (
      { baseUrl: 'https://spoolman.invalid' } as Awaited<ReturnType<typeof apiClient.getSpoolmanConfig>>
    );
    apiClient.getSpools = async () => ({
      items: [{
        id: 1, name: 'Fixture spool', filamentName: 'Fixture PLA', material: 'PLA',
        remainingWeightG: 500, initialWeightG: 1000, usedWeightG: 500,
      }],
      totalCount: 1,
    } as Awaited<ReturnType<typeof apiClient.getSpools>>);
    return client;
  });
  return (
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <main className="p-4 bg-pf-bg-2 text-pf-text-primary">
          {surface === 'parts' && <section aria-label="SKU fixture"><PartsTab /></section>}
          {surface === 'bins' && <section aria-label="Bin fixture"><BinsTab /></section>}
          {surface === 'spools' && <section aria-label="Spool fixture"><SpoolsTab /></section>}
        </main>
      </MemoryRouter>
    </QueryClientProvider>
  );
}
