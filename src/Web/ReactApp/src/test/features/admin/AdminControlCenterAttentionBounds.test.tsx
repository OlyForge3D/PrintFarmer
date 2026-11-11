import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { MemoryRouter } from 'react-router';
import { AdminAttentionPanel } from '@/features/admin/components';
import type { AttentionItemDto } from '@/types/adminOverview';

const access = { hasRole: () => true, hasPermission: () => true };

function printerIssues(count: number): AttentionItemDto[] {
  return Array.from({ length: count }, (_, index) => ({
    key: `printer-00000000-0000-0000-0000-${String(index).padStart(12, '0')}-unreachable`,
    severity: 'Warning',
    title: `Printer ${index} is unreachable`,
    detail: 'No connection.',
    actionLabel: 'Open printer',
    actionRoute: `/printers/${index}`,
  }));
}

function panel(items: AttentionItemDto[]) {
  return <MemoryRouter><AdminAttentionPanel items={items} access={access} labelledBy="issues-heading" /></MemoryRouter>;
}

describe('Operational issues grouped disclosures', () => {
  it.each([1, 4, 25, 50])('groups %i unexpected offline printers without hiding the count', count => {
    render(panel(printerIssues(count)));
    const groups = screen.getAllByTestId('admin-hub-issue-group');
    expect(groups).toHaveLength(1);
    expect(groups[0]).not.toHaveAttribute('open');
    expect(within(groups[0]).getByText('Unexpected offline printers')).toBeInTheDocument();
    expect(within(groups[0]).getByText(`${count} Warning`)).toBeInTheDocument();
    expect(screen.getByTestId('admin-hub-attention-summary')).toHaveTextContent(`${count} operational`);
    expect(screen.getAllByTestId('admin-hub-attention-item')).toHaveLength(count);
  });

  it('keeps critical errors expanded and reports every severity', () => {
    const error: AttentionItemDto = {
      key: 'health-database', severity: 'Error', title: 'Database failed', detail: 'Cannot connect.',
    };
    render(panel([error, ...printerIssues(25)]));
    const groups = screen.getAllByTestId('admin-hub-issue-group');
    expect(groups).toHaveLength(2);
    expect(groups[0]).toHaveAttribute('open');
    expect(groups[0]).toHaveTextContent('Database failed');
    expect(screen.getByTestId('admin-hub-attention-summary')).toHaveTextContent('26 operational issues: 1 Error, 25 Warnings');
  });

  it('uses native keyboard-focusable summaries and contextual links', async () => {
    const user = userEvent.setup();
    render(panel(printerIssues(4)));
    const group = screen.getByTestId('admin-hub-issue-group');
    const summary = group.querySelector('summary')!;
    await user.tab();
    expect(summary).toHaveFocus();
    // jsdom does not implement keyboard activation of native details.
    await user.click(summary);
    expect(group).toHaveAttribute('open');
    await user.tab();
    expect(screen.getByRole('link', { name: 'Open printer: Printer 0 is unreachable' })).toHaveFocus();
    expect(screen.getByRole('link', { name: 'Open printer: Printer 0 is unreachable' })).toHaveAttribute('href', '/printers/0');
  });

  it('preserves expanded groups during refresh and recovers focus when a row disappears', async () => {
    const user = userEvent.setup();
    const items = printerIssues(3);
    const { rerender } = render(panel(items));
    await user.click(screen.getByText('Unexpected offline printers'));
    const link = screen.getByRole('link', { name: 'Open printer: Printer 0 is unreachable' });
    link.focus();
    await act(async () => { rerender(panel(items.slice(1))); });
    expect(screen.getByTestId('admin-hub-issue-group')).toHaveAttribute('open');
    expect(screen.getByTestId('admin-hub-attention-panel')).toHaveFocus();
  });

  it('does not steal focus outside the panel on refresh', () => {
    const { rerender } = render(<><button>Refresh</button>{panel(printerIssues(3))}</>);
    screen.getByRole('button', { name: 'Refresh' }).focus();
    rerender(<><button>Refresh</button>{panel(printerIssues(2))}</>);
    expect(screen.getByRole('button', { name: 'Refresh' })).toHaveFocus();
  });

  it('recovers focus when the last issue clears', async () => {
    const user = userEvent.setup();
    const { rerender } = render(panel(printerIssues(1)));
    await user.click(screen.getByText('Unexpected offline printers'));
    screen.getByRole('link').focus();
    rerender(panel([]));
    expect(screen.getByTestId('admin-hub-attention-panel')).toHaveFocus();
    expect(screen.queryByTestId('admin-hub-attention')).not.toBeInTheDocument();
  });

  it('keeps unknown severity visible and blocks unsafe and unauthorized navigation', () => {
    render(panel([
      { key: 'unknown', severity: 'Future' as AttentionItemDto['severity'], title: 'Future issue', detail: '', actionRoute: 'javascript:alert(1)', actionLabel: 'Unsafe' },
      { key: 'self', severity: 'Warning', title: 'Self issue', detail: '', actionRoute: '/admin', actionDestinationId: 'admin-control-center', actionLabel: 'Self' },
    ]));
    expect(screen.getByTestId('admin-hub-attention-summary')).toHaveTextContent('1 unknown severity');
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });
});
