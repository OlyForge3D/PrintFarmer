import { act, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { FarmIdentityBrand } from '@/features/settings/components/FarmIdentityBrand';
import { formatFarmTime } from '@/features/settings/utils/farmTime';
import { useFarmIdentity } from '@/features/settings/hooks/useFarmIdentity';

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ isAuthenticated: true }),
}));
vi.mock('@/features/settings/hooks/useFarmIdentity', () => ({
  useFarmIdentity: vi.fn(),
}));

afterEach(() => {
  vi.useRealTimers();
});

describe('farm identity branding', () => {
  it('keeps the existing brand and UTC clock for an unconfigured farm', () => {
    vi.mocked(useFarmIdentity).mockReturnValue({
      data: { farmName: 'PrintFarmer', timeZone: 'UTC' },
      error: null,
    } as ReturnType<typeof useFarmIdentity>);
    render(<FarmIdentityBrand />);
    expect(screen.getByText('PrintFarmer')).toBeInTheDocument();
    expect(screen.getByTitle('Farm time (UTC)')).toHaveTextContent('(UTC)');
  });

  it('displays the custom name as text and updates the explicitly zoned clock', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-03-08T09:59:30Z'));
    vi.mocked(useFarmIdentity).mockReturnValue({
      data: { farmName: '<Workshop>', timeZone: 'America/Los_Angeles' },
      error: null,
    } as ReturnType<typeof useFarmIdentity>);
    const { container } = render(<FarmIdentityBrand />);
    expect(screen.getByText('<Workshop>')).toBeInTheDocument();
    expect(container.querySelector('time')).toHaveTextContent(/01:59|1:59/);
    act(() => vi.advanceTimersByTime(30_000));
    expect(container.querySelector('time')).toHaveTextContent(/03:00|3:00/);
    expect(screen.getByTitle('Farm time (America/Los_Angeles)')).toHaveTextContent('America/Los_Angeles');
  });

  it('distinguishes repeated fall-back wall times by their UTC offsets', () => {
    const before = formatFarmTime(new Date('2026-11-01T08:30:00Z'), 'America/Los_Angeles');
    const after = formatFarmTime(new Date('2026-11-01T09:30:00Z'), 'America/Los_Angeles');
    expect(before).toMatch(/01:30|1:30/);
    expect(after).toMatch(/01:30|1:30/);
    expect(before).toContain('GMT-7');
    expect(after).toContain('GMT-8');
  });

  it('announces the time, offset and IANA zone at both repeated fall-back instants', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-11-01T08:30:00Z'));
    vi.mocked(useFarmIdentity).mockReturnValue({
      data: { farmName: 'Workshop', timeZone: 'America/Los_Angeles' },
      error: null,
    } as ReturnType<typeof useFarmIdentity>);
    const { container } = render(<FarmIdentityBrand />);
    expect(container.querySelector('time')).toHaveAccessibleName('Farm time: 01:30 GMT-7 (America/Los_Angeles)');
    vi.setSystemTime(new Date('2026-11-01T09:30:00Z'));
    act(() => vi.advanceTimersByTime(30_000));
    expect(container.querySelector('time')).toHaveAccessibleName('Farm time: 01:30 GMT-8 (America/Los_Angeles)');
  });

  it('reports read failures without claiming a configured farm time', () => {
    vi.mocked(useFarmIdentity).mockReturnValue({
      data: undefined,
      error: new Error('offline'),
    } as ReturnType<typeof useFarmIdentity>);
    const { container } = render(<FarmIdentityBrand />);
    expect(screen.getByRole('status')).toHaveTextContent('Farm identity unavailable');
    expect(container.querySelector('time')).toBeNull();
  });

  it('keeps the name and surfaces a timezone unsupported by the browser', () => {
    vi.mocked(useFarmIdentity).mockReturnValue({
      data: { farmName: 'Workshop', timeZone: 'Unknown/Zone' },
      error: null,
    } as ReturnType<typeof useFarmIdentity>);
    const { container } = render(<FarmIdentityBrand />);
    expect(screen.getByText('Workshop')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Farm timezone unsupported by this browser');
    expect(container.querySelector('time')).toBeNull();
  });
});
