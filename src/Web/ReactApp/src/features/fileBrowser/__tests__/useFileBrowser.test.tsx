import { describe, it, expect, vi } from "vitest";
import { renderHook, act, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useFileBrowser } from "../useFileBrowser";
import type { FileQueryState, UseFileBrowserConfig } from "../types";

const createWrapper = () => {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
    },
  });

  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
};

describe("useFileBrowser", () => {
  const domainItem = { id: "1", path: "/file.gcode", name: "file.gcode" };

  const mapQueryParams = (query: FileQueryState) => ({
    search: query.search,
    page: query.page,
  });

  const baseConfig: UseFileBrowserConfig<typeof domainItem> = {
    fetcher: vi.fn(async () => ({
      items: [domainItem],
      totalItems: 1,
      totalPages: 1,
      page: 1,
    })),
    mapQueryParams,
    mapDomainToFileItem: (item) => ({
      id: item.id,
      path: item.path,
      fileName: item.name,
      isDirectory: false,
    }),
  };

  it("loads files and maps domain items", async () => {
    const wrapper = createWrapper();
    const { result } = renderHook(() => useFileBrowser(baseConfig), {
      wrapper,
    });

    await waitFor(() => {
      expect(result.current.files).toHaveLength(1);
    });

    expect(result.current.files[0].fileName).toBe("file.gcode");
  });

  it("supports selection", async () => {
    const wrapper = createWrapper();
    const { result } = renderHook(() => useFileBrowser(baseConfig), {
      wrapper,
    });

    await waitFor(() => {
      expect(result.current.files).toHaveLength(1);
    });

    act(() => {
      result.current.toggleSelect("1");
    });

    expect(result.current.selectedIds).toEqual(["1"]);
  });

  it("cycles server sorting through ascending, descending, and the configured default without reordering a page", async () => {
    const serverItems = [
      { id: "2", path: "/z.gcode", name: "z.gcode" },
      { id: "1", path: "/a.gcode", name: "a.gcode" },
    ];
    const fetcher = vi.fn(async (params: unknown) => ({
      items: serverItems, totalItems: 4, totalPages: 2, page: (params as FileQueryState).page,
    }));
    const config = {
      ...baseConfig, fetcher, mapQueryParams: (query: FileQueryState) => query,
      defaultSort: { sortBy: "date", sortOrder: "desc" as const },
    };
    const { result } = renderHook(() => useFileBrowser(config), { wrapper: createWrapper() });
    await waitFor(() => expect(result.current.isLoading).toBe(false));
    act(() => { result.current.setPage(2); result.current.toggleSelect("2"); });
    await waitFor(() => expect(result.current.page).toBe(2));

    act(() => result.current.cycleSort("name"));
    await waitFor(() => expect(fetcher).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortBy: "name", sortOrder: "asc", page: 1 }), expect.any(AbortSignal),
    ));
    act(() => result.current.cycleSort("name"));
    await waitFor(() => expect(fetcher).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortBy: "name", sortOrder: "desc", page: 1 }), expect.any(AbortSignal),
    ));
    act(() => result.current.cycleSort("name"));
    await waitFor(() => expect(fetcher).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortBy: "date", sortOrder: "desc", page: 1 }), expect.any(AbortSignal),
    ));
    expect(result.current.sortBy).toBe("");
    expect(result.current.selectedIds).toEqual(["2"]);
    expect(result.current.files.map(file => file.fileName)).toEqual(["z.gcode", "a.gcode"]);
    act(() => result.current.cycleSort("name"));
    expect(result.current.sortBy).toBe("name");
    expect(result.current.sortOrder).toBe("asc");
  });
});
