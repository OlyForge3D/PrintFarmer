import type React from 'react';

export type HomeButtonStyle = { className?: string; style?: React.CSSProperties };

export function getHomeButtonStyle(homingStateKnown: boolean, isHomed: boolean): HomeButtonStyle {
  if (!homingStateKnown) {
    return {};
  }

  const tokenPrefix = isHomed ? 'homed' : 'not-homed';

  return {
    className:
      '!text-white !bg-none !bg-[var(--pf-home-bg)] !border !border-pf-border-light enabled:hover:!border-pf-border enabled:active:!border-pf-border enabled:hover:!bg-none enabled:hover:!bg-[var(--pf-home-bg-hover)] enabled:active:!bg-none enabled:active:!bg-[var(--pf-home-bg-active)]',
    style: {
      ['--pf-home-bg' as unknown as keyof React.CSSProperties]: `var(--pf-home-${tokenPrefix}-bg)`,
      ['--pf-home-bg-hover' as unknown as keyof React.CSSProperties]: 'var(--pf-button-primary-bg)',
      ['--pf-home-bg-active' as unknown as keyof React.CSSProperties]: `var(--pf-home-${tokenPrefix}-bg)`,
    },
  };
}
