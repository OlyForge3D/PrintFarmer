import { describe, it, expect } from 'vitest';
import { gcodeLibraryTour } from '@/features/gcode/tours/gcode-library.tour';

describe('gcode library tour definition', () => {
  it('exports a valid array of tour steps', () => {
    expect(Array.isArray(gcodeLibraryTour)).toBe(true);
    expect(gcodeLibraryTour.length).toBeGreaterThan(0);
  });

  it('has a reasonable number of steps (4-8)', () => {
    expect(gcodeLibraryTour.length).toBeGreaterThanOrEqual(4);
    expect(gcodeLibraryTour.length).toBeLessThanOrEqual(8);
  });

  it('each step has an element selector', () => {
    for (const step of gcodeLibraryTour) {
      expect(step.element).toBeDefined();
      expect(typeof step.element).toBe('string');
      expect(step.element.length).toBeGreaterThan(0);
    }
  });

  it('each step has a popover with title and description', () => {
    for (const step of gcodeLibraryTour) {
      expect(step.popover).toBeDefined();
      expect(typeof step.popover.title).toBe('string');
      expect(step.popover.title.length).toBeGreaterThan(0);
      expect(typeof step.popover.description).toBe('string');
      expect(step.popover.description.length).toBeGreaterThan(0);
    }
  });

  it('steps reference data-tour-* selectors for stability', () => {
    for (const step of gcodeLibraryTour) {
      expect(step.element).toMatch(/\[data-tour[=-]/);
    }
  });

  it('step titles are concise (under 50 characters)', () => {
    for (const step of gcodeLibraryTour) {
      expect(step.popover.title.length).toBeLessThanOrEqual(50);
    }
  });

  it('step descriptions are not empty and reasonably sized', () => {
    for (const step of gcodeLibraryTour) {
      expect(step.popover.description.length).toBeGreaterThanOrEqual(10);
      expect(step.popover.description.length).toBeLessThanOrEqual(200);
    }
  });
});
