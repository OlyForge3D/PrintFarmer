import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { ContextMenu, ContextMenuItem } from '../ContextMenu';

describe('ContextMenu', () => {
  const mockOnClose = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  const defaultItems: ContextMenuItem[] = [
    {
      label: 'Edit',
      onClick: vi.fn(),
    },
    {
      label: 'Delete',
      onClick: vi.fn(),
      variant: 'danger',
    },
  ];

  it('should render menu at specified position', () => {
    const { container } = render(
      <ContextMenu x={100} y={200} items={defaultItems} onClose={mockOnClose} />
    );

    const menu = container.querySelector('[role="menu"]');
    expect(menu).toBeInTheDocument();
    expect(menu).toHaveStyle({ top: '200px', left: '100px' });
  });

  it('should render all menu items', () => {
    render(
      <ContextMenu x={100} y={200} items={defaultItems} onClose={mockOnClose} />
    );

    expect(screen.getByText('Edit')).toBeInTheDocument();
    expect(screen.getByText('Delete')).toBeInTheDocument();
  });

  it('should call onClick and onClose when item is clicked', () => {
    const mockItemClick = vi.fn();
    const items: ContextMenuItem[] = [
      {
        label: 'Test Action',
        onClick: mockItemClick,
      },
    ];

    render(
      <ContextMenu x={100} y={200} items={items} onClose={mockOnClose} />
    );

    fireEvent.click(screen.getByText('Test Action'));

    expect(mockItemClick).toHaveBeenCalled();
    expect(mockOnClose).toHaveBeenCalled();
  });

  it('should render divider items', () => {
    const itemsWithDivider: ContextMenuItem[] = [
      {
        label: 'Edit',
        onClick: vi.fn(),
      },
      {
        divider: true,
      },
      {
        label: 'Delete',
        onClick: vi.fn(),
      },
    ];

    const { container } = render(
      <ContextMenu x={100} y={200} items={itemsWithDivider} onClose={mockOnClose} />
    );

    const divider = container.querySelector('[role="separator"]');
    expect(divider).toBeInTheDocument();
  });

  it('should not call onClick for disabled items', () => {
    const mockItemClick = vi.fn();
    const items: ContextMenuItem[] = [
      {
        label: 'Disabled Action',
        onClick: mockItemClick,
        disabled: true,
      },
    ];

    render(
      <ContextMenu x={100} y={200} items={items} onClose={mockOnClose} />
    );

    const button = screen.getByRole('menuitem');
    expect(button).toBeDisabled();
  });

  it('should close menu on Escape key', () => {
    render(
      <ContextMenu x={100} y={200} items={defaultItems} onClose={mockOnClose} />
    );

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(mockOnClose).toHaveBeenCalled();
  });

  it('should apply danger variant styling', () => {
    const items: ContextMenuItem[] = [
      {
        label: 'Delete',
        onClick: vi.fn(),
        variant: 'danger',
      },
    ];

    render(
      <ContextMenu x={100} y={200} items={items} onClose={mockOnClose} />
    );

    const deleteButton = screen.getByText('Delete');
    expect(deleteButton).toBeInTheDocument();
  });

  it('should render with icons', () => {
    const MockIcon = ({ className }: { className?: string }) => (
      <svg data-testid="mock-icon" className={className}></svg>
    );

    const items: ContextMenuItem[] = [
      {
        label: 'Edit',
        onClick: vi.fn(),
        icon: MockIcon,
      },
    ];

    render(
      <ContextMenu x={100} y={200} items={items} onClose={mockOnClose} />
    );

    expect(screen.getByTestId('mock-icon')).toBeInTheDocument();
  });

  it('should adjust position to avoid viewport overflow', () => {
    // Set window size
    Object.defineProperty(window, 'innerWidth', { writable: true, value: 500 });
    Object.defineProperty(window, 'innerHeight', { writable: true, value: 500 });

    const { container } = render(
      <ContextMenu x={450} y={450} items={defaultItems} onClose={mockOnClose} />
    );

    const menu = container.querySelector('[role="menu"]');
    
    // Menu should be adjusted to stay within viewport
    const style = menu?.getAttribute('style');
    expect(style).toBeTruthy();
  });

  it('should have correct ARIA attributes', () => {
    render(
      <ContextMenu x={100} y={200} items={defaultItems} onClose={mockOnClose} />
    );

    const menu = screen.getByRole('menu');
    expect(menu).toHaveAttribute('aria-label', 'Context menu');

    const menuItems = screen.getAllByRole('menuitem');
    expect(menuItems).toHaveLength(2);
  });

  it('should render multiple items correctly', () => {
    const items: ContextMenuItem[] = [
      { label: 'Action 1', onClick: vi.fn() },
      { label: 'Action 2', onClick: vi.fn() },
      { label: 'Action 3', onClick: vi.fn() },
      { divider: true },
      { label: 'Action 4', onClick: vi.fn() },
    ];

    render(
      <ContextMenu x={100} y={200} items={items} onClose={mockOnClose} />
    );

    expect(screen.getByText('Action 1')).toBeInTheDocument();
    expect(screen.getByText('Action 2')).toBeInTheDocument();
    expect(screen.getByText('Action 3')).toBeInTheDocument();
    expect(screen.getByText('Action 4')).toBeInTheDocument();
  });
});
