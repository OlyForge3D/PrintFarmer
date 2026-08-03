import type { ReactNode } from 'react';
import { Button, Input } from '@/common/components/ui';
import { PlusIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { FailureDetectionStatusCard } from '@/features/admin/components/FailureDetectionStatusCard';
import { ObicoServersSection } from '@/features/admin/components/ObicoServersSection';
import type { SettingValue } from '@/common/components/SettingsPagelet';

/**
 * Context passed to a section renderer's `extension` so it can read the working
 * values for the section and push updates back into the parent group's dirty
 * state. Extensions are pure UI — they never call the API directly, which keeps
 * per-group save the single source of truth for persistence.
 */
export interface SectionRendererContext {
  values: Record<string, SettingValue>;
  onChange: (field: string, value: SettingValue) => void;
}

export interface SectionRenderer {
  /**
   * Optional bespoke UI rendered inside the section's card body, after the
   * metadata-driven fields. Use this when a section has state that isn't a flat
   * list of scalar fields (e.g. Obico's server table, SlicerSettings' per-engine
   * overrides, CostTracking's material price map).
   */
  extension?: (ctx: SectionRendererContext) => ReactNode;
  /**
   * When true, the section's card spans both columns in the group's grid. Use
   * for sections whose extension is wide (e.g. Obico's server table).
   */
  fullWidth?: boolean;
}

function renderObico(): ReactNode {
  return (
    // `auto-fit`, not a `lg:` breakpoint. This grid lives inside a settings
    // card in a `columns-*` flow, so its real width (~645px at a 1920px
    // viewport) has nothing to do with the viewport. A viewport breakpoint
    // split it into two 315px halves and clipped both panels.
    <div className="mt-4 grid gap-4 grid-cols-[repeat(auto-fit,minmax(28rem,1fr))]">
      <FailureDetectionStatusCard />
      <ObicoServersSection />
    </div>
  );
}

function renderPerEngine({ values, onChange }: SectionRendererContext): ReactNode {
  // Backend key is camelCase (`[JsonPropertyName("perEngine")]` on
  // `SlicerSettings.PerEngine`), and values arriving from `GET /api/settings/{key}`
  // are the raw wire JSON with no normalization. Indexing with the .NET
  // PascalCase name would always return `undefined` and the whole editor would
  // silently render nothing.
  const perEngine = values['perEngine'];
  if (!perEngine || typeof perEngine !== 'object') {
    return null;
  }
  const perEngineRecord = perEngine as Record<string, unknown>;

  return (
    <div className="mt-4 pt-3 border-t border-pf-border">
      <h4 className="text-base font-semibold mb-2">Per-Engine Slicer Settings</h4>
      {Object.entries(perEngineRecord).map(([engine, engineSettings]) => (
        <div key={engine} className="border rounded-sm p-3 mb-3 bg-pf-bg-2">
          <h5 className="font-medium text-sm mb-2">{engine}</h5>
          <div className="space-y-1.5">
            {Object.entries(
              (engineSettings ?? {}) as Record<string, string | number | boolean | undefined>,
            ).map(([field, value]) => (
              <div className="flex items-center gap-3" key={field}>
                <label
                  className="shrink-0 w-32 text-sm font-medium text-pf-text-primary"
                  htmlFor={`perengine-${engine}-${field}`}
                >
                  {field}
                </label>
                <Input
                  id={`perengine-${engine}-${field}`}
                  className="flex-1"
                  value={typeof value === 'string' || typeof value === 'number' ? value : ''}
                  placeholder={field}
                  title={field}
                  aria-label={`${engine} ${field}`}
                  onChange={(e) => {
                    const currentEngine =
                      typeof engineSettings === 'object' && engineSettings !== null
                        ? (engineSettings as Record<string, unknown>)
                        : {};
                    onChange('perEngine', {
                      ...perEngineRecord,
                      [engine]: {
                        ...currentEngine,
                        [field]: e.currentTarget.value,
                      },
                    });
                  }}
                />
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

function renderMaterialPriceDefaults({ values, onChange }: SectionRendererContext): ReactNode {
  const defaults = values['materialPriceDefaults'];
  if (!defaults || typeof defaults !== 'object') {
    return null;
  }
  const defaultsRecord = defaults as Record<string, unknown>;

  return (
    <div className="mt-4 pt-3 border-t border-pf-border">
      <div className="flex items-center justify-between mb-3">
        <div>
          <h4 className="text-base font-semibold text-pf-text-primary">Material Price Defaults</h4>
          <p className="text-xs text-pf-text-secondary mt-0.5">
            Default price per kilogram for each material type. Used when Spoolman pricing is unavailable.
          </p>
        </div>
        <Button
          type="button"
          variant="primary"
          size="sm"
          iconLeft={<PlusIcon className="w-3.5 h-3.5" />}
          onClick={() => {
            const existing = Object.keys(defaultsRecord);
            let newName = 'New Material';
            let counter = 1;
            while (existing.some((k) => k.toLowerCase() === newName.toLowerCase())) {
              newName = `New Material ${++counter}`;
            }
            onChange('materialPriceDefaults', { ...defaultsRecord, [newName]: 25 });
          }}
          aria-label="Add material price default"
        >
          Add Material
        </Button>
      </div>
      <div className="space-y-2">
        {Object.entries(defaultsRecord).map(([material, price]) => (
          <div key={material} className="flex items-center gap-2">
            <Input
              type="text"
              value={material}
              aria-label="Material name"
              title="Material type name (e.g., PLA, PETG)"
              className="w-40"
              onChange={(e) => {
                const newName = e.currentTarget.value;
                const updated: Record<string, unknown> = {};
                for (const [k, v] of Object.entries(defaultsRecord)) {
                  updated[k === material ? newName : k] = v;
                }
                onChange('materialPriceDefaults', updated);
              }}
            />
            <span className="text-sm text-pf-text-secondary">$</span>
            <Input
              type="number"
              value={typeof price === 'number' ? price : ''}
              min={0}
              max={500}
              step="any"
              aria-label={`Price per kg for ${material}`}
              title="Price per kilogram (USD)"
              className="w-28"
              onChange={(e) => {
                onChange('materialPriceDefaults', {
                  ...defaultsRecord,
                  [material]: e.currentTarget.value === '' ? '' : Number(e.currentTarget.value),
                });
              }}
            />
            <span className="text-xs text-pf-text-secondary">/kg</span>
            <Button
              type="button"
              variant="secondary"
              size="sm"
              iconLeft={<CloseIcon className="w-3.5 h-3.5" />}
              onClick={() => {
                const updated = { ...defaultsRecord };
                delete updated[material];
                onChange('materialPriceDefaults', updated);
              }}
              aria-label={`Remove ${material}`}
            />
          </div>
        ))}
      </div>
    </div>
  );
}

/**
 * Registry of custom section renderers, keyed by the section's identifier
 * (`meta.key` first, with `meta.className` as fallback for legacy metadata).
 *
 * Adding a new section-specific UI:
 *   1. Add a `[SettingDisplay]` on the backend (no change here required).
 *   2. If the section renders as a plain form, you're done — the metadata
 *      renderer handles it.
 *   3. If the section needs custom UI (a table, a nested map, a status card),
 *      write an extension component and register it below by the section's key.
 *
 * The registry is the *only* switchboard for section-specific behaviour. Do NOT
 * add hardcoded `meta.key === 'Foo'` branches back into SettingsPage or the
 * pagelet — that regresses the whole point of this indirection.
 */
export const sectionRendererRegistry: Record<string, SectionRenderer> = {
  Obico: {
    fullWidth: true,
    extension: renderObico,
  },
  SlicerSettings: {
    extension: renderPerEngine,
  },
  CostTrackingSettings: {
    extension: renderMaterialPriceDefaults,
  },
};

/**
 * Look up the renderer for a section. Checks `meta.key` first (the canonical
 * identifier) and falls back to `meta.className` so legacy metadata that only
 * emits the class name still resolves.
 */
export function getSectionRenderer(meta: {
  key: string;
  className: string;
}): SectionRenderer | undefined {
  return sectionRendererRegistry[meta.key] ?? sectionRendererRegistry[meta.className];
}
