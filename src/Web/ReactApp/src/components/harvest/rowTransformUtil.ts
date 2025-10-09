// Utility to generate and inject dynamic row transform classes for VirtualizedPrinterGrid
// Ensures zero inline styles for strict linter compliance

const styleSheetId = 'virtualized-printer-grid-dynamic-rows';
let styleSheet: HTMLStyleElement | null = null;
const injected: Record<number, string> = {};

export function getRowTransformClass(rowStart: number): string {
  if (injected[rowStart]) return injected[rowStart];
  if (!styleSheet) {
    styleSheet = document.createElement('style');
    styleSheet.id = styleSheetId;
    document.head.appendChild(styleSheet);
  }
  const className = `rowTranslateY${rowStart}`;
  const rule = `.${className} { transform: translateY(${rowStart}px) !important; }`;
  styleSheet.sheet?.insertRule(rule, styleSheet.sheet.cssRules.length);
  injected[rowStart] = className;
  return className;
}
