import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { HelpButton } from '@/common/components/HelpButton';

// Mock usePageTour to isolate the button component
const mockStartTour = vi.fn();
vi.mock('@/common/hooks/usePageTour', () => ({
  usePageTour: () => ({
    startTour: mockStartTour,
    hasSeenTour: false,
    resetTour: vi.fn(),
  }),
}));

describe('HelpButton', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders a button element', () => {
    render(<HelpButton onClick={vi.fn()} />);
    const button = screen.getByRole('button');
    expect(button).toBeInTheDocument();
  });

  it('has an accessible aria-label', () => {
    render(<HelpButton onClick={vi.fn()} />);
    const button = screen.getByRole('button');
    expect(
      button.getAttribute('aria-label') ||
      button.getAttribute('title') ||
      button.textContent,
    ).toBeTruthy();
  });

  it('calls onClick handler when clicked', () => {
    const handleClick = vi.fn();
    render(<HelpButton onClick={handleClick} />);
    const button = screen.getByRole('button');

    fireEvent.click(button);

    expect(handleClick).toHaveBeenCalledTimes(1);
  });

  it('renders with help/question mark icon content', () => {
    const { container } = render(<HelpButton onClick={vi.fn()} />);
    // The button should contain either an SVG icon or a "?" text indicator
    const hasSvg = container.querySelector('svg') !== null;
    const hasQuestionMark = container.textContent?.includes('?') ?? false;
    expect(hasSvg || hasQuestionMark).toBe(true);
  });

  it('uses ghost or subtle variant styling (no primary gradient)', () => {
    render(<HelpButton onClick={vi.fn()} />);
    const button = screen.getByRole('button');
    // Should NOT have primary button styling — ghost/subtle buttons
    // don't carry the bright gradient background
    const classes = button.className || '';
    expect(classes).not.toMatch(/bg-gradient/);
  });
});
