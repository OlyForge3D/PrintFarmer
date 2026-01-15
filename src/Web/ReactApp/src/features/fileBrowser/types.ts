import { ReactNode } from 'react';

export type SortOrder = 'asc' | 'desc';
export type ViewMode = 'grid' | 'explorer';

// Base UI representation of a file (generic across all file types)
export interface FileItem {
  id: string;
  path: string;
  fileName: string;
  isDirectory: boolean;
  fileSize?: number;
  uploadedAt?: string;
  tags?: Array<{ id: string; name: string; color?: string }>;
  thumbnailUrl?: string;
  meta?: {
    gcode?: {
      requiredMaterial?: string;
      extractedNozzleDiameter?: number;
      extractedPrinterModelName?: string;
      extractedMaterial?: string;
      extractedPrintTime?: number;
      extractedLayerHeight?: number;
    };
    model3d?: Record<string, unknown>;
  };
}

// Type-safe accessors for type-specific data
export interface GcodeFileItem extends FileItem {
  meta: NonNullable<FileItem['meta']>;
}

export interface  Model3DFileItem extends FileItem {
  meta: NonNullable<FileItem['meta']>
}

export interface FolderNode {
  path: string;
  name: string;
  children?: FolderNode[];
}

export interface FileQueryState {
  path: string;
  search: string;
  sortBy: string;
  sortOrder: SortOrder;
  page: number;
  pageSize: number;
}

export interface FetchFilesResult<TDomain> {
  items: TDomain[];
  totalItems: number;
  totalPages: number;
  totalSize?: number;
  page?: number;
  folders?: FolderNode[];
  currentPath?: string;
}

export interface ColumnDef {
  key: string;
  label: string;
  sortable?: boolean;
  align?: 'left' | 'right';
  width?: string;
  render?: (file: FileItem) => ReactNode;
}

export interface UseFileBrowserConfig<TDomain> {
  fetcher: (params: unknown, signal?: AbortSignal) => Promise<FetchFilesResult<TDomain>>;
  mapDomainToFileItem: (item: TDomain) => FileItem;
  mapQueryParams?: (params: FileQueryState) => unknown;
  pageSize?: number;
  pageSizeOptions?: number[];
  defaultSort?: { sortBy: string; sortOrder?: SortOrder };
  canCreateDirectory?: boolean;
  onCreateDirectory?: (path: string, folderName: string) => Promise<void>;
  onMoveFiles?: (fileIds: string[], targetPath: string) => Promise<void>;
  selectedIds?: string[];
  onSelectionChange?: (ids: string[]) => void;
  initialPath?: string;
  viewMode?: ViewMode;
  onViewModeChange?: (mode: ViewMode) => void;
  columns?: ColumnDef[];
  extraActionsSlot?: ReactNode;
}