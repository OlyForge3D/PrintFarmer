import { render as rtlRender, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactElement } from "react";
import { QueueJobsTable } from "../QueueJobsTable";
import { QueuedPrintJobWithFileMetaDto } from "@/services/printQueueService";
import { PrintJobPriority } from "@/types/api";
import "@testing-library/jest-dom";

// Hoisted so the vi.mock factory below can safely reference it before imports resolve.
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


describe("QueueJobsTable Component", () => {
  const createMockJob = (overrides?: Partial<QueuedPrintJobWithFileMetaDto>): QueuedPrintJobWithFileMetaDto => ({
    id: "job-1",
    job: {
      id: "job-1",
      name: "test-print",
      gcodeFileId: "file-1",
      status: "Queued",
      priority: PrintJobPriority.Low,
      queuePosition: 1,
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      queuedAtUtc: new Date().toISOString(),
      wasSeededFromHistory: false,
    },
    gcodeFile: {
      id: "file-1",
      fileName: "test-print.gcode",
      fileSizeBytes: 1024,
      materialType: "PLA",
      createdAtUtc: new Date().toISOString(),
    },
    assignedPrinter: {
      id: "printer-1",
      name: "Printer 1",
      modelName: "Prusa CORE One",
      status: "online",
      isOnline: true,
    },
    ...overrides,
  });

  const mockJobs: QueuedPrintJobWithFileMetaDto[] = [
    createMockJob({
      id: "job-1",
      job: {
        id: "job-1",
        name: "test-print",
        gcodeFileId: "file-1",
        status: "Queued",
        priority: PrintJobPriority.Low,
        queuePosition: 1,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
        queuedAtUtc: new Date().toISOString(),
        deadlineAtUtc: new Date(Date.now() + 48 * 60 * 60 * 1000).toISOString(),
        wasSeededFromHistory: false,
      },
      gcodeFile: {
        id: "file-1",
        fileName: "test-print.gcode",
        fileSizeBytes: 1024,
        materialType: "PLA",
        createdAtUtc: new Date().toISOString(),
      },
    }),
    createMockJob({
      id: "job-2",
      job: {
        id: "job-2",
        name: "another-print",
        gcodeFileId: "file-2",
        status: "Printing",
        priority: PrintJobPriority.Normal,
        queuePosition: 0,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
        queuedAtUtc: new Date().toISOString(),
        deadlineAtUtc: new Date(Date.now() - 60 * 60 * 1000).toISOString(),
        wasSeededFromHistory: true,
      },
      gcodeFile: {
        id: "file-2",
        fileName: "another-print.gcode",
        fileSizeBytes: 2048,
        materialType: "PETG",
        createdAtUtc: new Date().toISOString(),
      },
    }),
  ];

  it("should render empty state when no jobs provided", () => {
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel: vi.fn(),
      onPriority: vi.fn(),
    };

    render(<QueueJobsTable jobs={[]} {...mockHandlers} />);

    expect(screen.getByText("No Print Jobs Queued")).toBeInTheDocument();
  });

  it.each(Object.values(PrintJobPriority))(
    "preserves the %s priority label and emits its canonical enum name",
    (priority) => {
      const onPriority = vi.fn();
      const baseJob = createMockJob();
      const job = createMockJob({
        job: {
          ...baseJob.job,
          priority,
        },
      });

      render(<QueueJobsTable jobs={[job]} onPriority={onPriority} />);

      const control = screen.getByRole("combobox", { name: "Job priority" });
      expect(control).toHaveValue(priority);
      fireEvent.change(control, { target: { value: priority } });
      expect(onPriority).toHaveBeenCalledWith(job.job.id, priority);
    },
  );

  it("should render loading state", () => {
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel: vi.fn(),
      onPriority: vi.fn(),
    };

    render(<QueueJobsTable jobs={[]} isLoading={true} {...mockHandlers} />);

    expect(screen.getByText("Loading jobs...")).toBeInTheDocument();
  });

  it("should render all jobs in table", () => {
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel: vi.fn(),
      onPriority: vi.fn(),
    };

    render(<QueueJobsTable jobs={mockJobs} {...mockHandlers} />);

    expect(screen.getByText("test-print.gcode")).toBeInTheDocument();
    expect(screen.getByText("another-print.gcode")).toBeInTheDocument();
    expect(screen.getByLabelText("Imported")).toBeInTheDocument();
    expect(screen.getByText("Deadline")).toBeInTheDocument();
  });

  it("should render deadline urgency states", () => {
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel: vi.fn(),
      onPriority: vi.fn(),
    };

    const dueSoonJob = createMockJob({
      id: "job-due-soon",
      job: {
        id: "job-due-soon",
        name: "due-soon-print",
        gcodeFileId: "file-3",
        status: "Queued",
        priority: PrintJobPriority.Low,
        queuePosition: 2,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
        queuedAtUtc: new Date().toISOString(),
        deadlineAtUtc: new Date(Date.now() + 6 * 60 * 60 * 1000).toISOString(),
        wasSeededFromHistory: false,
      },
    });

    render(<QueueJobsTable jobs={[mockJobs[1], dueSoonJob]} {...mockHandlers} />);

    expect(screen.getByText("Overdue")).toBeInTheDocument();
    expect(screen.getByText("Due soon")).toBeInTheDocument();
  });

  it("should show pause button for printing jobs", () => {
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel: vi.fn(),
      onPriority: vi.fn(),
    };

    render(<QueueJobsTable jobs={mockJobs} {...mockHandlers} />);

    const pauseButton = screen.getByText("Pause");
    expect(pauseButton).toBeInTheDocument();
  });

  it("should call onCancel when cancel button is clicked", () => {
    const onCancel = vi.fn();
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel,
      onPriority: vi.fn(),
    };

    render(<QueueJobsTable jobs={mockJobs} {...mockHandlers} />);

    const cancelButtons = screen.getAllByText("Cancel");
    fireEvent.click(cancelButtons[0]);

    expect(onCancel).toHaveBeenCalledWith("job-1");
  });

  it("qualifies row action accessible names with file/printer/status context so they're row-unique (#2302)", () => {
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel: vi.fn(),
      onAbortPrint: vi.fn(),
      onPriority: vi.fn(),
    };

    // mockJobs[0] is Queued "test-print.gcode" on "Printer 1"; mockJobs[1] is
    // Printing "another-print.gcode" on the same printer — same printer, so
    // the fix must disambiguate on file name/status, not just printer name.
    render(<QueueJobsTable jobs={mockJobs} {...mockHandlers} />);

    const cancelButtons = screen.getAllByRole("button", { name: /^Cancel /i });
    expect(cancelButtons).toHaveLength(2);
    const cancelNames = cancelButtons.map((button) => button.getAttribute("aria-label"));
    expect(new Set(cancelNames).size).toBe(cancelNames.length);
    expect(cancelNames[0]).toBe("Cancel test-print.gcode on Printer 1 Queued");
    expect(cancelNames[1]).toBe("Cancel another-print.gcode on Printer 1 Printing");

    // Visible label must stay concise — only the accessible name is qualified.
    expect(cancelButtons[0]).toHaveTextContent("Cancel");
    expect(cancelButtons[0]).not.toHaveTextContent("test-print.gcode");

    const abortButton = screen.getByRole("button", { name: "Abort another-print.gcode on Printer 1 Printing" });
    expect(abortButton).toHaveTextContent("Abort");
  });

  it("does not expose manual reorder controls", () => {
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel: vi.fn(),
      onPriority: vi.fn(),
    };

    const { container } = render(<QueueJobsTable jobs={mockJobs} {...mockHandlers} />);

    expect(screen.queryByLabelText("Drag to reorder")).not.toBeInTheDocument();
    expect(container.querySelector("tbody[draggable]")).not.toBeInTheDocument();
  });

  it("should fall back to the live printer-side thumbnail when the job has no gcode thumbnail", () => {
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel: vi.fn(),
      onPriority: vi.fn(),
    };

    // Externally-started print: gcodeFile has no thumbnailUrl, but the printer
    // reports one live over SignalR.
    const externalJob = createMockJob({
      id: "ext-1",
      job: {
        id: "ext-1",
        name: "external-print",
        gcodeFileId: "",
        status: "Printing",
        priority: PrintJobPriority.Low,
        queuePosition: 1,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
        queuedAtUtc: new Date().toISOString(),
        wasSeededFromHistory: true,
      },
      gcodeFile: {
        id: "file-ext",
        fileName: "external-print.gcode",
        fileSizeBytes: 0,
        createdAtUtc: new Date().toISOString(),
      },
      assignedPrinter: {
        id: "printer-live",
        name: "U1",
        modelName: "Snapmaker U1",
        status: "online",
        isOnline: true,
      },
    });

    const { container } = render(
      <QueueJobsTable
        jobs={[externalJob]}
        printThumbnailByPrinterId={{ "printer-live": "http://printer/thumb.png" }}
        {...mockHandlers}
      />,
    );

    const img = container.querySelector('img[src="http://printer/thumb.png"]');
    expect(img).toBeInTheDocument();
  });

  it("should prefer the gcode thumbnail over the live printer thumbnail", () => {
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel: vi.fn(),
      onPriority: vi.fn(),
    };

    const job = createMockJob({
      id: "job-thumb",
      gcodeFile: {
        id: "file-thumb",
        fileName: "has-thumb.gcode",
        fileSizeBytes: 1024,
        thumbnailUrl: "http://server/gcode-thumb.png",
        createdAtUtc: new Date().toISOString(),
      },
      assignedPrinter: {
        id: "printer-live",
        name: "Printer 1",
        modelName: "Prusa CORE One",
        status: "online",
        isOnline: true,
      },
    });

    const { container } = render(
      <QueueJobsTable
        jobs={[job]}
        printThumbnailByPrinterId={{ "printer-live": "http://printer/live-thumb.png" }}
        {...mockHandlers}
      />,
    );

    expect(container.querySelector('img[src="http://server/gcode-thumb.png"]')).toBeInTheDocument();
    expect(container.querySelector('img[src="http://printer/live-thumb.png"]')).not.toBeInTheDocument();
  });

  it("should NOT show the live printer thumbnail on a Queued job pre-assigned to a busy printer", () => {
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel: vi.fn(),
      onPriority: vi.fn(),
    };

    // A queued job can be pre-assigned to a printer that is currently printing a
    // DIFFERENT job. The live thumbnail belongs to the printing job, not this
    // queued one — it must not leak onto the queued row.
    const queuedJob = createMockJob({
      id: "queued-1",
      job: {
        id: "queued-1",
        name: "waiting-print",
        gcodeFileId: "",
        status: "Queued",
        priority: PrintJobPriority.Low,
        queuePosition: 1,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
        queuedAtUtc: new Date().toISOString(),
      },
      gcodeFile: {
        id: "file-queued",
        fileName: "waiting-print.gcode",
        fileSizeBytes: 0,
        createdAtUtc: new Date().toISOString(),
      },
      assignedPrinter: {
        id: "printer-busy",
        name: "Printer Busy",
        modelName: "Prusa CORE One",
        status: "online",
        isOnline: true,
      },
    });

    const { container } = render(
      <QueueJobsTable
        jobs={[queuedJob]}
        printThumbnailByPrinterId={{ "printer-busy": "http://printer/other-job.png" }}
        {...mockHandlers}
      />,
    );

    expect(container.querySelector('img[src="http://printer/other-job.png"]')).not.toBeInTheDocument();
  });

  it("should render the placeholder, not a broken <img>, when the gcode file has no thumbnail metadata (#1911)", () => {
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel: vi.fn(),
      onPriority: vi.fn(),
    };

    // A queued/idle job whose gcode file has no embedded thumbnail: the backend
    // must omit gcodeFile.thumbnailUrl entirely (not point at a 404), and the
    // row must fall back to the placeholder rather than an <img> tag.
    const jobWithoutThumbnail = createMockJob({
      id: "no-thumb-1",
      job: {
        id: "no-thumb-1",
        name: "no-thumbnail-print",
        gcodeFileId: "file-no-thumb",
        status: "Queued",
        priority: PrintJobPriority.Low,
        queuePosition: 1,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
        queuedAtUtc: new Date().toISOString(),
      },
      gcodeFile: {
        id: "file-no-thumb",
        fileName: "no-thumbnail-print.gcode",
        fileSizeBytes: 0,
        createdAtUtc: new Date().toISOString(),
        // thumbnailUrl intentionally omitted — no embedded thumbnail metadata.
      },
    });

    const { container } = render(<QueueJobsTable jobs={[jobWithoutThumbnail]} {...mockHandlers} />);

    expect(container.querySelector("img")).not.toBeInTheDocument();
    expect(screen.getByText("—")).toBeInTheDocument();
  });

  it("should call onEdit when the detail row is clicked", () => {
    const onEdit = vi.fn();
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel: vi.fn(),
      onPriority: vi.fn(),
      onEdit,
    };

    const { container } = render(<QueueJobsTable jobs={mockJobs} {...mockHandlers} />);

    // The secondary (detail) row lives in the same <tbody> as the primary row;
    // clicking a detail chip must still open edit (handlers live on the <tbody>).
    const firstBody = container.querySelector("tbody");
    const detailRow = firstBody?.querySelectorAll("tr")[1];
    expect(detailRow).toBeTruthy();
    fireEvent.click(detailRow as Element);

    expect(onEdit).toHaveBeenCalledWith("job-1");
  });

  it("should call onEdit on Enter only when the row itself is focused", () => {
    const onEdit = vi.fn();
    const onCancel = vi.fn();
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel,
      onPriority: vi.fn(),
      onEdit,
    };

    const { container } = render(<QueueJobsTable jobs={mockJobs} {...mockHandlers} />);
    const firstBody = container.querySelector("tbody") as Element;

    // Enter on the row itself opens edit.
    fireEvent.keyDown(firstBody, { key: "Enter" });
    expect(onEdit).toHaveBeenCalledWith("job-1");
    onEdit.mockClear();

    // Enter on a child action control must NOT be hijacked into edit (WCAG 2.1.1).
    const cancelButton = screen.getAllByText("Cancel")[0];
    fireEvent.keyDown(cancelButton, { key: "Enter" });
    expect(onEdit).not.toHaveBeenCalled();
  });

});

// ---------------------------------------------------------------------------
// Filament coverage badge visibility (Fix #3: badge for runout only)
// ---------------------------------------------------------------------------

describe("QueueJobsTable — filament coverage badge", () => {
  const jobWithPrinter = {
    id: "badge-job",
    job: {
      id: "badge-job",
      name: "badge-test-print",
      gcodeFileId: "f-badge",
      status: "Queued" as const,
      priority: PrintJobPriority.Low,
      queuePosition: 1,
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      queuedAtUtc: new Date().toISOString(),
      wasSeededFromHistory: false,
    },
    gcodeFile: {
      id: "f-badge",
      fileName: "badge-test.gcode",
      fileSizeBytes: 512,
      createdAtUtc: new Date().toISOString(),
    },
    assignedPrinter: {
      id: "printer-badge",
      name: "Badge Printer",
      modelName: "Prusa CORE One",
      status: "online" as const,
      isOnline: true,
    },
  };

  function fleetWithStatus(status: string) {
    return {
      data: {
        printers: [
          {
            printerId: "printer-badge",
            printerName: "Badge Printer",
            status,
            toolheads: [],
            activeJobId: null,
            activeJobName: null,
            activeJobProgress: null,
            earliestPredictedRunoutAt: null,
            assignedQueuedJobCount: 0,
            evaluatedAtUtc: new Date().toISOString(),
          },
        ],
        evaluatedAtUtc: new Date().toISOString(),
      },
      isSuccess: true,
      isLoading: false,
      isError: false,
    };
  }

  beforeEach(() => {
    mockGetCoverage.mockReturnValue({
      data: { printers: [], evaluatedAtUtc: new Date().toISOString() },
      isSuccess: true,
      isLoading: false,
      isError: false,
    });
  });

  it("shows the runout badge when assigned printer status is 'runout'", async () => {
    mockGetCoverage.mockReturnValue(fleetWithStatus("runout"));
    render(<QueueJobsTable jobs={[jobWithPrinter]} />);
    const badge = await screen.findByRole("status", { name: /runout risk/i });
    expect(badge).toBeInTheDocument();
    expect(badge).toHaveAttribute("data-status", "runout");
  });

  it("does NOT show a badge when assigned printer status is 'unknown'", async () => {
    mockGetCoverage.mockReturnValue(fleetWithStatus("unknown"));
    render(<QueueJobsTable jobs={[jobWithPrinter]} />);
    await waitFor(() =>
      expect(screen.queryByRole("status", { name: /runout risk/i })).not.toBeInTheDocument(),
    );
  });

  it("does NOT show a badge when assigned printer status is 'covers'", async () => {
    mockGetCoverage.mockReturnValue(fleetWithStatus("covers"));
    render(<QueueJobsTable jobs={[jobWithPrinter]} />);
    await waitFor(() =>
      expect(screen.queryByRole("status", { name: /runout risk/i })).not.toBeInTheDocument(),
    );
  });

  it("does NOT show a badge when the printer is not in the fleet coverage map", async () => {
    mockGetCoverage.mockReturnValue({
      data: { printers: [], evaluatedAtUtc: new Date().toISOString() },
      isSuccess: true,
      isLoading: false,
      isError: false,
    });
    render(<QueueJobsTable jobs={[jobWithPrinter]} />);
    await waitFor(() =>
      expect(screen.queryByRole("status", { name: /runout risk/i })).not.toBeInTheDocument(),
    );
  });

  it("does NOT show the runout badge when the assigned printer is offline, even with a last-known 'runout' status (#1684)", async () => {
    mockGetCoverage.mockReturnValue(fleetWithStatus("runout"));
    const offlineJob = {
      ...jobWithPrinter,
      assignedPrinter: { ...jobWithPrinter.assignedPrinter, isOnline: false },
    };
    render(<QueueJobsTable jobs={[offlineJob]} />);
    await waitFor(() =>
      expect(screen.queryByRole("status", { name: /runout risk/i })).not.toBeInTheDocument(),
    );
  });
});