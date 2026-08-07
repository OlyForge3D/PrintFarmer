import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TagChip } from '@/common/components/ui/TagChip';
import { getTagChipForeground } from '@/common/components/ui/tag-chip-colors';
import { getContrastRatio } from '@/common/utils/accessibility';

describe('TagChip', () => {
  it('renders a display chip with the centralized tag shape and sizing', () => {
    render(<TagChip label="Resin" />);

    const chip = screen.getByText('Resin').closest('[data-pf-radius="full"]');
    expect(chip).toHaveClass('rounded-full', 'min-h-5', 'px-2', 'py-0.5');
  });

  it('applies a custom tag color with a readable foreground', () => {
    render(<TagChip label="Safety yellow" color="#ffff00" />);

    const chip = screen.getByText('Safety yellow').closest('[data-pf-radius="full"]');
    expect(chip).toHaveStyle({
      backgroundColor: '#ffff00',
      borderColor: '#ffff00',
      color: '#000000',
    });
    expect(getContrastRatio(getTagChipForeground('#ffff00'), '#ffff00')).toBeGreaterThanOrEqual(4.5);
  });

  it('exposes an accessible status name for announced display tags', () => {
    render(<TagChip label="PLA" statusLabel="Tag: PLA" />);
    expect(screen.getByRole('status', { name: 'Tag: PLA' })).toBeInTheDocument();
  });

  it('uses native keyboard activation for action tags', async () => {
    const onClick = vi.fn();
    const user = userEvent.setup();
    render(<TagChip mode="action" label="+ material:PLA" onClick={onClick} />);

    const chip = screen.getByRole('button', { name: '+ material:PLA' });
    chip.focus();
    await user.keyboard('{Enter}');

    expect(onClick).toHaveBeenCalledOnce();
  });

  it('exposes selection state for toggleable action tags', () => {
    render(<TagChip mode="action" label="PLA" pressed onClick={() => undefined} />);
    expect(screen.getByRole('button', { name: 'PLA' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('removes a tag through its named remove control', async () => {
    const onRemove = vi.fn();
    const user = userEvent.setup();
    render(
      <TagChip
        mode="removable"
        label="Prototype"
        onRemove={onRemove}
        removeLabel="Remove tag Prototype"
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Remove tag Prototype' }));
    expect(onRemove).toHaveBeenCalledOnce();
  });

  it('preserves keyboard removal shortcuts on an actionable removable tag', async () => {
    const onClick = vi.fn();
    const onRemove = vi.fn();
    const user = userEvent.setup();
    render(
      <TagChip
        mode="removable"
        label="Shortcut tag"
        onClick={onClick}
        onRemove={onRemove}
        removeLabel="Remove Shortcut tag"
      />,
    );

    const action = screen.getByRole('button', { name: 'Shortcut tag' });
    action.focus();
    await user.keyboard('{Control>}{Enter}{/Control}');
    await user.keyboard('{Delete}');

    expect(onRemove).toHaveBeenCalledTimes(2);
    expect(onClick).not.toHaveBeenCalled();
  });

  it('disables activation and removal behavior', async () => {
    const onClick = vi.fn();
    const onRemove = vi.fn();
    const user = userEvent.setup();
    render(
      <>
        <TagChip mode="action" label="Disabled action" disabled onClick={onClick} />
        <TagChip
          mode="removable"
          label="Disabled removable"
          disabled
          onRemove={onRemove}
          removeLabel="Remove disabled tag"
        />
      </>,
    );

    await user.click(screen.getByRole('button', { name: 'Disabled action' }));
    await user.click(screen.getByRole('button', { name: 'Remove disabled tag' }));
    expect(onClick).not.toHaveBeenCalled();
    expect(onRemove).not.toHaveBeenCalled();
  });

  it('renders removable activation and removal as sibling controls, never nested buttons', () => {
    const { container } = render(
      <TagChip
        mode="removable"
        label="Clickable"
        onClick={() => undefined}
        onRemove={() => undefined}
        removeLabel="Remove Clickable"
      />,
    );

    expect(screen.getAllByRole('button')).toHaveLength(2);
    expect(container.querySelector('button button')).toBeNull();
  });

  it('does not bubble removal clicks to containing interactive surfaces', async () => {
    const onParentClick = vi.fn();
    const onRemove = vi.fn();
    const user = userEvent.setup();
    render(
      <div onClick={onParentClick}>
        <TagChip
          mode="removable"
          label="Contained"
          onRemove={onRemove}
          removeLabel="Remove Contained"
        />
      </div>,
    );

    await user.click(screen.getByRole('button', { name: 'Remove Contained' }));
    expect(onRemove).toHaveBeenCalledOnce();
    expect(onParentClick).not.toHaveBeenCalled();
  });

  it('merges caller classes and non-paint styles while retaining controlled color treatment', () => {
    render(
      <TagChip
        label="Merged"
        color="#123456"
        className="max-w-24 uppercase"
        style={{ marginTop: '3px', color: '#ff00ff' }}
      />,
    );

    const chip = screen.getByText('Merged').closest('[data-pf-radius="full"]');
    expect(chip).toHaveClass('max-w-24', 'uppercase', 'rounded-full');
    expect(chip).toHaveStyle({ marginTop: '3px', backgroundColor: '#123456' });
    expect(chip).not.toHaveStyle({ color: '#ff00ff' });
  });
});
