import { render, screen, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PrintSessionTimeline } from '@/features/printers/components/PrintSessionTimeline';
import type { FailureDetectionEvent, JobStateHistoryDto } from '@/types/api';

let timelineData: JobStateHistoryDto | undefined;
let timelineLoading = false;
let timelineError = false;

vi.mock('@/common/hooks/useApi', () => ({
  usePrintSessionTimeline: () => ({
    data: timelineData,
    isLoading: timelineLoading,
    isError: timelineError,
  }),
}));

describe('PrintSessionTimeline', () => {
  beforeEach(() => {
    timelineData = undefined;
    timelineLoading = false;
    timelineError = false;
  });

  it('renders a mixed session timeline in chronological order for the tracked job', () => {
    timelineData = {
      jobId: 'job-1',
      jobName: 'jobs/calibration-cube.gcode',
      transitions: [
        {
          fromState: 'Queued',
          toState: 'Printing',
          transitionedAtUtc: '2026-03-27T10:00:00Z',
          notes: 'Printer started the queued job.',
        },
        {
          fromState: 'Paused',
          toState: 'Completed',
          transitionedAtUtc: '2026-03-27T10:10:00Z',
          durationInStateSeconds: 180,
        },
      ],
    };

    const incidents: FailureDetectionEvent[] = [
      {
        id: 'incident-1',
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        confidence: 0.91,
        detectedAt: '2026-03-27T10:05:00Z',
        snapshotUrl: 'http://example.com/incident.jpg',
        autoPaused: true,
      },
    ];

    render(
      <PrintSessionTimeline
        jobId="job-1"
        jobLabel="Calibration Cube"
        incidents={incidents}
      />
    );

    expect(screen.getByText('Selected session')).toBeInTheDocument();
    expect(screen.getByText('Calibration Cube')).toBeInTheDocument();
    expect(screen.getByText('1 incident')).toBeInTheDocument();

    const timeline = screen.getByRole('list', { name: /print session timeline for calibration cube/i });
    const items = within(timeline).getAllByRole('listitem');

    expect(within(items[0]).getByText('Print started')).toBeInTheDocument();
    expect(within(items[1]).getByText('Failure incident detected')).toBeInTheDocument();
    expect(within(items[2]).getByText('Print auto-paused')).toBeInTheDocument();
    expect(within(items[3]).getByText('Print completed')).toBeInTheDocument();
    expect(screen.getByText('91% confidence')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /open incident snapshot/i })).toHaveAttribute(
      'href',
      'http://example.com/incident.jpg'
    );
  });

  it('shows a loading message while the session timeline is being fetched', () => {
    timelineLoading = true;

    render(
      <PrintSessionTimeline
        jobId="job-1"
        jobLabel="Calibration Cube"
        incidents={[]}
      />
    );

    expect(screen.getByText('Loading the selected print session timeline…')).toBeInTheDocument();
  });

  it('shows an error message when the tracked job history cannot be loaded', () => {
    timelineError = true;

    render(
      <PrintSessionTimeline
        jobId="job-1"
        jobLabel="Calibration Cube"
        incidents={[]}
      />
    );

    expect(
      screen.getByText('PrintFarmer could not load the tracked job history for this session right now.')
    ).toBeInTheDocument();
  });
});
