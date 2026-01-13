import React, { useState, useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ChevronRightIcon, FolderIcon } from '@heroicons/react/24/outline';
import { Button } from '@/common/components/ui';
import { ModelListView } from '@/features/models3d/components/ModelListView';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';

export interface FileEntry {
  path: string;
  fileName: string;
  name?: string;
  size: number;
  modifiedAt: string;
  isDirectory: boolean;
  thumbnailPath?: string;
  modelId?: string;
  directoryId?: string;
  tags?: Array<{
    id: string;
    name: string;
    color?: string;
  }>;
}

interface FolderNode {
  name: string;
  path: string;
  directoryId: string;
  children: FolderNode[];
  expanded: boolean;
}

interface ExplorerModelListViewProps {
  onFileSelect?: (file: FileEntry) => void;
  onTagModel?: (model: FileEntry) => void;
  onDelete?: (file: FileEntry) => void;
  onDownload?: (modelId: string) => void;
  sortBy?: string;
  sortOrder?: 'asc' | 'desc';
  onSort?: (sortBy: string) => void;
  selectedFiles?: string[];
}

const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

export const ExplorerModelListView: React.FC<ExplorerModelListViewProps> = ({
  onFileSelect,
  onTagModel,
  onDelete,
  onDownload,
  selectedFiles = [],
}) => {
  const [selectedFolder, setSelectedFolder] = useState('/');
  const [expandedFolders, setExpandedFolders] = useState(new Set(['/']));
  const [breadcrumbs, setBreadcrumbs] = useState<Array<{ path: string; name: string }>>([]);
  const [isTreeCollapsed, setIsTreeCollapsed] = useState(false);

  // Update breadcrumbs when selected folder changes
  useEffect(() => {
    const parts = selectedFolder.split('/').filter(Boolean);
    const crumbs: Array<{ path: string; name: string }> = [{ path: '/', name: 'Root' }];
    let currentPath = '/';
    
    for (const part of parts) {
      currentPath = currentPath === '/' ? '/' + part : currentPath + '/' + part;
      crumbs.push({ path: currentPath, name: part });
    }
    
    setBreadcrumbs(crumbs);
  }, [selectedFolder]);

  // Fetch hierarchy data for selected folder
  const { data: hierarchyData } = useQuery({
    queryKey: ['models-explorer-hierarchy', selectedFolder],
    queryFn: async () => {
      const response = await fetch(
        `${getApiBaseUrl()}/3d-models/hierarchy?path=${encodeURIComponent(selectedFolder)}`,
        { headers: getAuthHeaders() }
      );
      if (!response.ok) throw new Error('Failed to fetch files');
      return response.json();
    }
  });

  // Fetch all folders for tree navigation
  const { data: allFolders = [] } = useQuery({
    queryKey: ['models-explorer-all-folders'],
    queryFn: async () => {
      const folders: string[] = [];
      const queue = ['/'];

      while (queue.length > 0) {
        const path = queue.shift()!;
        try {
          const response = await fetch(
            `${getApiBaseUrl()}/3d-models/hierarchy?path=${encodeURIComponent(path)}&pageSize=500`,
            { headers: getAuthHeaders() }
          );
          if (response.ok) {
            const data = await response.json();
            for (const entry of data.files) {
              if (entry.isDirectory) {
                folders.push(entry.path);
                queue.push(entry.path);
              }
            }
          }
        } catch (error) {
          console.error(`Failed to fetch folders at ${path}:`, error);
        }
      }
      return folders;
    },
    staleTime: 0,
    gcTime: 5 * 60 * 1000
  });

  // Separate folders and files
  const folders = hierarchyData?.files?.filter((f: FileEntry) => f.isDirectory) || [];
  const modelFiles = hierarchyData?.files?.filter((f: FileEntry) => !f.isDirectory) || [];

  // Convert files to Model format for ModelListView
  const models = modelFiles.map((file: FileEntry) => ({
    id: file.modelId || file.path,
    name: file.name || file.fileName,
    fileName: file.fileName,
    fileSize: file.size,
    fileType: (file.fileName?.split('.').pop() || 'stl') as 'stl' | '3mf' | 'obj' | 'ply',
    uploadedAt: file.modifiedAt,
    thumbnailPath: file.thumbnailPath,
    tags: file.tags || [],
    isSelected: selectedFiles.includes(file.modelId || file.path),
  }));

  // Build folder tree
  const buildTree = (): FolderNode => {
    const root: FolderNode = {
      name: 'Root',
      path: '/',
      directoryId: '/',
      children: [],
      expanded: true
    };

    const nodeMap = new Map<string, FolderNode>();
    nodeMap.set('/', root);

    // Build a map of folder paths to their directory IDs from the API response
    const folderIdMap = new Map<string, string>();
    (hierarchyData?.files || []).forEach((f: FileEntry) => {
      if (f.isDirectory && f.directoryId) {
        folderIdMap.set(f.path, f.directoryId);
      }
    });

    // Combine folders from allFolders and current hierarchyData to ensure immediate children are shown
    const allFolderPaths = new Set(allFolders);
    folders.forEach((f: FileEntry) => allFolderPaths.add(f.path));
    
    const sortedFolders = Array.from(allFolderPaths).sort();

    for (const folderPath of sortedFolders) {
      const node: FolderNode = {
        name: folderPath.split('/').filter(Boolean).pop() || folderPath,
        path: folderPath,
        directoryId: folderIdMap.get(folderPath) || folderPath,  // Use API directoryId or fall back to path
        children: [],
        expanded: expandedFolders.has(folderPath)
      };
      nodeMap.set(folderPath, node);

      const parentPath = folderPath.substring(0, folderPath.lastIndexOf('/')) || '/';
      const parent = nodeMap.get(parentPath);
      if (parent) {
        parent.children.push(node);
      }
    }

    return root;
  };

  const tree = buildTree();

  const toggleFolder = (path: string) => {
    setExpandedFolders(prev => {
      const next = new Set(prev);
      if (next.has(path)) {
        next.delete(path);
      } else {
        next.add(path);
      }
      return next;
    });
  };

  const renderFolderTree = (node: FolderNode, depth: number = 0): React.ReactNode => {
    const children = node.children.slice().sort((a, b) => a.name.localeCompare(b.name));
    const isSelected = selectedFolder === node.path;

    return (
      <div key={node.path}>
        {node.path !== '/' && (
          <div
            onClick={() => setSelectedFolder(node.path)}
            className={`flex items-center gap-2 px-2 py-1.5 cursor-pointer hover:bg-pf-bg-2 rounded transition-colors ${
              isSelected ? 'bg-pf-accent bg-opacity-40 border-l-2 border-pf-accent text-white font-semibold' : ''
            }`}
            style={{ paddingLeft: `${depth * 16 + 8}px` }}
          >
            <Button
              onClick={(e) => {
                e.stopPropagation();
                toggleFolder(node.path);
              }}
              variant="subtle"
              size="sm"
              className="!p-0 !bg-transparent !border-0 flex-shrink-0 text-transparent hover:text-pf-text-secondary"
            >
              <ChevronRightIcon
                className={`w-4 h-4 transition-transform ${
                  node.expanded ? 'rotate-90' : ''
                }`}
              />
            </Button>
            <FolderIcon className={`w-4 h-4 flex-shrink-0 ${
              isSelected ? 'text-white' : 'text-pf-text-secondary'
            }`} />
            <span className="truncate text-sm">{node.name}</span>
          </div>
        )}
        {node.expanded && children.length > 0 && (
          <div>
            {children.map(child => renderFolderTree(child, depth + 1))}
          </div>
        )}
      </div>
    );
  };

  return (
    <div className="flex h-full gap-0 bg-pf-bg rounded-lg border border-pf-border">
      {/* Left Pane: Collapsible Folder Tree (Option B - narrow sidebar when collapsed) */}
      <div
        className={`flex-shrink-0 border-r border-pf-border overflow-y-auto transition-all duration-300 ${
          isTreeCollapsed ? 'w-16' : 'w-64'
        }`}
      >
        <div className="p-3 sticky top-0 bg-pf-bg border-b border-pf-border flex items-center justify-between">
          <h3 className={`text-sm font-semibold text-pf-text ${isTreeCollapsed ? 'hidden' : ''}`}>
            Folders
          </h3>
          <Button
            onClick={() => setIsTreeCollapsed(!isTreeCollapsed)}
            variant="subtle"
            size="sm"
            title={isTreeCollapsed ? 'Expand folder tree' : 'Collapse folder tree'}
            aria-label={isTreeCollapsed ? 'Expand folder tree' : 'Collapse folder tree'}
            aria-expanded={!isTreeCollapsed}
            className="p-1 flex-shrink-0"
          >
            <ChevronRightIcon
              className={`w-4 h-4 transition-transform ${isTreeCollapsed ? 'rotate-0' : 'rotate-180'}`}
            />
          </Button>
        </div>
        {!isTreeCollapsed && <div className="p-2">{renderFolderTree(tree)}</div>}
      </div>

      {/* Right Pane: Main Content */}
      <div className="flex-1 flex flex-col overflow-hidden min-w-0">
        {/* Breadcrumbs */}
        <div className="flex items-center gap-2 px-4 py-3 border-b border-pf-border">
          {breadcrumbs.map((crumb, index) => (
            <div key={crumb.path} className="flex items-center gap-2">
              {index > 0 && <span className="text-pf-text-tertiary">/</span>}
              <Button
                onClick={() => setSelectedFolder(crumb.path)}
                variant="subtle"
                className="text-sm h-auto p-0 text-pf-accent hover:underline"
              >
                {crumb.name}
              </Button>
            </div>
          ))}
        </div>

        {/* File List or Folders */}
        {folders.length > 0 && modelFiles.length === 0 ? (
          // Show subfolders
          <div className="flex-1 overflow-y-auto p-4">
            <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
              {folders.map((folder: FileEntry) => (
                <Button
                  key={folder.path}
                  onClick={() => setSelectedFolder(folder.path)}
                  variant="secondary"
                  className="p-4 h-auto text-left justify-start"
                >
                  <div className="flex items-center gap-3">
                    <FolderIcon className="w-6 h-6 text-pf-text-secondary flex-shrink-0" />
                    <span className="text-sm font-medium text-pf-text-primary truncate">
                      {folder.name || folder.fileName}
                    </span>
                  </div>
                </Button>
              ))}
            </div>
          </div>
        ) : modelFiles.length > 0 ? (
          // Show files using ModelListView
          <div className="flex-1 overflow-hidden">
            <ModelListView
              models={models}
              isLoading={false}
              onViewerModel={(model) => {
                const file = modelFiles.find((f: FileEntry) => f.modelId === model.id);
                if (file) onFileSelect?.(file);
              }}
              onTagModel={(model) => {
                const file = modelFiles.find((f: FileEntry) => f.modelId === model.id);
                if (file) onTagModel?.(file);
              }}
              onDelete={(model) => {
                const file = modelFiles.find((f: FileEntry) => f.modelId === model.id);
                if (file) onDelete?.(file);
              }}
              onDownload={(model) => {
                onDownload?.(model.id);
              }}
              formatFileSize={formatBytes}
            />
          </div>
        ) : (
          // Empty folder
          <div className="flex-1 flex items-center justify-center">
            <div className="text-center">
              <FolderIcon className="w-12 h-12 text-pf-text-tertiary opacity-30 mx-auto mb-3" />
              <p className="text-pf-text-secondary">This folder is empty</p>
            </div>
          </div>
        )}
      </div>
    </div>
  );
};
