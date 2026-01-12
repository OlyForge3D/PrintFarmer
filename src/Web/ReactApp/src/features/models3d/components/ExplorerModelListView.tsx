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
  onTagModel
}) => {
  const [selectedFolder, setSelectedFolder] = useState('/');
  const [expandedFolders, setExpandedFolders] = useState(new Set(['/']));
  const [breadcrumbs, setBreadcrumbs] = useState<Array<{ path: string; name: string }>>([]);

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
    tags: file.tags || []
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

    const sortedFolders = [...allFolders].sort();

    for (const folderPath of sortedFolders) {
      const node: FolderNode = {
        name: folderPath.split('/').filter(Boolean).pop() || folderPath,
        path: folderPath,
        directoryId: folderPath,
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
            className={`w-full px-3 py-2 text-left text-sm flex items-center gap-2 hover:bg-pf-bg-secondary transition-colors cursor-pointer ${
              isSelected ? 'bg-pf-bg-secondary text-pf-accent font-semibold' : 'text-pf-text-primary'
            }`}
            style={{ paddingLeft: `${depth * 16 + 12}px` }}
          >
            <Button
              onClick={(e) => {
                e.stopPropagation();
                toggleFolder(node.path);
              }}
              variant="subtle"
              size="sm"
              className="p-0 h-auto hover:bg-pf-bg-1 rounded"
            >
              <ChevronRightIcon
                className={`w-4 h-4 transition-transform ${
                  node.expanded ? 'rotate-90' : ''
                }`}
              />
            </Button>
            <FolderIcon className="w-4 h-4 text-pf-text-secondary flex-shrink-0" />
            <span className="truncate">{node.name}</span>
          </div>
        )}
        {node.expanded && children.length > 0 && (
          <div>
            {children.map(child => renderFolderTree(child, depth + (node.path === '/' ? 0 : 1)))}
          </div>
        )}
      </div>
    );
  };

  return (
    <div className="flex h-full gap-4">
      {/* Folder Tree */}
      <div className="w-56 bg-pf-bg-1 rounded-lg border border-pf-border overflow-y-auto flex-shrink-0">
        <div className="p-3 border-b border-pf-border sticky top-0 bg-pf-bg-1">
          <h3 className="text-sm font-semibold text-pf-text-primary">Folders</h3>
        </div>
        <div className="p-2">
          {renderFolderTree(tree)}
        </div>
      </div>

      {/* Main Content */}
      <div className="flex-1 flex flex-col min-w-0">
        {/* Breadcrumbs */}
        <div className="flex items-center gap-2 px-4 py-2 border-b border-pf-border">
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
