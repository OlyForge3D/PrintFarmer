import { useState } from 'react';
import { Button } from '@/common/components/ui';
import { CheckCircleIcon, CircleIcon } from '@/common/components/icons/MdiIcons';
import type { Printer } from '@/types/api';

interface PrinterServiceStatusProps {
  printer: Printer;
  onToggleEnabled?: (printer: Printer) => void | Promise<void>;
}

export function PrinterServiceStatus({ printer, onToggleEnabled }: PrinterServiceStatusProps) {
  const [pending, setPending] = useState(false);
  const inService = printer.isEnabled !== false;
  const label = inService ? 'In Service' : 'Out of Service';

  if (!onToggleEnabled) {
    return <span className="text-xs text-pf-text-secondary">{label}</span>;
  }

  return (
    <Button
      type="button"
      variant="subtle"
      size="sm"
      className="min-h-11 min-w-11"
      loading={pending}
      aria-label={`Mark ${printer.name} ${inService ? 'Out of Service' : 'In Service'}`}
      onClick={async () => {
        setPending(true);
        try {
          await onToggleEnabled(printer);
        } finally {
          setPending(false);
        }
      }}
      iconLeft={inService ? <CheckCircleIcon className="h-4 w-4" /> : <CircleIcon className="h-4 w-4" />}
    >
      {label}
    </Button>
  );
}
