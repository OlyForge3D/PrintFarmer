import React from 'react';
import Button from '@/common/components/ui/Button';
import { PrinterDiscoveryModal } from '@/features/printers/components/PrinterDiscoveryModal';
import { useAuth } from '@/features/auth/hooks/useAuth';

export default function PrinterDiscoveryControls() {
  const [open, setOpen] = React.useState(false);
  const auth = useAuth();

  if (!auth.hasPermission('printers', 'admin')) return null;

  return (
    <div className="flex items-center gap-2">
      <Button onClick={() => setOpen(true)} variant="outline" size="sm">
        Discover Printers
      </Button>
      <PrinterDiscoveryModal isOpen={open} onClose={() => setOpen(false)} />
    </div>
  );
}
