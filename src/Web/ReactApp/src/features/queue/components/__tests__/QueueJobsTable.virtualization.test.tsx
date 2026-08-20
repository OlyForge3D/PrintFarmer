import { render as rtlRender, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactElement } from "react";
import {
  QueueJobsTable,
  QUEUE_TABLE_VIRTUALIZATION_THRESHOLD,
} from "../QueueJobsTable";
import { QueuedPrintJobWithFileMetaDto } from "@/services/printQueueService";
import { PrintJobPriority } from "@/types/api";
import "@testing-library/jest-dom";

// ---------------------------------------------------------------------------
// Mock @tanstack/react-virtual the same way PrinterCardGrid.test.tsx does:
// a deterministic fake that reports a fixed windowed range so we can assert
// on exactly which rows are (and are not) mounted, and on the spacer sizing
// math, without depending on real scroll/layout measurement in jsdom.
// ---------------------------------------------------------------------------
const ROW_HEIGHT = 84;
let windowStart = 0;
let windowEnd = 3;

interface MockVirtualizerOptions {
  count: number;
  overscan: number;
  scrollMargin: number;
}

vi.mock("@tanstack/react-virtual", () => ({
  useVirtualizer: (options: MockVirtualizerOptions) => {
    const clampedEnd = Math.min(windowEnd, options.count - 1);
    const items = [];
    for (let index = windowStart; index <= clampedEnd; index++) {
      items.push({
        index,
        key: `row-${index}`,
        start: options.scrollMargin + index * ROW_HEIGHT,
        end: options.scrollMargin + (index + 1) * ROW_HEIGHT,
      });
    }
    return {
      getVirtualItems: () => items,
      getTotalSize: () => options.count * ROW_HEIGHT,
      measureElement: vi.fn(),
    };
  },
}));

const mockGetCoverage = vi.hoisted(() =>
  vi.fn(() => ({
    data: { printers: [], evaluatedAtUtc: new Date().toISOString() },
    isSuccess: true,
    isLoading: false,
    isError: false,
  })),
);

vi.mock("@/services/printer-signalr", () => ({
  printerSignalRService: {
    connect: vi.fn().mockResolvedValue(undefined),
    onFilamentCoverageChanged: vi.fn(() => () => {}),
  },
}));

vi.mock("@/services/api", () => ({
  apiClient: {
    get: vi.fn().mockResolvedValue({
      data: { printers: [], evaluatedAtUtc: new Date().toISOString() },
    }),
  },
}));

vi.mock("@/features/filament-coverage/hooks", () => ({
  useFleetFilamentCoverage: () => mockGetCoverage(),
  usePrinterFilamentCoverage: vi.fn(() => ({ data: null, isLoading: false, isError: false })),
  __resetFilamentCoverageSubscriptionForTests: vi.fn(),
}));

function render(ui: ReactElement) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, refetchInterval: false, refetchOnWindowFocus: false, gcTime: 0 },
    },
  });
  return rtlRender(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

function createJobs(count: number): QueuedPrintJobWithFileMetaDto[] {
  return Array.from({ length: count }, (_, index) => {
    const id = `job-${index}`;
    return {
      id,
      job: {
        id,
        name: `print-${index}`,
        gcodeFileId: `file-${index}`,
        status: "Queued" as const,
        priority: PrintJobPriority.Normal,
        queuePosition: index,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
        queuedAtUtc: new Date().toISOString(),
        wasSeededFromHistory: false,
      },
      gcodeFile: {
        id: `file-${index}`,
        fileName: `print-${index}.gcode`,
        fileSizeBytes: 1024,
        materialType: "PLA",
        createdAtUtc: new Date().toISOString(),
      },
      assignedPrinter: {
        id: `printer-${index}`,
        name: `Printer ${index}`,
        modelName: "Prusa CORE One",
        status: "online" as const,
        isOnline: true,
      },
    };
  });
}

describe("QueueJobsTable virtualization", () => {
  beforeEach(() => {
    windowStart = 0;
    windowEnd = 3;
  });

  it("renders every job directly (no virtualizer wiring) at/under the threshold", () => {
    const jobs = createJobs(QUEUE_TABLE_VIRTUALIZATION_THRESHOLD);

    const { container } = render(<QueueJobsTable jobs={jobs} />);

    // All jobs mounted — one <tbody> per job.
    expect(container.querySelectorAll("tbody").length).toBe(jobs.length);
    jobs.forEach((job) => {
      expect(screen.getByText(job.gcodeFile!.fileName)).toBeInTheDocument();
    });

    // Non-virtualized path doesn't need explicit aria-rowcount — every row is
    // already in the DOM, so native table row counting is accurate.
    expect(container.querySelector("table")).not.toHaveAttribute("aria-rowcount");
  });

  it("windows rows above the threshold: only the virtualized range is mounted, with spacer rows", () => {
    const totalJobs = QUEUE_TABLE_VIRTUALIZATION_THRESHOLD + 30;
    windowStart = 5;
    windowEnd = 8;
    const jobs = createJobs(totalJobs);

    const { container } = render(<QueueJobsTable jobs={jobs} />);

    // Only the mocked window (indices 5-8 => 4 jobs) has its row group mounted.
    const bodies = Array.from(container.querySelectorAll("tbody"));
    const visibleBodies = bodies.filter((tbody) => !tbody.hasAttribute("aria-hidden"));
    expect(visibleBodies).toHaveLength(4);
    expect(screen.getByText(jobs[5].gcodeFile!.fileName)).toBeInTheDocument();
    expect(screen.getByText(jobs[8].gcodeFile!.fileName)).toBeInTheDocument();
    expect(screen.queryByText(jobs[0].gcodeFile!.fileName)).not.toBeInTheDocument();
    expect(screen.queryByText(jobs[totalJobs - 1].gcodeFile!.fileName)).not.toBeInTheDocument();

    // Top + bottom spacer <tbody> blocks represent the scrolled-out rows.
    const hiddenBodies = bodies.filter((tbody) => tbody.hasAttribute("aria-hidden"));
    expect(hiddenBodies).toHaveLength(2);
    const [topSpacer, bottomSpacer] = hiddenBodies;
    const topHeight = parseFloat((topSpacer.querySelector("td") as HTMLElement).style.height);
    const bottomHeight = parseFloat((bottomSpacer.querySelector("td") as HTMLElement).style.height);
    expect(topHeight).toBeCloseTo(5 * ROW_HEIGHT, 0);
    expect(bottomHeight).toBeCloseTo((totalJobs - 9) * ROW_HEIGHT, 0);

    // Screen-reader row count/position must still reflect the true total,
    // even though most rows are unmounted.
    const table = container.querySelector("table") as HTMLElement;
    expect(table).toHaveAttribute("aria-rowcount", String(1 + totalJobs * 2));
    const headerRow = container.querySelector("thead tr") as HTMLElement;
    expect(headerRow).toHaveAttribute("aria-rowindex", "1");
    const firstVisiblePrimaryRow = visibleBodies[0].querySelectorAll("tr")[0];
    expect(firstVisiblePrimaryRow).toHaveAttribute("aria-rowindex", String(5 * 2 + 2));
  });

  it("row click on a windowed-in row still opens JobDetailsModal via onEdit", () => {
    windowStart = 5;
    windowEnd = 8;
    const onEdit = vi.fn();
    const jobs = createJobs(QUEUE_TABLE_VIRTUALIZATION_THRESHOLD + 30);

    const { container } = render(<QueueJobsTable jobs={jobs} onEdit={onEdit} />);

    const visibleBody = Array.from(container.querySelectorAll("tbody")).find(
      (tbody) => !tbody.hasAttribute("aria-hidden"),
    ) as Element;
    fireEvent.click(visibleBody);

    expect(onEdit).toHaveBeenCalledWith("job-5");
  });

  it("keyboard activation (Enter) on a windowed-in row still opens JobDetailsModal via onEdit", () => {
    windowStart = 5;
    windowEnd = 8;
    const onEdit = vi.fn();
    const jobs = createJobs(QUEUE_TABLE_VIRTUALIZATION_THRESHOLD + 30);

    const { container } = render(<QueueJobsTable jobs={jobs} onEdit={onEdit} />);

    const visibleBody = Array.from(container.querySelectorAll("tbody")).find(
      (tbody) => !tbody.hasAttribute("aria-hidden"),
    ) as Element;
    fireEvent.keyDown(visibleBody, { key: "Enter" });

    expect(onEdit).toHaveBeenCalledWith("job-5");
  });

  it("re-renders correctly when the filtered jobs list identity changes", () => {
    windowStart = 0;
    windowEnd = 3;
    const jobsA = createJobs(QUEUE_TABLE_VIRTUALIZATION_THRESHOLD + 30);
    const jobsB = createJobs(QUEUE_TABLE_VIRTUALIZATION_THRESHOLD + 5).map((job, index) => ({
      ...job,
      id: `filtered-${index}`,
      job: { ...job.job, id: `filtered-${index}` },
      gcodeFile: { ...job.gcodeFile!, fileName: `filtered-${index}.gcode` },
    }));

    const { rerender, container } = rtlRender(
      <QueryClientProvider client={new QueryClient()}>
        <QueueJobsTable jobs={jobsA} />
      </QueryClientProvider>,
    );
    expect(screen.getByText(jobsA[0].gcodeFile!.fileName)).toBeInTheDocument();

    rerender(
      <QueryClientProvider client={new QueryClient()}>
        <QueueJobsTable jobs={jobsB} />
      </QueryClientProvider>,
    );

    expect(screen.queryByText(jobsA[0].gcodeFile!.fileName)).not.toBeInTheDocument();
    const table = container.querySelector("table") as HTMLElement;
    expect(table).toHaveAttribute("aria-rowcount", String(1 + jobsB.length * 2));
  });
});
