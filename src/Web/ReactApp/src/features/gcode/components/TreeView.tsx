import React, { useState } from 'react';
import { ChevronRightIcon, FolderIcon, DocumentIcon } from '@heroicons/react/24/outline';
import { Button, Checkbox } from '@/common/components/ui';

export interface TreeNode {
  path: string;
  name: string;
  isDirectory: boolean;
  children?: TreeNode[];
  size?: number;
  modifiedAt?: string;
  thumbnailUrl?: string;
  directoryId?: string; // For folder operations (drag-drop, move)
}

interface TreeViewProps {
  nodes: TreeNode[];
  onSelect: (node: TreeNode) => void;
  onNavigate: (path: string) => void;
  currentPath: string;
  selectedFiles: string[];
  onSelectFile: (path: string, selected: boolean) => void;
  onCreateFolder?: (name: string) => void;
  onDeleteFiles?: (paths: string[]) => void;
  isLoading?: boolean;
  // Folder-specific callbacks for drag-drop operations
  onFolderClick?: (folderPath: string) => void;
  onFolderToggleExpand?: (folderPath: string) => void;
  expandedFolders?: Set<string>;
  dragOverPath?: string | null;
  onDragOver?: (e: React.DragEvent, folderPath: string) => void;
  onDragLeave?: () => void;
  onDrop?: (e: React.DragEvent, folderPath: string, directoryId: string) => void;
}

const TreeItem: React.FC<{
  node: TreeNode;
  level: number;
  onSelect: (node: TreeNode) => void;
  onNavigate: (path: string) => void;
  currentPath: string;
  selectedFiles: string[];
  onSelectFile: (path: string, selected: boolean) => void;
  onFolderClick?: (folderPath: string) => void;
  onFolderToggleExpand?: (folderPath: string) => void;
  expandedFolders?: Set<string>;
  dragOverPath?: string | null;
  onDragOver?: (e: React.DragEvent, folderPath: string) => void;
  onDragLeave?: () => void;
  onDrop?: (e: React.DragEvent, folderPath: string, directoryId: string) => void;
}> = ({ 
  node, 
  level, 
  onSelect, 
  onNavigate, 
  currentPath, 
  selectedFiles, 
  onSelectFile,
  onFolderClick,
  onFolderToggleExpand,
  expandedFolders,
  dragOverPath,
  onDragOver,
  onDragLeave,
  onDrop
}) => {
  const [expanded, setExpanded] = useState(false);
  const isSelected = selectedFiles.includes(node.path);
  
  // Use expandedFolders prop if provided, otherwise use local state
  const isExpanded = expandedFolders ? expandedFolders.has(node.path) : expanded;

  const handleClick = () => {
    if (node.isDirectory) {
      if (onFolderClick) {
        onFolderClick(node.path);
      } else {
        onNavigate(node.path);
      }
      if (!expandedFolders) {
        setExpanded(true);
      }
    } else {
      onSelect(node);
    }
  };

  const handleCheckboxChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    e.stopPropagation();
    onSelectFile(node.path, e.target.checked);
  };

  const handleToggleExpand = (e: React.MouseEvent) => {
    e.stopPropagation();
    if (expandedFolders && onFolderToggleExpand) {
      onFolderToggleExpand(node.path);
    } else {
      setExpanded(!expanded);
    }
  };

  const isDragOver = dragOverPath === node.path;
  const isRoot = node.path === '/';

  return (
    <div key={node.path}>
      <div
        className={`flex items-center gap-2 px-2 py-1.5 cursor-pointer hover:bg-pf-bg-2 rounded transition-colors ${
          currentPath === node.path ? 'bg-pf-accent-bg border-l-2 border-pf-accent text-white font-semibold' : ''
        } ${
          isDragOver 
            ? 'bg-pf-primary/15 border-l-4 border-pf-primary' 
            : ''
        }`}
        style={{ paddingLeft: `${isRoot ? 8 : level * 16 + 8}px` }}
        onClick={handleClick}
        onDragOver={(e) => onDragOver?.(e, node.path)}
        onDragLeave={onDragLeave}
        onDrop={(e) => onDrop?.(e, node.path, node.directoryId || node.path)}
      >
        {node.isDirectory && node.children?.length ? (
          <Button
            onClick={handleToggleExpand}
            variant="subtle"
            size="sm"
            className="p-0! bg-transparent! border-0! shrink-0 text-transparent hover:text-pf-text-secondary transition-colors"
            aria-hidden="true"
          >
            <ChevronRightIcon
              className={`w-4 h-4 transition-transform ${isExpanded ? 'rotate-90' : ''}`}
            />
          </Button>
        ) : node.isDirectory ? (
          <div className="w-4 h-4 shrink-0" />
        ) : (
          <div className="w-4 h-4 shrink-0" />
        )}

        {!node.isDirectory && (
          <Checkbox
            checked={isSelected}
            onChange={handleCheckboxChange}
            className="shrink-0"
            onClick={(e) => e.stopPropagation()}
          />
        )}

        {node.isDirectory ? (
          <FolderIcon 
            className={`w-4 h-4 shrink-0 ${
              currentPath === node.path 
                ? 'text-white' 
                : isDragOver 
                  ? 'text-pf-primary'
                  : 'text-pf-text-secondary'
            }`}
          />
        ) : (
          <DocumentIcon className="w-4 h-4 text-pf-text-tertiary shrink-0" />
        )}

        <span
          className="flex-1 text-sm text-pf-text truncate"
          title={node.name}
        >
          {node.name}
        </span>

        {node.size && !node.isDirectory && (
          <span className="text-xs text-pf-text-tertiary shrink-0">
            {formatBytes(node.size)}
          </span>
        )}
      </div>

      {isExpanded && node.children?.length ? (
        <div>
          {node.children.map((child) => (
            <TreeItem
              key={child.path}
              node={child}
              level={level + 1}
              onSelect={onSelect}
              onNavigate={onNavigate}
              currentPath={currentPath}
              selectedFiles={selectedFiles}
              onSelectFile={onSelectFile}
              onFolderClick={onFolderClick}
              onFolderToggleExpand={onFolderToggleExpand}
              expandedFolders={expandedFolders}
              dragOverPath={dragOverPath}
              onDragOver={onDragOver}
              onDragLeave={onDragLeave}
              onDrop={onDrop}
            />
          ))}
        </div>
      ) : null}
    </div>
  );
};

function formatBytes(bytes: number): string {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
}

export const TreeView: React.FC<TreeViewProps> = ({
  nodes,
  onSelect,
  onNavigate,
  currentPath,
  selectedFiles,
  onSelectFile,
  isLoading = false,
  onFolderClick,
  onFolderToggleExpand,
  expandedFolders,
  dragOverPath,
  onDragOver,
  onDragLeave,
  onDrop,
}) => {
  return (
    <div className="flex flex-col gap-2 h-full max-h-96 overflow-y-auto">
      {isLoading ? (
        <div className="flex items-center justify-center h-32">
          <div className="pf-animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent" />
        </div>
      ) : nodes.length === 0 ? (
        <div className="text-center py-8">
          <p className="text-sm text-pf-text-tertiary">No files or folders</p>
        </div>
      ) : (
        nodes.map((node) => (
          <TreeItem
            key={node.path}
            node={node}
            level={0}
            onSelect={onSelect}
            onNavigate={onNavigate}
            currentPath={currentPath}
            selectedFiles={selectedFiles}
            onSelectFile={onSelectFile}
            onFolderClick={onFolderClick}
            onFolderToggleExpand={onFolderToggleExpand}
            expandedFolders={expandedFolders}
            dragOverPath={dragOverPath}
            onDragOver={onDragOver}
            onDragLeave={onDragLeave}
            onDrop={onDrop}
          />
        ))
      )}
    </div>
  );
};
