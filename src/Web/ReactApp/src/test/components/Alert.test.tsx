import '@testing-library/jest-dom';
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { Alert } from '@/common/components/ui/Alert';

describe('Alert', () => {
  describe('Basic Rendering', () => {
    it('should render children content', () => {
      render(<Alert>Alert message</Alert>);

      expect(screen.getByText('Alert message')).toBeInTheDocument();
    });

    it('should render title when provided', () => {
      render(<Alert title="Alert Title">Alert message</Alert>);

      expect(screen.getByText('Alert Title')).toBeInTheDocument();
      expect(screen.getByText('Alert message')).toBeInTheDocument();
    });
  });

  describe('Alert Types', () => {
    it('should render info alert by default', () => {
      render(<Alert>Info message</Alert>);

      // Get the outermost alert container with border classes
      const alert = screen.getByText('Info message').closest('.border.rounded.p-3');
      expect(alert).toHaveClass('bg-pf-accent-bg');
    });

    it('should render success alert', () => {
      render(<Alert type="success">Success message</Alert>);

      const alert = screen.getByText('Success message').closest('.border.rounded.p-3');
      expect(alert).toHaveClass('bg-pf-success-bg');
    });

    it('should render error alert with role="alert"', () => {
      render(<Alert type="error">Error message</Alert>);

      expect(screen.getByRole('alert')).toBeInTheDocument();
      expect(screen.getByText('Error message')).toBeInTheDocument();
    });

    it('should render warning alert', () => {
      render(<Alert type="warning">Warning message</Alert>);

      const alert = screen.getByText('Warning message').closest('.border.rounded.p-3');
      expect(alert).toHaveClass('bg-pf-warning');
    });
  });

  describe('Close Button', () => {
    it('should show close button when onClose is provided', () => {
      render(<Alert onClose={vi.fn()}>Dismissible alert</Alert>);

      expect(screen.getByLabelText('Dismiss message')).toBeInTheDocument();
    });

    it('should not show close button when onClose is not provided', () => {
      render(<Alert>Non-dismissible alert</Alert>);

      expect(screen.queryByLabelText('Dismiss message')).not.toBeInTheDocument();
    });

    it('should call onClose when close button is clicked', () => {
      const onClose = vi.fn();
      render(<Alert onClose={onClose}>Dismissible alert</Alert>);

      fireEvent.click(screen.getByLabelText('Dismiss message'));

      expect(onClose).toHaveBeenCalledTimes(1);
    });
  });

  describe('Styling', () => {
    it('should apply custom className', () => {
      render(<Alert className="custom-class">Styled alert</Alert>);

      // Custom className is applied to the outermost div with border
      const alert = screen.getByText('Styled alert').closest('.border.rounded.p-3');
      expect(alert).toHaveClass('custom-class');
    });

    it('should have default border and padding styles', () => {
      render(<Alert>Styled alert</Alert>);

      // Get the outer alert container
      const alert = screen.getByText('Styled alert').closest('.border.rounded.p-3');
      expect(alert).toHaveClass('border');
      expect(alert).toHaveClass('rounded');
      expect(alert).toHaveClass('p-3');
    });
  });

  describe('Accessibility', () => {
    it('should have alert role for error type', () => {
      render(<Alert type="error">Error message</Alert>);

      expect(screen.getByRole('alert')).toBeInTheDocument();
    });

    it('should not have alert role for non-error types', () => {
      render(<Alert type="info">Info message</Alert>);

      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });

    it('should have accessible dismiss button', () => {
      render(<Alert onClose={vi.fn()}>Dismissible</Alert>);

      const closeButton = screen.getByLabelText('Dismiss message');
      expect(closeButton).toHaveAttribute('aria-label', 'Dismiss message');
    });
  });

  describe('Content Structure', () => {
    it('should render title with proper emphasis', () => {
      render(<Alert title="Important">Content</Alert>);

      const title = screen.getByText('Important');
      expect(title).toHaveClass('font-semibold');
    });

    it('should render complex children', () => {
      render(
        <Alert>
          <p>Paragraph 1</p>
          <p>Paragraph 2</p>
        </Alert>
      );

      expect(screen.getByText('Paragraph 1')).toBeInTheDocument();
      expect(screen.getByText('Paragraph 2')).toBeInTheDocument();
    });
  });
});
