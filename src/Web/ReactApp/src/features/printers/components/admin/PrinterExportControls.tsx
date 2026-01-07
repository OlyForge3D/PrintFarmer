import React from 'react';
import Button from '@/common/components/ui/Button';
import { apiClient } from '@/services/api';
import { useAuth } from '@/features/auth/hooks/useAuth';

export default function PrinterExportControls() {
  const auth = useAuth();
  const [exporting, setExporting] = React.useState(false);

  if (!auth.hasPermission('printers', 'admin')) return null;

  async function handleExport() {
    setExporting(true);
    try {
      const stream = await apiClient.streamExportFile('printers/export');
      // streamExportFile handles saving on client; we just trigger it here
      await stream;
    } catch (err) {
      console.error('Export failed', err);
    } finally {
      setExporting(false);
    }
  }

  return (
    <div className="flex items-center gap-2">
      <Button onClick={handleExport} disabled={exporting} variant="outline" size="sm">
        {exporting ? 'Exporting…' : 'Export printers'}
      </Button>
    </div>
  );
}
