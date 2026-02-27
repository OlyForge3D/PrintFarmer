import React from 'react';
import { Modal } from './Modal';

export interface ImagePreviewModalProps {
  /** Whether the modal is open */
  isOpen: boolean;
  /** Callback when modal should close */
  onClose: () => void;
  /** Image source URL */
  src: string;
  /** Alt text for the image */
  alt?: string;
  /** Optional title for the modal header */
  title?: string;
}

/**
 * Simple modal that displays a full-size image preview.
 * Used for enlarging thumbnails on click.
 */
export function ImagePreviewModal({ isOpen, onClose, src, alt = 'Image preview', title }: ImagePreviewModalProps) {
  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={title}
      size="lg"
      closeOnBackdrop
    >
      <div className="flex items-center justify-center">
        <img
          src={src}
          alt={alt}
          className="max-w-full max-h-[70vh] rounded-lg object-contain"
        />
      </div>
    </Modal>
  );
}
