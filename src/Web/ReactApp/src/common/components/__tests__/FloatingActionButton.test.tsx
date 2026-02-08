import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { FloatingActionButton } from '../FloatingActionButton';

const MockIcon = ({ className }: { className?: string }) => (
  <svg className={className} data-testid="mock-icon" />
);

describe('FloatingActionButton', () => {
  it('should render with icon and label', () => {
    render(
      <FloatingActionButton
        icon={MockIcon}
        onClick={vi.fn()}
        label="Add Item"
      />
    );

    expect(screen.getByTestId('mock-icon')).toBeInTheDocument();
    expect(screen.getByLabelText('Add Item')).toBeInTheDocument();
  });

  it('should call onClick when clicked', async () => {
    const handleClick = vi.fn();
    const user = userEvent.setup();

    render(
      <FloatingActionButton
        icon={MockIcon}
        onClick={handleClick}
        label="Click Me"
      />
    );

    await user.click(screen.getByLabelText('Click Me'));

    expect(handleClick).toHaveBeenCalledTimes(1);
  });

  it('should apply bottom-right position by default', () => {
    render(
      <FloatingActionButton
        icon={MockIcon}
        onClick={vi.fn()}
        label="Default Position"
      />
    );

    const button = screen.getByLabelText('Default Position');
    expect(button).toHaveClass('bottom-6', 'right-6');
  });

  it('should apply bottom-center position', () => {
    render(
      <FloatingActionButton
        icon={MockIcon}
        onClick={vi.fn()}
        label="Center Position"
        position="bottom-center"
      />
    );

    const button = screen.getByLabelText('Center Position');
    expect(button).toHaveClass('bottom-6', 'left-1/2');
  });

  it('should apply bottom-left position', () => {
    render(
      <FloatingActionButton
        icon={MockIcon}
        onClick={vi.fn()}
        label="Left Position"
        position="bottom-left"
      />
    );

    const button = screen.getByLabelText('Left Position');
    expect(button).toHaveClass('bottom-6', 'left-6');
  });

  it('should be disabled when disabled prop is true', () => {
    render(
      <FloatingActionButton
        icon={MockIcon}
        onClick={vi.fn()}
        label="Disabled Button"
        disabled
      />
    );

    expect(screen.getByLabelText('Disabled Button')).toBeDisabled();
  });

  it('should show loading spinner when loading', () => {
    render(
      <FloatingActionButton
        icon={MockIcon}
        onClick={vi.fn()}
        label="Loading Button"
        loading
      />
    );

    expect(screen.queryByTestId('mock-icon')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Loading Button').querySelector('.animate-spin')).toBeInTheDocument();
  });

  it('should be disabled when loading', () => {
    render(
      <FloatingActionButton
        icon={MockIcon}
        onClick={vi.fn()}
        label="Loading Button"
        loading
      />
    );

    expect(screen.getByLabelText('Loading Button')).toBeDisabled();
  });

  it('should apply custom className', () => {
    render(
      <FloatingActionButton
        icon={MockIcon}
        onClick={vi.fn()}
        label="Custom Class"
        className="custom-test-class"
      />
    );

    expect(screen.getByLabelText('Custom Class')).toHaveClass('custom-test-class');
  });

  it('should apply secondary variant', () => {
    render(
      <FloatingActionButton
        icon={MockIcon}
        onClick={vi.fn()}
        label="Secondary Button"
        variant="secondary"
      />
    );

    expect(screen.getByLabelText('Secondary Button')).toBeInTheDocument();
  });
});
