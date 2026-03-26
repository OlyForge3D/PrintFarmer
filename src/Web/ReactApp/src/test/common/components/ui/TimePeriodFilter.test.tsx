import '@testing-library/jest-dom';
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { TimePeriodFilter } from '@/common/components/ui/TimePeriodFilter';
import type { TimePeriodFilterValue } from '@/common/components/ui/timePeriodOptions';

describe('TimePeriodFilter', () => {
  it('renders all preset buttons plus Custom', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'preset', days: 30 }}
        onChange={onChange}
      />
    );

    expect(screen.getByText('7 days')).toBeInTheDocument();
    expect(screen.getByText('30 days')).toBeInTheDocument();
    expect(screen.getByText('90 days')).toBeInTheDocument();
    expect(screen.getByText('1 year')).toBeInTheDocument();
    expect(screen.getByText('All time')).toBeInTheDocument();
    expect(screen.getByText('Custom')).toBeInTheDocument();
  });

  it('highlights the active preset button', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'preset', days: 30 }}
        onChange={onChange}
      />
    );

    const active = screen.getByRole('button', { name: '30 days' });
    expect(active).toHaveAttribute('aria-pressed', 'true');

    const inactive = screen.getByRole('button', { name: '7 days' });
    expect(inactive).toHaveAttribute('aria-pressed', 'false');
  });

  it('calls onChange with preset value when a preset button is clicked', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'preset', days: 30 }}
        onChange={onChange}
      />
    );

    fireEvent.click(screen.getByText('7 days'));
    expect(onChange).toHaveBeenCalledWith({ type: 'preset', days: 7 });
  });

  it('shows date inputs when Custom is selected', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'custom', startDate: '2025-01-01', endDate: '2025-01-31' }}
        onChange={onChange}
      />
    );

    expect(screen.getByLabelText('Start date')).toBeInTheDocument();
    expect(screen.getByLabelText('End date')).toBeInTheDocument();
    expect(screen.getByText('to')).toBeInTheDocument();
  });

  it('does not show date inputs in preset mode', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'preset', days: 30 }}
        onChange={onChange}
      />
    );

    expect(screen.queryByLabelText('Start date')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('End date')).not.toBeInTheDocument();
  });

  it('highlights Custom button when in custom mode', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'custom', startDate: '2025-01-01', endDate: '2025-01-31' }}
        onChange={onChange}
      />
    );

    const custom = screen.getByRole('button', { name: 'Custom' });
    expect(custom).toHaveAttribute('aria-pressed', 'true');
  });

  it('switches to custom mode with sensible defaults when Custom is clicked', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'preset', days: 30 }}
        onChange={onChange}
      />
    );

    fireEvent.click(screen.getByText('Custom'));

    expect(onChange).toHaveBeenCalledTimes(1);
    const call = onChange.mock.calls[0][0] as TimePeriodFilterValue;
    expect(call.type).toBe('custom');
    if (call.type === 'custom') {
      expect(call.startDate).toBeTruthy();
      expect(call.endDate).toBeTruthy();
      expect(call.startDate <= call.endDate).toBe(true);
    }
  });

  it('switches back to preset when Custom is toggled off', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'custom', startDate: '2025-01-01', endDate: '2025-01-31' }}
        onChange={onChange}
      />
    );

    fireEvent.click(screen.getByText('Custom'));
    expect(onChange).toHaveBeenCalledWith({ type: 'preset', days: 30 });
  });

  it('fires onChange with updated start date when valid', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'custom', startDate: '2025-01-01', endDate: '2025-01-31' }}
        onChange={onChange}
      />
    );

    const startInput = screen.getByLabelText('Start date') as HTMLInputElement;
    fireEvent.change(startInput, { target: { value: '2025-01-10' } });
    expect(onChange).toHaveBeenCalledWith({
      type: 'custom',
      startDate: '2025-01-10',
      endDate: '2025-01-31',
    });
  });

  it('fires onChange with updated end date when valid', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'custom', startDate: '2025-01-01', endDate: '2025-01-31' }}
        onChange={onChange}
      />
    );

    const endInput = screen.getByLabelText('End date') as HTMLInputElement;
    fireEvent.change(endInput, { target: { value: '2025-02-15' } });
    expect(onChange).toHaveBeenCalledWith({
      type: 'custom',
      startDate: '2025-01-01',
      endDate: '2025-02-15',
    });
  });

  it('does not fire onChange when start date is after end date', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'custom', startDate: '2025-01-01', endDate: '2025-01-31' }}
        onChange={onChange}
      />
    );

    const startInput = screen.getByLabelText('Start date') as HTMLInputElement;
    fireEvent.change(startInput, { target: { value: '2025-02-15' } });
    // start > end → onChange should NOT fire
    expect(onChange).not.toHaveBeenCalled();
  });

  it('does not fire onChange when end date is before start date', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'custom', startDate: '2025-01-15', endDate: '2025-01-31' }}
        onChange={onChange}
      />
    );

    const endInput = screen.getByLabelText('End date') as HTMLInputElement;
    fireEvent.change(endInput, { target: { value: '2025-01-01' } });
    // end < start → onChange should NOT fire
    expect(onChange).not.toHaveBeenCalled();
  });

  it('sets max attribute on start date input to prevent invalid selection', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'custom', startDate: '2025-01-01', endDate: '2025-01-31' }}
        onChange={onChange}
      />
    );

    const startInput = screen.getByLabelText('Start date') as HTMLInputElement;
    expect(startInput).toHaveAttribute('max', '2025-01-31');
  });

  it('sets min attribute on end date input to prevent invalid selection', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'custom', startDate: '2025-01-01', endDate: '2025-01-31' }}
        onChange={onChange}
      />
    );

    const endInput = screen.getByLabelText('End date') as HTMLInputElement;
    expect(endInput).toHaveAttribute('min', '2025-01-01');
  });

  it('switches from custom to a preset when a preset button is clicked', () => {
    const onChange = vi.fn();
    render(
      <TimePeriodFilter
        value={{ type: 'custom', startDate: '2025-01-01', endDate: '2025-01-31' }}
        onChange={onChange}
      />
    );

    fireEvent.click(screen.getByText('90 days'));
    expect(onChange).toHaveBeenCalledWith({ type: 'preset', days: 90 });
  });
});
