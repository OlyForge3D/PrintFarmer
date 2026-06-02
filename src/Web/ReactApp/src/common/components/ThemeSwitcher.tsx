import { Button } from '@/common/components/ui/Button';
import { useTheme, type NewTheme } from '@/common/hooks/useTheme';

interface ThemeOption {
  id: NewTheme;
  label: string;
  description: string;
  swatches: string[];
}

const THEME_OPTIONS: ThemeOption[] = [
  {
    id: 'dark',
    label: 'Dark',
    description: 'Mission Control — cool slate-navy with precision-teal',
    swatches: ['#08101f', '#0e1729', '#14b8a6', '#e8eef8'],
  },
  {
    id: 'light',
    label: 'Light',
    description: 'Workshop Daylight — clean, high-contrast',
    swatches: ['#f5f7fa', '#ffffff', '#0d7d75', '#0b1320'],
  },
  {
    id: 'matrix',
    label: 'Matrix',
    description: 'Terminal — phosphor-green CRT aesthetic',
    swatches: ['#000000', '#050a05', '#00ff41', '#4ade80'],
  },
  {
    id: 'blueprint',
    label: 'Blueprint',
    description: 'Schematic — cyanotype drafting aesthetic',
    swatches: ['#0c1a2e', '#102137', '#38bdf8', '#e6f2ff'],
  },
  {
    id: 'ratos',
    label: 'RatOS',
    description: 'Firmware — black and green terminal aesthetic',
    swatches: ['#080a08', '#0c110d', '#22c55e', '#86efac'],
  },
  {
    id: 'voron',
    label: 'Voron',
    description: 'Industrial — red-on-black precision engineering',
    swatches: ['#090909', '#101012', '#dc2626', '#a1a1aa'],
  },
  {
    id: 'farm',
    label: 'Farm',
    description: 'Harvest — warm autumn festival colors',
    swatches: ['#24150c', '#332113', '#ea580c', '#84cc16'],
  },
];

export function ThemeSwitcher() {
  const { theme, setTheme } = useTheme();

  return (
    <div className="grid grid-cols-2 gap-3 sm:grid-cols-4" role="radiogroup" aria-label="Color theme">
      {THEME_OPTIONS.map(option => {
        const isActive = theme === option.id;
        return (
          <Button
            key={option.id}
            variant="unstyled"
            role="radio"
            aria-checked={isActive}
            onClick={() => setTheme(option.id)}
            className={[
              'group flex flex-col gap-2 rounded-lg border p-3 text-left transition-all duration-200',
              'focus:outline-none focus-visible:ring-2 focus-visible:ring-[var(--pf-focus-ring)] focus-visible:ring-offset-2 focus-visible:ring-offset-[var(--pf-focus-ring-offset)]',
              isActive
                ? 'border-[var(--pf-accent)] bg-[var(--pf-accent-bg)] text-[var(--pf-on-accent)] shadow-[var(--pf-glow-accent)]'
                : 'border-[var(--pf-border)] bg-[var(--pf-card-bg)] hover:border-[var(--pf-border-strong)] hover:bg-[var(--pf-hover-overlay)]',
            ].join(' ')}
            title={option.description}
          >
            {/* Color swatches preview */}
            <div className="flex gap-1" aria-hidden="true">
              {option.swatches.map((color, i) => (
                <span
                  key={i}
                  className="h-5 w-5 rounded-sm ring-1 ring-black/10"
                  style={{ backgroundColor: color }}
                />
              ))}
            </div>

            {/* Theme label */}
            <span
              className={[
                'text-sm font-semibold',
                isActive ? 'text-[var(--pf-on-accent)]' : 'text-[var(--pf-text-primary)]',
              ].join(' ')}
            >
              {option.label}
            </span>

            {/* Active indicator */}
            {isActive && (
              <span className="text-xs text-[var(--pf-on-accent)]/80">Active</span>
            )}
          </Button>
        );
      })}
    </div>
  );
}
