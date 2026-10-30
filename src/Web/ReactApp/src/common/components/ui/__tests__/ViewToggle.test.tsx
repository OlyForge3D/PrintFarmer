import { act, render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { ViewToggle } from '../ViewToggle';
import { viewKindIcons, type ViewKind, type ViewModeOption } from '../viewModeIcons';

type TestMode = 'grid' | 'table' | 'list';

const mockOptions: ViewModeOption<TestMode>[] = [
  { mode: 'grid', kind: 'cards', title: 'Grid view' },
  { mode: 'table', kind: 'table', title: 'Table view' },
  { mode: 'list', kind: 'list', title: 'List view' },
];

const rawIconPathOption: ViewModeOption<'grid'> = {
  mode: 'grid',
  // @ts-expect-error Raw SVG paths are not semantic view kinds.
  kind: 'M3,11H11V3H3',
  title: 'Grid view',
};
void rawIconPathOption;

describe('ViewToggle', () => {
  it('should render all view options as buttons', () => {
    render(
      <ViewToggle value="grid" onChange={() => {}} options={mockOptions} />
    );
    
    expect(screen.getAllByRole('button')).toHaveLength(3);
    expect(screen.getByRole('button', { name: 'Grid view' })).toHaveAttribute('data-size', 'sm');
  });

  it('should use an explicitly requested button size', () => {
    render(
      <ViewToggle value="grid" onChange={() => {}} options={mockOptions} size="md" />
    );

    expect(screen.getByRole('button', { name: 'Grid view' })).toHaveAttribute('data-size', 'md');
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

  it('should render named icon-only buttons with pressed state and no native title', () => {
    render(
      <ViewToggle value="grid" onChange={() => {}} options={mockOptions} />
    );

    for (const option of mockOptions) {
      const button = screen.getByRole('button', { name: option.title });
      expect(button).toHaveAttribute('aria-pressed', String(option.mode === 'grid'));
      expect(button).not.toHaveAttribute('title');
      expect(button).toHaveTextContent('');
      expect(button.querySelector('svg')).toBeInTheDocument();
    }
  });

  it('should show option titles in a tooltip on keyboard focus', () => {
    render(
      <ViewToggle value="grid" onChange={() => {}} options={mockOptions} />
    );
    const button = screen.getByRole('button', { name: 'Grid view' });

    fireEvent.focus(button);

    const tooltip = screen.getByRole('tooltip', { hidden: true });
    expect(tooltip).toHaveTextContent('Grid view');
    expect(tooltip).toHaveAttribute('aria-hidden', 'true');
    expect(button).not.toHaveAttribute('aria-describedby');
  });

  it('should show option titles in a tooltip on hover', async () => {
    vi.useFakeTimers();
    try {
      render(
        <ViewToggle value="grid" onChange={() => {}} options={mockOptions} />
      );
      const button = screen.getByRole('button', { name: 'Grid view' });
      fireEvent.mouseEnter(button.parentElement!);

      await act(async () => {
        vi.advanceTimersByTime(200);
      });

      expect(screen.getByRole('tooltip', { hidden: true })).toHaveTextContent('Grid view');
    } finally {
      vi.useRealTimers();
    }
  });

  it('should handle empty options array', () => {
    render(
      <ViewToggle value="grid" onChange={() => {}} options={[]} />
    );
    
    expect(screen.getByRole('group')).toBeInTheDocument();
    expect(screen.queryAllByRole('button')).toHaveLength(0);
  });

  it('assigns a distinct icon to every semantic view kind', () => {
    const icons = Object.values(viewKindIcons);
    expect(new Set(icons).size).toBe(icons.length);
  });

  it('renders each semantic view kind with its registered glyph', () => {
    const semanticKinds = [
      'cards',
      'table',
      'list',
      'compactCards',
      'detailedCards',
      'explorer',
      'locationGroups',
      'printerGroups',
    ] as const satisfies readonly ViewKind[];
    expect([...semanticKinds].sort()).toEqual(Object.keys(viewKindIcons).sort());
    const options: ViewModeOption[] = semanticKinds.map((kind) => ({
      mode: kind,
      kind,
      title: `${kind} view`,
    }));

    render(<ViewToggle value="cards" onChange={() => {}} options={options} />);

    for (const option of options) {
      expect(
        screen.getByRole('button', { name: option.title }).querySelector('path'),
      ).toHaveAttribute('d', viewKindIcons[option.kind]);
    }
  });

  it('should work with two options', () => {
    const twoOptions: ViewModeOption<'grid' | 'table'>[] = [
      { mode: 'grid', kind: 'cards', title: 'Grid' },
      { mode: 'table', kind: 'table', title: 'Table' },
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
