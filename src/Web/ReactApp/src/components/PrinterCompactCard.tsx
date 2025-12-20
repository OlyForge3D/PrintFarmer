import React, { useState } from 'react';
import { Printer } from '@/types/api';
import { EditIcon, DeleteIcon } from '@/components/icons/MdiIcons';
import { Button } from '@/components/ui';

interface PrinterCompactCardProps {
  printer: Printer;
  onEdit: (printer: Printer) => void;
  onDelete: (printer: Printer) => void;
  getPrinterStatus?: (id: string) => { state?: string; isOnline?: boolean } | undefined;
}

export function PrinterCompactCard({
  printer: p,
  onEdit,
  onDelete,
  getPrinterStatus
}: PrinterCompactCardProps) {
  const [imageError, setImageError] = useState(false);
  const status = (getPrinterStatus?.(p.id)?.state ?? p.state ?? '') as string;
  const isPrinting = status.toLowerCase().includes('printing');
  const isOnline = !!p.isOnline || ['operational', 'ready', 'idle'].some(x => status.toLowerCase().includes(x));

  return (
    <div className="bg-pf-bg-1 rounded-lg p-4 shadow border border-pf-border hover:border-pf-primary transition-colors">
      <div className="mb-3">
        {p.thumbnailUrl && !imageError ? (
          <div className="w-full h-32 bg-pf-border flex items-center justify-center rounded overflow-hidden mb-3">
            <img
              src={p.thumbnailUrl}
              alt={`${p.name} thumbnail`}
              className="w-full h-full object-cover rounded"
              loading="lazy"
              onError={() => setImageError(true)}
            />
          </div>
        ) : null}
        <div className="text-base font-medium">{p.name}</div>
        <div className="text-sm text-pf-text-secondary">
          {p.manufacturerName ? `${p.manufacturerName} ${p.modelName ?? ''}` : (p.modelName ?? '')}
        </div>
      </div>
      <div className="flex items-center gap-2 mb-3">
        <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${isOnline ? 'bg-pf-status-online-bg text-pf-status-online-text' : 'bg-pf-border-medium text-pf-text-secondary'}`}>
          {isOnline ? 'Online' : 'Offline'}
        </span>
        {isPrinting && <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-pf-warning text-pf-text-primary">Printing</span>}
      </div>
      <div className="flex gap-2">
        <Button
          aria-label={`Edit ${p.name}`}
          title="Edit"
          variant="primary"
          size="sm"
          onClick={() => onEdit(p)}
          className="flex-1"
        >
          <EditIcon className="w-4 h-4 mr-1" /> Edit
        </Button>
        <Button
          aria-label={`Delete ${p.name}`}
          title="Delete"
          variant="subtle"
          size="sm"
          onClick={() => onDelete(p)}
        >
          <DeleteIcon className="w-4 h-4" />
        </Button>
      </div>
    </div>
  );
}
