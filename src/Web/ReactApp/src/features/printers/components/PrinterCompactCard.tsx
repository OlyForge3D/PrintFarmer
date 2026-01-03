import React, { useState } from 'react';
import { Printer } from '@/types/api';
import { EditIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';

interface PrinterCompactCardProps {
  printer: Printer;
  onEdit: (printer: Printer) => void;
  onDelete: (printer: Printer) => void;
}

export function PrinterCompactCard({
  printer: p,
  onEdit,
  onDelete
}: PrinterCompactCardProps) {
  const [imageError, setImageError] = useState(false);
  const isOnline = p.isOnline ?? false;
  const state = p.state ?? '';
  const isPrinting = state.toLowerCase().includes('printing');

  return (
    <div className="bg-pf-bg-1 rounded-lg p-3 shadow border border-pf-border hover:border-pf-primary transition-colors">
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
        <div className="font-bold text-lg text-pf-text-primary font-bebas uppercase mb-1">
          {p.name}
        </div>
        <div className="text-pf-text-secondary text-xs">
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
          variant="subtle"
          size="sm"
          onClick={() => onEdit(p)}
          className="!p-1 !h-auto"
        >
          <EditIcon className="w-4 h-4" />
        </Button>
        <Button
          aria-label={`Delete ${p.name}`}
          title="Delete"
          variant="subtle"
          size="sm"
          onClick={() => onDelete(p)}
          className="!p-1 !h-auto hover:text-pf-error"
        >
          <DeleteIcon className="w-4 h-4" />
        </Button>
      </div>
    </div>
  );
}
