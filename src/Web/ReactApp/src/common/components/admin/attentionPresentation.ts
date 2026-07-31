import type { ReactElement } from 'react';
import {
  AlertCircleIcon,
  AlertIcon,
  InfoIcon,
} from '@/common/components/icons/MdiIcons';

/**
 * How each attention severity is painted.
 *
 * Split out of `AttentionRow.tsx` so that file exports a component and nothing
 * else — mixing constants and components in one module breaks Fast Refresh,
 * which silently degrades every edit in dev into a full reload.
 */

/** Severities the row knows how to paint. Anything else degrades to info. */
export type AttentionSeverity = 'Error' | 'Warning' | 'Info';

const KNOWN_SEVERITIES: readonly string[] = ['Error', 'Warning', 'Info'];

export interface AttentionPresentation {
  label: string;
  Icon: (props: { className?: string; ariaLabel?: string }) => ReactElement;
  iconClass: string;
  badgeVariant: 'error' | 'warning' | 'info' | 'default';
  rowBorderClass: string;
  rowBgClass: string;
  srPrefix: string;
}

export const ATTENTION_PRESENTATION: Record<AttentionSeverity, AttentionPresentation> = {
  Error: {
    label: 'Error',
    Icon: AlertCircleIcon,
    iconClass: 'text-pf-error',
    badgeVariant: 'error',
    rowBorderClass: 'border-pf-error/40',
    rowBgClass: 'bg-pf-error-bg/40',
    srPrefix: 'Error',
  },
  Warning: {
    label: 'Warning',
    Icon: AlertIcon,
    iconClass: 'text-pf-warning',
    badgeVariant: 'warning',
    rowBorderClass: 'border-pf-warning/40',
    rowBgClass: 'bg-pf-bg-1',
    srPrefix: 'Warning',
  },
  Info: {
    label: 'Info',
    Icon: InfoIcon,
    iconClass: 'text-pf-accent',
    badgeVariant: 'info',
    rowBorderClass: 'border-pf-border',
    rowBgClass: 'bg-pf-bg-1',
    srPrefix: 'Info',
  },
};

/**
 * Resolve a raw severity string to its presentation.
 *
 * The backend serializes this enum as a string, so an unrecognized value means
 * the server shipped a severity this build doesn't know. Degrade to the info
 * treatment rather than dropping the row — a visible "unknown severity" row is
 * far better than silently hiding a problem — and put the raw value in the
 * screen-reader prefix so it stays diagnosable.
 */
export function presentationForAttentionSeverity(raw: string): AttentionPresentation {
  if (KNOWN_SEVERITIES.includes(raw)) {
    return ATTENTION_PRESENTATION[raw as AttentionSeverity];
  }
  return {
    ...ATTENTION_PRESENTATION.Info,
    label: raw || 'Notice',
    srPrefix: `Unknown severity "${raw || 'unspecified'}"`,
  };
}
