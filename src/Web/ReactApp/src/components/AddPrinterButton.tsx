import { useState } from 'react';
import { Plus } from 'lucide-react';
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
      <button
        type="button"
        onClick={handleAddPrinter}
        className="inline-flex items-center px-4 py-2 bg-pf-success hover:bg-pf-success-hover text-white font-medium rounded-lg transition-colors duration-200 shadow-sm hover:shadow-md"
      >
        <Plus className="w-4 h-4 mr-2" />
        Add Printer
      </button>

      <AddPrinterModal
        isOpen={isModalOpen}
        onClose={handleCloseModal}
        onSuccess={handleSuccess}
      />
    </>
  );
}