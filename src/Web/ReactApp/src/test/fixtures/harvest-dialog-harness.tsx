import React from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HarvestJobDialog } from '@/features/harvest/components/HarvestJobDialog';

export function mountHarvestDialog(element: HTMLElement) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  createRoot(element).render(
    <QueryClientProvider client={queryClient}>
      <HarvestJobDialog
        isOpen
        onClose={() => {}}
        job={{ id: 'synthetic-job-1', name: 'Synthetic bracket x2' }}
      />
    </QueryClientProvider>,
  );
}
