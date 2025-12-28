import React, { useState, useRef, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { ChevronRightIcon, FolderIcon, DocumentIcon, TrashIcon, FolderPlusIcon } from '@heroicons/react/24/outline';
import { Button, Checkbox } from '@/components/ui';
import { toast } from 'sonner';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';

export interface FileEntry {
  path: string;
  name: string;
  size: number;
  modifiedAt: string;
  isDirectory: boolean;
  thumbnailUrl?: string;
  modelId?: string;  // File ID (GUID) for efficient file lookups
  directoryId?: string;  // Directory ID (virtual path) for efficient directory lookups
}

interface FolderNode {
  name: string;
  path: string;
  directoryId: string;  // Use GUID for directory lookups
  children: FolderNode[];
  expanded: boolean;
}

interface ExplorerFileBrowserProps {
  endpoint: 'models' | 'gcode';
  onFileSelect?: (file: FileEntry) => void;
  onFileDelete?: (paths: string[]) => void;
}

const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

const formatDate = (dateString: string): string => {
  return new Date(dateString).toLocaleDateString('en-US', {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit'
  });
};

export const ExplorerFileBrowser: React.FC<ExplorerFileBrowserProps> = ({
  endpoint,
  onFileSelect,
  onFileDelete
}) => {
  const [selectedFolder, setSelectedFolder] = useState('/');
  const [expandedFolders, setExpandedFolders] = useState(new Set(['/']));
  const [isCreatingFolder, setIsCreatingFolder] = useState(false);
  const [newFolderName, setNewFolderName] = useState('');
  const [folderNameError, setFolderNameError] = useState<string | null>(null);
  const [selectedFiles, setSelectedFiles] = useState<string[]>([]);
  const [isDragging, setIsDragging] = useState(false);
  const [dragOverPath, setDragOverPath] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const queryClient = useQueryClient();

  // Focus input when creation mode starts
  useEffect(() => {
    if (isCreatingFolder && inputRef.current) {
      inputRef.current.focus();
    }
  }, [isCreatingFolder]);

  // Fetch folders and files for the entire tree and selected folder
  const { data: hierarchyData } = useQuery({
    queryKey: [`${endpoint}-hierarchy`, selectedFolder],
    queryFn: async () => {
      const response = await fetch(
        `${getApiBaseUrl()}/${endpoint === 'models' ? '3d-models' : 'gcode-files'}/hierarchy?path=${encodeURIComponent(selectedFolder)}`,
        { headers: getAuthHeaders() }
      );
      if (!response.ok) throw new Error('Failed to fetch files');
      return response.json();
    }
  });

  // Fetch all folders recursively to build tree
  const { data: allFolders = [] } = useQuery({
    queryKey: [`${endpoint}-all-folders`],
    queryFn: async () => {
      const folders: string[] = [];
      const queue = ['/'];

      while (queue.length > 0) {
        const path = queue.shift()!;
        try {
          const response = await fetch(
            `${getApiBaseUrl()}/${endpoint === 'models' ? '3d-models' : 'gcode-files'}/hierarchy?path=${encodeURIComponent(path)}&pageSize=500`,
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

  // Separate files and folders
  const files = hierarchyData?.files?.filter((f: FileEntry) => !f.isDirectory) || [];
  const folders = hierarchyData?.files?.filter((f: FileEntry) => f.isDirectory) || [];

  // Build tree structure for left pane
  const buildTree = (): FolderNode => {
    const root: FolderNode = {
      name: 'Root',
      path: '/',
      directoryId: '/',  // Root directory uses its path as ID
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

    // Sort folders for consistent display
    const sortedFolders = [...allFolders].sort();

    for (const folderPath of sortedFolders) {
      const node: FolderNode = {
        name: folderPath.split('/').filter(Boolean).pop() || folderPath,
        path: folderPath,
        directoryId: folderIdMap.get(folderPath) || folderPath,  // Use API directoryId or fall back to path
        children: [],
        expanded: expandedFolders.has(folderPath)
      };
      nodeMap.set(folderPath, node);

      // Find parent
      const parentPath = folderPath.substring(0, folderPath.lastIndexOf('/')) || '/';
      const parent = nodeMap.get(parentPath);
      if (parent) {
        parent.children.push(node);
      }
    }

    return root;
  };

  const tree = buildTree();

  // Delete selected files
  const deleteFilesMutation = useMutation({
    mutationFn: async (paths: string[]) => {
      const response = await fetch(
        `${getApiBaseUrl()}/${endpoint === 'models' ? '3d-models' : 'gcode-files'}`,
        {
          method: 'DELETE',
          headers: {
            'Content-Type': 'application/json',
            ...getAuthHeaders()
          },
          body: JSON.stringify({ modelPaths: paths })
        }
      );
      if (!response.ok) throw new Error('Failed to delete files');
      return response.json();
    },
    onSuccess: () => {
      toast.success('Files deleted successfully');
      queryClient.invalidateQueries({ queryKey: [`${endpoint}-hierarchy`] });
      queryClient.invalidateQueries({ queryKey: [`${endpoint}-all-folders`] });
      onFileDelete?.(files.map((f: FileEntry) => f.path));
    },
    onError: () => {
      toast.error('Failed to delete files');
    }
  });

  const handleDeleteFile = (filePath: string) => {
    if (confirm(`Delete ${filePath}?`)) {
      deleteFilesMutation.mutate([filePath]);
    }
  };

  const handleSelectFile = (filePath: string, selected: boolean) => {
    setSelectedFiles(prev =>
      selected ? [...prev, filePath] : prev.filter(f => f !== filePath)
    );
  };

  const handleSelectAll = (selected: boolean) => {
    if (selected) {
      setSelectedFiles(files.map((f: FileEntry) => f.path));
    } else {
      setSelectedFiles([]);
    }
  };

  // Validate folder name - alphanumeric, spaces, hyphens, underscores, periods
  const validateFolderName = (name: string): string | null => {
    const trimmed = name.trim();
    
    if (!trimmed) {
      return 'Folder name cannot be empty';
    }
    
    if (trimmed.length > 255) {
      return 'Folder name is too long';
    }
    
    // Allow alphanumeric, spaces, hyphens, underscores, and periods
    if (!/^[\w\s.-]+$/.test(trimmed)) {
      return 'Folder name can only contain letters, numbers, spaces, hyphens, underscores, and periods';
    }
    
    // Check if folder already exists with this name
    const newPath = selectedFolder === '/' ? `/${trimmed}` : `${selectedFolder}/${trimmed}`;
    if (allFolders.includes(newPath)) {
      return 'A folder with this name already exists';
    }
    
    return null;
  };

  // Create folder mutation
  const createFolderMutation = useMutation({
    mutationFn: async ({ folderName }: { folderName: string }) => {
      const newPath = selectedFolder === '/' ? `/${folderName}` : `${selectedFolder}/${folderName}`;
      
      const response = await fetch(
        `${getApiBaseUrl()}/${endpoint === 'models' ? '3d-models' : 'gcode-files'}/folder`,
        {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            ...getAuthHeaders()
          },
          body: JSON.stringify({ path: newPath })
        }
      );
      
      if (!response.ok) {
        const error = await response.json().catch(() => ({message: 'Unknown error'}));
        throw new Error((error as {message: string}).message || 'Failed to create folder');
      }
      
      return response.json();
    },
    onSuccess: () => {
      toast.success('Folder created successfully');
      setIsCreatingFolder(false);
      setNewFolderName('');
      setFolderNameError(null);
      // Invalidate and refetch immediately
      queryClient.invalidateQueries({ queryKey: [`${endpoint}-hierarchy`], refetchType: 'all' });
      queryClient.invalidateQueries({ queryKey: [`${endpoint}-all-folders`], refetchType: 'all' });
    },
    onError: (error: Error) => {
      toast.error(error.message);
      setFolderNameError(error.message);
    }
  });

  // Move files mutation
  const moveFilesMutation = useMutation({
    mutationFn: async ({ files: modelIds, targetDirectoryId }: { files: string[]; targetDirectoryId: string }) => {
      const response = await fetch(
        `${getApiBaseUrl()}/${endpoint === 'models' ? '3d-models' : 'gcode-files'}/move`,
        {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            ...getAuthHeaders()
          },
          body: JSON.stringify({ modelIds, targetDirectoryId })
        }
      );
      if (!response.ok) throw new Error('Failed to move files');
      const data = await response.json();
      // Check the success field in the response, not just HTTP status
      if (!data.success) {
        throw new Error(data.message || 'Failed to move files');
      }
      return data;
    },
    onSuccess: (data) => {
      toast.success(data.message || 'Files moved successfully');
      setSelectedFiles([]);
      setDragOverPath(null);
      queryClient.invalidateQueries({ queryKey: [`${endpoint}-hierarchy`] });
    },
    onError: (error) => {
      const message = error instanceof Error ? error.message : 'Failed to move files';
      toast.error(message);

      setDragOverPath(null);
    }
  });

  const handleCreateFolder = () => {
    const error = validateFolderName(newFolderName);
    
    if (error) {
      setFolderNameError(error);
      return;
    }
    
    createFolderMutation.mutate({ folderName: newFolderName.trim() });
  };

  const handleCancelCreateFolder = () => {
    setIsCreatingFolder(false);
    setNewFolderName('');
    setFolderNameError(null);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      handleCreateFolder();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      handleCancelCreateFolder();
    }
  };

  const handleToggleFolderExpand = (folderPath: string) => {
    const newExpanded = new Set(expandedFolders);
    if (newExpanded.has(folderPath)) {
      newExpanded.delete(folderPath);
    } else {
      newExpanded.add(folderPath);
    }
    setExpandedFolders(newExpanded);
  };

  const handleDragOver = (e: React.DragEvent, folderPath: string) => {
    e.preventDefault();
    e.stopPropagation();
    setDragOverPath(folderPath);
    e.dataTransfer.effectAllowed = 'move';
  };

  const handleDragLeave = () => {
    setDragOverPath(null);
  };

  const handleDropOnFolder = (e: React.DragEvent, folderPath: string, directoryId: string) => {
    e.preventDefault();
    e.stopPropagation();
    setDragOverPath(null);

    // Get the files from dataTransfer
    const data = e.dataTransfer.getData('application/json');
    if (!data) {
      toast.error('No files to move');
      return;
    }

    try {
      const filesToMove = JSON.parse(data) as string[];
      if (filesToMove.length === 0) {
        toast.error('Please select files to move');
        return;
      }

      moveFilesMutation.mutate({ files: filesToMove, targetDirectoryId: directoryId });
    } catch (error) {
      toast.error('Failed to parse files for move');
    }
  };

  // Render tree nodes
  const renderTreeNode = (node: FolderNode, level: number): React.ReactNode => {
    const isRoot = node.path === '/';
    const hasChildren = node.children.length > 0;

    return (
      <div key={node.path}>
        <div
          className={`flex items-center gap-2 px-2 py-1.5 cursor-pointer hover:bg-pf-bg-2 rounded transition-colors ${
            selectedFolder === node.path ? 'bg-pf-accent bg-opacity-40 border-l-2 border-pf-accent text-white font-semibold' : ''
          } ${
            dragOverPath === node.path 
              ? 'bg-pf-primary bg-opacity-15 border-l-4 border-pf-primary' 
              : ''
          }`}
          style={{ paddingLeft: `${isRoot ? 8 : level * 16 + 8}px` }}
          onClick={() => setSelectedFolder(node.path)}
          onDragOver={(e) => handleDragOver(e, node.path)}
          onDragLeave={handleDragLeave}
          onDrop={(e) => handleDropOnFolder(e, node.path, node.directoryId)}
        >
          {hasChildren && (
            <Button
              onClick={(e) => {
                e.stopPropagation();
                handleToggleFolderExpand(node.path);
              }}
              variant="subtle"
              size="sm"
              className="!p-0 !bg-transparent !border-0 flex-shrink-0 text-transparent hover:text-pf-text-secondary"
              aria-hidden="true"
            >
              <ChevronRightIcon
                className={`w-4 h-4 transition-transform ${node.expanded ? 'rotate-90' : ''}`}
              />
            </Button>
          )}
          {!hasChildren && <div className="w-4" />}
          <FolderIcon className={`w-4 h-4 flex-shrink-0 ${
            dragOverPath === node.path ? 'text-pf-primary' : 'text-pf-text-secondary'
          }`} />
          <span className="text-sm text-pf-text truncate">{node.name}</span>
        </div>

        {node.expanded &&
          node.children.map((child) => renderTreeNode(child, level + 1))}
      </div>
    );
  };

  return (
    <div className="flex h-full gap-4 bg-pf-bg rounded-lg border border-pf-border">
      {/* Left Pane: Folder Tree */}
      <div className="w-64 flex-shrink-0 border-r border-pf-border overflow-y-auto">
        <div className="p-3 sticky top-0 bg-pf-bg border-b border-pf-border">
          <h3 className="text-sm font-semibold text-pf-text">Folders</h3>
        </div>
        <div className="p-2">{renderTreeNode(tree, 0)}</div>
      </div>

      {/* Right Pane: Files */}
      <div className="flex-1 flex flex-col overflow-hidden">
        <div className="p-3 border-b border-pf-border sticky top-0 bg-pf-bg">
          <div className="flex items-center justify-between">
            <h3 className="text-sm font-semibold text-pf-text">
              {selectedFolder === '/' ? '/' : selectedFolder}
            </h3>
            <div className="flex items-center gap-3">
              <Button
                onClick={() => setIsCreatingFolder(true)}
                disabled={isCreatingFolder}
                variant="secondary"
                size="sm"
                title="Create new folder"
              >
                <FolderPlusIcon className="w-4 h-4 mr-1" />
                New Folder
              </Button>
              <span className="text-xs text-pf-text-secondary">{files.length} files</span>
            </div>
          </div>
        </div>

        <div className="flex-1 overflow-y-auto">
          {/* Inline folder creation */}
          {isCreatingFolder && (
            <div className="border-b border-pf-border bg-pf-bg-2 p-4">
              <div className="space-y-2">
                <label className="block text-xs font-medium text-pf-text">New Folder Name</label>
                <input
                  ref={inputRef}
                  type="text"
                  value={newFolderName}
                  onChange={(e) => {
                    setNewFolderName(e.target.value);
                    setFolderNameError(null);
                  }}
                  onKeyDown={handleKeyDown}
                  placeholder="Enter folder name..."
                  className="w-full px-3 py-2 bg-pf-bg border border-pf-border rounded text-sm text-pf-text placeholder-pf-text-secondary focus:outline-none focus:ring-1 focus:ring-pf-accent"
                />
                {folderNameError && (
                  <p className="text-xs text-pf-error">{folderNameError}</p>
                )}
                <div className="flex gap-2 pt-2">
                  <Button
                    onClick={handleCreateFolder}
                    disabled={createFolderMutation.isPending}
                    size="sm"
                  >
                    {createFolderMutation.isPending ? 'Creating...' : 'Create'}
                  </Button>
                  <Button
                    onClick={handleCancelCreateFolder}
                    disabled={createFolderMutation.isPending}
                    variant="secondary"
                    size="sm"
                  >
                    Cancel
                  </Button>
                </div>
              </div>
            </div>
          )}

          {files.length === 0 && !isCreatingFolder ? (
            <div className="flex items-center justify-center h-full text-pf-text-secondary">
              <p>No files in this folder</p>
            </div>
          ) : files.length === 0 && isCreatingFolder ? null : (
            <table className="w-full text-sm">
              <thead className="sticky top-0 bg-pf-bg-2 border-b border-pf-border">
                <tr>
                  <th className="px-4 py-2 text-left w-8">
                    {files.length > 0 && (
                      <Checkbox
                        checked={selectedFiles.length === files.length && files.length > 0}
                        onChange={(e) => handleSelectAll(e.currentTarget.checked)}
                      />
                    )}
                  </th>
                  <th className="px-4 py-2 text-left font-semibold text-pf-text">Thumbnail / Name</th>
                  <th className="px-4 py-2 text-right font-semibold text-pf-text w-24">Size</th>
                  <th className="px-4 py-2 text-left font-semibold text-pf-text w-40">Modified</th>
                  <th className="px-4 py-2 text-center font-semibold text-pf-text w-12">Action</th>
                </tr>
              </thead>
              <tbody>
                {files.map((file: FileEntry) => (
                  <tr
                    key={file.path}
                    className={`border-b border-pf-border hover:bg-pf-bg-2 transition-colors cursor-grab active:cursor-grabbing ${
                      selectedFiles.includes(file.path) ? 'bg-pf-primary bg-opacity-10' : ''
                    }`}
                    draggable={true}
                    onDragStart={(e) => {
                      setIsDragging(true);
                      // If this file isn't selected, move just this file
                      // Otherwise move all selected files
                      const filesToMove = selectedFiles.includes(file.path) 
                        ? selectedFiles 
                        : [file.path];
                      
                      // Store files to move in dataTransfer for use in drop handler
                      // Note: filesToMove already contains full paths from the API
                      e.dataTransfer!.setData('application/json', JSON.stringify(filesToMove));
                      
                      // Set custom drag image using thumbnail if available
                      if (file.thumbnailUrl) {
                        const img = new Image();
                        img.src = file.thumbnailUrl;
                        const canvas = document.createElement('canvas');
                        canvas.width = 80;
                        canvas.height = 80;
                        const ctx = canvas.getContext('2d');
                        if (ctx) {
                          ctx.fillStyle = 'rgba(0,0,0,0.7)';
                          ctx.fillRect(0, 0, 80, 80);
                          ctx.drawImage(img, 0, 0, 80, 80);
                          e.dataTransfer!.setDragImage(canvas, 40, 40);
                        }
                      }
                      e.dataTransfer!.effectAllowed = 'move';
                    }}
                    onDragEnd={() => setIsDragging(false)}
                  >
                    <td className="px-4 py-3">
                      <Checkbox
                        checked={selectedFiles.includes(file.path)}
                        onChange={(e) => handleSelectFile(file.path, e.currentTarget.checked)}
                      />
                    </td>
                    <td className="px-4 py-3 flex items-center gap-3">
                      {/* Thumbnail Column */}
                      <div className="relative flex-shrink-0">
                        {file.thumbnailUrl ? (
                          <div
                            className={`w-12 h-12 rounded border-2 overflow-hidden transition-all cursor-grab active:cursor-grabbing ${
                              selectedFiles.includes(file.path)
                                ? 'border-pf-primary shadow-md'
                                : 'border-pf-border hover:border-pf-primary'
                            }`}
                            draggable
                            onDragStart={(e) => {
                              e.stopPropagation();
                              setIsDragging(true);
                              e.dataTransfer!.effectAllowed = 'move';
                              // If this file isn't selected, move just this file
                              // Otherwise move all selected files
                              const filesToMove = selectedFiles.includes(file.path)
                                ? selectedFiles
                                : [file.path];
                              
                              // Store files to move in dataTransfer
                              e.dataTransfer!.setData('application/json', JSON.stringify(filesToMove));
                              
                              // Create drag image from thumbnail
                              const img = document.createElement('img');
                              img.src = file.thumbnailUrl!;
                              img.onload = () => {
                                const canvas = document.createElement('canvas');
                                canvas.width = 60;
                                canvas.height = 60;
                                const ctx = canvas.getContext('2d');
                                if (ctx) {
                                  ctx.fillStyle = 'rgba(0,0,0,0.8)';
                                  ctx.fillRect(0, 0, 60, 60);
                                  ctx.drawImage(img, 0, 0, 60, 60);
                                  ctx.strokeStyle = 'white';
                                  ctx.lineWidth = 2;
                                  ctx.strokeRect(0, 0, 60, 60);
                                  e.dataTransfer!.setDragImage(canvas, 30, 30);
                                }
                              };
                            }}
                            onDragEnd={() => setIsDragging(false)}
                          >
                            <img
                              src={file.thumbnailUrl}
                              alt={file.name}
                              className="w-full h-full object-cover"
                            />
                            {selectedFiles.length > 1 && selectedFiles.includes(file.path) && (
                              <div className="absolute -top-1 -right-1 bg-pf-primary text-white rounded-full w-5 h-5 flex items-center justify-center text-xs font-bold">
                                {selectedFiles.indexOf(file.path) + 1}
                              </div>
                            )}
                          </div>
                        ) : (
                          <div className="w-12 h-12 rounded border-2 border-pf-border bg-pf-bg-2 flex items-center justify-center">
                            <DocumentIcon className="w-6 h-6 text-pf-text-secondary" />
                          </div>
                        )}
                      </div>
                      <div>
                        <div className="font-medium text-pf-text-primary">{file.name}</div>
                        <div className="text-xs text-pf-text-tertiary">{formatBytes(file.size)}</div>
                      </div>
                    </td>
                    <td className="px-4 py-3 text-right text-pf-text-secondary">
                      {formatBytes(file.size)}
                    </td>
                    <td className="px-4 py-3 text-pf-text-secondary">
                      {formatDate(file.modifiedAt)}
                    </td>
                    <td className="px-4 py-3 text-center">
                      <Button
                        onClick={() => handleDeleteFile(file.path)}
                        variant="danger"
                        size="sm"
                        className="!p-0 !bg-transparent !border-0 text-pf-error hover:text-pf-error hover:opacity-70"
                        title="Delete"
                      >
                        <TrashIcon className="w-4 h-4" />
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>
    </div>
  );
};
