import '@testing-library/jest-dom';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

// Mock the API hooks
const mockScheduleJob = vi.fn();
vi.mock('@/common/hooks/useApi', () => ({
  useScheduleJob: () => ({
    mutate: mockScheduleJob,
    isPending: false,
  }),
  useTimezones: () => ({
    data: [{ id: 'America/New_York', displayName: 'Eastern Time', offset: '-05:00' }],
  }),
  queryKeys: {
    jobQueue: (printerId?: string) => ['job-queue', printerId] as const,
  },
}));

// Mock apiClient
vi.mock('@/services/api', () => ({
  apiClient: {
    getJobQueue: vi.fn(),
  },
}));

// Mock the Modal component
vi.mock('@/common/components/modals/Modal', () => ({
  Modal: ({ isOpen, title, children, footer }: {
    isOpen: boolean;
    title: string;
    children: React.ReactNode;
    footer: React.ReactNode;
  }) =>
    isOpen ? (
      <div data-testid="modal">
        <h2>{title}</h2>
        {children}
        <div data-testid="modal-footer">{footer}</div>
      </div>
    ) : null,
}));

import { ScheduleModal } from '@/features/scheduling/components/ScheduleModal';
import { apiClient } from '@/services/api';
import type { QueuedPrintJobWithFileMetaDto } from '@/types/api';

const mockJobs: QueuedPrintJobWithFileMetaDto[] = [
  {
    job: {
      id: 'job-1',
      name: 'Benchy',
      gcodeFileId: 'file-1',
      status: 'Queued',
      priority: 0,
      queuePosition: 1,
      createdAtUtc: '2025-01-01T00:00:00Z',
      updatedAtUtc: '2025-01-01T00:00:00Z',
      queuedAtUtc: '2025-01-01T00:00:00Z',
      copies: 1,
      completedCopies: 0,
      remainingCopies: 1,
    },
    gcodeFile: { id: 'file-1', name: 'benchy.gcode', fileName: 'benchy.gcode', fileSizeBytes: 1024, createdAtUtc: '2025-01-01T00:00:00Z' },
    assignedPrinter: { id: 'printer-1', name: 'Prusa MK4', modelName: 'MK4', status: 'Idle', isOnline: true },
  },
  {
    job: {
      id: 'job-2',
      name: 'Calibration Cube',
      gcodeFileId: 'file-2',
      status: 'Assigned',
      priority: 0,
      queuePosition: 2,
      createdAtUtc: '2025-01-01T00:00:00Z',
      updatedAtUtc: '2025-01-01T00:00:00Z',
      queuedAtUtc: '2025-01-01T00:00:00Z',
      copies: 1,
      completedCopies: 0,
      remainingCopies: 1,
    },
    gcodeFile: { id: 'file-2', name: 'cube.gcode', fileName: 'cube.gcode', fileSizeBytes: 512, createdAtUtc: '2025-01-01T00:00:00Z' },
  },
  {
    job: {
      id: 'job-3',
      name: 'Already Printing',
      gcodeFileId: 'file-3',
      status: 'Printing',
      priority: 0,
      queuePosition: 3,
      createdAtUtc: '2025-01-01T00:00:00Z',
      updatedAtUtc: '2025-01-01T00:00:00Z',
      queuedAtUtc: '2025-01-01T00:00:00Z',
      copies: 1,
      completedCopies: 0,
      remainingCopies: 1,
    },
    gcodeFile: { id: 'file-3', name: 'part.gcode', fileName: 'part.gcode', fileSizeBytes: 2048, createdAtUtc: '2025-01-01T00:00:00Z' },
  },
];

function renderWithQueryClient(ui: React.ReactElement) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>
  );
}

describe('ScheduleModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiClient.getJobQueue).mockResolvedValue(mockJobs);
  });

  it('renders a job picker Select instead of a text input', async () => {
    renderWithQueryClient(
      <ScheduleModal isOpen onClose={vi.fn()} />
    );

    expect(await screen.findByText('Benchy — Prusa MK4')).toBeInTheDocument();
    expect(screen.getByText('Calibration Cube — Unassigned')).toBeInTheDocument();
    // Printing jobs should be filtered out
    expect(screen.queryByText(/Already Printing/)).not.toBeInTheDocument();
    // The old text input placeholder should not exist
    expect(screen.queryByPlaceholderText('Enter job ID to schedule')).not.toBeInTheDocument();
  });

  it('shows "Select a job" placeholder option', async () => {
    renderWithQueryClient(
      <ScheduleModal isOpen onClose={vi.fn()} />
    );

    expect(await screen.findByText('Select a job…')).toBeInTheDocument();
  });

  it('pre-selects a job when jobId prop is provided', async () => {
    renderWithQueryClient(
      <ScheduleModal isOpen onClose={vi.fn()} jobId="job-1" />
    );

    await screen.findByText('Benchy — Prusa MK4');
    const select = document.getElementById('jobId') as HTMLSelectElement;
    expect(select).toBeTruthy();
    expect(select.value).toBe('job-1');
  });

  it('shows empty state when no schedulable jobs exist', async () => {
    vi.mocked(apiClient.getJobQueue).mockResolvedValue([
      {
        job: {
          id: 'job-x',
          name: 'Done Job',
          gcodeFileId: 'file-x',
          status: 'Completed',
          priority: 0,
          queuePosition: 0,
          createdAtUtc: '2025-01-01T00:00:00Z',
          updatedAtUtc: '2025-01-01T00:00:00Z',
          queuedAtUtc: '2025-01-01T00:00:00Z',
          copies: 1,
          completedCopies: 1,
          remainingCopies: 0,
        },
      },
    ]);

    renderWithQueryClient(
      <ScheduleModal isOpen onClose={vi.fn()} />
    );

    expect(await screen.findByText('No pending jobs available to schedule.')).toBeInTheDocument();
  });

  it('does not render modal when isOpen is false', () => {
    renderWithQueryClient(
      <ScheduleModal isOpen={false} onClose={vi.fn()} />
    );

    expect(screen.queryByTestId('modal')).not.toBeInTheDocument();
  });
});
