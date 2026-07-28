import { describe, expect, it } from 'vitest';
import {
  formatScheduleWallTime,
  scheduleWallClock,
  scheduleWallDateKey,
} from '@/features/scheduling/utils/scheduleWallTime';

describe('schedule wall-time semantics', () => {
  it('buckets by reviewed local date instead of the UTC instant date', () => {
    expect(scheduleWallDateKey('2026-06-01T21:30:00')).toBe('2026-5-1');
    expect(scheduleWallClock('2026-06-01T21:30:00')).toBe('21:30');
  });

  it('renders reviewed fields unchanged when browser and schedule zones differ', () => {
    const rendered = formatScheduleWallTime(
      '2026-06-01T21:30:00',
      'America/New_York',
      'en-US'
    );

    expect(rendered).toContain('Jun 1, 2026');
    expect(rendered).toContain('9:30 PM');
    expect(rendered).toContain('America/New_York');
  });

  it.each([
    '2026-03-08T03:00:00',
    '2026-11-01T01:00:00',
  ])('preserves reviewed DST-boundary wall time %s', (wallTime) => {
    expect(scheduleWallDateKey(wallTime)).toBe(
      wallTime.startsWith('2026-03') ? '2026-2-8' : '2026-10-1'
    );
    expect(scheduleWallClock(wallTime)).toBe(
      wallTime.slice(11, 16)
    );
  });
});
