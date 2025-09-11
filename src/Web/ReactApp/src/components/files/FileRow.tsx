import React, { useState } from 'react';
import { formatDistanceToNow } from 'date-fns';
import {
  FolderIcon,
  DocumentIcon,
  ArrowDownTrayIcon,
  TrashIcon,
  ClipboardIcon
} from '@heroicons/react/24/outline';

import { GcodeFile } from '@/types/api';
import { useAuth } from '@/contexts/AuthContext';
import { toast } from 'sonner';
import { useFileHash } from '@/hooks/useFileHash';

interface FileRowProps {
  file: GcodeFile;
  selected: boolean;
  onSelect: (selected: boolean) => void;
  onDownload?: () => void;
  onDelete?: () => void;
  onNavigate?: () => void;
  onRename?: (newName: string) => Promise<void>;
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
  onNavigate,
  onRename
}) => {
  const { hasPermission } = useAuth();
  const [renaming, setRenaming] = useState(false);
  const [nameInput, setNameInput] = useState(file.name);
  const [hashAlgo] = useState<'sha256' | 'sha1'>('sha256');
  const { data: hashData, isFetching: hashing } = useFileHash(file.isDirectory ? undefined : file.path, hashAlgo);
  const hashValue = hashData?.hash;
  const fetchHash = async (e: React.MouseEvent) => {
    e.stopPropagation();
    if (file.isDirectory) return;
    if (!hashValue) {
      toast.info('Computing hash…');
    } else {
      try { await navigator.clipboard.writeText(hashValue); toast.success('Hash copied'); } catch { toast.success('Hash ready'); }
    }
  };

  const onContextMenu = async (e: React.MouseEvent) => {
    if (file.isDirectory) return; // only files
    if (!e.shiftKey) return; // cheap guard: only show custom menu if user holds shift (avoids full menu implementation)
    e.preventDefault();
    if (!hashValue) {
      toast.info('Computing hash…');
    } else {
      try { await navigator.clipboard.writeText(hashValue); toast.success('Hash copied'); } catch { toast.success('Hash ready'); }
    }
  };

  const handleRowClick = () => {
    if (file.isDirectory && onNavigate) {
      onNavigate();
    }
  };

  return (
  <div className="px-4 py-3 hover:bg-gray-50 flex items-center" onContextMenu={onContextMenu}>
      <input
        type="checkbox"
        aria-label={`Select ${file.name}`}
        title={`Select ${file.name}`}
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
          <div className="flex flex-col">
            {renaming ? (
              <form
                onSubmit={async (e) => {
                  e.preventDefault();
                  if (!onRename) { setRenaming(false); return; }
                  const trimmed = nameInput.trim();
                  if (!trimmed || trimmed === file.name) { setRenaming(false); return; }
                  try { await onRename(trimmed); } catch (err) { console.error(err); }
                  setRenaming(false);
                }}
                className="flex items-center gap-2"
              >
                <input
                  aria-label="Rename folder"
                  title="Rename folder"
                  value={nameInput}
                  onChange={e => setNameInput(e.target.value)}
                  autoFocus
                  onKeyDown={e => { if (e.key === 'Escape') { setRenaming(false); setNameInput(file.name); } }}
                  className="px-1 py-0.5 border border-gray-300 rounded text-sm"
                />
                <button type="submit" className="text-xs px-2 py-0.5 bg-blue-500 text-white rounded">Save</button>
                <button type="button" onClick={() => { setRenaming(false); setNameInput(file.name); }} className="text-xs px-2 py-0.5 border rounded">Cancel</button>
              </form>
            ) : (
              <div className="font-medium text-gray-900 truncate flex items-center gap-2">
                <span>{file.name}</span>
                {file.isDirectory && hasPermission('gcode_harvest', 'update') && onRename && (
                  <button
                    type="button"
                    className="text-xs text-blue-600 hover:underline"
                    onClick={(e) => { e.stopPropagation(); setRenaming(true); }}
                  >Rename</button>
                )}
              </div>
            )}
            {!file.isDirectory && hashValue && (
              <div className="text-[10px] font-mono text-gray-500" title={hashValue}>{hashValue.slice(0,16)}…</div>
            )}
            {file.isDirectory && (
              <div className="text-xs text-gray-500">Folder</div>
            )}
          </div>
        </div>
        {/* Size */}
        <div className="col-span-2 text-gray-600">{file.isDirectory ? '—' : formatBytes(file.size)}</div>
        {/* Modified */}
        <div className="col-span-3 text-gray-600">{formatDistanceToNow(file.modifiedAt, { addSuffix: true })}</div>
        {/* Actions */}
        <div className="col-span-2 flex items-center space-x-2">
          {!file.isDirectory && onDownload && hasPermission('gcode_harvest', 'read') && (
            <button
              onClick={(e) => { e.stopPropagation(); onDownload(); }}
              className="p-1 text-gray-400 hover:text-blue-600"
              title="Download file"
            >
              <ArrowDownTrayIcon className="w-4 h-4" />
            </button>
          )}
          {!file.isDirectory && hasPermission('gcode_harvest', 'read') && (
            <button
              onClick={fetchHash}
              className="p-1 text-gray-400 hover:text-green-600 relative"
              title={hashValue ? `Hash: ${hashValue}` : (hashing ? 'Computing hash…' : `Compute & copy ${hashAlgo.toUpperCase()} hash`)}
            >
              <ClipboardIcon className={`w-4 h-4 ${hashing ? 'opacity-40' : ''}`} />
              {hashing && (
                <span className="absolute inset-0 flex items-center justify-center">
                  <svg className="w-3 h-3 animate-spin text-green-600" viewBox="0 0 24 24">
                    <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" fill="none" />
                    <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z" />
                  </svg>
                </span>
              )}
            </button>
          )}
          {onDelete && hasPermission('gcode_harvest', 'delete') && (
            <button
              onClick={(e) => { e.stopPropagation(); onDelete(); }}
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