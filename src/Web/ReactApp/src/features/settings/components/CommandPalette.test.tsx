import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { CommandPalette } from '@/features/settings/components/CommandPalette';
import type { SettingsCommandItem } from '@/features/settings/settings-navigation';

const items: SettingsCommandItem[] = [
  {
    id: 'hardware.cameras',
    categoryId: 'hardware',
    subPageId: 'cameras',
    label: 'Cameras',
    description: 'Manage printer cameras.',
    breadcrumb: 'Settings / Hardware / Cameras',
    keywords: ['camera', 'hardware'],
  },
  {
    id: 'notifications.email',
    categoryId: 'notifications',
    subPageId: 'email',
    label: 'Email Notifications',
    description: 'Configure alert emails.',
    breadcrumb: 'Settings / Notifications / Email',
    keywords: ['email', 'alert', 'notification'],
  },
];

describe('CommandPalette', () => {
  beforeEach(() => {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: vi.fn().mockImplementation(() => ({
        matches: false,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    });
  });

  it('keeps listbox semantics on non-button options and activates through the combobox input', () => {
    const onSelect = vi.fn();

    render(
      <CommandPalette
        isOpen
        items={items}
        onClose={vi.fn()}
        onSelect={onSelect}
      />,
    );

    const input = screen.getByRole('combobox', { name: 'Search settings command palette' });
    const listbox = screen.getByRole('listbox', { name: 'Settings search results' });
    const [firstOption, secondOption] = within(listbox).getAllByRole('option');

    expect(firstOption.tagName).toBe('DIV');
    expect(secondOption.tagName).toBe('DIV');
    expect(within(listbox).queryByRole('button')).not.toBeInTheDocument();

    input.focus();
    fireEvent.keyDown(input, { key: 'ArrowDown' });

    expect(input).toHaveFocus();
    expect(input).toHaveAttribute('aria-activedescendant', secondOption.id);
    expect(secondOption).toHaveAttribute('aria-selected', 'true');

    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onSelect).toHaveBeenCalledWith(items[1]);
  });

  it('keeps typing focus in the filter field after arrow navigation', () => {
    render(
      <CommandPalette
        isOpen
        items={items}
        onClose={vi.fn()}
        onSelect={vi.fn()}
      />,
    );

    const input = screen.getByRole('combobox', { name: 'Search settings command palette' });

    input.focus();
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.change(input, { target: { value: 'email' } });

    expect(input).toHaveFocus();
    expect(input).toHaveValue('email');
  });

  it('renders keyboard hint text in the footer', () => {
    render(
      <CommandPalette
        isOpen
        items={items}
        onClose={vi.fn()}
        onSelect={vi.fn()}
      />,
    );

    expect(screen.getByText('↑↓ navigate · ↵ open · esc close')).toBeInTheDocument();
  });
});
