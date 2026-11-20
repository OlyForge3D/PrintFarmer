import React from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ProfileEditorModal } from '@/features/slicer/components/ProfileEditorModal';

const queryClient = new QueryClient();
const profile = {
  name: 'Test PLA',
  material: 'PLA',
  nozzleTemperature: 210,
  bedTemperature: 60,
  printSpeed: 60,
  settings: {
    cool_plate_temp_initial_layer: ['50'],
    cool_plate_temp: ['45'],
    textured_plate_temp_initial_layer: ['65'],
    textured_plate_temp: ['60'],
  },
};

export function mountFilamentEditor() {
  const root = document.getElementById('filament-editor-root');
  if (!root) throw new Error('Filament editor root is missing');
  createRoot(root).render(
    <QueryClientProvider client={queryClient}>
      <ProfileEditorModal isOpen onClose={() => {}} profileType="filament" originalProfile={profile} />
    </QueryClientProvider>,
  );
}
