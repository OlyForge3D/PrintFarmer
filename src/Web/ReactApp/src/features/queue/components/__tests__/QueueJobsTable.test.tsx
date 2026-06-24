import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import { QueueJobsTable } from "../QueueJobsTable";
import { QueuedPrintJobWithFileMetaDto } from "@/services/printQueueService";
import "@testing-library/jest-dom";

describe("QueueJobsTable Component", () => {
  const createMockJob = (overrides?: Partial<QueuedPrintJobWithFileMetaDto>): QueuedPrintJobWithFileMetaDto => ({
    id: "job-1",
    job: {
      id: "job-1",
      name: "test-print",
      gcodeFileId: "file-1",
      status: "Queued",
      priority: 0,
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
        priority: 0,
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
        priority: 1,
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
        priority: 0,
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

  it("should render drag handles for reordering", () => {
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel: vi.fn(),
      onPriority: vi.fn(),
    };

    render(<QueueJobsTable jobs={mockJobs} {...mockHandlers} />);

    const dragHandles = screen.getAllByLabelText("Drag to reorder");
    expect(dragHandles.length).toBe(mockJobs.length);
  });

  it("should render rows as draggable", () => {
    const mockHandlers = {
      onPause: vi.fn(),
      onResume: vi.fn(),
      onCancel: vi.fn(),
      onPriority: vi.fn(),
    };

    const { container } = render(<QueueJobsTable jobs={mockJobs} {...mockHandlers} />);

    const rows = container.querySelectorAll('[role="listitem"]');
    expect(rows.length).toBe(mockJobs.length);
    rows.forEach((row) => {
      expect(row).toHaveAttribute("draggable", "true");
    });
  });
});
