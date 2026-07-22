import { fireEvent, render, screen } from "@testing-library/react";
import "@testing-library/jest-dom";
import { describe, expect, it, vi } from "vitest";
import { QueueJobsCardView, QueueJobsListView } from "../QueueJobsCollectionViews";
import { QueueViewModeSelector } from "../QueueViewModeSelector";
import type { QueuedPrintJobWithFileMetaDto } from "@/services/printQueueService";

function createMockJob(overrides?: Partial<QueuedPrintJobWithFileMetaDto>): QueuedPrintJobWithFileMetaDto {
  return {
    id: "job-1",
    job: {
      id: "job-1",
      name: "benchy-print",
      gcodeFileId: "file-1",
      status: "Queued",
      priority: 0,
      queuePosition: 1,
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      queuedAtUtc: new Date().toISOString(),
      deadlineAtUtc: new Date(Date.now() + 6 * 60 * 60 * 1000).toISOString(),
      estimatedPrintTimeSeconds: 3_600,
      estimatedFilamentUsageGrams: 23.5,
      requiredMaterialType: "PLA",
      wasSeededFromHistory: false,
    },
    gcodeFile: {
      id: "file-1",
      fileName: "benchy.gcode",
      fileSizeBytes: 2048,
      materialType: "PLA",
      createdAtUtc: new Date().toISOString(),
      thumbnailUrl: "https://example.com/thumb.png",
    },
    assignedPrinter: {
      id: "printer-1",
      name: "Printer One",
      modelName: "X1C",
      status: "online",
      isOnline: true,
    },
    ...overrides,
  };
}

describe("Queue view mode + collection renderers", () => {
  it("switches view mode from selector", () => {
    const onChange = vi.fn();
    render(<QueueViewModeSelector value="table" onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: "List view" }));

    expect(onChange).toHaveBeenCalledWith("list");
  });

  it("renders card view metadata and opens details on Enter", () => {
    const onEdit = vi.fn();
    const job = createMockJob();
    render(<QueueJobsCardView jobs={[job]} onEdit={onEdit} />);

    expect(screen.getByText("benchy.gcode")).toBeInTheDocument();
    expect(screen.getByText("Printer One")).toBeInTheDocument();
    expect(screen.getByText("Due soon")).toBeInTheDocument();

    const card = screen.getByRole("listitem", { name: /benchy\.gcode/i });
    fireEvent.keyDown(card, { key: "Enter" });

    expect(onEdit).toHaveBeenCalledWith("job-1");
  });

  it("renders list view actions", () => {
    const onCancel = vi.fn();
    const job = createMockJob();
    render(<QueueJobsListView jobs={[job]} onCancel={onCancel} />);

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(onCancel).toHaveBeenCalledWith("job-1");
  });

  it("card view falls back to the live printer thumbnail for an active external print", () => {
    // External print: no local gcode thumbnail, but the printer reports one live.
    const job = createMockJob({
      job: {
        id: "job-1",
        name: "external-print",
        gcodeFileId: "",
        status: "Printing",
        priority: 0,
        queuePosition: 1,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
        queuedAtUtc: new Date().toISOString(),
      },
      gcodeFile: {
        id: "file-1",
        fileName: "external-print.gcode",
        fileSizeBytes: 0,
        createdAtUtc: new Date().toISOString(),
      },
    });

    const { container } = render(
      <QueueJobsCardView jobs={[job]} printThumbnailByPrinterId={{ "printer-1": "http://printer/live.png" }} />,
    );

    expect(container.querySelector('img[src="http://printer/live.png"]')).toBeInTheDocument();
  });

  it("list view does NOT show a live thumbnail on a Queued job pre-assigned to a busy printer", () => {
    const job = createMockJob({
      job: {
        id: "job-1",
        name: "waiting-print",
        gcodeFileId: "",
        status: "Queued",
        priority: 0,
        queuePosition: 1,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
        queuedAtUtc: new Date().toISOString(),
      },
      gcodeFile: {
        id: "file-1",
        fileName: "waiting-print.gcode",
        fileSizeBytes: 0,
        createdAtUtc: new Date().toISOString(),
      },
    });

    const { container } = render(
      <QueueJobsListView jobs={[job]} printThumbnailByPrinterId={{ "printer-1": "http://printer/other-job.png" }} />,
    );

    expect(container.querySelector('img[src="http://printer/other-job.png"]')).not.toBeInTheDocument();
  });
});

