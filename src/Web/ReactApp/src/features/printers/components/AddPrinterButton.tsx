import { useState } from 'react';
import { Button } from '@/common/components/ui';
import { AddPrinterModal } from './AddPrinterModal';

interface AddPrinterButtonProps {
  onSuccess?: () => void;
}

export function AddPrinterButton({ onSuccess }: AddPrinterButtonProps) {
  const [isModalOpen, setIsModalOpen] = useState(false);

  const handleAddPrinter = () => {
    setIsModalOpen(true);
  };

  const handleCloseModal = () => {
    setIsModalOpen(false);
  };

  const handleSuccess = () => {
    setIsModalOpen(false);
    onSuccess?.();
  };

  return (
    <>
      <Button
        variant="primary"
        onClick={handleAddPrinter}
        className="inline-flex items-center"
      >
        Add Printer
      </Button>

      <AddPrinterModal
        isOpen={isModalOpen}
        onClose={handleCloseModal}
        onSuccess={handleSuccess}
      />
    </>
  );
}