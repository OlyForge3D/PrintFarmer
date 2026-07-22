import { describe, it, expect } from 'vitest';

/**
 * Tests for the custom profile compatible_printers filtering logic
 * used in NewSliceJobPage for customFilamentProfiles and customProcessProfiles.
 *
 * The logic under test (extracted from the useMemo hooks):
 *   - When no machine is selected, return ALL profiles of the given type.
 *   - When a machine IS selected, only return profiles whose rawJson
 *     contains a compatible_printers array that includes the selected machine.
 *   - Profiles with missing/empty compatible_printers, missing rawJson,
 *     or unparseable rawJson are HIDDEN (filtered out).
 */

interface CustomProfile {
  id: string;
  name: string;
  profileType: 'machine' | 'filament' | 'process';
  isSystem: boolean;
  createdAt: string;
  rawJson?: string;
}

/**
 * Pure extraction of the filtering logic from NewSliceJobPage useMemo hooks.
 * Mirrors lines 741-773 after the bug fix.
 */
function filterCustomProfilesByCompatibility(
  profiles: CustomProfile[],
  profileType: 'filament' | 'process',
  selectedMachineProfileId: string,
): CustomProfile[] {
  const allOfType = profiles.filter(p => p.profileType === profileType);
  if (!selectedMachineProfileId) return allOfType;
  return allOfType.filter(p => {
    if (p.rawJson) {
      try {
        const parsed = JSON.parse(p.rawJson) as Record<string, unknown>;
        const compatible = parsed.compatible_printers as string[] | undefined;
        if (compatible && compatible.length > 0) {
          return compatible.some(c => c === selectedMachineProfileId);
        }
      } catch { /* hide profile if can't parse */ }
    }
    return false;
  });
}

const MACHINE_ID = 'Bambu Lab X1 Carbon 0.4 nozzle';

function makeProfile(
  overrides: Partial<CustomProfile> & { profileType: 'filament' | 'process' },
): CustomProfile {
  return {
    id: overrides.id ?? 'test-id',
    name: overrides.name ?? 'Test Profile',
    isSystem: false,
    createdAt: '2025-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('Custom profile compatible_printers filtering', () => {
  const profileTypes: Array<'filament' | 'process'> = ['filament', 'process'];

  for (const profileType of profileTypes) {
    describe(`${profileType} profiles`, () => {
      it('shows profile with matching compatible_printers', () => {
        const profiles = [
          makeProfile({
            profileType,
            rawJson: JSON.stringify({ compatible_printers: [MACHINE_ID] }),
          }),
        ];
        const result = filterCustomProfilesByCompatibility(profiles, profileType, MACHINE_ID);
        expect(result).toHaveLength(1);
      });

      it('hides profile with non-matching compatible_printers', () => {
        const profiles = [
          makeProfile({
            profileType,
            rawJson: JSON.stringify({ compatible_printers: ['Some Other Printer'] }),
          }),
        ];
        const result = filterCustomProfilesByCompatibility(profiles, profileType, MACHINE_ID);
        expect(result).toHaveLength(0);
      });

      it('hides profile without compatible_printers field', () => {
        const profiles = [
          makeProfile({
            profileType,
            rawJson: JSON.stringify({ some_other_field: 'value' }),
          }),
        ];
        const result = filterCustomProfilesByCompatibility(profiles, profileType, MACHINE_ID);
        expect(result).toHaveLength(0);
      });

      it('hides profile with empty compatible_printers array', () => {
        const profiles = [
          makeProfile({
            profileType,
            rawJson: JSON.stringify({ compatible_printers: [] }),
          }),
        ];
        const result = filterCustomProfilesByCompatibility(profiles, profileType, MACHINE_ID);
        expect(result).toHaveLength(0);
      });

      it('hides profile with no rawJson', () => {
        const profiles = [
          makeProfile({ profileType, rawJson: undefined }),
        ];
        const result = filterCustomProfilesByCompatibility(profiles, profileType, MACHINE_ID);
        expect(result).toHaveLength(0);
      });

      it('hides profile with unparseable rawJson', () => {
        const profiles = [
          makeProfile({ profileType, rawJson: '{not valid json' }),
        ];
        const result = filterCustomProfilesByCompatibility(profiles, profileType, MACHINE_ID);
        expect(result).toHaveLength(0);
      });

      it('shows ALL custom profiles when no machine is selected', () => {
        const profiles = [
          makeProfile({
            id: '1',
            profileType,
            rawJson: JSON.stringify({ compatible_printers: [MACHINE_ID] }),
          }),
          makeProfile({
            id: '2',
            profileType,
            rawJson: undefined,
          }),
          makeProfile({
            id: '3',
            profileType,
            rawJson: JSON.stringify({ compatible_printers: [] }),
          }),
        ];
        const result = filterCustomProfilesByCompatibility(profiles, profileType, '');
        expect(result).toHaveLength(3);
      });

      it('shows profile when it lists multiple printers including the selected one', () => {
        const profiles = [
          makeProfile({
            profileType,
            rawJson: JSON.stringify({
              compatible_printers: ['Printer A', MACHINE_ID, 'Printer B'],
            }),
          }),
        ];
        const result = filterCustomProfilesByCompatibility(profiles, profileType, MACHINE_ID);
        expect(result).toHaveLength(1);
      });
    });
  }
});
