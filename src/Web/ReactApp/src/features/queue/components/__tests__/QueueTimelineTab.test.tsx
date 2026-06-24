import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import "@testing-library/jest-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import QueueTimelineTab from "../QueueTimelineTab";
import type { QueueOverviewDto, QueueStatsDto, TimelineEventDto } from "@/types/api";

const getAnalyticsTimelineMock = vi.fn();
const getQueueOverviewMock = vi.fn();

vi.mock("@/services/api", () => ({
  apiClient: {
    getAnalyticsTimeline: (...args: unknown[]) => getAnalyticsTimelineMock(...args),
    getQueueOverview: (...args: unknown[]) => getQueueOverviewMock(...args),
  },
}));

const stats: QueueStatsDto = {
  totalQueued: 6,
  totalPrinting: 3,
  totalPaused: 1,
  averageWaitTimeMinutes: 12,
  estimatedQueueCompletionUtc: "2026-06-24T19:30:00.000Z",
  staffedCompletionUtc: "2026-06-25T16:15:00.000Z",
  assumptions: {
    workdayStartHourUtc: 8,
    workdayEndHourUtc: 17,
    bedClearMinutes: 10,
  },
  byModel: {},
};

const timelineEvents: TimelineEventDto[] = [
  {
    jobId: "job-1",
    jobName: "RocketVase.gcode",
    printerName: "Core One A",
    state: "Printing",
    enteredAtUtc: "2026-06-24T09:00:00.000Z",
    exitedAtUtc: "2026-06-24T10:10:00.000Z",
    durationSeconds: 4200,
  },
  {
    jobId: "job-2",
    jobName: "CaseTop.gcode",
    printerName: "Core One B",
    state: "Queued",
    enteredAtUtc: "2026-06-24T09:15:00.000Z",
    exitedAtUtc: "2026-06-24T09:55:00.000Z",
    durationSeconds: 2400,
  },
];

const queueOverview: QueueOverviewDto[] = [
  {
    printerId: "p1",
    printerName: "Core One A",
    printerModel: "Core One",
    isAvailable: false,
    queuedJobsCount: 2,
    currentJobId: "job-1",
    currentJobName: "RocketVase.gcode",
    estimatedCompletionTime: "2026-06-24T10:30:00.000Z",
  },
  {
    printerId: "p2",
    printerName: "Core One B",
    printerModel: "Core One",
    isAvailable: true,
    queuedJobsCount: 1,
    estimatedCompletionTime: "2026-06-24T11:00:00.000Z",
  },
];

function renderTimeline() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <QueueTimelineTab stats={stats} />
    </QueryClientProvider>
  );
}

describe("QueueTimelineTab", () => {
  beforeEach(() => {
    getAnalyticsTimelineMock.mockReset();
    getQueueOverviewMock.mockReset();
  });

  it("shows loading state while timeline is fetching", () => {
    getAnalyticsTimelineMock.mockReturnValue(new Promise(() => {}));
    getQueueOverviewMock.mockResolvedValue(queueOverview);

    renderTimeline();

    expect(screen.getByText("Loading timeline...")).toBeInTheDocument();
  });

  it("renders summary cards and timeline lanes", async () => {
    getAnalyticsTimelineMock.mockResolvedValue(timelineEvents);
    getQueueOverviewMock.mockResolvedValue(queueOverview);

    renderTimeline();

    expect(await screen.findByText("RocketVase.gcode")).toBeInTheDocument();
    expect(screen.getByText("Queued Jobs")).toBeInTheDocument();
    expect(screen.getByText("Currently Printing")).toBeInTheDocument();
    expect(screen.getByText("Active Printers")).toBeInTheDocument();
    expect(screen.getByText("Next Estimated Completion")).toBeInTheDocument();
    expect(screen.getByText("Queue Completion (Continuous)")).toBeInTheDocument();
    expect(screen.getByText("Queue Completion (Staffed Hours)")).toBeInTheDocument();
    expect(screen.getByText(/Adjusted for non-working hours/i)).toBeInTheDocument();
    expect(screen.getByText("6")).toBeInTheDocument();
    expect(screen.getByText("3")).toBeInTheDocument();
    expect(screen.getByText("Core One A")).toBeInTheDocument();
    expect(screen.getByText("Core One B")).toBeInTheDocument();
    expect(screen.getByText("CaseTop.gcode")).toBeInTheDocument();
    expect(screen.getByRole("img", { name: /RocketVase.gcode on Core One A/i })).toBeInTheDocument();

    await waitFor(() => {
      expect(getAnalyticsTimelineMock).toHaveBeenCalledTimes(1);
      expect(getQueueOverviewMock).toHaveBeenCalledTimes(1);
    });
  });

  it("renders empty state when no timeline events exist", async () => {
    getAnalyticsTimelineMock.mockResolvedValue([]);
    getQueueOverviewMock.mockResolvedValue(queueOverview);

    renderTimeline();

    expect(await screen.findByText("No timeline events")).toBeInTheDocument();
    expect(screen.getByText("Timeline data will appear after jobs move through queue states.")).toBeInTheDocument();
  });

  it("renders error state when timeline request fails", async () => {
    getAnalyticsTimelineMock.mockRejectedValue(new Error("timeline unavailable"));
    getQueueOverviewMock.mockResolvedValue(queueOverview);

    renderTimeline();

    expect(await screen.findByText(/Failed to load timeline/i)).toBeInTheDocument();
    expect(screen.getByText(/timeline unavailable/i)).toBeInTheDocument();
  });
});
