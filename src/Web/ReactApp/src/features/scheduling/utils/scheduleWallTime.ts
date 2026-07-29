interface WallTimeParts {
  year: number;
  month: number;
  day: number;
  hour: number;
  minute: number;
  second: number;
}

const WALL_TIME_PATTERN =
  /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?$/;

export function parseScheduleWallTime(value: string): WallTimeParts {
  const match = WALL_TIME_PATTERN.exec(value);
  if (!match) {
    throw new Error(`Invalid scheduled wall time: ${value}`);
  }
  return {
    year: Number(match[1]),
    month: Number(match[2]),
    day: Number(match[3]),
    hour: Number(match[4]),
    minute: Number(match[5]),
    second: Number(match[6] ?? 0),
  };
}

export function scheduleWallDateKey(value: string): string {
  const { year, month, day } = parseScheduleWallTime(value);
  return `${year}-${month - 1}-${day}`;
}

export function formatScheduleWallTime(
  value: string,
  timeZone: string,
  locale?: string
): string {
  const parts = parseScheduleWallTime(value);
  const stableUtc = new Date(Date.UTC(
    parts.year,
    parts.month - 1,
    parts.day,
    parts.hour,
    parts.minute,
    parts.second
  ));
  const formatted = new Intl.DateTimeFormat(locale, {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'UTC',
  }).format(stableUtc);
  return `${formatted} (${timeZone})`;
}

export function formatInstantInScheduleZone(
  value: string,
  timeZone: string,
  locale?: string
): string {
  return `${new Intl.DateTimeFormat(locale, {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone,
  }).format(new Date(value))} (${timeZone})`;
}

export function scheduleWallClock(value: string): string {
  const { hour, minute } = parseScheduleWallTime(value);
  return `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`;
}
