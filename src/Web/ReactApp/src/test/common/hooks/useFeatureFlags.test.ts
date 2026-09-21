import { describe, expect, it } from 'vitest';
import {
  normalizeFeatureFlags,
  type FeatureFlags,
} from '@/common/hooks/useFeatureFlags';

describe('normalizeFeatureFlags', () => {
  it('fills missing server flags with their enabled defaults', () => {
    const flags = normalizeFeatureFlags({
      'orca.schemaEditor': false,
    });

    const expected: FeatureFlags = {
      'orca.handcraftedEditors': true,
      'orca.schemaEditor': false,
      'orca.profileComparison': true,
      'orca.inheritanceDiff': true,
      'orca.importConflictResolver': true,
      'orca.expandedDtos': true,
    };

    expect(flags).toEqual(expected);
  });

  it('preserves every known server flag value', () => {
    const serverFlags: Record<string, boolean> = {
      'orca.handcraftedEditors': false,
      'orca.schemaEditor': true,
      'orca.profileComparison': false,
      'orca.inheritanceDiff': true,
      'orca.importConflictResolver': false,
      'orca.expandedDtos': false,
    };

    expect(normalizeFeatureFlags(serverFlags)).toEqual(serverFlags);
  });
});
