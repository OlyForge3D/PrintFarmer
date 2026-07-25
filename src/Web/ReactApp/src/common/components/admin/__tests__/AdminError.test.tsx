import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { AdminError } from '../AdminError';

describe('AdminError', () => {
  it('exposes role=alert for AT announcements', () => {
    render(<AdminError />);
    expect(screen.getByRole('alert')).toBeInTheDocument();
  });

  it('renders default title when none provided', () => {
    render(<AdminError />);
    expect(screen.getByRole('heading', { level: 3, name: 'Something went wrong' })).toBeInTheDocument();
  });

  it('renders custom title and description', () => {
    render(<AdminError title="Fetch failed" description="Backend unreachable." />);
    expect(screen.getByRole('heading', { name: 'Fetch failed' })).toBeInTheDocument();
    expect(screen.getByText('Backend unreachable.')).toBeInTheDocument();
  });

  it('hides retry button when onRetry is not provided', () => {
    render(<AdminError />);
    expect(screen.queryByRole('button', { name: /try again/i })).not.toBeInTheDocument();
  });

  it('shows retry button and fires callback when provided', async () => {
    const user = userEvent.setup();
    const onRetry = vi.fn();
    render(<AdminError onRetry={onRetry} />);
    const btn = screen.getByRole('button', { name: /try again/i });
    await user.click(btn);
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('honours a custom retry label', () => {
    render(<AdminError onRetry={() => undefined} retryLabel="Reload" />);
    expect(screen.getByRole('button', { name: 'Reload' })).toBeInTheDocument();
  });

  it('renders error message inside a collapsible disclosure', async () => {
    const user = userEvent.setup();
    render(<AdminError error={new Error('boom')} />);
    // details closed by default
    const summary = screen.getByText(/show details/i);
    expect(summary).toBeInTheDocument();
    // opening reveals the error text
    await user.click(summary);
    expect(screen.getByText(/boom/)).toBeInTheDocument();
  });

  it('stringifies non-Error values into the disclosure', async () => {
    const user = userEvent.setup();
    render(<AdminError error={{ status: 500, message: 'nope' }} />);
    await user.click(screen.getByText(/show details/i));
    expect(screen.getByText(/nope/)).toBeInTheDocument();
    expect(screen.getByText(/500/)).toBeInTheDocument();
  });

  it('omits the disclosure entirely when no error payload is passed', () => {
    render(<AdminError title="oops" />);
    expect(screen.queryByText(/show details/i)).not.toBeInTheDocument();
  });
});
