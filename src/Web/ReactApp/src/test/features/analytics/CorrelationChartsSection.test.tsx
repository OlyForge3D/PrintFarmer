import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CorrelationChartsSection } from '@/features/analytics/components/CorrelationChartsSection';

const { get } = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock('@/services/api', () => ({ apiClient: { get } }));

function renderCharts(days = 30) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const renderWithDays = (value: number) => (
    <QueryClientProvider client={client}>
      <CorrelationChartsSection days={value} />
    </QueryClientProvider>
  );
  return { ...render(renderWithDays(days)), renderWithDays, client };
}

describe('CorrelationChartsSection', () => {
  beforeEach(() => {
    get.mockReset();
    get.mockResolvedValue({ data: [] });
  });

  it('fetches only selected charts and reuses their fresh cache', async () => {
    const user = userEvent.setup();
    renderCharts();
    await waitFor(() => expect(get).toHaveBeenCalledTimes(1));
    expect(get).toHaveBeenNthCalledWith(1, '/correlation-analytics/material-success-rates?days=30');

    const tabs = [
      ['Printer × Material', 'printer-material-performance'],
      ['Temperature', 'temperature-quality'],
      ['Duration Trends', 'duration-trends'],
      ['Failure Reasons', 'failure-reasons'],
    ];
    for (const [label, endpoint] of tabs) {
      await user.click(screen.getByRole('tab', { name: label }));
      await waitFor(() => expect(get).toHaveBeenCalledWith(`/correlation-analytics/${endpoint}?days=30`));
    }
    expect(get).toHaveBeenCalledTimes(5);
    await user.click(screen.getByRole('tab', { name: 'Material Success' }));
    expect(get).toHaveBeenCalledTimes(5);
  });

  it('fetches the new period only for the active chart until another is opened', async () => {
    const user = userEvent.setup();
    const { rerender, renderWithDays } = renderCharts();
    await waitFor(() => expect(get).toHaveBeenCalledTimes(1));
    await user.click(screen.getByRole('tab', { name: 'Temperature' }));
    await waitFor(() => expect(get).toHaveBeenCalledTimes(2));
    rerender(renderWithDays(7));
    await waitFor(() => expect(get).toHaveBeenCalledTimes(3));
    expect(get).toHaveBeenLastCalledWith('/correlation-analytics/temperature-quality?days=7');
    await user.click(screen.getByRole('tab', { name: 'Material Success' }));
    await waitFor(() => expect(get).toHaveBeenCalledTimes(4));
    expect(get).toHaveBeenLastCalledWith('/correlation-analytics/material-success-rates?days=7');
  });

  it('shows loading and errors and selects charts using the keyboard', async () => {
    let rejectRequest: (error: Error) => void = () => {};
    get.mockImplementation(() => new Promise((_resolve, reject) => { rejectRequest = reject; }));
    renderCharts();
    expect(screen.getByLabelText('Loading chart')).toBeInTheDocument();
    rejectRequest(new Error('Unavailable'));
    expect(await screen.findByText('Error loading data')).toBeInTheDocument();
    get.mockResolvedValue({ data: [] });
    const first = screen.getByRole('tab', { name: 'Material Success' });
    first.focus();
    fireEvent.keyDown(first, { key: 'ArrowRight' });
    const next = screen.getByRole('tab', { name: 'Printer × Material' });
    expect(next).toHaveFocus();
    expect(next).toHaveAttribute('aria-selected', 'true');
    await waitFor(() => expect(get).toHaveBeenLastCalledWith('/correlation-analytics/printer-material-performance?days=30'));
  });
});
