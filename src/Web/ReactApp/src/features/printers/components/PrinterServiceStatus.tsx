import { useState } from 'react';
import { mdiPowerPlug, mdiPowerPlugOutline } from '@mdi/js';
import { Button } from '@/common/components/ui';
import { CheckCircleIcon, CircleIcon } from '@/common/components/icons/MdiIcons';
import type { Printer } from '@/types/api';

interface PrinterServiceStatusProps {
  printer: Printer;
  onToggleEnabled?: (printer: Printer) => void | Promise<void>;
  iconOnly?: boolean;
}

export function PrinterServiceStatus({ printer, onToggleEnabled, iconOnly = false }: PrinterServiceStatusProps) {
  const [pending, setPending] = useState(false);
  const inService = printer.isEnabled !== false;
  const label = inService ? 'In Service' : 'Out of Service';
  const actionLabel = `${label}: Mark ${printer.name} ${inService ? 'Out of Service' : 'In Service'}`;
  const glyph = (
    <svg
      viewBox="0 0 24 24"
      className={`h-5 w-5 ${inService ? 'text-pf-success' : 'text-pf-text-secondary'}`}
      role="img"
      aria-label={label}
    >
      <path fill="currentColor" d={inService ? mdiPowerPlug : mdiPowerPlugOutline} />
    </svg>
  );

  if (!onToggleEnabled) {
    if (iconOnly) {
      return <span className="inline-flex min-h-11 min-w-11 items-center justify-center" title={label}>{glyph}</span>;
    }
    return <span className="text-xs text-pf-text-secondary">{label}</span>;
  }

  return (
    <Button
      type="button"
      variant={iconOnly ? 'ghost' : 'subtle'}
      size="sm"
      className="min-h-11 min-w-11 text-pf-text-primary"
      loading={pending}
      title={iconOnly ? actionLabel : undefined}
      aria-pressed={iconOnly ? inService : undefined}
      aria-busy={iconOnly ? pending : undefined}
      aria-label={`${pending ? 'Please wait…' : label}: Mark ${printer.name} ${inService ? 'Out of Service' : 'In Service'}`}
      onClick={async () => {
        setPending(true);
        try {
          await onToggleEnabled(printer);
        } finally {
          setPending(false);
        }
      }}
      iconLeft={iconOnly ? glyph : inService ? <CheckCircleIcon className="h-4 w-4" /> : <CircleIcon className="h-4 w-4" />}
    >
      {iconOnly ? undefined : label}
    </Button>
  );
}
