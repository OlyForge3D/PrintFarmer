import React from 'react';
import { formatDistanceToNow } from 'date-fns';
import { 
  FolderIcon,
  DocumentIcon,
  ArrowDownTrayIcon,
  TrashIcon
} from '@heroicons/react/24/outline';

import { GcodeFile } from '@/types/api';
import { useAuth } from '@/contexts/AuthContext';

interface FileRowProps {
  file: GcodeFile;
  selected: boolean;
  onSelect: (selected: boolean) => void;
  onDownload?: () => void;
  onDelete?: () => void;
  onNavigate?: () => void;
}

// Utility function to format bytes
const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 Bytes';
  const k = 1024;
  const sizes = ['Bytes', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

export const FileRow: React.FC<FileRowProps> = ({
  file,
  selected,
  onSelect,
  onDownload,
  onDelete,
  onNavigate
}) => {
  const { hasPermission } = useAuth();

  const handleRowClick = () => {
    if (file.isDirectory && onNavigate) {
      onNavigate();
    }
  };

  return (
    <div className="px-4 py-3 hover:bg-gray-50 flex items-center">
      <input
        type="checkbox"
        checked={selected}
        onChange={(e) => onSelect(e.target.checked)}
        className="mr-4"
        onClick={(e) => e.stopPropagation()}
      />
      
      <div 
        className="flex-1 grid grid-cols-12 gap-4 text-sm cursor-pointer"
        onClick={handleRowClick}
      >
        {/* Name */}
        <div className="col-span-5 flex items-center space-x-3">
          {file.isDirectory ? (
            <FolderIcon className="w-5 h-5 text-blue-500" />
          ) : (
            <DocumentIcon className="w-5 h-5 text-gray-400" />
          )}
          <div>
            <div className="font-medium text-gray-900 truncate">
              {file.name}
            </div>
            {file.isDirectory && (
              <div className="text-xs text-gray-500">Folder</div>
            )}
          </div>
        </div>
        
        {/* Size */}
        <div className="col-span-2 text-gray-600">
          {file.isDirectory ? '—' : formatBytes(file.size)}
        </div>
        
        {/* Modified */}
        <div className="col-span-3 text-gray-600">
          {formatDistanceToNow(file.modifiedAt, { addSuffix: true })}
        </div>
        
        {/* Actions */}
        <div className="col-span-2 flex items-center space-x-2">
          {!file.isDirectory && onDownload && hasPermission('gcode_harvest', 'read') && (
            <button
              onClick={(e) => {
                e.stopPropagation();
                onDownload();
              }}
              className="p-1 text-gray-400 hover:text-blue-600"
              title="Download file"
            >
              <ArrowDownTrayIcon className="w-4 h-4" />
            </button>
          )}
          
          {onDelete && hasPermission('gcode_harvest', 'delete') && (
            <button
              onClick={(e) => {
                e.stopPropagation();
                onDelete();
              }}
              className="p-1 text-gray-400 hover:text-red-600"
              title="Delete file"
            >
              <TrashIcon className="w-4 h-4" />
            </button>
          )}
        </div>
      </div>
    </div>
  );
};