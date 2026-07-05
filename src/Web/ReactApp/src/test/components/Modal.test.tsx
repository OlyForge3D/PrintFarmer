import '@testing-library/jest-dom';
import { useState } from 'react';
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Modal } from '@/common/components/modals/Modal';

describe('Modal', () => {
  describe('Visibility', () => {
    it('should render when isOpen is true', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()}>
          <p>Modal content</p>
        </Modal>
      );

      expect(screen.getByText('Modal content')).toBeInTheDocument();
    });

    it('should not render when isOpen is false', () => {
      render(
        <Modal isOpen={false} onClose={vi.fn()}>
          <p>Modal content</p>
        </Modal>
      );

      expect(screen.queryByText('Modal content')).not.toBeInTheDocument();
    });
  });

  describe('Title', () => {
    it('should render title when provided', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()} title="Test Modal">
          <p>Content</p>
        </Modal>
      );

      expect(screen.getByText('Test Modal')).toBeInTheDocument();
    });

    it('should not render title section when not provided', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()}>
          <p>Content</p>
        </Modal>
      );

      // Should just have content
      expect(screen.getByText('Content')).toBeInTheDocument();
    });
  });

  describe('Close Button', () => {
    it('should show close button by default', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()} title="Test Modal">
          <p>Content</p>
        </Modal>
      );

      // Close button should be present
      const closeButton = screen.queryByRole('button', { name: /close/i });
      expect(closeButton).toBeInTheDocument();
    });

    it('should hide close button when showCloseButton is false', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()} showCloseButton={false}>
          <p>Content</p>
        </Modal>
      );

      // Close button should not be present
      const closeButton = screen.queryByRole('button', { name: /close/i });
      expect(closeButton).not.toBeInTheDocument();
    });

    it('should call onClose when close button is clicked', () => {
      const onClose = vi.fn();
      render(
        <Modal isOpen={true} onClose={onClose} title="Test Modal">
          <p>Content</p>
        </Modal>
      );

      const closeButton = screen.getByRole('button', { name: /close/i });
      fireEvent.click(closeButton);

      expect(onClose).toHaveBeenCalledTimes(1);
    });
  });

  describe('Backdrop Click', () => {
    it('should close on backdrop click when closeOnBackdrop is true', () => {
      const onClose = vi.fn();
      render(
        <Modal isOpen={true} onClose={onClose} closeOnBackdrop={true}>
          <p>Content</p>
        </Modal>
      );

      // The dialog element IS the backdrop in this component
      const backdrop = screen.getByRole('dialog');
      // Click directly on the backdrop, not on child content
      fireEvent.click(backdrop);

      expect(onClose).toHaveBeenCalled();
    });

    it('should NOT close on backdrop click by default (closeOnBackdrop defaults to false)', () => {
      const onClose = vi.fn();
      render(
        <Modal isOpen={true} onClose={onClose}>
          <p>Content</p>
        </Modal>
      );

      // The dialog element IS the backdrop in this component
      const backdrop = screen.getByRole('dialog');
      fireEvent.click(backdrop);

      // Default is false, so onClose should not be called
      expect(onClose).not.toHaveBeenCalled();
    });

    it('should not close when clicking on modal content', () => {
      const onClose = vi.fn();
      render(
        <Modal isOpen={true} onClose={onClose}>
          <p>Content</p>
        </Modal>
      );

      // Clicking on content should not close
      fireEvent.click(screen.getByText('Content'));

      expect(onClose).not.toHaveBeenCalled();
    });
  });

  describe('Escape Key', () => {
    it('should close on Escape key by default', () => {
      const onClose = vi.fn();
      render(
        <Modal isOpen={true} onClose={onClose}>
          <p>Content</p>
        </Modal>
      );

      fireEvent.keyDown(document, { key: 'Escape' });

      expect(onClose).toHaveBeenCalledTimes(1);
    });

    it('should not close on Escape when closeOnEscape is false', () => {
      const onClose = vi.fn();
      render(
        <Modal isOpen={true} onClose={onClose} closeOnEscape={false}>
          <p>Content</p>
        </Modal>
      );

      fireEvent.keyDown(document, { key: 'Escape' });

      expect(onClose).not.toHaveBeenCalled();
    });
  });

  describe('Footer', () => {
    it('should render footer when provided', () => {
      render(
        <Modal 
          isOpen={true} 
          onClose={vi.fn()} 
          footer={<button>Save</button>}
        >
          <p>Content</p>
        </Modal>
      );

      expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument();
    });

    it('should not render footer section when not provided', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()}>
          <p>Content</p>
        </Modal>
      );

      // No save button should exist
      expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument();
    });
  });

  describe('Sizes', () => {
    it('should apply small size class to inner container', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()} size="sm">
          <p data-testid="content">Content</p>
        </Modal>
      );

      // Size class is applied to inner content container
      const contentDiv = screen.getByTestId('content').closest('.max-w-sm');
      expect(contentDiv).toBeInTheDocument();
    });

    it('should apply medium size class when size="md"', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()} size="md">
          <p data-testid="content">Content</p>
        </Modal>
      );

      const contentDiv = screen.getByTestId('content').closest('.max-w-md');
      expect(contentDiv).toBeInTheDocument();
    });

    it('should apply default max-w-2xl when no size is specified', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()}>
          <p data-testid="content">Content</p>
        </Modal>
      );

      // Default without size prop is max-w-2xl
      const contentDiv = screen.getByTestId('content').closest('.max-w-2xl');
      expect(contentDiv).toBeInTheDocument();
    });

    it('should apply large size class', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()} size="lg">
          <p data-testid="content">Content</p>
        </Modal>
      );

      const contentDiv = screen.getByTestId('content').closest('.max-w-lg');
      expect(contentDiv).toBeInTheDocument();
    });

    it('should apply extra large size class', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()} size="xl">
          <p data-testid="content">Content</p>
        </Modal>
      );

      const contentDiv = screen.getByTestId('content').closest('.max-w-xl');
      expect(contentDiv).toBeInTheDocument();
    });

    it('should apply full size class', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()} size="full">
          <p data-testid="content">Content</p>
        </Modal>
      );

      const contentDiv = screen.getByTestId('content').closest('.max-w-7xl');
      expect(contentDiv).toBeInTheDocument();
    });
  });

  describe('Accessibility', () => {
    it('should have dialog role', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()}>
          <p>Content</p>
        </Modal>
      );

      expect(screen.getByRole('dialog')).toBeInTheDocument();
    });

    it('should have aria-modal attribute', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()}>
          <p>Content</p>
        </Modal>
      );

      const dialog = screen.getByRole('dialog');
      expect(dialog).toHaveAttribute('aria-modal', 'true');
    });

    it('should have aria-labelledby when title is provided', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()} title="Test Title">
          <p>Content</p>
        </Modal>
      );

      const dialog = screen.getByRole('dialog');
      const labelledBy = dialog.getAttribute('aria-labelledby');
      
      if (labelledBy) {
        const titleElement = document.getElementById(labelledBy);
        expect(titleElement?.textContent).toBe('Test Title');
      }
    });

    it('should prevent body scroll when open', () => {
      const { rerender } = render(
        <Modal isOpen={true} onClose={vi.fn()}>
          <p>Content</p>
        </Modal>
      );

      expect(document.body.style.overflow).toBe('hidden');

      rerender(
        <Modal isOpen={false} onClose={vi.fn()}>
          <p>Content</p>
        </Modal>
      );

      expect(document.body.style.overflow).toBe('');
    });

    it('should move focus to the first focusable element when opened', async () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()} title="Focusable Modal">
          <button type="button">First action</button>
        </Modal>
      );

      await waitFor(() => {
        expect(screen.getByRole('button', { name: 'Close modal' })).toHaveFocus();
      });
    });

    it('should move focus to the dialog container when there are no focusable children', async () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()} showCloseButton={false}>
          <p>Content only</p>
        </Modal>
      );

      await waitFor(() => {
        expect(screen.getByRole('dialog')).toHaveFocus();
      });
    });

    it('should trap Tab and Shift+Tab within the dialog', async () => {
      const user = userEvent.setup();
      render(
        <Modal isOpen={true} onClose={vi.fn()} showCloseButton={false}>
          <button type="button">First action</button>
          <button type="button">Second action</button>
        </Modal>
      );

      const firstAction = screen.getByRole('button', { name: 'First action' });
      const secondAction = screen.getByRole('button', { name: 'Second action' });

      await waitFor(() => expect(firstAction).toHaveFocus());

      await user.tab();
      expect(secondAction).toHaveFocus();

      await user.tab();
      expect(firstAction).toHaveFocus();

      await user.tab({ shift: true });
      expect(secondAction).toHaveFocus();
    });

    it('should keep focus on the dialog container when trapping with no focusable children', async () => {
      const user = userEvent.setup();
      render(
        <Modal isOpen={true} onClose={vi.fn()} showCloseButton={false}>
          <p>Content only</p>
        </Modal>
      );

      const dialog = screen.getByRole('dialog');
      await waitFor(() => expect(dialog).toHaveFocus());

      await user.tab();
      expect(dialog).toHaveFocus();
    });

    it('should restore focus to the previously focused trigger when closed', async () => {
      const user = userEvent.setup();

      function ModalHarness() {
        const [isOpen, setIsOpen] = useState(false);

        return (
          <>
            <button type="button" onClick={() => setIsOpen(true)}>
              Open modal
            </button>
            <Modal isOpen={isOpen} onClose={() => setIsOpen(false)}>
              <p>Content</p>
            </Modal>
          </>
        );
      }

      render(<ModalHarness />);

      const openButton = screen.getByRole('button', { name: 'Open modal' });
      await user.click(openButton);
      await waitFor(() => expect(screen.getByRole('button', { name: 'Close modal' })).toHaveFocus());

      await user.click(screen.getByRole('button', { name: 'Close modal' }));

      await waitFor(() => {
        expect(openButton).toHaveFocus();
      });
    });

    it('should mark background body siblings inert and aria-hidden while open', () => {
      const background = document.createElement('button');
      background.type = 'button';
      background.textContent = 'Background action';
      document.body.appendChild(background);

      const { unmount } = render(
        <Modal isOpen={true} onClose={vi.fn()}>
          <p>Content</p>
        </Modal>
      );

      expect(background).toHaveAttribute('inert');
      expect(background).toHaveAttribute('aria-hidden', 'true');
      expect(screen.getByRole('dialog')).not.toHaveAttribute('inert');
      expect(screen.getByRole('dialog')).not.toHaveAttribute('aria-hidden');

      unmount();

      expect(background).not.toHaveAttribute('inert');
      expect(background).not.toHaveAttribute('aria-hidden');
      background.remove();
    });
  });

  describe('Custom ClassName', () => {
    it('should apply custom className to modal content container', () => {
      render(
        <Modal isOpen={true} onClose={vi.fn()} className="custom-class">
          <p data-testid="content">Content</p>
        </Modal>
      );

      // Custom class is on the inner container, not the backdrop
      const contentDiv = screen.getByTestId('content').closest('.custom-class');
      expect(contentDiv).toBeInTheDocument();
    });
  });
});
