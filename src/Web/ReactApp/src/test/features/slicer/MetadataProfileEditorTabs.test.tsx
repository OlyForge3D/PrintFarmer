/**
 * Tab styling + change-tracking behavior for the metadata-driven process editor.
 *
 * Mirrors OrcaSlicer: the selected tab is bold with an accent bottom border,
 * unselected tabs are muted/normal weight, and any tab containing a setting that
 * differs from the original snapshot is rendered in the "modified" (warning) color.
 */
import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import '@testing-library/jest-dom';
import metadata from '@/features/slicer/generated/orcaSettingsMetadata.json';
import { MetadataProfileEditor } from '@/features/slicer/components/settings/MetadataProfileRenderer';
import type { ProfileTypeMetadata } from '@/features/slicer/components/settings/metadataTypes';

const processMeta = (metadata as unknown as Record<string, ProfileTypeMetadata>).process;

// Find the first field that is visible in simple mode, plus its owning tab.
function firstVisibleSimpleTabAndField(): { tabName: string; key: string } {
  for (const tab of processMeta.tabs) {
    for (const section of tab.sections) {
      for (const field of section.fields) {
        const m = processMeta.settings[field.key];
        if (!m) continue;
        if (m.mode === 'developer') continue;
        if (m.mode === 'advanced') continue; // hidden in simple mode
        return { tabName: tab.name, key: field.key };
      }
    }
  }
  throw new Error('No simple-visible process field found in metadata');
}

function tabButtons(container: HTMLElement): HTMLButtonElement[] {
  return Array.from(container.querySelectorAll<HTMLButtonElement>('button[aria-selected]'));
}

describe('MetadataProfileEditor — OrcaSlicer-style tabs', () => {
  it('renders the selected tab bold with an accent underline and unselected tabs muted/normal', () => {
    const { container } = render(
      <MetadataProfileEditor profileType="process" settings={{}} onUpdate={() => {}} />,
    );

    const buttons = tabButtons(container);
    expect(buttons.length).toBeGreaterThan(1);

    const active = buttons.find((b) => b.getAttribute('aria-selected') === 'true')!;
    expect(active).toBeDefined();
    expect(active.className).toContain('font-bold');
    expect(active.className).toContain('border-pf-accent-2');

    const inactive = buttons.filter((b) => b.getAttribute('aria-selected') === 'false');
    expect(inactive.length).toBeGreaterThan(0);
    for (const b of inactive) {
      expect(b.className).toContain('font-normal');
      expect(b.className).toContain('border-transparent');
      expect(b.className).not.toContain('text-pf-warning');
    }
  });

  it('colors a tab in the modified (warning) color when one of its fields differs from the original', () => {
    const { tabName, key } = firstVisibleSimpleTabAndField();

    const { container } = render(
      <MetadataProfileEditor
        profileType="process"
        settings={{ [key]: 1 }}
        originalSettings={{ [key]: 0 }}
        onUpdate={() => {}}
      />,
    );

    const dirtyTab = tabButtons(container).find((b) => b.textContent === tabName);
    expect(dirtyTab).toBeDefined();
    expect(dirtyTab!.className).toContain('text-pf-warning');
  });

  it('does not color any tab as modified when settings match the original snapshot', () => {
    const { key } = firstVisibleSimpleTabAndField();

    const { container } = render(
      <MetadataProfileEditor
        profileType="process"
        settings={{ [key]: 0 }}
        originalSettings={{ [key]: 0 }}
        onUpdate={() => {}}
      />,
    );

    for (const b of tabButtons(container)) {
      expect(b.className).not.toContain('text-pf-warning');
    }
  });
});
