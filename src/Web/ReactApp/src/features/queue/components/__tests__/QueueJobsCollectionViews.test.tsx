import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import { describe, expect, it, vi } from "vitest";
import {
  QueueJobsCardView,
  QueueJobsListView,
} from "../QueueJobsCollectionViews";
import { QueueViewModeSelector } from "../QueueViewModeSelector";
import type { QueuedPrintJobWithFileMetaDto } from "@/services/printQueueService";
import { PrintJobPriority } from "@/types/api";
import {
  canDropQueueJob,
  getQueueMoveNeighbors,
} from "@/features/queue/utils/queueReordering";

function createMockJob(
  overrides?: Partial<QueuedPrintJobWithFileMetaDto>,
): QueuedPrintJobWithFileMetaDto {
  return {
    job: {
      id: "job-1",
      name: "benchy-print",
      gcodeFileId: "file-1",
      copies: 1,
      completedCopies: 0,
      remainingCopies: 1,
      status: "Queued",
      priority: PrintJobPriority.Low,
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
      name: "benchy.gcode",
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

  it("supports both keyboard reorder buttons and restricts pointer drops to the same priority", async () => {
    const firstBase = createMockJob();
    const first = createMockJob({
      job: { ...firstBase.job, rowVersion: "etag-job-1" },
    });
    const second = createMockJob({
      job: { ...first.job, id: "job-2", name: "next-print", rowVersion: "etag-job-2" },
      gcodeFile: { ...first.gcodeFile!, id: "file-2", name: "next.gcode", fileName: "next.gcode" },
    });
    const differentPriority = createMockJob({
      job: {
        ...first.job,
        id: "job-3",
        name: "urgent-print",
        rowVersion: "etag-job-3",
        priority: PrintJobPriority.Urgent,
      },
      gcodeFile: {
        ...first.gcodeFile!,
        id: "file-3",
        name: "urgent.gcode",
        fileName: "urgent.gcode",
      },
    });
    const jobs = [first, second, differentPriority];
    const onMoveJob = vi.fn();
    const onDragStartJob = vi.fn();
    const user = userEvent.setup();

    const { rerender } = render(
      <QueueJobsListView
        jobs={jobs}
        canReorder
        reorderNeighbors={getQueueMoveNeighbors(jobs)}
        onMoveJob={onMoveJob}
      />,
    );
    const moveUp = screen.getByRole("button", { name: "Move next.gcode up" });
    const moveDown = screen.getByRole("button", { name: "Move benchy.gcode down" });
    moveUp.focus();
    await user.keyboard("{Enter}");
    expect(onMoveJob).toHaveBeenCalledWith("job-2", "job-1", "before");
    expect(moveDown).toBeEnabled();
    await user.click(moveDown);
    expect(onMoveJob).toHaveBeenLastCalledWith("job-1", "job-2", "after");
    expect(screen.getByRole("button", { name: "Move next.gcode down" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Move urgent.gcode up" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Move urgent.gcode down" })).toBeDisabled();

    onMoveJob.mockClear();
    rerender(
      <QueueJobsCardView
        jobs={jobs}
        canReorder
        draggedJobId="job-2"
        reorderNeighbors={getQueueMoveNeighbors(jobs)}
        onMoveJob={onMoveJob}
        onDragStartJob={onDragStartJob}
        canDropOnJob={(movedId, neighborId) => canDropQueueJob(jobs, movedId, neighborId)}
      />,
    );
    const source = screen.getByRole("listitem", { name: /next\.gcode/i });
    const samePriorityTarget = screen.getByRole("listitem", { name: /benchy\.gcode/i });
    const otherPriorityTarget = screen.getByRole("listitem", { name: /urgent\.gcode/i });
    const dataTransfer = {
      effectAllowed: "",
      dropEffect: "",
      setData: vi.fn(),
      getData: vi.fn().mockReturnValue("job-2"),
    } as unknown as DataTransfer;
    vi.spyOn(samePriorityTarget, "getBoundingClientRect").mockReturnValue(new DOMRect(0, 0, 100, 100));

    fireEvent.dragStart(source, { dataTransfer });
    fireEvent.drop(otherPriorityTarget, { dataTransfer, clientY: 5 });
    expect(onMoveJob).not.toHaveBeenCalled();

    fireEvent.drop(samePriorityTarget, { dataTransfer, clientY: 95 });

    expect(onDragStartJob).toHaveBeenCalledWith("job-2");
    expect(onMoveJob).toHaveBeenCalledWith("job-2", "job-1", "after");
  });

  it("card view falls back to the live printer thumbnail for an active external print", () => {
    // External print: no local gcode thumbnail, but the printer reports one live.
    const job = createMockJob({
      job: {
        id: "job-1",
        name: "external-print",
        gcodeFileId: "",
        copies: 1,
        completedCopies: 0,
        remainingCopies: 1,
        status: "Printing",
        priority: PrintJobPriority.Low,
        queuePosition: 1,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
        queuedAtUtc: new Date().toISOString(),
      },
      gcodeFile: {
        id: "file-1",
        name: "file-1",
        fileName: "external-print.gcode",
        fileSizeBytes: 0,
        createdAtUtc: new Date().toISOString(),
      },
    });

    const { container } = render(
      <QueueJobsCardView
        jobs={[job]}
        printThumbnailByPrinterId={{ "printer-1": "http://printer/live.png" }}
      />,
    );

    expect(
      container.querySelector('img[src="http://printer/live.png"]'),
    ).toBeInTheDocument();
  });

  it("list view does NOT show a live thumbnail on a Queued job pre-assigned to a busy printer", () => {
    const job = createMockJob({
      job: {
        id: "job-1",
        name: "waiting-print",
        gcodeFileId: "",
        copies: 1,
        completedCopies: 0,
        remainingCopies: 1,
        status: "Queued",
        priority: PrintJobPriority.Low,
        queuePosition: 1,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
        queuedAtUtc: new Date().toISOString(),
      },
      gcodeFile: {
        id: "file-1",
        name: "file-1",
        fileName: "waiting-print.gcode",
        fileSizeBytes: 0,
        createdAtUtc: new Date().toISOString(),
      },
    });

    const { container } = render(
      <QueueJobsListView
        jobs={[job]}
        printThumbnailByPrinterId={{
          "printer-1": "http://printer/other-job.png",
        }}
      />,
    );

    expect(
      container.querySelector('img[src="http://printer/other-job.png"]'),
    ).not.toBeInTheDocument();
  });
});
