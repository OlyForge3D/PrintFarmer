import { useState } from 'react';
import { HelpCircleIcon } from '@/common/components/icons/MdiIcons';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui';
import { OfflineTroubleshootingGuide } from '@/features/printers/components/OfflineTroubleshootingGuide';
import type { Printer } from '@/types/api';

interface PrinterTroubleshootingButtonProps {
  printer: Printer;
}

export function PrinterTroubleshootingButton({ printer }: PrinterTroubleshootingButtonProps) {
  const [isOpen, setIsOpen] = useState(false);

  return (
    <>
      <Button
        type="button"
        variant="ghost"
        size="sm"
        className="ml-auto h-11 w-11 shrink-0 p-0 text-pf-warning-text enabled:hover:text-pf-text-primary sm:h-8 sm:w-8"
        title="Troubleshoot offline printer"
        aria-label={`Open troubleshooting for ${printer.name}`}
        aria-haspopup="dialog"
        aria-expanded={isOpen}
        onClick={() => setIsOpen(true)}
        iconCenter={<HelpCircleIcon className="h-4 w-4" />}
      />
      <Modal
        isOpen={isOpen}
        onClose={() => setIsOpen(false)}
        title="Troubleshooting"
        closeAriaLabel="Close troubleshooting"
        closeOnBackdrop
        width="max-w-md"
        maxHeight="h-dvh max-h-dvh"
        className="fixed inset-y-0 right-0 rounded-none"
        closeButtonClassName="min-h-11 min-w-11"
      >
        <p className="mb-4 break-words text-sm text-pf-text-primary">{printer.name}</p>
        <OfflineTroubleshootingGuide
          printerBackend={printer.backend}
          printerIp={printer.ipAddress}
          serverUrl={printer.serverUrl ?? printer.backendUrl}
          frontendUrl={printer.frontendUrl}
          variant="content"
        />
      </Modal>
    </>
  );
}
