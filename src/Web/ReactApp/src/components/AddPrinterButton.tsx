import { useState } from 'react';
import { Plus } from 'lucide-react';
import { Button } from '@/components/ui';
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
        variant="success"
        onClick={handleAddPrinter}
        className="inline-flex items-center"
      >
        <Plus className="w-4 h-4 mr-2" />
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