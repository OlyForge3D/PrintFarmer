/**
 * Utility functions for handling datetime parsing and formatting
 * 
 * Note: The API returns datetime strings in UTC format without timezone indicators.
 * These utilities ensure proper timezone handling.
 */

/**
 * Parse a datetime string from the API, ensuring it's treated as UTC
 * @param dateTimeString - ISO datetime string from API (assumes UTC)
 * @returns Date object with correct timezone handling
 */
export const parseApiDateTime = (dateTimeString: string): Date => {
  // If the string already has timezone info, use it as-is
  if (dateTimeString.includes('Z') || dateTimeString.includes('+') || dateTimeString.match(/[+-]\d{2}:\d{2}$/)) {
    return new Date(dateTimeString);
  }
  
  // Otherwise, append 'Z' to indicate UTC
  return new Date(dateTimeString + 'Z');
};

/**
 * Parse a datetime value (string or Date) from the API
 * @param dateTime - Date object or datetime string from API
 * @returns Date object with correct timezone handling
 */
export const parseApiDateTimeValue = (dateTime: Date | string): Date => {
  if (typeof dateTime === 'string') {
    return parseApiDateTime(dateTime);
  }
  return dateTime;
};

/**
 * Calculate duration between two datetime values in a human-readable format
 * @param start - Start datetime (string or Date)
 * @param end - End datetime (string or Date, defaults to now)
 * @returns Formatted duration string (e.g., "2h 30m", "45s")
 */
export const formatDuration = (start: Date | string, end?: Date | string): string => {
  const startTime = parseApiDateTimeValue(start);
  const endTime = end ? parseApiDateTimeValue(end) : new Date();
  const duration = endTime.getTime() - startTime.getTime();
  
  // Handle negative durations (shouldn't happen with proper timezone handling)
  if (duration < 0) {
    return '0s';
  }
  
  const seconds = Math.floor(duration / 1000);
  const minutes = Math.floor(seconds / 60);
  const hours = Math.floor(minutes / 60);

  if (hours > 0) return `${hours}h ${minutes % 60}m`;
  if (minutes > 0) return `${minutes}m ${seconds % 60}s`;
  return `${seconds}s`;
};

/**
 * Format print time in minutes to human-readable format (Xd Yh Zm)
 * Only shows non-zero units (no decimal places)
 * @param minutes - Print time in minutes
 * @returns Formatted string (e.g., "2h 30m", "1d 5h 30m", "45m")
 */
export const formatPrintTimeMinutes = (minutes: number): string => {
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  const mins = Math.floor(minutes % 60);
  
  const parts = [];
  if (days > 0) parts.push(`${days}d`);
  if (hours > 0) parts.push(`${hours}h`);
  if (mins > 0 || parts.length === 0) parts.push(`${mins}m`);
  
  return parts.join(' ');
};