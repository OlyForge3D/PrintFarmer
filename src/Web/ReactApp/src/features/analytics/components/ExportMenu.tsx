import React, { useState, useRef, useEffect, useCallback } from 'react';
import { Button } from '@/common/components/ui';
import { DownloadIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';

interface Props {
  days?: number;
}

type ExportType = 'pdf' | 'jobs-csv' | 'cost-csv' | 'utilization-csv';

const EXPORT_OPTIONS: { type: ExportType; label: string }[] = [
  { type: 'pdf', label: 'PDF Report' },
  { type: 'jobs-csv', label: 'Job History CSV' },
  { type: 'cost-csv', label: 'Cost Breakdown CSV' },
  { type: 'utilization-csv', label: 'Utilization CSV' },
];

function triggerDownload(blob: Blob, filename: string) {
  const url = window.URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  document.body.appendChild(anchor);
  anchor.click();
  window.URL.revokeObjectURL(url);
  document.body.removeChild(anchor);
}

function buildFilename(type: ExportType): string {
  const datePart = new Date().toISOString().split('T')[0];
  const names: Record<ExportType, string> = {
    pdf: `printfarmer-report-${datePart}.pdf`,
    'jobs-csv': `job-history-${datePart}.csv`,
    'cost-csv': `cost-breakdown-${datePart}.csv`,
    'utilization-csv': `printer-utilization-${datePart}.csv`,
  };
  return names[type];
}

function fetchExport(type: ExportType, days?: number): Promise<Blob> {
  const fetchers: Record<ExportType, (d?: number) => Promise<Blob>> = {
    pdf: (d) => apiClient.exportPdfReport(d),
    'jobs-csv': (d) => apiClient.exportJobHistoryCsv(d),
    'cost-csv': (d) => apiClient.exportCostCsv(d),
    'utilization-csv': (d) => apiClient.exportUtilizationCsv(d),
  };
  return fetchers[type](days);
}

export const ExportMenu: React.FC<Props> = ({ days }) => {
  const [open, setOpen] = useState(false);
  const [exporting, setExporting] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);
  const itemRefs = useRef<(HTMLButtonElement | null)[]>([]);

  useEffect(() => {
    function handleClickOutside(e: MouseEvent) {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    if (open) {
      document.addEventListener('mousedown', handleClickOutside);
      itemRefs.current[0]?.focus();
    }
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [open]);

  const handleMenuKeyDown = useCallback((e: React.KeyboardEvent) => {
    const items = itemRefs.current.filter(Boolean) as HTMLButtonElement[];
    const currentIndex = items.indexOf(document.activeElement as HTMLButtonElement);

    if (e.key === 'Escape') {
      setOpen(false);
    } else if (e.key === 'ArrowDown') {
      e.preventDefault();
      items[(currentIndex + 1) % items.length]?.focus();
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      items[(currentIndex - 1 + items.length) % items.length]?.focus();
    }
  }, []);

  const handleExport = async (type: ExportType) => {
    setOpen(false);
    setExporting(true);
    try {
      const blob = await fetchExport(type, days);
      triggerDownload(blob, buildFilename(type));
      toast.success('Report exported successfully');
    } catch (err) {
      toast.error(`Failed to export: ${String(err)}`);
    } finally {
      setExporting(false);
    }
  };

  return (
    <div className="relative inline-block" ref={menuRef}>
      <Button
        variant="secondary"
        iconLeft={<DownloadIcon />}
        loading={exporting}
        onClick={() => setOpen((prev) => !prev)}
        aria-haspopup="menu"
        aria-expanded={open}
      >
        Export
      </Button>
      {open && (
        <div
          className="absolute right-0 z-30 mt-1 w-48 rounded-md border border-pf-border bg-pf-bg-0 py-1 shadow-lg"
          role="menu"
          aria-label="Export options"
          onKeyDown={handleMenuKeyDown}
        >
          {EXPORT_OPTIONS.map((opt, i) => (
            <button
              key={opt.type}
              ref={(el) => { itemRefs.current[i] = el; }}
              role="menuitem"
              className="w-full px-3 py-1.5 text-left text-sm text-pf-text-primary hover:bg-pf-hover transition-colors"
              onClick={() => handleExport(opt.type)}
            >
              {opt.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
};
