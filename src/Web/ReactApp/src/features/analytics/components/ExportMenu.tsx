import React, { useState, useRef, useEffect } from 'react';
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

async function fetchExport(type: ExportType, days?: number): Promise<Blob> {
  const params = days ? `?days=${days}` : '';
  const endpoints: Record<ExportType, string> = {
    pdf: `/statistics/export/pdf${params}`,
    'jobs-csv': `/statistics/export/jobs-csv${params}`,
    'cost-csv': `/statistics/export/cost-csv${params}`,
    'utilization-csv': `/statistics/export/utilization-csv${params}`,
  };
  const response = await apiClient.get(endpoints[type], { responseType: 'blob' });
  return response.data as Blob;
}

export const ExportMenu: React.FC<Props> = ({ days }) => {
  const [open, setOpen] = useState(false);
  const [exporting, setExporting] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    function handleClickOutside(e: MouseEvent) {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    if (open) {
      document.addEventListener('mousedown', handleClickOutside);
    }
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [open]);

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
        aria-haspopup="true"
        aria-expanded={open}
      >
        Export
      </Button>
      {open && (
        <div
          className="absolute right-0 z-30 mt-1 w-48 rounded-md border border-pf-border bg-pf-bg-0 py-1 shadow-lg"
          role="list"
        >
          {EXPORT_OPTIONS.map((opt) => (
            <Button
              key={opt.type}
              variant="ghost"
              size="sm"
              className="w-full justify-start rounded-none"
              onClick={() => handleExport(opt.type)}
            >
              {opt.label}
            </Button>
          ))}
        </div>
      )}
    </div>
  );
};
