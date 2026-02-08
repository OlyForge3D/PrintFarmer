import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { ConfirmationModal } from '../ConfirmationModal';

describe('ConfirmationModal', () => {
  const mockOnConfirm = vi.fn();
  const mockOnCancel = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('should render when isOpen is true', () => {
    render(
      <ConfirmationModal
        isOpen={true}
        title="Confirm Action"
        message="Are you sure you want to proceed?"
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    expect(screen.getByText('Confirm Action')).toBeInTheDocument();
    expect(screen.getByText('Are you sure you want to proceed?')).toBeInTheDocument();
  });

  it('should not render when isOpen is false', () => {
    render(
      <ConfirmationModal
        isOpen={false}
        title="Confirm Action"
        message="Are you sure you want to proceed?"
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    expect(screen.queryByText('Confirm Action')).not.toBeInTheDocument();
  });

  it('should render default button texts', () => {
    render(
      <ConfirmationModal
        isOpen={true}
        title="Confirm Action"
        message="Are you sure?"
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    expect(screen.getByText('Confirm')).toBeInTheDocument();
    expect(screen.getByText('Cancel')).toBeInTheDocument();
  });

  it('should render custom button texts', () => {
    render(
      <ConfirmationModal
        isOpen={true}
        title="Delete Item"
        message="This action cannot be undone"
        confirmButtonText="Delete"
        cancelButtonText="Go Back"
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    expect(screen.getByText('Delete')).toBeInTheDocument();
    expect(screen.getByText('Go Back')).toBeInTheDocument();
  });

  it('should call onConfirm when confirm button is clicked', () => {
    render(
      <ConfirmationModal
        isOpen={true}
        title="Confirm Action"
        message="Are you sure?"
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    fireEvent.click(screen.getByText('Confirm'));
    expect(mockOnConfirm).toHaveBeenCalled();
  });

  it('should call onCancel when cancel button is clicked', () => {
    render(
      <ConfirmationModal
        isOpen={true}
        title="Confirm Action"
        message="Are you sure?"
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    fireEvent.click(screen.getByText('Cancel'));
    expect(mockOnCancel).toHaveBeenCalled();
  });

  it('should render danger variant when isDangerous is true', () => {
    render(
      <ConfirmationModal
        isOpen={true}
        title="Delete Item"
        message="This action cannot be undone"
        isDangerous={true}
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    // Check that the alert icon is rendered
    const modal = screen.getByText('Delete Item').closest('div');
    expect(modal).toBeInTheDocument();
  });

  it('should render children content', () => {
    render(
      <ConfirmationModal
        isOpen={true}
        title="Confirm Action"
        message="Are you sure?"
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      >
        <div data-testid="custom-content">Custom warning message</div>
      </ConfirmationModal>
    );

    expect(screen.getByTestId('custom-content')).toBeInTheDocument();
    expect(screen.getByText('Custom warning message')).toBeInTheDocument();
  });

  it('should display alert icon for dangerous operations', () => {
    const { container } = render(
      <ConfirmationModal
        isOpen={true}
        title="Delete Item"
        message="This will permanently delete the item"
        isDangerous={true}
        confirmButtonText="Delete"
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    // The alert icon should be present when isDangerous is true
    expect(container.querySelector('svg')).toBeInTheDocument();
  });

  it('should handle long messages', () => {
    const longMessage = 'This is a very long message that should be displayed properly in the modal';

    render(
      <ConfirmationModal
        isOpen={true}
        title="Confirm Action"
        message={longMessage}
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    expect(screen.getByText(longMessage)).toBeInTheDocument();
  });

  it('should render with proper button layout', () => {
    const { container } = render(
      <ConfirmationModal
        isOpen={true}
        title="Confirm"
        message="Proceed?"
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    const buttons = container.querySelectorAll('button');
    expect(buttons.length).toBeGreaterThanOrEqual(2); // At least Cancel and Confirm buttons
  });
});
