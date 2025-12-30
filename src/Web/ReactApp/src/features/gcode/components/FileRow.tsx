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
import { useAuth } from '@/features/auth/hooks/useAuth';
import { toast } from 'sonner';
import { useFileHash } from '@/features/gcode/hooks/useFileHash';
import { Button, Checkbox, Input } from '@/common/components/ui';

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
  <div className="px-4 py-3 hover:bg-pf-hover flex items-center" onContextMenu={onContextMenu}>
      <Checkbox
        checked={selected}
        onChange={(e) => onSelect(e.target.checked)}
        className="mr-4"
        title={`Select ${file.name}`}
        aria-label={`Select ${file.name}`}
      />
      <div
  className="flex-1 grid grid-cols-12 gap-4 text-sm cursor-pointer"
        onClick={handleRowClick}
      >
        {/* Name */}
  <div className="col-span-5 flex items-center space-x-3">
          {file.isDirectory ? (
            <FolderIcon className="w-5 h-5 text-pf-accent" />
          ) : (
            <DocumentIcon className="w-5 h-5 text-pf-text-tertiary" />
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
                <Input
                  type="text"
                  aria-label="Rename folder"
                  title="Rename folder"
                  value={nameInput}
                  onChange={e => setNameInput(e.target.value)}
                  autoFocus
                  onKeyDown={e => { if (e.key === 'Escape') { setRenaming(false); setNameInput(file.name); } }}
                  className="text-sm"
                />
                <Button type="submit" variant="primary" size="sm" className="text-xs">Save</Button>
                <Button type="button" variant="secondary" size="sm" className="text-xs" onClick={() => { setRenaming(false); setNameInput(file.name); }}>Cancel</Button>
              </form>
            ) : (
              <div className="font-medium text-pf-text-primary truncate flex items-center gap-2">
                <span>{file.name}</span>
                {file.isDirectory && hasPermission('gcode_harvest', 'update') && onRename && (
                  <Button
                    type="button"
                    variant="subtle"
                    size="sm"
                    className="text-xs"
                    onClick={(e) => { e.stopPropagation(); setRenaming(true); }}
                  >Rename</Button>
                )}
              </div>
            )}
            {!file.isDirectory && hashValue && (
              <div className="text-[10px] font-mono text-pf-text-tertiary" title={hashValue}>{hashValue.slice(0,16)}…</div>
            )}
            {file.isDirectory && (
              <div className="text-xs text-pf-text-tertiary">Folder</div>
            )}
          </div>
        </div>
        {/* Size */}
  <div className="col-span-2 text-pf-text-secondary">{file.isDirectory ? '—' : formatBytes(file.size)}</div>
        {/* Modified */}
  <div className="col-span-3 text-pf-text-secondary">{formatDistanceToNow(file.modifiedAt, { addSuffix: true })}</div>
        {/* Actions */}
  <div className="col-span-2 flex items-center space-x-2">
          {!file.isDirectory && onDownload && hasPermission('gcode_harvest', 'read') && (
            <Button
              onClick={(e) => { e.stopPropagation(); onDownload(); }}
              variant="subtle"
              size="sm"
              title="Download file"
              className="!p-1"
            >
              <ArrowDownTrayIcon className="w-4 h-4" />
            </Button>
          )}
          {!file.isDirectory && hasPermission('gcode_harvest', 'read') && (
            <Button
              onClick={fetchHash}
              variant="subtle"
              size="sm"
              title={hashValue ? `Hash: ${hashValue}` : (hashing ? 'Computing hash...' : `Compute & copy ${hashAlgo.toUpperCase()} hash`)}
              className="!p-1 relative"
            >
              <ClipboardIcon className={`w-4 h-4 ${hashing ? 'opacity-40' : ''}`} />
              {hashing && (
                <span className="absolute inset-0 flex items-center justify-center">
                  <svg className="w-3 h-3 animate-spin text-pf-success" viewBox="0 0 24 24">
                    <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" fill="none" />
                    <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z" />
                  </svg>
                </span>
              )}
            </Button>
          )}
          {onDelete && hasPermission('gcode_harvest', 'delete') && (
            <Button
              onClick={(e) => { e.stopPropagation(); onDelete(); }}
              variant="subtle"
              size="sm"
              title="Delete file"
              className="!p-1 hover:text-pf-error"
            >
              <TrashIcon className="w-4 h-4" />
            </Button>
          )}
        </div>
      </div>
    </div>
  );
};