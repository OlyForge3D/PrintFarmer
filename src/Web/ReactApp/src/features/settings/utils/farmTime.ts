/** Formats an instant in the farm zone, including the offset to disambiguate DST fall-back. */
export function formatFarmTime(instant: Date, timeZone: string): string | null {
  try {
    return new Intl.DateTimeFormat(undefined, {
      timeZone,
      hour: '2-digit',
      minute: '2-digit',
      hourCycle: 'h23',
      timeZoneName: 'shortOffset',
    }).format(instant);
  } catch (error) {
    // Browser timezone data can lag behind the server's IANA database.
    if (!(error instanceof RangeError)) throw error;
    return null;
  }
}
