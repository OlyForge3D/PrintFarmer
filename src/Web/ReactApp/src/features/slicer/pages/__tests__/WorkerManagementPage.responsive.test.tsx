import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router';
import { WorkerManagementPage } from '../WorkerManagementPage';
import { workerService, WorkerResponse } from '@/services/workerService';
import { slicerHubService } from '@/services/slicerHubService';

vi.mock('@/services/workerService', async () => {
  const actual = await vi.importActual<typeof import('@/services/workerService')>('@/services/workerService');
  return {
    ...actual,
    workerService: {
      getAllWorkers: vi.fn(),
      getWorkersByStatus: vi.fn(),
      getWorkerJobs: vi.fn(),
      disableWorker: vi.fn(),
      enableWorker: vi.fn(),
      resetWorker: vi.fn(),
      deleteWorker: vi.fn(),
      updateWorkerSlots: vi.fn(),
      isHeartbeatStale: vi.fn(() => false),
      calculateUtilization: vi.fn(() => 50),
      calculateSuccessRate: vi.fn(() => 100),
      getUptime: vi.fn(() => '1h'),
    },
  };
});

vi.mock('@/services/slicerHubService', () => ({
  slicerHubService: {
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    onSlicerRegistered: vi.fn(),
    onSlicerHeartbeat: vi.fn(),
    onSlicerDeregistered: vi.fn(),
  },
}));

vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({ title, children }: { title: string; children: React.ReactNode }) => (
    <div data-testid="page-template">
      <h1>{title}</h1>
      {children}
    </div>
  ),
}));

const mockWorker: WorkerResponse = {
  id: 'worker-1',
  serviceId: 'svc-1',
  name: 'Worker One',
  endpointUrl: 'http://worker-one.local',
  capabilities: ['orca'],
  status: 'Online',
  freeSlots: 2,
  totalSlots: 4,
  activeJobs: 2,
  completedJobs: 10,
  failedJobs: 1,
  averageProcessingTimeSeconds: 120,
  lastHeartbeat: '2026-05-26T17:20:00Z',
  registeredAt: '2026-05-01T00:00:00Z',
  apiKey: 'key',
  version: '1.0.0',
  createdAt: '2026-05-01T00:00:00Z',
  updatedAt: '2026-05-26T17:20:00Z',
  isDisabled: false,
};

function setViewportWidth(width: number) {
  Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: width });
  window.dispatchEvent(new Event('resize'));
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/admin/manage?tab=operations&sub=workers']}>
      <WorkerManagementPage />
    </MemoryRouter>,
  );
}

describe('WorkerManagementPage responsive workers table', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(workerService.getAllWorkers).mockResolvedValue([mockWorker]);
    vi.mocked(slicerHubService.start).mockResolvedValue(undefined);
  });

  it.each([320, 768, 1920])('keeps every column and the Actions buttons reachable at %ipx viewport', async (width) => {
    setViewportWidth(width);
    renderPage();

    await waitFor(() => {
      expect(screen.getByText('Worker One')).toBeInTheDocument();
    });

    // All column headers must be present in the DOM regardless of viewport width.
    expect(screen.getByRole('columnheader', { name: 'Worker' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Status' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Capacity' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Statistics' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Performance' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Actions' })).toBeInTheDocument();

    // Action buttons must be reachable in the accessibility tree (keyboard/pointer/touch) at every width.
    expect(screen.getByRole('button', { name: 'Disable' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Delete' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Edit Slots' })).toBeInTheDocument();
  });

  it('wraps the table in a horizontally scrollable container instead of clipping it', async () => {
    setViewportWidth(375);
    renderPage();

    await waitFor(() => {
      expect(screen.getByText('Worker One')).toBeInTheDocument();
    });

    const table = screen.getByRole('table');
    const scrollContainer = table.parentElement;
    expect(scrollContainer).toHaveClass('overflow-x-auto');
    // The container must not clip content — that was the original bug.
    expect(scrollContainer).not.toHaveClass('overflow-hidden');

    // The outer wrapper (border/background) must not itself clip either.
    const outerWrapper = scrollContainer?.parentElement;
    expect(outerWrapper).not.toHaveClass('overflow-hidden');
  });
});
