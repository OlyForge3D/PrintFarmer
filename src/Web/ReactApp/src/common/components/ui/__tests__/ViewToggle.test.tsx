import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { ViewToggle } from '../ViewToggle';
import { type ViewModeOption } from '../viewModeIcons';

type TestMode = 'grid' | 'table' | 'list';

const mockOptions: ViewModeOption<TestMode>[] = [
  { mode: 'grid', icon: 'grid', title: 'Grid view' },
  { mode: 'table', icon: 'table', title: 'Table view' },
  { mode: 'list', icon: 'list', title: 'List view' },
];

describe('ViewToggle', () => {
  it('should render all view options as buttons', () => {
    render(
      <ViewToggle value="grid" onChange={() => {}} options={mockOptions} />
    );
    
    expect(screen.getAllByRole('button')).toHaveLength(3);
  });

  it('should render with role group', () => {
    render(
      <ViewToggle value="grid" onChange={() => {}} options={mockOptions} />
    );
    
    expect(screen.getByRole('group')).toBeInTheDocument();
  });

  it('should apply aria-label to group', () => {
    render(
      <ViewToggle 
        value="grid" 
        onChange={() => {}} 
        options={mockOptions} 
        ariaLabel="Choose display mode"
      />
    );
    
    expect(screen.getByRole('group')).toHaveAttribute('aria-label', 'Choose display mode');
  });

  it('should use default aria-label when not provided', () => {
    render(
      <ViewToggle value="grid" onChange={() => {}} options={mockOptions} />
    );
    
    expect(screen.getByRole('group')).toHaveAttribute('aria-label', 'View mode toggle');
  });

  it('should call onChange when option is clicked', () => {
    const handleChange = vi.fn();
    render(
      <ViewToggle value="grid" onChange={handleChange} options={mockOptions} />
    );
    
    // Click the table view button (second button)
    const buttons = screen.getAllByRole('button');
    fireEvent.click(buttons[1]);
    
    expect(handleChange).toHaveBeenCalledWith('table');
  });

  it('should apply custom className to container', () => {
    render(
      <ViewToggle 
        value="grid" 
        onChange={() => {}} 
        options={mockOptions} 
        className="custom-toggle"
      />
    );
    
    const group = screen.getByRole('group');
    expect(group).toHaveClass('custom-toggle');
  });

  it('should render buttons with title attribute from options', () => {
    render(
      <ViewToggle value="grid" onChange={() => {}} options={mockOptions} />
    );
    
    expect(screen.getByTitle('Grid view')).toBeInTheDocument();
    expect(screen.getByTitle('Table view')).toBeInTheDocument();
    expect(screen.getByTitle('List view')).toBeInTheDocument();
  });

  it('should handle empty options array', () => {
    render(
      <ViewToggle value="grid" onChange={() => {}} options={[]} />
    );
    
    expect(screen.getByRole('group')).toBeInTheDocument();
    expect(screen.queryAllByRole('button')).toHaveLength(0);
  });

  it('should work with two options', () => {
    const twoOptions: ViewModeOption<'grid' | 'table'>[] = [
      { mode: 'grid', icon: 'grid', title: 'Grid' },
      { mode: 'table', icon: 'table', title: 'Table' },
    ];
    
    render(
      <ViewToggle value="grid" onChange={() => {}} options={twoOptions} />
    );
    
    expect(screen.getAllByRole('button')).toHaveLength(2);
  });

  it('should call onChange with different option values', () => {
    const handleChange = vi.fn();
    render(
      <ViewToggle value="grid" onChange={handleChange} options={mockOptions} />
    );
    
    const buttons = screen.getAllByRole('button');
    
    fireEvent.click(buttons[0]);
    expect(handleChange).toHaveBeenCalledWith('grid');
    
    fireEvent.click(buttons[1]);
    expect(handleChange).toHaveBeenCalledWith('table');
    
    fireEvent.click(buttons[2]);
    expect(handleChange).toHaveBeenCalledWith('list');
  });

  it('should render SVG icons in buttons', () => {
    render(
      <ViewToggle value="grid" onChange={() => {}} options={mockOptions} />
    );
    
    const buttons = screen.getAllByRole('button');
    buttons.forEach(button => {
      expect(button.querySelector('svg')).toBeInTheDocument();
    });
  });
});
