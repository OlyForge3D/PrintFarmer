import { describe, it, expect } from 'vitest';
import { isGcodeFileName } from '@/features/slicer/utils/gcodeFileUtils';

describe('isGcodeFileName', () => {
  it.each([
    ['model.gcode', true],
    ['output.GCODE', true],
    ['print.bgcode', true],
    ['legacy.g', true],
    ['file.G', true],
    ['thumbnail.png', false],
    ['project.3mf', false],
    ['slicer.log', false],
    ['model.stl', false],
    ['archive.gcode.zip', false],
  ])('isGcodeFileName(%s) → %s', (fileName, expected) => {
    expect(isGcodeFileName(fileName)).toBe(expected);
  });
});
