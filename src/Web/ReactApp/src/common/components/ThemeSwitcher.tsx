import { useMemo, useState, type CSSProperties } from 'react';
import clsx from 'clsx';
import { ChevronDownIcon, ChevronUpIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { useTheme, type NewTheme } from '@/common/hooks/useTheme';

interface ThemePreviewPalette {
  canvas: string;
  panel: string;
  panelMuted: string;
  text: string;
  muted: string;
  accent: string;
  accentSoft: string;
  nav: string;
}

interface ThemeOption {
  id: NewTheme;
  label: string;
  description: string;
  swatches: string[];
  preview: ThemePreviewPalette;
}

const THEME_OPTIONS: ThemeOption[] = [
  {
    id: 'dark',
    label: 'Dark',
    description: 'Mission Control — cool slate-navy with precision-teal',
    swatches: ['#08101f', '#0e1729', '#14b8a6', '#e8eef8'],
    preview: {
      canvas: '#07101d',
      panel: '#111b2e',
      panelMuted: '#162238',
      text: '#e8eef8',
      muted: '#8ea1be',
      accent: '#14b8a6',
      accentSoft: 'rgba(20,184,166,0.16)',
      nav: '#0b1423',
    },
  },
  {
    id: 'light',
    label: 'Light',
    description: 'Workshop Daylight — clean, high-contrast',
    swatches: ['#f5f7fa', '#ffffff', '#0d7d75', '#0b1320'],
    preview: {
      canvas: '#eef3f7',
      panel: '#ffffff',
      panelMuted: '#f3f6f8',
      text: '#0b1320',
      muted: '#5b6577',
      accent: '#0d7d75',
      accentSoft: 'rgba(13,125,117,0.14)',
      nav: '#dbe5ec',
    },
  },
  {
    id: 'matrix',
    label: 'Matrix',
    description: 'Terminal — phosphor-green CRT aesthetic',
    swatches: ['#000000', '#050a05', '#00ff41', '#4ade80'],
    preview: {
      canvas: '#010401',
      panel: '#041106',
      panelMuted: '#07180a',
      text: '#7dff9f',
      muted: '#2ba44d',
      accent: '#00ff41',
      accentSoft: 'rgba(0,255,65,0.14)',
      nav: '#020802',
    },
  },
  {
    id: 'blueprint',
    label: 'Blueprint',
    description: 'Schematic — cyanotype drafting aesthetic',
    swatches: ['#0c1a2e', '#102137', '#38bdf8', '#e6f2ff'],
    preview: {
      canvas: '#09172b',
      panel: '#0f2138',
      panelMuted: '#16304c',
      text: '#e6f2ff',
      muted: '#8cb5d0',
      accent: '#38bdf8',
      accentSoft: 'rgba(56,189,248,0.16)',
      nav: '#0b1c31',
    },
  },
  {
    id: 'ratos',
    label: 'RatOS',
    description: 'Firmware — black and green terminal aesthetic',
    swatches: ['#080a08', '#0c110d', '#22c55e', '#86efac'],
    preview: {
      canvas: '#070907',
      panel: '#101410',
      panelMuted: '#151b15',
      text: '#e7f8e9',
      muted: '#8fb79b',
      accent: '#22c55e',
      accentSoft: 'rgba(34,197,94,0.14)',
      nav: '#0b0f0c',
    },
  },
  {
    id: 'voron',
    label: 'Voron',
    description: 'Industrial — red-on-black precision engineering',
    swatches: ['#090909', '#101012', '#dc2626', '#a1a1aa'],
    preview: {
      canvas: '#070707',
      panel: '#111114',
      panelMuted: '#17171b',
      text: '#f2f2f3',
      muted: '#9f9fa8',
      accent: '#dc2626',
      accentSoft: 'rgba(220,38,38,0.14)',
      nav: '#0b0b0d',
    },
  },
  {
    id: 'farm',
    label: 'Farm',
    description: 'Harvest — warm autumn festival colors',
    swatches: ['#24150c', '#332113', '#ea580c', '#84cc16'],
    preview: {
      canvas: '#1d1209',
      panel: '#2a1b10',
      panelMuted: '#332113',
      text: '#f6e7d7',
      muted: '#c7a98b',
      accent: '#ea580c',
      accentSoft: 'rgba(234,88,12,0.16)',
      nav: '#22150c',
    },
  },
];

function ThemePreview({ option }: { option: ThemeOption }) {
  const previewStyle = useMemo(() => ({
    '--preview-canvas': option.preview.canvas,
    '--preview-panel': option.preview.panel,
    '--preview-panel-muted': option.preview.panelMuted,
    '--preview-text': option.preview.text,
    '--preview-muted': option.preview.muted,
    '--preview-accent': option.preview.accent,
    '--preview-accent-soft': option.preview.accentSoft,
    '--preview-nav': option.preview.nav,
  }) as CSSProperties, [option.preview]);

  return (
    <div className="space-y-3 rounded-[1.35rem] border border-pf-border bg-pf-bg-1/60 p-4 shadow-[inset_0_1px_0_rgba(255,255,255,0.04)]">
      <div>
        <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-pf-text-tertiary">Live preview</p>
        <p className="mt-1 text-sm text-pf-text-secondary">{option.label} live preview</p>
      </div>

      <div
        className="aspect-[5/6] overflow-hidden rounded-[1.25rem] border border-white/6 shadow-[0_18px_40px_-24px_rgba(0,0,0,0.7)]"
        style={previewStyle}
      >
        <div
          className="flex h-full flex-col bg-[var(--preview-canvas)] text-[var(--preview-text)]"
          style={{
            backgroundImage: 'linear-gradient(180deg, rgba(255,255,255,0.04), rgba(255,255,255,0) 18%)',
          }}
        >
          <div className="border-b border-white/8 bg-[var(--preview-nav)] px-4 py-3">
            <div className="flex items-center justify-between gap-3">
              <div>
                <div className="text-[10px] font-semibold uppercase tracking-[0.18em] text-[var(--preview-muted)]">Dashboard</div>
                <div className="mt-1 text-sm font-semibold">Farm overview</div>
              </div>
              <div className="flex items-center gap-1.5" aria-hidden="true">
                <span className="h-2 w-2 rounded-full bg-[var(--preview-accent)]" />
                <span className="h-2 w-2 rounded-full bg-white/16" />
                <span className="h-2 w-2 rounded-full bg-white/10" />
              </div>
            </div>
          </div>

          <div className="grid flex-1 gap-3 p-4">
            <div className="rounded-[1.1rem] border border-white/6 bg-[var(--preview-panel)] p-4 shadow-[inset_0_1px_0_rgba(255,255,255,0.04)]">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <div className="text-[10px] uppercase tracking-[0.16em] text-[var(--preview-muted)]">Printer card</div>
                  <div className="mt-1 text-sm font-semibold">Voron 2.4</div>
                </div>
                <div className="rounded-full bg-[var(--preview-accent-soft)] px-2 py-1 text-[10px] font-semibold text-[var(--preview-accent)]">
                  Active
                </div>
              </div>

              <div className="mt-4 space-y-2">
                <div className="flex items-center justify-between text-[11px] text-[var(--preview-muted)]">
                  <span>Current job</span>
                  <span>Layer 182 / 244</span>
                </div>
                <div className="h-2 rounded-full bg-white/8">
                  <div className="h-full w-[74%] rounded-full bg-[var(--preview-accent)]" />
                </div>
              </div>
            </div>

            <div className="grid grid-cols-[118px_minmax(0,1fr)] gap-3">
              <div className="rounded-[1.1rem] border border-white/6 bg-[var(--preview-panel)] p-3 shadow-[inset_0_1px_0_rgba(255,255,255,0.03)]">
                <div className="text-[10px] uppercase tracking-[0.16em] text-[var(--preview-muted)]">Nozzle</div>
                <div className="mt-3 flex items-center justify-center">
                  <div
                    className="grid h-20 w-20 place-items-center rounded-full"
                    style={{
                      background: `conic-gradient(var(--preview-accent) 0deg 248deg, rgba(255,255,255,0.1) 248deg 360deg)`,
                    }}
                  >
                    <div className="grid h-14 w-14 place-items-center rounded-full bg-[var(--preview-canvas)]">
                      <div className="text-center">
                        <div className="text-lg font-semibold">242°</div>
                        <div className="text-[10px] uppercase tracking-[0.16em] text-[var(--preview-muted)]">Stable</div>
                      </div>
                    </div>
                  </div>
                </div>
              </div>

              <div className="space-y-3 rounded-[1.1rem] border border-white/6 bg-[var(--preview-panel-muted)] p-3 shadow-[inset_0_1px_0_rgba(255,255,255,0.03)]">
                <div>
                  <div className="text-[10px] uppercase tracking-[0.16em] text-[var(--preview-muted)]">Queue pressure</div>
                  <div className="mt-2 flex items-end justify-between gap-2">
                    <div className="text-xl font-semibold">68%</div>
                    <div className="text-[11px] text-[var(--preview-muted)]">4 waiting jobs</div>
                  </div>
                </div>
                <div className="space-y-2">
                  {[58, 32, 76].map((value) => (
                    <div key={value} className="flex items-center gap-2">
                      <span className="w-8 text-[10px] uppercase tracking-[0.16em] text-[var(--preview-muted)]">{value}</span>
                      <div className="h-1.5 flex-1 rounded-full bg-white/8">
                        <div className="h-full rounded-full bg-[var(--preview-accent)]" style={{ width: `${value}%` }} />
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

export function ThemeSwitcher() {
  const { theme, setTheme } = useTheme();
  const [showMobilePreview, setShowMobilePreview] = useState(false);
  const activeOption = useMemo(() => THEME_OPTIONS.find((option) => option.id === theme) ?? THEME_OPTIONS[0], [theme]);

  return (
    <div className="lg:grid lg:grid-cols-[minmax(0,1fr)_300px] lg:items-start lg:gap-5">
      <div>
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4" role="radiogroup" aria-label="Color theme">
          {THEME_OPTIONS.map((option) => {
            const isActive = theme === option.id;

            return (
              <Button
                key={option.id}
                variant="unstyled"
                role="radio"
                aria-checked={isActive}
                onClick={() => setTheme(option.id)}
                className={clsx(
                  'group flex flex-col gap-2 rounded-2xl border p-3 text-left transition-all duration-200',
                  'focus:outline-none focus-visible:ring-2 focus-visible:ring-[var(--pf-focus-ring)] focus-visible:ring-offset-2 focus-visible:ring-offset-[var(--pf-focus-ring-offset)]',
                  isActive
                    ? 'border-[var(--pf-accent)] bg-[var(--pf-accent-bg)] text-[var(--pf-on-accent)] shadow-[var(--pf-glow-accent)]'
                    : 'border-[var(--pf-border)] bg-[var(--pf-card-bg)] hover:border-[var(--pf-border-strong)] hover:bg-[var(--pf-hover-overlay)]',
                )}
                title={option.description}
              >
                <div className="flex gap-1" aria-hidden="true">
                  {option.swatches.map((color) => (
                    <span
                      key={`${option.id}-${color}`}
                      className="h-5 w-5 rounded-sm ring-1 ring-black/10"
                      style={{ backgroundColor: color }}
                    />
                  ))}
                </div>

                <span className={clsx('text-sm font-semibold', isActive ? 'text-[var(--pf-on-accent)]' : 'text-[var(--pf-text-primary)]')}>
                  {option.label}
                </span>

                <span className={clsx('text-xs', isActive ? 'text-[var(--pf-on-accent)]/80' : 'text-pf-text-secondary')}>
                  {option.description}
                </span>

                {isActive ? <span className="mt-0.5 text-xs text-[var(--pf-on-accent)]/80">✓ Active</span> : null}
              </Button>
            );
          })}
        </div>

        <div className="mt-4 lg:hidden">
          <Button
            type="button"
            variant="secondary"
            onClick={() => setShowMobilePreview((current) => !current)}
            iconRight={showMobilePreview ? <ChevronUpIcon className="h-4 w-4" /> : <ChevronDownIcon className="h-4 w-4" />}
            aria-expanded={showMobilePreview}
            aria-controls="theme-preview-mobile"
            className="w-full justify-between"
          >
            {showMobilePreview ? 'Hide live preview' : 'Show live preview'}
          </Button>

          {showMobilePreview ? (
            <div id="theme-preview-mobile" className="mt-4">
              <ThemePreview option={activeOption} />
            </div>
          ) : null}
        </div>
      </div>

      <div className="hidden lg:block lg:sticky lg:top-6">
        <ThemePreview option={activeOption} />
      </div>
    </div>
  );
}
