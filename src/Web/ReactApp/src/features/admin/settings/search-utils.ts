import type {
  SettingMetadata,
  SettingPropertyMetadata,
} from '@/common/components/SettingsPagelet';

/**
 * Case-insensitive substring check that treats an empty query as "no query"
 * rather than "matches nothing". Callers rely on this so a fresh render with
 * an empty search box does not accidentally hide every setting.
 */
export function textMatches(haystack: string | undefined | null, query: string): boolean {
  if (!query) {
    return false;
  }
  if (!haystack) {
    return false;
  }
  return haystack.toLowerCase().includes(query.toLowerCase());
}

export function propertyMatchesQuery(
  prop: SettingPropertyMetadata,
  query: string,
): boolean {
  if (!query) {
    return false;
  }
  return (
    textMatches(prop.display?.name, query)
    || textMatches(prop.name, query)
    // #1025 moved unit suffixes out of the label and into an adornment beside
    // the control. A user who types "MB" is searching for text they can see, so
    // the unit has to stay searchable wherever it is rendered — otherwise the
    // filter silently got worse the moment a label was cleaned up.
    || textMatches(prop.display?.unit, query)
    || textMatches(prop.display?.description, query)
  );
}

export function sectionMatchesQuery(section: SettingMetadata, query: string): boolean {
  if (!query) {
    return false;
  }
  return (
    textMatches(section.displayName, query)
    || textMatches(section.className, query)
    || textMatches(section.description, query)
  );
}

export function groupMatchesQuery(
  groupKey: string,
  groupDisplay: string,
  query: string,
): boolean {
  if (!query) {
    return false;
  }
  return textMatches(groupKey, query) || textMatches(groupDisplay, query);
}

/**
 * Split a string into segments alternating between "unmatched" and "matched"
 * runs relative to a case-insensitive query. Returns the segments in order so
 * a renderer can wrap the matched runs in `<mark>` without altering casing.
 *
 * A pure function taking primitives only, so it is safe to call from `useMemo`.
 */
export interface HighlightSegment {
  text: string;
  matched: boolean;
}

export function splitOnQuery(text: string, query: string): HighlightSegment[] {
  if (!query) {
    return [{ text, matched: false }];
  }
  const lower = text.toLowerCase();
  const q = query.toLowerCase();
  if (!lower.includes(q)) {
    return [{ text, matched: false }];
  }

  const segments: HighlightSegment[] = [];
  let cursor = 0;
  while (cursor < text.length) {
    const idx = lower.indexOf(q, cursor);
    if (idx === -1) {
      segments.push({ text: text.slice(cursor), matched: false });
      break;
    }
    if (idx > cursor) {
      segments.push({ text: text.slice(cursor, idx), matched: false });
    }
    segments.push({ text: text.slice(idx, idx + query.length), matched: true });
    cursor = idx + query.length;
  }
  return segments;
}
