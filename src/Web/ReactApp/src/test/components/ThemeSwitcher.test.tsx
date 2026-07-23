import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
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

  it('uses the on-accent token for the active theme chip', () => {
    render(<ThemeSwitcher />);

    const activeChip = screen.getByRole('radio', { name: /Matrix/ });
    expect(activeChip.className).toContain('bg-[var(--pf-accent-bg)]');
    expect(activeChip.className).toContain('text-[var(--pf-on-accent)]');
  });

  it('renders a live preview for the active theme', () => {
    render(<ThemeSwitcher />);

    expect(screen.getByText('Matrix live preview')).toBeInTheDocument();
    expect(screen.getByText('Farm overview')).toBeInTheDocument();
  });
});
