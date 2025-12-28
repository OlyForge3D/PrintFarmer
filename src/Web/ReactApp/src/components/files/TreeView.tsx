import React, { useState } from 'react';
import { ChevronRightIcon, FolderIcon, DocumentIcon } from '@heroicons/react/24/outline';
import { Button } from '@/components/ui';

export interface TreeNode {
  path: string;
  name: string;
  isDirectory: boolean;
  children?: TreeNode[];
  size?: number;
  modifiedAt?: string;
  thumbnailUrl?: string;
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
}

const TreeItem: React.FC<{
  node: TreeNode;
  level: number;
  onSelect: (node: TreeNode) => void;
  onNavigate: (path: string) => void;
  currentPath: string;
  selectedFiles: string[];
  onSelectFile: (path: string, selected: boolean) => void;
}> = ({ node, level, onSelect, onNavigate, currentPath, selectedFiles, onSelectFile }) => {
  const [expanded, setExpanded] = useState(false);
  const isSelected = selectedFiles.includes(node.path);

  const handleClick = () => {
    if (node.isDirectory) {
      onNavigate(node.path);
      setExpanded(true);
    } else {
      onSelect(node);
    }
  };

  const handleCheckboxChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    e.stopPropagation();
    onSelectFile(node.path, e.target.checked);
  };

  return (
    <div key={node.path}>
      <div
        className={`flex items-center gap-2 px-2 py-1.5 cursor-pointer hover:bg-pf-bg-2 rounded transition-colors ${
          currentPath === node.path ? 'bg-pf-accent bg-opacity-40 border-l-2 border-pf-accent text-white font-semibold' : ''
        }`}
        style={{ paddingLeft: `${level * 16 + 8}px` }}
      >
        {node.isDirectory && node.children?.length ? (
          <Button
            onClick={(e) => {
              e.stopPropagation();
              setExpanded(!expanded);
            }}
            variant="subtle"
            size="sm"
            className="!p-0 !bg-transparent !border-0 flex-shrink-0 text-transparent hover:text-pf-text-secondary transition-colors"
            aria-hidden="true"
          >
            <ChevronRightIcon
              className={`w-4 h-4 transition-transform ${expanded ? 'rotate-90' : ''}`}
            />
          </Button>
        ) : node.isDirectory ? (
          <div className="w-4 h-4 flex-shrink-0" />
        ) : (
          <div className="w-4 h-4 flex-shrink-0" />
        )}

        {!node.isDirectory && (
          <input
            type="checkbox"
            checked={isSelected}
            onChange={handleCheckboxChange}
            className="flex-shrink-0 w-4 h-4"
            onClick={(e) => e.stopPropagation()}
          />
        )}

        {node.isDirectory ? (
          <FolderIcon className="w-4 h-4 text-pf-text-secondary flex-shrink-0" />
        ) : (
          <DocumentIcon className="w-4 h-4 text-pf-text-tertiary flex-shrink-0" />
        )}

        <span
          onClick={handleClick}
          className="flex-1 text-sm text-pf-text-primary truncate"
          title={node.name}
        >
          {node.name}
        </span>

        {node.size && !node.isDirectory && (
          <span className="text-xs text-pf-text-tertiary flex-shrink-0">
            {formatBytes(node.size)}
          </span>
        )}
      </div>

      {expanded && node.children?.length ? (
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
          />
        ))
      )}
    </div>
  );
};
