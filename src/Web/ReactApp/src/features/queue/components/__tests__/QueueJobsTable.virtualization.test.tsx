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
// Captures the real `getItemKey` option passed by QueueJobsTable so tests can
// assert it's actually wired (keyed by job.id, not the default index-based
// identity) without needing the mock to reimplement TanStack's internal
// measurement cache.
let capturedGetItemKey: ((index: number) => string | number) | undefined;

interface MockRange {
  startIndex: number;
  endIndex: number;
  overscan: number;
  count: number;
}

interface MockVirtualizerOptions {
  count: number;
  overscan: number;
  scrollMargin: number;
  rangeExtractor?: (range: MockRange) => number[];
  getItemKey?: (index: number) => string | number;
}

vi.mock("@tanstack/react-virtual", () => ({
  defaultRangeExtractor: (range: MockRange) => {
    const indexes = [];
    for (let index = range.startIndex; index <= range.endIndex; index++) indexes.push(index);
    return indexes;
  },
  useVirtualizer: (options: MockVirtualizerOptions) => {
    capturedGetItemKey = options.getItemKey;
    const clampedEnd = Math.min(windowEnd, options.count - 1);
    const range: MockRange = { startIndex: windowStart, endIndex: clampedEnd, overscan: options.overscan, count: options.count };
    const indexes = options.rangeExtractor
      ? options.rangeExtractor(range)
      : Array.from({ length: clampedEnd - windowStart + 1 }, (_, i) => windowStart + i);
    const items = indexes.map((index) => ({
      index,
      key: options.getItemKey ? options.getItemKey(index) : `row-${index}`,
      start: options.scrollMargin + index * ROW_HEIGHT,
      end: options.scrollMargin + (index + 1) * ROW_HEIGHT,
    }));
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
    capturedGetItemKey = undefined;
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

  it("keeps a focused row mounted even after it scrolls outside the windowed range, with a spacer for the gap it creates", () => {
    windowStart = 5;
    windowEnd = 8;
    const totalJobs = QUEUE_TABLE_VIRTUALIZATION_THRESHOLD + 30;
    const jobs = createJobs(totalJobs);

    const { container, rerender } = render(<QueueJobsTable jobs={jobs} />);

    const rowFive = container.querySelector('[data-job-id="job-5"]') as HTMLElement;
    expect(rowFive).toBeInTheDocument();
    fireEvent.focus(rowFive);

    // Simulate a mouse-wheel scroll (independent of Tab) that moves the
    // windowed range well past the focused row, making the rendered range
    // non-contiguous: [5, 20, 21, 22, 23].
    windowStart = 20;
    windowEnd = 23;
    rerender(
      <QueryClientProvider client={new QueryClient()}>
        <QueueJobsTable jobs={jobs} />
      </QueryClientProvider>,
    );

    // The focused row must still be in the DOM (force-included by the
    // rangeExtractor) so focus never silently falls back to <body>.
    expect(container.querySelector('[data-job-id="job-5"]')).toBeInTheDocument();
    expect(screen.getByText(jobs[5].gcodeFile!.fileName)).toBeInTheDocument();
    // The new windowed range (20-23) is also mounted alongside it.
    expect(screen.getByText(jobs[20].gcodeFile!.fileName)).toBeInTheDocument();
    // Rows 6-19 (14 rows) are skipped in between — they must not collapse
    // into nothing; a dedicated middle spacer must occupy that gap in
    // addition to the top (rows 0-4) and bottom (rows 24-49) spacers,
    // otherwise the table's total height/scroll math would be wrong.
    const hiddenBodies = Array.from(container.querySelectorAll("tbody[aria-hidden]"));
    expect(hiddenBodies).toHaveLength(3);
    const heights = hiddenBodies.map((tbody) => parseFloat((tbody.querySelector("td") as HTMLElement).style.height));
    expect(heights[0]).toBeCloseTo(5 * ROW_HEIGHT, 0); // rows 0-4, before the focused row
    expect(heights[1]).toBeCloseTo(14 * ROW_HEIGHT, 0); // rows 6-19, the gap the focused row creates
    expect(heights[2]).toBeCloseTo(26 * ROW_HEIGHT, 0); // rows 24-49, after the window
  });

  it("keys virtualized rows by job.id (not index) so reordering, inserting, or removing jobs can't misapply a stale row", () => {
    windowStart = 0;
    windowEnd = 2;
    const jobs = createJobs(QUEUE_TABLE_VIRTUALIZATION_THRESHOLD + 10);

    const { rerender } = render(<QueueJobsTable jobs={jobs} />);

    expect(capturedGetItemKey).toBeDefined();
    expect(capturedGetItemKey!(0)).toBe(jobs[0].job.id);
    expect(capturedGetItemKey!(2)).toBe(jobs[2].job.id);
    expect(screen.getByText(jobs[0].gcodeFile!.fileName)).toBeInTheDocument();

    // Insert a new job at the front (e.g. a poll/SignalR update surfacing a
    // newly queued job) while the windowed range (indices 0-2) is unchanged.
    const insertedJob: QueuedPrintJobWithFileMetaDto = {
      ...jobs[0],
      id: "job-inserted",
      job: { ...jobs[0].job, id: "job-inserted" },
      gcodeFile: { ...jobs[0].gcodeFile!, fileName: "inserted.gcode" },
    };
    const jobsAfterInsert = [insertedJob, ...jobs];
    rerender(
      <QueryClientProvider client={new QueryClient()}>
        <QueueJobsTable jobs={jobsAfterInsert} />
      </QueryClientProvider>,
    );

    // Index 0 now maps to the newly inserted job, not the original job[0] —
    // getItemKey (and the rendered content) must follow the new job at that
    // index rather than reusing stale identity/measurement for job[0].
    expect(capturedGetItemKey!(0)).toBe("job-inserted");
    expect(screen.getByText("inserted.gcode")).toBeInTheDocument();
    expect(screen.getByText(jobs[0].gcodeFile!.fileName)).toBeInTheDocument();

    // Reorder (swap indices 0 and 1) — e.g. a priority change re-sorting the
    // queue. The item key at each index must follow the job, not the slot.
    const reordered = [jobsAfterInsert[1], jobsAfterInsert[0], ...jobsAfterInsert.slice(2)];
    rerender(
      <QueryClientProvider client={new QueryClient()}>
        <QueueJobsTable jobs={reordered} />
      </QueryClientProvider>,
    );

    expect(capturedGetItemKey!(0)).toBe(reordered[0].job.id);
    expect(capturedGetItemKey!(1)).toBe(reordered[1].job.id);
    expect(screen.getByText(jobs[0].gcodeFile!.fileName)).toBeInTheDocument();

    // Remove a job from the front — indices shift, and the key at index 0
    // must reflect whatever job now actually occupies it.
    const afterRemoval = reordered.slice(1);
    rerender(
      <QueryClientProvider client={new QueryClient()}>
        <QueueJobsTable jobs={afterRemoval} />
      </QueryClientProvider>,
    );

    expect(capturedGetItemKey!(0)).toBe(afterRemoval[0].job.id);
    expect(screen.getByText(afterRemoval[0].gcodeFile!.fileName)).toBeInTheDocument();
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
