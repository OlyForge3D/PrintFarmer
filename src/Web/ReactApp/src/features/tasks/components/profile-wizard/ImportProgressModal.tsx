import React from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Download, Package, Printer, Palette, Settings, Loader2 } from 'lucide-react';

interface ImportProgressModalProps {
  /** Whether the modal is open */
  isOpen: boolean;
  /** Total number of machine profiles being imported */
  machineCount: number;
  /** Total number of process profiles being imported */
  processCount: number;
  /** Total number of filament profiles being imported */
  filamentCount: number;
}

/**
 * Modal that displays progress during profile import.
 * Shows an animated progress indicator and profile counts being imported.
 */
export const ImportProgressModal: React.FC<ImportProgressModalProps> = ({
  isOpen,
  machineCount,
  processCount,
  filamentCount,
}) => {
  const totalCount = machineCount + processCount + filamentCount;

  return (
    <Modal
      isOpen={isOpen}
      onClose={() => {}} // Prevent closing during import
      size="md"
      showCloseButton={false}
      closeOnBackdrop={false}
      closeOnEscape={false}
    >
      <div className="p-6 text-center">
        {/* Animated import icon */}
        <div className="flex justify-center mb-6">
          <div className="relative">
            {/* Spinning outer ring */}
            <div className="absolute inset-0 rounded-full border-4 border-pf-accent/20" />
            <div className="w-20 h-20 rounded-full border-4 border-transparent border-t-pf-accent animate-spin" />
            {/* Center icon */}
            <div className="absolute inset-0 flex items-center justify-center">
              <Download className="h-8 w-8 text-pf-accent animate-pulse" />
            </div>
          </div>
        </div>

        {/* Title */}
        <h3 className="text-xl font-semibold text-pf-text-primary mb-2">
          Importing Profiles
        </h3>
        <p className="text-pf-text-secondary mb-6">
          Please wait while we import your slicer profiles...
        </p>

        {/* Profile summary cards */}
        <div className="grid grid-cols-3 gap-3 mb-6">
          <ImportingCard
            icon={<Printer className="h-5 w-5" />}
            label="Machine"
            count={machineCount}
          />
          <ImportingCard
            icon={<Settings className="h-5 w-5" />}
            label="Process"
            count={processCount}
          />
          <ImportingCard
            icon={<Palette className="h-5 w-5" />}
            label="Filament"
            count={filamentCount}
          />
        </div>

        {/* Total count */}
        <div className="flex items-center justify-center gap-2 text-sm text-pf-text-tertiary">
          <Package className="h-4 w-4" />
          <span>Total: {totalCount} profiles</span>
        </div>

        {/* Progress indicator dots */}
        <div className="flex justify-center gap-1.5 mt-6">
          <div className="w-2 h-2 rounded-full bg-pf-accent animate-bounce" />
          <div className="w-2 h-2 rounded-full bg-pf-accent animate-bounce [animation-delay:150ms]" />
          <div className="w-2 h-2 rounded-full bg-pf-accent animate-bounce [animation-delay:300ms]" />
        </div>
      </div>
    </Modal>
  );
};

interface ImportingCardProps {
  icon: React.ReactNode;
  label: string;
  count: number;
}

const ImportingCard: React.FC<ImportingCardProps> = ({ icon, label, count }) => (
  <div className="bg-pf-bg-2 rounded-lg p-3 border border-pf-border">
    <div className="flex items-center justify-center gap-2 text-pf-accent mb-1">
      {icon}
      <Loader2 className="h-3 w-3 animate-spin" />
    </div>
    <div className="text-lg font-semibold text-pf-text-primary">{count}</div>
    <div className="text-xs text-pf-text-tertiary">{label}</div>
  </div>
);

export default ImportProgressModal;
