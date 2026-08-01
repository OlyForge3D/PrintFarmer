import { describe, it, expect } from 'vitest';
import {
  formatDirtySummary,
  formatSaveOutcome,
} from '@/features/admin/settings/settingsSaveRegistry';

/**
 * #1013 — the save bar's wording.
 *
 * The old bar said "3 unsaved changes", which tells the user nothing they did
 * not already know. What they need before pressing Save is *what* is about to
 * be written. These cases pin the point where naming stops being useful and
 * counting takes over.
 */
describe('formatDirtySummary', () => {
  it('names a single section', () => {
    expect(formatDirtySummary(1, ['System Log'])).toEqual({
      text: '1 change in System Log',
    });
  });

  it('keeps the section name when one section holds several edits', () => {
    expect(formatDirtySummary(3, ['System Log'])).toEqual({
      text: '3 changes in System Log',
    });
  });

  it('names both sections when there are two', () => {
    expect(formatDirtySummary(3, ['Network Discovery', 'Database'])).toEqual({
      text: '3 changes in Network Discovery and Database',
    });
  });

  it('counts sections past two and carries the list in a tooltip', () => {
    expect(formatDirtySummary(7, ['A', 'B', 'C', 'D'])).toEqual({
      text: '7 changes in 4 sections',
      title: 'A, B, C, D',
    });
  });

  it('falls back to a bare count when no section is named', () => {
    expect(formatDirtySummary(2, [])).toEqual({ text: '2 unsaved changes' });
    expect(formatDirtySummary(1, [])).toEqual({ text: '1 unsaved change' });
  });
});

/**
 * A partial failure is the case worth getting right. Collapsing "two saved, one
 * failed" into "save failed" leaves the user unsure whether to retry all of it.
 */
describe('formatSaveOutcome', () => {
  it('names what was saved', () => {
    expect(formatSaveOutcome(['System Log'], [])).toBe('Saved System Log');
    expect(formatSaveOutcome(['A', 'B'], [])).toBe('Saved A, B');
  });

  it('names what failed when nothing landed', () => {
    expect(formatSaveOutcome([], ['Network Discovery']))
      .toBe('Failed to save Network Discovery');
  });

  it('names both halves of a partial failure', () => {
    expect(formatSaveOutcome(['System Log', 'Database'], ['Network Discovery']))
      .toBe('Saved System Log, Database. Failed to save Network Discovery');
  });

  it('counts instead of listing once the list stops being readable', () => {
    expect(formatSaveOutcome(['A', 'B', 'C', 'D'], [])).toBe('Saved 4 sections');
    expect(formatSaveOutcome(['A', 'B', 'C'], [])).toBe('Saved A, B, C');
  });
});
