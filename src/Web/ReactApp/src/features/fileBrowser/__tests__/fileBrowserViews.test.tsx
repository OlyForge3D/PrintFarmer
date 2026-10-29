import { describe, it, expect, vi, afterEach } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { GridView } from "../components/GridView";
import { ExplorerView } from "../components/ExplorerView";
import type { ColumnDef, FileItem, FolderNode } from "../types";

/** Installs a `window.matchMedia` mock reporting `matches` for every query, mirroring
 * the media-query listener contract `ExplorerView` relies on to detect the mobile layout. */
function mockMatchMedia(matches: boolean) {
  window.matchMedia = vi.fn().mockImplementation((query: string) => ({
    matches,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(),
  }));
}

const files: FileItem[] = [
  {
    id: "1",
    path: "/file.gcode",
    fileName: "file.gcode",
    isDirectory: false,
    fileSize: 1024,
  },
  { id: "2", path: "/dir", fileName: "dir", isDirectory: true },
];

const folders: FolderNode[] = [
  {
    path: "/",
    name: "Root",
    children: [{ path: "/dir", name: "dir", children: [] }],
  },
];

const columns: ColumnDef[] = [
  { key: "fileName", label: "Name", sortable: true },
];

describe("GridView", () => {
  it("supports select all", async () => {
    const onSelectAll = vi.fn();
    render(
      <GridView
        files={files}
        selectedIds={[]}
        onToggle={vi.fn()}
        onSelectAll={onSelectAll}
        renderItemActions={() => null}
        page={1}
        totalPages={1}
        onPageChange={vi.fn()}
      />,
    );

    await userEvent.click(screen.getByLabelText("Select all files"));
    expect(onSelectAll).toHaveBeenCalled();
  });

  it("navigates to the next server page", async () => {
    const onPageChange = vi.fn();
    render(
      <GridView
        files={files}
        selectedIds={[]}
        onToggle={vi.fn()}
        onSelectAll={vi.fn()}
        page={1}
        totalPages={3}
        onPageChange={onPageChange}
      />,
    );

    await userEvent.click(screen.getByRole("button", { name: "Next page" }));

    expect(onPageChange).toHaveBeenCalledWith(2);
    expect(screen.getByText("Page 1 of 3")).toBeVisible();
  });
});

describe("ExplorerView", () => {
  it("supports sorting and select all", async () => {
    const onSort = vi.fn();
    const onSelectAll = vi.fn();

    render(
      <ExplorerView
        folders={folders}
        files={files}
        selectedIds={[]}
        onToggle={vi.fn()}
        onSelectAll={onSelectAll}
        onNavigate={vi.fn()}
        currentPath="/"
        renderActions={() => null}
        sortBy="fileName"
        sortOrder="asc"
        onSort={onSort}
        page={1}
        totalPages={1}
        onPageChange={vi.fn()}
        columns={columns}
      />,
    );

    await userEvent.click(screen.getByLabelText("Select all files"));
    expect(onSelectAll).toHaveBeenCalled();

    await userEvent.click(
      screen.getByRole("button", { name: /Sort by Name/i }),
    );
    expect(onSort).toHaveBeenCalledWith("fileName");
  });

  it("keeps the folder delete action named, destructive, and operable", async () => {
    render(
      <ExplorerView
        folders={folders}
        files={files}
        selectedIds={[]}
        onToggle={vi.fn()}
        onSelectAll={vi.fn()}
        onNavigate={vi.fn()}
        currentPath="/"
        renderActions={() => null}
        sortBy="fileName"
        sortOrder="asc"
        onSort={vi.fn()}
        page={1}
        totalPages={1}
        onPageChange={vi.fn()}
        columns={columns}
      />,
    );

    fireEvent.contextMenu(screen.getByRole("button", { name: "dir" }));

    const deleteAction = screen.getByRole("button", { name: "Delete Folder" });
    expect(deleteAction).toHaveClass(
      "text-[var(--pf-error-fg)]",
      "enabled:hover:bg-pf-bg-1",
    );
    expect(deleteAction).not.toHaveClass("text-pf-error");

    await userEvent.click(deleteAction);
    expect(
      screen.getByText('Delete folder "dir"? This action cannot be undone.'),
    ).toBeVisible();
  });
});

describe("compact presentation (opt-in, used by /files)", () => {
  const renderTable = (compact?: boolean, showFolderNavigation?: boolean) =>
    render(
      <ExplorerView
        folders={folders}
        files={files.filter((file) => !file.isDirectory)}
        selectedIds={[]}
        onToggle={vi.fn()}
        onSelectAll={vi.fn()}
        onNavigate={vi.fn()}
        currentPath="/"
        renderActions={() => null}
        sortBy="fileName"
        sortOrder="asc"
        onSort={vi.fn()}
        page={1}
        totalPages={1}
        onPageChange={vi.fn()}
        columns={columns}
        compact={compact}
        showFolderNavigation={showFolderNavigation}
      />,
    );
  const renderGrid = (compact?: boolean) =>
    render(
      <GridView
        files={files}
        selectedIds={[]}
        onToggle={vi.fn()}
        onSelectAll={vi.fn()}
        renderCard={(file) => <span>{file.fileName}</span>}
        page={1}
        totalPages={1}
        onPageChange={vi.fn()}
        compact={compact}
      />,
    );

  it("renders the file table at 12px with tight cells when compact", () => {
    renderTable(true);
    const table = screen.getByRole("table", { name: "Files list" });
    expect(table).toHaveClass("text-xs");
    expect(table).not.toHaveClass("text-sm");
    expect(screen.getByText("file.gcode").closest("td")).toHaveClass("py-1.5");
  });

  it("keeps the default table unchanged for other file browsers", () => {
    renderTable();
    const table = screen.getByRole("table", { name: "Files list" });
    expect(table).toHaveClass("text-sm");
    expect(table).not.toHaveClass("text-xs");
    expect(screen.getByRole("list", { name: "Folder tree" })).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "File path" })).toBeInTheDocument();
  });

  it("removes the tree, divider and old breadcrumb when a caller supplies folder navigation", () => {
    renderTable(true, false);
    expect(screen.getByRole("table", { name: "Files list" })).toBeInTheDocument();
    expect(screen.queryByRole("list", { name: "Folder tree" })).not.toBeInTheDocument();
    expect(screen.queryByRole("navigation", { name: "File path" })).not.toBeInTheDocument();
    expect(screen.queryByRole("separator", { name: "Resize tree and list views" })).not.toBeInTheDocument();
    expect(screen.queryByTitle("Collapse folder panel")).not.toBeInTheDocument();
  });

  it("lays custom cards out in the auto-fill compact grid only when compact", () => {
    const { unmount } = renderGrid(true);
    expect(screen.getByRole("group", { name: "file.gcode" }).parentElement).toHaveClass(
      "grid-cols-[repeat(auto-fill,minmax(9.5rem,1fr))]",
    );
    unmount();

    renderGrid();
    const grid = screen.getByRole("group", { name: "file.gcode" }).parentElement;
    expect(grid).toHaveClass("sm:grid-cols-2");
    expect(grid).not.toHaveClass("grid-cols-[repeat(auto-fill,minmax(9.5rem,1fr))]");
  });
});

describe("ExplorerView mobile layout (issue #1688)", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  const renderExplorer = () =>
    render(
      <ExplorerView
        folders={folders}
        files={files}
        selectedIds={[]}
        onToggle={vi.fn()}
        onSelectAll={vi.fn()}
        onNavigate={vi.fn()}
        currentPath="/"
        renderActions={() => null}
        sortBy="fileName"
        sortOrder="asc"
        onSort={vi.fn()}
        page={1}
        totalPages={1}
        onPageChange={vi.fn()}
        columns={columns}
      />,
    );

  it("stacks the folder tree above the file table below the sm breakpoint instead of clipping it", () => {
    mockMatchMedia(true); // simulates the 375px reproduction viewport

    renderExplorer();

    const region = screen.getByRole("region", { name: "Explorer view" });
    expect(region).toHaveClass("flex-col");
    expect(region).not.toHaveClass("flex-row");

    // The resize divider only makes sense for a side-by-side (width) split; it must
    // not render in the stacked mobile layout.
    expect(
      screen.queryByRole("separator", { name: "Resize tree and list views" }),
    ).not.toBeInTheDocument();

    // The tree panel must not be pinned to a fixed pixel width on mobile, or it
    // would again squeeze the file table into a narrow clipped column.
    const folderTreeHeading = screen.getByText("Folders");
    const treePanel = folderTreeHeading.closest('div[class*="max-h-"]');
    expect(treePanel).not.toBeNull();
    expect(treePanel).not.toHaveAttribute(
      "style",
      expect.stringContaining("width"),
    );

    // Folders and the file table both remain present and readable.
    expect(screen.getByLabelText("Folder tree")).toBeVisible();
    expect(screen.getByRole("table", { name: "Files list" })).toBeVisible();
  });

  it("keeps the folder tree and file table side-by-side with a resizable divider at desktop widths", () => {
    mockMatchMedia(false);

    renderExplorer();

    const region = screen.getByRole("region", { name: "Explorer view" });
    expect(region).toHaveClass("flex-row");
    expect(region).not.toHaveClass("flex-col");

    expect(
      screen.getByRole("separator", { name: "Resize tree and list views" }),
    ).toBeInTheDocument();
  });
});
