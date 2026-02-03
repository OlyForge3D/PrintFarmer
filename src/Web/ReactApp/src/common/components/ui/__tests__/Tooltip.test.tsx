import { render, screen, fireEvent, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { Tooltip } from '../Tooltip';

describe('Tooltip', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('should render children without tooltip initially', () => {
    render(
      <Tooltip content="Helpful text">
        <button>Hover me</button>
      </Tooltip>
    );
    
    expect(screen.getByText('Hover me')).toBeInTheDocument();
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
  });

  it('should show tooltip on mouse enter after delay', async () => {
    render(
      <Tooltip content="Helpful text" delay={200}>
        <button>Hover me</button>
      </Tooltip>
    );
    
    const trigger = screen.getByText('Hover me').parentElement!;
    fireEvent.mouseEnter(trigger);
    
    // Tooltip should not appear immediately
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
    
    // Advance timer past delay
    await act(async () => {
      vi.advanceTimersByTime(200);
    });
    
    expect(screen.getByRole('tooltip')).toBeInTheDocument();
    expect(screen.getByText('Helpful text')).toBeInTheDocument();
  });

  it('should hide tooltip on mouse leave', async () => {
    render(
      <Tooltip content="Helpful text" delay={0}>
        <button>Hover me</button>
      </Tooltip>
    );
    
    const trigger = screen.getByText('Hover me').parentElement!;
    
    fireEvent.mouseEnter(trigger);
    await act(async () => {
      vi.advanceTimersByTime(0);
    });
    
    expect(screen.getByRole('tooltip')).toBeInTheDocument();
    
    fireEvent.mouseLeave(trigger);
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
  });

  it('should show tooltip on focus', async () => {
    render(
      <Tooltip content="Focus text" delay={0}>
        <button>Focus me</button>
      </Tooltip>
    );
    
    const trigger = screen.getByText('Focus me').parentElement!;
    fireEvent.focus(trigger);
    
    await act(async () => {
      vi.advanceTimersByTime(0);
    });
    
    expect(screen.getByRole('tooltip')).toBeInTheDocument();
  });

  it('should hide tooltip on blur', async () => {
    render(
      <Tooltip content="Focus text" delay={0}>
        <button>Focus me</button>
      </Tooltip>
    );
    
    const trigger = screen.getByText('Focus me').parentElement!;
    fireEvent.focus(trigger);
    await act(async () => {
      vi.advanceTimersByTime(0);
    });
    
    expect(screen.getByRole('tooltip')).toBeInTheDocument();
    
    fireEvent.blur(trigger);
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
  });

  it('should not show tooltip when disabled', async () => {
    render(
      <Tooltip content="Disabled text" disabled delay={0}>
        <button>Cannot tooltip</button>
      </Tooltip>
    );
    
    const trigger = screen.getByText('Cannot tooltip').parentElement!;
    fireEvent.mouseEnter(trigger);
    
    await act(async () => {
      vi.advanceTimersByTime(0);
    });
    
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
  });

  it('should render tooltip at top position by default', async () => {
    render(
      <Tooltip content="Top tooltip" delay={0}>
        <button>Button</button>
      </Tooltip>
    );
    
    const trigger = screen.getByText('Button').parentElement!;
    fireEvent.mouseEnter(trigger);
    await act(async () => {
      vi.advanceTimersByTime(0);
    });
    
    const tooltip = screen.getByRole('tooltip');
    expect(tooltip).toHaveClass('bottom-full');
  });

  it('should render tooltip at bottom position', async () => {
    render(
      <Tooltip content="Bottom tooltip" position="bottom" delay={0}>
        <button>Button</button>
      </Tooltip>
    );
    
    const trigger = screen.getByText('Button').parentElement!;
    fireEvent.mouseEnter(trigger);
    await act(async () => {
      vi.advanceTimersByTime(0);
    });
    
    const tooltip = screen.getByRole('tooltip');
    expect(tooltip).toHaveClass('top-full');
  });

  it('should render tooltip at left position', async () => {
    render(
      <Tooltip content="Left tooltip" position="left" delay={0}>
        <button>Button</button>
      </Tooltip>
    );
    
    const trigger = screen.getByText('Button').parentElement!;
    fireEvent.mouseEnter(trigger);
    await act(async () => {
      vi.advanceTimersByTime(0);
    });
    
    const tooltip = screen.getByRole('tooltip');
    expect(tooltip).toHaveClass('right-full');
  });

  it('should render tooltip at right position', async () => {
    render(
      <Tooltip content="Right tooltip" position="right" delay={0}>
        <button>Button</button>
      </Tooltip>
    );
    
    const trigger = screen.getByText('Button').parentElement!;
    fireEvent.mouseEnter(trigger);
    await act(async () => {
      vi.advanceTimersByTime(0);
    });
    
    const tooltip = screen.getByRole('tooltip');
    expect(tooltip).toHaveClass('left-full');
  });

  it('should apply custom className to tooltip', async () => {
    render(
      <Tooltip content="Styled tooltip" className="custom-tooltip" delay={0}>
        <button>Button</button>
      </Tooltip>
    );
    
    const trigger = screen.getByText('Button').parentElement!;
    fireEvent.mouseEnter(trigger);
    await act(async () => {
      vi.advanceTimersByTime(0);
    });
    
    const tooltip = screen.getByRole('tooltip');
    expect(tooltip).toHaveClass('custom-tooltip');
  });

  it('should cancel timer on mouse leave before tooltip shows', async () => {
    render(
      <Tooltip content="Will not show" delay={500}>
        <button>Quick hover</button>
      </Tooltip>
    );
    
    const trigger = screen.getByText('Quick hover').parentElement!;
    fireEvent.mouseEnter(trigger);
    
    // Leave before delay completes
    await act(async () => {
      vi.advanceTimersByTime(200);
    });
    
    fireEvent.mouseLeave(trigger);
    
    // Continue past original delay
    await act(async () => {
      vi.advanceTimersByTime(500);
    });
    
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
  });

  it('should render ReactNode content', async () => {
    render(
      <Tooltip content={<span data-testid="complex-content">Complex <strong>content</strong></span>} delay={0}>
        <button>Button</button>
      </Tooltip>
    );
    
    const trigger = screen.getByText('Button').parentElement!;
    fireEvent.mouseEnter(trigger);
    await act(async () => {
      vi.advanceTimersByTime(0);
    });
    
    expect(screen.getByTestId('complex-content')).toBeInTheDocument();
  });

  it('should not render tooltip if content is empty', async () => {
    render(
      <Tooltip content="" delay={0}>
        <button>Button</button>
      </Tooltip>
    );
    
    const trigger = screen.getByText('Button').parentElement!;
    fireEvent.mouseEnter(trigger);
    await act(async () => {
      vi.advanceTimersByTime(0);
    });
    
    // With empty content, tooltip is still not in the document
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
  });
});
