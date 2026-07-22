import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import "@testing-library/jest-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import QueueTimelineTab, { buildTicks } from "../QueueTimelineTab";
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

const TEST_DATE_FROM = new Date("2026-06-24T00:00:00.000Z");
const TEST_DATE_TO = new Date("2026-06-25T00:00:00.000Z");

interface RenderTimelineOptions {
  dateFrom?: Date;
  dateTo?: Date;
}

function renderTimeline({
  dateFrom = TEST_DATE_FROM,
  dateTo = TEST_DATE_TO,
}: RenderTimelineOptions = {}) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <QueueTimelineTab
        stats={stats}
        dateFrom={dateFrom}
        dateTo={dateTo}
      />
    </QueryClientProvider>
  );
}

describe("buildTicks", () => {
  it("emits week-mode major ticks at local midnight across a DST transition", () => {
    const originalTz = process.env.TZ;
    process.env.TZ = "America/Los_Angeles";

    try {
      const start = new Date(2026, 2, 8, 0, 0, 0, 0).getTime();
      const end = new Date(2026, 2, 15, 0, 0, 0, 0).getTime();
      const majorTicks = buildTicks(start, end, "week")
        .filter((tick) => tick.major)
        .map((tick) => new Date(tick.ms));

      expect(majorTicks).toHaveLength(8);
      expect(majorTicks.map((tick) => tick.getDate())).toEqual([8, 9, 10, 11, 12, 13, 14, 15]);
      expect(majorTicks.every((tick) => tick.getHours() === 0 && tick.getMinutes() === 0)).toBe(true);
    } finally {
      if (originalTz == null) {
        delete process.env.TZ;
      } else {
        process.env.TZ = originalTz;
      }
    }
  });
});

describe("QueueTimelineTab", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-06-24T12:00:00.000Z"));
    global.ResizeObserver = class ResizeObserver {
      observe() {}
      unobserve() {}
      disconnect() {}
    };
    getAnalyticsTimelineMock.mockReset();
    getQueueOverviewMock.mockReset();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("shows loading state while timeline is fetching", () => {
    getAnalyticsTimelineMock.mockReturnValue(new Promise(() => {}));
    getQueueOverviewMock.mockResolvedValue(queueOverview);

    renderTimeline();

    expect(screen.getByRole('status', { name: 'Loading timeline' })).toBeInTheDocument();
  });

  it("renders summary cards and timeline lanes", async () => {
    getAnalyticsTimelineMock.mockResolvedValue(timelineEvents);
    getQueueOverviewMock.mockResolvedValue(queueOverview);

    renderTimeline();

    expect(await screen.findByText("RocketVase.gcode")).toBeInTheDocument();
    expect(screen.getByText("Prints Queued")).toBeInTheDocument();
    expect(screen.getByText("Printing Now")).toBeInTheDocument();
    expect(screen.getByText("Printers Active")).toBeInTheDocument();
    expect(screen.getByText("Until All Done")).toBeInTheDocument();
    expect(screen.getByText("6")).toBeInTheDocument();
    expect(screen.getByText("3")).toBeInTheDocument();
    expect(screen.getByText("Core One A")).toBeInTheDocument();
    expect(screen.getByText("Core One B")).toBeInTheDocument();
    expect(screen.getByRole("img", { name: /CaseTop\.gcode/i })).toBeInTheDocument();
    expect(screen.getByRole("img", { name: /RocketVase\.gcode on Core One A/i })).toBeInTheDocument();

    await waitFor(() => {
      expect(getAnalyticsTimelineMock).toHaveBeenCalledTimes(1);
      expect(getQueueOverviewMock).toHaveBeenCalledTimes(1);
    });
  });

  it("renders recent events and the now marker in a multi-day external range", async () => {
    getAnalyticsTimelineMock.mockResolvedValue([
      {
        jobId: "job-recent",
        jobName: "RecentWidget.gcode",
        printerName: "Core One A",
        state: "Printing",
        enteredAtUtc: "2026-06-24T11:00:00.000Z",
        durationSeconds: 3600,
      },
    ] satisfies TimelineEventDto[]);
    getQueueOverviewMock.mockResolvedValue(queueOverview);

    renderTimeline({
      dateFrom: new Date("2026-06-17T12:00:00.000Z"),
      dateTo: new Date("2026-06-24T12:00:00.000Z"),
    });

    expect(await screen.findByRole("img", { name: /RecentWidget\.gcode on Core One A/i })).toBeInTheDocument();
    expect(screen.getByText("Now")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Week" })).toHaveAttribute("aria-pressed", "true");
  });

  it("renders empty state when no timeline events exist", async () => {
    getAnalyticsTimelineMock.mockResolvedValue([]);
    getQueueOverviewMock.mockResolvedValue([]);

    renderTimeline();

    expect(await screen.findByText("No activity in this window")).toBeInTheDocument();
    expect(screen.getByText("Navigate forward or back, or switch to Week zoom.")).toBeInTheDocument();
  });

  it("renders error state when timeline request fails", async () => {
    getAnalyticsTimelineMock.mockRejectedValue(new Error("timeline unavailable"));
    getQueueOverviewMock.mockResolvedValue(queueOverview);

    renderTimeline();

    expect(await screen.findByText(/Failed to load timeline/i)).toBeInTheDocument();
    expect(screen.getByText(/timeline unavailable/i)).toBeInTheDocument();
  });
});
