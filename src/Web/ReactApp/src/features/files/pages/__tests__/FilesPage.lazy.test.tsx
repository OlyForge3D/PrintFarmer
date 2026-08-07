import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';

const moduleLoads = vi.hoisted(() => ({
  printablesBrowser: vi.fn(),
  printablesImport: vi.fn(),
  harvest: vi.fn(),
  queue: vi.fn(),
  quickSlice: vi.fn(),
}));
const quickSliceModule = vi.hoisted(() => {
  let resolve: () => void = () => undefined;
  const ready = new Promise<void>((resolveReady) => {
    resolve = resolveReady;
  });
  return { ready, resolve };
});

vi.mock('@/features/models3d/components/PrintablesBrowserModal', () => {
  moduleLoads.printablesBrowser();
  return {
    PrintablesBrowserModal: ({ onImportUrl }: { onImportUrl: (url: string) => void }) => (
      <div role="dialog" aria-label="Printables browser mock">
        <button type="button" onClick={() => onImportUrl('https://www.printables.com/model/1')}>
          Choose Printables model
        </button>
      </div>
    ),
  };
});

vi.mock('@/features/models3d/components/PrintablesImportModal', async () => {
  moduleLoads.printablesImport();
  const { Modal } = await import('@/common/components/modals/Modal');
  return {
    PrintablesImportModal: ({ isOpen, onClose }: { isOpen: boolean; onClose: () => void }) => (
      <Modal isOpen={isOpen} onClose={onClose} title="Printables import mock">
        <button type="button" onClick={onClose}>Close import</button>
      </Modal>
    ),
  };
});

vi.mock('@/features/gcode/components/harvest/HarvestWizardModal', () => {
  moduleLoads.harvest();
  return {
    HarvestWizardModal: () => <div role="dialog" aria-label="Harvest wizard mock" />,
  };
});

vi.mock('@/features/gcode/components/QueueGcodeModal', () => {
  moduleLoads.queue();
  return {
    QueueGcodeModal: () => <div role="dialog" aria-label="Queue G-code mock" />,
  };
});

vi.mock('@/features/slicer/components/QuickSliceModal', async () => {
  moduleLoads.quickSlice();
  await quickSliceModule.ready;
  return {
    QuickSliceModal: () => <div role="dialog" aria-label="Quick slice mock" />,
  };
});

vi.mock('@/features/fileBrowser/components/FileBrowser', async () => {
  const ReactModule = await import('react');
  return {
    FileBrowser: ReactModule.forwardRef(function FileBrowserMock(
      {
        renderItemActions,
      }: {
        renderItemActions: (file: {
          id: string;
          fileName: string;
          meta: Record<string, unknown>;
        }) => React.ReactNode;
      },
      ref,
    ) {
      void ref;
      return (
        <div>
          {renderItemActions({
            id: 'gcode-1',
            fileName: 'part.gcode',
            meta: { gcode: { id: 'gcode-1', name: 'part.gcode', tags: [] } },
          })}
          {renderItemActions({
            id: 'model-1',
            fileName: 'part.stl',
            meta: {
              model3d: {
                id: 'model-1',
                name: 'part.stl',
                fileType: 'stl',
                tags: [],
              },
            },
          })}
        </div>
      );
    }),
  };
});

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({ data: [] }),
}));

vi.mock('@/common/hooks/useViewModePreference', () => ({
  useViewModePreference: () => ({ viewMode: 'grid', setViewMode: vi.fn() }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getHarvestOperations: vi.fn().mockResolvedValue([]),
    get3DModelsQuery: vi.fn().mockResolvedValue({ models: [], totalPages: 1 }),
    getGcodeFilesQuery: vi.fn().mockResolvedValue({ files: [], totalPages: 1 }),
  },
}));

import { FilesPage } from '../FilesPage';

describe('FilesPage lazy interactions', () => {
  it('loads each large modal only after direct user intent', async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/files']}>
          <FilesPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    expect(moduleLoads.printablesBrowser).not.toHaveBeenCalled();
    expect(moduleLoads.printablesImport).not.toHaveBeenCalled();
    expect(moduleLoads.harvest).not.toHaveBeenCalled();
    expect(moduleLoads.queue).not.toHaveBeenCalled();
    expect(moduleLoads.quickSlice).not.toHaveBeenCalled();

    fireEvent.click(screen.getByTitle('Quick slice'));
    expect(await screen.findByRole('status', { name: 'Loading quick slice' })).toBeVisible();
    await waitFor(() => expect(moduleLoads.quickSlice).toHaveBeenCalledTimes(1));
    quickSliceModule.resolve();
    expect(await screen.findByRole('dialog', { name: 'Quick slice mock' })).toBeInTheDocument();

    const printablesButton = screen.getByRole('button', { name: 'Printables' });
    fireEvent.focus(printablesButton);
    await waitFor(() => expect(moduleLoads.printablesBrowser).toHaveBeenCalledTimes(1));
    await user.click(printablesButton);
    await user.click(await screen.findByRole('button', { name: 'Choose Printables model' }));
    expect(await screen.findByRole('dialog', { name: 'Printables import mock' })).toBeInTheDocument();
    expect(moduleLoads.printablesImport).toHaveBeenCalledTimes(1);
    await user.click(screen.getByRole('button', { name: 'Close import' }));
    await new Promise<void>((resolve) => window.requestAnimationFrame(() => resolve()));
    expect(screen.getByRole('button', { name: 'Printables' })).toHaveFocus();

    await user.click(screen.getByRole('button', { name: 'Start Harvest' }));
    expect(await screen.findByRole('dialog', { name: 'Harvest wizard mock' })).toBeInTheDocument();
    expect(moduleLoads.harvest).toHaveBeenCalledTimes(1);

    await user.click(screen.getByTitle('Queue for printing'));
    expect(await screen.findByRole('dialog', { name: 'Queue G-code mock' })).toBeInTheDocument();
    expect(moduleLoads.queue).toHaveBeenCalledTimes(1);

  });
});
