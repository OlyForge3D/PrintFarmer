import { useCallback, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  type ColumnDef,
  type FileItem,
  type FileQueryState,
  type FolderNode,
  type SortOrder,
  type UseFileBrowserConfig,
  type ViewMode,
} from './types';

const normalizeFile = (file: FileItem): FileItem => ({
  ...file,
  id: file.id || file.path,
});

const buildFoldersFromFiles = (files: FileItem[]): FolderNode[] => {
  const roots: Record<string, FolderNode> = {};
  const ensureNode = (path: string): FolderNode => {
    if (roots[path]) return roots[path];
    const name = path === '/' ? 'Root' : path.split('/').filter(Boolean).pop() || path;
    const node: FolderNode = { path, name, children: [] };
    roots[path] = node;
    return node;
  };

  files
    .filter((f) => f.isDirectory)
    .forEach((dir) => {
      const node = ensureNode(dir.path);
      const parentPath = dir.path === '/' ? null : dir.path.slice(0, dir.path.lastIndexOf('/')) || '/';
      if (parentPath) {
        const parent = ensureNode(parentPath);
        if (!parent.children?.some((c) => c.path === dir.path)) {
          parent.children = [...(parent.children || []), node];
        }
      }
    });

  const rootNode = ensureNode('/');
  return [rootNode];
};

export interface UseFileBrowserResult {
  files: FileItem[];
  folders: FolderNode[];
  page: number;
  totalPages: number;
  totalSize: number;
  sortBy: string;
  sortOrder: SortOrder;
  search: string;
  viewMode: ViewMode;
  selectedIds: string[];
  isLoading: boolean;
  isMutating: boolean;
  pageSize: number;
  setSearch: (value: string) => void;
  setSort: (sortBy: string, sortOrder?: SortOrder) => void;
  setPage: (page: number) => void;
  setPageSize: (size: number) => void;
  setViewMode: (mode: ViewMode) => void;
  navigate: (path: string) => void;
  toggleSelect: (id: string) => void;
  selectAll: () => void;
  clearSelection: () => void;
  createDirectory: () => void;
  moveFiles: (fileIds: string[], targetPath: string) => Promise<void>;
  refetch: () => Promise<void>;
  columns?: ColumnDef[];
  currentPath: string;
}

export function useFileBrowser<TDomain>(config: UseFileBrowserConfig<TDomain>): UseFileBrowserResult {
  const {
    fetcher,
    mapDomainToFileItem,
    mapQueryParams,
    pageSize: pageSizeInput = 50,
    defaultSort = { sortBy: 'name', sortOrder: 'asc' },
    selectedIds: controlledSelected,
    onSelectionChange,
    initialPath = '/',
    viewMode: controlledViewMode,
    onViewModeChange,
    columns,
  } = config;

  const queryClient = useQueryClient();
  const [path, setPath] = useState(initialPath);
  const [search, setSearch] = useState('');
  const [sortBy, setSortBy] = useState(defaultSort.sortBy);
  const [sortOrder, setSortOrder] = useState<SortOrder>(defaultSort.sortOrder ?? 'asc');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(pageSizeInput);
  const [localSelection, setLocalSelection] = useState<string[]>([]);
  const [viewModeState, setViewModeState] = useState<ViewMode>(controlledViewMode ?? 'grid');

  const viewMode = controlledViewMode ?? viewModeState;
  const selectedIds = controlledSelected ?? localSelection;

  const setSelection = useCallback(
    (ids: string[]) => {
      if (!controlledSelected) {
        setLocalSelection(ids);
      }
      onSelectionChange?.(ids);
    },
    [controlledSelected, onSelectionChange]
  );

  const setViewMode = useCallback(
    (mode: ViewMode) => {
      if (!controlledViewMode) {
        setViewModeState(mode);
      }
      onViewModeChange?.(mode);
    },
    [controlledViewMode, onViewModeChange]
  );

  const queryState: FileQueryState = useMemo(
    () => ({ path, search, sortBy, sortOrder, page, pageSize }),
    [page, pageSize, path, search, sortBy, sortOrder]
  );

  // Compute actual query params that will be sent to the API
  const actualQueryParams = useMemo(
    () => mapQueryParams ? mapQueryParams(queryState) : queryState,
    [mapQueryParams, queryState]
  );

  const queryKey = useMemo(
    () => [
      'file-browser',
      viewMode, // Include viewMode to trigger refetch when switching between grid/explorer
      JSON.stringify(actualQueryParams),
    ],
    [actualQueryParams, viewMode]
  );

  const filesQuery = useQuery({
    queryKey,
    queryFn: async ({ signal }): Promise<{
      files: FileItem[];
      folders: FolderNode[];
      totalItems: number;
      totalPages: number;
      totalSize: number;
      page: number;
      currentPath: string;
    }> => {
      const result = await fetcher(actualQueryParams, signal);
      const normalizedFiles = (result.items || []).map(mapDomainToFileItem).map(normalizeFile);
      const folders = result.folders && result.folders.length > 0
        ? result.folders
        : buildFoldersFromFiles(normalizedFiles);

      return {
        files: normalizedFiles,
        folders,
        totalItems: result.totalItems,
        totalPages: result.totalPages,
        totalSize: result.totalSize ?? 0,
        page: result.page ?? page,
        currentPath: result.currentPath ?? path,
      };
    },
    placeholderData: (previousData) => previousData,
  });

  const createDirMutation = useMutation({
    mutationFn: async (folderName: string) => {
      const { onCreateDirectory, canCreateDirectory } = config;
      if (!canCreateDirectory || !onCreateDirectory) {
        throw new Error('Create directory not configured');
      }
      await onCreateDirectory(path, folderName);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['file-browser'] });
    },
  });

  const moveFilesMutation = useMutation({
    mutationFn: async (params: { fileIds: string[]; targetPath: string }) => {
      const { onMoveFiles } = config;
      if (!onMoveFiles) {
        throw new Error('Move files not configured');
      }
      await onMoveFiles(params.fileIds, params.targetPath);
    },
    onSuccess: () => {
      setSelection([]);
      queryClient.invalidateQueries({ queryKey: ['file-browser'] });
    },
  });

  const toggleSelect = useCallback(
    (id: string) => {
      const next = selectedIds.includes(id)
        ? selectedIds.filter((p) => p !== id)
        : [...selectedIds, id];
      setSelection(next);
    },
    [selectedIds, setSelection]
  );

  const selectAll = useCallback(() => {
    const ids = (filesQuery.data?.files ?? [])
      .filter((f) => !f.isDirectory)
      .map((f) => f.id || f.path);
    // Toggle: if all are selected, clear; otherwise select all
    if (selectedIds.length === ids.length && ids.every((id) => selectedIds.includes(id))) {
      setSelection([]);
    } else {
      setSelection(ids);
    }
  }, [filesQuery.data?.files, selectedIds, setSelection]);

  const clearSelection = useCallback(() => setSelection([]), [setSelection]);

  const setSort = useCallback(
    (nextSortBy: string, nextSortOrder?: SortOrder) => {
      if (nextSortBy === sortBy) {
        const flipped = nextSortOrder ?? (sortOrder === 'asc' ? 'desc' : 'asc');
        setSortOrder(flipped);
      } else {
        setSortBy(nextSortBy);
        setSortOrder(nextSortOrder ?? 'asc');
      }
      setPage(1);
    },
    [sortBy, sortOrder]
  );

  const setSearchTerm = useCallback((value: string) => {
    setSearch(value);
    setPage(1);
  }, []);

  const navigate = useCallback((nextPath: string) => {
    setPath(nextPath || '/');
    setPage(1);
  }, []);

  const createDirectory = useCallback(() => {
    const folderName = prompt('Enter folder name:');
    if (folderName?.trim()) {
      createDirMutation.mutateAsync(folderName);
    }
  }, [createDirMutation]);

  const moveFiles = useCallback(
    async (fileIds: string[], targetPath: string) => {
      if (!fileIds.length) return;
      await moveFilesMutation.mutateAsync({ fileIds, targetPath });
    },
    [moveFilesMutation]
  );

  return {
    files: filesQuery.data?.files || [],
    folders: filesQuery.data?.folders || [],
    page: filesQuery.data?.page ?? page,
    totalPages: filesQuery.data?.totalPages ?? 1,
    totalSize: filesQuery.data?.totalSize ?? 0,
    sortBy,
    sortOrder,
    search,
    viewMode,
    selectedIds,
    isLoading: filesQuery.isLoading || filesQuery.isFetching,
    isMutating: createDirMutation.isPending || moveFilesMutation.isPending,
    pageSize,
    setSearch: setSearchTerm,
    setSort,
    setPage,
    setPageSize: (size: number) => {
      setPageSize(size);
      setPage(1);
    },
    setViewMode,
    navigate,
    toggleSelect,
    selectAll,
    clearSelection,
    createDirectory,
    moveFiles,
    refetch: async () => {
      await filesQuery.refetch();
    },
    columns,
    currentPath: filesQuery.data?.currentPath ?? path,
  };
}