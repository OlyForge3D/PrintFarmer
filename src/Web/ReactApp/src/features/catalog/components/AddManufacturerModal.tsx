import { useState } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { Textarea } from '@/common/components/ui/Textarea';

export interface AddManufacturerData {
  name: string;
  url?: string;
  description?: string;
}

interface AddManufacturerModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (data: AddManufacturerData) => Promise<void>;
  isLoading?: boolean;
}

/**
 * Modal for adding a new manufacturer with optional metadata
 * 
 * Collects:
 * - Manufacturer name (required)
 * - Website URL (optional)
 * - Description (optional)
 */
export function AddManufacturerModal({
  isOpen,
  onClose,
  onSubmit,
  isLoading = false,
}: AddManufacturerModalProps) {
  const [name, setName] = useState('');
  const [url, setUrl] = useState('');
  const [description, setDescription] = useState('');
  const [error, setError] = useState<string | null>(null);

  const handleSubmit = async () => {
    if (!name.trim()) {
      setError('Manufacturer name is required');
      return;
    }

    try {
      setError(null);
      await onSubmit({
        name: name.trim(),
        url: url.trim() || undefined,
        description: description.trim() || undefined,
      });
      // Reset form on success
      setName('');
      setUrl('');
      setDescription('');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to add manufacturer');
    }
  };

  const handleClose = () => {
    if (!isLoading) {
      setName('');
      setUrl('');
      setDescription('');
      setError(null);
      onClose();
    }
  };

  const handleKeyPress = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !isLoading) {
      handleSubmit();
    }
  };

  return (
    <Modal isOpen={isOpen} onClose={handleClose} title="Add New Manufacturer">
      <div className="space-y-4 p-4">
        {/* Manufacturer Name - Required */}
        <div>
          <label htmlFor="mfg-name" className="block text-sm font-medium text-pf-text mb-1">
            Manufacturer Name <span className="text-red-500">*</span>
          </label>
          <Input
            id="mfg-name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            onKeyPress={handleKeyPress}
            placeholder="e.g., Prusa Research"
            disabled={isLoading}
            autoFocus
            className="w-full"
          />
        </div>

        {/* Website URL - Optional */}
        <div>
          <label htmlFor="mfg-url" className="block text-sm font-medium text-pf-text mb-1">
            Website URL <span className="text-xs text-pf-text-secondary">(optional)</span>
          </label>
          <Input
            id="mfg-url"
            type="url"
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            onKeyPress={handleKeyPress}
            placeholder="e.g., https://www.prusa3d.com"
            disabled={isLoading}
            className="w-full"
          />
        </div>

        {/* Description - Optional */}
        <div>
          <label htmlFor="mfg-desc" className="block text-sm font-medium text-pf-text mb-1">
            Description <span className="text-xs text-pf-text-secondary">(optional)</span>
          </label>
          <Textarea
            id="mfg-desc"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            onKeyPress={handleKeyPress}
            placeholder="Brief description of the manufacturer"
            disabled={isLoading}
            rows={6}
            className="w-full resize-none"
          />
        </div>

        {/* Error Message */}
        {error && (
          <div className="p-3 bg-red-900/20 border border-red-600 rounded text-red-400 text-sm">
            {error}
          </div>
        )}

        {/* Action Buttons */}
        <div className="flex justify-end gap-2 pt-4">
          <Button
            onClick={handleClose}
            variant="subtle"
            disabled={isLoading}
          >
            Cancel
          </Button>
          <Button
            onClick={handleSubmit}
            disabled={!name.trim() || isLoading}
            className={isLoading ? 'opacity-50 cursor-not-allowed' : ''}
          >
            {isLoading ? 'Adding...' : 'Finish'}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
