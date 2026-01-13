import React, { useState, useRef, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { ChevronRightIcon, DocumentIcon, TrashIcon, FolderPlusIcon } from '@heroicons/react/24/outline';
import { Button, Checkbox } from '@/common/components/ui';
import { Select } from '@/common/components/ui/Select';
import { SelectableRow } from '@/common/components/Table/SelectableRow';
import { toast } from 'sonner';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';
import { TreeView, type TreeNode } from './TreeView';
import type { PrinterModelDto } from '@/types/api';

export interface FileEntry {
  path: string;
  fileName: string;  // GUID-based filename for internal storage
  name?: string;  // Original filename uploaded by user (for display)
  size: number;
  modifiedAt: string;
  isDirectory: boolean;
  thumbnailPath?: string;  // Changed from 'thumbnailUrl' to match API response
  modelId?: string;  // File ID (GUID) for 3D models
  gcodeFileId?: string;  // File ID (GUID) for gcode files
  directoryId?: string;  // Directory ID (virtual path) for efficient directory lookups
  targetModelName?: string;  // Printer model (for gcode files)
  requiredMaterial?: string;  // Required filament type (for gcode files)
  extractedNozzleDiameter?: number;  // Extracted nozzle size in mm
  extractedMaterial?: string;  // Extracted material from G-code
  extractedPrinterModel?: string;  // Extracted printer model from G-code
}

interface ExplorerFileBrowserProps {
  endpoint: 'models' | 'gcode';
  onFileSelect?: (file: FileEntry) => void;
  onFileDelete?: (paths: string[]) => void;
  sortBy?: string;
  sortOrder?: 'asc' | 'desc';
  onSort?: (sortBy: string) => void;
}

const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

const formatDate = (dateString: string): string => {
  if (!dateString) return '—';
  return new Date(dateString).toLocaleDateString();
};

const renderSortIndicator = (column: string, sortBy?: string, sortOrder?: 'asc' | 'desc'): string | null => {
  if (sortBy !== column) return null;
  return sortOrder === 'asc' ? ' ▲' : ' ▼';
};

const getSortableHeaderClass = (onSort?: (sortBy: string) => void): string => {
  return onSort ? 'cursor-pointer hover:text-pf-accent transition-colors' : '';
};

export const ExplorerFileBrowser: React.FC<ExplorerFileBrowserProps> = ({
  endpoint,
  onFileDelete,
  sortBy,
  sortOrder,
  onSort
}) => {
  const [selectedFolder, setSelectedFolder] = useState('/');
  const [expandedFolders, setExpandedFolders] = useState(new Set(['/']));
  const [isCreatingFolder, setIsCreatingFolder] = useState(false);
  const [newFolderName, setNewFolderName] = useState('');
  const [folderNameError, setFolderNameError] = useState<string | null>(null);
  const [selectedFiles, setSelectedFiles] = useState<string[]>([]);
  const [isTreeCollapsed, setIsTreeCollapsed] = useState(false);
  // Delete flow handled inline via mutation; no dedicated fileToDelete state needed
  const [dragOverPath, setDragOverPath] = useState<string | null>(null);
  const [selectedPrinterModel, setSelectedPrinterModel] = useState<string>('all');
  const inputRef = useRef<HTMLInputElement>(null);
  const queryClient = useQueryClient();

  // Focus input when creation mode starts
  useEffect(() => {
    if (isCreatingFolder && inputRef.current) {
      inputRef.current.focus();
    }
  }, [isCreatingFolder]);

  // Fetch printer models for filter (only for gcode endpoint)
  const { data: printerModels = [] } = useQuery<PrinterModelDto[]>({
    queryKey: ['printer-models'],
    queryFn: async () => {
      if (endpoint !== 'gcode') return [];
      const response = await fetch(
        `${getApiBaseUrl()}/catalog/printer-models`,
        { headers: getAuthHeaders() }
      );
      if (!response.ok) throw new Error('Failed to fetch printer models');
      return response.json();
    },
    enabled: endpoint === 'gcode'
  });

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

  // Separate files and folders, then filter by printer model
  const allFiles = hierarchyData?.files?.filter((f: FileEntry) => !f.isDirectory) || [];
  const files = selectedPrinterModel === 'all'
    ? allFiles
    : allFiles.filter((f: FileEntry) => f.targetModelName === selectedPrinterModel);

  // Build tree structure for left pane - returns TreeNode[]
  const buildTree = (): TreeNode[] => {
    // Build a map of folder paths to their directory IDs from the API response
    const folderIdMap = new Map<string, string>();
    (hierarchyData?.files || []).forEach((f: FileEntry) => {
      if (f.isDirectory && f.directoryId) {
        folderIdMap.set(f.path, f.directoryId);
      }
    });

    // Create node map for building hierarchy
    const nodeMap = new Map<string, TreeNode>();
    
    // Sort folders for consistent display
    const sortedFolders = ['/', ...allFolders].sort();

    for (const folderPath of sortedFolders) {
      if (nodeMap.has(folderPath)) continue; // Skip root if already added
      
      const node: TreeNode = {
        name: folderPath === '/' ? 'Root' : (folderPath.split('/').filter(Boolean).pop() || folderPath),
        path: folderPath,
        isDirectory: true,
        children: [],
        directoryId: folderIdMap.get(folderPath) || folderPath,
      };
      nodeMap.set(folderPath, node);
    }

    // Build parent-child relationships
    for (const folderPath of sortedFolders) {
      if (folderPath === '/') continue; // Skip root for parent lookup
      
      const node = nodeMap.get(folderPath)!;
      const parentPath = folderPath.substring(0, folderPath.lastIndexOf('/')) || '/';
      const parent = nodeMap.get(parentPath);
      
      if (parent) {
        parent.children!.push(node);
      }
    }

    // Return only root node wrapped in array
    const root = nodeMap.get('/');
    return root ? [root] : [];
  };

  const treeNodes = buildTree();

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
    // Immediate confirmation flow
    const confirmed = window.confirm('Delete this file? This action cannot be undone.');
    if (!confirmed) return;
    deleteFilesMutation.mutate([filePath]);
  };

  // Confirmation flow handled inline where delete is triggered; handler removed to avoid unused-vars

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
        // Try to parse as JSON first, then fall back to text
        let errorMessage = 'Failed to create folder';
        try {
          const contentType = response.headers.get('content-type');
          if (contentType && contentType.includes('application/json')) {
            const error = await response.json();
            errorMessage = error.message || errorMessage;
          } else {
            // Plain text response
            errorMessage = await response.text();
          }
        } catch {
          // If parsing fails, use default message
        }
        throw new Error(errorMessage);
      }
      
      return response.json();
    },
    onSuccess: () => {
      toast.success('Folder created successfully');
      setIsCreatingFolder(false);
      setNewFolderName('');
      setFolderNameError(null);
      // Force immediate refetch of both queries
      queryClient.refetchQueries({ queryKey: [`${endpoint}-hierarchy`] });
      queryClient.refetchQueries({ queryKey: [`${endpoint}-all-folders`] });
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
    } catch {
      toast.error('Failed to parse files for move');
    }
  };

  return (
    /* eslint-disable local/pf-no-unguarded-console */
    <div className="flex h-full gap-0 bg-pf-bg rounded-lg border border-pf-border">
      {/* Left Pane: Collapsible Folder Tree */}
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
        {!isTreeCollapsed && (
          <div className="p-2">
            <TreeView
              nodes={treeNodes}
              onSelect={() => {}} // Not used for folders
              onNavigate={() => {}} // Not used, we use onFolderClick instead
              currentPath={selectedFolder}
              selectedFiles={[]}
              onSelectFile={() => {}} // Not used for folders
              onFolderClick={setSelectedFolder}
              onFolderToggleExpand={handleToggleFolderExpand}
              expandedFolders={expandedFolders}
              dragOverPath={dragOverPath}
              onDragOver={handleDragOver}
              onDragLeave={handleDragLeave}
              onDrop={handleDropOnFolder}
            />
          </div>
        )}
      </div>

      {/* Right Pane: Files */}
      <div className="flex-1 flex flex-col overflow-hidden">
        <div className="p-3 border-b border-pf-border sticky top-0 bg-pf-bg">
          <div className="flex items-center justify-between">
            <h3 className="text-sm font-semibold text-pf-text">
              {selectedFolder === '/' ? '/' : selectedFolder}
            </h3>
            <div className="flex items-center gap-3">
              {endpoint === 'gcode' && printerModels.length > 0 && (
                <Select
                  value={selectedPrinterModel}
                  onChange={(e) => setSelectedPrinterModel(e.target.value)}
                  aria-label="Filter by printer model"
                >
                  <option value="all">All Models ({allFiles.length})</option>
                  {printerModels.map((model) => {
                    const count = allFiles.filter((f: FileEntry) => f.targetModelName === model.name).length;
                    return (
                      <option key={model.id} value={model.name}>
                        {model.name} ({count})
                      </option>
                    );
                  })}
                </Select>
              )}
              <Button
                onClick={() => setIsCreatingFolder(true)}
                disabled={isCreatingFolder}
                variant="subtle"
                size="sm"
                title="Create new folder"
              >
                <FolderPlusIcon className="w-4 h-4 mr-1" />
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
                  <th className={`px-4 py-2 text-left font-semibold text-pf-text ${getSortableHeaderClass(onSort)}`} onClick={() => onSort?.('name')}>
                    Thumbnail / Name{renderSortIndicator('name', sortBy, sortOrder)}
                  </th>
                  <th className={`px-4 py-2 text-left font-semibold text-pf-text w-24 ${getSortableHeaderClass(onSort)}`} onClick={() => onSort?.('size')}>
                    Size{renderSortIndicator('size', sortBy, sortOrder)}
                  </th>
                  <th className="px-4 py-2 text-left font-semibold text-pf-text w-24">Nozzle</th>
                  <th className="px-4 py-2 text-left font-semibold text-pf-text w-32">Material</th>
                  <th className="px-4 py-2 text-left font-semibold text-pf-text w-32">Printer Model</th>
                  <th className={`px-4 py-2 text-left font-semibold text-pf-text w-40 ${getSortableHeaderClass(onSort)}`} onClick={() => onSort?.('date')}>
                    Modified{renderSortIndicator('date', sortBy, sortOrder)}
                  </th>
                  <th className="px-4 py-2 text-center font-semibold text-pf-text w-12">Action</th>
                </tr>
              </thead>
              <tbody>
                {files.map((file: FileEntry) => (
                  <SelectableRow
                    key={file.path}
                    className="border-b border-pf-border cursor-grab active:cursor-grabbing"
                    isSelected={selectedFiles.includes(file.path)}
                    draggable={true}
                    onDragStart={(e) => {
                      // Get file ID (GUID) - use gcodeFileId for gcode, modelId for 3D models
                      const fileId = endpoint === 'gcode' ? file.gcodeFileId : file.modelId;
                      if (!fileId) {
                        toast.error('Cannot move file: missing file ID');
                        e.preventDefault();
                        return;
                      }
                      
                      // If this file isn't selected, move just this file
                      // Otherwise move all selected files by their IDs
                      const filesToMove = selectedFiles.includes(file.path) 
                        ? files.filter((f: FileEntry) => selectedFiles.includes(f.path)).map((f: FileEntry) => endpoint === 'gcode' ? f.gcodeFileId : f.modelId).filter(Boolean) as string[]
                        : [fileId];
                      
                      // Store file IDs (GUIDs) to move in dataTransfer for use in drop handler
                      e.dataTransfer!.setData('application/json', JSON.stringify(filesToMove));
                      
                      // Set custom drag image using thumbnail if available
                      if (file.thumbnailPath) {
                        const img = new Image();
                        img.src = file.thumbnailPath;
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
                        {file.thumbnailPath ? (
                          <div
                            className={`w-10 h-10 rounded border-2 overflow-hidden transition-all cursor-grab active:cursor-grabbing ${
                              selectedFiles.includes(file.path)
                                ? 'border-pf-primary shadow-md'
                                : 'border-pf-border hover:border-pf-primary'
                            }`}
                            draggable
                            onDragStart={(e) => {
                              e.stopPropagation();
                              e.dataTransfer!.effectAllowed = 'move';
                              
                              // Get file ID (GUID) - use gcodeFileId for gcode, modelId for 3D models
                              const fileId = endpoint === 'gcode' ? file.gcodeFileId : file.modelId;
                              if (!fileId) {
                                toast.error('Cannot move file: missing file ID');
                                e.preventDefault();
                                return;
                              }
                              
                              // If this file isn't selected, move just this file
                              // Otherwise move all selected files by their IDs
                              const filesToMove = selectedFiles.includes(file.path)
                                ? files.filter((f: FileEntry) => selectedFiles.includes(f.path)).map((f: FileEntry) => endpoint === 'gcode' ? f.gcodeFileId : f.modelId).filter(Boolean) as string[]
                                : [fileId];
                              
                              // Store files to move in dataTransfer
                              e.dataTransfer!.setData('application/json', JSON.stringify(filesToMove));
                              
                              // Create drag image from thumbnail
                              const img = document.createElement('img');
                              img.src = file.thumbnailPath!;
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
                          >
                            <img
                              src={file.thumbnailPath}
                              alt={file.fileName}
                              className="w-full h-full object-cover"
                              onError={(e) => {
                                e.currentTarget.src = 'data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iNDgiIGhlaWdodD0iNDgiIHZpZXdCb3g9IjAgMCA0OCA0OCIgZmlsbD0ibm9uZSIgeG1sbnM9Imh0dHA6Ly93d3cudzMub3JnLzIwMDAvc3ZnIj48cmVjdCB3aWR0aD0iNDgiIGhlaWdodD0iNDgiIGZpbGw9IiNFNUU3RUIiLz48cmVjdCB4PSI4IiB5PSI4IiB3aWR0aD0iMzIiIGhlaWdodD0iMzIiIHN0cm9rZT0iIzk1OTdiMCIgc3Ryb2tlLXdpZHRoPSIyIiBmaWxsPSJub25lIi8+PGNpcmNsZSBjeD0iMjQiIGN5PSIyNCIgcj0iMiIgZmlsbD0iIzk1OTdiMCIvPjwvc3ZnPg=='
                              }}
                            />
                            {selectedFiles.length > 1 && selectedFiles.includes(file.path) && (
                              <div className="absolute -top-1 -right-1 bg-pf-primary text-white rounded-full w-5 h-5 flex items-center justify-center text-xs font-bold">
                                {selectedFiles.indexOf(file.path) + 1}
                              </div>
                            )}
                          </div>
                        ) : (
                          <div className="w-10 h-10 rounded border-2 border-pf-border bg-pf-bg-2 flex items-center justify-center">
                            <DocumentIcon className="w-5 h-5 text-pf-text-secondary" />
                          </div>
                        )}
                      </div>
                      <div>
                        <div className="font-medium text-pf-text-primary">{file.name}</div>
                        {file.targetModelName && (
                          <div className="text-xs text-pf-text-tertiary">{file.targetModelName}</div>
                        )}
                      </div>
                    </td>
                    <td className="px-4 py-3 text-pf-text-secondary">
                      {formatBytes(file.size)}
                    </td>
                    <td className="px-4 py-3 text-pf-text-secondary">
                      {file.isDirectory ? '-' : file.extractedNozzleDiameter ? `${file.extractedNozzleDiameter}mm` : '-'}
                    </td>
                    <td className="px-4 py-3 text-pf-text-secondary">
                      {file.isDirectory ? '-' : file.requiredMaterial || file.extractedMaterial || '-'}
                    </td>
                    <td className="px-4 py-3 text-pf-text-secondary">
                      {file.isDirectory ? '-' : file.extractedPrinterModel || file.targetModelName || '-'}
                    </td>
                    <td className="px-4 py-3 text-pf-text-secondary">
                      {file.modifiedAt ? formatDate(file.modifiedAt) : '—'}
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
                  </SelectableRow>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>
    </div>
  );
};
