import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { ThemeSwitcher } from '@/common/components/ThemeSwitcher';
import type { NewTheme } from '@/common/hooks/useTheme';

const setTheme = vi.fn();
const useThemeMock = vi.fn();

vi.mock('@/common/hooks/useTheme', () => ({
  useTheme: () => useThemeMock(),
}));

describe('ThemeSwitcher', () => {
  beforeEach(() => {
    setTheme.mockReset();
    useThemeMock.mockReturnValue({
      theme: 'matrix' satisfies NewTheme,
      setTheme,
    });
  });

  it('keeps only the active radio in the tab order', () => {
    render(<ThemeSwitcher />);

    expect(screen.getByRole('radio', { name: /Matrix/ })).toHaveAttribute('tabindex', '0');
    expect(screen.getByRole('radio', { name: /Dark/ })).toHaveAttribute('tabindex', '-1');
    expect(screen.getByRole('radio', { name: /Blueprint/ })).toHaveAttribute('tabindex', '-1');
  });

  it('moves focus and selection with arrow keys', () => {
    render(<ThemeSwitcher />);

    const activeRadio = screen.getByRole('radio', { name: /Matrix/ });
    const nextRadio = screen.getByRole('radio', { name: /Blueprint/ });

    activeRadio.focus();
    fireEvent.keyDown(activeRadio, { key: 'ArrowRight' });

    expect(setTheme).toHaveBeenCalledWith('blueprint');
    expect(nextRadio).toHaveFocus();
  });
});
